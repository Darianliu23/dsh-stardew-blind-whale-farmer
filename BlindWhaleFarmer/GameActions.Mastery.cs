using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Constants;
using StardewValley.Menus;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 精通（1.6）─────────────────────────
        // 五项技能都 10 级后，森林里精通洞穴的门才开；之后继续攒的经验算精通经验，
        // 每涨一级精通（1 万/2.5 万/4.5 万/7 万/10 万）就能去洞里一个技能的台子上领一份奖励，每个技能只能领一次。

        /// <summary>Item = 直接给的东西；Recipes = 学会的制作配方；Perk = 1_6_Strings 里精通效果说明的键。和 MasteryTrackerMenu 构造函数里一致。</summary>
        private sealed record MasterySkill(int Which, string Key, string Name, string Item, string[] Recipes, string Perk)
        {
            public string Rewards()
            {
                var parts = new List<string>();
                if (Item != null)
                    parts.Add(ItemRegistry.GetDataOrErrorItem(Item).DisplayName);
                foreach (string r in Recipes)
                    parts.Add(ItemRegistry.GetDataOrErrorItem(r).DisplayName + "配方");
                string perk = Game1.content.LoadString("Strings\\1_6_Strings:" + Perk).Replace("\n", " ").Trim().TrimEnd('。', '.');
                if (perk.Length > 0)
                    parts.Add("效果：" + perk);
                return string.Join("、", parts);
            }
        }

        private static readonly MasterySkill[] MasterySkills =
        {
            new(0, "farming", "耕种", "(W)66", new[] { "(BC)StatueOfBlessings" }, "Farming_Mastery"),
            new(3, "mining", "采矿", null, new[] { "(BC)StatueOfTheDwarfKing", "(BC)HeavyFurnace" }, "Mining_Mastery"),
            new(1, "fishing", "钓鱼", "(T)AdvancedIridiumRod", new[] { "(O)ChallengeBait" }, "Fishing_Mastery"),
            new(2, "foraging", "采集", null, new[] { "(O)MysticTreeSeed", "(O)TreasureTotem" }, "Foraging_Mastery"),
            new(4, "combat", "战斗", null, new[] { "(BC)Anvil", "(BC)MiniForge" }, "Trinkets_Description"),
        };

        private static bool AllSkillsMaxed =>
            Player.farmingLevel.Value >= 10 && Player.miningLevel.Value >= 10 && Player.fishingLevel.Value >= 10
            && Player.foragingLevel.Value >= 10 && Player.combatLevel.Value >= 10;

        private static bool MasteryClaimed(MasterySkill s) => Player.stats.Get(StatKeys.Mastery(s.Which)) > 0;

        /// <summary>还能领几份精通奖励（精通等级 - 已经领过的）。</summary>
        private static int MasteryPointsLeft() =>
            Math.Max(0, MasteryTrackerMenu.getCurrentMasteryLevel() - (int)Game1.stats.Get("masteryLevelsSpent"));

        private static string MasteryProgress()
        {
            int level = MasteryTrackerMenu.getCurrentMasteryLevel();
            int exp = (int)Game1.stats.Get("MasteryExp");
            return level >= 5
                ? "精通 5/5 级（满了）"
                : $"精通 {level}/5 级，精通经验 {exp}/{MasteryTrackerMenu.getMasteryExpNeededForLevel(level + 1)}（耕种经验算一半）";
        }

        /// <summary>状态里的提示：有精通奖励没领。没事返回 null。</summary>
        public static string MasteryHint()
        {
            if (!AllSkillsMaxed)
                return null;
            int left = MasteryPointsLeft();
            if (left <= 0 || MasterySkills.All(MasteryClaimed))
                return null;
            return $"精通：有 {left} 份精通奖励没领（{MasteryProgress()}）。用 mastery 查看各技能的奖励，mastery skill=技能名 去森林的精通洞穴领";
        }

        /// <summary>带这个 Action 的格子里，旁边有地方站的那一格（门、台子常占上下两格，最上面那格四周都是墙）。</summary>
        private static Point? ReachableActionTile(GameLocation loc, string action)
        {
            Point? any = null;
            int w = Pathfinder.MapWidth(loc), h = Pathfinder.MapHeight(loc);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    if (loc.doesTileHaveProperty(x, y, "Action", "Buildings")?.Trim() == action)
                    {
                        var p = new Point(x, y);
                        if (Pathfinder.AdjacentStandTiles(loc, p).Count > 0)
                            return p;
                        any ??= p;
                    }
            return any;
        }

        public IEnumerable<object> Mastery(string skillSpec)
        {
            if (!AllSkillsMaxed)
            {
                yield return ActionResult.Fail("精通要五项技能都到 10 级才开（森林里精通洞穴的门才打得开）。现在："
                    + $"耕种 {Player.farmingLevel.Value}、采矿 {Player.miningLevel.Value}、钓鱼 {Player.fishingLevel.Value}、采集 {Player.foragingLevel.Value}、战斗 {Player.combatLevel.Value}");
                yield break;
            }
            int left = MasteryPointsLeft();
            string Overview() => MasteryProgress() + $"，还能领 {left} 份。各技能奖励：\n"
                + string.Join("\n", MasterySkills.Select(s => $"  {s.Name}（{s.Key}）：{s.Rewards()}" + (MasteryClaimed(s) ? "【已领】" : "")));
            if (string.IsNullOrWhiteSpace(skillSpec))
            {
                yield return ActionResult.Success(Overview() + (left > 0 ? "\n用 mastery skill=技能名 去精通洞穴领一份（每个技能只能领一次，先挑最有用的）" : "\n继续攒经验涨精通等级才能再领"));
                yield break;
            }
            string spec = skillSpec.Trim();
            var skill = MasterySkills.FirstOrDefault(s => s.Key.Equals(spec, StringComparison.OrdinalIgnoreCase) || spec.Contains(s.Name));
            if (skill == null)
            {
                yield return ActionResult.Fail($"没有“{skillSpec}”这个技能。可选：{string.Join("、", MasterySkills.Select(s => $"{s.Name}({s.Key})"))}");
                yield break;
            }
            if (MasteryClaimed(skill))
            {
                yield return ActionResult.Fail($"{skill.Name}精通已经领过了。" + Overview());
                yield break;
            }
            if (left <= 0)
            {
                yield return ActionResult.Fail("现在没有精通点可用：" + MasteryProgress() + "。继续干活攒经验，涨一级精通就能领一份");
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }

            var o = new Outcome();
            // 1) 进精通洞穴：森林里那扇门（地图 Action MasteryRoom）
            if (!Loc.Name.Equals("MasteryCave", StringComparison.OrdinalIgnoreCase))
            {
                if (!Loc.Name.Equals("Forest", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in TravelInner("Forest", o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail("去森林失败：" + o.Message);
                        yield break;
                    }
                }
                if (ReachableActionTile(Loc, "MasteryRoom") is not Point door)
                {
                    yield return ActionResult.Fail("在森林里没找到精通洞穴的门");
                    yield break;
                }
                foreach (var s in GoAdjacent(door, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"走不到精通洞穴的门 ({door.X},{door.Y})：{o.Message}");
                    yield break;
                }
                Loc.checkAction(new xLocation(door.X, door.Y), Game1.viewport, Player);
                for (int i = 0; i < 300 && !Loc.Name.Equals("MasteryCave", StringComparison.OrdinalIgnoreCase); i++)
                    yield return null;
                foreach (var s in WaitSceneSettled())
                    yield return s;
                if (!Loc.Name.Equals("MasteryCave", StringComparison.OrdinalIgnoreCase))
                {
                    if (Game1.activeClickableMenu is DialogueBox db)
                    {
                        string msg = db.getCurrentString();
                        Game1.activeClickableMenu = null;
                        yield return ActionResult.Fail("进不去精通洞穴：" + msg);
                    }
                    else
                        yield return ActionResult.Fail("进不去精通洞穴（门没反应）");
                    yield break;
                }
            }

            // 2) 走到这个技能的台子，打开领奖界面，按“领取”（MasteryTrackerMenu.claimReward）
            string action = "MasteryCave_" + char.ToUpperInvariant(skill.Key[0]) + skill.Key.Substring(1);
            if (ReachableActionTile(Loc, action) is not Point pedestal)
            {
                yield return ActionResult.Fail($"洞里没找到{skill.Name}的台子");
                yield break;
            }
            foreach (var s in GoAdjacent(pedestal, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail($"走不到{skill.Name}的台子 ({pedestal.X},{pedestal.Y})：{o.Message}");
                yield break;
            }
            int spentBefore = (int)Game1.stats.Get("masteryLevelsSpent");
            Loc.checkAction(new xLocation(pedestal.X, pedestal.Y), Game1.viewport, Player);
            foreach (var s in WaitTicks(15))
                yield return s;
            if (Game1.activeClickableMenu is not MasteryTrackerMenu menu)
            {
                yield return ActionResult.Fail($"{skill.Name}的台子没打开领奖界面");
                yield break;
            }
            reflection.GetMethod(menu, "claimReward").Invoke();
            foreach (var s in WaitTicks(30))
                yield return s;
            if (Game1.activeClickableMenu is MasteryTrackerMenu)
                Game1.activeClickableMenu.exitThisMenu(false);
            foreach (var s in WaitTicks(10))
                yield return s;
            if (!MasteryClaimed(skill) || (int)Game1.stats.Get("masteryLevelsSpent") <= spentBefore)
            {
                yield return ActionResult.Fail($"{skill.Name}精通没领成（按了领取但没生效）");
                yield break;
            }
            bool fullBag = skill.Item != null && !Player.Items.Any(i => i?.QualifiedItemId == skill.Item);
            left = MasteryPointsLeft();
            yield return ActionResult.Success($"领了{skill.Name}精通：{skill.Rewards()}。配方已经学会，用 craft 做"
                + (fullBag ? "；背包满了，奖励的工具掉在脚边，走一步捡起来" : "")
                + (left > 0 ? $"。还能再领 {left} 份" : "。精通点用完了，" + MasteryProgress())
                + "。出洞用 travel_to 回别的地方");
        }
    }
}
