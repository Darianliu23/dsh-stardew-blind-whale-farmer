using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Enchantments;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    /// <summary>烹饪、蟹笼、战斗、下矿洞。</summary>
    internal sealed partial class GameActions
    {
        // ───────────────────────── 烹饪 ─────────────────────────

        private static string IngredientLabel(string id, int count)
        {
            string name = int.TryParse(id, out int n) && n < 0 ? "任意" + SObject.GetCategoryDisplayName(n) : Util.ObjectName(id);
            return $"{name}x{count}";
        }

        /// <summary>厨房里能用的冰箱（农舍冰箱 + 迷你冰箱）。</summary>
        private static List<IInventory> KitchenContainers(GameLocation loc)
        {
            var list = new List<IInventory>();
            var fridge = loc.GetFridge();
            if (fridge != null)
                list.Add(fridge.Items);
            foreach (var obj in loc.objects.Values)
                if (obj is Chest c && c.fridge.Value && c.bigCraftable.Value)
                    list.Add(c.Items);
            return list;
        }

        public IEnumerable<object> Cook(string recipeSpec, int count)
        {
            var known = Player.cookingRecipes.Keys.ToList();
            if (known.Count == 0)
            {
                yield return ActionResult.Fail("你还没学会任何菜谱（看电视“烹饪节目”(周日)、村民来信、升级好感都能学到）");
                yield break;
            }

            // 先回到有厨房的地方（农舍升级一次后才有厨房）
            var o = new Outcome();
            var kitchen = FindTileAction(Loc, "kitchen");
            if (kitchen == null)
            {
                if (Utility.getHomeOfFarmer(Player) is FarmHouse home && home.upgradeLevel < 1)
                {
                    yield return ActionResult.Fail("农舍还没有厨房：需要找木匠 Robin（ScienceHouse）升级房子（10000g + 450 木材）");
                    yield break;
                }
                foreach (var s in TravelInner("FarmHouse", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("回家做饭失败：" + o.Message);
                    yield break;
                }
                kitchen = FindTileAction(Loc, "kitchen");
                if (kitchen == null)
                {
                    yield return ActionResult.Fail("家里找不到厨房");
                    yield break;
                }
            }
            foreach (var s in GoAdjacent(kitchen.Value, o))
                yield return s;

            var containers = KitchenContainers(Loc);
            var extra = containers.SelectMany(c => c).Where(i => i != null).ToList();

            string key = known.FirstOrDefault(k => k.Equals(recipeSpec, StringComparison.OrdinalIgnoreCase))
                ?? known.FirstOrDefault(k => new CraftingRecipe(k, true).DisplayName.Equals(recipeSpec, StringComparison.OrdinalIgnoreCase))
                ?? known.FirstOrDefault(k => k.Contains(recipeSpec ?? "", StringComparison.OrdinalIgnoreCase) || new CraftingRecipe(k, true).DisplayName.Contains(recipeSpec ?? "", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(recipeSpec) || key == null)
            {
                var lines = known.Select(k =>
                {
                    var r = new CraftingRecipe(k, true);
                    string mark = r.doesFarmerHaveIngredientsInInventory(extra) ? "【现在能做】" : "";
                    return $"{r.DisplayName}({k}){mark}：{string.Join("、", r.recipeList.Select(kv => IngredientLabel(kv.Key, kv.Value)))}";
                });
                string head = string.IsNullOrWhiteSpace(recipeSpec) ? "已学会的菜谱" : $"没学会“{recipeSpec}”。已学会的菜谱";
                yield return ActionResult.Fail(head + "（背包和冰箱里的材料都能用）：\n" + string.Join("\n", lines));
                yield break;
            }

            var recipe = new CraftingRecipe(key, true);
            int made = 0;
            Item last = null;
            for (int i = 0; i < Math.Clamp(count, 1, 20); i++)
            {
                extra = containers.SelectMany(c => c).Where(it => it != null).ToList();
                if (!recipe.doesFarmerHaveIngredientsInInventory(extra))
                    break;
                var dish = recipe.createItem();
                if (!Player.couldInventoryAcceptThisItem(dish))
                    break;
                // 与烹饪界面 CraftingPage 点击菜谱时的逻辑一致
                recipe.consumeIngredients(containers);
                Player.addItemToInventoryBool(dish);
                Player.NotifyQuests(q => q.OnRecipeCrafted(recipe, dish));
                Player.cookedRecipe(dish.ItemId);
                Game1.stats.checkForCookingAchievements();
                Game1.playSound("coin");
                last = dish;
                made++;
                foreach (var s in WaitTicks(10))
                    yield return s;
            }
            if (made > 0)
                yield return ActionResult.Success($"做了 {made} 份 {recipe.DisplayName}（{Util.ItemLabel(last)}）");
            else
                yield return ActionResult.Fail($"材料不够或背包满了。{recipe.DisplayName} 需要：{string.Join("、", recipe.recipeList.Select(kv => IngredientLabel(kv.Key, kv.Value)))}");
        }

        // ───────────────────────── 蟹笼 ─────────────────────────

        /// <summary>收当前地点所有蟹笼的收获，没鱼饵的补上鱼饵。</summary>
        public IEnumerable<object> CheckCrabPots(string location)
        {
            var o = new Outcome();
            if (!string.IsNullOrWhiteSpace(location) && !Loc.NameOrUniqueName.Equals(location, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in TravelInner(location, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }
            var loc = Loc;
            var pots = loc.objects.Pairs.Where(p => p.Value is CrabPot).Select(p => new Point((int)p.Key.X, (int)p.Key.Y)).ToList();
            if (pots.Count == 0 && loc is StardewValley.Locations.BeachNightMarket)
            {
                yield return ActionResult.Fail("夜市（冬季 15-17 日）期间海滩换成了夜市的地图，海滩上的蟹笼这几天碰不到（游戏本身就是这样，手动玩也收不了）。蟹笼里的东西不会丢，夜市结束后再来收");
                yield break;
            }
            if (pots.Count == 0)
            {
                yield return ActionResult.Fail("这里没有蟹笼（在鱼店买或用配方制作，用 place_item 放到水边的水面上）");
                yield break;
            }

            int collected = 0, baited = 0;
            var got = new List<string>();
            string note = null;
            foreach (var tile in Util.NearestOrder(Player.TilePoint, pots))
            {
                var v = new Vector2(tile.X, tile.Y);
                if (!loc.objects.TryGetValue(v, out var obj) || obj is not CrabPot pot)
                    continue;
                bool ready = pot.readyForHarvest.Value && pot.heldObject.Value != null;
                bool needBait = pot.NeedsBait(Player) && pot.bait.Value == null;
                int baitIdx = Player.Items.ToList().FindIndex(i => i is SObject b && b.Category == SObject.baitCategory);
                if (!ready && !(needBait && baitIdx >= 0))
                    continue;

                foreach (var s in GoAdjacent(tile, o))
                    yield return s;
                if (!o.Ok)
                    continue;

                if (ready)
                {
                    if (Player.freeSpotsInInventory() == 0)
                    {
                        note = "背包满了，有蟹笼没收";
                        break;
                    }
                    string name = pot.heldObject.Value.DisplayName;
                    // 先换成工具，避免手里拿着鱼饵时触发放鱼饵而不是收获
                    int safe = Util.FindTool<Tool>();
                    if (safe >= 0)
                        Player.CurrentToolIndex = safe;
                    loc.checkAction(new xLocation(tile.X, tile.Y), Game1.viewport, Player);
                    foreach (var s in WaitTicks(10))
                        yield return s;
                    if (pot.heldObject.Value == null)
                    {
                        collected++;
                        got.Add(name);
                    }
                }

                if (pot.NeedsBait(Player) && pot.bait.Value == null)
                {
                    baitIdx = Player.Items.ToList().FindIndex(i => i is SObject b && b.Category == SObject.baitCategory);
                    if (baitIdx < 0)
                    {
                        note = "鱼饵用完了（鱼店有卖，或者用配方制作）";
                        continue;
                    }
                    var bait = Player.Items[baitIdx];
                    Player.CurrentToolIndex = baitIdx;
                    // 与手拿鱼饵点蟹笼的逻辑一致：放入后由调用方扣掉一个
                    if (pot.performObjectDropInAction(bait, false, Player))
                    {
                        Player.Items[baitIdx] = bait.ConsumeStack(1);
                        baited++;
                    }
                    foreach (var s in WaitTicks(6))
                        yield return s;
                }
            }

            int empty = loc.objects.Values.OfType<CrabPot>().Count(p => p.NeedsBait(Player) && p.bait.Value == null);
            string msg = $"收了 {collected} 个蟹笼{(got.Count > 0 ? "：" + string.Join("、", got) : "")}，放了 {baited} 份鱼饵；这里共 {pots.Count} 个蟹笼，{empty} 个还没鱼饵";
            if (note != null)
                msg += "（" + note + "）";
            yield return collected > 0 || baited > 0 ? ActionResult.Success(msg) : ActionResult.Fail(msg);
        }

        // ───────────────────────── 战斗 ─────────────────────────

        private sealed class FightStats
        {
            public int Kills, Swings, Eaten;
            /// <summary>打倒木乃伊的次数（它倒地 10 秒后会满血爬起来，不算真的打死）。</summary>
            public int MummyDowns;
            /// <summary>用炸弹炸死的倒地木乃伊、用掉的炸弹。</summary>
            public int MummiesBombed, BombsUsed;
            /// <summary>这次打架里放炸弹没放成（来不及跑开之类），别再反复试。</summary>
            public bool BombGaveUp;
            /// <summary>要在结果里告诉 AI 的事（比如迫不得已吃了要交的东西）。</summary>
            public readonly List<string> Notes = new();
            /// <summary>找不到路走过去、等了也没过来的怪。</summary>
            public readonly HashSet<Monster> Unreachable = new();
            /// <summary>打了不掉血、这次不再打的怪。</summary>
            public readonly HashSet<Monster> Immune = new();
            /// <summary>每只怪连着砍中却没掉血的次数（探索时会反复调 FightInner，计数要跨调用累计）。</summary>
            public readonly Dictionary<Monster, int> NoEffect = new();
        }

        private static IEnumerable<Monster> LiveMonsters(GameLocation loc) =>
            loc.characters.OfType<Monster>().Where(m => m.Health > 0 && !m.IsInvisible && !IsDownedMummy(m) && !NeverHurtable(m));

        /// <summary>怎么打都不掉血的怪（takeDamage 恒返回 -1）：火山传送带房间里的尖刺机关、剧情里的蝙蝠。不当怪打，当障碍绕开。</summary>
        internal static bool NeverHurtable(Monster m) => m is Spiker || m is Bat { Age: 789 };

        /// <summary>暂时打不到的怪：潜在岩浆里的岩浆潜伏者（浮上来才打得到）。</summary>
        private static bool HiddenNow(Monster m) => m is LavaLurk lurk && lurk.currentState.Value == LavaLurk.State.Submerged;

        /// <summary>
        /// 倒在地上的木乃伊（Mummy.takeDamage：血打到 0 时不死，血回满、倒地 10 秒再爬起来）。倒地期间不动、碰到不扣血，
        /// 武器打不动（只有爆炸能一下炸死）。所以不算活怪：不去空砍它、不当威胁、不算“附近还剩几只”。
        /// </summary>
        private static bool IsDownedMummy(Monster m) => m is Mummy mummy && mummy.reviveTimer.Value > 0;

        /// <summary>附近倒地的木乃伊（结果里提醒“它会爬起来”）。</summary>
        private static int DownedMummiesNear(int radius) => Loc == null ? 0
            : Loc.characters.OfType<Monster>().Count(m => IsDownedMummy(m) && Util.Manhattan(m.TilePoint, Player.TilePoint) <= radius);

        /// <summary>木乃伊的说明：打倒了几次、附近还躺着几只，以及怎样才能彻底消灭。没打倒过也没躺着的返回 null。</summary>
        private static string MummyNote(FightStats st, int radius)
        {
            int downed = DownedMummiesNear(radius);
            if (st.MummyDowns == 0 && downed == 0 && st.MummiesBombed == 0)
                return null;
            bool crusader = BestWeapon() is int w && w >= 0 && Player.Items[w] is MeleeWeapon mw && mw.hasEnchantmentOfType<CrusaderEnchantment>();
            string bombed = st.MummiesBombed > 0 ? $"用了 {st.BombsUsed} 颗炸弹炸死 {st.MummiesBombed} 只木乃伊" : null;
            if (downed == 0)
                return bombed;
            return (bombed != null ? bombed + "；" : "") + (st.MummyDowns > 0 ? $"木乃伊打倒了 {st.MummyDowns} 次" : "")
                + (downed > 0 ? $"{(st.MummyDowns > 0 ? "，" : "")}附近还有 {downed} 只倒在地上" : "")
                + "：木乃伊倒地 10 秒后会满血爬起来，武器砍倒地的木乃伊没用。"
                + (crusader ? "你的武器有十字军附魔，再打倒就会直接死。" : "只有趁它倒地时用炸弹（樱桃炸弹、炸弹、超级炸弹）炸，或者用带十字军附魔的武器，才能彻底消灭。")
                + "没炸弹就别跟它耗，趁它倒着赶紧找楼梯或跳竖井";
        }

        /// <summary>radius 格内最近的活怪（会主动打人的）；没有返回 null。</summary>
        private static Monster NearestThreat(int radius)
        {
            if (Loc == null)
                return null;
            var me = Player.TilePoint;
            return LiveMonsters(Loc)
                .Where(m => m.DamageToFarmer > 0 && !CannotHurt(m) && !(ShellClosed(m) && KnownUnreachable.Contains(m)) && Math.Max(Math.Abs(m.TilePoint.X - me.X), Math.Abs(m.TilePoint.Y - me.Y)) <= radius)
                .OrderBy(m => Util.Manhattan(m.TilePoint, me))
                .FirstOrDefault();
        }

        /// <summary>上一次 fight 走不过去的怪：只要它还没贴到身边，提醒里就不再催 AI 去打。</summary>
        private static HashSet<Monster> KnownUnreachable = new();

        /// <summary>身边有会打人的怪时的提醒：状态里和每个动作结果后面都会带上，让 AI 先清怪再干活。</summary>
        public static string ThreatWarning()
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Game1.eventUp)
                return null;
            var near = LiveMonsters(Loc).Where(m => m.DamageToFarmer > 0 && !CannotHurt(m)).ToList();
            var me = Player.TilePoint;
            var close = near.Where(m => Util.Manhattan(m.TilePoint, me) <= 6)
                // 打不着的怪贴到身边会打人，照样提醒；缩壳的岩石蟹不动也不打人，fight 敲不动（没体力）以后就别再催
                .Where(m => !KnownUnreachable.Contains(m) || (Util.Manhattan(m.TilePoint, me) <= 1 && !ShellClosed(m)))
                .OrderBy(m => Util.Manhattan(m.TilePoint, me)).ToList();
            if (close.Count == 0)
                return null;
            string list = string.Join("、", close.Take(4).Select(m => $"{MonsterName(m)}({m.TilePoint.X},{m.TilePoint.Y})"));
            return $"⚠ 附近有 {close.Count} 只怪：{list}，生命 {Player.health}/{Player.maxHealth}。先用 fight 清掉再干别的；生命低又没有回血食物就撤退";
        }

        /// <summary>被打时报给 AI：最近的会打人的怪叫什么、在哪、哪个方向（远处放法术的也算，不限 6 格）。没有返回 null。</summary>
        public static string NearestAttacker()
        {
            if (!StardewModdingAPI.Context.IsWorldReady)
                return null;
            var me = Player.TilePoint;
            var m = LiveMonsters(Loc).Where(x => x.DamageToFarmer > 0 && !CannotHurt(x)).OrderBy(x => Util.Manhattan(x.TilePoint, me)).FirstOrDefault();
            if (m == null)
                return null;
            var p = m.TilePoint;
            string dir = (p.Y < me.Y ? "北" : p.Y > me.Y ? "南" : "") + (p.X < me.X ? "西" : p.X > me.X ? "东" : "");
            return $"{MonsterName(m)} ({p.X},{p.Y})，在你{(dir == "" ? "脚下" : dir + "边")} {Util.Manhattan(p, me)} 格";
        }

        /// <summary>怪物的中文名。联机时农场工人这边的怪是主机同步过来的，displayName 没翻译，只剩英文内部名，所以按名字查数据。</summary>
        internal static string MonsterName(Monster m) => Monster.GetDisplayName(m.Name);

        /// <summary>会给怪加血的怪：暗影萨满每次施法约四成概率给附近血量比例最低的怪（常常是它自己）加 60 血，血少于 30 会逃跑。</summary>
        private static bool IsHealer(Monster m) => m is ShadowShaman;

        /// <summary>
        /// 岩石蟹（长得像石头的怪）缩在壳里、停着不动：这时武器只有 0 伤害（RockCrab.takeDamage），要用镐子敲（敲一下就醒，
        /// 走动时武器打得动；敲 5 下壳就碎）。竹节虫（isStickBug）不吃镐子，不算。
        /// </summary>
        private static bool ShellClosed(Monster m) =>
            m is RockCrab crab && !crab.isStickBug.Value && !crab.shellGone.Value && crab.Sprite.currentFrame % 4 == 0;

        /// <summary>现在手上的东西打不动的怪：没有镐子时缩壳的岩石蟹；最好的武器没有除虫附魔时的装甲虫。</summary>
        private static bool CannotHurt(Monster m)
        {
            if (ShellClosed(m))
                return Util.FindTool<Pickaxe>() < 0;
            if (m is Bug bug && bug.isArmoredBug.Value)
            {
                int best = BestWeapon();
                return best < 0 || Player.Items[best] is not MeleeWeapon w || !w.hasEnchantmentOfType<BugKillerEnchantment>();
            }
            return false;
        }

        /// <summary>伤害最高的近战武器，没有就返回 -1。镰刀不算：拿它打矿洞深处的怪打不动，只会一层都下不去地空转。</summary>
        private static int BestWeapon()
        {
            int best = -1, bestDmg = -1;
            for (int i = 0; i < Player.Items.Count; i++)
            {
                if (Player.Items[i] is MeleeWeapon w && !w.isScythe())
                {
                    int dmg = w.minDamage.Value + w.maxDamage.Value;
                    if (dmg > bestDmg)
                    {
                        best = i;
                        bestDmg = dmg;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 打架时该吃哪样：任务、特别订单、收集包要交的不吃；够回满大半血的里面挑最便宜的（别把贵的鱼、料理浪费在掉一点血上），
        /// 都不够就吃回血最多的。只剩要交的东西时，血量危急（≤20%）才吃，reserved 返回原因。
        /// </summary>
        private static int BestHealingFood(out string reserved)
        {
            reserved = null;
            var isReserved = ReservedChecker();
            var foods = Player.Items.Select((it, i) => (it, i))
                .Where(x => x.it is SObject o && o.Edibility > 0 && o.healthRecoveredOnConsumption() > 0)
                .Select(x => (x.i, o: (SObject)x.it, why: isReserved(x.it))).ToList();
            var free = foods.Where(f => f.why == null).ToList();
            if (free.Count > 0)
            {
                int missing = Player.maxHealth - Player.health;
                var enough = free.Where(f => f.o.healthRecoveredOnConsumption() >= missing * 0.6f).ToList();
                return enough.Count > 0
                    ? enough.OrderBy(f => f.o.sellToStorePrice()).ThenBy(f => f.o.healthRecoveredOnConsumption()).First().i
                    : free.OrderByDescending(f => f.o.healthRecoveredOnConsumption()).First().i;
            }
            if (foods.Count > 0 && Player.health <= Player.maxHealth * 0.2f)
            {
                var last = foods.OrderByDescending(f => f.o.healthRecoveredOnConsumption()).First();
                reserved = $"{last.o.DisplayName}（{last.why}）";
                return last.i;
            }
            if (foods.Count > 0)
                reserved = string.Join("、", foods.Select(f => $"{f.o.DisplayName}（{f.why}）").Distinct());
            return -1;
        }

        private static int DirToward(Point from, Point to)
        {
            int dx = to.X - from.X, dy = to.Y - from.Y;
            if (Math.Abs(dx) > Math.Abs(dy))
                return dx > 0 ? 1 : 3;
            return dy < 0 ? 0 : 2;
        }

        /// <summary>
        /// 站在原地朝哪个方向挥能砍到这只怪；砍不到返回 -1。
        /// 用游戏自己的 MeleeWeapon.getAreaOfEffect 算判定区（和真正挥砍时 DoDamage 用的同一个函数）：
        /// 匕首的判定区比剑、锤窄短，有的武器还带“额外范围”（addedAreaOfEffect）往四周撑大；剑挥动时每帧位置不同，取全部帧的并集。
        /// </summary>
        private static int ReachDirection(MeleeWeapon weapon, Monster m)
        {
            // 判定区盖住了也不一定打得到：隔着石头、墙就打不到（游戏的 isMonsterDamageApplicable）
            if (!HasDamageLineOfSight(m))
                return -1;
            var box = m.GetBoundingBox();
            int primary = DirToward(Player.StandingPixel, box.Center);
            int facing = Player.FacingDirection;
            try
            {
                // 先试正对着的方向，再试另一条轴上靠近它的方向（怪在斜角时两边都可能砍得到）
                int dx = box.Center.X - Player.StandingPixel.X, dy = box.Center.Y - Player.StandingPixel.Y;
                int secondary = primary is 1 or 3 ? (dy < 0 ? 0 : 2) : (dx > 0 ? 1 : 3);
                foreach (int dir in new[] { primary, secondary })
                {
                    Player.FacingDirection = dir;
                    var at = Player.GetToolLocation(ignoreClick: true);
                    Vector2 t1 = Vector2.Zero, t2 = Vector2.Zero;
                    for (int frame = 0; frame <= 5; frame++)
                    {
                        var area = weapon.getAreaOfEffect((int)at.X, (int)at.Y, dir, ref t1, ref t2, Player.GetBoundingBox(), frame);
                        if (area.Intersects(box))
                            return dir;
                    }
                }
                return -1;
            }
            catch
            {
                // 算不出来（比如模组武器重写了判定）：退回到按距离估算
                return Vector2.Distance(box.Center.ToVector2(), Player.StandingPixel.ToVector2()) <= 90f ? primary : -1;
            }
            finally
            {
                Player.FacingDirection = facing;
            }
        }

        /// <summary>
        /// 照抄游戏 GameLocation.isMonsterDamageApplicable：怪不贴身（曼哈顿距离 &gt; 1）时，
        /// 从她的格子一格格走到怪的格子（先横后竖、先竖后横两条路，有一条通就行），
        /// 中间有不能穿过的物体（石头等）或挡伤害的地形就打不到。会飞的怪和无视遮挡的怪不受影响。
        /// </summary>
        private static bool HasDamageLineOfSight(Monster m)
        {
            if (m.isGlider.Value || m.ignoreDamageLOS.Value)
                return true;
            Point from = Player.TilePoint, to = m.TilePoint;
            if (Util.Manhattan(from, to) <= 1)
                return true;
            bool Clear(bool horizontalBias)
            {
                int dx = to.X - from.X, dy = to.Y - from.Y, x = from.X, y = from.Y;
                while (dx != 0 || dy != 0)
                {
                    bool stepX = horizontalBias ? Math.Abs(dx) >= Math.Abs(dy) : Math.Abs(dy) < Math.Abs(dx);
                    if (stepX) { x += Math.Sign(dx); dx -= Math.Sign(dx); }
                    else { y += Math.Sign(dy); dy -= Math.Sign(dy); }
                    if ((Loc.objects.TryGetValue(new Vector2(x, y), out var o) && !o.isPassable()) || Loc.BlocksDamageLOS(x, y))
                        return false;
                }
                return true;
            }
            return Clear(true) || Clear(false);
        }

        /// <summary>她和怪之间（怪在 3 格以内）挡着的、紧挨着她的那块能敲碎的石头；没有返回 null。</summary>
        private static Point? BlockingStoneNextToMe(Monster m)
        {
            Point me = Player.TilePoint, to = m.TilePoint;
            if (Util.Manhattan(me, to) > 3)
                return null;
            foreach (var p in new[] { new Point(me.X + Math.Sign(to.X - me.X), me.Y), new Point(me.X, me.Y + Math.Sign(to.Y - me.Y)) })
            {
                if (p == me)
                    continue;
                if (Loc.objects.TryGetValue(new Vector2(p.X, p.Y), out var o) && o.IsBreakableStone())
                    return p;
            }
            return null;
        }

        /// <summary>朝怪物挥一次武器（和左键挥武器同一套流程）。</summary>
        private IEnumerable<object> SwingAt(int weaponIdx, Monster m, int dir)
        {
            for (int i = 0; i < 60 && (Player.UsingTool || Player.FarmerSprite.isUsingWeapon()); i++)
                yield return null;
            if (Player.Items[weaponIdx] is not MeleeWeapon weapon)
                yield break;
            Player.CurrentToolIndex = weaponIdx;
            var center = m.GetBoundingBox().Center;
            Player.faceDirection(dir);
            Player.lastClick = new Vector2(center.X, center.Y);
            Player.completelyStopAnimatingOrDoingAction();
            Player.CanMove = false;
            Player.UsingTool = true;
            Player.canReleaseTool = true;
            weapon.setFarmerAnimating(Player);
            for (int i = 0; i < 60 && (Player.UsingTool || Player.FarmerSprite.isUsingWeapon()); i++)
                yield return null;
        }

        /// <summary>
        /// 打附近的怪。生命低于 minHealth 比例时吃回血最多的食物；没食物就以 "!" 开头失败（调用方应撤退）。
        /// </summary>
        private IEnumerable<object> FightInner(int radius, float minHealth, int maxSeconds, FightStats st, Outcome o)
        {
            int weaponIdx = BestWeapon();
            if (weaponIdx < 0)
            {
                // 箱子里有武器就说在哪个箱子，免得白跑一趟公会
                string where = MissingForTrip("mine") is string miss && miss.Contains("武器在") ? "。" + miss : "";
                o.Fail("!背包里没有武器（镰刀不算；冒险者公会 AdventureGuild 可以买剑，进矿洞第一次也会送一把）" + where);
                yield break;
            }
            int start = Game1.ticks;
            var unreachable = new HashSet<Monster>();
            var everUnreachable = st.Unreachable;
            var waited = new Dictionary<Monster, int>();
            Monster chasing = null;
            int unreachableReset = Game1.ticks;
            var walk = new Outcome();
            var noEffect = st.NoEffect;
            // 连着 8 下也不掉血就别打了（会飞的怪多半只是没砍中，放宽到 20 下；木乃伊倒地时等它起来）
            void NoteSwing(Monster m, int hpBefore)
            {
                if (m.Health < hpBefore || (m is Mummy mummy && mummy.reviveTimer.Value > 0))
                {
                    noEffect.Remove(m);
                    return;
                }
                noEffect.TryGetValue(m, out int n);
                noEffect[m] = ++n;
                if (n >= (m.isGlider.Value ? 20 : 8))
                    st.Immune.Add(m);
            }
            while (Game1.ticks - start < maxSeconds * 60)
            {
                if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.isWarping || Player.health <= 0)
                    break;

                // 先保命
                if (Player.health < Player.maxHealth * minHealth)
                {
                    int food = BestHealingFood(out string reservedFood);
                    if (food < 0)
                    {
                        o.Fail($"!生命只剩 {Player.health}/{Player.maxHealth}，背包里没有能回血的食物"
                            + (reservedFood != null ? $"（只有要交的 {reservedFood}，没舍得吃）" : "") + "，赶紧撤退");
                        yield break;
                    }
                    foreach (var s in Eat(food.ToString(), force: true))
                        if (s is not ActionResult)
                            yield return s;
                    st.Eaten++;
                    if (reservedFood != null)
                        st.Notes.Add($"血量危急，只好吃了要交的 {reservedFood}");
                    continue;
                }

                if (Game1.ticks - unreachableReset > 300)
                {
                    unreachable.Clear();
                    unreachableReset = Game1.ticks;
                }
                var me = Player.TilePoint;
                // 会加血的怪（暗影萨满）先打：不然它一直给自己和别的怪回血。打残了它会逃跑，所以盯上以后追出 radius 也要追到死
                if (chasing != null && (chasing.Health <= 0 || !Loc.characters.Contains(chasing) || unreachable.Contains(chasing)
                                        || Util.Manhattan(chasing.TilePoint, me) > radius + 10))
                    chasing = null;
                var target = chasing ?? LiveMonsters(Loc)
                    .Where(m => !unreachable.Contains(m) && !st.Immune.Contains(m) && !HiddenNow(m) && Util.Manhattan(m.TilePoint, me) <= radius)
                    .OrderBy(m => IsHealer(m) ? 0 : ShellClosed(m) ? 2 : 1) // 缩壳的岩石蟹不动也不打人，放到最后
                    .ThenBy(m => Vector2.Distance(m.GetBoundingBox().Center.ToVector2(), Player.StandingPixel.ToVector2()))
                    .FirstOrDefault();
                if (target == null)
                {
                    // 只剩倒在地上的木乃伊：身上有炸弹就趁它们倒着炸掉（武器砍不动，10 秒后又会爬起来）
                    if (!st.BombGaveUp && BombsInBag().Count > 0 && DownedMummiesNear(radius) > 0)
                    {
                        int usedBefore = st.BombsUsed;
                        foreach (var s in BombDownedMummies(radius, st, st.Notes))
                            yield return s;
                        if (st.BombsUsed > usedBefore)
                            continue;
                        st.BombGaveUp = true;
                    }
                    break;
                }
                if (IsHealer(target))
                    chasing = target;

                if (CannotHurt(target))
                {
                    st.Immune.Add(target);
                    continue;
                }

                bool shellClosed = ShellClosed(target);
                if (shellClosed && Util.Manhattan(target.TilePoint, me) == 1)
                {
                    foreach (var s in UseToolOnce(Util.FindTool<Pickaxe>(), target.TilePoint, walk))
                        yield return s;
                    st.Swings++;
                    // 体力见底敲不动了
                    if (!walk.Ok)
                        st.Immune.Add(target);
                    walk.Succeed();
                    continue;
                }

                int swingDir = !shellClosed && Player.Items[weaponIdx] is MeleeWeapon held ? ReachDirection(held, target) : -1;
                if (swingDir >= 0)
                {
                    int hpBefore = target.Health;
                    foreach (var s in SwingAt(weaponIdx, target, swingDir))
                        yield return s;
                    st.Swings++;
                    if (target.Health <= 0 || !Loc.characters.Contains(target))
                        st.Kills++;
                    else if (IsDownedMummy(target))
                        st.MummyDowns++;
                    else
                        NoteSwing(target, hpBefore);
                    continue;
                }

                // 靠近：只走几步就重新判断，怪会动
                var goals = Pathfinder.AdjacentStandTiles(Loc, target.TilePoint);
                // 敲壳要站在旁边，不能走到蟹那一格上
                if (!shellClosed && Pathfinder.IsPassable(Loc, target.TilePoint.X, target.TilePoint.Y))
                    goals.Add(target.TilePoint);
                var path = goals.Count > 0 ? Pathfinder.FindPath(Loc, me, goals, 8000) : null;
                if (!shellClosed && (path == null || path.Count < 2) && Util.Manhattan(target.TilePoint, me) <= 1)
                {
                    // 已经贴着它了，判定区却没算到（碰撞框偏一点）：没地方再靠近，直接朝它挥
                    int hpBefore = target.Health;
                    foreach (var s in SwingAt(weaponIdx, target, DirToward(Player.StandingPixel, target.GetBoundingBox().Center)))
                        yield return s;
                    st.Swings++;
                    if (target.Health <= 0 || !Loc.characters.Contains(target))
                        st.Kills++;
                    else if (IsDownedMummy(target))
                        st.MummyDowns++;
                    else
                        NoteSwing(target, hpBefore);
                    continue;
                }
                if ((path == null || path.Count < 2) && InDungeon)
                {
                    // 被杂草、木桶、石头挡住：穿过去的路线上第一个障碍就在身边就清掉它（挥一两下就回来重新判断），
                    // 不在身边就先走到它跟前
                    var clearGoals = StandOrClearTiles(target.TilePoint);
                    var clearPath = clearGoals.Count > 0 ? Pathfinder.FindPath(Loc, me, clearGoals, 8000, clearable: ClearableObstacle) : null;
                    int block = clearPath?.FindIndex(1, p => ClearableObstacle(p)) ?? -1;
                    if (block == 1)
                    {
                        foreach (var s in ClearObstacle(clearPath[1], walk, maxHits: 2, stopOnThreat: false))
                            yield return s;
                        walk.Succeed();
                        continue;
                    }
                    if (block > 1)
                    {
                        foreach (var s in WalkPath(clearPath.Take(Math.Min(block, 4)).ToList(), walk))
                            yield return s;
                        walk.Succeed();
                        continue;
                    }
                }
                if (path == null || path.Count < 2)
                {
                    // 绕不过去、而且挡在中间的就是她身边的一块石头：用镐子敲掉再打（隔着石头挥剑是打不到的）
                    if (BlockingStoneNextToMe(target) is Point stone && Util.FindTool<Pickaxe>() is int pick && pick >= 0)
                    {
                        for (int k = 0; k < 6 && Loc.objects.ContainsKey(new Vector2(stone.X, stone.Y)); k++)
                        {
                            foreach (var s in UseToolOnce(pick, stone, walk))
                                yield return s;
                            if (!walk.Ok)
                                break;
                        }
                        walk.Succeed();
                        if (!Loc.objects.ContainsKey(new Vector2(stone.X, stone.Y)))
                            continue;
                    }
                    // 会飞的怪（蝙蝠等）过一会儿自己会飞过来；地上的怪离得近也多半在朝我们走（被石头挡住的路它可能绕得过来），原地等它几秒
                    waited.TryGetValue(target, out int w);
                    if (target.isGlider.Value || (Util.Manhattan(target.TilePoint, me) <= 5 && w < 240))
                    {
                        waited[target] = w + 15;
                        foreach (var s in WaitTicks(15))
                            yield return s;
                    }
                    else
                    {
                        unreachable.Add(target);
                        everUnreachable.Add(target);
                    }
                    continue;
                }
                foreach (var s in WalkPath(path.Take(Math.Min(path.Count, 4)).ToList(), walk))
                    yield return s;
            }
            o.Succeed();
        }

        public IEnumerable<object> Fight(int radius, int minHealthPercent)
        {
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            // 只剩倒地的木乃伊、身上又有炸弹时照样进去：FightInner 会去炸它们
            if (!LiveMonsters(Loc).Any() && !(BombsInBag().Count > 0 && DownedMummiesNear(Math.Clamp(radius, 2, 40)) > 0))
            {
                yield return ActionResult.Success("附近没有能打的怪物" + (MummyNote(new FightStats(), radius) is string onlyMummies ? "。" + onlyMummies : ""));
                yield break;
            }
            var st = new FightStats();
            var o = new Outcome();
            foreach (var s in FightInner(Math.Clamp(radius, 2, 40), Math.Clamp(minHealthPercent, 10, 90) / 100f, 120, st, o))
                yield return s;
            int left = LiveMonsters(Loc).Count(m => Util.Manhattan(m.TilePoint, Player.TilePoint) <= radius);
            string msg = $"挥了 {st.Swings} 下，打倒 {st.Kills} 只，吃了 {st.Eaten} 次东西，生命 {Player.health}/{Player.maxHealth}，附近还剩 {left} 只";
            if (MummyNote(st, radius) is string mummyNote)
                msg += "。" + mummyNote;
            if (st.Notes.Count > 0)
                msg += "。" + string.Join("；", st.Notes.Distinct());
            var blocked = st.Unreachable.Where(m => m.Health > 0 && Loc.characters.Contains(m)).ToList();
            var immune = st.Immune.Where(m => m.Health > 0 && Loc.characters.Contains(m)).ToList();
            KnownUnreachable = new HashSet<Monster>(blocked.Concat(immune));
            if (blocked.Count > 0)
                msg += $"。够不着：{string.Join("、", blocked.Take(4).Select(m => $"{MonsterName(m)}({m.TilePoint.X},{m.TilePoint.Y})"))}"
                     + " 和你之间没有能走的路（被石头或墙隔开）。它过不来就不用管，继续干别的；挡路的话用 use_tool 拿镐子敲掉中间的石头再 fight";
            if (immune.Count > 0)
                msg += $"。打不动：{string.Join("、", immune.Take(4).Select(m => $"{MonsterName(m)}({m.TilePoint.X},{m.TilePoint.Y})"))}"
                     + "（缩壳的岩石蟹要用镐子敲，没镐子或没体力时打不了；装甲虫要带除虫附魔的武器）。先绕开它，继续干别的";
            yield return o.Ok ? ActionResult.Success(msg) : ActionResult.Fail(o.Message.TrimStart('!') + "。" + msg);
        }

        // ───────────────────────── 下矿洞 ─────────────────────────

        /// <summary>
        /// 矿洞里还没拿空的宝箱（每 10 层的奖励箱、宝藏房的箱子）。它们放在 overlayObjects 里，
        /// 但 GameLocation.objects 是叠加了 overlayObjects 的字典，所以从 objects 里就能找到。
        /// </summary>
        private static IEnumerable<(Point tile, Chest chest)> MineChests(GameLocation mine) =>
            mine.objects.Pairs
                .Where(p => p.Value is Chest c && !c.playerChest.Value && c.GetItemsForPlayer().Count > 0)
                .Select(p => (new Point((int)p.Key.X, (int)p.Key.Y), (Chest)p.Value))
                .OrderBy(p => Util.Manhattan(p.Item1, Player.TilePoint));

        /// <summary>
        /// 开矿洞宝箱：和玩家点击一样走 checkAction。第一下掀开盖子，之后每点一下拿出一样东西（举过头顶展示，
        /// 背包满时弹出拿取界面）。拿不了（走不到、背包满）时通过 note 说明，调用方就不再反复尝试这个箱子。
        /// </summary>
        private IEnumerable<object> OpenMineChest(GameLocation mine, Point tile, List<string> took, Action<string> note)
        {
            var o = new Outcome();
            foreach (var s in GoAdjacent(tile, o))
                yield return s;
            if (!o.Ok)
            {
                note($"({tile.X},{tile.Y}) 有个宝箱走不过去（{o.Message}），可以敲开周围的石头后用 interact 打开");
                yield break;
            }
            Player.faceDirection(DirToward(Player.TilePoint, tile));
            for (int click = 0; click < 12; click++)
            {
                if (!(mine.objects.TryGetValue(new Vector2(tile.X, tile.Y), out var obj) && obj is Chest chest) || chest.GetItemsForPlayer().Count == 0)
                    yield break;
                var before = chest.GetItemsForPlayer().Where(i => i != null).Select(i => i.DisplayName).ToList();
                mine.checkAction(new xLocation(tile.X, tile.Y), Game1.viewport, Player);
                // 掀盖子、举起物品的动画
                for (int t = 0; t < 90 && (Player.freezePause > 0 || Player.isEmoting || Player.FarmerSprite.PauseForSingleAnimation); t++)
                    yield return null;
                foreach (var s in WaitTicks(30))
                    yield return s;
                var after = chest.GetItemsForPlayer().Where(i => i != null).Select(i => i.DisplayName).ToList();
                foreach (var name in after)
                    before.Remove(name);
                if (Game1.activeClickableMenu is ItemGrabMenu grab)
                {
                    // 背包满了：游戏已经把这件从箱子里拿出来放进拿取界面。宝箱里多半是独一份的装备（鞋子、武器），
                    // 不能关掉界面让它掉在地上：好鞋直接穿上，其他的把背包里最不值钱的一格丢到脚边换它进来
                    var reward = grab.ItemsToGrabMenu.actualInventory.Where(i => i != null).ToList();
                    var gotList = new List<string>();
                    var onGround = GrabAll(grab, gotList);
                    var rewardNames = reward.Select(Util.ItemLabel).ToList();
                    var lost = onGround.Where(rewardNames.Contains).ToList();
                    if (lost.Count == 0)
                    {
                        took.AddRange(reward.Select(i => i.DisplayName));
                        if (onGround.Count > 0)
                            note($"背包满了，为了装下宝箱里的 {string.Join("、", rewardNames)}，把 {string.Join("、", onGround)} 丢在了脚边 ({Player.TilePoint.X},{Player.TilePoint.Y})");
                        continue;
                    }
                    var dropped = lost;
                    var left = mine.objects.TryGetValue(new Vector2(tile.X, tile.Y), out var rest) && rest is Chest restChest
                        ? restChest.GetItemsForPlayer().Where(i => i != null).Select(i => i.DisplayName).ToList() : new List<string>();
                    note($"背包满了，宝箱里的 {string.Join("、", dropped)} 掉在你脚边 ({Player.TilePoint.X},{Player.TilePoint.Y}) 附近的地上：扔掉或用掉一样东西腾出位置，再走过去就会捡起来"
                         + (left.Count > 0 ? $"。箱子里还剩 {string.Join("、", left)}，腾出位置后 interact ({tile.X},{tile.Y})" : ""));
                    yield break;
                }
                took.AddRange(before);
                if (Game1.activeClickableMenu is DialogueBox)
                    Game1.exitActiveMenu();
            }
        }

        /// <summary>矿洞里的矿石巨石（2x2，任何镐子都能敲），按离她的距离排序；corner 是左上角。</summary>
        private static IEnumerable<(Point corner, ResourceClump clump)> MineBoulders(MineShaft mine) =>
            mine.resourceClumps
                .Where(c => c.parentSheetIndex.Value is 752 or 754 or 756 or 758)
                .Select(c => (new Point((int)c.Tile.X, (int)c.Tile.Y), c))
                .OrderBy(b => Util.Manhattan(b.Item1, Player.TilePoint));

        private static IEnumerable<Point> BoulderTiles(Point corner) =>
            new[] { corner, new Point(corner.X + 1, corner.Y), new Point(corner.X, corner.Y + 1), new Point(corner.X + 1, corner.Y + 1) };

        /// <summary>走到这几格中任意一格的旁边（巨石占 2x2，哪一面能站就从哪一面敲）。</summary>
        private IEnumerable<object> GoAdjacentToAny(IEnumerable<Point> tiles, Outcome o)
        {
            var set = tiles.ToHashSet();
            var me = Player.TilePoint;
            if (set.Any(t => Util.Manhattan(t, me) == 1))
            {
                o.Succeed();
                yield break;
            }
            var stands = set.SelectMany(t => Pathfinder.AdjacentStandTiles(Loc, t)).Where(p => !set.Contains(p)).Distinct().ToList();
            var path = stands.Count > 0 ? Pathfinder.FindPath(Loc, me, stands, 8000) : null;
            if (path == null && InDungeon)
            {
                var around = set.SelectMany(StandOrClearTiles).Where(p => !set.Contains(p)).Distinct().ToList();
                foreach (var s in GoToAnyClearing(around, o, "巨石旁边"))
                    yield return s;
                yield break;
            }
            if (path == null)
            {
                o.Fail("走不到巨石旁边");
                yield break;
            }
            foreach (var s in WalkPath(path, o))
                yield return s;
        }

        private static Point? NearestMineTile(GameLocation loc, int tileIndex)
        {
            Point? best = null;
            int bestD = int.MaxValue;
            var me = Player.TilePoint;
            int w = Pathfinder.MapWidth(loc), h = Pathfinder.MapHeight(loc);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    if (loc.getTileIndexAt(x, y, "Buildings", "mine") == tileIndex)
                    {
                        int d = Util.Manhattan(me, new Point(x, y));
                        if (d < bestD)
                        {
                            bestD = d;
                            best = new Point(x, y);
                        }
                    }
            return best;
        }

        /// <summary>
        /// 自动下矿：打附近的怪、敲石头找楼梯、下楼，直到下了 levels 层或者体力/生命/时间不够。
        /// 在矿洞入口（Mine）时会先下到第 1 层，或者用电梯到 start_level（必须是 5 的倍数且到过）。
        /// </summary>
        /// <summary>血量到这个数才跳竖井：一次最多连降 15 层、掉 45 点血，跳完还要剩三成血应付下面的怪。</summary>
        private static int ShaftSafeHealth => 45 + (int)(Player.maxHealth * 0.3f);

        internal enum MineGoal { Balanced, Descent, Ore, Coal, Gems }

        internal enum MineNodeTier
        {
            Stone, Copper, Iron, Gold, Iridium, Coal, CoalStone, LowCoalStone, Gem,
            Geode, FrozenGeode, MagmaGeode, OmniGeode, Mystic, Radioactive, CalicoEgg,
            CinderShard, Mussel, Bone, Clay
        }

        internal static MineGoal? ParseMineGoal(string goal) => goal?.Trim().ToLowerInvariant() switch
        {
            null or "" or "balanced" => MineGoal.Balanced,
            "descent" => MineGoal.Descent,
            "ore" => MineGoal.Ore,
            "coal" => MineGoal.Coal,
            "gems" => MineGoal.Gems,
            _ => null
        };

        // GameLocation.cs:14861-15062 是专属掉落；这里只给有额外煤率的石头单列档位。
        // 普通矿层石头仍可能随机出煤、矿或晶球（MineShaft.cs:3670-3709），继续按普通石头找楼梯。
        internal static MineNodeTier ClassifyMineStone(string id, bool generatedMine) => id switch
        {
            "751" or "849" => MineNodeTier.Copper,
            "290" or "850" => MineNodeTier.Iron,
            "764" or "VolcanoGoldNode" => MineNodeTier.Gold,
            "765" => MineNodeTier.Iridium,
            "BasicCoalNode0" or "BasicCoalNode1" or "VolcanoCoalNode0" or "VolcanoCoalNode1" => MineNodeTier.Coal,
            "668" or "670" or "845" or "846" or "847" => MineNodeTier.CoalStone,
            "40" or "42" when generatedMine => MineNodeTier.LowCoalStone, // MineShaft.cs:3623、3695-3704
            "343" or "450" when !generatedMine => MineNodeTier.CoalStone, // GameLocation.cs:14832-14847
            "2" or "4" or "6" or "8" or "10" or "12" or "14" or "44" => MineNodeTier.Gem,
            "75" => MineNodeTier.Geode,
            "76" => MineNodeTier.FrozenGeode,
            "77" => MineNodeTier.MagmaGeode,
            "819" => MineNodeTier.OmniGeode,
            "46" => MineNodeTier.Mystic,
            "95" => MineNodeTier.Radioactive,
            "CalicoEggStone_0" or "CalicoEggStone_1" or "CalicoEggStone_2" => MineNodeTier.CalicoEgg,
            "843" or "844" => MineNodeTier.CinderShard,
            "25" => MineNodeTier.Mussel,
            "816" or "817" => MineNodeTier.Bone,
            "818" => MineNodeTier.Clay,
            _ => MineNodeTier.Stone
        };

        private static MineNodeTier GetMineNodeTier(SObject node, GameLocation location)
        {
            string id = node.ItemId;
            bool generatedMine = (id is "40" or "42" or "343" or "450") && MineShaft.IsGeneratedLevel(location);
            return ClassifyMineStone(id, generatedMine);
        }

        /// <summary>单点挖矿和炸点共用的评分入口。输入真实节点和地点，档位与偏好可独立调整。</summary>
        internal static int ScoreMineNode(SObject node, GameLocation location, MineGoal goal) =>
            ScoreMineNodeTier(GetMineNodeTier(node, location), goal);

        internal static int ScoreMineNodeTier(MineNodeTier tier, MineGoal goal)
        {
            // 列顺序：均衡、下楼、金属、煤、宝石。下楼分只用于顺路取舍，不换算成绕路距离。
            var (balanced, descent, ore, coal, gems) = tier switch
            {
                MineNodeTier.Copper      => ( 6, 0,  8,  2,  2),
                MineNodeTier.Iron        => ( 7, 0, 10,  2,  2),
                MineNodeTier.Gold        => ( 8, 0, 12,  3,  3),
                MineNodeTier.Iridium     => (12, 2, 16,  4,  4),
                MineNodeTier.Coal        => ( 8, 0,  2, 16,  2),
                MineNodeTier.CoalStone   => ( 2, 0,  0,  6,  0),
                MineNodeTier.LowCoalStone => ( 1, 0,  0,  3,  0),
                MineNodeTier.Gem         => ( 8, 0,  2,  2, 14),
                MineNodeTier.Geode       => ( 5, 0,  1,  1,  8),
                MineNodeTier.FrozenGeode => ( 6, 0,  1,  1, 10),
                MineNodeTier.MagmaGeode  => ( 7, 0,  1,  1, 12),
                MineNodeTier.OmniGeode   => ( 9, 1,  2,  2, 15),
                MineNodeTier.Mystic      => (13, 3, 14,  4, 16),
                MineNodeTier.Radioactive => (11, 2, 13,  3,  3),
                MineNodeTier.CalicoEgg   => ( 8, 0,  2,  2,  2),
                MineNodeTier.CinderShard => ( 8, 0,  4,  2,  2),
                MineNodeTier.Mussel      => ( 4, 0,  1,  1,  1),
                MineNodeTier.Bone        => ( 5, 0,  1,  1,  1),
                MineNodeTier.Clay        => ( 4, 0,  1,  1,  1),
                _                       => ( 0, 0,  0,  0,  0)
            };
            return goal switch
            {
                MineGoal.Descent => descent,
                MineGoal.Ore => ore,
                MineGoal.Coal => coal,
                MineGoal.Gems => gems,
                _ => balanced
            };
        }

        private static int MineNodeDetour(int score, MineGoal goal) => goal == MineGoal.Descent ? 0 : score;

        private static IEnumerable<(Point pt, int score)> ScoredMineStones(GameLocation location, MineGoal goal) =>
            location.objects.Pairs.Where(kv => kv.Value.IsBreakableStone())
                .Select(kv => (new Point((int)kv.Key.X, (int)kv.Key.Y), ScoreMineNode(kv.Value, location, goal)));

        // 通路已找到才顺手挖。下楼目标只挖站在原路线就够得着的矿；其他目标按真实步数限制绕路。
        private static (Point tile, Point stand)? MineNodeNearExit(GameLocation mine, MineGoal goal,
            List<Point> route, IEnumerable<(Point pt, int score)> stones)
        {
            var candidates = stones.Where(t => t.score > 0).ToList();
            if (goal == MineGoal.Descent)
            {
                foreach (var stand in route)
                {
                    var node = candidates.Where(t => Util.Manhattan(t.pt, stand) == 1).OrderByDescending(t => t.score).FirstOrDefault();
                    if (node.score > 0)
                        return (node.pt, stand);
                }
                return null;
            }

            var me = Player.TilePoint;
            foreach (var node in candidates.Where(t => Util.Manhattan(t.pt, me) <= 2 + MineNodeDetour(t.score, goal))
                .OrderBy(t => Util.Manhattan(t.pt, me) - MineNodeDetour(t.score, goal)).ThenByDescending(t => t.score))
            {
                var path = Pathfinder.FindPath(mine, me, Pathfinder.AdjacentStandTiles(mine, node.pt), 8000);
                if (path != null && path.Count - 1 <= 1 + MineNodeDetour(node.score, goal))
                    return (node.pt, path[^1]);
            }
            return null;
        }

        private static string MineNodeName(MineNodeTier tier) => tier switch
        {
            MineNodeTier.Copper => "铜矿",
            MineNodeTier.Iron => "铁矿",
            MineNodeTier.Gold => "金矿",
            MineNodeTier.Iridium => "铱矿",
            MineNodeTier.Coal => "煤矿",
            MineNodeTier.CoalStone => "概率掉煤石",
            MineNodeTier.LowCoalStone => "低概率掉煤石",
            MineNodeTier.Gem => "宝石矿",
            MineNodeTier.Geode => "普通晶球石",
            MineNodeTier.FrozenGeode => "冰封晶球石",
            MineNodeTier.MagmaGeode => "岩浆晶球石",
            MineNodeTier.OmniGeode => "万象晶球石",
            MineNodeTier.Mystic => "神秘石",
            MineNodeTier.Radioactive => "放射性矿",
            MineNodeTier.CalicoEgg => "三花蛋石",
            MineNodeTier.CinderShard => "熔岩碎片矿",
            MineNodeTier.Mussel => "贻贝石",
            MineNodeTier.Bone => "骨头石",
            MineNodeTier.Clay => "黏土石",
            _ => "普通石头"
        };

        // 按回合比较矿点，连寻路时顺手清掉的也能算进去；报节点数，掉落件数由背包变化报告。
        private sealed class MineNodeTally
        {
            private readonly Dictionary<MineNodeTier, int> mined = new();
            private readonly Dictionary<Vector2, (SObject node, MineNodeTier tier)> remaining = new();
            private GameLocation last;

            public int Rocks { get; private set; }

            public void Observe(GameLocation location)
            {
                if (last != null)
                    foreach (var (tile, previous) in remaining)
                    {
                        if (last.objects.TryGetValue(tile, out var node) && ReferenceEquals(node, previous.node) && node.IsBreakableStone())
                            continue;
                        Rocks++;
                        if (previous.tier != MineNodeTier.Stone)
                            mined[previous.tier] = mined.GetValueOrDefault(previous.tier) + 1;
                    }

                last = location;
                remaining.Clear();
                if (location == null)
                    return;
                foreach (var (tile, node) in location.objects.Pairs)
                    if (node.IsBreakableStone())
                        remaining[tile] = (node, GetMineNodeTier(node, location));
            }

            public string Summary(MineGoal goal)
            {
                Observe(null);
                string label = goal.ToString().ToLowerInvariant();
                return mined.Count == 0 ? $"；目标 {label}，没有挖到特殊节点"
                    : $"；目标 {label}，挖掉的节点 " + string.Join("、", mined.OrderByDescending(kv => ScoreMineNodeTier(kv.Key, goal))
                        .ThenBy(kv => kv.Key).Select(kv => $"{MineNodeName(kv.Key)} {kv.Value}"));
            }
        }

        /// <summary>下楼前顺手处理的东西最远多少格（曼哈顿距离）：再远就不绕了。</summary>
        private const int MineExtraRange = 8;

        /// <summary>这一层附近能捡的矿物/采集物和能劈的木箱木桶，近的在前。</summary>
        private static IEnumerable<(Point p, SObject obj)> MineExtras(GameLocation mine, HashSet<Point> skipped, int range = MineExtraRange)
        {
            var me = Player.TilePoint;
            return mine.objects.Pairs
                .Select(kv => (p: new Point((int)kv.Key.X, (int)kv.Key.Y), obj: kv.Value))
                .Where(t => !skipped.Contains(t.p) && Util.Manhattan(t.p, me) <= range
                    && (t.obj is BreakableContainer || IsGrabbable(t.obj) && Player.couldInventoryAcceptThisItem(t.obj)))
                .OrderBy(t => Util.Manhattan(t.p, me));
        }

        /// <summary>矿层怎么称呼：骷髅洞穴的层在游戏里是 121 起，对玩家说“骷髅洞穴第 N 层”。</summary>
        private static string MineLevelName(int level) => level > 120 ? $"骷髅洞穴第 {level - 120} 层" : $"第 {level} 层";

        /// <summary>
        /// 跳竖井（骷髅洞穴里的洞，图块 174）：点它会问“要不要跳下去”，选“是”后连降几层、每层掉 3 点血（不会摔死，最少剩 1）。
        /// 落地后游戏弹一句“你掉了 N 层”，这里顺手关掉。成功返回 true（已经在新的一层）。
        /// </summary>
        private IEnumerable<object> JumpDownShaft(MineShaft mine, Point shaft, Outcome o)
        {
            foreach (var s in GoAdjacent(shaft, o))
                yield return s;
            if (!o.Ok)
                yield break;
            mine.checkAction(new xLocation(shaft.X, shaft.Y), Game1.viewport, Player);
            foreach (var s in WaitTicks(10))
                yield return s;
            if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask)
            {
                int yes = Array.FindIndex(ask.responses, r => r.responseKey is "Jump" or "Yes"); // 游戏里这个问题的选项是 Jump / Do（跳下去 / 算了）
                if (yes < 0)
                {
                    ask.closeDialogue();
                    o.Fail("竖井的问题里没找到“跳下去”这个选项：" + string.Join("/", ask.responses.Select(r => r.responseKey)));
                    yield break;
                }
                foreach (var s in Dialogue(yes))
                    if (s is not ActionResult)
                        yield return s;
            }
            // 选完以后游戏马上弹一句“你掉了 N 层”，要等这句话关掉才真的换到下面的楼层，所以在这里就把它关了
            for (int t = 0; t < 900 && Loc == mine; t++)
            {
                if (Game1.activeClickableMenu is DialogueBox { isQuestion: false } fellMsg)
                    fellMsg.closeDialogue();
                yield return null;
            }
            if (Loc == mine)
            {
                o.Fail("选了跳下去，但是没有掉下去");
                yield break;
            }
            // 掉落动画放完、落地的提示出来再继续；提示是普通对话框，关掉就行
            for (int t = 0; t < 600; t++)
            {
                if (Game1.activeClickableMenu is DialogueBox { isQuestion: false } landedMsg)
                {
                    landedMsg.closeDialogue(); // 用对话框自己的关闭：直接退菜单会留下 dialogueUp，人就动不了了
                    break;
                }
                if (t > 120 && Loc is MineShaft landed && !landed.isFallingDownShaft && Player.CanMove && !Game1.fadeToBlack && !Game1.isWarping)
                    break;
                yield return null;
            }
            foreach (var s in WaitTicks(20))
                yield return s;
            o.Succeed();
        }

        public IEnumerable<object> ExploreMine(int levels, int? startLevel, string whichMine = null, int? maxBombs = null, string goal = null)
        {
            if (ParseMineGoal(goal) is not MineGoal miningGoal)
            {
                yield return ActionResult.Fail($"不认识采矿目标“{goal}”，可以用 balanced、descent、ore、coal 或 gems");
                yield break;
            }

            var o = new Outcome();
            string startNote = null; // 没传 start_level 时自动坐电梯去最深层，结果里说清楚
            if (Blocked(out string blockedWhy))
            {
                yield return ActionResult.Fail(blockedWhy);
                yield break;
            }
            // 在农场出发、背包杂或空位少：先自动整理一次（purpose=mine）。人工整理过的话杂物少，这里就不会触发；
            // 只在农场做（箱子在那儿），进出矿洞来回不重复跑
            string tidyNote = null;
            if (Loc is Farm && TripClutterHint("mine") != null)
            {
                ActionResult tidied = null;
                foreach (var s in TidyBackpack("", "mine"))
                {
                    if (s is ActionResult r)
                        tidied = r;
                    else
                        yield return s;
                }
                tidyNote = tidied == null ? null : "出发前自动整理了背包：" + tidied.Message;
            }
            // 出门带了下矿用不上的东西，结束时提醒一句（下次出门前整理）
            string clutter = TripClutterHint("mine");
            if (tidyNote != null)
                clutter = clutter == null ? tidyNote : tidyNote + "；" + clutter;
            // 刚下矿时背包几乎满了：照样下，结束时说清楚（上面的杂物提醒已经说了空位的话就不重复）
            if (clutter == null && Loc is not MineShaft and not VolcanoDungeon && Player.freeSpotsInInventory() < 3)
                clutter = $"⚠ 下矿时背包只剩 {Player.freeSpotsInInventory()} 个空位，敲出来的矿石宝石很快就装不下（掉在地上）。下次先 tidy_backpack purpose=mine 再来";
            // 去哪个矿：明说 skull，或者人已经在沙漠 / 骷髅洞穴入口，就是骷髅洞穴
            whichMine = whichMine?.Trim().ToLowerInvariant();
            // 姜岛火山：地图和走法都不一样（岩浆、机关门、往上走），单独处理
            if (whichMine is "volcano" or "火山" or "火山地牢" || (string.IsNullOrEmpty(whichMine) && Loc is VolcanoDungeon or Caldera or IslandNorth))
            {
                foreach (var s in ExploreVolcano(levels, maxBombs, miningGoal))
                    yield return s;
                yield break;
            }
            // 去掉空格、下划线、连字符再比，大小写不分："Skull Cavern"、skull_cavern、skullcavern 都算
            string whereKey = (whichMine ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            bool skull = whereKey is "skull" or "skullcave" or "skullcavern" or "desert" or "骷髅" or "骷髅洞" or "骷髅洞穴" or "沙漠"
                || (string.IsNullOrEmpty(whichMine) && Loc.Name is "SkullCave" or "Desert");
            if (Loc is not MineShaft && skull)
            {
                if (!Player.hasSkullKey)
                {
                    yield return ActionResult.Fail("没有骷髅钥匙，进不了骷髅洞穴（普通矿洞下到 120 层会拿到）");
                    yield break;
                }
                if (Loc.Name != "SkullCave")
                {
                    foreach (var s in TravelInner("SkullCave", o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail("去骷髅洞穴入口失败（在沙漠的西北角，要先坐巴士到沙漠）：" + o.Message);
                        yield break;
                    }
                }
                var door = FindTileAction(Loc, "SkullDoor");
                if (!door.HasValue)
                {
                    yield return ActionResult.Fail("在骷髅洞穴入口没找到那扇门");
                    yield break;
                }
                foreach (var s in GoAdjacent(door.Value, o))
                    yield return s;
                // 第一次点是用钥匙开锁（弹一句话），第二次才进去
                for (int attempt = 0; attempt < 3 && Loc is not MineShaft; attempt++)
                {
                    Loc.checkAction(new xLocation(door.Value.X, door.Value.Y), Game1.viewport, Player);
                    for (int t = 0; t < 240 && Loc is not MineShaft; t++)
                    {
                        if (Game1.activeClickableMenu is DialogueBox { isQuestion: false } unlockMsg)
                            unlockMsg.closeDialogue();
                        yield return null;
                    }
                }
                foreach (var s in WaitSceneSettled())
                    yield return s;
                if (Loc is not MineShaft)
                {
                    yield return ActionResult.Fail("没能进入骷髅洞穴");
                    yield break;
                }
            }
            else if (Loc is not MineShaft)
            {
                // 电梯层：5 的倍数、到过的，最多到 120（再往下是骷髅洞穴的层数）。出发前就检查，别走到矿洞门口才说
                int topElevator = Math.Min(MineShaft.lowestLevelReached, 120) / 5 * 5;
                // 没传 start_level：有电梯就直接坐到到过的最深一层，别每次都从第 1 层重新挖（想从头挖就传 start_level=1）
                int target = startLevel ?? (topElevator >= 5 ? topElevator : 1);
                if (startLevel == null && topElevator >= 5)
                    startNote = $"没指定 start_level，已坐电梯直达你到过的最深电梯层（第 {topElevator} 层）；想从浅层挖就传 start_level（比如 1、5、45）";
                if (target > 1 && (target % 5 != 0 || target > topElevator))
                {
                    string floors = topElevator < 5 ? "你还没解锁电梯（下到第 5 层才有），只能从第 1 层开始（不传 start_level）"
                        : $"你现在能按：{string.Join("、", Enumerable.Range(1, topElevator / 5).Select(i => i * 5))}";
                    int suggest = Math.Min(target, topElevator) / 5 * 5;
                    yield return ActionResult.Fail($"电梯到不了第 {target} 层：电梯只能到 5 的倍数层，而且要到过（你最深到过第 {MineShaft.lowestLevelReached} 层）。{floors}"
                        + (suggest >= 5 ? $"。离你想去的最近的是 start_level={suggest}" : "") + "。没有出发");
                    yield break;
                }
                // 120 层是普通矿洞的最底层，下面没有楼梯了：拿过骷髅钥匙以后再按 120 只能在这一层打转
                if (target >= 120 && MineShaft.lowestLevelReached >= 120 && Game1.player.hasSkullKey)
                {
                    yield return ActionResult.Fail("第 120 层是普通矿洞的最底层，下面没有了，骷髅钥匙也已经拿过，再去只能在这一层打转。"
                        + "按想要的东西选层：铜矿 start_level=5~35，铁矿 45~75，金矿 85~115（层越深宝石、晶球越好）；"
                        + "要铱矿、更深的层就去骷髅洞穴 explore_mine where=skull。没有出发");
                    yield break;
                }
                if (!Loc.Name.Equals("Mine", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in TravelInner("Mine", o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail("去矿洞入口失败（春季第5天后才开放）：" + o.Message);
                        yield break;
                    }
                }
                // 第一次进矿洞会放马龙的剧情：剧情在入口地图上播，这时进矿洞会把人带走、剧情卡在入口，人困在地图外
                if (Blocked(out string sceneWhy))
                {
                    yield return ActionResult.Fail("到了矿洞入口，但" + sceneWhy + "。先用 dialogue 推进（或 skip_event 跳过），剧情放完再 explore_mine");
                    yield break;
                }
                var entry = FindTileAction(Loc, target > 1 ? "MineElevator" : "Mine");
                if (entry.HasValue)
                {
                    foreach (var s in GoAdjacent(entry.Value, o))
                        yield return s;
                }
                if (target > 1)
                    Game1.enterMine(target); // 等同在电梯界面选了这一层
                else if (entry.HasValue)
                    Loc.checkAction(new xLocation(entry.Value.X, entry.Value.Y), Game1.viewport, Player);
                else
                    Game1.enterMine(1);
                for (int t = 0; t < 600 && Loc is not MineShaft; t++)
                    yield return null;
                foreach (var s in WaitSceneSettled())
                    yield return s;
                if (Loc is not MineShaft)
                {
                    yield return ActionResult.Fail("没能进入矿洞");
                    yield break;
                }
            }

            levels = Math.Clamp(levels, 1, 20);
            int startLvl = ((MineShaft)Loc).mineLevel;
            int descended = 0;
            var minedNodes = new MineNodeTally();
            var st = new FightStats();
            string stop = null;
            int pick = Util.FindTool<Pickaxe>();
            if (pick < 0)
            {
                yield return ActionResult.Fail("没有镐子，没法在矿洞里敲石头");
                yield break;
            }

            int guard = 0;
            int lastHp = Player.health;
            var loot = new List<string>();
            var chestNotes = new List<string>();
            var skippedChests = new HashSet<Point>();
            var skippedBoulders = new HashSet<Point>();
            // 走不到的石头（隔着岩浆、水、墙）：记下来不再试，接着试更远的，别因为最近几块够不着就停下
            var skippedStones = new HashSet<(int level, Point pt)>();
            // 走不过去的竖井，和当时已经敲掉了多少块石头：再敲掉几块以后路可能通了，到时再试
            var skippedShafts = new Dictionary<Point, int>();
            int boulders = 0, shaftJumps = 0;
            // 下楼前顺手捡的矿物/采集物、劈开的木箱木桶；走不过去或弄不掉的格子记下来不再试
            var grabbed = new List<string>();
            int containers = 0;
            var skippedExtras = new HashSet<Point>();
            // 炸石头：这次最多用几颗（不传 = 最多 5 颗，0 = 不用），放过没成的格子不再试
            int bombBudget = Math.Clamp(maxBombs ?? 5, 0, 50), blastBombs = 0, blastStones = 0, blastOres = 0;
            var skippedBlasts = new HashSet<Point>();
            var blastNotes = new List<string>();
            // 这一层是不是“有空”把东西收拾干净再走：体力、血量够，身边没怪，在这层待得还不到 90 分钟。
            int levelSeen = -1, levelSince = 0;
            while (descended < levels && guard++ < 400)
            {
                if (Loc is not MineShaft mine)
                {
                    stop = "离开了矿洞";
                    break;
                }
                minedNodes.Observe(mine);
                MainThread.Progress = $"{(mine.mineLevel > 120 ? $"骷髅洞穴第 {mine.mineLevel - 120} 层" : $"矿洞第 {mine.mineLevel} 层")}，这次已下 {descended}/{levels} 层，"
                    + $"敲了 {minedNodes.Rocks} 块石头，打倒 {st.Kills} 只怪，生命 {Player.health}/{Player.maxHealth}，体力 {(int)Player.Stamina}，背包空位 {Player.freeSpotsInInventory()}";
                if (Game1.isWarping || Game1.fadeToBlack || Game1.globalFade)
                {
                    // 下楼梯/进矿洞的淡入淡出还没结束，等它结束再继续，不算停下
                    foreach (var s in WaitSceneSettled())
                        yield return s;
                    continue;
                }
                if (Blocked(out string why))
                {
                    // 宝箱、对话等：交给 AI 处理
                    stop = why;
                    break;
                }
                if (Player.Stamina < 15)
                {
                    stop = "体力不够了";
                    break;
                }
                // 背包满了：敲出来的矿石、宝石捡不起来，再往下走就丢在这一层了
                var leftBehind = mine.debris.Where(d => IsPickupDebris(d) && !CanAccept(d)).ToList();
                if (leftBehind.Count > 0)
                {
                    var where = DebrisTile(leftBehind[0]);
                    stop = $"背包满了，地上的 {string.Join("、", leftBehind.Select(DebrisName).Distinct().Take(5))} 捡不起来（在 ({where.X},{where.Y}) 附近）。"
                         + "先腾位置：吃掉食物（eat），或者用 trash_item 扔掉最不值钱的东西，再走过去捡，然后继续 explore_mine。下一层之前不捡就丢了";
                    break;
                }
                if (TimeToHeadHome() is string late)
                {
                    stop = late;
                    break;
                }

                int nowMin = Game1.timeOfDay / 100 * 60 + Game1.timeOfDay % 100;
                if (mine.mineLevel != levelSeen)
                {
                    levelSeen = mine.mineLevel;
                    levelSince = nowMin;
                    skippedShafts.Clear();
                }
                bool leisure = Player.Stamina >= Player.MaxStamina * 0.35f && Player.health >= Player.maxHealth * 0.5f && NearestThreat(4) == null
                    && nowMin - levelSince <= 90 && !Player.isInventoryFull();
                int reach = leisure && miningGoal == MineGoal.Balanced ? 200 : MineExtraRange;
                // 血量够、开过宝箱就先跳竖井：一次最多掉 45 点血，跳完仍要留三成血。
                var shaft = NearestMineTile(mine, 174);
                // 有竖井却走了楼梯，多半是血量不够安全跳（每层掉 3 点血）：记下来告诉玩家，别让人以为“有洞不跳”是 bug
                if (shaft.HasValue && Player.health < ShaftSafeHealth && !chestNotes.Any(n => n.StartsWith("有竖井")))
                    chestNotes.Add($"有竖井但生命 {Player.health} 低于跳竖井要求的 {ShaftSafeHealth}（一次最多掉 45 点），所以没跳、走楼梯；吃点东西回血就会跳");
                if (shaft.HasValue && !MineChests(mine).Any(c => !skippedChests.Contains(c.tile)) && (!skippedShafts.TryGetValue(shaft.Value, out int rocksThen) || minedNodes.Rocks >= rocksThen + 2) && Player.health >= ShaftSafeHealth)
                {
                    int levelBefore = mine.mineLevel;
                    foreach (var s in JumpDownShaft(mine, shaft.Value, o))
                        yield return s;
                    if (o.Ok && Loc is MineShaft below && below.mineLevel > levelBefore)
                    {
                        descended += below.mineLevel - levelBefore;
                        shaftJumps++;
                        lastHp = Player.health; // 摔的那几点血不算挨打
                        continue;
                    }
                    skippedShafts[shaft.Value] = minedNodes.Rocks; // 走不过去或者没跳成：先照常敲石头，敲掉两块以后再试
                    o.Succeed();
                    if (Loc != mine)
                        continue;
                }

                // 1) 清理靠近的怪（先保命再干活）。上一轮挨了打、身边却没有怪：多半是骷髅扔骨头、暗影萨满放法术这种远程怪，
                //    只打 6 格以内永远碰不到它，会一直挨打、吃东西、敲不成石头（夏21 第 75、83 层就这样耗了几分钟），所以扩大范围去找它
                bool sniped = Player.health < lastHp && NearestThreat(2) == null;
                // 附近有萨满时多给点时间：它会回血、会逃，20 秒常常打不死
                bool healerNear = LiveMonsters(mine).Any(m => IsHealer(m) && Util.Manhattan(m.TilePoint, Player.TilePoint) <= 10);
                int swingsBefore = st.Swings;
                foreach (var s in FightInner(sniped || healerNear ? 16 : 6, 0.4f, sniped || healerNear ? 45 : 20, st, o))
                    yield return s;
                // 扩大范围也一下没打着（远程怪隔着墙/岩浆，走不过去）：这一轮敲石头时不再因为远处的伤害停手，否则会原地打转
                bool shooterUnreachable = sniped && st.Swings == swingsBefore;
                lastHp = Player.health;
                if (!o.Ok && o.Message.StartsWith("!"))
                {
                    stop = o.Message.Substring(1);
                    break;
                }
                o.Succeed();

                // 2) 有宝箱先开（每 10 层的奖励箱、宝藏房），再找楼梯
                if (MineChests(mine).Where(c => !skippedChests.Contains(c.tile)).Select(c => (Point?)c.tile).FirstOrDefault() is Point chestTile)
                {
                    var took = new List<string>();
                    string chestNote = null;
                    foreach (var s in OpenMineChest(mine, chestTile, took, n => chestNote = n))
                        yield return s;
                    loot.AddRange(took);
                    // 点了也没拿到东西（打不开）：记下来，不再反复试
                    if (chestNote == null && took.Count == 0 && MineChests(mine).Any(c => c.tile == chestTile))
                        chestNote = $"({chestTile.X},{chestTile.Y}) 的宝箱没打开，可以试试 interact";
                    if (chestNote != null)
                    {
                        skippedChests.Add(chestTile);
                        chestNotes.Add(chestNote);
                    }
                    continue;
                }

                var me = Player.TilePoint;
                var ladder = NearestMineTile(mine, 173);
                var ladderRoute = ladder is Point ladderTile
                    ? Pathfinder.FindPath(mine, me, Pathfinder.AdjacentStandTiles(mine, ladderTile), 8000) : null;
                bool blockedShaft = shaft is Point shaftTile && skippedShafts.ContainsKey(shaftTile) && Player.health >= ShaftSafeHealth;
                bool surrounded = !Pathfinder.Dirs.Any(d => Pathfinder.IsPassable(mine, me.X + d.X, me.Y + d.Y));
                bool opening = blockedShaft || surrounded || (ladder.HasValue && ladderRoute == null);
                bool Available(Point pt) => !skippedStones.Contains((mine.mineLevel, pt));

                // 通路没被堵住才顺手捡地上的东西、劈木箱；赶楼层时直接找楼梯。
                if (!opening && miningGoal != MineGoal.Descent && NearestThreat(3) == null
                    && Player.health >= Player.maxHealth * 0.4f && !Player.isInventoryFull()
                    && MineExtras(mine, skippedExtras, reach).FirstOrDefault() is var extra && extra.obj != null)
                {
                    bool box = extra.obj is BreakableContainer;
                    string extraName = extra.obj.DisplayName;
                    foreach (var s in GoAdjacent(extra.p, o))
                        yield return s;
                    if (o.Ok)
                    {
                        foreach (var s in ClearObstacle(extra.p, o, maxHits: 12))
                            yield return s;
                    }
                    bool gone = !mine.objects.ContainsKey(new Vector2(extra.p.X, extra.p.Y));
                    if (!gone)
                        skippedExtras.Add(extra.p);
                    else if (box)
                    {
                        containers++;
                        var drops = new List<string>();
                        foreach (var s in CollectDebris(new Rectangle(extra.p.X - 2, extra.p.Y - 2, 5, 5), drops, 30))
                            yield return s;
                    }
                    else
                        grabbed.Add(extraName);
                    o.Succeed();
                    continue;
                }

                // 有楼梯且路通，体力有余就按目标顺手敲矿。路堵了直接去开路。
                var nearLadder = !opening && ladderRoute != null && Player.Stamina >= Player.MaxStamina * 0.2f
                    && NearestThreat(3) == null && !Player.isInventoryFull()
                    ? MineNodeNearExit(mine, miningGoal, ladderRoute, ScoredMineStones(mine, miningGoal).Where(t => Available(t.pt))) : null;
                if (ladder.HasValue && nearLadder == null)
                {
                    foreach (var s in GoAdjacent(ladder.Value, o))
                        yield return s;
                    if (o.Ok)
                    {
                        mine.checkAction(new xLocation(ladder.Value.X, ladder.Value.Y), Game1.viewport, Player);
                        for (int t = 0; t < 300 && Loc == mine; t++)
                            yield return null;
                        foreach (var s in WaitTicks(30))
                            yield return s;
                        if (Loc != mine)
                        {
                            descended++;
                            continue;
                        }
                    }
                    o.Succeed();
                }

                // 没有现成通路要走时，炸一片石头省体力、找楼梯；骷髅洞穴里留 2 颗给木乃伊。
                if (!opening && !ladder.HasValue && blastBombs < bombBudget && NearestThreat(4) == null
                    && Player.health >= Player.maxHealth * 0.5f && !Player.isInventoryFull())
                {
                    var bombs = BombsInBag();
                    int reserve = mine.mineLevel > 120 ? 2 : 0;
                    int have = bombs.Sum(b => Player.Items[b.idx]?.Stack ?? 0);
                    if (bombs.Count > 0 && have > reserve)
                    {
                        bool tired = Player.Stamina < Player.MaxStamina * 0.3f;
                        var spot = BestBlastSpot(mine, bombs, 8, tired ? 3 : 8, miningGoal, skippedBlasts);
                        if (spot.Found)
                        {
                            foreach (var s in BlastStones(spot, o, blastNotes, (b, n) => { blastStones += b; blastOres += n; }))
                                yield return s;
                            if (o.Ok)
                                blastBombs++;
                            else
                                skippedBlasts.Add(spot.Tile);
                            o.Succeed();
                            lastHp = Player.health;
                            continue;
                        }
                    }
                }

                // 仍然敲石头找楼梯。挡路时先敲身边或通往出口的石头；平时用评分抵掉一部分距离。
                Point? digToward = blockedShaft ? shaft : ladder;
                me = Player.TilePoint;
                var stones = ScoredMineStones(mine, miningGoal).Where(t => Available(t.pt)).ToList();
                var targets = nearLadder is { } near ? new[] { near.tile } : stones
                    .OrderBy(t => Util.Manhattan(t.pt, me) + (surrounded ? 0 : digToward is Point exit
                        ? Util.Manhattan(t.pt, exit) : -MineNodeDetour(t.score, miningGoal)))
                    .ThenBy(t => opening ? Util.Manhattan(t.pt, me) : -t.score)
                    .Take(6).Select(t => t.pt);
                bool hit = false, skippedStone = false;
                foreach (var stone in targets)
                {
                    foreach (var s in GoAdjacent(stone, o, nearLadder?.stand))
                        yield return s;
                    if (!o.Ok)
                    {
                        o.Succeed();
                        if (!Blocked(out _) && Loc == mine)
                        {
                            skippedStones.Add((mine.mineLevel, stone));
                            skippedStone = true;
                        }
                        continue;
                    }
                    int hpBefore = Player.health;
                    for (int k = 0; k < 8 && mine.objects.ContainsKey(new Vector2(stone.X, stone.Y)); k++)
                    {
                        // 被打了（包括远处飞来的骨头、法术）或者有怪贴上来：先停手，回到循环开头去打它。
                        // 只有刚才确认打不到那只远程怪时，远处的伤害才不打断（生命低于四成照样停下吃东西）
                        bool hurt = Player.health < hpBefore && !shooterUnreachable;
                        if (hurt || NearestThreat(2) != null || Player.health < Player.maxHealth * 0.4f)
                            break;
                        foreach (var s in UseToolOnce(pick, stone, o))
                            yield return s;
                        if (!o.Ok)
                            break;
                    }
                    if (mine.objects.TryGetValue(new Vector2(stone.X, stone.Y), out var remaining) && remaining.IsBreakableStone()
                        && o.Ok && Loc == mine && Player.health >= hpBefore && NearestThreat(2) == null
                        && Player.health >= Player.maxHealth * 0.4f)
                        skippedStones.Add((mine.mineLevel, stone)); // 敲了 8 下还在，先试别处
                    hit = true;
                    break;
                }
                if (!o.Ok)
                {
                    stop = o.Message;
                    break;
                }
                if (hit)
                    continue;
                // 这几块够不着，但这层还有别的石头没试过：下一轮试更远的
                if (skippedStone && stones.Any(t => Available(t.pt)))
                    continue;
                // 楼梯边的矿够不着：回到开头重新挑，挑不到就下楼
                if (nearLadder.HasValue)
                    continue;

                // 4) 小石头都够不着：多半是矿石巨石（2x2）挡了路，任何镐子都能敲碎（游戏里 752/754/756/758 只要求是镐子）
                bool smashed = false;
                foreach (var (corner, clump) in MineBoulders(mine).Take(3))
                {
                    if (skippedBoulders.Contains(corner))
                        continue;
                    var target = BoulderTiles(corner).OrderBy(p => Util.Manhattan(p, Player.TilePoint)).First();
                    foreach (var s in GoAdjacentToAny(BoulderTiles(corner), o))
                        yield return s;
                    if (!o.Ok)
                    {
                        o.Succeed();
                        skippedBoulders.Add(corner);
                        continue;
                    }
                    target = BoulderTiles(corner).OrderBy(p => Util.Manhattan(p, Player.TilePoint)).First();
                    for (int k = 0; k < 25 && mine.resourceClumps.Contains(clump); k++)
                    {
                        if (NearestThreat(2) != null || Player.health < Player.maxHealth * 0.4f || Player.Stamina < 15)
                            break;
                        foreach (var s in UseToolOnce(pick, target, o))
                            yield return s;
                        if (!o.Ok)
                            break;
                    }
                    o.Succeed();
                    if (!mine.resourceClumps.Contains(clump))
                    {
                        boulders++;
                        smashed = true;
                    }
                    else if (NearestThreat(2) == null && Player.health >= Player.maxHealth * 0.4f && Player.Stamina >= 15)
                        skippedBoulders.Add(corner); // 敲了 25 下还没碎：别卡在这块上
                    break;
                }
                if (smashed || (MineBoulders(mine).Any(b => !skippedBoulders.Contains(b.corner)) && Player.Stamina >= 15))
                    continue;

                // 5) 没石头可敲：有些层要把怪全打完楼梯才出现
                if (LiveMonsters(mine).Any())
                {
                    foreach (var s in FightInner(60, 0.4f, 60, st, o))
                        yield return s;
                    if (!o.Ok && o.Message.StartsWith("!"))
                    {
                        stop = o.Message.Substring(1);
                        break;
                    }
                    o.Succeed();
                    if (NearestMineTile(mine, 173).HasValue)
                        continue;
                }
                stop = NearestMineTile(mine, 174).HasValue
                    ? "这一层只有竖井，没有楼梯，也没有能走到的石头了。现在生命 " + Player.health + "，不到 " + ShaftSafeHealth + "，没敢跳（每降一层掉 3 点血，一次最多掉 45 点）：先 eat 回血再 explore_mine，或者 travel_to 出去"
                    : "这一层找不到楼梯，也没有能走到的石头了（可以试试 use_tool 敲别处，或者 travel_to 出去）";
                break;
            }

            int now = Loc is MineShaft m2 ? m2.mineLevel : 0;
            string nodeSummary = minedNodes.Summary(miningGoal);
            string msg = $"从{MineLevelName(startLvl)}下到{(now > 0 ? MineLevelName(now) : "第 ? 层")}（下了 {descended} 层{(shaftJumps > 0 ? $"，其中跳了 {shaftJumps} 次竖井" : "")}），敲了 {minedNodes.Rocks} 块石头，打倒 {st.Kills} 只怪，吃了 {st.Eaten} 次东西；生命 {Player.health}/{Player.maxHealth}，体力 {(int)Player.Stamina}";
            msg += nodeSummary;
            if (startNote != null)
                msg += "。" + startNote;
            if (MummyNote(st, 12) is string mummyNote)
                msg += "。" + mummyNote;
            if (blastBombs > 0)
                msg += $"；用了 {blastBombs} 颗炸弹炸石头，炸掉 {blastStones} 块（其中矿石/宝石节点 {blastOres} 个），掉的东西都捡了";
            if (blastNotes.Count > 0)
                msg += "；" + string.Join("；", blastNotes.Distinct());
            if (boulders > 0)
                msg += $"；敲碎了 {boulders} 块挡路的矿石巨石";
            if (grabbed.Count > 0)
                msg += "；顺手捡了：" + string.Join("、", grabbed.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key));
            if (containers > 0)
                msg += $"；劈开了 {containers} 个木箱/木桶";
            if (loot.Count > 0)
                msg += "；开宝箱拿到：" + string.Join("、", loot);
            if (chestNotes.Count > 0)
                msg += "；" + string.Join("；", chestNotes);
            if (st.Notes.Count > 0)
                msg += "；" + string.Join("；", st.Notes.Distinct());
            if (stop != null)
                msg += "；停下原因：" + stop;
            if (clutter != null)
                msg += "。" + clutter;
            yield return descended > 0 || minedNodes.Rocks > 0 || st.Kills > 0 || loot.Count > 0 ? ActionResult.Success(msg) : ActionResult.Fail(msg);
        }
    }
}
