using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;

partial class Program
{
    static void TestEventRescueAndSprinkler(Type tActions)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // ── 第 1 项：剧情结束后卡位与异常位置脱困 ──
        var needsRescue = tActions.GetMethod("NeedsPositionRescue", Any);
        var pickRescue = tActions.GetMethod("PickRescueTile", Any);
        Check(needsRescue != null && pickRescue != null, "GameActions 暴露 NeedsPositionRescue / PickRescueTile");

        bool Need(Point p, bool onMap, bool passable, bool afterEvent, bool canReachExit, bool hasExits, int w = 120, int h = 110) =>
            (bool)needsRescue.Invoke(null, new object[] { p, onMap, passable, afterEvent, canReachExit, hasExits, w, h });

        Check(Need(new Point(-100, -100), false, false, false, false, true), "地图外 (-100,-100) 需要脱困");
        Check(Need(new Point(0, 0), true, true, false, true, true), "落在 (0,0) 需要脱困");
        Check(Need(new Point(12, 12), true, false, false, false, true), "落在不可走格需要脱困");
        Check(Need(new Point(0, 4), true, true, true, false, true), "剧情后落在 Town (0,4) 孤立角落需要脱困");
        Check(Need(new Point(0, 4), true, true, false, false, true), "平时卡在 (0,4) 孤立角落也能自动脱困");
        Check(Need(new Point(40, 40), true, true, true, false, true), "剧情后落在地图中部孤立死角需要脱困");
        Check(!Need(new Point(40, 40), true, true, true, true, true), "剧情后落在正常连通格不误触发");
        Check(!Need(new Point(40, 40), true, true, false, false, true), "非剧情后且不在边角的可走格不误触发");

        Point? Pick(Point cur, Point? before, IEnumerable<Point> exits, Point center, Func<Point, bool> stand, Func<Point, bool> reach) =>
            (Point?)pickRescue.Invoke(null, new object[] { cur, before, exits, center, stand, reach });

        // 模拟 Town (0,4) 孤立死角：附近几格可站但到不了出口，beforeEvent 是 (0,0)，应跳到出口 (15,20)
        var exitTile = new Point(15, 20);
        var rescuedFromCorner = Pick(
            new Point(0, 4),
            new Point(0, 0),
            new[] { exitTile },
            new Point(60, 55),
            p => (p.X <= 2 && p.Y <= 5) || p == exitTile,
            p => p == exitTile);
        Check(rescuedFromCorner == exitTile, $"Town (0,4) 孤立角落拒绝 (0,0) 并脱困到出口：{rescuedFromCorner}");

        // 剧情前位置有效且能到出口：优先回到剧情前位置
        var validBefore = new Point(30, 50);
        var rescuedToBefore = Pick(
            new Point(0, 0),
            validBefore,
            new[] { exitTile },
            new Point(60, 55),
            p => p == validBefore || p == exitTile,
            p => p == validBefore || p == exitTile);
        Check(rescuedToBefore == validBefore, $"有效剧情前位置优先恢复：{rescuedToBefore}");

        // 只偏进墙体 1 格：就近挪到旁边可走且连通出口的格子
        var nearSafe = new Point(21, 20);
        var rescuedNear = Pick(
            new Point(20, 20),
            null,
            new[] { exitTile },
            new Point(60, 55),
            p => p == nearSafe || p == exitTile,
            p => p == nearSafe || p == exitTile);
        Check(rescuedNear == nearSafe, $"偏进墙体 1 格就近挪出：{rescuedNear}");

        // ── 第 2 项：洒水器范围优先开地/播种、种子数截断、姜岛默认落点 ──
        var selectTill = tActions.GetMethod("SelectTillTargets", Any);
        var capSeeds = tActions.GetMethod("CapTillCountBySeeds", Any);
        var filterPlant = tActions.GetMethod("FilterPlantTilesBySprinkler", Any);
        var iwSpotField = tActions.GetField("IslandWestDefaultSpot", Any);
        Check(selectTill != null && capSeeds != null && filterPlant != null && iwSpotField != null,
            "GameActions 暴露 SelectTillTargets / CapTillCountBySeeds / FilterPlantTilesBySprinkler / IslandWestDefaultSpot");

        var tillPool = Enumerable.Range(0, 10).SelectMany(y => Enumerable.Range(0, 10).Select(x => new Point(x, y))).ToList();
        var sprCov = new HashSet<Point> { new(5, 5), new(5, 6), new(6, 5), new(6, 6) };
        var restricted = (List<Point>)selectTill.Invoke(null, new object[] { tillPool, new Point(0, 0), sprCov, 80, true });
        Check(restricted.Count == 4 && restricted.All(sprCov.Contains),
            $"SelectTillTargets(restrictToSprinkler=true) 严格只开洒水器覆盖格（实际 {restricted.Count}）");

        int Cap(int want, int invSeeds, int emptyDirt, bool force) =>
            (int)capSeeds.Invoke(null, new object[] { want, invSeeds, emptyDirt, force });
        Check(Cap(80, 33, 0, false) == 33, "有 33 颗种子、0 空地时最多开 33 格");
        Check(Cap(80, 33, 20, false) == 13, "有 33 颗种子、已有 20 空地时最多再开 13 格");
        Check(Cap(80, 33, 40, false) == 0, "空耕地已够种背包种子时不继续开地");
        Check(Cap(20, 0, 5, false) == 20, "背包没种子时不阻断提前开地");
        Check(Cap(80, 33, 40, true) == 80, "force=true 时不受种子数限制");

