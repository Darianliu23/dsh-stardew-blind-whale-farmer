using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Tools;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// 姜岛火山地牢（VolcanoDungeon0~9，再往上是火山口 Caldera）。和矿洞不一样：没有梯子，每层地图下方是入口、
    /// 上方某处是出口（走进去就到下一层）；路上有岩浆（用水壶浇冷才能走）、矮人机关门（踩亮这一层所有开关才开）。
    /// 第 5 层是安全层（矮人商店、泉水），第 9 层往上是火山口（锻造台）。
    /// </summary>
    internal sealed partial class GameActions
    {
        /// <summary>这一格是不是还烫着的岩浆（第 5 层的是泉水，不算）。</summary>
        private static bool IsHotLava(GameLocation loc, Point p) =>
            loc is VolcanoDungeon vd && vd.level.Value != 5 && vd.waterTiles != null
            && p.X >= 0 && p.Y >= 0 && p.X < Pathfinder.MapWidth(vd) && p.Y < Pathfinder.MapHeight(vd)
            && vd.waterTiles[p.X, p.Y] && !vd.IsCooledLava(p.X, p.Y);

        /// <summary>背包里还有水的水壶（浇岩浆用），没有返回 -1。</summary>
        private static int VolcanoCanWithWater() => Util.FindTool<WateringCan>(c => c.IsBottomless || c.WaterLeft > 0);

        private static string VolcanoLevelName(GameLocation loc) => loc switch
        {
            VolcanoDungeon vd => $"火山第 {vd.level.Value} 层",
            Caldera => "火山口",
            _ => loc?.Name ?? "?"
        };

        /// <summary>第 0 层入口和第 5 层的泉水能给水壶灌水（VolcanoDungeon.CanRefillWateringCanOnTile）。</summary>
        private static IEnumerable<Point> VolcanoRefillTiles(VolcanoDungeon vd) => vd.level.Value switch
        {
            0 => from x in Enumerable.Range(24, 4) from y in Enumerable.Range(43, 4) select new Point(x, y),
            5 => from x in Enumerable.Range(27, 4) from y in Enumerable.Range(29, 4) select new Point(x, y),
            _ => Enumerable.Empty<Point>()
        };

        /// <summary>在能灌水的那层，水壶不满就去泉水边灌满。</summary>
        private IEnumerable<object> RefillCanInVolcano(VolcanoDungeon vd, List<string> notes)
        {
            int idx = Util.FindTool<WateringCan>();
            if (idx < 0 || Player.Items[idx] is not WateringCan can || can.IsBottomless || can.WaterLeft >= can.waterCanMax)
                yield break;
            var spring = VolcanoRefillTiles(vd).ToList();
            if (spring.Count == 0)
                yield break;
            var o = new Outcome();
            var target = spring.OrderBy(p => Util.Manhattan(p, Player.TilePoint)).First();
            foreach (var s in GoToAny(spring.SelectMany(p => Pathfinder.AdjacentStandTiles(vd, p)).Distinct().ToList(), o, "泉水边"))
                yield return s;
            if (!o.Ok)
                yield break;
            target = spring.OrderBy(p => Util.Manhattan(p, Player.TilePoint)).First();
            foreach (var s in UseToolOnce(idx, target, o))
                yield return s;
            if (o.Ok && can.WaterLeft >= can.waterCanMax)
                notes.Add($"在{VolcanoLevelName(vd)}的泉水灌满了水壶");
        }

        /// <summary>这一层通往下一层（或火山口）的出口格：走上去就传送。</summary>
        private static List<Point> VolcanoExits(VolcanoDungeon vd)
        {
            string next = VolcanoDungeon.GetLevelName(vd.level.Value + 1);
            return vd.warps.Where(w => w.TargetName == next || (vd.level.Value == 9 && w.TargetName == "Caldera"))
                .Select(w => new Point(w.X, w.Y)).Distinct().ToList();
        }

        /// <summary>还没踩的矮人机关开关（踩亮一层的全部开关，机关门才开）。</summary>
        private static List<Point> VolcanoSwitches(VolcanoDungeon vd) =>
            vd.dwarfGates.Where(g => !g.opened.Value)
                .SelectMany(g => g.switches.Pairs.Where(kv => !kv.Value).Select(kv => kv.Key)).ToList();

        /// <summary>
        /// 下火山：打附近的怪、开宝箱、捡东西，找路去这一层的出口（路上的石头敲掉、岩浆用水壶浇冷），
        /// 出口被机关门挡着就先去踩开关。下够 levels 层、到了火山口、或者体力/生命/时间不够时停下。
        /// </summary>
        public IEnumerable<object> ExploreVolcano(int levels, int? maxBombs)
        {
            var o = new Outcome();
            string clutter = TripClutterHint("mine", i => i is WateringCan); // 火山里水壶要用来浇岩浆
            if (Loc is not VolcanoDungeon && Loc is not Caldera)
            {
                if (!Exits.BoatRepaired)
                {
                    yield return ActionResult.Fail("火山在姜岛北边，要先修好威利的船（repair_boat）坐船去姜岛");
                    yield break;
                }
                foreach (var s in TravelInner("VolcanoDungeon0", o, exactName: true))
                    yield return s;
                foreach (var s in WaitSceneSettled())
                    yield return s;
                if (Loc is not VolcanoDungeon)
                {
                    yield return ActionResult.Fail("没能进火山（入口在姜岛北边 IslandNorth；去岛北要先给鹦鹉交核桃让乌龟让路？）：" + o.Message);
                    yield break;
                }
            }
            if (Loc is Caldera)
            {
                yield return ActionResult.Success("已经在火山口了（最顶上，有锻造台；回去用 travel_to）");
                yield break;
            }
            int pick = Util.FindTool<Pickaxe>();
            if (pick < 0)
            {
                yield return ActionResult.Fail("没有镐子，火山里的石头敲不开");
                yield break;
            }

            levels = Math.Clamp(levels, 1, 10);
            var startLoc = Loc;
            int descended = 0, rocks = 0, switchesPressed = 0, lavaCooled = 0;
            var st = new FightStats();
            var loot = new List<string>();
            var chestNotes = new List<string>();
            var notes = new List<string>();
            var grabbed = new List<string>();
            var skippedChests = new HashSet<Point>();
            var skippedExtras = new HashSet<Point>();
            var skippedSwitches = new HashSet<Point>();
            var switchTries = new Dictionary<Point, int>();
            int bombBudget = Math.Clamp(maxBombs ?? 5, 0, 50), blastBombs = 0, blastStones = 0, blastOres = 0;
            var skippedBlasts = new HashSet<Point>();
            var blastNotes = new List<string>();
            string stop = null;
            VolcanoDungeon refilledOn = null;
            int guard = 0;

            while (descended < levels && guard++ < 300)
            {
                if (Game1.isWarping || Game1.fadeToBlack || Game1.globalFade)
                {
                    foreach (var s in WaitSceneSettled())
                        yield return s;
                    continue;
                }
                if (Loc is Caldera)
                {
                    stop = "到了火山口（最顶上，锻造台在这里）";
                    break;
                }
                if (Loc is not VolcanoDungeon vd)
                {
                    stop = "离开了火山";
                    break;
                }
                // 敲石头、开箱子掉金核桃时弹“你获得了金色核桃！”：只是通知，关掉接着走
                if (Game1.CurrentEvent == null && Game1.activeClickableMenu is DialogueBox { isQuestion: false })
                {
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;
                    continue;
                }
                if (Blocked(out string why))
                {
                    stop = why;
                    break;
                }
                if (Player.Stamina < 15)
                {
                    stop = "体力不够了";
                    break;
                }
                if (Game1.timeOfDay >= 2300)
                {
                    stop = "已经 23 点了，该回家睡觉";
                    break;
                }
                // 能灌水的那层先把水壶灌满（后面的岩浆要浇）
                if (refilledOn != vd)
                {
                    refilledOn = vd;
                    foreach (var s in RefillCanInVolcano(vd, notes))
                        yield return s;
                }

                // 1) 先打附近的怪
                foreach (var s in FightInner(6, 0.4f, 20, st, o))
                    yield return s;
                if (!o.Ok && o.Message.StartsWith("!"))
                {
                    stop = o.Message.Substring(1);
                    break;
                }
                o.Succeed();

                // 2) 宝箱（火山的宝箱可能有金核桃、龙牙、好装备）
                if (MineChests(vd).Where(c => !skippedChests.Contains(c.tile)).Select(c => (Point?)c.tile).FirstOrDefault() is Point chestTile)
                {
                    var took = new List<string>();
                    string chestNote = null;
                    foreach (var s in OpenMineChest(vd, chestTile, took, n => chestNote = n))
                        yield return s;
                    loot.AddRange(took);
                    if (chestNote == null && took.Count == 0 && MineChests(vd).Any(c => c.tile == chestTile))
                        chestNote = $"({chestTile.X},{chestTile.Y}) 的宝箱没打开";
                    if (chestNote != null)
                    {
                        skippedChests.Add(chestTile);
                        chestNotes.Add(chestNote);
                    }
                    continue;
                }

                // 3) 顺手捡附近地上的东西、劈木箱
                if (NearestThreat(3) == null && Player.health >= Player.maxHealth * 0.4f && !Player.isInventoryFull()
                    && MineExtras(vd, skippedExtras).FirstOrDefault() is var extra && extra.obj != null)
                {
                    bool box = extra.obj is StardewValley.Objects.BreakableContainer;
                    string extraName = extra.obj.DisplayName;
                    foreach (var s in GoAdjacent(extra.p, o))
                        yield return s;
                    if (o.Ok)
                        foreach (var s in ClearObstacle(extra.p, o, maxHits: 12))
                            yield return s;
                    if (vd.objects.ContainsKey(new Vector2(extra.p.X, extra.p.Y)))
                        skippedExtras.Add(extra.p);
                    else if (box)
                    {
                        var drops = new List<string>();
                        foreach (var s in CollectDebris(new Rectangle(extra.p.X - 2, extra.p.Y - 2, 5, 5), drops, 30))
                            yield return s;
                    }
                    else
                        grabbed.Add(extraName);
                    o.Succeed();
                    continue;
                }

                // 4) 有炸弹、附近一片值得炸的石头：炸一颗（和下矿一样的条件）
                if (blastBombs < bombBudget && NearestThreat(4) == null && Player.health >= Player.maxHealth * 0.5f && !Player.isInventoryFull())
                {
                    var bombs = BombsInBag();
                    if (bombs.Count > 0)
                    {
                        bool tired = Player.Stamina < Player.MaxStamina * 0.3f;
                        var spot = BestBlastSpot(vd, bombs, 8, skippedBlasts);
                        if (spot.Found && (spot.Ores >= 2 || spot.Stones >= 8 || (tired && spot.Stones >= 3)))
                        {
                            foreach (var s in BlastStones(spot, o, blastNotes, (b, n) => { blastStones += b; blastOres += n; rocks += b; }))
                                yield return s;
                            if (o.Ok)
                                blastBombs++;
                            else
                                skippedBlasts.Add(spot.Tile);
                            o.Succeed();
                            continue;
                        }
                    }
                }

                // 5) 去出口：路上的石头、杂物敲掉，岩浆浇冷（GoToAnyClearing 每清一格重新找路）
                var exits = VolcanoExits(vd);
                if (exits.Count == 0)
                {
                    stop = $"{VolcanoLevelName(vd)}没找到出口";
                    break;
                }
                int lavaBefore = vd.cooledLavaTiles.Count();
                int stonesBefore = vd.objects.Values.Count(x => x.IsBreakableStone());
                var path = Pathfinder.FindPath(vd, Player.TilePoint, exits, 30000, clearable: ClearableObstacle, clearCost: 6);
                if (path != null)
                {
                    foreach (var s in GoToAnyClearing(exits, o, "这一层的出口"))
                        yield return s;
                    lavaCooled += Math.Max(0, vd.cooledLavaTiles.Count() - lavaBefore);
                    rocks += Math.Max(0, stonesBefore - vd.objects.Values.Count(x => x.IsBreakableStone()));
                    // 站上出口格：游戏只在人自己走动碰到传送点时才传送（我们是直接挪的坐标），所以和 travel_to 一样手动传送，
                    // 目标坐标照抄地图上的传送点（到下一层后游戏按 y=1 把人放到入口）
                    if (o.Ok && Loc == vd && exits.Contains(Player.TilePoint) && !Game1.isWarping && Game1.locationRequest == null)
                    {
                        var warp = vd.warps.FirstOrDefault(w => w.X == Player.TilePoint.X && w.Y == Player.TilePoint.Y);
                        if (warp != null)
                            Game1.warpFarmer(warp.TargetName, warp.TargetX, warp.TargetY, 0);
                    }
                    for (int t = 0; t < 300 && Loc == vd; t++)
                        yield return null;
                    foreach (var s in WaitSceneSettled())
                        yield return s;
                    if (Loc != vd)
                    {
                        descended++;
                        o.Succeed();
                        continue;
                    }
                    if (!o.Ok && o.Message.Contains("岩浆"))
                    {
                        stop = o.Message;
                        break;
                    }
                    o.Succeed();
                    // 走了一段被打断（怪贴上来之类）：回到开头处理，再接着走
                    continue;
                }

                // 6) 出口被机关门挡着：去踩开关（开关是地上的按钮，踩上去就亮）
                var sw = VolcanoSwitches(vd).Where(p => !skippedSwitches.Contains(p)).OrderBy(p => Util.Manhattan(p, Player.TilePoint)).ToList();
                if (sw.Count > 0)
                {
                    var target = sw[0];
                    foreach (var s in GoToAnyClearing(new[] { target }, o, $"机关开关 ({target.X},{target.Y})"))
                        yield return s;
                    foreach (var s in WaitTicks(20))
                        yield return s;
                    if (!VolcanoSwitches(vd).Contains(target))
                    {
                        switchesPressed++;
                        // 开关全踩亮以后门要过一会儿才打开（DwarfGate 的开门动画），门开之前寻路还是走不通
                        for (int t = 0; t < 300 && vd.dwarfGates.Any(g => !g.opened.Value && g.switches.Values.All(v => v)); t++)
                            yield return null;
                        foreach (var s in WaitTicks(30))
                            yield return s;
                    }
                    else
                    {
                        // 路上被怪打断不算走不到：试 3 次都没踩上再放弃
                        switchTries[target] = switchTries.GetValueOrDefault(target) + 1;
                        if (switchTries[target] >= 3)
                        {
                            skippedSwitches.Add(target);
                            notes.Add($"机关开关 ({target.X},{target.Y}) 走不过去：{o.Message}");
                        }
                    }
                    o.Succeed();
                    continue;
                }

                stop = VolcanoCanWithWater() < 0 && Util.FindTool<WateringCan>() >= 0
                    ? $"{VolcanoLevelName(vd)}去出口的路被岩浆挡住了，水壶没水浇不冷（第 0 层入口和第 5 层有泉水）"
                    : Util.FindTool<WateringCan>() < 0
                        ? $"{VolcanoLevelName(vd)}去出口的路被岩浆挡住了，背包里没有水壶（水壶浇岩浆会冷却成路）"
                        : $"{VolcanoLevelName(vd)}找不到去出口的路（开关也踩不到），可以试试 use_tool 敲开别处的石头，或者 travel_to 出去";
                break;
            }

            string msg = $"从{VolcanoLevelName(startLoc)}到了{VolcanoLevelName(Loc)}（上了 {descended} 层），敲了 {rocks} 块石头，打倒 {st.Kills} 只怪，吃了 {st.Eaten} 次东西；生命 {Player.health}/{Player.maxHealth}，体力 {(int)Player.Stamina}";
            if (lavaCooled > 0)
                msg += $"；浇冷了 {lavaCooled} 格岩浆";
            if (switchesPressed > 0)
                msg += $"；踩了 {switchesPressed} 个机关开关";
            if (blastBombs > 0)
                msg += $"；用了 {blastBombs} 颗炸弹，炸掉 {blastStones} 块石头（其中矿石/宝石节点 {blastOres} 个）";
            if (blastNotes.Count > 0)
                msg += "；" + string.Join("；", blastNotes.Distinct());
            if (grabbed.Count > 0)
                msg += "；顺手捡了：" + string.Join("、", grabbed.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key));
            if (loot.Count > 0)
                msg += "；开宝箱拿到：" + string.Join("、", loot);
            if (chestNotes.Count > 0)
                msg += "；" + string.Join("；", chestNotes);
            if (notes.Count > 0)
                msg += "；" + string.Join("；", notes.Distinct());
            if (st.Notes.Count > 0)
                msg += "；" + string.Join("；", st.Notes.Distinct());
            if (Loc is VolcanoDungeon { level.Value: 5 })
                msg += "；第 5 层是安全层：矮人商店（会矮人语才能买）、泉水";
            if (stop != null)
                msg += "；停下原因：" + stop;
            if (clutter != null)
                msg += "。" + clutter;
            yield return descended > 0 || rocks > 0 || st.Kills > 0 || loot.Count > 0 ? ActionResult.Success(msg) : ActionResult.Fail(msg);
        }
    }
}
