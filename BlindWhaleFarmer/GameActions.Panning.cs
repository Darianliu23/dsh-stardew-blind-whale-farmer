using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 淘盘 ─────────────────────────
        // 社区中心鱼缸修好后（ccFishTank），河、湖边每 10 分钟有机会冒出一个“淘盘点”（水面上闪光的一小片，沙滩没有），
        // 站在 4 格以内对着它用淘盘，出铜铁金矿、晶球、宝石之类；淘完点就没了，过一阵别处再冒。

        /// <summary>现在哪些地点有淘盘点（当前地点排最前）。</summary>
        private static List<GameLocation> PanSpots() =>
            Game1.locations.Where(l => l != null && l.orePanPoint.Value != Point.Zero)
                .OrderBy(l => l == Game1.currentLocation ? 0 : 1).ToList();

        /// <summary>状态里的提示：背包里有淘盘、附近有淘盘点。没有返回 null。</summary>
        public static string PanHint()
        {
            if (Util.FindTool<Pan>() < 0 || Game1.currentLocation?.orePanPoint.Value is not Point p || p == Point.Zero)
                return null;
            return $"淘盘点：这里 ({p.X},{p.Y}) 的水面在闪，用 pan 淘一下（出矿石、晶球，有时有宝石）";
        }

        public IEnumerable<object> PanOre()
        {
            int pan = Util.FindTool<Pan>();
            if (pan < 0)
            {
                yield return ActionResult.Fail("背包里没有淘盘（社区中心鱼缸收集包交完、看完矿洞口河边那块闪光大石头被清走的剧情就会拿到；在箱子里就先 take_items 拿上）");
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var spots = PanSpots();
            if (spots.Count == 0)
            {
                yield return ActionResult.Fail(Game1.MasterPlayer.mailReceived.Contains("ccFishTank")
                    ? "现在哪儿都没有淘盘点（河、湖边的水面闪光，每 10 分钟有机会冒一个，沙滩没有），过一会儿再看"
                    : "社区中心的鱼缸还没修好，淘盘点不会出现");
                yield break;
            }

            var o = new Outcome();
            var target = spots[0];
            if (target != Loc)
            {
                foreach (var s in TravelInner(target.NameOrUniqueName, o))
                    yield return s;
                if (Loc != target)
                {
                    yield return ActionResult.Fail($"去 {target.NameOrUniqueName} 的淘盘点失败：{o.Message}");
                    yield break;
                }
            }
            var point = Loc.orePanPoint.Value;
            if (point == Point.Zero)
            {
                yield return ActionResult.Fail("走到的时候淘盘点已经没了（过一阵会在别处再冒），再调用 pan 找下一个");
                yield break;
            }
            // 淘盘点在水里，站在 3 格以内的岸上就够得着（游戏算的是 4 格）
            var near = new List<Point>();
            for (int dx = -3; dx <= 3; dx++)
                for (int dy = -3; dy <= 3; dy++)
                    if (dx * dx + dy * dy <= 10 && !Loc.isWaterTile(point.X + dx, point.Y + dy))
                        near.Add(new Point(point.X + dx, point.Y + dy));
            foreach (var s in GoToAny(near, o, "淘盘点旁边的岸上"))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail($"走不到淘盘点 ({point.X},{point.Y}) 附近的岸上：{o.Message}");
                yield break;
            }

            var before = Player.Items.Where(i => i != null).GroupBy(i => i.QualifiedItemId).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));
            foreach (var s in UseToolOnce(pan, point, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail("淘盘失败：" + o.Message);
                yield break;
            }
            foreach (var s in WaitPlayerFree(300))
                yield return s;
            // 背包满时游戏会弹物品界面让你挑
            var got = new List<string>();
            if (Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu grab)
                GrabAll(grab, got);
            var gained = Player.Items.Where(i => i != null).GroupBy(i => i.QualifiedItemId)
                .Select(g => (item: g.First(), n: g.Sum(i => i.Stack) - before.GetValueOrDefault(g.Key)))
                .Where(x => x.n > 0).Select(x => $"{x.item.DisplayName}x{x.n}").ToList();
            if (Loc.orePanPoint.Value == point && gained.Count == 0)
            {
                yield return ActionResult.Fail($"对着 ({point.X},{point.Y}) 用了淘盘但没淘到（可能站得太远），再试一次");
                yield break;
            }
            var left = PanSpots();
            yield return ActionResult.Success($"在 {Loc.DisplayName} 淘了一盘：{(gained.Count > 0 ? string.Join("、", gained) : "背包满了，东西掉在地上")}"
                + (left.Count > 0 ? $"。{string.Join("、", left.Select(l => l.DisplayName))} 还有淘盘点" : "。现在别处没有淘盘点了，过一会儿再看"));
        }
    }
}