        var plantable = new List<Point> { new(5, 5), new(5, 6), new(10, 10), new(11, 10), new(12, 10) };
        var f1 = ((List<Point> tiles, int skipped))filterPlant.Invoke(null, new object[] { plantable, sprCov, false });
        Check(f1.tiles.Count == 2 && f1.tiles.All(sprCov.Contains) && f1.skipped == 3,
            "FilterPlantTilesBySprinkler 有洒水器时只种覆盖格并统计范围外空地");

        var outsideOnly = new List<Point> { new(10, 10), new(11, 10), new(12, 10) };
        var f2 = ((List<Point> tiles, int skipped))filterPlant.Invoke(null, new object[] { outsideOnly, sprCov, false });
        Check(f2.tiles.Count == 0 && f2.skipped == 3,
            "FilterPlantTilesBySprinkler 洒水器范围种满后不往范围外乱种");

        var f3 = ((List<Point> tiles, int skipped))filterPlant.Invoke(null, new object[] { outsideOnly, sprCov, true });
        Check(f3.tiles.Count == 3 && f3.skipped == 0,
            "FilterPlantTilesBySprinkler 显式指定范围外矩形时放行");

        var f4 = ((List<Point> tiles, int skipped))filterPlant.Invoke(null, new object[] { outsideOnly, new HashSet<Point>(), false });
        Check(f4.tiles.Count == 3 && f4.skipped == 0,
            "FilterPlantTilesBySprinkler 无洒水器时原样返回");

        var iwSpot = (Point)iwSpotField.GetValue(null);
        Check(iwSpot == new Point(77, 42), $"姜岛默认落点在农舍前/农田边 ({iwSpot.X},{iwSpot.Y})");

        // ── 第 3 轮新增：脱困不误挪正常状态、姜岛乌龟碰撞、洒水器范围外提示 ──
        var skipRescue = tActions.GetMethod("ShouldSkipPositionRescue", Any);
        Check(skipRescue != null, "GameActions 暴露 ShouldSkipPositionRescue");
        bool Skip(bool afterEvent, bool sit, bool horse, bool swim, bool tool, bool eat, bool fish, bool cantMove, bool onMap) =>
            (bool)skipRescue.Invoke(null, new object[] { afterEvent, sit, horse, swim, tool, eat, fish, cantMove, onMap });

        Check(Skip(false, true, false, false, false, false, false, false, true), "坐着时不触发 5 秒脱困");
        Check(Skip(true, true, false, false, false, false, false, false, true), "剧情后坐着也不触发脱困");
        Check(Skip(false, false, true, false, false, false, false, false, true), "骑马时不触发 5 秒脱困");
        Check(Skip(true, false, true, false, false, false, false, false, true), "剧情后骑马也不触发脱困");
        Check(Skip(false, false, false, true, false, false, false, false, true), "澡堂游泳时不触发脱困");
        Check(Skip(false, false, false, false, true, false, false, false, true), "使用工具时不触发 5 秒脱困");
        Check(Skip(false, false, false, false, false, false, true, false, true), "钓鱼时不触发 5 秒脱困");
        Check(Skip(false, false, false, false, false, false, false, true, true), "播动画 CanMove=false 时不触发 5 秒脱困");
        Check(!Skip(false, false, false, false, false, false, false, false, false), "真在地图外时仍正常脱困");
        Check(!Skip(false, false, false, false, false, false, false, false, true), "普通站立卡死时不跳过脱困");

        var tPathfinder = tActions.Assembly.GetType("BlindWhaleFarmer.Pathfinder");
        var turtleBlocked = tPathfinder?.GetMethod("IsBlockedByIslandSouthTurtles", Any);
        Check(turtleBlocked != null, "Pathfinder 暴露 IsBlockedByIslandSouthTurtles");
        bool TurtleHit(int tx, int ty, bool exitsBlocked, bool westernMoved) =>
            (bool)turtleBlocked.Invoke(null, new object[] { new Rectangle(tx * 64 + 8, ty * 64, 48, 32), exitsBlocked, westernMoved });

        Check(TurtleHit(1, 11, false, false), "姜岛西乌龟未挪开时 (1,11) 判为走不通");
        Check(!TurtleHit(1, 11, false, true), "姜岛西乌龟挪开后 (1,11) 判为能走");
        Check(!TurtleHit(5, 11, false, false), "西乌龟范围外 (5,11) 判为能走");
        Check(TurtleHit(18, 1, true, false), "姜岛北乌龟未解锁时 (18,1) 判为走不通");
        Check(!TurtleHit(18, 1, false, false), "姜岛北乌龟解锁后 (18,1) 判为能走");

        var fmtUncovered = tActions.GetMethod("FormatUncoveredBySprinklerHint", Any);
        Check(fmtUncovered != null, "GameActions 暴露 FormatUncoveredBySprinklerHint");
        string Uncov(int crops, int empty, bool rain) =>
            (string)fmtUncovered.Invoke(null, new object[] { crops, empty, rain });

        string hBoth = Uncov(3, 5, false);
        Check(hBoth != null && hBoth.Contains("3 株") && hBoth.Contains("5 格") && hBoth.Contains("手动浇水"),
            "洒水器范围外同时有作物与空耕地时提示计数正确：" + hBoth);
        string hCrop = Uncov(2, 0, false);
        Check(hCrop != null && hCrop.Contains("2 株") && !hCrop.Contains("空耕地"),
            "洒水器范围外只有待浇水作物时提示正确：" + hCrop);
        string hEmpty = Uncov(0, 4, false);
        Check(hEmpty != null && hEmpty.Contains("4 格") && hEmpty.Contains("手动浇水"),
            "洒水器范围外只有空耕地时提示正确：" + hEmpty);
        Check(Uncov(3, 5, true) == null, "下雨天不提示洒水器范围外浇水");
        Check(Uncov(0, 0, false) == null, "范围外无待浇水作物与空耕地时不提示");
    }
}
