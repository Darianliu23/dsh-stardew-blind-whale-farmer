using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;

namespace BlindWhaleFarmer
{
    /// <summary>基于格子的 A* 寻路（四方向）。</summary>
    internal static class Pathfinder
    {
        public static readonly Point[] Dirs = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };

        public static int MapWidth(GameLocation loc) => loc.Map.Layers[0].LayerWidth;
        public static int MapHeight(GameLocation loc) => loc.Map.Layers[0].LayerHeight;

        private static readonly Rectangle IslandSouthTurtle1 = new(1088, 0, 192, 192);
        private static readonly Rectangle IslandSouthTurtle2 = new(0, 640, 256, 256);
        private static readonly Rectangle IslandNorthBoulder = new(1344, 3008, 128, 64);
        private static readonly Rectangle MountainLandslide = new(3200, 256, 192, 320);
        private static readonly Rectangle MountainRailroadBlock = new(512, 0, 256, 320);
        private static readonly Rectangle DesertMerchantBounds = new(2112, 1280, 836, 280);
        private static readonly FieldInfo BeachOldMarinerField = typeof(Beach).GetField("oldMariner", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MountainLandslideField = typeof(Mountain).GetField("landslide", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MountainRailroadBlockedField = typeof(Mountain).GetField("railroadAreaBlocked", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool IsMountainLandslide(Mountain m) =>
            (MountainLandslideField?.GetValue(m) as Netcode.NetBool)?.Value ?? false;

        internal static bool IsMountainRailroadBlocked(Mountain m) =>
            (MountainRailroadBlockedField?.GetValue(m) as Netcode.NetBool)?.Value ?? false;

        /// <summary>纯逻辑判定：姜岛南边乌龟是否挡住给定碰撞矩形（对照 IslandSouth.cs:274,276,519-529）。</summary>
        internal static bool IsBlockedByIslandSouthTurtles(Rectangle rect, bool exitsBlocked, bool westernTurtleMoved) =>
            (exitsBlocked && rect.Intersects(IslandSouthTurtle1))
            || (!westernTurtleMoved && rect.Intersects(IslandSouthTurtle2));

        /// <summary>
        /// 6 个地图子类重写的是 6 参数 isCollidingPosition，不会被带 pathfinding 参数的重载调用。
        /// 对照源码逐一检查其专有阻挡矩形（无推 NPC/晃草副作用）：
        /// IslandSouth（乌龟）、IslandNorth（洞口巨石）、Mountain（山体滑坡/铁路路障）、Desert（沙漠商人）、Railroad（行驶列车）、Beach（老水手）。
        /// </summary>
        internal static bool IsBlockedByLocationSpecial(GameLocation loc, Rectangle rect)
        {
            switch (loc)
            {
                case IslandSouth isouth:
                {
                    bool exitsBlocked = !(Game1.MasterPlayer?.hasOrWillReceiveMail("Island_FirstParrot") ?? false);
                    return IsBlockedByIslandSouthTurtles(rect, exitsBlocked, isouth.westernTurtleMoved.Value);
                }
                case IslandNorth inorth:
                    return !inorth.caveOpened.Value && rect.Intersects(IslandNorthBoulder);
                case Mountain mountain:
                    return (IsMountainLandslide(mountain) && rect.Intersects(MountainLandslide))
                        || (IsMountainRailroadBlocked(mountain) && rect.Intersects(MountainRailroadBlock));
                case Desert:
                    return rect.Intersects(DesertMerchantBounds);
                case Railroad railroad:
                    return !Game1.eventUp && railroad.train.Value != null && railroad.train.Value.getBoundingBox().Intersects(rect);
                case Beach beach:
                    return BeachOldMarinerField?.GetValue(beach) is NPC mariner && rect.Intersects(mariner.GetBoundingBox());
                default:
                    return false;
            }
        }

        /// <summary>农夫站在这格时的碰撞箱（和 Farmer.GetBoundingBox 一样）。</summary>
        internal static Rectangle FarmerTileBox(int x, int y)
        {
            int top = Game1.player?.Sprite != null ? Game1.player.Sprite.getHeight() - 32 : 0;
            return new Rectangle(x * 64 + 8, y * 64 + top, 48, 32);
        }

        /// <summary>农夫能否站在这一格。用的是农夫站在这格时的碰撞箱（和 Farmer.GetBoundingBox 一样）。</summary>
        public static bool IsPassable(GameLocation loc, int x, int y)
        {
            if (!loc.isTileOnMap(x, y))
                return false;
            // Farmer.GetBoundingBox = (X+8, Y+精灵高-32, 48, 32)，站在格子上时 Position = 格子左上角，碰撞箱是这格的上半截。
            // 以前写成 y*64+32，下边缘正好压到下一行（Bottom/64 = y+1），等于每格都顺带检查下面一格，
            // 海滩木栈道这种下面一排是栏杆的地方就会被误判成走不通。
            var rect = FarmerTileBox(x, y);
            if (IsBlockedByLocationSpecial(loc, rect))
                return false;
            // pathfinding + skipCollisionEffects：只做检测，不触发推开 NPC、草晃动等副作用；NPC 不算障碍（会走开）
            return !loc.isCollidingPosition(rect, Game1.viewport, true, 0, false, Game1.player,
                pathfinding: true, projectile: false, ignoreCharacterRequirement: false, skipCollisionEffects: true);
        }

        /// <summary>踩上去就会触发传送的格子（TouchAction Warp / MagicWarp / LoadMap）。寻路时绕开，除非它就是目的地。</summary>
        public static bool IsTouchWarp(GameLocation loc, int x, int y)
        {
            string touch = loc.doesTileHaveProperty(x, y, "TouchAction", "Back");
            return IsTouchWarpAction(touch);
        }

        /// <summary>判断一条 TouchAction 字符串是不是踩上去就传走的动作（Warp / MagicWarp / SVE 的 LoadMap）。</summary>
        internal static bool IsTouchWarpAction(string touch)
        {
            if (string.IsNullOrWhiteSpace(touch))
                return false;
            var verb = touch.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return verb.Equals("Warp", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("MagicWarp", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("LoadMap", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按地图总格数给大地图（如 SVE 156x156 / 240x156、IF2R 163x156）放宽探索上限。</summary>
        internal static int MapNodeBudget(GameLocation loc, int minNodes, int cap = 120000)
        {
            if (loc?.Map?.Layers == null || loc.Map.Layers.Count == 0)
                return minNodes;
            long area = (long)MapWidth(loc) * MapHeight(loc);
            int scaled = area > int.MaxValue ? cap : (int)Math.Min(cap, Math.Max(minNodes, area * 2));
            return scaled;
        }

        /// <summary>
        /// 关着的室内门（Buildings 层 Action "Door [主人…]"）。关门时那格有碰撞，开门后才能走。
        /// 返回 Action 拆开后的参数（第 0 个是 "Door"），不是关着的门返回 null。
        /// </summary>
        public static string[] ClosedDoor(GameLocation loc, Point p)
        {
            if (!loc.interiorDoors.TryGetValue(p, out bool isOpen) || isOpen)
                return null;
            string action = loc.doesTileHaveProperty(p.X, p.Y, "Action", "Buildings");
            if (action == null || !action.StartsWith("Door"))
                return null;
            return ArgUtility.SplitBySpace(action);
        }

        /// <summary>室内门（不管开着还是关着）的 Action 参数（第 0 个是 "Door"），不是室内门返回 null。</summary>
        public static string[] InteriorDoor(GameLocation loc, Point p)
        {
            if (!loc.interiorDoors.ContainsKey(p))
                return null;
            string action = loc.doesTileHaveProperty(p.X, p.Y, "Action", "Buildings");
            return action != null && action.StartsWith("Door") ? ArgUtility.SplitBySpace(action) : null;
        }

        /// <summary>和 GameLocation.performAction 的 "Door" 一样：没写主人随便开；写了主人要有 2 心好感或以前开过。</summary>
        public static bool CanOpenDoor(GameLocation loc, string[] door)
        {
            if (door.Length <= 1)
                return true;
            for (int i = 1; i < door.Length; i++)
            {
                if (Game1.player.getFriendshipHeartLevelForNPC(door[i]) >= 2 || Game1.player.mailReceived.Contains("doorUnlock" + door[i]))
                    return true;
                if (door[i] == "Sebastian" && loc.IsGreenRainingHere() && Game1.year == 1)
                    return true;
            }
            return false;
        }

        /// <summary>这一格上的栅栏门（开着关着都算）；没有返回 null。关着的门走到跟前可以点开，所以寻路当它能走。</summary>
        public static Fence GateAt(GameLocation loc, Point p) =>
            loc.objects.TryGetValue(new Vector2(p.X, p.Y), out var o) && o is Fence f && f.isGate.Value && f.health.Value > 1f ? f : null;

        /// <summary>寻路时这一格能不能踩：能开的门算能走；传送格只在它就是目的地时才踩。</summary>
        private static bool StepPassable(GameLocation loc, Point p, HashSet<Point> goalSet, bool throughLockedDoors)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= MapWidth(loc) || p.Y >= MapHeight(loc))
                return false;
            // 自己开不了的门，开着也当锁着：村民刚推开的卧室门（比如晚上德米特里厄斯进卧室）一会儿就自己关上，
            // 好感不够的玩家走到跟前就被拦下
            if (InteriorDoor(loc, p) is { } door)
            {
                if (!CanOpenDoor(loc, door))
                    return throughLockedDoors;
                if (ClosedDoor(loc, p) != null)
                    return true;
            }
            if (GateAt(loc, p) != null)
                return true;
            return IsPassable(loc, p.X, p.Y) && (goalSet.Contains(p) || !IsTouchWarp(loc, p.X, p.Y));
        }

        /// <summary>
        /// 找不到路时用：从 start 出发能走到多少格（最多数到 maxNodes），以及其中离 goals 最近的一格。
        /// </summary>
        public static (int Reachable, Point Closest, int Distance) Explore(GameLocation loc, Point start, ICollection<Point> goals, int maxNodes = 20000)
        {
            maxNodes = MapNodeBudget(loc, maxNodes, 60000);
            var goalSet = new HashSet<Point>(goals);
            var seen = new HashSet<Point> { start };
            var queue = new Queue<Point>();
            queue.Enqueue(start);
            Point closest = start;
            int best = int.MaxValue;
            while (queue.Count > 0 && seen.Count < maxNodes)
            {
                var cur = queue.Dequeue();
                foreach (var g in goalSet)
                {
                    int dist = Math.Abs(g.X - cur.X) + Math.Abs(g.Y - cur.Y);
                    if (dist < best)
                    {
                        best = dist;
                        closest = cur;
                    }
                }
                foreach (var d in Dirs)
                {
                    var next = new Point(cur.X + d.X, cur.Y + d.Y);
                    if (!seen.Contains(next) && StepPassable(loc, next, goalSet, false))
                    {
                        seen.Add(next);
                        queue.Enqueue(next);
                    }
                }
            }
            return (seen.Count, closest, best);
        }

        /// <summary>
        /// 火山传送带房间里的尖刺机关（Spiker）打不死，人站到它那条直线上它就冲过来撞人。
        /// 它所在的格子和它来回滑的那条轨道算“危险格”，寻路时多算步数：能绕就绕，非穿不可时横穿过去。没有尖刺返回 null。
        /// </summary>
        private static Dictionary<Point, int> SpikerLanes(GameLocation loc)
        {
            Dictionary<Point, int> lanes = null;
            foreach (var spiker in loc.characters.OfType<StardewValley.Monsters.Spiker>())
            {
                lanes ??= new Dictionary<Point, int>();
                var at = spiker.TilePoint;
                lanes[at] = 30;
                var axis = spiker.targetDirection % 2 == 0 ? new[] { Dirs[0], Dirs[2] } : new[] { Dirs[1], Dirs[3] };
                foreach (var d in axis)
                {
                    var p = at;
                    for (int i = 0; i < 40; i++)
                    {
                        p = new Point(p.X + d.X, p.Y + d.Y);
                        if (!loc.isTileOnMap(p.X, p.Y) || !IsPassable(loc, p.X, p.Y))
                            break;
                        lanes[p] = Math.Max(lanes.GetValueOrDefault(p), i < 2 ? 12 : 6);
                    }
                }
            }
            return lanes;
        }

        /// <summary>
        /// 寻找从 start 到任一 goal 的路径（含起点），找不到返回 null。能开的室内门算可走；
        /// throughLockedDoors 时锁着的门也算，只用来解释为什么走不到。
        /// clearable 给了的话，它认可的格子（杂草、石头这类能清掉的障碍）也算能走，每格多算 clearCost 步，
        /// 这样能绕就绕，绕不过去才穿过去；走的时候由调用方把路上的障碍清掉。
        /// </summary>
        public static List<Point> FindPath(GameLocation loc, Point start, ICollection<Point> goals, int maxNodes = 60000, bool throughLockedDoors = false,
            Func<Point, bool> clearable = null, int clearCost = 4)
        {
            if (goals.Count == 0)
                return null;
            if (maxNodes >= 60000)
                maxNodes = MapNodeBudget(loc, maxNodes, 120000);
            var goalSet = new HashSet<Point>(goals);
            if (goalSet.Contains(start))
                return new List<Point> { start };

            // 0 = 走不了，1 = 能走，2 = 有障碍但能清掉
            var passCache = new Dictionary<Point, int>();
            int Passable(Point p)
            {
                if (!passCache.TryGetValue(p, out int v))
                    passCache[p] = v = StepPassable(loc, p, goalSet, throughLockedDoors) ? 1
                        : clearable != null && p.X >= 0 && p.Y >= 0 && p.X < MapWidth(loc) && p.Y < MapHeight(loc) && clearable(p) ? 2 : 0;
                return v;
            }

            int Heuristic(Point p)
            {
                int best = int.MaxValue;
                foreach (var g in goalSet)
                    best = Math.Min(best, Math.Abs(g.X - p.X) + Math.Abs(g.Y - p.Y));
                return best;
            }

            var danger = SpikerLanes(loc);
            var open = new PriorityQueue<Point, int>();
            var cameFrom = new Dictionary<Point, Point>();
            var gScore = new Dictionary<Point, int> { [start] = 0 };
            open.Enqueue(start, Heuristic(start));
            int expanded = 0;

            while (open.Count > 0 && expanded < maxNodes)
            {
                var cur = open.Dequeue();
                expanded++;
                if (goalSet.Contains(cur))
                {
                    var path = new List<Point> { cur };
                    while (cameFrom.TryGetValue(cur, out var prev))
                    {
                        cur = prev;
                        path.Add(cur);
                    }
                    path.Reverse();
                    return path;
                }

                int curG = gScore[cur];
                foreach (var d in Dirs)
                {
                    var next = new Point(cur.X + d.X, cur.Y + d.Y);
                    int pass = Passable(next);
                    if (pass == 0)
                        continue;
                    int ng = curG + (pass == 2 ? 1 + clearCost : 1);
                    if (danger != null && danger.TryGetValue(next, out int extra) && !goalSet.Contains(next))
                        ng += extra;
                    if (gScore.TryGetValue(next, out int old) && old <= ng)
                        continue;
                    gScore[next] = ng;
                    cameFrom[next] = cur;
                    open.Enqueue(next, ng + Heuristic(next));
                }
            }
            return null;
        }

        /// <summary>目标格四周可站立的格子（用于对目标使用工具/交互）。</summary>
        public static List<Point> AdjacentStandTiles(GameLocation loc, Point target)
        {
            var list = new List<Point>();
            foreach (var d in Dirs)
            {
                var p = new Point(target.X + d.X, target.Y + d.Y);
                if (IsPassable(loc, p.X, p.Y))
                    list.Add(p);
            }
            return list;
        }

        /// <summary>离 target 最近的若干可站立格（target 本身不可站时用，比如地图边缘的传送点）。</summary>
        public static List<Point> NearestStandTiles(GameLocation loc, Point target, int radius)
        {
            var list = new List<Point>();
            int w = MapWidth(loc), h = MapHeight(loc);
            for (int r = 0; r <= radius && list.Count == 0; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                            continue;
                        int x = target.X + dx, y = target.Y + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h)
                            continue;
                        if (IsPassable(loc, x, y))
                            list.Add(new Point(x, y));
                    }
                }
            }
            return list;
        }

        public static int DirectionTo(Point from, Point to)
        {
            int dx = to.X - from.X, dy = to.Y - from.Y;
            if (Math.Abs(dx) > Math.Abs(dy))
                return dx > 0 ? 1 : 3;
            return dy < 0 ? 0 : 2;
        }
    }
}
