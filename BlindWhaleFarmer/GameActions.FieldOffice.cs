using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 姜岛考察站 ─────────────────────────
        // 姜岛北边的考察站（IslandFieldOffice）里，蜗牛教授收化石：11 个位置，拼齐一组给金核桃和奖励
        // （中间骨架 6 块、蛇 3 块、蝙蝠、青蛙），奖励要另外领。墙上还有两个调查：紫花数量 22、紫海星数量 18。
        // 全部化石交齐、两个调查答对以后，会有完结剧情。

        private static readonly (string id, string name, int[] slots)[] FieldOfficeBones =
        {
            ("(O)820", "化石头骨", new[] { 5 }),
            ("(O)821", "化石脊椎", new[] { 4 }),
            ("(O)822", "化石尾巴", new[] { 3 }),
            ("(O)823", "化石腿骨", new[] { 0, 2 }),
            ("(O)824", "化石肋骨", new[] { 1 }),
            ("(O)825", "蛇的头骨", new[] { 8 }),
            ("(O)826", "蛇的脊椎", new[] { 7, 6 }),
            ("(O)827", "木乃伊蝙蝠", new[] { 9 }),
            ("(O)828", "木乃伊青蛙", new[] { 10 }),
        };

        private static IslandFieldOffice FieldOffice() => Game1.getLocationFromName("IslandFieldOffice") as IslandFieldOffice;

        private static string FieldOfficeStatus(IslandFieldOffice office)
        {
            var missing = new List<string>();
            foreach (var b in FieldOfficeBones)
            {
                int need = b.slots.Count(s => !office.piecesDonated[s]);
                if (need > 0)
                    missing.Add(need > 1 ? $"{ItemRegistry.GetData(b.id)?.DisplayName ?? b.name}x{need}" : ItemRegistry.GetData(b.id)?.DisplayName ?? b.name);
            }
            int done = office.piecesDonated.Count(x => x);
            string text = $"考察站化石 {done}/11" + (missing.Count > 0 ? "，还缺：" + string.Join("、", missing) : "，全交齐了");
            var surveys = new List<string>();
            if (!office.plantsRestoredLeft.Value) surveys.Add("紫花");
            if (!office.plantsRestoredRight.Value) surveys.Add("紫海星");
            text += surveys.Count > 0 ? $"；调查还没答：{string.Join("、", surveys)}（action=survey）" : "；两个调查都答对了";
            if (office.uncollectedRewards.Count > 0)
                text += $"；有 {office.uncollectedRewards.Count} 份奖励没领";
            if (Game1.player.hasOrWillReceiveMail("fieldOfficeFinale"))
                text += "；考察站已经全部完成";
            return text;
        }

        /// <summary>姜岛考察站：交化石（donate，默认）、看进度（status）、回答墙上的调查（survey）。交完会顺手领奖励。</summary>
        public IEnumerable<object> FieldOfficeAction(string action)
        {
            action = (action ?? "donate").Trim().ToLowerInvariant();
            var office = FieldOffice();
            if (office == null || !Game1.player.hasOrWillReceiveMail("islandNorthCaveOpened"))
            {
                yield return ActionResult.Fail("考察站还没开：要先去姜岛北边（IslandNorth），从洞里把蜗牛教授救出来（洞口要先开），他才会去考察站收化石");
                yield break;
            }
            if (action == "status")
            {
                yield return ActionResult.Success(FieldOfficeStatus(office));
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            bool hasBones = FieldOfficeBones.Any(b => b.slots.Any(s => !office.piecesDonated[s]) && Player.Items.Any(i => i?.QualifiedItemId == b.id));
            if (action == "donate" && !hasBones && office.uncollectedRewards.Count == 0)
            {
                yield return ActionResult.Fail("背包里没有考察站还缺的化石。" + FieldOfficeStatus(office)
                    + "。化石在姜岛挖远古斑点、打火山怪、钓鱼宝箱里出");
                yield break;
            }
            if (action == "survey" && office.plantsRestoredLeft.Value && office.plantsRestoredRight.Value)
            {
                yield return ActionResult.Fail("两个调查都已经答对了。" + FieldOfficeStatus(office));
                yield break;
            }
            if (action == "survey" && office.hasFailedSurveyToday.Value)
            {
                yield return ActionResult.Fail("今天调查答错过一次，明天才能再答");
                yield break;
            }

            var o = new Outcome();
            if (Loc != office)
            {
                foreach (var s in TravelInner("IslandFieldOffice", o))
                    yield return s;
                if (Loc != office)
                {
                    yield return ActionResult.Fail("去考察站失败：" + o.Message);
                    yield break;
                }
            }
            foreach (var s in WaitPlayerFree(600))
                yield return s;
            var desk = FindTileAction(office, action == "survey" ? "FieldOfficeSurvey" : "FieldOfficeDesk") ?? office.getSafariGuy()?.TilePoint ?? new Point(8, 6);
            foreach (var s in GoAdjacent(desk, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail((action == "survey" ? "走不到调查板旁边：" : "走不到蜗牛教授的桌子旁边：") + o.Message);
                yield break;
            }

            var log = new List<string>();
            if (action == "survey")
            {
                // 墙上的调查：紫花 22 朵、紫海星 18 只（和对着调查板选答案一样）
                if (!office.plantsRestoredLeft.Value)
                {
                    office.plantsRestoredLeft.Value = true;
                    log.Add("紫花调查答 22，答对了");
                }
                else if (!office.plantsRestoredRight.Value)
                {
                    office.plantsRestoredRight.Value = true;
                    log.Add("紫海星调查答 18，答对了");
                }
                Game1.playSound("newArtifact");
            }
            else
            {
                bool reward = false;
                foreach (var b in FieldOfficeBones)
                {
                    foreach (int slot in b.slots)
                    {
                        if (office.piecesDonated[slot])
                            continue;
                        var item = Player.Items.FirstOrDefault(i => i?.QualifiedItemId == b.id);
                        if (item == null)
                            break;
                        log.Add(item.DisplayName);
                        if (item.Stack <= 1)
                            Player.removeItemFromInventory(item);
                        else
                            item.Stack--;
                        reward |= office.donatePiece(slot);
                    }
                }
                if (log.Count > 0)
                {
                    Game1.playSound("newArtifact");
                    log[0] = "交了化石：" + log[0];
                    if (reward)
                        log.Add("拼齐了一组，蜗牛教授给了奖励");
                }
            }

            // 有没领的奖励就领走（背包放不下的留着下次领）
            var got = new List<string>();
            foreach (var item in office.uncollectedRewards.ToList())
            {
                string name = item.DisplayName;
                int n = item.Stack;
                if (item.QualifiedItemId == "(O)73")
                {
                    // 金核桃不占格子，和游戏的 foundWalnut 一样记账（但不弹“获得了金色核桃”的框，免得卡住下一个动作）
                    if (Game1.netWorldState.Value.GoldenWalnutsFound < 130)
                    {
                        Game1.netWorldState.Value.GoldenWalnuts += n;
                        Game1.netWorldState.Value.GoldenWalnutsFound += n;
                    }
                    Game1.playSound("dwop");
                    office.uncollectedRewards.Remove(item);
                    got.Add($"{name}x{n}");
                    continue;
                }
                if (Player.addItemToInventoryBool(item))
                {
                    office.uncollectedRewards.Remove(item);
                    got.Add($"{name}x{n}");
                }
            }
            if (got.Count > 0)
                log.Add("领了奖励：" + string.Join("、", got));
            if (office.uncollectedRewards.Count > 0)
                log.Add($"背包满了，还有 {office.uncollectedRewards.Count} 份奖励没领，腾出地方再来");

            if (!Game1.player.hasOrWillReceiveMail("fieldOfficeFinale") && office.isRangeAllTrue(0, 11)
                && office.plantsRestoredLeft.Value && office.plantsRestoredRight.Value)
            {
                office.triggerFinaleCutscene();
                log.Add("全部完成，蜗牛教授的完结剧情要开始了");
            }
            yield return ActionResult.Success(string.Join("，", log) + "。" + FieldOfficeStatus(office));
        }
    }
}
