using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.GameData.Objects;
using StardewValley.Locations;
using StardewValley.TokenizableStrings;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 完美度、怪物讨伐目标 ─────────────────────────
        // 算法照抄 Utility.percentGameComplete 和它调的那些函数，另外把每一项还缺什么列出来（游戏里的追踪器只给百分比）。

        private static string Names(List<string> list, int max = 25) =>
            list.Count == 0 ? "无" : string.Join("、", list.Take(max)) + (list.Count > max ? $" 等 {list.Count} 项" : "");

        public IEnumerable<object> Perfection(string detail)
        {
            var who = Player;
            string d = detail?.Trim().ToLowerInvariant() ?? "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"完美度 {Utility.percentGameComplete() * 100f:0.#}%（满 100% 后睡一晚，第二天去铁路西北角上山顶看结局）");

            // 出货
            var notShipped = new List<string>();
            int shipTotal = 0;
            foreach (var data in ItemRegistry.GetObjectTypeDefinition().GetAllData())
                if (data.Category != -7 && data.Category != -2 && StardewValley.Object.isPotentialBasicShipped(data.ItemId, data.Category, data.ObjectType))
                {
                    shipTotal++;
                    if (!who.basicShipped.ContainsKey(data.ItemId))
                        notShipped.Add(data.DisplayName);
                }
            sb.AppendLine($"- 出货（15%）：{shipTotal - notShipped.Count}/{shipTotal}" + (d == "ship" ? "，没出过：" + Names(notShipped, 200) : notShipped.Count > 0 ? "，没出过：" + Names(notShipped, 12) : ""));

            // 方尖碑、黄金时钟
            var obelisks = new[] { ("Earth Obelisk", "山地"), ("Water Obelisk", "海滩"), ("Desert Obelisk", "沙漠"), ("Island Obelisk", "姜岛") };
            var missingOb = obelisks.Where(o => !Game1.IsBuildingConstructed(o.Item1)).Select(o => o.Item2 + "方尖碑").ToList();
            sb.AppendLine($"- 方尖碑（4%）：{4 - missingOb.Count}/4" + (missingOb.Count > 0 ? "，缺 " + string.Join("、", missingOb) + "（build 找法师建）" : ""));
            sb.AppendLine("- 黄金时钟（10%）：" + (Game1.IsBuildingConstructed("Gold Clock") ? "已建" : "没建（build Gold Clock 找法师建，1000 万金）"));

            // 怪物讨伐
            bool slayerDone = who.hasCompletedAllMonsterSlayerQuests.Value || AdventureGuild.areAllMonsterSlayerQuestsComplete();
            sb.AppendLine("- 怪物讨伐（10%）：" + (slayerDone ? "全部完成" : "还没完成：" + string.Join("；", SlayerGoals().Where(g => !g.done).Select(g => $"{g.name} {g.kills}/{g.count}"))));

            // 好感
            var friendsLeft = new List<string>();
            int friendTotal = 0;
            foreach (var (key, data) in Game1.characterData)
            {
                if (!data.PerfectionScore || GameStateQuery.IsImmutablyFalse(data.CanSocialize))
                    continue;
                friendTotal++;
                int need = (data.CanBeRomanced ? 8 : 10) * 250;
                int have = who.friendshipData.TryGetValue(key, out var f) && f != null ? f.Points : 0;
                if (have < need)
                    friendsLeft.Add($"{TokenParser.ParseText(data.DisplayName) ?? key} {have / 250}/{need / 250}心" + (who.friendshipData.ContainsKey(key) ? "" : "(没见过)"));
            }
            sb.AppendLine($"- 好感（11%）：{friendTotal - friendsLeft.Count}/{friendTotal} 人满心" + (friendsLeft.Count > 0 ? "，还差：" + Names(friendsLeft, d == "friends" ? 200 : 15) : ""));

            // 技能
            string[] skillNames = { "耕种", "钓鱼", "采集", "采矿", "战斗" };
            var lowSkills = Enumerable.Range(0, 5).Where(i => who.GetUnmodifiedSkillLevel(i) < 10).Select(i => $"{skillNames[i]} {who.GetUnmodifiedSkillLevel(i)} 级").ToList();
            sb.AppendLine("- 五项技能 10 级（5%）：" + (lowSkills.Count == 0 ? "都满了" : "还差 " + string.Join("、", lowSkills)));

            // 星之果实
            var drops = new (string mail, string where)[]
            {
                ("CF_Fair", "秋 16 日展览会用 2000 星星币换"),
                ("CF_Fish", "钓齐鱼类图鉴后威利来信送"),
                ("CF_Mines", "矿井 100 层的宝箱"),
                ("CF_Sewer", "下水道科罗布斯那里 20000 金买（open_shop）"),
                ("museumComplete", "博物馆捐齐 95 件"),
                ("CF_Spouse", "配偶或室友好感很高时送"),
                ("CF_Statue", "秘密森林的雕像前拿着宝石甜莓 interact"),
            };
            bool allDrops = Utility.foundAllStardrops(who);
            var missDrops = drops.Where(s => !(who.hasOrWillReceiveMail(s.mail) || (s.mail == "CF_Mines" && who.chestConsumedMineLevels.GetValueOrDefault(100, false)))).Select(s => s.where).ToList();
            sb.AppendLine($"- 星之果实（10%）：{(allDrops ? 7 : 7 - missDrops.Count)}/7" + (!allDrops && missDrops.Count > 0 ? "，还差：" + string.Join("；", missDrops) : ""));

            // 烹饪
            var notLearned = new List<string>();
            var notCooked = new List<string>();
            foreach (var (key, value) in CraftingRecipe.cookingRecipes)
            {
                string name = new CraftingRecipe(key, true).DisplayName;
                if (!who.cookingRecipes.ContainsKey(key))
                    notLearned.Add(name);
                else if (!who.recipesCooked.ContainsKey(ArgUtility.SplitBySpaceAndGet(ArgUtility.Get(value.Split('/'), 2), 0)))
                    notCooked.Add(name);
            }
            int cookTotal = CraftingRecipe.cookingRecipes.Count, cookMax = d == "cooking" ? 200 : 12;
            sb.AppendLine($"- 烹饪（10%）：{cookTotal - notLearned.Count - notCooked.Count}/{cookTotal}"
                + (notCooked.Count > 0 ? "；会做没做过：" + Names(notCooked, cookMax) : "")
                + (notLearned.Count > 0 ? "；还没学会：" + Names(notLearned, cookMax) : ""));

            // 制作
            var craftNotLearned = new List<string>();
            var craftNotMade = new List<string>();
            foreach (string key in CraftingRecipe.craftingRecipes.Keys)
            {
                if (key == "Wedding Ring")
                    continue;
                string name = new CraftingRecipe(key, false).DisplayName;
                if (!who.craftingRecipes.TryGetValue(key, out int made))
                    craftNotLearned.Add(name);
                else if (made <= 0)
                    craftNotMade.Add(name);
            }
            int craftTotal = CraftingRecipe.craftingRecipes.Count - 1, craftMax = d == "crafting" ? 200 : 12;
            sb.AppendLine($"- 制作（10%）：{craftTotal - craftNotLearned.Count - craftNotMade.Count}/{craftTotal}"
                + (craftNotMade.Count > 0 ? "；会做没做过：" + Names(craftNotMade, craftMax) : "")
                + (craftNotLearned.Count > 0 ? "；还没学会：" + Names(craftNotLearned, craftMax) : ""));

            // 钓鱼
            var notCaught = new List<string>();
            int fishTotal = 0;
            foreach (var data in ItemRegistry.GetObjectTypeDefinition().GetAllData())
                if (data.ObjectType == "Fish" && !(data.RawData is ObjectData { ExcludeFromFishingCollection: true }))
                {
                    fishTotal++;
                    if (!who.fishCaught.ContainsKey(data.QualifiedItemId))
                        notCaught.Add(data.DisplayName);
                }
            sb.AppendLine($"- 钓鱼（10%）：{fishTotal - notCaught.Count}/{fishTotal}" + (notCaught.Count > 0 ? "，没钓过：" + Names(notCaught, d == "fish" ? 200 : 15) : ""));

            // 金色核桃
            int walnuts = Game1.netWorldState.Value.GoldenWalnutsFound;
            sb.AppendLine($"- 金色核桃（5%）：{Math.Min(walnuts, 130)}/130");

            if (d is "" or "all")
                sb.Append("（某一项想看完整清单：detail 传 ship / friends / cooking / crafting / fish）");
            yield return ActionResult.Success(sb.ToString().TrimEnd());
        }

        private static List<(string id, string name, int kills, int count, bool done, bool collected)> SlayerGoals()
        {
            var list = new List<(string, string, int, int, bool, bool)>();
            foreach (var (id, goal) in DataLoader.MonsterSlayerQuests(Game1.content))
            {
                int kills = goal.Targets?.Sum(t => Game1.stats.getMonstersKilled(t)) ?? 0;
                list.Add((id, TokenParser.ParseText(goal.DisplayName), kills, goal.Count, AdventureGuild.IsComplete(goal), AdventureGuild.HasCollectedReward(Player, id)));
            }
            return list;
        }

        /// <summary>冒险家公会有完成了还没领的讨伐奖励时的提示；没有返回 null。</summary>
        public static string SlayerRewardHint()
        {
            if (!Player.mailReceived.Contains("guildMember"))
                return null;
            var ready = SlayerGoals().Where(g => g.done && !g.collected).Select(g => g.name).ToList();
            return ready.Count == 0 ? null
                : $"冒险家公会：讨伐目标 {string.Join("、", ready)} 已完成、奖励还没领，去 AdventureGuild 找 Gil（interact npc=Gil）领";
        }

        public IEnumerable<object> MonsterGoals()
        {
            var goals = SlayerGoals();
            var lines = goals.Select(g => $"- {g.name}：{Math.Min(g.kills, g.count)}/{g.count}" + (g.collected ? "（奖励已领）" : g.done ? "（完成了，奖励没领，找 Gil）" : ""));
            string head = goals.All(g => g.done) ? "怪物讨伐目标全部完成（完美度这项 10% 拿到了）" : $"怪物讨伐目标 {goals.Count(g => g.done)}/{goals.Count} 完成";
            yield return ActionResult.Success(head + "：\n" + string.Join("\n", lines)
                + "\n（完成后去冒险家公会 AdventureGuild 找 Gil 领奖励：interact npc=Gil）");
        }
    }
}
