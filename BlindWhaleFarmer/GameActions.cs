using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Crops;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// 所有游戏内动作的实现。每个动作都是一个协程：yield null 表示等一帧，yield ActionResult 表示结束。
    /// 动作尽量走游戏自己的逻辑（BeginUsingTool、checkAction、tryToPlaceItem 等），保证效果和手动操作一致。
    /// </summary>
    internal sealed partial class GameActions
    {
        private readonly IReflectionHelper reflection;
        private readonly Observer observer;

        public GameActions(IReflectionHelper reflection, Observer observer)
        {
            this.reflection = reflection;
            this.observer = observer;
            sharedObserver = observer;
        }

        /// <summary>给静态的 Blocked 用：界面挡住动作时，把界面内容直接写进报错。</summary>
        private static Observer sharedObserver;

        private static Farmer Player => Game1.player;
        private static GameLocation Loc => Game1.currentLocation;

        // ───────────────────────── 基础工具 ─────────────────────────

        private static bool Blocked(out string why)
        {
            // 暂停/背包菜单（E、Esc 打开的 GameMenu）对 AI 干活没用，被谁按出来的都直接关掉继续
            if (Game1.activeClickableMenu is GameMenu pause && pause.readyToClose())
                pause.exitThisMenu(playSound: false);
            if (Game1.activeClickableMenu is ReadyCheckDialog ready)
            {
                why = ReadyCheckHint(ready);
                return true;
            }
            if (Game1.activeClickableMenu != null)
            {
                // 报错里直接带上界面内容和该用哪个工具，不用再 get_state 一次
                string menu = null;
                try
                {
                    menu = sharedObserver?.DescribeMenu();
                }
                catch
                {
                    // 界面读不出来就只报类型
                }
                if (menu != null && menu.Length > 700)
                    menu = menu.Substring(0, 700) + "…（get_state 看完整内容）";
                why = "有界面打开了（" + Game1.activeClickableMenu.GetType().Name + "），请先处理界面"
                    + (string.IsNullOrEmpty(menu) ? "：对话用 dialogue，其他用 close_menu" : "。当前界面：\n" + menu);
                return true;
            }
            // festival 工具在节日里自由走动、找人说话时不算被剧情挡住
            if (festivalWalk && FestivalFreeRoam())
            {
                why = null;
                return false;
            }
            if (Game1.eventUp)
            {
                why = Game1.CurrentEvent is { isFestival: true } ? "正在参加节日（节日里用 festival 工具：greet 打招呼、start 开始主活动、leave 离开）"
                    : Game1.CurrentEvent == null ? StuckEventHint
                    : "正在播放剧情事件";
                return true;
            }
            if (Game1.isWarping || Game1.fadeToBlack)
            {
                why = "正在切换场景";
                return true;
            }
            why = null;
            return false;
        }

        /// <summary>联机时游戏在等所有玩家（睡觉、进出节日、开始主活动）。</summary>
        internal static string ReadyCheckHint(ReadyCheckDialog ready)
        {
            string what = ready.checkName switch
            {
                "sleep" => "已经上床了，要所有玩家都上床才会到第二天。想接着等就再 sleep",
                "festivalStart" => "要所有玩家都走到节日才会开始。想接着等就再 festival enter",
                "festivalEnd" => "要所有玩家都同意才能离开节日。想接着等就再 festival leave",
                _ when ready.checkName.StartsWith("weddingEnd") => "婚礼放完了，要所有玩家都看完才结束，不用做别的，过一会儿再看状态",
                _ => "正在等其他玩家准备好",
            };
            return what + (ready.isCancelable() ? "；不等了就 close_menu 取消，然后可以继续干活" : "；现在不能取消");
        }

        /// <summary>联机时“等所有人”的对话框开着就等，最多 maxSeconds 秒。</summary>
        private static IEnumerable<object> WaitForPlayers(int maxSeconds)
        {
            for (int i = 0; i < maxSeconds * 60 && Game1.activeClickableMenu is ReadyCheckDialog; i++)
                yield return null;
        }

        private static IEnumerable<object> WaitTicks(int n)
        {
            for (int i = 0; i < n; i++)
                yield return null;
        }

        /// <summary>等待玩家可以行动（工具动画结束、没有在吃东西等）。</summary>
        /// <summary>等切换场景（传送、淡入淡出）真正结束，再多等几帧让新地图加载好。最多等 maxTicks 帧。</summary>
        private static IEnumerable<object> WaitSceneSettled(int maxTicks = 300)
        {
            for (int i = 0; i < maxTicks && (Game1.isWarping || Game1.fadeToBlack || Game1.globalFade || Game1.locationRequest != null); i++)
                yield return null;
            foreach (var s in WaitTicks(15))
                yield return s;
        }

        private static IEnumerable<object> WaitPlayerFree(int maxTicks = 180)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                if (!Player.UsingTool && !Player.isEating && Player.CanMove && !Player.FarmerSprite.isUsingWeapon())
                    yield break;
                yield return null;
            }
        }

        private static void SnapToTile(Point p)
        {
            Player.Position = new Vector2(p.X * 64, p.Y * 64);
        }

        private static void Face(Point target)
        {
            var me = Player.TilePoint;
            if (me == target)
                return;
            Player.faceDirection(Pathfinder.DirectionTo(me, target));
        }

        private static int WalkAnimation(int dir) => dir switch
        {
            0 => FarmerSprite.walkUp,
            1 => FarmerSprite.walkRight,
            3 => FarmerSprite.walkLeft,
            _ => FarmerSprite.walkDown
        };

        /// <summary>沿路径逐格移动（直接驱动坐标，配合走路动画）。</summary>
        private IEnumerable<object> WalkPath(List<Point> path, Outcome o)
        {
            var loc = Loc;
            // 先对齐到当前格，避免半格偏移导致碰撞
            SnapToTile(Player.TilePoint);
            for (int i = 1; i < path.Count; i++)
            {
                var target = new Vector2(path[i].X * 64, path[i].Y * 64);
                int dir = Pathfinder.DirectionTo(path[i - 1], path[i]);
                Player.faceDirection(dir);
                if (Pathfinder.ClosedDoor(loc, path[i]) != null)
                {
                    // 和手动对着门按交互键一样，走 performAction 的 "Door"（会检查好感、播放开门声）
                    Player.Halt();
                    loc.checkAction(new xLocation(path[i].X, path[i].Y), Game1.viewport, Player);
                    foreach (var w in WaitTicks(20))
                        yield return w;
                    if (Pathfinder.ClosedDoor(loc, path[i]) != null)
                    {
                        o.Fail($"({path[i].X},{path[i].Y}) 的门打不开");
                        yield break;
                    }
                }
                while (true)
                {
                    if (Blocked(out string why))
                    {
                        Player.Halt();
                        o.Fail("移动被打断：" + why);
                        yield break;
                    }
                    if (Game1.currentLocation != loc)
                    {
                        o.Fail("移动途中地点发生了变化");
                        yield break;
                    }
                    var pos = Player.Position;
                    var delta = target - pos;
                    float len = delta.Length();
                    float speed = Math.Clamp(Player.getMovementSpeed(), 2f, 10f);
                    if (len <= speed)
                    {
                        Player.Position = target;
                        break;
                    }
                    Player.Position = pos + delta / len * speed;
                    Player.FarmerSprite.animate(WalkAnimation(dir), Game1.currentGameTime);
                    yield return null;
                }
                foreach (var w in PickUpForageNearby(loc, dir))
                    yield return w;
            }
            Player.Halt();
            Player.FarmerSprite.StopAnimation();
            o.Succeed();
        }

        /// <summary>走到 goals 里任意一格。</summary>
        private IEnumerable<object> GoToAny(ICollection<Point> goals, Outcome o, string what)
        {
            if (Blocked(out string why))
            {
                o.Fail(why);
                yield break;
            }
            foreach (var _ in WaitPlayerFree())
                yield return null;
            var path = Pathfinder.FindPath(Loc, Player.TilePoint, goals);
            if (path == null)
            {
                // 镇上、山上的窄路会被新长出来的杂草、地上的野菜堵死：清掉就能过的话边走边清
                if (!InDungeon && Pathfinder.FindPath(Loc, Player.TilePoint, goals, 20000, clearable: ClearableObstacle) != null)
                {
                    foreach (var x in GoToAnyClearing(goals, o, what))
                        yield return x;
                    yield break;
                }
                o.Fail($"找不到通往{what}的路（{WhyUnreachable(goals)}）");
                yield break;
            }
            foreach (var x in WalkPath(path, o))
                yield return x;
        }

        /// <summary>这一格上有什么（报错时告诉 AI）：物件、家具、大石头树桩、树、建筑、村民、水；认不出返回 null。</summary>
        private static string WhatIsAt(Point p)
        {
            var v = new Vector2(p.X, p.Y);
            var rect = new Rectangle(p.X * 64, p.Y * 64, 64, 64);
            if (Loc.objects.TryGetValue(v, out var obj))
                return obj.DisplayName;
            // 地毯（furniture_type 12）踩得上去，不算东西
            if (Loc.furniture.FirstOrDefault(f => f.furniture_type.Value != 12 && f.GetBoundingBox().Intersects(rect)) is { } furniture)
                return furniture.DisplayName;
            if (Loc.resourceClumps.FirstOrDefault(c => c.getBoundingBox().Intersects(rect)) is { } clump)
                return Observer.ClumpName(clump);
            if (Loc.terrainFeatures.TryGetValue(v, out var tf) && !tf.isPassable(Player))
                return tf switch { FruitTree => "果树", Tree => "树", Bush => "灌木", _ => tf.GetType().Name };
            if (Loc.largeTerrainFeatures.Any(l => l.getBoundingBox().Intersects(rect)))
                return "灌木丛";
            if (Loc.getBuildingAt(v) is { } building)
                return building is ShippingBin ? "出货箱" : "建筑 " + building.buildingType.Value;
            if (Loc.characters.FirstOrDefault(c => c != null && c.TilePoint == p) is { } who)
                return who.displayName;
            return Loc.isWaterTile(p.X, p.Y) ? "水" : null;
        }

        /// <summary>center 周围 radius 格内走不了的格子上都是什么（水和认不出的墙不算），最多列 4 个。</summary>
        private static string BlockersNear(Point center, int radius)
        {
            // 大树桩、建筑占好几格：同名的只报第一格，后面写一共几格
            var found = new List<(string What, Point At)>();
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    var p = new Point(center.X + dx, center.Y + dy);
                    if (Pathfinder.IsPassable(Loc, p.X, p.Y))
                        continue;
                    string what = WhatIsAt(p);
                    if (what != null && what != "水")
                        found.Add((what, p));
                }
            var groups = found.GroupBy(f => f.What).Select(g => $"{g.Key}({g.First().At.X},{g.First().At.Y}){(g.Count() > 1 ? $" 等 {g.Count()} 格" : "")}").ToList();
            return groups.Count == 0 ? null : string.Join("、", groups.Take(4)) + (groups.Count > 4 ? " 等" : "");
        }

        /// <summary>和村民说话能站的格子：游戏只要求在他周围一圈以内（Utility.withinRadiusOfPlayer 半径 1，斜角也算）。优先上下左右，没有再用斜角。</summary>
        private static List<Point> NpcStandTiles(Point npcTile)
        {
            var orth = Pathfinder.AdjacentStandTiles(Loc, npcTile);
            if (orth.Count > 0)
                return orth;
            var diag = new List<Point>();
            foreach (var (dx, dy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                if (Pathfinder.IsPassable(Loc, npcTile.X + dx, npcTile.Y + dy))
                    diag.Add(new Point(npcTile.X + dx, npcTile.Y + dy));
            return diag;
        }

        /// <summary>玩家是否在这一格周围一圈以内（含斜角），也就是能和站在这里的村民说话。</summary>
        private static bool NextTo(Point p)
        {
            var me = Player.TilePoint;
            return me != p && Math.Abs(me.X - p.X) <= 1 && Math.Abs(me.Y - p.Y) <= 1;
        }

        /// <summary>npc 正前方隔一格的柜台：key 是站立格，value 是中间那格带 Action 的柜台。</summary>
        private Dictionary<Point, Point> CountersInFrontOf(Point npcTile)
        {
            var result = new Dictionary<Point, Point>();
            foreach (var d in Pathfinder.Dirs)
            {
                var counter = new Point(npcTile.X + d.X, npcTile.Y + d.Y);
                var stand = new Point(npcTile.X + 2 * d.X, npcTile.Y + 2 * d.Y);
                if (Loc.doesTileHaveProperty(counter.X, counter.Y, "Action", "Buildings") != null
                    && Pathfinder.IsPassable(Loc, stand.X, stand.Y))
                    result[stand] = counter;
            }
            return result;
        }

        /// <summary>走不到时，看看是不是被锁着的房门挡住了。</summary>
        private string WhyUnreachable(ICollection<Point> goals)
        {
            var viaLocked = Pathfinder.FindPath(Loc, Player.TilePoint, goals, throughLockedDoors: true);
            var owners = viaLocked?
                .Select(p => Pathfinder.ClosedDoor(Loc, p))
                .Where(d => d != null && !Pathfinder.CanOpenDoor(Loc, d))
                .SelectMany(d => d.Skip(1))
                .Distinct()
                .Select(n => Game1.getCharacterFromName(n)?.displayName ?? n)
                .ToList();
            if (owners is { Count: > 0 })
                return $"要穿过{string.Join("/", owners)}的房门，得先和{string.Join("或", owners)}有 2 心好感才能进";
            var me = Player.TilePoint;
            var (reachable, closest, dist) = Pathfinder.Explore(Loc, me, goals);
            if (reachable <= 1)
                return $"你站的 ({me.X},{me.Y}) 四周都走不了，被困住了";
            // 礼物箱（婚后的星之果实、新家的礼物）放在进门那格前面，会把门堵住，打开它就没了
            var gift = Loc.objects.Pairs.Where(p => p.Value is StardewValley.Objects.Chest { giftbox.Value: true })
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y))
                .Where(p => Util.Manhattan(p, closest) <= 1).Select(p => (Point?)p).FirstOrDefault();
            if (gift is Point g)
                return $"礼物箱 ({g.X},{g.Y}) 挡在路上：先 interact ({g.X},{g.Y}) 打开它（东西会进背包，星之果实会直接吃掉），箱子就没了，再走";
            // 村民不挡路（寻路直接穿过去），挡路的是地上的东西：说清楚是什么、在哪，AI 才知道该清掉还是绕开
            string blockers = BlockersNear(closest, 2);
            return $"被挡住了。从 ({me.X},{me.Y}) 能走到 {reachable} 格，其中离目标最近的是 ({closest.X},{closest.Y})，还差 {dist} 格"
                + (blockers != null ? $"；那附近挡着的有：{blockers}。能清的用 clear_area 或 use_tool 清掉，清不掉就换条路" : "；那附近是墙或水，换个方向或换条路");
        }

        /// <summary>走到目标格旁边（上下左右之一）并面向目标。prefer 为优先站立的格子。</summary>
        private IEnumerable<object> GoAdjacent(Point target, Outcome o, Point? prefer = null)
        {
            var me = Player.TilePoint;
            if (Util.Manhattan(me, target) == 1)
            {
                SnapToTile(me);
                Face(target);
                o.Succeed();
                yield break;
            }
            var stands = Pathfinder.AdjacentStandTiles(Loc, target);
            // 矿洞里被杂草、石头、木桶围住：边走边清
            if (NeedsClearing(target, stands))
            {
                foreach (var x in GoToAnyClearing(StandOrClearTiles(target), o, $"({target.X},{target.Y}) 旁边"))
                    yield return x;
                if (o.Ok)
                    Face(target);
                yield break;
            }
            if (prefer.HasValue && stands.Contains(prefer.Value))
            {
                var path = Pathfinder.FindPath(Loc, me, new[] { prefer.Value });
                if (path != null)
                {
                    foreach (var x in WalkPath(path, o))
                        yield return x;
                    if (o.Ok)
                        Face(target);
                    yield break;
                }
            }
            if (stands.Count == 0)
            {
                o.Fail($"({target.X},{target.Y}) 四周没有可以站的地方");
                yield break;
            }
            foreach (var x in GoToAny(stands, o, $"({target.X},{target.Y}) 旁边"))
                yield return x;
            if (o.Ok)
                Face(target);
        }

        /// <summary>选中背包某格并对 target 使用一次工具（会播放动画、消耗体力，和手动一样）。</summary>
        /// <param name="power">蓄力几档（0 = 不蓄力，只作用一格）。升级过的锄头、水壶蓄力后一次能作用一排或一片，最高档等于工具等级。</param>
        private IEnumerable<object> UseToolOnce(int toolIndex, Point target, Outcome o, int power = 0)
        {
            foreach (var _ in WaitPlayerFree())
                yield return null;
            if (Blocked(out string why))
            {
                o.Fail(why);
                yield break;
            }
            if (toolIndex < 0 || toolIndex >= Player.Items.Count || Player.Items[toolIndex] is not Tool tool)
            {
                o.Fail("背包里没有这个工具");
                yield break;
            }
            if (tool is not MeleeWeapon && Player.Stamina <= 2f)
            {
                o.Fail("体力快耗尽了，先吃东西或者去睡觉");
                yield break;
            }

            Player.CurrentToolIndex = toolIndex;
            Face(target);
            Player.lastClick = new Vector2(target.X * 64 + 32, target.Y * 64 + 32);

            if (tool is MeleeWeapon weapon)
            {
                // 和游戏里左键挥武器的流程一致
                Player.completelyStopAnimatingOrDoingAction();
                Player.CanMove = false;
                Player.UsingTool = true;
                Player.canReleaseTool = true;
                weapon.setFarmerAnimating(Player);
            }
            else
            {
                Player.toolPower.Value = 0;
                Player.toolHold.Value = 0;
                Player.BeginUsingTool();
            }
            yield return null;
            yield return null;

            // 升级过的工具会进入“蓄力”状态，等待松开按键。玩家是按住不放一档一档蓄上去的，这里直接把档位设好再松开，
            // 效果一样：游戏松开时按 toolPower 算作用范围（Tool.tilesAffected）、扣对应的体力和水
            if (tool is not MeleeWeapon && Player.UsingTool && Player.canReleaseTool)
            {
                if (power > 0)
                    Player.toolPower.Value = Math.Min(power, tool.UpgradeLevel);
                Player.EndUsingTool();
            }

            for (int i = 0; i < 180 && (Player.UsingTool || Player.FarmerSprite.isUsingWeapon()); i++)
                yield return null;
            foreach (var _ in WaitTicks(4))
                yield return null;
            o.Succeed();
        }

        /// <summary>
        /// 站好、面朝 target 以后，升级过的锄头/水壶该蓄几档。游戏自己算每一档会作用到哪些格子（铜：前方一排 3 格，钢：5 格，
        /// 金：3x3，铱：6x3），这里挑“用得上的格子最多”的那一档；有一格是 harmful（比如会锄到不该开垦的地方）的档位不用。
        /// 只多照顾到一格以下就不蓄力（返回 0）。maxPower 用来限制水壶：蓄力浇水更费水。
        /// </summary>
        private int BestToolPower(Tool tool, Point target, Func<Point, bool> wanted, Func<Point, bool> harmful, int maxPower)
        {
            int best = 0, bestUseful = 1;
            for (int power = 1; power <= Math.Min(tool.UpgradeLevel, maxPower); power++)
            {
                List<Vector2> affected;
                try
                {
                    affected = reflection.GetMethod(tool, "tilesAffected").Invoke<List<Vector2>>(new Vector2(target.X, target.Y), power, Player);
                }
                catch
                {
                    break; // 游戏改了这个方法就老老实实一格一格来
                }
                var pts = affected.Select(v => new Point((int)v.X, (int)v.Y)).ToList();
                if (harmful != null && pts.Any(harmful))
                    continue;
                int useful = pts.Count(wanted);
                if (useful > bestUseful)
                {
                    best = power;
                    bestUseful = useful;
                }
            }
            return best;
        }

        /// <summary>till_area 一格都开不了时，数一数区域里都是些什么格子，告诉 AI 下一步该干什么。</summary>
        private static string WhyNotTillable(GameLocation loc, int x1, int y1, int x2, int y2)
        {
            int notDiggable = 0, tilled = 0, cluttered = 0, blocked = 0;
            foreach (var p in RectTiles(x1, y1, x2, y2, 80))
            {
                var v = new Vector2(p.X, p.Y);
                if (loc.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back") == null)
                    notDiggable++;
                else if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt)
                    tilled++;
                else if (loc.Objects.ContainsKey(v) || loc.terrainFeatures.ContainsKey(v))
                    cluttered++;
                else
                    blocked++;
            }
            var parts = new List<string>();
            if (tilled > 0)
                parts.Add($"{tilled} 格已经耕过了（可以直接 plant）");
            if (cluttered > 0)
                parts.Add($"{cluttered} 格有杂草、石头、树枝或长草（先 clear_area）");
            if (notDiggable > 0)
                parts.Add($"{notDiggable} 格是不能开垦的地面（路、水、房子旁边、地图外）");
            if (blocked > 0)
                parts.Add($"{blocked} 格被挡住走不过去");
            return string.Join("，", parts);
        }

        private static List<Point> RectTiles(int x1, int y1, int x2, int y2, int cap)
        {
            var list = new List<Point>();
            for (int y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
                for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
                {
                    list.Add(new Point(x, y));
                    if (list.Count >= cap)
                        return list;
                }
            return list;
        }

        private static string TileName(Point p)
        {
            var (_, name, _) = Observer.ClassifyFeature(Loc, new Vector2(p.X, p.Y));
            return name;
        }

        // ───────────────────────── 移动 ─────────────────────────

        public IEnumerable<object> MoveTo(int x, int y)
        {
            var o = new Outcome();
            var target = new Point(x, y);
            if (!Loc.isTileOnMap(x, y))
            {
                yield return ActionResult.Fail($"({x},{y}) 不在当前地图内");
                yield break;
            }
            var goals = Pathfinder.IsPassable(Loc, x, y)
                ? new List<Point> { target }
                : Pathfinder.NearestStandTiles(Loc, target, 2);
            foreach (var s in GoToAny(goals, o, $"({x},{y})"))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            var me = Player.TilePoint;
            yield return ActionResult.Success(me == target ? $"已到达 ({x},{y})" : $"目标格不可站立，已走到最近的 ({me.X},{me.Y})");
        }

        public static GameLocation ResolveLocation(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            name = name.Trim();
            // 联机时农场工人的家是自己的小屋，FarmHouse 是主机的房子
            if (name.Equals("FarmHouse", StringComparison.OrdinalIgnoreCase) && !Game1.IsMasterGame && Utility.getHomeOfFarmer(Game1.player) is { } cabin)
                return cabin;
            var loc = Game1.getLocationFromName(name);
            if (loc != null)
                return loc;
            foreach (var l in Game1.locations)
            {
                if (l.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || l.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return l;
            }
            return null;
        }

        /// <summary>平常的沙滩，或者夜市那几天顶替它的地图。</summary>
        private static bool IsBeach(string name) =>
            name != null && (name.Equals("Beach", StringComparison.OrdinalIgnoreCase) || name.Equals("BeachNightMarket", StringComparison.OrdinalIgnoreCase));

        public IEnumerable<object> TravelTo(string name, bool detour = false)
        {
            var o = new Outcome();
            foreach (var s in TravelInner(name, o, askBeforeDetour: !detour))
                yield return s;
            // 回到农场：背包快满或者战利品攒多了，顺手收进箱子（收了什么由 Tools 接在结果后面）
            if (o.Ok)
                foreach (var s in StoreLootHere(storedLootNotes))
                    yield return s;
            yield return o.Ok ? ActionResult.Success(o.Message) : ActionResult.Fail(o.Message);
        }

        /// <param name="askBeforeDetour">近路被挡时停下来让 AI 选开路还是绕路（travel_to 用）。默认直接绕：回家睡觉、去节日这些顺路的走法没工夫停下来选。</param>
        private IEnumerable<object> TravelInner(string name, Outcome o, bool exactName = false, bool askBeforeDetour = false)
        {
            // exactName：name 已经是游戏里的地点名（比如跟着主机进他的 FarmHouse），不要再换成自己的小屋
            var target = exactName ? Game1.getLocationFromName(name) : ResolveLocation(name);
            if (target == null)
            {
                o.Fail($"不认识地点“{name}”，请使用英文内部名，例如 Farm、FarmHouse、Town、SeedShop、Beach、Mountain、Forest、BusStop");
                yield break;
            }
            string targetName = target.NameOrUniqueName;
            doorWaitNote = null;
            // 地图上被石头、树挡住的出口：这次不走了，换一条路线（比如农场南门堵了就经过小镇去森林）
            var blockedExits = new HashSet<string>();
            string blockedWhy = null;
            for (int hop = 0; hop < 25; hop++)
            {
                // 节日开始后地图换成了临时地图，名字是 Temp 而不是节日所在的 Town/Forest/Beach。这时不能再按名字找路：
                // 临时地图也有出口，走出去节日还留在 Temp 上，eventUp 一直是 true，人就看不见、动不了了
                if (Game1.CurrentEvent is { isFestival: true })
                {
                    if (Knowledge.TodaysFestival() is { } today && today.Location.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                        o.Succeed($"已到达 {targetName}，节日开始了");
                    else
                        o.Fail("正在参加节日，走不了。用 festival leave 离开节日后再去");
                    yield break;
                }
                if (Loc.NameOrUniqueName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    // 到达时触发了剧情（比如第一次进矿洞马龙来打招呼）：人被剧情藏到地图外，坐标是 (-99,-99) 之类，别让 AI 以为到了就接着干活
                    if (Game1.eventUp && Game1.CurrentEvent is { isFestival: false })
                    {
                        o.Succeed($"已到达 {targetName}，但一进来就触发了剧情：用 dialogue 推进对话，或者 skip_event 跳过；剧情放完之前别做别的事");
                        yield break;
                    }
                    var t = Player.TilePoint;
                    o.Succeed($"已到达 {targetName}，位置 ({t.X},{t.Y})" + (blockedWhy != null ? "（近路被挡住了，绕路来的）" : "") + doorWaitNote);
                    yield break;
                }
                // 夜市（冬 15-17）那几天，游戏把去沙滩的人送到 BeachNightMarket 这张地图，名字不是 Beach 但就是沙滩
                if (IsBeach(Loc.Name) && IsBeach(targetName))
                {
                    var t = Player.TilePoint;
                    o.Succeed($"已到达 {targetName}，位置 ({t.X},{t.Y})（夜市这几天沙滩换成了夜市地图 {Loc.Name}，就是同一个沙滩；商店 17:00 开）");
                    yield break;
                }
                // 矿洞每层都是临时地图，地图上没有出口，只能从入口梯子出去
                if (Loc is MineShaft mine)
                {
                    foreach (var s in LeaveMine(mine, o))
                        yield return s;
                    if (!o.Ok)
                        yield break;
                    continue;
                }
                // 别的地图上通往沙滩的出口写的都是 Beach，所以要去夜市地图时也按 Beach 找路，到了再认
                var route = Exits.PlanRoute(Loc, IsBeach(targetName) ? "Beach" : targetName, blockedExits);
                if (route == null || route.Count == 0)
                {
                    o.Fail(blockedWhy != null ? $"{blockedWhy}；试过的其他路线也走不通" : $"找不到从 {Loc.NameOrUniqueName} 到 {targetName} 的路线");
                    yield break;
                }
                // 节日进行中走进节日地图会直接开始节日：时间停住、普通动作都用不了，所以这里先拦下
                var fest = Knowledge.TodaysFestival();
                if (fest != null && fest.EnteringStartsFestival && !travelIntoFestival
                    && route.Any(e => e.Target.Equals(fest.Location, StringComparison.OrdinalIgnoreCase)))
                {
                    o.Fail($"今天是{fest}，现在进 {fest.Location} 会直接进入节日。想参加就用 festival enter；"
                        + $"不参加的话这条路线要经过 {fest.Location}，请等到 {Util.TimeString(fest.End)} 以后再去，或者先做不经过 {fest.Location} 的事");
                    yield break;
                }
                // 路线上有营业时间的门（铁匠铺、杂货店……）现在进不去就先别走：到了门口才发现关着是白跑一趟。
                // 快开门（两小时内）的照样出发，路上要花时间，到门口还没开就等一会儿（见 TakeExit）
                if (RouteDoorClosed(route) is string closed)
                {
                    o.Fail(closed);
                    yield break;
                }
                string exitFrom = Loc.NameOrUniqueName;
                foreach (var s in TakeExit(route[0], o))
                    yield return s;
                if (!o.Ok && o.Message.StartsWith("找不到通往") && Loc.NameOrUniqueName == exitFrom)
                {
                    blockedExits.Add(Exits.ExitKey(exitFrom, route[0]));
                    blockedWhy = o.Message;
                    if (askBeforeDetour)
                    {
                        var other = Exits.PlanRoute(Loc, targetName, blockedExits);
                        o.Fail(blockedWhy + "。你来选：① 开路：用 clear_area 清掉挡路的东西（砍树砸石要花体力和时间，但这条路以后都能走）"
                            + (other is { Count: > 0 }
                                ? $"；② 绕路：travel_to 加 detour=true，这次经过 {string.Join("→", other.Select(e => e.Target))}（不花体力但路远，下次还得绕）。看看今天的体力和时间，这条路常走就开路"
                                : "。这里没有别的路线可以绕"));
                        yield break;
                    }
                    o.Succeed();
                    continue;
                }
                if (!o.Ok)
                    yield break;
            }
            o.Fail("路线太长，没有走到");
        }

        /// <summary>这次 travel 在门口等开门花的时间，接在“已到达”后面。</summary>
        private string doorWaitNote;

        /// <summary>到门口最多等多久开门（游戏分钟）；离开门还更久就不出发。</summary>
        private const int DoorWaitMinutes = 120;

        /// <summary>路线上哪扇门今天进不去、或者还要很久才开：返回说明；都能进（或快开了）返回 null。</summary>
        private string RouteDoorClosed(List<Exit> route)
        {
            var from = Loc;
            foreach (var e in route)
            {
                string why = Exits.DoorClosedReason(from, e, out int opensAt);
                if (why != null)
                {
                    if (opensAt < 0)
                        return why + "。没有出发";
                    int minutes = Utility.CalculateMinutesBetweenTimes(Game1.timeOfDay, opensAt);
                    if (minutes > DoorWaitMinutes)
                        return $"{why}，还要 {minutes} 分钟才开，没有出发。先干别的，{Util.TimeString(opensAt)} 前后再来（路上也要花时间，可以提前一小时出发）";
                }
                from = Game1.getLocationFromName(e.Target);
                if (from == null)
                    break;
            }
            return null;
        }

        /// <summary>走到这一层的入口梯子，选“离开矿井”（回到矿洞入口或骷髅洞穴入口）。</summary>
        private IEnumerable<object> LeaveMine(MineShaft mine, Outcome o)
        {
            var below = mine.tileBeneathLadder;
            var ladder = new Point((int)below.X, (int)below.Y - 1);
            foreach (var s in GoAdjacent(ladder, o))
                yield return s;
            if (!o.Ok)
            {
                o.Fail($"走不到矿洞第 {mine.mineLevel} 层的出口梯子 ({ladder.X},{ladder.Y})：{o.Message}");
                yield break;
            }
            mine.checkAction(new xLocation(ladder.X, ladder.Y), Game1.viewport, Player);
            foreach (var s in WaitTicks(10))
                yield return s;
            int leave = Game1.activeClickableMenu is DialogueBox { isQuestion: true } db
                ? Array.FindIndex(db.responses, r => r.responseKey == "Leave") : -1;
            if (leave < 0)
            {
                o.Fail($"矿洞第 {mine.mineLevel} 层的出口梯子 ({ladder.X},{ladder.Y}) 没有反应。当前界面：{observer.DescribeMenu()}");
                yield break;
            }
            foreach (var s in Dialogue(leave))
                if (s is not ActionResult)
                    yield return s;
            for (int t = 0; t < 600 && Loc is MineShaft; t++)
                yield return null;
            foreach (var s in WaitSceneSettled())
                yield return s;
            if (Loc is MineShaft)
                o.Fail("选了离开矿井，但是没有出去");
            else
                o.Succeed();
        }

        private IEnumerable<object> TakeExit(Exit e, Outcome o)
        {
            var from = Loc;
            switch (e.Kind)
            {
                case ExitKind.Warp:
                {
                    var tile = e.Tile;
                    var goals = Loc.isTileOnMap(tile.X, tile.Y) && Pathfinder.IsPassable(Loc, tile.X, tile.Y)
                        ? new List<Point> { tile }
                        : Pathfinder.NearestStandTiles(Loc, new Point(
                            Math.Clamp(tile.X, 0, Pathfinder.MapWidth(Loc) - 1),
                            Math.Clamp(tile.Y, 0, Pathfinder.MapHeight(Loc) - 1)), 3);
                    foreach (var s in GoToAny(goals, o, $"去 {e.Target} 的出口"))
                        yield return s;
                    if (!o.Ok)
                        yield break;
                    if (e.IsTouch)
                    {
                        // 踩上去游戏会自己传送，给它一点时间，避免重复传送
                        for (int i = 0; i < 40 && Game1.currentLocation == from && !Game1.isWarping && Game1.locationRequest == null; i++)
                            yield return null;
                    }
                    if (Game1.currentLocation == from && !Game1.isWarping && Game1.locationRequest == null && Game1.activeClickableMenu is not ReadyCheckDialog)
                        Game1.warpFarmer(e.Target, e.TargetTile.X, e.TargetTile.Y, Player.FacingDirection);
                    break;
                }
                case ExitKind.Door:
                {
                    foreach (var s in GoAdjacent(e.Tile, o, new Point(e.Tile.X, e.Tile.Y + 1)))
                        yield return s;
                    if (!o.Ok)
                        yield break;
                    // 到门口还没开门、但快开了：在门口等着，开门就进
                    if (Exits.DoorClosedReason(Loc, e, out int opensAt) is string closed)
                    {
                        int minutes = opensAt < 0 ? -1 : Utility.CalculateMinutesBetweenTimes(Game1.timeOfDay, opensAt);
                        if (minutes < 0 || minutes > DoorWaitMinutes)
                        {
                            o.Fail(closed);
                            yield break;
                        }
                        int startTime = Game1.timeOfDay;
                        for (int t = 0; t < 60 * 600 && Game1.timeOfDay < opensAt; t++)
                        {
                            if ((Game1.eventUp && Game1.CurrentEvent != null) || Game1.activeClickableMenu != null)
                            {
                                o.Fail($"在 {e.Target} 门口等开门时被打断了（现在 {Util.TimeString(Game1.timeOfDay)}，{Util.TimeString(opensAt)} 开门）：出现了剧情或界面，先处理它");
                                yield break;
                            }
                            yield return null;
                        }
                        doorWaitNote = $"（到门口还没开门，等了 {Utility.CalculateMinutesBetweenTimes(startTime, Game1.timeOfDay)} 分钟）";
                    }
                    Loc.checkAction(new xLocation(e.Tile.X, e.Tile.Y), Game1.viewport, Player);
                    break;
                }
                case ExitKind.BuildingDoor:
                {
                    foreach (var s in GoAdjacent(e.Tile, o, new Point(e.Tile.X, e.Tile.Y + 1)))
                        yield return s;
                    if (!o.Ok)
                        yield break;
                    if (!e.Building.doAction(new Vector2(e.Tile.X, e.Tile.Y), Player))
                        Loc.checkAction(new xLocation(e.Tile.X, e.Tile.Y), Game1.viewport, Player);
                    break;
                }
                case ExitKind.Bus:
                {
                    // 售票机 / 沙漠里的巴士：交互后问“要不要坐车”，选“是”以后人自己走上车，巴士开走再换地图
                    if (e.IsTouch)
                    {
                        // 沙漠的巴士：踩到车门那格就弹出问题，走路会被这个界面打断，所以不看走路的结果，看问题出来没有
                        foreach (var s in GoToAny(new List<Point> { e.Tile }, o, $"去 {e.Target} 的{(Exits.IsBoat(e) ? "船" : "巴士")}"))
                            yield return s;
                        if (!o.Ok && Game1.activeClickableMenu is not DialogueBox { isQuestion: true })
                            yield break;
                        o.Succeed();
                    }
                    else
                    {
                        foreach (var s in GoAdjacent(e.Tile, o, new Point(e.Tile.X, e.Tile.Y + 1)))
                            yield return s;
                        if (!o.Ok)
                            yield break;
                        Loc.checkAction(new xLocation(e.Tile.X, e.Tile.Y), Game1.viewport, Player);
                    }
                    foreach (var s in WaitTicks(10))
                        yield return s;
                    if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask)
                    {
                        int yes = Array.FindIndex(ask.responses, r => r.responseKey == "Yes");
                        if (yes < 0)
                        {
                            o.Fail($"坐不了去 {e.Target} 的{(Exits.IsBoat(e) ? "船" : "巴士")}。当前界面：{observer.DescribeMenu()}");
                            yield break;
                        }
                        foreach (var s in Dialogue(yes))
                            if (s is not ActionResult)
                                yield return s;
                    }
                    break;
                }
            }
            // 巴士要等人走上车、开出画面才换地图，比走门慢得多
            int waitTicks = Exits.IsBoat(e) ? 4800 : e.Kind == ExitKind.Bus ? 2400 : 600;

            // 联机时走进节日地图不会马上传送：游戏先等所有玩家都到（确认后才传送）
            if (Game1.activeClickableMenu is ReadyCheckDialog ready)
            {
                foreach (var s in WaitForPlayers(300))
                    yield return s;
                if (Game1.activeClickableMenu == ready)
                {
                    o.Fail($"等了 5 分钟，其他玩家还没到，已经在 {from.NameOrUniqueName} 门口停下。{ReadyCheckHint(ready)}");
                    yield break;
                }
            }

            // 等待场景切换完成
            for (int i = 0; i < waitTicks; i++)
            {
                yield return null;
                if (Game1.currentLocation != from && !Game1.isWarping && !Game1.fadeToBlack && Game1.locationRequest == null)
                {
                    foreach (var s in WaitTicks(10))
                        yield return s;
                    // 到站后巴士还要开进来、人下车，这段时间动不了
                    if (e.Kind == ExitKind.Bus)
                    {
                        for (int w = 0; w < 900 && (!Player.CanMove || Game1.freezeControls || Player.controller != null || Game1.globalFade || Game1.fadeToBlack); w++)
                            yield return null;
                        foreach (var s in WaitTicks(20))
                            yield return s;
                    }
                    o.Succeed();
                    yield break;
                }
                if (Game1.activeClickableMenu is DialogueBox db && Game1.currentLocation == from)
                {
                    string text = db.getCurrentString();
                    db.closeDialogue();
                    o.Fail(Exits.IsBoat(e)
                        ? $"坐不了去 {e.Target} 的船：{text}（去姜岛的船票 1000g；船要先用 repair_boat 修好）"
                        : e.Kind == ExitKind.Bus
                        ? $"坐不了去 {e.Target} 的巴士：{text}（去沙漠要 500g 车票，帕姆在巴士站台时才发车，大约 10:00-17:00）"
                        : $"进不去 {e.Target}：{text}");
                    yield break;
                }
                // 门上贴的告示（比如威利的“钓鱼去也。明日归”）是信件样式的界面，不是对话框
                if (Game1.activeClickableMenu != null && Game1.currentLocation == from && !Game1.isWarping && i > 10)
                {
                    string text = observer.DescribeMenu().Trim();
                    Game1.activeClickableMenu.exitThisMenu(playSound: false);
                    o.Fail($"进不去 {e.Target}，门口弹出了：{text}");
                    yield break;
                }
            }
            o.Fail($"没能进入 {e.Target}（门可能锁着或者还没开门）");
        }

        // ───────────────────────── 工具和交互 ─────────────────────────

        public IEnumerable<object> UseTool(string toolSpec, int x, int y, int times)
        {
            int idx = Util.FindToolIndex(toolSpec);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有工具“{toolSpec}”");
                yield break;
            }
            var target = new Point(x, y);
            var o = new Outcome();
            // 对着池塘中间灌水：那格四周全是水站不了人，改成去最近的岸边灌
            if (Player.Items[idx] is WateringCan && Loc.CanRefillWateringCanOnTile(x, y)
                && Pathfinder.AdjacentStandTiles(Loc, target).Count == 0)
            {
                foreach (var s in RefillWateringCan(idx, o))
                    yield return s;
                yield return o.Ok
                    ? ActionResult.Success($"({x},{y}) 在水中间够不着，{o.Message}")
                    : ActionResult.Fail(o.Message);
                yield break;
            }
            foreach (var s in GoAdjacent(target, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            string before = TileName(target);
            times = Math.Clamp(times, 1, 30);
            int used = 0;
            for (int i = 0; i < times; i++)
            {
                foreach (var s in UseToolOnce(idx, target, o))
                    yield return s;
                if (!o.Ok)
                    break;
                used++;
                // 目标已经被清除就不用再敲了
                if (before != null && TileName(target) != before)
                    break;
            }
            string after = TileName(target) ?? "空地";
            string msg = $"对 ({x},{y}) 使用 {Player.Items[idx]?.DisplayName} {used} 次，目标从“{before ?? "空地"}”变为“{after}”，剩余体力 {(int)Player.Stamina}";
            // 砍倒树/敲碎石头后把掉落物捡起来（树要倒地后才掉木头，多等一会儿）
            if (before != null && after != before && o.Ok && !Blocked(out _))
            {
                var pickup = new List<string>();
                foreach (var s in CollectDebris(new Rectangle(x - 4, y - 4, 9, 9), pickup, before.Contains("树") ? 150 : 45))
                    yield return s;
                if (pickup.Count > 0)
                    msg += "；" + string.Join("；", pickup);
            }
            if (Player.Items[idx] is WateringCan && !Loc.CanRefillWateringCanOnTile(x, y)
                && !(Loc.terrainFeatures.TryGetValue(new Vector2(x, y), out var tf) && tf is HoeDirt))
                msg += "（这格既不是水面也不是耕地，水壶没起作用；灌水要对着水面格，浇水用 water_crops）";
            yield return o.Ok ? ActionResult.Success(msg) : ActionResult.Fail(msg + "；" + o.Message);
        }

        public IEnumerable<object> Interact(int? x, int? y, string npcName, string itemSpec)
        {
            var o = new Outcome();
            NPC npc = null;
            if (!string.IsNullOrWhiteSpace(npcName))
            {
                npc = Loc.characters.FirstOrDefault(c => c.Name.Equals(npcName, StringComparison.OrdinalIgnoreCase) || c.displayName.Equals(npcName, StringComparison.OrdinalIgnoreCase));
                if (npc == null)
                {
                    // 不在这张地图：和 give_gift 一样先找到他在哪，再走过去
                    var villager = FindVillager(npcName);
                    var where = villager?.currentLocation;
                    if (villager == null || where == null)
                    {
                        yield return ActionResult.Fail($"找不到叫“{npcName}”的人（名字可以用中文或英文）");
                        yield break;
                    }
                    var go = new Outcome();
                    foreach (var s in TravelInner(where.NameOrUniqueName, go))
                        yield return s;
                    if (!go.Ok)
                    {
                        yield return ActionResult.Fail($"{villager.displayName} 现在在 {where.NameOrUniqueName}，走过去失败：{go.Message}");
                        yield break;
                    }
                    npc = Loc.characters.FirstOrDefault(c => c == villager);
                    if (npc == null)
                    {
                        yield return ActionResult.Fail($"到了 {Loc.NameOrUniqueName}，但 {villager.displayName} 已经走到别处了");
                        yield break;
                    }
                }
            }
            else if (!x.HasValue || !y.HasValue)
            {
                yield return ActionResult.Fail("需要提供坐标 x,y 或 npc 名字");
                yield break;
            }

            int itemIdx = -1;
            if (!string.IsNullOrWhiteSpace(itemSpec))
            {
                itemIdx = Util.FindItemIndex(itemSpec);
                if (itemIdx < 0)
                {
                    yield return ActionResult.Fail($"背包里没有“{itemSpec}”");
                    yield break;
                }
            }

            // 站在柜台后面的人（皮埃尔、格斯、克林特、冈瑟…）旁边没有能站的格子，或者能站的格子在柜台里面走不进去：
            // 和手动一样隔着柜台点柜台
            var npcStands = npc != null ? NpcStandTiles(npc.TilePoint) : null;
            if (npc != null && (npcStands.Count == 0 || Pathfinder.FindPath(Loc, Player.TilePoint, npcStands) == null))
            {
                var counters = CountersInFrontOf(npc.TilePoint);
                if (counters.Count > 0 && itemIdx >= 0)
                {
                    yield return ActionResult.Fail($"{npc.displayName} 在柜台后面，隔着柜台没法递东西；等他从柜台后面出来再送");
                    yield break;
                }
                if (counters.Count > 0)
                {
                    foreach (var s in GoToAny(counters.Keys, o, $"{npc.displayName} 的柜台前"))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail(o.Message);
                        yield break;
                    }
                    var counter = counters[Player.TilePoint];
                    Face(counter);
                    var menuBefore = Game1.activeClickableMenu;
                    bool used = Loc.checkAction(new xLocation(counter.X, counter.Y), Game1.viewport, Player);
                    foreach (var s in WaitTicks(25))
                        yield return s;
                    string note = $"{npc.displayName} 在柜台后面，隔着柜台只能用柜台的功能；想聊天或送礼要等他从柜台后面出来";
                    if (Game1.activeClickableMenu != null && Game1.activeClickableMenu != menuBefore)
                        yield return ActionResult.Success($"{note}。点了柜台，打开了界面：\n{observer.DescribeMenu()}");
                    else
                        yield return ActionResult.Fail(used ? $"{note}（点了柜台但没有打开界面）" : $"{note}（柜台现在没反应）");
                    yield break;
                }
            }

            // NPC 会走动，最多追 4 次
            Point target = npc?.TilePoint ?? new Point(x.Value, y.Value);
            // 家具常常占好几格（电视 2x2），传的那一格四周可能都是墙：换成家具上旁边能站人、离得最近的一格
            if (npc == null)
            {
                var piece = Loc.furniture.FirstOrDefault(f => f.furniture_type.Value != 12 && f.GetBoundingBox().Contains(target.X * 64, target.Y * 64));
                if (piece != null)
                {
                    var bb = piece.GetBoundingBox();
                    var me = Player.TilePoint;
                    var tiles = new List<Point>();
                    for (int tx = bb.Left / 64; tx <= (bb.Right - 1) / 64; tx++)
                        for (int ty = bb.Top / 64; ty <= (bb.Bottom - 1) / 64; ty++)
                            tiles.Add(new Point(tx, ty));
                    var reachable = tiles.Where(t => Pathfinder.AdjacentStandTiles(Loc, t).Count > 0).OrderBy(t => Util.Manhattan(t, me)).ToList();
                    if (reachable.Count > 0 && !reachable.Contains(target))
                        target = reachable[0];
                }
            }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (npc != null)
                    target = npc.TilePoint;
                if (Player.TilePoint == target && npc == null)
                    break;
                if (npc != null)
                {
                    if (NextTo(npc.TilePoint))
                        break;
                    var stands = NpcStandTiles(target);
                    if (stands.Count == 0)
                    {
                        yield return ActionResult.Fail($"{npc.displayName} 在 ({target.X},{target.Y})，四周（包括斜角）都没有能站的地方，等他走开再试");
                        yield break;
                    }
                    foreach (var s in GoToAny(stands, o, $"{npc.displayName} 旁边"))
                        yield return s;
                }
                else
                {
                    foreach (var s in GoAdjacent(target, o))
                        yield return s;
                }
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
                if (npc == null || NextTo(npc.TilePoint))
                    break;
            }
            if (npc != null)
            {
                target = npc.TilePoint;
                Face(target);
            }

            if (itemIdx >= 0)
                Player.CurrentToolIndex = itemIdx;
            else if (Player.ActiveObject != null)
            {
                // 避免手里拿着东西时误送礼/误放置：换成一个工具或空格
                int safe = Util.FindTool<Tool>();
                if (safe < 0)
                    safe = Player.Items.IndexOf(null);
                if (safe >= 0)
                    Player.CurrentToolIndex = safe;
            }

            var before = Game1.activeClickableMenu;
            var fromLoc = Loc;
            var v = new Vector2(target.X, target.Y);
            bool ok;
            if (npc != null)
                ok = npc.checkAction(Player, Loc);
            else
            {
                var who = Loc.isCharacterAtTile(v);
                ok = who != null && who.checkAction(Player, Loc);
                // 游戏里家具只有按右键才打开（电视选台、坐椅子），左键是把桌上的东西拿走；
                // checkAction 认不出这是右键，所以家具在这里直接按右键处理
                var furniture = ok ? null : Loc.furniture.FirstOrDefault(f => f.furniture_type.Value != 12 && f.GetBoundingBox().Contains(target.X * 64, target.Y * 64));
                if (furniture != null)
                    ok = (Player.ActiveObject != null && furniture.performObjectDropInAction(Player.ActiveObject, false, Player)) || furniture.checkForAction(Player);
                if (!ok && furniture == null)
                    ok = Loc.checkAction(new xLocation(target.X, target.Y), Game1.viewport, Player);
                if (!ok && Loc.terrainFeatures.TryGetValue(v, out var tf))
                    ok = tf.performUseAction(v);
            }

            foreach (var s in WaitTicks(25))
                yield return s;

            if (Game1.activeClickableMenu != null && Game1.activeClickableMenu != before)
                yield return ActionResult.Success("交互后打开了界面：\n" + observer.DescribeMenu());
            else if (Game1.currentLocation != fromLoc)
                yield return ActionResult.Success($"进入了 {Game1.currentLocation.NameOrUniqueName}");
            else if (ok)
                yield return ActionResult.Success($"与 ({target.X},{target.Y}) 交互成功");
            else if (npc != null && Player.friendshipData.TryGetValue(npc.Name, out var friend) && friend.TalkedToToday)
                yield return ActionResult.Fail($"{npc.displayName} 今天已经聊过了，没有新的话（每人每天只能聊一次；买东西用 open_shop，送礼用 give_gift）");
            else if (npc != null)
                yield return ActionResult.Fail($"{npc.displayName} 现在没有反应（可能在忙或者不想说话）");
            else
                yield return ActionResult.Fail($"({target.X},{target.Y}) " + WhyNothingHappened(target));
        }

        /// <summary>交互没反应时说清楚为什么：机器还在加工、机器空着、手里拿着干草但点的不是食槽……</summary>
        private static string WhyNothingHappened(Point target)
        {
            if (Loc.objects.TryGetValue(new Vector2(target.X, target.Y), out var obj) && obj.bigCraftable.Value)
            {
                if (obj.heldObject.Value != null && obj.MinutesUntilReady > 0)
                    return $"的{obj.DisplayName}还在加工{obj.heldObject.Value.DisplayName}，还要约 {obj.MinutesUntilReady} 分钟（游戏时间）";
                if (obj.heldObject.Value == null)
                    return $"的{obj.DisplayName}是空的：要放材料就用 interact 带上 item（比如熔炉放 5 个矿石，背包里还要有煤炭）";
            }
            if (Player.ActiveObject?.QualifiedItemId == "(O)178" && Loc is AnimalHouse)
            {
                var troughs = TroughTiles(Loc).Where(p => !Loc.objects.ContainsKey(new Vector2(p.X, p.Y))).ToList();
                return troughs.Count == 0
                    ? "不是空食槽，这里的食槽都放满了干草"
                    : $"不是食槽。这里的空食槽在：{string.Join("、", troughs.Select(p => $"({p.X},{p.Y})"))}（care_animals 会自动用背包里的干草放满）";
            }
            string here = WhatIsAt(target);
            string nearby = NearbyInteractables(target, 3);
            return (here != null ? $"是{here}，交互没有反应" : "没有可以交互的东西")
                + (nearby != null ? $"。附近 3 格内能交互的：{nearby}" : "。附近 3 格内也没有能交互的东西，get_state 的“附近物体”里有坐标");
        }

        /// <summary>center 周围 radius 格内能 interact 的东西：村民、机器和箱子、家具、地图上带动作的格子（门、告示、柜台）。</summary>
        private static string NearbyInteractables(Point center, int radius)
        {
            var found = new List<(int Dist, string Text)>();
            void Add(Point p, string name)
            {
                if (p != center && Math.Abs(p.X - center.X) <= radius && Math.Abs(p.Y - center.Y) <= radius)
                    found.Add((Util.Manhattan(p, center), $"{name}({p.X},{p.Y})"));
            }
            foreach (var c in Loc.characters.Where(c => c != null && !c.IsMonster && !c.IsInvisible))
                Add(c.TilePoint, c.displayName);
            foreach (var pair in Loc.objects.Pairs.Where(p => p.Value.bigCraftable.Value || p.Value.IsSpawnedObject))
                Add(new Point((int)pair.Key.X, (int)pair.Key.Y), pair.Value.DisplayName);
            foreach (var f in Loc.furniture.Where(f => f.furniture_type.Value != 12))
                Add(new Point((int)f.TileLocation.X, (int)f.TileLocation.Y), f.DisplayName);
            for (int x = center.X - radius; x <= center.X + radius; x++)
                for (int y = center.Y - radius; y <= center.Y + radius; y++)
                    if (Loc.isTileOnMap(x, y) && Loc.doesTileHaveProperty(x, y, "Action", "Buildings") is { } action)
                        Add(new Point(x, y), "地图动作 " + action.Split(' ')[0]);
            return found.Count == 0 ? null : string.Join("、", found.OrderBy(f => f.Dist).Take(6).Select(f => f.Text));
        }

        public IEnumerable<object> PlaceItem(string itemSpec, int x, int y, bool force = false)
        {
            int idx = Util.FindItemIndex(itemSpec, i => i is SObject);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有可放置的“{itemSpec}”");
                yield break;
            }
            // 机器、箱子随手一放就会压在田里、堵在门口；集中放在一片
            if (!force && PlacementProblem(Loc, Player.Items[idx], new Point(x, y)) is string problem)
            {
                yield return ActionResult.Fail($"{problem}。{MachineAreaHint(Loc)}。{ForceHint}");
                yield break;
            }
            // 洒水器放歪了等于白放：浇不到耕地，或者田里有明显更好的位置时先拦下来，告诉她放哪
            // force 时也算一遍，成功消息里的“能浇到 N 格”要准
            string badSpot = SprinklerProblem(Loc, Player.Items[idx], new Point(x, y), out int sprinklerGain);
            if (!force && badSpot != null)
            {
                // 种着作物的格子 force 也放不上去，不提 force
                bool onCrop = Loc.terrainFeatures.TryGetValue(new Vector2(x, y), out var under) && under is HoeDirt { crop: not null };
                yield return ActionResult.Fail(onCrop ? badSpot : $"{badSpot}。{ForceHint}");
                yield break;
            }
            var o = new Outcome();
            var target = new Point(x, y);
            foreach (var s in GoAdjacent(target, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            Player.CurrentToolIndex = idx;
            var item = (SObject)Player.Items[idx];
            string name = item.DisplayName;
            bool isSprinkler = item.IsSprinkler();
            bool placed = Utility.tryToPlaceItem(Loc, item, x * 64 + 32, y * 64 + 32);
            foreach (var s in WaitTicks(10))
                yield return s;
            yield return placed
                ? ActionResult.Success($"已在 ({x},{y}) 放置/种下 {name}" + (isSprinkler ? $"，能浇到 {sprinklerGain} 格耕地" : ""))
                : ActionResult.Fail($"不能在 ({x},{y}) 放置 {name}（种子需要空耕地且当季可种；物品需要空地）");
        }

        // ───────────────────────── 批量农活 ─────────────────────────

        /// <summary>
        /// till_area 入口：给了矩形就开矩形；只给 count（或什么都不给）就挨着最大的田往空地最多的一边扩 count 格。
        /// 农场、温室这些种得了庄稼的地方以外不开（锄了也种不了，force 才开）。
        /// </summary>
        public IEnumerable<object> TillArea(int? x1, int? y1, int? x2, int? y2, int? count, bool force)
        {
            var loc = Loc;
            bool canPlant = loc.IsFarm || loc.IsGreenhouse || (loc.GetData()?.CanPlantHere ?? false);
            if (!canPlant && !force)
            {
                yield return ActionResult.Fail($"{loc.DisplayName} 种不了庄稼（只有农场、温室、姜岛农场这类地方能播种），在这里锄地没用。要开田先 travel_to Farm。{ForceHint}");
                yield break;
            }
            if (x1.HasValue && y1.HasValue && x2.HasValue && y2.HasValue)
            {
                foreach (var s in TillArea(x1.Value, y1.Value, x2.Value, y2.Value))
                    yield return s;
                yield break;
            }
            if (x1.HasValue || y1.HasValue || x2.HasValue || y2.HasValue)
            {
                yield return ActionResult.Fail("坐标要给全 x1,y1,x2,y2；或者都不给、只给 count，挨着现有的田自动扩");
                yield break;
            }
            var main = Fields(loc).FirstOrDefault(f => f.Tiles.Count >= 4);
            if (main == null)
            {
                yield return ActionResult.Fail("这里还没有成片的田，没法“挨着扩”：第一块田要给坐标 x1,y1,x2,y2（挑离家、出货箱近，方方正正的空地）");
                yield break;
            }
            int want = Math.Clamp(count ?? 20, 1, 80);
            var best = BestExpansion(loc, main, want);
            if (best.n == 0)
            {
                yield return ActionResult.Fail($"最大的田 {main.Describe()}，四周都是树、大树桩大石头或者不能开垦的地面，没法挨着扩：给坐标另挑地方开一块");
                yield break;
            }
            foreach (var s in TillArea(best.r.Left, best.r.Top, best.r.Right - 1, best.r.Bottom - 1))
                yield return s is ActionResult { Ok: true } r ? ActionResult.Success($"挨着田往{best.side}扩。" + r.Message.Substring(3)) : s;
        }

        public IEnumerable<object> TillArea(int x1, int y1, int x2, int y2)
        {
            int hoe = Util.FindTool<Hoe>();
            if (hoe < 0)
            {
                yield return ActionResult.Fail("没有锄头");
                yield break;
            }
            var loc = Loc;
            // 先把区域里的杂草、长草、小石头、树枝、树桩、小树苗清掉，不然这些格子会被跳过，田中间留洞（树和大树桩大石头不动）
            string clearNote = null;
            if (RectTiles(x1, y1, x2, y2, 80).Any(p => FieldClutter(loc, p)))
            {
                ActionResult cleared = null;
                foreach (var s in ClearArea(x1, y1, x2, y2, includeTrees: false))
                {
                    if (s is ActionResult r)
                        cleared = r;
                    else
                        yield return s;
                }
                if (cleared != null)
                    clearNote = "先清理了区域里的杂物：" + cleared.Message.Substring(3) + "。";
                if (Blocked(out _))
                {
                    yield return ActionResult.Fail(clearNote + "清理时被打断了");
                    yield break;
                }
            }
            // 新田不必挨着老田：不同作物可以分块种，每块挑方便的地方。只是另开一块时老田还空着不少，提醒一句（空耕地几天不种会消失）
            string emptyFieldNote = null;
            if (loc is Farm)
            {
                var want = new Rectangle(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);
                var fields = Fields(loc).Where(f => f.Tiles.Count >= 4).ToList();
                int empty = fields.Where(f => RectGap(want, f.Box) > 1).Sum(f => f.Empty); // 隔一格以内会并成同一块田（和 Fields 的分法一致）
                if (empty >= 6)
                    emptyFieldNote = $"（别的田里还有 {empty} 格空耕地没种，记得种上，空着几天会自己消失）";
            }
            var tiles = RectTiles(x1, y1, x2, y2, 80).Where(p =>
                loc.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back") != null
                && !loc.Objects.ContainsKey(new Vector2(p.X, p.Y))
                && !loc.terrainFeatures.ContainsKey(new Vector2(p.X, p.Y))
                && Pathfinder.IsPassable(loc, p.X, p.Y)).ToList();
            if (tiles.Count == 0)
            {
                yield return ActionResult.Fail(clearNote + "区域内没有可以开垦的空地：" + WhyNotTillable(loc, x1, y1, x2, y2));
                yield break;
            }
            string err = null;
            var o = new Outcome();
            var todo = new HashSet<Point>(tiles);
            bool IsTilled(Point t) => loc.terrainFeatures.TryGetValue(new Vector2(t.X, t.Y), out var f) && f is HoeDirt;
            // 蓄力时会多锄到的格子：能开垦、却不在这次要开垦的范围里的空地，不能碰
            bool WouldTillOutside(Point t) => !todo.Contains(t) && !IsTilled(t)
                && loc.doesTileHaveProperty(t.X, t.Y, "Diggable", "Back") != null
                && !loc.Objects.ContainsKey(new Vector2(t.X, t.Y)) && !loc.terrainFeatures.ContainsKey(new Vector2(t.X, t.Y));
            int charged = 0;
            foreach (var p in Util.NearestOrder(Player.TilePoint, tiles))
            {
                if (IsTilled(p))
                    continue; // 前面蓄力的一锄头已经顺带开了
                foreach (var s in GoAdjacent(p, o))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    if (Blocked(out _))
                        break;
                    continue;
                }
                // 升级过的锄头蓄力，一次开一排或一片
                int power = Player.Items[hoe] is Hoe h && h.UpgradeLevel > 0
                    ? BestToolPower(h, p, t => todo.Contains(t) && !IsTilled(t), WouldTillOutside, 4) : 0;
                if (power > 0)
                    charged++;
                foreach (var s in UseToolOnce(hoe, p, o, power))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    break;
                }
            }
            int done = tiles.Count(IsTilled);
            var tilled = tiles.Where(IsTilled).ToList();
            string range = tilled.Count > 0 ? $"，范围 ({tilled.Min(t => t.X)},{tilled.Min(t => t.Y)})-({tilled.Max(t => t.X)},{tilled.Max(t => t.Y)})" : "";
            string msg = clearNote + $"开垦了 {done}/{tiles.Count} 格{range}{(charged > 0 ? $"（蓄力 {charged} 次）" : "")}，剩余体力 {(int)Player.Stamina}";
            // 区域里还有没锄上的格子（树、大树桩、大石头、走不到的）：说清楚在哪、为什么，免得 AI 以为整块都开好了
            var holes = RectTiles(x1, y1, x2, y2, 80)
                .Where(p => loc.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back") != null && !IsTilled(p)
                    // 洒水器、稻草人、箱子、小路、果树是故意放的，不算洞
                    && Observer.ClassifyFeature(loc, new Vector2(p.X, p.Y)).ch is not ('X' or '&' or '_' or 'F'))
                .Select(p => (p, what: HoleName(loc, p))).ToList();
            if (holes.Count > 0)
                msg += "；区域里有 " + holes.Count + " 格没开出来：" + string.Join("、", holes.GroupBy(h => h.what).Select(g => $"{g.Key} {string.Join(" ", g.Take(4).Select(h => $"({h.p.X},{h.p.Y})"))}{(g.Count() > 4 ? "…" : "")}"))
                    + "（树要砍就 clear_area include_trees=true；大树桩、大石头要升级过的斧头镐子）";
            yield return done > 0 ? ActionResult.Success(msg + (err != null ? "；部分失败：" + err : "") + emptyFieldNote) : ActionResult.Fail(msg + "；" + err);
        }

        public IEnumerable<object> PlantSeeds(string seedSpec, int? x1, int? y1, int? x2, int? y2, int max)
        {
            int idx = Util.FindItemIndex(seedSpec, i => i is SObject);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有“{seedSpec}”");
                yield break;
            }
            var seed = Player.Items[idx];
            string seedName = seed.DisplayName;
            var loc = Loc;
            var tiles = loc.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt hd && hd.crop == null && !loc.Objects.ContainsKey(p.Key))
                .Where(p => !(seed.Category == SObject.SeedsCategory) || ((HoeDirt)p.Value).canPlantThisSeedHere(seed.ItemId))
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y))
                .Where(p => !x1.HasValue || (p.X >= Math.Min(x1.Value, x2 ?? x1.Value) && p.X <= Math.Max(x1.Value, x2 ?? x1.Value)
                                             && p.Y >= Math.Min(y1 ?? 0, y2 ?? y1 ?? 0) && p.Y <= Math.Max(y1 ?? 0, y2 ?? y1 ?? 0)))
                .ToList();
            if (tiles.Count == 0)
            {
                yield return ActionResult.Fail("没有可以播种的空耕地（先用 till_area 开垦；注意种子要当季）");
                yield break;
            }

            // 没给区域时按田来种：先把已经有作物的田补满、再种大块的田，一块种完再换下一块；零散的一两格耕地不种。
            // 以前是从近到远挑空耕地，种子会撒得到处都是，浇水收菜都要满农场跑。
            string skippedNote = null, apartNote = null;
            List<Point> order;
            if (!x1.HasValue && seed.Category == SObject.SeedsCategory)
            {
                var set = new HashSet<Point>(tiles);
                var fields = Fields(loc);
                // 同样的作物种在一块：先种已经种着这种作物的田，再种空着的田（新作物自己占一块），最后才往种着别的作物的田里补
                string seedId = seed.ItemId;
                var ranked = fields.Where(f => f.Tiles.Any(set.Contains))
                    .OrderByDescending(f => f.Tiles.Count >= 4)
                    .ThenByDescending(f => f.SeedIds.Contains(seedId))
                    .ThenByDescending(f => f.Crops == 0)
                    .ThenByDescending(f => f.Tiles.Count)
                    .ThenBy(f => f.Tiles.Min(p => Util.Manhattan(p, Player.TilePoint)))
                    .ToList();
                bool hasRealField = ranked.Any(f => f.Tiles.Count >= 4);
                // 有种着这种作物或者空着的田，就不往种着别的作物的田里补（不同作物分开种）；一块这样的田都没有时才补空位
                bool Fits(Field f) => f.SeedIds.Contains(seedId) || f.Crops == 0;
                bool keepApart = ranked.Any(f => (f.Tiles.Count >= 4 || !hasRealField) && Fits(f));
                order = new List<Point>();
                int skipped = 0;
                var otherCropFields = new List<Field>();
                foreach (var f in ranked)
                {
                    var mine = f.Tiles.Where(set.Contains).ToList();
                    if (hasRealField && f.Tiles.Count < 4)
                    {
                        skipped += mine.Count;
                        continue;
                    }
                    if (keepApart && !Fits(f))
                    {
                        otherCropFields.Add(f);
                        continue;
                    }
                    // 一块田里一行一行种（蛇形），比“最近的下一格”整齐
                    order.AddRange(mine.OrderBy(p => p.Y).ThenBy(p => p.Y % 2 == 0 ? p.X : -p.X));
                }
                if (skipped > 0)
                    skippedNote = $"零散的 {skipped} 格耕地没种（离田太远，种了不好照顾；真要种就传 x1,y1,x2,y2 指定区域）";
                if (otherCropFields.Count > 0)
                    apartNote = $"种着别的作物的田（{string.Join("；", otherCropFields.Take(3).Select(f => f.Describe()))}）里的空位没往里种，不同作物分开种。"
                        + "种子还有剩就 till_area 给它开一块新田"
                        + (loc is Farm && SuggestFieldExpansion(loc) is string ext2 ? $"（比如{ext2}）" : "")
                        + "，或者传 x1,y1,x2,y2 指定就种在那里";
            }
            else
                order = Util.NearestOrder(Player.TilePoint, tiles).ToList();

            int done = 0;
            string err = null;
            var o = new Outcome();
            foreach (var p in order.Take(Math.Clamp(max, 1, 200)))
            {
                if (Player.Items[idx] == null || Player.Items[idx].DisplayName != seedName)
                {
                    err = "种子用完了";
                    break;
                }
                foreach (var s in GoAdjacent(p, o))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    if (Blocked(out _))
                        break;
                    continue;
                }
                Player.CurrentToolIndex = idx;
                if (Utility.tryToPlaceItem(loc, (SObject)Player.Items[idx], p.X * 64 + 32, p.Y * 64 + 32))
                    done++;
                else
                    err = $"({p.X},{p.Y}) 种不下去（可能不是当季种子）";
                foreach (var s in WaitTicks(4))
                    yield return s;
            }
            string msg = $"种下了 {done} 格 {seedName}";
            if (skippedNote != null)
                msg += "；" + skippedNote;
            // 种子还有剩、又因为“分开种”没往别的田里补：说清楚剩多少、怎么办
            int seedsLeft = Player.Items[idx] is { } rest && rest.DisplayName == seedName ? rest.Stack : 0;
            if (apartNote != null && seedsLeft > 0)
                msg += $"；还剩 {seedsLeft} 颗{seedName}：" + apartNote;
            // 新种下的当天也要浇水才算长了一天：AI 常常早上浇完一轮、下午买了种子种下就不管了
            int thirsty = loc.terrainFeatures.Pairs.Count(p => p.Value is HoeDirt hd && Observer.NeedsWater(hd));
            if (done > 0 && thirsty > 0)
                msg += $"。这里有 {thirsty} 株没浇水（包括刚种的），今天浇了才算长一天：water_crops";
            if (done == 0 && err == null)
                err = "没有能种的耕地" + (loc is Farm && SuggestFieldExpansion(loc) is string ext ? "，" + ext : "");
            yield return done > 0 ? ActionResult.Success(msg + (err != null ? "；" + err : "")) : ActionResult.Fail(msg + "；" + err);
        }

        private IEnumerable<object> RefillWateringCan(int canIdx, Outcome o)
        {
            var loc = Loc;
            var me = Player.TilePoint;
            var water = new List<Point>();
            for (int x = me.X - 50; x <= me.X + 50; x++)
                for (int y = me.Y - 50; y <= me.Y + 50; y++)
                    if (loc.isTileOnMap(x, y) && loc.CanRefillWateringCanOnTile(x, y))
                        water.Add(new Point(x, y));
            if (water.Count == 0)
            {
                o.Fail("附近找不到可以灌水的水源");
                yield break;
            }
            foreach (var w in water.OrderBy(p => Util.Manhattan(p, me)).Take(12))
            {
                if (Pathfinder.AdjacentStandTiles(loc, w).Count == 0)
                    continue;
                foreach (var s in GoAdjacent(w, o))
                    yield return s;
                if (!o.Ok)
                    continue;
                foreach (var s in UseToolOnce(canIdx, w, o))
                    yield return s;
                if (Player.Items[canIdx] is WateringCan can && can.WaterLeft > 0)
                {
                    o.Succeed($"在 ({w.X},{w.Y}) 灌满了水壶");
                    yield break;
                }
            }
            o.Fail("没能给水壶灌水");
        }

        public IEnumerable<object> WaterCrops(int max)
        {
            int canIdx = Util.FindTool<WateringCan>();
            if (canIdx < 0)
            {
                yield return ActionResult.Fail("没有水壶");
                yield break;
            }
            var loc = Loc;
            var tiles = loc.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt hd && Observer.NeedsWater(hd))
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y)).ToList();
            if (tiles.Count == 0)
            {
                yield return ActionResult.Success("这里没有需要浇水的作物");
                yield break;
            }
            string err = null;
            var o = new Outcome();
            bool Thirsty(Point t) => loc.terrainFeatures.TryGetValue(new Vector2(t.X, t.Y), out var f) && f is HoeDirt d && Observer.NeedsWater(d);
            int charged = 0;
            foreach (var p in Util.NearestOrder(Player.TilePoint, tiles).Take(Math.Clamp(max, 1, 300)))
            {
                if (!Thirsty(p))
                    continue; // 前面蓄力的一壶水已经顺带浇到了
                if (Player.Items[canIdx] is WateringCan can && can.WaterLeft <= 0)
                {
                    foreach (var s in RefillWateringCan(canIdx, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        err = o.Message;
                        break;
                    }
                }
                foreach (var s in GoAdjacent(p, o))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    if (Blocked(out _))
                        break;
                    continue;
                }
                // 升级过的水壶蓄力，一次浇一排或一片（多浇到的空地没坏处）。蓄几档就多费几格水，水不够就少蓄
                int power = Player.Items[canIdx] is WateringCan wc && wc.UpgradeLevel > 0
                    ? BestToolPower(wc, p, Thirsty, null, wc.WaterLeft - 1) : 0;
                if (power > 0)
                    charged++;
                foreach (var s in UseToolOnce(canIdx, p, o, power))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    break;
                }
            }
            int left = loc.terrainFeatures.Pairs.Count(p => p.Value is HoeDirt hd && Observer.NeedsWater(hd));
            int done = tiles.Count - tiles.Count(Thirsty);
            string msg = $"浇了 {done} 株{(charged > 0 ? $"（蓄力 {charged} 次）" : "")}，还剩 {left} 株没浇，剩余体力 {(int)Player.Stamina}";
            yield return done > 0 ? ActionResult.Success(msg + (err != null ? "；" + err : "")) : ActionResult.Fail(msg + "；" + err);
        }

        /// <summary>
        /// 收完菜、清完地以后的下一步提示：背包里能卖的作物多少、空位多少。AI 常常收完就去干别的，东西一直堆在背包里。
        /// </summary>
        private static string AfterGatherNote()
        {
            var crops = Player.Items.OfType<SObject>().Where(i => i.Category is SObject.VegetableCategory or SObject.FruitsCategory
                or SObject.flowersCategory or SObject.GreensCategory).ToList();
            int free = Player.freeSpotsInInventory();
            var parts = new List<string> { $"背包空位 {free} 个" };
            if (crops.Count > 0)
                parts.Add($"身上有 {crops.Sum(c => c.Stack)} 个作物/野菜（{string.Join("、", crops.Select(c => c.DisplayName).Distinct().Take(6))}）：今天要卖的 ship_items 放出货箱，留着做菜送礼的 tidy_backpack 存箱子");
            else if (free <= 4)
                parts.Add("背包快满了，tidy_backpack 存一下");
            return string.Join("；", parts);
        }

        public IEnumerable<object> HarvestCrops(int max)
        {
            var loc = Loc;
            var tiles = loc.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt hd && hd.crop != null && hd.readyForHarvest())
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y)).ToList();
            // 季节种子（野生种子）成熟时游戏把作物删掉，在耕地上换成一个采集物：按作物找不到，一起捡
            bool WildOnSoil(Point t) => loc.objects.TryGetValue(new Vector2(t.X, t.Y), out var w) && IsGrabbable(w)
                && loc.terrainFeatures.TryGetValue(new Vector2(t.X, t.Y), out var under) && under is HoeDirt;
            var wild = loc.objects.Keys.Select(k => new Point((int)k.X, (int)k.Y)).Where(WildOnSoil).ToList();
            tiles.AddRange(wild);
            if (tiles.Count == 0)
            {
                yield return ActionResult.Success("这里没有可以收获的作物");
                yield break;
            }
            int wildPicked = 0;
            int scythe = Util.FindTool<MeleeWeapon>(w => w.isScythe());
            int done = 0;
            string err = null;
            var o = new Outcome();
            foreach (var p in Util.NearestOrder(Player.TilePoint, tiles).Take(Math.Clamp(max, 1, 300)))
            {
                if (Player.isInventoryFull())
                {
                    err = "背包满了";
                    break;
                }
                var v = new Vector2(p.X, p.Y);
                if (WildOnSoil(p))
                {
                    foreach (var s in GoAdjacent(p, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        err = o.Message;
                        if (Blocked(out _))
                            break;
                        continue;
                    }
                    Player.Halt();
                    Face(p);
                    loc.checkAction(new xLocation(p.X, p.Y), Game1.viewport, Player);
                    foreach (var s in WaitPlayerFree(60))
                        yield return s;
                    if (!loc.objects.ContainsKey(v))
                        wildPicked++;
                    continue;
                }
                if (!loc.terrainFeatures.TryGetValue(v, out var tf) || tf is not HoeDirt hd || hd.crop == null || !hd.readyForHarvest())
                    continue;
                foreach (var s in GoAdjacent(p, o))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    if (Blocked(out _))
                        break;
                    continue;
                }
                if (hd.crop.GetHarvestMethod() == HarvestMethod.Scythe)
                {
                    if (scythe < 0)
                    {
                        err = "这种作物要用镰刀收割，但背包里没有镰刀";
                        continue;
                    }
                    foreach (var s in UseToolOnce(scythe, p, o))
                        yield return s;
                }
                else
                {
                    if (Player.ActiveObject != null)
                    {
                        int safe = Util.FindTool<Tool>();
                        if (safe >= 0)
                            Player.CurrentToolIndex = safe;
                    }
                    if (!loc.checkAction(new xLocation(p.X, p.Y), Game1.viewport, Player))
                        hd.performUseAction(v);
                    foreach (var s in WaitTicks(15))
                        yield return s;
                    foreach (var s in WaitPlayerFree())
                        yield return s;
                }
                if (!(loc.terrainFeatures.TryGetValue(v, out var after) && after is HoeDirt hd2 && hd2.crop != null && hd2.readyForHarvest()))
                    done++;
            }
            string msg = done > 0 || wildPicked == 0 ? $"收获了 {done} 株" : "";
            if (wildPicked > 0)
                msg += (msg.Length > 0 ? "，" : "") + $"捡了 {wildPicked} 个季节种子长出来的野菜";
            if (done + wildPicked > 0)
                msg += "。" + AfterGatherNote();
            done += wildPicked;
            yield return done > 0 ? ActionResult.Success(msg + (err != null ? "；" + err : "")) : ActionResult.Fail(msg + "；" + err);
        }

        /// <summary>能捡进背包的掉落物（木头、石头、纤维、种子等；不含砍树时飞溅的碎屑特效）。</summary>
        private static bool IsPickupDebris(Debris d) =>
            d.Chunks.Count > 0 && (d.item != null || !string.IsNullOrEmpty(d.itemId.Value))
            && d.debrisType.Value is Debris.DebrisType.OBJECT or Debris.DebrisType.RESOURCE or Debris.DebrisType.ARCHAEOLOGY or Debris.DebrisType.CHUNKS;

        private static Point DebrisTile(Debris d)
        {
            var sum = Vector2.Zero;
            foreach (var c in d.Chunks)
                sum += c.position.Value;
            var avg = sum / d.Chunks.Count;
            return new Point((int)(avg.X + 32) / 64, (int)(avg.Y + 32) / 64);
        }

        internal static bool CanAccept(Debris d) =>
            d.item != null ? Player.couldInventoryAcceptThisItem(d.item) : Player.couldInventoryAcceptThisItem(d.itemId.Value, 1, d.itemQuality);

        internal static string DebrisName(Debris d) =>
            d.item?.DisplayName ?? ItemRegistry.GetDataOrErrorItem(d.itemId.Value).DisplayName;

        /// <summary>
        /// 把 area 里的掉落物捡起来：游戏只在掉落物落地停稳后、玩家在磁吸半径（默认 2 格）内时才吸过来，
        /// 所以走到每个掉落物旁边等它被吸走。背包放不下的不去追，记进 notes。
        /// </summary>
        private IEnumerable<object> CollectDebris(Rectangle area, List<string> notes, int settleTicks = 45)
        {
            // 先等最后一下敲出来的东西落地
            foreach (var s in WaitTicks(settleTicks))
                yield return s;
            var loc = Loc;
            var skip = new HashSet<Debris>();
            var o = new Outcome();
            for (int round = 0; round < 40; round++)
            {
                if (Loc != loc || Blocked(out _))
                    break;
                var pending = loc.debris.Where(d => IsPickupDebris(d) && !skip.Contains(d) && area.Contains(DebrisTile(d))).ToList();
                var full = pending.Where(d => !CanAccept(d)).ToList();
                if (full.Count > 0)
                {
                    notes.Add($"背包满了，{string.Join("、", full.Select(DebrisName).Distinct())} 留在地上（腾出背包后走过去就会自动捡起）");
                    foreach (var d in full)
                        skip.Add(d);
                    pending = pending.Except(full).ToList();
                }
                if (pending.Count == 0)
                    break;
                var me = Player.TilePoint;
                var target = pending.OrderBy(d => Util.Manhattan(DebrisTile(d), me)).First();
                var tile = DebrisTile(target);
                if (Util.Manhattan(tile, me) > 1)
                {
                    var goals = Pathfinder.IsPassable(loc, tile.X, tile.Y)
                        ? new List<Point> { tile }
                        // 落在墙根、石头上走不到：人身边 2 格（128 像素，Farmer.GetAppliedMagneticRadius 的最小值）内的东西会自己飞过来，站到 2 格内就行
                        : Pathfinder.NearestStandTiles(loc, tile, 2);
                    foreach (var s in GoToAny(goals, o, "掉落物"))
                        yield return s;
                    if (!o.Ok)
                    {
                        skip.Add(target);
                        continue;
                    }
                }
                for (int i = 0; i < 90 && loc.debris.Contains(target); i++)
                    yield return null;
                if (loc.debris.Contains(target))
                    skip.Add(target);
            }
            var stranded = skip.Where(d => loc.debris.Contains(d) && CanAccept(d)).Select(DebrisName).Distinct().ToList();
            if (stranded.Count > 0)
                notes.Add($"{string.Join("、", stranded)} 走不过去，没捡到");
        }

        /// <summary>不给坐标时清自己周围（左右上下各 radius 格）；坐标只给了一半算参数错误。</summary>
        public IEnumerable<object> ClearArea(int? x1, int? y1, int? x2, int? y2, int? radius, bool includeTrees)
        {
            int given = new[] { x1, y1, x2, y2 }.Count(v => v.HasValue);
            if (given != 0 && given != 4)
            {
                yield return ActionResult.Fail("clear_area 的 x1、y1、x2、y2 要么四个都给，要么都不给（不给就清你周围）");
                yield break;
            }
            if (given == 0)
            {
                int r = Math.Clamp(radius ?? 4, 1, 8);
                var me = Player.TilePoint;
                x1 = me.X - r; y1 = me.Y - r; x2 = me.X + r; y2 = me.Y + r;
            }
            foreach (var s in ClearArea(x1.Value, y1.Value, x2.Value, y2.Value, includeTrees))
                yield return s;
        }

        public IEnumerable<object> ClearArea(int x1, int y1, int x2, int y2, bool includeTrees)
        {
            var loc = Loc;
            int axe = Util.FindTool<Axe>(), pick = Util.FindTool<Pickaxe>(), hoe = Util.FindTool<Hoe>();
            int scythe = Util.FindTool<MeleeWeapon>(w => w.isScythe());

            // 每格：用哪个工具、最多敲几下
            var jobs = new List<(Point p, int tool, int maxHits)>();
            foreach (var p in RectTiles(x1, y1, x2, y2, 400))
            {
                var v = new Vector2(p.X, p.Y);
                var (ch, _, _) = Observer.ClassifyFeature(loc, v);
                switch (ch)
                {
                    case 'o': jobs.Add((p, pick, 8)); break;
                    case 'w': jobs.Add((p, scythe >= 0 ? scythe : axe, 2)); break;
                    case '/': jobs.Add((p, axe, 2)); break;
                    case '?': jobs.Add((p, hoe, 2)); break;
                    case 'd': jobs.Add((p, scythe >= 0 ? scythe : axe, 2)); break;
                    case '"': if (scythe >= 0) jobs.Add((p, scythe, 2)); break;
                    case 't': jobs.Add((p, axe, 8)); break; // 长到第 3、4 阶段的树苗有 10 点血，初始斧头一下 2 点，要 5 下
                    case 'S': jobs.Add((p, axe, 10)); break;
                    case 'T': if (includeTrees) jobs.Add((p, axe, 25)); break;
                }
            }
            jobs.RemoveAll(j => j.tool < 0);
            if (jobs.Count == 0)
            {
                // 只说区域里真有的：大型障碍物（树桩、原木、大石头）要升级的斧头/镐子，树要 include_trees
                var left = RectTiles(x1, y1, x2, y2, 400).Select(p => Observer.ClassifyFeature(loc, new Vector2(p.X, p.Y)).Item1).ToList();
                var notes = new List<string>();
                if (left.Contains('R'))
                    notes.Add("大型障碍物 R（树桩、原木、大石头）要升级过的斧头或镐子");
                if (left.Contains('T') && !includeTrees)
                    notes.Add("有树，要砍就加 include_trees");
                yield return ActionResult.Success("区域内没有可以清理的东西" + (notes.Count > 0 ? "（" + string.Join("；", notes) + "）" : ""));
                yield break;
            }
            jobs = jobs.Take(60).ToList();
            var order = Util.NearestOrder(Player.TilePoint, jobs.Select(j => j.p));
            int cleared = 0;
            string err = null;
            var o = new Outcome();
            foreach (var p in order)
            {
                var job = jobs.First(j => j.p == p);
                string before = TileName(p);
                if (before == null)
                    continue;
                foreach (var s in GoAdjacent(p, o))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    if (Blocked(out _))
                        break;
                    continue;
                }
                for (int hit = 0; hit < job.maxHits; hit++)
                {
                    foreach (var s in UseToolOnce(job.tool, p, o))
                        yield return s;
                    if (!o.Ok || TileName(p) != before)
                        break;
                }
                if (!o.Ok)
                {
                    err = o.Message;
                    break;
                }
                if (TileName(p) != before)
                    cleared++;
            }
            string msg = $"清理了 {cleared}/{order.Count} 处，剩余体力 {(int)Player.Stamina}";
            if (jobs.Count >= 60)
                msg += "（一次最多处理 60 处，区域里可能还有）";
            // 敲完马上走开时掉落物还在弹，不会被吸过来：最后回头把区域里的掉落物捡一遍
            if (!Blocked(out _))
            {
                var pickup = new List<string>();
                foreach (var s in CollectDebris(new Rectangle(Math.Min(x1, x2) - 2, Math.Min(y1, y2) - 2, Math.Abs(x2 - x1) + 5, Math.Abs(y2 - y1) + 5), pickup))
                    yield return s;
                if (pickup.Count > 0)
                    msg += "；" + string.Join("；", pickup);
            }
            // 清出来的木头、石头、纤维一直堆在背包里，下一件事就没地方装了
            if (cleared > 0 && Player.freeSpotsInInventory() <= 4)
                msg += $"；背包空位只剩 {Player.freeSpotsInInventory()} 个，用 tidy_backpack 把木头石头这些存进箱子";
            yield return cleared > 0 ? ActionResult.Success(msg + (err != null ? "；" + err : "")) : ActionResult.Fail(msg + "；" + err);
        }

        // ───────────────────────── 物品 ─────────────────────────

        public IEnumerable<object> ShipItems(string itemSpec, int? count, bool force = false)
        {
            int idx = Util.FindItemIndex(itemSpec);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有“{itemSpec}”");
                yield break;
            }
            if (!force && ReservedChecker()(Player.Items[idx]) is string reservedWhy)
            {
                yield return ActionResult.Fail($"{Player.Items[idx].DisplayName} 是{reservedWhy}的，卖了就交不了，先留着（交完再卖多的）。{ForceHint}");
                yield break;
            }
            if (!Player.Items[idx].canBeShipped())
            {
                yield return ActionResult.Fail($"{Player.Items[idx].DisplayName} 不能出货");
                yield break;
            }
            var o = new Outcome();
            if (Loc is not Farm)
            {
                foreach (var s in TravelInner("Farm", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }
            var farm = (Farm)Loc;
            var bin = farm.buildings.OfType<ShippingBin>().OrderBy(b => Util.Manhattan(new Point(b.tileX.Value, b.tileY.Value), Player.TilePoint)).FirstOrDefault();
            if (bin == null)
            {
                yield return ActionResult.Fail("农场上找不到出货箱");
                yield break;
            }
            var binTile = new Point(bin.tileX.Value, bin.tileY.Value);
            if (Vector2.Distance(Player.Tile, new Vector2(bin.tileX.Value + 0.5f, bin.tileY.Value)) > 2f)
            {
                var stands = new List<Point>();
                for (int dx = 0; dx < bin.tilesWide.Value; dx++)
                    stands.AddRange(Pathfinder.AdjacentStandTiles(farm, new Point(binTile.X + dx, binTile.Y)));
                foreach (var s in GoToAny(stands, o, "出货箱"))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }

            idx = Util.FindItemIndex(itemSpec);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有“{itemSpec}”了");
                yield break;
            }
            var item = Player.Items[idx];
            int n = Math.Clamp(count ?? item.Stack, 1, item.Stack);
            string name = item.DisplayName;
            int price = Utility.getSellToStorePriceOfItem(item, false) * n;
            if (n >= item.Stack)
                farm.shipItem(item, Player);
            else
            {
                var part = item.getOne();
                part.Stack = n;
                item.Stack -= n;
                farm.getShippingBin(Player).Add(part);
                farm.lastItemShipped = part;
                bin.showShipment(part);
            }
            foreach (var s in WaitTicks(10))
                yield return s;
            yield return ActionResult.Success($"已把 {n} 个 {name} 放进出货箱（预计今晚结算约 {price}g）");
        }

        /// <summary>扔进垃圾桶（和背包界面里拖到垃圾桶一样，升级过垃圾桶会退一点钱）：背包满了又不在箱子旁边时腾位置用。</summary>
        public IEnumerable<object> TrashItem(string itemSpec, int? count, bool force = false)
        {
            int idx = Util.FindItemIndex(itemSpec, i => i is not Tool && i.canBeTrashed());
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有能扔的“{itemSpec}”（工具、武器、任务物品不能扔）");
                yield break;
            }
            if (!force && ReservedChecker()(Player.Items[idx]) is string reservedWhy)
            {
                yield return ActionResult.Fail($"{Player.Items[idx].DisplayName} 是{reservedWhy}的，别扔，扔别的（石头、泥土、杂物）。{ForceHint}");
                yield break;
            }
            var item = Player.Items[idx];
            int n = Math.Clamp(count ?? item.Stack, 1, item.Stack);
            string name = item.DisplayName;
            Item gone;
            if (n >= item.Stack)
            {
                gone = item;
                Player.Items[idx] = null;
            }
            else
            {
                gone = item.getOne();
                gone.Stack = n;
                item.Stack -= n;
            }
            int money = Player.Money;
            Utility.trashItem(gone);
            string refund = Player.Money > money ? $"，垃圾桶退了 {Player.Money - money}g" : "";
            yield return ActionResult.Success($"扔掉了 {name} x{n}{refund}。背包空位 {Player.Items.Count(i => i == null)} 个");
        }

        public IEnumerable<object> Eat(string itemSpec, bool force = false)
        {
            int idx = Util.FindItemIndex(itemSpec, i => i is SObject o && o.Edibility != -300);
            if (idx < 0)
            {
                // 列出能吃的（格号和回复多少体力），省得一格一格去试；要交的不列
                var reservedCheck = ReservedChecker();
                var edible = Player.Items
                    .Select((item, i) => (item, i))
                    .Where(x => x.item is SObject o && o.Edibility > 0 && reservedCheck(x.item) == null)
                    .OrderByDescending(x => ((SObject)x.item).staminaRecoveredOnConsumption())
                    .Take(8)
                    .Select(x => $"{x.i}:{x.item.DisplayName}（+{((SObject)x.item).staminaRecoveredOnConsumption()} 体力）")
                    .ToList();
                yield return ActionResult.Fail($"背包里没有可以吃的“{itemSpec}”。"
                    + (edible.Count > 0 ? "能吃的：" + string.Join("、", edible) : "背包里没有能吃的东西"));
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            if (!force && ReservedChecker()(Player.Items[idx]) is string reservedWhy)
            {
                yield return ActionResult.Fail($"{Player.Items[idx].DisplayName} 是{reservedWhy}的，吃了就交不了，换别的吃（get_state 背包里标了【留着】的都别吃）。{ForceHint}");
                yield break;
            }
            foreach (var s in WaitPlayerFree())
                yield return s;
            string name = Player.Items[idx].DisplayName;
            float before = Player.Stamina;
            Player.CurrentToolIndex = idx;
            // eatHeldObject 会吃 mostRecentlyGrabbedItem，不一致时会把它换到手上，所以先对齐
            Player.mostRecentlyGrabbedItem = Player.Items[idx];
            Player.isEating = false;
            Player.eatHeldObject();
            for (int i = 0; i < 300 && (Player.isEating || Player.FarmerSprite.PauseForSingleAnimation); i++)
                yield return null;
            foreach (var s in WaitTicks(20))
                yield return s;
            yield return ActionResult.Success($"吃了 {name}，体力 {(int)before} → {(int)Player.Stamina}");
        }

        // ───────────────────────── 商店 / 制作 ─────────────────────────

        private static readonly Dictionary<string, string> ShopByLocation = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SeedShop"] = "SeedShop",
            ["Blacksmith"] = "Blacksmith",
            ["ScienceHouse"] = "Carpenter",
            ["FishShop"] = "FishShop",
            ["Saloon"] = "Saloon",
            ["JojaMart"] = "Joja",
            ["AnimalShop"] = "AnimalShop",
            ["AdventureGuild"] = "AdventureShop",
            ["Hospital"] = "Hospital",
        };

        public IEnumerable<object> OpenShop(string shopId)
        {
            if (string.IsNullOrWhiteSpace(shopId) && !ShopByLocation.TryGetValue(Loc.Name, out shopId))
            {
                yield return ActionResult.Fail("这里没有商店（皮埃尔的店在 SeedShop，铁匠在 Blacksmith，木匠在 ScienceHouse，威利在 FishShop）");
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            bool opened = Utility.TryOpenShopMenu(shopId, Loc);
            foreach (var s in WaitTicks(15))
                yield return s;
            if (opened && Game1.activeClickableMenu is ShopMenu)
                yield return ActionResult.Success("已打开商店：\n" + observer.DescribeMenu());
            else if (Game1.activeClickableMenu is DialogueBox db)
                yield return ActionResult.Fail("商店没开：" + db.getCurrentString());
            else
                yield return ActionResult.Fail("商店没开（店主不在或者不在营业时间）");
        }

        private static ISalable FindSalable(ShopMenu shop, string spec)
        {
            if (int.TryParse(spec, out int i))
            {
                var visible = shop.forSale.Where(s => shop.itemPriceAndStock.ContainsKey(s)).ToList();
                return i >= 0 && i < visible.Count ? visible[i] : null;
            }
            return shop.forSale.FirstOrDefault(s => s.DisplayName.Equals(spec, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(spec, StringComparison.OrdinalIgnoreCase))
                ?? shop.forSale.FirstOrDefault(s => s.DisplayName.Contains(spec, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(spec, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<object> Buy(string spec, int count)
        {
            if (Game1.activeClickableMenu is not ShopMenu shop)
            {
                yield return ActionResult.Fail("没有打开商店，先用 open_shop");
                yield break;
            }
            var salable = FindSalable(shop, spec);
            if (salable == null || !shop.itemPriceAndStock.TryGetValue(salable, out var info))
            {
                yield return ActionResult.Fail($"商店里没有“{spec}”");
                yield break;
            }
            count = Math.Clamp(count, 1, 999);
            if (info.Stock != int.MaxValue)
                count = Math.Min(count, info.Stock);
            if (count <= 0)
            {
                yield return ActionResult.Fail("已经卖完了");
                yield break;
            }
            int moneyBefore = Player.Money;
            if (info.Price * count > moneyBefore && info.Price > 0)
            {
                count = moneyBefore / info.Price;
                if (count <= 0)
                {
                    yield return ActionResult.Fail($"钱不够（单价 {info.Price}g，你只有 {moneyBefore}g）");
                    yield break;
                }
            }
            string name = salable.DisplayName;
            reflection.GetMethod(shop, "tryToPurchaseItem").Invoke<bool>(salable, null, count, 0, 0);
            if (shop.heldItem is Item held)
            {
                if (!Player.addItemToInventoryBool(held))
                {
                    Game1.createItemDebris(held, Player.getStandingPosition(), Player.FacingDirection);
                    shop.heldItem = null;
                    yield return ActionResult.Success($"买了 {name}，但背包满了，掉在了地上");
                    yield break;
                }
                shop.heldItem = null;
            }
            yield return null;
            int spent = moneyBefore - Player.Money;
            yield return spent > 0 || info.Price == 0
                ? ActionResult.Success($"买了 {count} 个 {name}，花费 {spent}g，剩余 {Player.Money}g")
                : ActionResult.Fail($"没买成 {name}（钱或交换物不够，或者不能购买）");
        }

        public IEnumerable<object> Sell(string itemSpec, int? count)
        {
            if (Game1.activeClickableMenu is not ShopMenu shop)
            {
                yield return ActionResult.Fail("没有打开商店，先用 open_shop");
                yield break;
            }
            int idx = Util.FindItemIndex(itemSpec);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有“{itemSpec}”");
                yield break;
            }
            var item = Player.Items[idx];
            if (!shop.highlightItemToSell(item) || shop.currency != 0)
            {
                yield return ActionResult.Fail($"这家店不收 {item.DisplayName}（大部分作物可以放进农场出货箱）");
                yield break;
            }
            int n = Math.Clamp(count ?? item.Stack, 1, item.Stack);
            int price = Utility.getSellToStorePriceOfItem(item, false) * n;
            string name = item.DisplayName;
            Player.Money += price;
            if (n >= item.Stack)
                Player.removeItemFromInventory(item);
            else
                item.Stack -= n;
            Game1.playSound("sell");
            yield return null;
            yield return ActionResult.Success($"卖出 {n} 个 {name}，得到 {price}g，现在有 {Player.Money}g");
        }

        public IEnumerable<object> Craft(string recipeSpec, int count)
        {
            string key = Player.craftingRecipes.Keys.FirstOrDefault(k => k.Equals(recipeSpec, StringComparison.OrdinalIgnoreCase))
                ?? Player.craftingRecipes.Keys.FirstOrDefault(k => new CraftingRecipe(k).DisplayName.Equals(recipeSpec, StringComparison.OrdinalIgnoreCase))
                ?? Player.craftingRecipes.Keys.FirstOrDefault(k => k.Contains(recipeSpec, StringComparison.OrdinalIgnoreCase) || new CraftingRecipe(k).DisplayName.Contains(recipeSpec, StringComparison.OrdinalIgnoreCase));
            if (key == null)
            {
                string known = string.Join("、", Player.craftingRecipes.Keys.Select(k => $"{new CraftingRecipe(k).DisplayName}({k})"));
                yield return ActionResult.Fail($"不会制作“{recipeSpec}”。已学会的配方：{known}");
                yield break;
            }
            var recipe = new CraftingRecipe(key);
            int made = 0;
            for (int i = 0; i < Math.Clamp(count, 1, 50); i++)
            {
                if (!recipe.doesFarmerHaveIngredientsInInventory())
                    break;
                var item = recipe.createItem();
                if (!Player.couldInventoryAcceptThisItem(item))
                    break;
                recipe.consumeIngredients(null);
                Player.addItemToInventoryBool(item);
                // 与制作界面一致：计数、任务进度、成就
                Player.craftingRecipes[key] += recipe.numberProducedPerCraft;
                var crafted = item;
                Player.NotifyQuests(q => q.OnRecipeCrafted(recipe, crafted));
                Game1.stats.checkForCraftingAchievements();
                made++;
            }
            yield return null;
            if (made > 0)
                yield return ActionResult.Success($"制作了 {made} 个 {recipe.DisplayName}");
            else
            {
                string need = string.Join("、", recipe.recipeList.Select(kv => $"{Util.ObjectName(kv.Key)} x{kv.Value}"));
                yield return ActionResult.Fail($"材料不够或背包满了。{recipe.DisplayName} 需要：{need}");
            }
        }

        // ───────────────────────── 界面 ─────────────────────────

        /// <summary>给孩子、刚出生的动物起名：和手动输入后按回车一样。</summary>
        public IEnumerable<object> SetName(string name)
        {
            if (Game1.activeClickableMenu is not NamingMenu naming)
            {
                yield return ActionResult.Fail("现在没有要起名的界面");
                yield break;
            }
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 12)
            {
                yield return ActionResult.Fail("名字要 1~12 个字");
                yield break;
            }
            naming.textBox.Text = name;
            naming.textBoxEnter(naming.textBox);
            foreach (var s in WaitTicks(20))
                yield return s;
            yield return ActionResult.Success("起好名字了：" + name + "。当前界面：" + observer.DescribeMenu());
        }

        public IEnumerable<object> Dialogue(int? choice)
        {
            // 剧情放到一半常有几秒没有对话框（比如婚礼，又不能跳过），等一下下一句，别让 AI 空转
            for (int i = 0; i < 8 * 60 && Game1.activeClickableMenu == null && Game1.eventUp && Game1.CurrentEvent is { isFestival: false }; i++)
                yield return null;
            if (Game1.activeClickableMenu is ReadyCheckDialog ready)
            {
                yield return ActionResult.Fail(ReadyCheckHint(ready));
                yield break;
            }
            if (Game1.activeClickableMenu is not DialogueBox db)
            {
                yield return ActionResult.Fail(Game1.eventUp && Game1.CurrentEvent is { isFestival: false } ? "剧情还在放，现在没有对话框，过一会儿再推进" : "现在没有对话框");
                yield break;
            }
            if (choice.HasValue && (!db.isQuestion || choice.Value < 0 || choice.Value >= db.responses.Length))
            {
                yield return ActionResult.Fail("没有这个选项");
                yield break;
            }
            string text = db.getCurrentString();
            bool clicked = false;
            for (int i = 0; i < 600; i++)
            {
                if (Game1.activeClickableMenu != db)
                    break;
                string cur = db.getCurrentString();
                // 翻到了下一页，或者问题已经回答完：停下，让 AI 看新内容
                if (clicked && (cur != text || (choice.HasValue && !db.isQuestion)))
                    break;
                if (!db.transitioning && db.safetyTimer <= 0)
                {
                    if (db.isQuestion)
                    {
                        if (!choice.HasValue)
                        {
                            yield return ActionResult.Fail("这是个选择题，请用 choice 选择一个选项");
                            yield break;
                        }
                        db.selectedResponse = choice.Value;
                    }
                    db.receiveLeftClick(0, 0);
                    clicked = true;
                }
                yield return null;
            }
            foreach (var s in WaitTicks(20))
                yield return s;
            yield return ActionResult.Success("对话已推进。当前界面：" + observer.DescribeMenu());
        }

        public IEnumerable<object> MenuClick(int? index, string name = null)
        {
            var menu = Game1.activeClickableMenu;
            if (menu == null)
            {
                yield return ActionResult.Fail("没有打开的界面");
                yield break;
            }
            var comps = Observer.MenuComponents(menu);
            string elements = $"当前界面 {menu.GetType().Name} 可点击的元素：\n{Observer.ListComponents(menu)}";
            if (index == null && string.IsNullOrWhiteSpace(name))
            {
                yield return ActionResult.Fail("要传 index（元素序号）或 name（元素名字）。" + elements);
                yield break;
            }
            if (index == null)
            {
                // 按名字找：先找名字完全一样的，没有再找包含这个词的
                string want = name.Trim();
                var exact = Enumerable.Range(0, comps.Count)
                    .Where(i => Observer.ComponentLabel(comps[i]).Split(" / ").Any(p => p.Equals(want, StringComparison.OrdinalIgnoreCase))).ToList();
                var hits = exact.Count > 0 ? exact
                    : Enumerable.Range(0, comps.Count).Where(i => Observer.ComponentLabel(comps[i]).Contains(want, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0)
                {
                    yield return ActionResult.Fail($"界面里没有名字带“{want}”的元素。{elements}");
                    yield break;
                }
                if (hits.Count > 1)
                {
                    yield return ActionResult.Fail($"名字带“{want}”的元素有 {hits.Count} 个：{string.Join("；", hits.Take(10).Select(i => $"{i}: {Observer.ComponentLabel(comps[i])}"))}。用 index 传序号点其中一个");
                    yield break;
                }
                index = hits[0];
            }
            if (index < 0 || index >= comps.Count)
            {
                yield return ActionResult.Fail($"没有序号 {index} 的元素。{elements}");
                yield break;
            }
            var c = comps[index.Value];
            menu.receiveLeftClick(c.bounds.Center.X, c.bounds.Center.Y);
            foreach (var s in WaitTicks(20))
                yield return s;
            yield return ActionResult.Success($"点击了 {c.name}。当前界面：{observer.DescribeMenu()}");
        }

        public IEnumerable<object> CloseMenu()
        {
            var menu = Game1.activeClickableMenu;
            if (menu == null)
            {
                yield return ActionResult.Success("本来就没有界面");
                yield break;
            }
            switch (menu)
            {
                case DialogueBox:
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;
                    break;
                case LevelUpMenu lvl when !lvl.isProfessionChooser:
                    lvl.okButtonClicked();
                    break;
                case LevelUpMenu:
                    yield return ActionResult.Fail("需要先用 choose_profession 选择职业");
                    yield break;
                case ShippingMenu:
                    reflection.GetMethod(menu, "okClicked").Invoke();
                    break;
                case ReadyCheckDialog ready when !ready.isCancelable():
                    yield return ActionResult.Fail(ReadyCheckHint(ready));
                    yield break;
                // 钓鱼宝箱、背包满时弹的“拿走物品”界面：直接关掉东西会掉在地上，先拿进背包
                case ItemGrabMenu grab when grab.source != ItemGrabMenu.source_chest && grab.context is not StardewValley.Objects.Chest
                                            && grab.ItemsToGrabMenu?.actualInventory.Any(i => i != null) == true:
                {
                    var taken = new List<string>();
                    var dropped = GrabAll(grab, taken);
                    yield return ActionResult.Success((taken.Count > 0 ? $"拿进背包：{string.Join("、", taken)}" : "一样都没拿进背包")
                        + (dropped.Count > 0 ? $"；背包满了，{string.Join("、", dropped)} 掉在脚边（空出位置走过去能捡回来）" : ""));
                    yield break;
                }
                default:
                    // 铁匠正在砸晶球：等砸完，否则开出来的东西就没了
                    for (int i = 0; i < 300 && menu is GeodeMenu { geodeAnimationTimer: > 0 }; i++)
                        yield return null;
                    if (menu.readyToClose())
                        menu.exitThisMenu();
                    else
                    {
                        // 关不了多半是手上还拿着东西：先放回背包，直接清掉界面东西就丢了
                        menu.emergencyShutDown();
                        Game1.exitActiveMenu();
                    }
                    break;
            }
            for (int i = 0; i < 120 && Game1.activeClickableMenu == menu; i++)
                yield return null;
            yield return ActionResult.Success("已关闭。当前界面：" + observer.DescribeMenu());
        }

        public IEnumerable<object> ChooseProfession(string side)
        {
            if (Game1.activeClickableMenu is not LevelUpMenu lvl || !lvl.isProfessionChooser)
            {
                yield return ActionResult.Fail("现在没有职业选择界面");
                yield break;
            }
            List<int> choices = null;
            for (int i = 0; i < 120; i++)
            {
                choices = reflection.GetField<List<int>>(lvl, "professionsToChoose").GetValue();
                if (choices.Count >= 2)
                    break;
                yield return null;
            }
            if (choices == null || choices.Count < 2)
            {
                yield return ActionResult.Fail("职业选项还没准备好，稍后再试");
                yield break;
            }
            int pick = side != null && side.Trim().ToLowerInvariant() is "right" or "右" or "1" ? choices[1] : choices[0];
            Player.professions.Add(pick);
            lvl.getImmediateProfessionPerk(pick);
            lvl.isActive = false;
            lvl.informationUp = false;
            lvl.isProfessionChooser = false;
            lvl.RemoveLevelFromLevelList();
            foreach (var s in WaitTicks(30))
                yield return s;
            yield return ActionResult.Success($"选择了职业：{LevelUpMenu.getProfessionTitleFromNumber(pick)}");
        }

        public IEnumerable<object> SkipEvent()
        {
            var ev = Game1.CurrentEvent;
            if (Game1.eventUp && ev == null)
            {
                // 剧情挂在别的地图上（人被带离了放剧情的地图），这里找不到它，跳不了：剩下的对话说完，
                // 再等模组自己把剧情标记解开、把人送回地图里（每样 5 秒），不用 AI 自己 wait
                for (int i = 0; i < 20 && Game1.activeClickableMenu is DialogueBox; i++)
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;
                for (int t = 0; t < 60 * 15; t++)
                {
                    var at = Player.TilePoint;
                    if (!Game1.eventUp && Loc.isTileOnMap(at.X, at.Y) && Game1.activeClickableMenu == null)
                        break;
                    yield return null;
                }
                var now = Player.TilePoint;
                yield return !Game1.eventUp && Loc.isTileOnMap(now.X, now.Y)
                    ? ActionResult.Success($"剧情没在这张地图上，跳不了；已经把剩下的对话说完、解开了剧情标记，人回到了 {Loc.NameOrUniqueName} ({now.X},{now.Y})")
                    : ActionResult.Fail(StuckEventHint);
                yield break;
            }
            if (!Game1.eventUp || ev == null)
            {
                // 剧情已经结束了，但人还被藏在地图外面（剧情没把她送回来）：等模组把她送回地图里
                var off = Player.TilePoint;
                if (!Loc.isTileOnMap(off.X, off.Y))
                {
                    for (int t = 0; t < 60 * 10 && !Loc.isTileOnMap(Player.TilePoint.X, Player.TilePoint.Y); t++)
                        yield return null;
                    var back = Player.TilePoint;
                    yield return Loc.isTileOnMap(back.X, back.Y)
                        ? ActionResult.Success($"剧情已经结束了，人被剧情留在了地图外面，已经送回 {Loc.NameOrUniqueName} ({back.X},{back.Y})")
                        : ActionResult.Fail("剧情已经结束了，但人还在地图外面，没能送回来：试试 travel_to Farm");
                    yield break;
                }
                yield return ActionResult.Fail("现在没有剧情事件");
                yield break;
            }
            if (!ev.skippable)
            {
                yield return ActionResult.Fail("这个事件不能跳过，请用 dialogue 推进对话，或者 wait");
                yield break;
            }
            ev.skipEvent();
            for (int i = 0; i < 300 && Game1.eventUp; i++)
                yield return null;
            // 跳过以后游戏还要淡出淡入、把人送回去：等画面稳定再返回，不然下一个动作会被“正在切换场景”挡掉
            foreach (var s in WaitSceneSettled())
                yield return s;
            var p = Player.TilePoint;
            yield return ActionResult.Success($"已跳过事件，现在在 {Loc.NameOrUniqueName} ({p.X},{p.Y})");
        }

        /// <summary>离开节日：和手动走出节日地图边界一样，弹出“要离开节日吗”并确认（Event.forceEndFestival）。</summary>
        public IEnumerable<object> LeaveFestival()
        {
            var ev = Game1.CurrentEvent;
            if (!Game1.eventUp || ev == null || !ev.isFestival)
            {
                yield return ActionResult.Fail("现在不在节日里");
                yield break;
            }
            if (FestivalMainRunning(ev))
            {
                yield return ActionResult.Fail("主活动进行中，离不开节日：用 festival start 接着参加，结束后游戏会送你回去。大家要一起看完，别人也在等你");
                yield break;
            }
            // 正在进行节日主活动（比如找彩蛋、跳舞）时不能走，和手动一样要等它结束
            if (Game1.activeClickableMenu is DialogueBox)
            {
                yield return ActionResult.Fail("先用 dialogue 把对话说完再离开");
                yield break;
            }
            var festLoc = Loc;
            Game1.activeClickableMenu?.exitThisMenu(playSound: false);
            if (!ev.TryStartEndFestivalDialogue(Player))
            {
                yield return ActionResult.Fail("现在没法离开节日（可能正在进行节日活动）");
                yield break;
            }
            // 联机时是“等所有人同意”的对话框（也是 ConfirmationDialog，不能替大家确认）
            if (Game1.activeClickableMenu is ReadyCheckDialog ready)
            {
                foreach (var s in WaitForPlayers(300))
                    yield return s;
                if (Game1.activeClickableMenu == ready)
                {
                    yield return ActionResult.Fail("等了 5 分钟，其他玩家还不想离开节日。" + ReadyCheckHint(ready));
                    yield break;
                }
            }
            else if (Game1.activeClickableMenu is ConfirmationDialog confirm)
                confirm.confirm();
            for (int i = 0; i < 900 && (Game1.eventUp || Game1.isWarping || Game1.fadeToBlack || Loc == festLoc); i++)
                yield return null;
            foreach (var s in WaitTicks(20))
                yield return s;
            yield return Game1.eventUp
                ? ActionResult.Fail("离开节日没有完成，还在节日里")
                : ActionResult.Success($"离开了节日，现在在 {Loc.NameOrUniqueName}，时间 {Util.TimeString(Game1.timeOfDay)}");
        }

        // ───────────────────────── 时间 ─────────────────────────

        public IEnumerable<object> Sleep()
        {
            var o = new Outcome();
            // 联机时已经躺下在等别人的话，接着等就行
            if (Game1.activeClickableMenu is not ReadyCheckDialog { checkName: "sleep" })
            {
                // 睡前在农场（或农舍里）有箱子的话，把今天攒的战利品收进去
                foreach (var s in StoreLootHere(storedLootNotes))
                    yield return s;
                foreach (var s in GetIntoBed(o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }

            // 等到第二天真正开始再返回：以前一睡下就返回，下一个动作常常撞上还没自动确认的出货结算界面
            // （AutoConfirmNightMenus 要 4 秒才点 OK），白白失败一次。最多等 3 分钟（存档慢的时候）；
            // 联机时还要等其他玩家上床，等到快到工具的时间上限为止。
            uint day = Game1.stats.DaysPlayed;
            int maxSeconds = Context.IsMultiplayer ? 400 : 180;
            for (int i = 0; i < maxSeconds * 60; i++)
            {
                if (Game1.activeClickableMenu is NamingMenu)
                {
                    yield return ActionResult.Success("夜里有新生命要起名（孩子或者刚出生的动物）。用 set_name 起个名字，起完不用再 sleep");
                    yield break;
                }
                // 选职业的界面是夜里结算时弹的，那时屏幕还是黑的（fadeToBlack），等不到“天亮”，要先认出来交给 AI。
                // 以前放在天亮之后才判断，结果白等 3 分钟、回一句“可以开始新的一天”，AI 不知道要选职业，界面就一直挂着
                if (Game1.activeClickableMenu is LevelUpMenu { isProfessionChooser: true })
                {
                    yield return ActionResult.Success($"睡下了，夜里技能升级要选职业（选完游戏才会存档、进入新的一天，不用再 sleep）：\n" + observer.DescribeMenu());
                    yield break;
                }
                if (Game1.farmEvent != null && Game1.activeClickableMenu is DialogueBox nightBox)
                {
                    if (nightBox.isQuestion)
                    {
                        yield return ActionResult.Success("夜里有问题要回答，回答完不用再 sleep：\n" + observer.DescribeMenu());
                        yield break;
                    }
                    foreach (var s in ClickThroughDialogue(YesChoice, 120))
                        yield return s;
                }
                // 夜里的农场事件（修船、陨石、仙子、女巫、建筑完工……）在新的一天开始前播，那时 eventUp 也开着、时钟还停在 24:00，
                // 不能当成“早上的剧情”提前返回：以前会回“睡醒了，00:00”，状态还提示“必须马上 sleep”
                if (Game1.stats.DaysPlayed != day && !Game1.newDay && !Game1.fadeToBlack && Context.IsWorldReady && Game1.farmEvent == null)
                {
                    // 早上的剧情事件交给 AI 处理
                    if (Game1.eventUp || (Game1.activeClickableMenu == null && Player.CanMove))
                        break;
                }
                yield return null;
            }
            if (Game1.activeClickableMenu is ReadyCheckDialog waiting)
            {
                yield return ActionResult.Success("还在等：" + ReadyCheckHint(waiting));
                yield break;
            }
            // 等到头还有界面挂着（不是会自动确认的结算/普通升级），别说“可以开始新的一天”，把界面交给 AI
            if (Game1.activeClickableMenu is { } left && left is not (ShippingMenu or SaveGameMenu) && left is not LevelUpMenu { isProfessionChooser: false })
            {
                yield return ActionResult.Success("睡下了，但还有界面要处理：\n" + observer.DescribeMenu());
                yield break;
            }
            yield return ActionResult.Success(Game1.stats.DaysPlayed == day
                ? "上床睡觉了，正在进入新的一天（结算界面会自动确认）"
                : $"睡醒了，{Util.Stamp()}" + (Game1.eventUp ? "，有剧情事件在播放（用 skip_event 跳过，或者 wait 看完）" : "，可以开始新的一天"));
        }

        /// <summary>走回自己家（联机时农场工人是自己的小屋）上床。</summary>
        private IEnumerable<object> GetIntoBed(Outcome o)
        {
            var house = Utility.getHomeOfFarmer(Player);
            if (Loc != house)
            {
                foreach (var s in TravelInner(house.NameOrUniqueName, o))
                    yield return s;
                if (!o.Ok)
                {
                    o.Fail("回不了家：" + o.Message);
                    yield break;
                }
            }
            var bed = house.GetPlayerBedSpot();
            var path = Pathfinder.FindPath(house, Player.TilePoint, new[] { bed });
            if (path != null)
            {
                foreach (var s in WalkPath(path, o))
                    yield return s;
            }
            if (path == null || !o.Ok)
                SnapToTile(bed);
            foreach (var s in WaitTicks(20))
                yield return s;

            // 走到床上时游戏可能已经弹出“要睡觉吗”
            if (Game1.activeClickableMenu is DialogueBox db && db.isQuestion)
            {
                int yes = Array.FindIndex(db.responses, r => r.responseKey == "Yes");
                if (yes < 0)
                    yes = 0;
                foreach (var s in Dialogue(yes))
                    if (s is not ActionResult)
                        yield return s;
            }
            else
            {
                Game1.activeClickableMenu?.exitThisMenu();
                house.answerDialogueAction("Sleep_Yes", null);
            }

            for (int i = 0; i < 300 && !Game1.newDay && !Player.isInBed.Value && !Game1.fadeToBlack && Game1.activeClickableMenu is not ReadyCheckDialog; i++)
                yield return null;
            if (!Game1.newDay && !Player.isInBed.Value && !Game1.fadeToBlack && Game1.activeClickableMenu is not ReadyCheckDialog)
            {
                o.Fail("没能睡下");
                yield break;
            }
            o.Succeed();
        }

        private static int ToMinutes(int time) => time / 100 * 60 + time % 100;
        private static int FromMinutes(int minutes) => minutes / 60 * 100 + minutes % 60;

        /// <summary>
        /// 估算走回家上床要多少游戏分钟：沿 travel_to 的路线，把每张地图里入口到出口的曼哈顿距离加起来（×1.3 算绕路），
        /// 按 WalkPath 的速度换算（每帧 getMovementSpeed 像素，1 游戏分钟 = realMilliSecondsPerGameMinute 毫秒），每次切地图再加 2 分钟。
        /// 找不到路线（比如在矿洞深处）返回 null。
        /// </summary>
        public static int? EstimateMinutesToBed()
        {
            var home = Utility.getHomeOfFarmer(Player);
            if (home == null)
                return null;
            var bed = home.GetPlayerBedSpot();
            Point cur = Player.TilePoint;
            double tiles = 0;
            int hops = 0;
            if (Loc != home)
            {
                var route = Exits.PlanRoute(Loc, home.NameOrUniqueName);
                if (route == null)
                    return null;
                foreach (var e in route)
                {
                    tiles += Util.Manhattan(cur, e.Tile);
                    cur = e.TargetTile;
                    hops++;
                }
            }
            tiles += Util.Manhattan(cur, bed);
            float speed = Math.Clamp(Player.getMovementSpeed(), 2f, 10f);
            double tilesPerGameMinute = speed * 60 / 64 * Game1.realMilliSecondsPerGameMinute / 1000.0;
            int minutes = (int)Math.Ceiling(tiles * 1.3 / tilesPerGameMinute) + hops * 2;
            return (minutes + 9) / 10 * 10;
        }

        /// <summary>晚上的回家提醒和体力提醒；urgentOnly 时只在来不及（过了 24:00 才能躺下）或体力快见底时返回。</summary>
        public static string BedtimeWarning(bool urgentOnly = false)
        {
            if (!Context.IsWorldReady || Game1.eventUp)
                return null;
            var notes = new List<string>();
            if (Player.Stamina <= 0)
                notes.Add($"⚠ 筋疲力尽了（体力 {(int)Player.Stamina}）：跑不动，只能慢走（速度不到平时一半，回家要多花一倍多时间）；降到 -15 会直接晕倒、这一天结束。"
                          + "吃东西（eat）把体力吃回 0 以上就能跑了；但“筋疲力尽”状态会留到睡觉（诊所的“肌肉回复药”能解除），今晚睡下明早只有一半体力");
            else if (Player.Stamina <= 15)
                notes.Add($"⚠ 体力只剩 {(int)Player.Stamina}：降到 0 就会筋疲力尽（跑不动、明早只有一半体力），降到 -15 会直接晕倒、这一天结束。去吃东西（eat）或 sleep");
            else if (Player.exhausted.Value && !urgentOnly)
                notes.Add("今天筋疲力尽过：今晚睡下明早只有一半体力（诊所卖的“肌肉回复药”能解除）");
            int now = Game1.timeOfDay;
            if (now >= 1900)
            {
                int? est = EstimateMinutesToBed();
                string trip = est.HasValue ? $"回家上床大约要 {est} 分钟" : "这里算不出回家要多久（可能在矿洞里，先回到地面）";
                int arrive = est.HasValue ? FromMinutes(ToMinutes(now) + est.Value) : now;
                if (now >= 2500 || arrive >= 2550)
                    notes.Add($"⚠⚠ 必须马上 sleep：现在 {Util.TimeString(now)}，{trip}。凌晨 2:00 还没睡就会晕倒，被扣钱、第二天体力减半");
                else if (arrive >= 2400)
                    notes.Add($"⚠ 该回家了：现在 {Util.TimeString(now)}，{trip}，现在出发约 {Util.TimeString(arrive)} 躺下。过了 24:00 才睡第二天体力会少，2:00 前一定要 sleep");
                else if (!urgentOnly)
                    notes.Add($"晚上了：{trip}（现在出发约 {Util.TimeString(arrive)} 到床上）。23:00 左右就该 sleep");
            }
            return notes.Count > 0 ? string.Join("\n", notes) : null;
        }

        public IEnumerable<object> Wait(int minutes)
        {
            // 一次最多等两小时：等太久中途发生什么都看不到，剩下的再 wait 一次
            int asked = minutes;
            minutes = Math.Clamp(minutes, 10, MaxWaitMinutes);
            string rest = asked > MaxWaitMinutes ? $"（一次最多等 {MaxWaitMinutes} 分钟；你要等 {asked} 分钟，还差 {asked - MaxWaitMinutes} 分钟，需要的话再 wait 一次）" : "";
            int start = Game1.timeOfDay;
            int startMin = start / 100 * 60 + start % 100;
            int targetMin = startMin + minutes;
            int startDay = Game1.dayOfMonth;
            for (int i = 0; i < 60 * 600; i++)
            {
                int now = Game1.timeOfDay / 100 * 60 + Game1.timeOfDay % 100;
                if (now >= targetMin || Game1.dayOfMonth != startDay)
                    break;
                // 剧情/对话期间时间是停的，继续等只会卡死（而且等待占着动作通道，dialogue 也发不进来）：立刻停下交给 AI 处理
                // 剧情标记卡住（没有真的剧情）时不算被打断，接着等，模组几秒内会解开它
                if ((Game1.eventUp && Game1.CurrentEvent != null) || Game1.activeClickableMenu != null)
                {
                    yield return ActionResult.Success($"等待被打断（{Util.TimeString(Game1.timeOfDay)}）：出现了剧情或界面，先处理它。\n{observer.DescribeMenu()}");
                    yield break;
                }
                yield return null;
            }
            yield return ActionResult.Success($"等待结束，现在是 {Util.TimeString(Game1.timeOfDay)}{rest}");
        }

        private const int MaxWaitMinutes = 120;

        // ───────────────────────── 剧情标记卡住 ─────────────────────────

        private int stuckEventTicks;

        /// <summary>
        /// 正在播放的普通剧情（不算节日、夜里的农场事件）给 AI 看的说明：在哪、有谁、怎么推进。没有返回 null。
        /// 动作途中冒出剧情时用它停下动作（MainThread）。
        /// </summary>
        internal static string SceneStartedNote()
        {
            if (!Game1.eventUp || Game1.farmEvent != null)
                return null;
            var ev = Game1.CurrentEvent;
            if (ev is { isFestival: true })
                return null;
            // 坐威利的船去姜岛（-78765）、从姜岛回来（-157039427）的过场是坐船本身，不算半路冒出来的剧情
            if (ev?.id is "-78765" or "-157039427")
                return null;
            var who = ev?.actors.Where(a => a != null).Select(a => a.displayName).Distinct().Take(3).ToList() ?? new List<string>();
            return $"在 {Loc?.DisplayName ?? "这里"} 触发了剧情{(who.Count > 0 ? "（" + string.Join("、", who) + "）" : "")}，刚才的动作停下了，没做完。"
                + "用 dialogue 推进对话，或者 skip_event 跳过；剧情放完、人回到地图上以后再接着做";
        }

        private const string StuckEventHint = "游戏的剧情标记卡住了（其实没有剧情在播），模组会在几秒内自动解开：用 skip_event（它会把剩下的对话说完、等标记解开），或者 wait 1 分钟再试，不用换别的动作";

        /// <summary>
        /// Game1.eventUp 开着，但当前地图没有剧情、没有界面、也不在切场景/淡入淡出：游戏自己不会再关掉它，
        /// 所有动作都会被“正在播放剧情事件”挡住，时间也停住（冬8日冰雪节路上在镇子里就卡成这样）。
        /// 持续 5 秒就记下现场（以便查原因），再用游戏自己结束剧情的 Game1.eventFinished() 收尾。每帧调用。
        /// </summary>
        /// <summary>
        /// 人被带离了放剧情的地图（比如马龙的剧情还在矿洞入口播，人已经进了第 1 层）：那段剧情留在原地图上，
        /// 下次回去会接着播一半。剧情卡住、人被困地图外时一起收拾：别的地图上的普通剧情直接丢掉（节日不动）。
        /// </summary>
        private static void DropOrphanEvents(StardewModdingAPI.IMonitor monitor)
        {
            foreach (var l in Game1.locations.Where(l => l != Loc && l.currentEvent is { isFestival: false }).ToList())
            {
                monitor.Log($"丢掉留在 {l.NameOrUniqueName} 上没播完的剧情 {l.currentEvent.id}（人已经不在那里）", StardewModdingAPI.LogLevel.Info);
                l.currentEvent = null;
            }
        }

        public void RecoverStuckEvent(StardewModdingAPI.IMonitor monitor)
        {
            bool stuck = Game1.eventUp && Game1.CurrentEvent == null && Game1.farmEvent == null
                && Game1.activeClickableMenu == null && Game1.currentMinigame == null
                && !Game1.isWarping && !Game1.fadeToBlack && !Game1.globalFade && !Game1.newDay
                && Game1.locationRequest == null;
            if (!stuck)
            {
                stuckEventTicks = 0;
                return;
            }
            if (++stuckEventTicks < 300)
                return;
            stuckEventTicks = 0;
            var withEvent = Game1.locations.Where(l => l.currentEvent != null)
                .Select(l => $"{l.NameOrUniqueName}:{l.currentEvent.id}{(l.currentEvent.isFestival ? "(节日)" : "")}").ToList();
            DropOrphanEvents(monitor);
            monitor.Log($"剧情标记卡住了：eventUp=true 但 {Loc?.NameOrUniqueName} 没有剧情；位置 {Player.TilePoint}，eventOver={Game1.eventOver}，"
                + $"有剧情的地图：{(withEvent.Count > 0 ? string.Join("、", withEvent) : "无")}，时间 {Util.Stamp()}。已用 eventFinished 解开。",
                StardewModdingAPI.LogLevel.Warn);
            try
            {
                Game1.eventFinished();
            }
            catch (Exception ex)
            {
                // eventFinished 依赖的状态不全时退回最小的解法
                monitor.Log("eventFinished 失败，直接清掉标记：" + ex.Message, StardewModdingAPI.LogLevel.Warn);
                Game1.eventUp = false;
                Player.CanMove = true;
                Game1.displayHUD = true;
            }
        }

        // ───────────────────────── 人在地图外面 ─────────────────────────

        private int offMapTicks;

        /// <summary>
        /// 没有剧情、界面、小游戏，也不在切场景，人却站在地图外面：剧情开头会把主角藏到地图外（马龙送剑是 -100,-100），
        /// 结束时送回“剧情前的位置”，这个位置记错了人就留在外面，四周没有能走的格子，自己回不来。
        /// 持续 5 秒就记下现场，把她送回地图里：剧情前的位置能站就用它，否则放到最近的出口旁边。每帧调用。
        /// </summary>
        public void RecoverOffMap(StardewModdingAPI.IMonitor monitor)
        {
            var at = Player.TilePoint;
            bool lost = Loc != null && !Loc.isTileOnMap(at.X, at.Y)
                && !Game1.eventUp && Game1.CurrentEvent == null && Game1.farmEvent == null
                && Game1.activeClickableMenu == null && Game1.currentMinigame == null
                && !Game1.isWarping && !Game1.fadeToBlack && !Game1.globalFade && !Game1.newDay
                && Game1.locationRequest == null;
            if (!lost)
            {
                offMapTicks = 0;
                return;
            }
            if (++offMapTicks < 300)
                return;
            offMapTicks = 0;

            bool Standable(Point p) => Loc.isTileOnMap(p.X, p.Y) && Pathfinder.IsPassable(Loc, p.X, p.Y) && !Pathfinder.IsTouchWarp(Loc, p.X, p.Y);
            var before = new Point((int)Player.positionBeforeEvent.X, (int)Player.positionBeforeEvent.Y);
            Point? to = Standable(before) ? before : null;
            // 出口旁边一定连着这张地图能走的部分；矿洞每层没有出口，用下来的梯子底下那格
            var anchors = Exits.Get(Loc).Select(e => e.Tile).ToList();
            if (Loc is MineShaft mine)
                anchors.Insert(0, new Point((int)mine.tileBeneathLadder.X, (int)mine.tileBeneathLadder.Y));
            anchors.Add(new Point(Pathfinder.MapWidth(Loc) / 2, Pathfinder.MapHeight(Loc) / 2));
            foreach (var anchor in anchors)
            {
                if (to != null)
                    break;
                for (int r = 0; r <= 12 && to == null; r++)
                    for (int dx = -r; dx <= r && to == null; dx++)
                        for (int dy = -r; dy <= r && to == null; dy++)
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Standable(new Point(anchor.X + dx, anchor.Y + dy)))
                                to = new Point(anchor.X + dx, anchor.Y + dy);
            }
            DropOrphanEvents(monitor);
            monitor.Log($"人在地图外面：{Loc.NameOrUniqueName} ({at.X},{at.Y})，没有剧情在播；剧情前的位置记的是 ({before.X},{before.Y})，时间 {Util.Stamp()}。"
                + (to is Point t ? $"已送回 ({t.X},{t.Y})。" : "找不到能站的格子，没有动她。"), StardewModdingAPI.LogLevel.Warn);
            if (to is Point target)
            {
                Player.Halt();
                Player.setTileLocation(new Vector2(target.X, target.Y));
                Player.CanMove = true;
            }
        }

        // ───────────────────────── 夜间自动确认 ─────────────────────────

        private IClickableMenu autoMenu;
        private int autoMenuTicks;

        /// <summary>自动确认结算界面和不需要选择的升级界面（每帧调用）。</summary>
        public void AutoConfirmNightMenus()
        {
            var menu = Game1.activeClickableMenu;
            if (menu != autoMenu)
            {
                autoMenu = menu;
                autoMenuTicks = 0;
                return;
            }
            autoMenuTicks++;
            try
            {
                if (menu is ShippingMenu && autoMenuTicks == 240)
                    reflection.GetMethod(menu, "okClicked").Invoke();
                else if (menu is LevelUpMenu lvl && !lvl.isProfessionChooser && autoMenuTicks == 180)
                    lvl.okButtonClicked();
            }
            catch
            {
                // 忽略，下一帧/AI 会处理
            }
        }
    }
}
