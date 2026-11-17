using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 姜岛：修威利的船 ─────────────────────────

        /// <summary>修船的三样东西：邮件标记、要交的物品、数量、交在船坞哪一格（null = 售票机，按 Action BoatTicket 找）。和 BoatTunnel.checkAction 一致。</summary>
        private static readonly (string mail, string item, string name, int count, Point? tile)[] BoatParts =
        {
            ("willyBoatHull", "(O)709", "硬木", 200, new Point(6, 8)),
            ("willyBoatAnchor", "(O)337", "铱锭", 5, new Point(8, 10)),
            ("willyBoatTicketMachine", "(O)787", "电池组", 5, null),
        };

        /// <summary>修船还差什么（已经交了的不列）。</summary>
        private static List<string> BoatPartsMissing() =>
            BoatParts.Where(p => !Game1.MasterPlayer.hasOrWillReceiveMail(p.mail))
                .Select(p => $"{p.name} {p.count} 个（背包里有 {Player.Items.CountId(p.item)}）").ToList();

        /// <summary>姜岛相关的提示：收到威利的邀请但船没修好时说差什么；船修好了、在姜岛上时报金核桃数。</summary>
        public static string IslandHint()
        {
            if (!Game1.player.mailReceived.Contains("willyBackRoomInvitation"))
                return null;
            if (!Exits.BoatRepaired)
            {
                var missing = BoatPartsMissing();
                return missing.Count == 0
                    ? "威利的船修好了，明天就能坐船去姜岛（travel_to IslandSouth）"
                    : $"威利的船（鱼店后屋）还没修好，去姜岛要先 repair_boat。还差：{string.Join("、", missing)}";
            }
            if (Loc?.InIslandContext() == true)
                return $"金核桃：现有 {Game1.netWorldState.Value.GoldenWalnuts} 个，一共找到 {Game1.netWorldState.Value.GoldenWalnutsFound} 个（给鹦鹉解锁岛上的东西；攻略“姜岛”一节）";
            return null;
        }

        /// <summary>去威利鱼店后屋的船坞，把背包里够数的修船材料交上（硬木 200、铱锭 5、电池组 5），报告还差什么。</summary>
        public IEnumerable<object> RepairBoat()
        {
            if (!Game1.player.mailReceived.Contains("willyBackRoomInvitation"))
            {
                yield return ActionResult.Fail("还进不了威利的后屋：要先修好社区中心（或者走完 Joja 路线），威利会寄信邀请你");
                yield break;
            }
            if (Exits.BoatRepaired)
            {
                yield return ActionResult.Success("船已经修好了，直接 travel_to IslandSouth 坐船去姜岛（船票 1000g）");
                yield break;
            }
            var todo = BoatParts.Where(p => !Game1.MasterPlayer.hasOrWillReceiveMail(p.mail)).ToList();
            if (todo.Count == 0)
            {
                yield return ActionResult.Success("修船的材料都交齐了，船明天修好，到时候 travel_to IslandSouth");
                yield break;
            }
            var ready = todo.Where(p => Player.Items.ContainsId(p.item, p.count)).ToList();
            if (ready.Count == 0)
            {
                yield return ActionResult.Fail("背包里修船的材料都不够数，先备齐再来。还差：" + string.Join("、", BoatPartsMissing())
                    + "（硬木：砍大树桩、大原木，秘密森林每天刷；铱锭：骷髅洞穴的铱矿熔炼；电池组：避雷针在雷雨天充电）");
                yield break;
            }

            var o = new Outcome();
            if (Loc.Name != "BoatTunnel")
            {
                foreach (var s in TravelInner("BoatTunnel", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }

            var done = new List<string>();
            foreach (var part in ready)
            {
                Point tile = part.tile ?? FindActionTile(Loc, "BoatTicket") ?? new Point(-1, -1);
                if (tile.X < 0)
                    continue;
                foreach (var s in GoAdjacent(tile, o))
                    yield return s;
                if (!o.Ok)
                {
                    o.Succeed();
                    continue;
                }
                Loc.checkAction(new xLocation(tile.X, tile.Y), Game1.viewport, Player);
                foreach (var s in WaitTicks(10))
                    yield return s;
                if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask)
                {
                    int yes = System.Array.FindIndex(ask.responses, r => r.responseKey == "Yes");
                    foreach (var s in Dialogue(yes < 0 ? 0 : yes))
                        if (s is not ActionResult)
                            yield return s;
                    if (Game1.MasterPlayer.hasOrWillReceiveMail(part.mail))
                        done.Add($"{part.name} {part.count} 个");
                }
                else if (Game1.activeClickableMenu != null)
                {
                    foreach (var s in CloseMenu())
                        if (s is not ActionResult)
                            yield return s;
                }
            }
            // 交齐时游戏 1.5 秒后才弹一句“今天晚上会修好这条船”（BoatTunnel.checkForBoatComplete），等它出来再关掉，不然会挡住下一个动作
            bool allDone = BoatParts.All(p => Game1.MasterPlayer.hasOrWillReceiveMail(p.mail));
            for (int i = 0; i < (allDone ? 360 : 30) && Game1.activeClickableMenu is not DialogueBox; i++)
                yield return null;
            for (int i = 0; i < 5 && Game1.activeClickableMenu is DialogueBox; i++)
                foreach (var s in Dialogue(null))
                    if (s is not ActionResult)
                        yield return s;

            var missing = BoatPartsMissing();
            string result = (done.Count > 0 ? "交给威利修船：" + string.Join("、", done) + "。" : "没交上东西。")
                + (missing.Count == 0 ? "材料都齐了，船明天修好，到时候 travel_to IslandSouth 坐船去姜岛（船票 1000g）" : "还差：" + string.Join("、", missing));
            yield return done.Count > 0 ? ActionResult.Success(result) : ActionResult.Fail(result);
        }

        private static Point? FindActionTile(GameLocation loc, string action)
        {
            int w = Pathfinder.MapWidth(loc), h = Pathfinder.MapHeight(loc);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    if (loc.doesTileHaveProperty(x, y, "Action", "Buildings")?.Trim() == action)
                        return new Point(x, y);
            return null;
        }
    }
}
