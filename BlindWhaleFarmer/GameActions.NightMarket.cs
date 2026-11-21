using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 夜市（冬 15-17）的美人鱼表演 ─────────────────────────
        // 夜市沙滩 (58,31) 的船，17:00-24:30 开门，免费。进去自动开演，一分多钟（现实时间）。
        // 演完以后按 1、5、4、2、3 的顺序敲台前五个贝壳（MermaidHouse.playClamTone 里 0,4,3,1,2），美人鱼会送一颗珍珠，每个存档一次（gotPearl）。
        // 表演没演完敲贝壳没反应；中途出门再进来要重新看。

        /// <summary>按顺序要敲的贝壳（从左往右 0~4）。</summary>
        private static readonly int[] ClamOrder = { 0, 4, 3, 1, 2 };

        private static bool HasMermaidPearl => Player.mailReceived.Contains("gotPearl");

        /// <summary>状态里的提示：今天有夜市、美人鱼表演还没看（珍珠没拿）。没事返回 null。</summary>
        public static string NightMarketHint()
        {
            if (!IsNightMarketDay() || Game1.timeOfDay >= 2400 || Game1.currentLocation is MermaidHouse)
                return null;
            string when = Game1.timeOfDay < 1700 ? "今晚 17:00 起" : "现在";
            return HasMermaidPearl
                ? $"夜市：{when}沙滩上有夜市（商船、美人鱼表演、深海潜水艇 1000g 下潜钓深海鱼）"
                : $"夜市：{when}沙滩上有夜市。美人鱼表演（免费）看完按顺序敲贝壳能拿一颗珍珠（只有一次），用 mermaid_show 自己去看、自己敲，"
                  + "别钓鱼钓到太晚忘了去（船 24:30 关门，夜市只开冬季 15-17 日三晚）";
        }

        public IEnumerable<object> MermaidShow(bool again = false)
        {
            if (HasMermaidPearl && !again)
            {
                yield return ActionResult.Fail("美人鱼表演已经看过、珍珠也拿过了（每个存档只有一次），不用再去。真想再看一遍传 again=true");
                yield break;
            }
            if (!IsNightMarketDay())
            {
                yield return ActionResult.Fail("美人鱼表演在夜市（冬季 15-17 日晚上），今天没有夜市");
                yield break;
            }
            if (Loc is not MermaidHouse && (Game1.timeOfDay < 1500 || Game1.timeOfDay >= 2400))
            {
                yield return ActionResult.Fail($"美人鱼表演的船 17:00-24:30 开门，现在 {Util.TimeString(Game1.timeOfDay)}" + (Game1.timeOfDay < 1500 ? "，先去干别的，傍晚再来" : "，今天来不及了"));
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }

            var o = new Outcome();
            if (Loc is not MermaidHouse)
            {
                foreach (var s in TravelInner("MermaidHouse", o))
                    yield return s;
                if (!o.Ok || Loc is not MermaidHouse)
                {
                    yield return ActionResult.Fail("去美人鱼表演的船失败：" + o.Message);
                    yield break;
                }
            }
            var house = (MermaidHouse)Loc;

            // 等表演演完（贝壳在 68 秒以后才响）。表演按现实时间走，最多等 3 分钟
            var watch = reflection.GetField<float>(house, "oldStopWatchTime");
            for (int t = 0; t < 60 * 180 && watch.GetValue() < 68500f; t++)
            {
                if (Loc != house)
                {
                    yield return ActionResult.Fail("表演还没演完就离开船了，再进去要重新看");
                    yield break;
                }
                yield return null;
            }
            if (watch.GetValue() < 68500f)
            {
                yield return ActionResult.Fail("等了 3 分钟表演还没演完（可能游戏被暂停了），过一会儿再调用 mermaid_show");
                yield break;
            }
            if (HasMermaidPearl)
            {
                yield return ActionResult.Success("看完了美人鱼表演（珍珠以前已经拿过了）。出去用 travel_to");
                yield break;
            }

            // 找五个贝壳（mermaid_tiles 图块 56~60，从左往右）
            var clams = new Point?[5];
            int w = Pathfinder.MapWidth(house), h = Pathfinder.MapHeight(house);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int idx = house.getTileIndexAt(x, y, "Buildings", "mermaid_tiles");
                    if (idx is >= 56 and <= 60)
                        clams[idx - 56] ??= new Point(x, y);
                }
            if (clams.Any(c => c == null))
            {
                yield return ActionResult.Fail("船里没找到五个贝壳");
                yield break;
            }

            foreach (int which in ClamOrder)
            {
                var clam = clams[which].Value;
                foreach (var s in GoAdjacent(clam, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"走不到第 {which + 1} 个贝壳 ({clam.X},{clam.Y})：{o.Message}");
                    yield break;
                }
                house.checkAction(new xLocation(clam.X, clam.Y), Game1.viewport, Player);
                foreach (var s in WaitTicks(20))
                    yield return s;
            }

            // 敲对了：美人鱼回奏一遍，几秒后把珍珠递过来（背包满时弹物品界面）
            for (int t = 0; t < 60 * 10 && !HasMermaidPearl; t++)
                yield return null;
            if (!HasMermaidPearl)
            {
                yield return ActionResult.Fail("按 1、5、4、2、3 敲了贝壳，但美人鱼没给珍珠");
                yield break;
            }
            foreach (var s in WaitPlayerFree(300))
                yield return s;
            if (Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu grab)
            {
                var got = new List<string>();
                GrabAll(grab, got);
            }
            var notes = new List<string>();
            foreach (var s in CloseInfoDialogs(notes))
                yield return s;
            bool inBag = Player.Items.Any(i => i?.QualifiedItemId == "(O)797");
            yield return ActionResult.Success("看完美人鱼表演，按顺序敲了贝壳，美人鱼送了一颗珍珠"
                + (inBag ? "（在背包里，很值钱，留着送人或者卖掉都行）" : "（背包满了，珍珠掉在脚边，空出位置走一步捡起来）")
                + "。出去用 travel_to");
        }
    }
}
