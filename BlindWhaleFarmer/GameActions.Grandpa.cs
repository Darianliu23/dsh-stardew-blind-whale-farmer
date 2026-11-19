using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 爷爷的评价 ─────────────────────────
        // 第三年春 1 日早上爷爷的鬼魂来农场神龛评分，按分数点亮 1-4 根蜡烛（12 分以上 4 根）。
        // 没满 4 根的话，往神龛上放一颗钻石，第二天早上重新评；4 根都亮以后去神龛领完美雕像（放下后每天出铱矿石）。

        private static int DiamondIndex()
        {
            var items = Game1.player.Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i]?.QualifiedItemId == "(O)72")
                    return i;
            return -1;
        }

        private static List<(string what, int got, int max, string tip)> GrandpaBreakdown()
        {
            var p = Game1.player;
            long earned = p.totalMoneyEarned;
            int money = (earned >= 50000 ? 1 : 0) + (earned >= 100000 ? 1 : 0) + (earned >= 200000 ? 1 : 0) + (earned >= 300000 ? 1 : 0)
                + (earned >= 500000 ? 1 : 0) + (earned >= 1000000 ? 2 : 0);
            bool ccOpen = Game1.isLocationAccessible("CommunityCenter");
            int friends = Utility.getNumberOfFriendsWithinThisRange(p, 1975, 999999);
            int level = p.Level;
            return new()
            {
                ("累计赚钱", money, 7, $"现在 {earned}g；5万/10万/20万/30万/50万各 1 分，100万 2 分"),
                ("博物馆收藏全齐（成就）", p.achievements.Contains(5) ? 1 : 0, 1, "博物馆的文物和矿物全部捐齐"),
                ("骷髅钥匙", p.hasSkullKey ? 1 : 0, 1, "矿洞打到 120 层"),
                ("社区中心全部收集包交完", ccOpen || p.hasCompletedCommunityCenter() ? 1 : 0, 1, "六个房间加布告栏全交完（走 Joja 拿不到）"),
                ("社区中心修好的庆典", ccOpen ? 2 : 0, 2, "看过社区中心修好后的庆典剧情（走 Joja 拿不到）"),
                ("结婚且房子升到二级", p.isMarriedOrRoommates() && Utility.getHomeOfFarmer(p).upgradeLevel >= 2 ? 1 : 0, 1, "结婚，房子升级两次"),
                ("生锈的钥匙", p.hasRustyKey ? 1 : 0, 1, "博物馆捐 60 件"),
                ("钓齐所有鱼 / 出货所有东西（成就）", (p.achievements.Contains(26) ? 1 : 0) + (p.achievements.Contains(34) ? 1 : 0), 2, "钓过每一种鱼 1 分，每种能出货的东西都出过 1 分"),
                ("8 心以上的好友人数", (friends >= 5 ? 1 : 0) + (friends >= 10 ? 1 : 0), 2, $"现在 {friends} 人有 8 心以上（约 1975 好感），5 人 1 分、10 人 2 分"),
                ("技能等级合计", (level >= 15 ? 1 : 0) + (level >= 25 ? 1 : 0), 2, $"现在农夫等级 {level}（五个技能的一半之和），15 级 1 分、25 级 2 分"),
                ("宠物很爱你", p.mailReceived.Contains("petLoveMessage") ? 1 : 0, 1, "宠物好感满（每天摸、水碗加水）"),
            };
        }

        /// <summary>看爷爷评分；放钻石重新评；4 根蜡烛后领雕像。action：status（默认）/ diamond / statue。</summary>
        public IEnumerable<object> Grandpa(string action)
        {
            action = (action ?? "status").Trim().ToLowerInvariant();
            var farm = Game1.getFarm();
            int score = Utility.getGrandpaScore();
            int candlesNow = Utility.getGrandpaCandlesFromScore(score);
            int lit = farm.grandpaScore.Value;
            var parts = GrandpaBreakdown();
            string status = $"爷爷的评分现在算下来是 {score} 分（满 21 分），评的话能点 {candlesNow} 根蜡烛（4 分 2 根、8 分 3 根、12 分 4 根）。"
                + (lit > 0 ? $"神龛上现在亮着 {lit} 根。" : Game1.year >= 3 ? "爷爷还没来评过。" : $"现在是第 {Game1.year} 年，第三年春 1 日早上爷爷才来评。")
                + "\n" + string.Join("\n", parts.Select(x => $"{(x.got >= x.max ? "✓" : "·")} {x.what} {x.got}/{x.max}" + (x.got < x.max ? $"（{x.tip}）" : "")));
            if (lit is > 0 and < 4)
                status += "\n没点满 4 根：分数够了以后往神龛放一颗钻石（grandpa action=diamond），第二天早上重新评";
            else if (lit >= 4)
                status += Utility.doesItemExistAnywhere("(BC)160") ? "\n4 根蜡烛都亮了，完美雕像已经领过" : "\n4 根蜡烛都亮了，可以去神龛领完美雕像（grandpa action=statue）";

            if (action is not ("diamond" or "statue"))
            {
                yield return ActionResult.Success(status);
                yield break;
            }
            if (action == "diamond")
            {
                if (lit <= 0 || Game1.year < 3)
                {
                    yield return ActionResult.Fail("爷爷还没来评过，现在放钻石没用（第三年春 1 日早上才评）。\n" + status);
                    yield break;
                }
                if (lit >= 4)
                {
                    yield return ActionResult.Fail("已经 4 根蜡烛了，不用再放钻石。\n" + status);
                    yield break;
                }
                if (candlesNow <= lit)
                {
                    yield return ActionResult.Fail($"现在的分数重评也只有 {candlesNow} 根，放了钻石也不会多，先把分数提上去。\n" + status);
                    yield break;
                }
                if (DiamondIndex() < 0)
                {
                    yield return ActionResult.Fail("背包里没有钻石（矿洞 50 层以下的宝石矿石、晶球里出，也能在箱子里 take_items 拿）");
                    yield break;
                }
            }
            else
            {
                if (lit < 4)
                {
                    yield return ActionResult.Fail("要 4 根蜡烛都亮了才能领雕像。\n" + status);
                    yield break;
                }
                if (Utility.doesItemExistAnywhere("(BC)160"))
                {
                    yield return ActionResult.Fail("完美雕像已经领过了（在哪个箱子或已经放在地上）");
                    yield break;
                }
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }

            var o = new Outcome();
            if (Loc != farm)
            {
                foreach (var s in TravelInner("Farm", o))
                    yield return s;
                if (Loc != farm)
                {
                    yield return ActionResult.Fail("回农场失败：" + o.Message);
                    yield break;
                }
            }
            Point shrine = farm.GetGrandpaShrinePosition();
            foreach (var s in GoAdjacent(shrine, o, new Point(shrine.X, shrine.Y + 1)))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail($"走不到神龛 ({shrine.X},{shrine.Y}) 前面：{o.Message}");
                yield break;
            }
            if (action == "diamond")
            {
                int idx = DiamondIndex();
                if (idx < 0)
                {
                    yield return ActionResult.Fail("背包里的钻石不见了");
                    yield break;
                }
                Player.CurrentToolIndex = idx;
            }
            Player.faceGeneralDirection(new Vector2(shrine.X, shrine.Y) * 64f);
            farm.checkAction(new xTile.Dimensions.Location(shrine.X, shrine.Y), Game1.viewport, Player);
            // 从没看过神龛上爷爷的信时，第一次互动只是弹信：关掉再互动一次
            if (Game1.activeClickableMenu is StardewValley.Menus.LetterViewerMenu note)
            {
                note.exitThisMenu(false);
                foreach (var s in WaitTicks(10))
                    yield return s;
                farm.checkAction(new xTile.Dimensions.Location(shrine.X, shrine.Y), Game1.viewport, Player);
            }
            foreach (var s in WaitPlayerFree(300))
                yield return s;
            var got = new List<string>();
            if (Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu grab)
                GrabAll(grab, got);
            foreach (var s in CloseDialogues())
                yield return s;

            if (action == "diamond")
                yield return ActionResult.Success($"在爷爷的神龛上放了一颗钻石，明天早上爷爷会重新评分（按现在的 {score} 分算是 {candlesNow} 根蜡烛）");
            else
                yield return Utility.doesItemExistAnywhere("(BC)160")
                    ? ActionResult.Success("从神龛领到了完美雕像，放下以后每天会出铱矿石")
                    : ActionResult.Fail("对神龛互动了，但没拿到雕像（背包可能满了），腾出一格再试");
        }
    }
}
