using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    /// <summary>装备：钓鱼挑最好的鱼竿并装上鱼饵/钓具，背包里有更好的鞋子、戒指就换上。</summary>
    internal partial class GameActions
    {
        /// <summary>
        /// 鱼竿好坏：铱金(3/4) &gt; 玻璃纤维(2) &gt; 竹竿(0) &gt; 训练用鱼竿。
        /// 训练用鱼竿的工具等级是 1，但游戏规定它钓不到难度 50 以上的鱼（GameLocation 里的 TrainingRod 判断），所以排最后。
        /// </summary>
        private static int RodScore(FishingRod rod) =>
            rod.QualifiedItemId == "(T)TrainingRod" ? -1 : rod.UpgradeLevel;

        private static int BestRodIndex()
        {
            int best = -1, bestScore = int.MinValue;
            for (int i = 0; i < Player.Items.Count; i++)
            {
                if (Player.Items[i] is FishingRod rod && RodScore(rod) > bestScore)
                {
                    best = i;
                    bestScore = RodScore(rod);
                }
            }
            return best;
        }

        /// <summary>
        /// 鱼竿能装鱼饵（玻璃纤维以上）/钓具（铱金以上）但空着时，从背包里拿来装上。返回装了什么，没装返回 null。
        /// 鱼饵类别 -21，钓具类别 -22，能不能装由鱼竿自己判断（canThisBeAttached）。
        /// </summary>
        private static string FillRodAttachments(FishingRod rod)
        {
            var added = new List<string>();
            void Fill(int category)
            {
                for (int i = 0; i < Player.Items.Count; i++)
                {
                    if (Player.Items[i] is SObject o && o.Category == category && rod.canThisBeAttached(o))
                    {
                        Player.Items[i] = null;
                        var old = rod.attach(o);
                        if (old != null)
                            Player.addItemToInventory(old);
                        added.Add(o.DisplayName + (o.Stack > 1 ? $"x{o.Stack}" : ""));
                        return;
                    }
                }
            }
            if (rod.CanUseBait() && rod.GetBait() == null)
                Fill(SObject.baitCategory);
            if (rod.CanUseTackle() && rod.GetTackle().All(t => t == null))
                Fill(SObject.tackleCategory);
            return added.Count > 0 ? $"给 {rod.DisplayName} 装上了 {string.Join("、", added)}" : null;
        }

        private static int BootsScore(Boots b) => b == null ? -1 : b.defenseBonus.Value + b.immunityBonus.Value;

        /// <summary>戒指好坏没有统一的数值，用游戏里的售价当参考（越稀有越贵）。</summary>
        private static int RingScore(Ring r) => r == null ? -1 : r.salePrice();

        /// <summary>
        /// 背包里有更好的鞋子（防御+免疫更高）或戒指（空着的手指先戴上，否则换掉售价最低的那只）就换上，
        /// 换下来的放回背包原来那一格。用游戏的 Farmer.Equip，戒指的光照、加成等效果和手动换装一样生效。
        /// 返回换了什么，没换返回 null。
        /// </summary>
        public static string AutoEquip()
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.eventUp || Game1.activeClickableMenu != null)
                return null;
            var changes = new List<string>(pendingGearNotes);
            pendingGearNotes.Clear();

            // 帽子没有数值，空着就把背包里的戴上
            if (Player.hat.Value == null)
            {
                for (int i = 0; i < Player.Items.Count; i++)
                {
                    if (Player.Items[i] is Hat h)
                    {
                        Player.Items[i] = null;
                        Player.Items[i] = Player.Equip(h, Player.hat);
                        changes.Add($"戴上了 {h.DisplayName}");
                        break;
                    }
                }
            }

            for (int i = 0; i < Player.Items.Count; i++)
            {
                if (Player.Items[i] is Boots b && BootsScore(b) > BootsScore(Player.boots.Value))
                {
                    Player.Items[i] = null;
                    var old = Player.Equip(b, Player.boots);
                    Player.Items[i] = old;
                    changes.Add($"穿上了 {b.DisplayName}（防御 +{b.defenseBonus.Value}，免疫 +{b.immunityBonus.Value}）");
                }
            }

            for (int i = 0; i < Player.Items.Count; i++)
            {
                if (Player.Items[i] is not Ring r)
                    continue;
                // 结婚戒指要留着求婚，戴上也没有效果
                if (r.QualifiedItemId == "(O)801")
                    continue;
                var slot = Player.leftRing.Value == null ? Player.leftRing
                         : Player.rightRing.Value == null ? Player.rightRing
                         : RingScore(Player.leftRing.Value) <= RingScore(Player.rightRing.Value) ? Player.leftRing : Player.rightRing;
                if (slot.Value != null && RingScore(r) <= RingScore(slot.Value))
                    continue;
                Player.Items[i] = null;
                var old = Player.Equip(r, slot);
                Player.Items[i] = old;
                changes.Add($"戴上了 {r.DisplayName}" + (old != null ? $"（换下 {old.DisplayName}）" : ""));
            }

            return changes.Count > 0 ? string.Join("；", changes) : null;
        }

        /// <summary>背包满时直接穿上的装备，等下一次 AutoEquip 一起报给 AI。</summary>
        private static readonly List<string> pendingGearNotes = new();

        /// <summary>装备、武器、工具：独一份，丢了很难再拿到。</summary>
        private static bool IsGear(Item i) => i is Boots or Ring or Hat or Clothing or Tool;

        /// <summary>一格东西大概值多少（整叠）。装备算最贵。</summary>
        private static int StackWorth(Item i) => IsGear(i) ? int.MaxValue / 2 : System.Math.Max(0, i.salePrice()) * System.Math.Max(1, i.Stack);

        /// <summary>
        /// 把东西放进背包。背包满了放不下时不直接丢掉它：
        /// ① 是比身上更好的鞋子、能戴的戒指、没戴帽子时的帽子，直接穿上；
        /// ② 是装备，或者比背包里最不值钱的那一格贵得多，就把最不值钱的那一格丢到脚边，换它进来。
        /// 返回最后掉在地上的东西（可能是换下来的旧鞋、被挤出去的杂物，或者它自己），都放下了返回空列表。
        /// </summary>
        internal static List<Item> AddOrMakeRoom(Item item)
        {
            var onGround = new List<Item>();
            var left = Player.addItemToInventory(item);
            if (left == null)
                return onGround;

            void Drop(Item it)
            {
                onGround.Add(it);
                Game1.createItemDebris(it, Player.getStandingPosition(), Player.FacingDirection);
            }

            // ① 直接穿上
            Item displaced = null;
            bool worn = false;
            if (left is Boots b && BootsScore(b) > BootsScore(Player.boots.Value))
            {
                displaced = Player.Equip(b, Player.boots);
                pendingGearNotes.Add($"背包满了，直接穿上了 {b.DisplayName}（防御 +{b.defenseBonus.Value}，免疫 +{b.immunityBonus.Value}）");
                worn = true;
            }
            else if (left is Ring r && r.QualifiedItemId != "(O)801" && (Player.leftRing.Value == null || Player.rightRing.Value == null))
            {
                Player.Equip(r, Player.leftRing.Value == null ? Player.leftRing : Player.rightRing);
                pendingGearNotes.Add($"背包满了，直接戴上了 {r.DisplayName}");
                worn = true;
            }
            else if (left is Hat h && Player.hat.Value == null)
            {
                Player.Equip(h, Player.hat);
                pendingGearNotes.Add($"背包满了，直接戴上了 {h.DisplayName}");
                worn = true;
            }
            if (worn)
            {
                if (displaced != null && Player.addItemToInventory(displaced) is Item old)
                    Drop(old); // 换下来的旧鞋比身上的差，掉了也不可惜
                return onGround;
            }

            // ② 把最不值钱的一格换出去
            var reserved = ReservedChecker();
            int cheapest = -1, cheapestWorth = int.MaxValue;
            for (int i = 0; i < Player.Items.Count; i++)
            {
                var it = Player.Items[i];
                if (it == null || IsGear(it) || reserved(it) != null || (it is SObject so && so.questItem.Value))
                    continue;
                int w = StackWorth(it);
                if (w < cheapestWorth)
                {
                    cheapest = i;
                    cheapestWorth = w;
                }
            }
            int worth = StackWorth(left);
            bool worthIt = cheapest >= 0 && (IsGear(left) || (worth >= 250 && worth >= cheapestWorth * 3L));
            if (!worthIt)
            {
                Drop(left);
                return onGround;
            }
            var junk = Player.Items[cheapest];
            Player.Items[cheapest] = null;
            var still = Player.addItemToInventory(left);
            Drop(junk);
            if (still != null)
                Drop(still);
            return onGround;
        }

        /// <summary>身上穿戴了什么；戒指位空着、箱子里有能穿的更好装备时给出提示。</summary>
        public static string GearOverview()
        {
            var lines = new List<string>();
            var boots = Player.boots.Value;
            lines.Add("鞋子：" + (boots != null ? $"{boots.DisplayName}（防御 +{boots.defenseBonus.Value}，免疫 +{boots.immunityBonus.Value}）" : "没穿"));
            lines.Add($"戒指：{Player.leftRing.Value?.DisplayName ?? "空"}、{Player.rightRing.Value?.DisplayName ?? "空"}");
            lines.Add("帽子：" + (Player.hat.Value?.DisplayName ?? "没戴"));

            // 箱子里放着能穿的：拿到背包里就会自动穿上
            bool ringFree = Player.leftRing.Value == null || Player.rightRing.Value == null;
            var inChests = new List<string>();
            foreach (var (loc, tile, chest) in AllChests())
            {
                foreach (var it in chest.GetItemsForPlayer())
                {
                    bool better = it is Boots b && BootsScore(b) > BootsScore(boots)
                        || it is Ring r && r.QualifiedItemId != "(O)801" && ringFree
                        || it is Hat && Player.hat.Value == null;
                    if (better)
                        inChests.Add($"{it.DisplayName} 在 {loc.NameOrUniqueName} ({tile.X},{tile.Y})");
                }
            }
            if (inChests.Count > 0)
                lines.Add("箱子里有能穿的：" + string.Join("、", inChests.Take(4)) + "。用 take_items 拿到背包里就会自动穿上");

            if (ringFree)
            {
                // 会做的戒指配方（坚固戒指、勇士戒指……）
                var known = Player.craftingRecipes.Keys.Where(k => k.EndsWith("Ring") || k.EndsWith("Band"))
                    .Select(k => { try { return new CraftingRecipe(k).DisplayName; } catch { return k; } }).ToList();
                lines.Add("有戒指位空着：" + (known.Count > 0 ? $"你会做 {string.Join("、", known)}（用 craft，材料不够它会说缺什么）；" : "")
                    + "冒险者公会 AdventureGuild 也卖戒指（open_shop 看价钱）。戒指能加防御、攻击或回血，值得弄一个");
            }
            return string.Join("\n", lines);
        }
    }
}
