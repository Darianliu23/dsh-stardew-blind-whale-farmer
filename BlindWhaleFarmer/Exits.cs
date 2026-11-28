using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace BlindWhaleFarmer
{
    internal enum ExitKind
    {
        /// <summary>地图边缘/地面传送点，走过去即可切换地点。</summary>
        Warp,
        /// <summary>需要对门交互（可能有营业时间）。</summary>
        Door,
        /// <summary>农场上的建筑门。</summary>
        BuildingDoor,
        /// <summary>巴士：巴士站的售票机（去沙漠，要买票）、沙漠里停着的巴士（回巴士站，免费）。</summary>
        Bus,
        /// <summary>矿车（巴士站、镇上、矿洞口、采石场之间；锅炉房收集包或 Joja 矿车项目完成后才通）。</summary>
        Minecart
    }

    internal sealed class Exit
    {
        public ExitKind Kind;
        public Point Tile;
        public string Target;
        public Point TargetTile;
        public Building Building;
        /// <summary>踩上去由游戏自动触发的传送。</summary>
        public bool IsTouch;
        public string Note = "";
        /// <summary>LockedDoorWarp 要求的好感：比如玛鲁房间的侧门要和玛鲁 500 点（2 心）。没有要求时为 null。</summary>
        public string FriendNpc;
        public int FriendPoints;
        /// <summary>LockedDoorWarp 的营业时间（游戏时间，比如 900、1600）；不是有营业时间的门时为 -1。</summary>
        public int OpenTime = -1, CloseTime = -1;
        /// <summary>矿车的目的地，和整条线路的开通条件。</summary>
        public StardewValley.GameData.Minecarts.MinecartDestinationData Minecart;
        public string MinecartUnlock;

        /// <summary>和 GameLocation.lockedDoorWarp 一样判断好感门槛（冬天这类门不限好感）。</summary>
        public bool FriendshipOk(GameLocation from) =>
            FriendNpc == null || from.IsWinterHere()
            || (Game1.player.friendshipData.TryGetValue(FriendNpc, out var f) && f.Points >= FriendPoints);

        public string Describe()
        {
            string kind = Kind switch
            {
                ExitKind.Warp => "出口",
                ExitKind.Door => "门",
                ExitKind.Bus => Target is "IslandSouth" or "BoatTunnel" ? "船" : "巴士",
                ExitKind.Minecart => "矿车",
                _ => "建筑门"
            };
            return $"{kind} ({Tile.X},{Tile.Y}) → {Target}{Note}";
        }
    }

    /// <summary>计算每个地点的出口，并在地点之间做广度优先搜索规划路线。</summary>
    internal static class Exits
    {
        private static readonly Dictionary<string, List<Exit>> TileExitCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (xTile.Map Map, int[,] Grid, long DoorMask, int GateCount)> AreaCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string[]>> DoorActionCache = new(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            TileExitCache.Clear();
            AreaCache.Clear();
            DoorActionCache.Clear();
        }

        public static List<Exit> Get(GameLocation loc)
        {
            var list = new List<Exit>();
            if (loc?.Map == null)
                return list;

            foreach (var w in loc.warps)
            {
                // 岛北去火山的传送点写的是 VolcanoEntrance，游戏踩上去时换成火山第 0 层（GameLocation.isCollidingWithWarp）
                string target = w.TargetName == "VolcanoEntrance" ? StardewValley.Locations.VolcanoDungeon.GetLevelName(0) : w.TargetName;
                // 火山的层是临时生成的：getLocationFromName 会当场生成一整层地图，只看名字对不对就行
                // 岛北悬崖下蜗牛教授的洞（IslandNorthCave1）洞口被大石头堵着，要先用炸弹炸开（IslandNorth.caveOpened）；
                // 没炸开时传送点仍然在，但人走不进去，只能当它不存在，免得寻路往石头里钻
                if (target == "IslandNorthCave1" && loc is StardewValley.Locations.IslandNorth { caveOpened.Value: false })
                    continue;
                // 姜岛南边乌龟挡路：未喂第一只鹦鹉前去岛北被 turtle1 挡住；未喂西乌龟（westernTurtleMoved）前去岛西被 turtle2 挡住
                if (loc is StardewValley.Locations.IslandSouth isouth)
                {
                    if (target == "IslandWest" && !isouth.westernTurtleMoved.Value)
                        continue;
                    if (target == "IslandNorth" && !(Game1.MasterPlayer?.hasOrWillReceiveMail("Island_FirstParrot") ?? false))
                        continue;
                }
                // 山区滑坡/铁路路障未解开前不能通过对应出口
                if (loc is StardewValley.Locations.Mountain mountain)
                {
                    if (target == "Railroad" && Pathfinder.IsMountainRailroadBlocked(mountain))
                        continue;
                    if (target == "Mine" && Pathfinder.IsMountainLandslide(mountain))
                        continue;
                }
                if (w.npcOnly.Value || ModSupport.IsWarpRoom(target)
                    || (!StardewValley.Locations.VolcanoDungeon.IsGeneratedLevel(target) && Game1.getLocationFromName(target) == null))
                    continue;
                list.Add(new Exit { Kind = ExitKind.Warp, Tile = new Point(w.X, w.Y), Target = target, TargetTile = new Point(w.TargetX, w.TargetY) });
            }

            // 去沙漠的巴士要等金库收集包（或 Joja 的巴士项目）完成、巴士修好以后才有
            // 威利的后屋（船坞）要收到威利的邀请信才进得去；去姜岛的船要修好才开
            // 下水道入口（Town / SVE 蓝月葡萄园的 EnterSewer）需要生锈的钥匙
            list.AddRange(GetTileExits(loc).Where(e =>
                (e.Kind != ExitKind.Bus || e.Target != "Desert" || BusRepaired)
                && (e.Target != "BoatTunnel" || e.Kind != ExitKind.Door || Game1.player.mailReceived.Contains("willyBackRoomInvitation"))
                && (e.Kind != ExitKind.Bus || e.Target != "IslandSouth" || BoatRepaired)
                && (e.Target != "QiNutRoom" || StardewValley.Locations.IslandWest.IsQiWalnutRoomDoorUnlocked(out _))
                && (e.Target != "Sewer" || e.Kind != ExitKind.Door || Game1.player.hasRustyKey)
                && (e.Kind != ExitKind.Minecart || MinecartUsable(loc, e))));

            foreach (var b in loc.buildings)
            {
                string indoors = null;
                try
                {
                    indoors = b.GetIndoorsName();
                }
                catch
                {
                    // 某些建筑没有室内
                }
                if (string.IsNullOrEmpty(indoors) || b.isUnderConstruction())
                    continue;
                var door = b.humanDoor.Value;
                if (door.X < 0 || door.Y < 0)
                    continue;
                list.Add(new Exit
                {
                    Kind = ExitKind.BuildingDoor,
                    Tile = new Point(b.tileX.Value + door.X, b.tileY.Value + door.Y),
                    Target = indoors,
                    Building = b,
                    Note = $"（{b.buildingType.Value}）"
                });
            }

            // 合并同一目标、相邻的传送格，只保留一个代表，避免列表太长
            var merged = new List<Exit>();
            foreach (var e in list)
            {
                bool dup = merged.Any(m => m.Kind == e.Kind && m.Target == e.Target
                    && Math.Abs(m.Tile.X - e.Tile.X) + Math.Abs(m.Tile.Y - e.Tile.Y) <= 3);
                if (!dup)
                    merged.Add(e);
            }
            return merged;
        }

        /// <summary>
        /// 这扇有营业时间的门现在进不进得去（和 GameLocation.lockedDoorWarp 的判断一样，好感门槛另算）。
        /// 进得去返回 null；今天晚些时候会开时 opensAt 是开门时间，今天不会再开时为 -1。
        /// </summary>
        public static string DoorClosedReason(GameLocation from, Exit e, out int opensAt)
        {
            opensAt = -1;
            if (e.Kind != ExitKind.Door || e.OpenTime < 0 || from == null)
                return null;
            string place = PlaceName(e.Target);
            if (GameLocation.AreStoresClosedForFestival() && from.InValleyContext())
                return $"今天是节日，镇上的店铺都关门，{place}进不去。今天先干别的（或者去参加节日），明天再来";
            bool townKey = Game1.player.HasTownKey && from.InValleyContext() && (from is not StardewValley.Locations.BeachNightMarket || e.Target == "FishShop");
            if (e.Target == "SeedShop" && Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth) == "Wed" && !Utility.HasAnyPlayerSeenEvent("191393") && !Game1.player.HasTownKey)
                return $"{place}每周三休息（社区中心修好以后才每天开），今天进不去。种子可以去 JojaMart 买，或者明天再来";
            if (townKey)
                return null;
            if (from.IsGreenRainingHere() && Game1.year == 1 && from is not StardewValley.Locations.Beach && from is not StardewValley.Locations.Forest && e.Target != "AdventureGuild")
                return null;
            int open = e.Target == "FishShop" && Game1.player.mailReceived.Contains("willyHours") ? 800 : e.OpenTime;
            int now = Game1.timeOfDay;
            if (now >= open && now < e.CloseTime)
                return null;
            string hours = $"营业 {Util.TimeString(open)}-{Util.TimeString(e.CloseTime)}，现在 {Util.TimeString(now)}";
            if (now < open)
            {
                opensAt = open;
                return $"{place}还没开门（{hours}）";
            }
            return $"{place}今天已经关门了（{hours}），明天 {Util.TimeString(open)} 以后再来";
        }

        private static string PlaceName(string name)
        {
            var loc = Game1.getLocationFromName(name);
            string display = loc?.DisplayName;
            return string.IsNullOrEmpty(display) || display == name ? name : $"{display}（{name}）";
        }

        /// <summary>巴士修好了没有（和游戏里售票机的判断一样，看主机收没收到 ccVault）。</summary>
        public static bool BusRepaired =>
            Game1.MasterPlayer.mailReceived.Contains("ccVault") || Game1.MasterPlayer.mailReceived.Contains("jojaVault");

        /// <summary>威利去姜岛的船修好了没有（和船坞售票机的判断一样）。</summary>
        public static bool BoatRepaired => Game1.MasterPlayer.mailReceived.Contains("willyBoatFixed");

        /// <summary>这个“巴士”类出口其实是威利的船（去姜岛、从姜岛回来）。</summary>
        public static bool IsBoat(Exit e) => e.Kind == ExitKind.Bus && e.Target is "IslandSouth" or "BoatTunnel";

        private static List<Exit> GetTileExits(GameLocation loc)
        {
            string key = loc.NameOrUniqueName;
            if (TileExitCache.TryGetValue(key, out var cached)
                && !(loc is StardewValley.Locations.IslandWest iw && iw.farmhouseRestored.Value && !cached.Any(e => e.Target == "IslandFarmHouse")))
                return cached;

            var list = new List<Exit>();
            int w = Pathfinder.MapWidth(loc), h = Pathfinder.MapHeight(loc);
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    string action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
                    if (!string.IsNullOrEmpty(action))
                    {
                        if (action.StartsWith("MinecartTransport"))
                            list.AddRange(MinecartExits(action, x, y));
                        var e = ParseDoorAction(action, x, y);
                        if (e != null)
                            list.Add(e);
                    }

                    // 姜岛西边齐先生核桃房的门：也没有 Action，按图块编号 1470 认（IslandWest.checkAction，找到 100 个金核桃才开）
                    if (loc is StardewValley.Locations.IslandWest && loc.getTileIndexAt(x, y, "Buildings", "untitled tile sheet") == 1470
                        && Game1.getLocationFromName("QiNutRoom") != null)
                    {
                        list.Add(new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "QiNutRoom", TargetTile = new Point(7, 8),
                            Note = "（齐先生的核桃房，一共找到 100 个金核桃才开）" });
                    }

                    // 巴士站的售票机没有 Action 属性，游戏是按图块编号 1057 认的（BusStop.checkAction）
                    if (loc is StardewValley.Locations.BusStop && loc.getTileIndexAt(x, y, "Buildings") == 1057
                        && Game1.getLocationFromName("Desert") != null)
                    {
                        list.Add(new Exit { Kind = ExitKind.Bus, Tile = new Point(x, y), Target = "Desert",
                            Note = "（售票机，坐巴士去沙漠，车票 500g；帕姆在站台时才发车，大约 10:00-17:00）" });
                    }

                    string touch = loc.doesTileHaveProperty(x, y, "TouchAction", "Back");
                    if (!string.IsNullOrEmpty(touch) && ParseTouchWarp(touch, x, y) is Exit touchExit)
                        list.Add(touchExit);
                }
            }
            TileExitCache[key] = list;
            return list;
        }

        internal static Exit ParseTouchWarp(string touch, int x, int y, Func<string, bool> locationExists = null)
        {
            if (string.IsNullOrWhiteSpace(touch))
                return null;
            locationExists ??= name => Game1.getLocationFromName(name) != null;
            var parts = touch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;
            string verb = parts[0];
            // 沙漠里停着的巴士：走到车门那一格就问“是否返回星露谷”
            if (verb.Equals("DesertBus", StringComparison.OrdinalIgnoreCase) && locationExists("BusStop"))
                return new Exit { Kind = ExitKind.Bus, Tile = new Point(x, y), Target = "BusStop", IsTouch = true, Note = "（坐巴士回巴士站，免费）" };
            // 姜岛南边码头的船：走上去问“要不要回去”，坐船回威利的船坞（免费）
            if (verb.Equals("LeaveIsland", StringComparison.OrdinalIgnoreCase) && locationExists("BoatTunnel"))
                return new Exit { Kind = ExitKind.Bus, Tile = new Point(x, y), Target = "BoatTunnel", IsTouch = true, Note = "（坐威利的船回鹈鹕镇，免费）" };
            // Warp / MagicWarp / SVE 的 LoadMap <地点> <x> <y> [朝向]
            if (parts.Length >= 4
                && (verb.Equals("Warp", StringComparison.OrdinalIgnoreCase)
                    || verb.Equals("MagicWarp", StringComparison.OrdinalIgnoreCase)
                    || verb.Equals("LoadMap", StringComparison.OrdinalIgnoreCase))
                && !ModSupport.IsWarpRoom(parts[1])
                && locationExists(parts[1])
                && int.TryParse(parts[2], out int tx) && int.TryParse(parts[3], out int ty))
            {
                return new Exit { Kind = ExitKind.Warp, Tile = new Point(x, y), Target = parts[1], TargetTile = new Point(tx, ty), IsTouch = true };
            }
            return null;
        }

        internal static Exit ParseDoorAction(string action, int x, int y, Func<string, bool> locationExists = null)
        {
            var p = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0)
                return null;
            locationExists ??= name => Game1.getLocationFromName(name) != null;
            switch (p[0])
            {
                case "Warp":
                case "LockedDoorWarp":
                case "WarpMensLocker":
                case "WarpWomensLocker":
                    if (p.Length >= 4 && !ModSupport.IsWarpRoom(p[3]) && locationExists(p[3])
                        && int.TryParse(p[1], out int tx) && int.TryParse(p[2], out int ty))
                    {
                        string note = "";
                        if (p[0] == "LockedDoorWarp" && p.Length >= 6)
                            note = $"（营业 {p[4]}-{p[5]}）";
                        var exit = new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = p[3], TargetTile = new Point(tx, ty), Note = note };
                        if (p[0] == "LockedDoorWarp" && p.Length >= 6 && int.TryParse(p[4], out int open) && int.TryParse(p[5], out int close))
                        {
                            exit.OpenTime = open;
                            exit.CloseTime = close;
                        }
                        // LockedDoorWarp x y 地点 开门 关门 [NPC 好感点数]
                        if (p[0] == "LockedDoorWarp" && p.Length >= 8 && int.TryParse(p[7], out int points) && points > 0)
                        {
                            exit.FriendNpc = p[6];
                            exit.FriendPoints = points;
                            exit.Note += $"（要和 {p[6]} 有 {points / 250} 心好感）";
                        }
                        return exit;
                    }
                    break;
                case "MagicWarp":
                    if (p.Length >= 4 && !ModSupport.IsWarpRoom(p[1]) && locationExists(p[1])
                        && int.TryParse(p[2], out int mtx) && int.TryParse(p[3], out int mty))
                        return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = p[1], TargetTile = new Point(mtx, mty) };
                    break;
                case "EnterSewer" when locationExists("Sewer"):
                    return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "Sewer", TargetTile = new Point(16, 11), Note = "（下水道，需要生锈的钥匙）" };
                case "WarpCommunityCenter":
                    return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "CommunityCenter" };
                // 皮埃尔家通往卡洛琳阳光房的门（GameLocation.performAction，和卡洛琳 2 心以上才开）
                case "Warp_Sunroom_Door" when locationExists("Sunroom"):
                    return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "Sunroom", TargetTile = new Point(5, 13),
                        FriendNpc = "Caroline", FriendPoints = 500, Note = "（卡洛琳的阳光房，要和卡洛琳有 2 心好感）" };
                // 威利鱼店里通往后屋船坞的门（FishShop.performAction，收到邀请信才开）
                case "WarpBoatTunnel" when locationExists("BoatTunnel"):
                    return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "BoatTunnel", TargetTile = new Point(6, 12), Note = "（威利的船坞）" };
                // 船坞的售票机：买票坐船去姜岛（BoatTunnel.checkAction，船修好以后）
                case "BoatTicket" when locationExists("IslandSouth"):
                    return new Exit { Kind = ExitKind.Bus, Tile = new Point(x, y), Target = "IslandSouth", Note = "（售票机，坐威利的船去姜岛，船票 1000g）" };
            }
            return null;
        }

        /// <summary>从当前地点到目标地点的一串出口（BFS，最多 12 跳）。</summary>
        /// <summary>一个出口的标识：Get 每次可能给新对象，不能直接比较对象。</summary>
        /// <summary>矿车站（Action: MinecartTransport [线路] [本站 Id]）能去的每一站，各算一个出口。开没开通在 Get 里每次现查。</summary>
        private static IEnumerable<Exit> MinecartExits(string action, int x, int y)
        {
            var parts = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string networkId = parts.Length > 1 ? parts[1] : "Default";
            string exclude = parts.Length > 2 ? parts[2] : null;
            Dictionary<string, StardewValley.GameData.Minecarts.MinecartNetworkData> data;
            try
            {
                data = DataLoader.Minecarts(Game1.content);
            }
            catch
            {
                yield break;
            }
            if (!data.TryGetValue(networkId, out var network) || network?.Destinations == null)
                yield break;
            foreach (var d in network.Destinations)
            {
                if (d == null || string.IsNullOrWhiteSpace(d.TargetLocation) || d.Id.Equals(exclude, StringComparison.OrdinalIgnoreCase)
                    || Game1.getLocationFromName(d.TargetLocation) == null)
                    continue;
                yield return new Exit
                {
                    Kind = ExitKind.Minecart, Tile = new Point(x, y), Target = d.TargetLocation, TargetTile = d.TargetTile,
                    Minecart = d, MinecartUnlock = network.UnlockCondition,
                    Note = d.Price > 0 ? $"（坐矿车，{d.Price}g）" : "（坐矿车，免费）"
                };
            }
        }

        /// <summary>矿车现在能不能坐：线路开通了（锅炉房收集包/Joja 矿车项目）、这一站的条件满足、钱够。骑着马的话坐之前先下马（马留在原地，第二天自己回家）。</summary>
        public static bool MinecartUsable(GameLocation loc, Exit e) =>
            e.Minecart != null
            && GameStateQuery.CheckConditions(e.MinecartUnlock, loc) && GameStateQuery.CheckConditions(e.Minecart.Condition, loc)
            && Game1.player.Money >= e.Minecart.Price && !e.Target.Equals(loc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase);

        public static string ExitKey(string fromName, Exit e) => $"{fromName}>{e.Target}@{e.Tile.X},{e.Tile.Y}";

        /// <summary>
        /// 地图里被悬崖、水或锁住的室内门隔开、互不相通的区域编号（大多数地图只有一块，都是 1；无法计算时退回 0）。
        /// 动态按静态地图地形做连通分量计算（不因农场/树林上临时长出的杂草树枝误切），同时兼容原版和 SVE 重绘/新增的多分区地图
        /// （如 Backwoods、Railroad、Custom_ForestWest、Custom_Highlands 等）。
        /// </summary>
        public static int Area(string locationName, Point tile)
        {
            if (string.IsNullOrWhiteSpace(locationName))
                return 0;
            GameLocation loc = null;
            try
            {
                loc = Game1.getLocationFromName(locationName);
            }
            catch
            {
                // 离线测试没有 Game1.locations
            }
            return Area(loc, tile);
        }

        private static int Area(GameLocation loc, Point tile)
        {
            if (loc?.Map == null)
                return 0;
            var grid = GetAreaGrid(loc);
            if (grid == null)
                return 0;
            return AreaFromGrid(grid, grid.GetLength(0), grid.GetLength(1), tile);
        }

        private static int[,] GetAreaGrid(GameLocation loc)
        {
            string key = loc.NameOrUniqueName;
            long doorMask = ComputeDoorMask(loc, key);
            var volcano = loc as VolcanoDungeon;
            int gateCount = volcano?.dwarfGates.Count ?? 0;
            if (AreaCache.TryGetValue(key, out var cached) && cached.Map == loc.Map
                && cached.DoorMask == doorMask && cached.GateCount == gateCount)
                return cached.Grid;
            int w = Pathfinder.MapWidth(loc), h = Pathfinder.MapHeight(loc);
            if (w <= 0 || h <= 0)
                return null;
            // 机关门能踩开，岩浆能浇冷。区域规划允许经过它们，实际通行由 TakeExit 寻路处理。
            var gateTiles = volcano?.dwarfGates.Select(g => g.tilePosition.Value + new Point(0, 1)).ToHashSet();
            var grid = ComputeComponents(w, h, (x, y) => gateTiles?.Contains(new Point(x, y)) == true
                || (volcano is { level.Value: not 5, waterTiles: not null } && volcano.waterTiles[x, y])
                || StaticTerrainPassable(loc, x, y));
            AreaCache[key] = (loc.Map, grid, doorMask, gateCount);
            return grid;
        }

        /// <summary>
        /// 计算室内门的可开状态掩码：既支持未进过室内（loc.interiorDoors 尚未填充、只从地图属性 Doors 读取）的情况，
        /// 又能让有室内门的地图同样命中缓存，且在好感/解锁条件变化时自动失效重算。
        /// </summary>
        private static long ComputeDoorMask(GameLocation loc, string key)
        {
            if (loc.isOutdoors.Value)
                return 0;
            if (!DoorActionCache.TryGetValue(key, out var doors))
            {
                doors = new List<string[]>();
                var seen = new HashSet<Point>();
                foreach (var p in InteriorDoorDictionary.GetDoorTilesFromMapProperty(loc))
                {
                    if (!seen.Add(p))
                        continue;
                    if (loc.doesTileHaveProperty(p.X, p.Y, "Action", "Buildings") is { } action
                        && ArgUtility.SplitBySpace(action) is { Length: > 0 } parts
                        && parts[0] == "Door")
                        doors.Add(parts);
                }
                DoorActionCache[key] = doors;
            }
            long mask = 0;
            int n = Math.Min(doors.Count, 62);
            for (int i = 0; i < n; i++)
            {
                if (Pathfinder.CanOpenDoor(loc, doors[i]))
                    mask |= 1L << i;
            }
            return mask;
        }

        /// <summary>
        /// 只看静态地图地形（悬崖、墙、未修好的桥、水域与锁着的室内门），忽略杂草/树枝/树木/石块等动态障碍，
        /// 避免农场长满杂物时把连通的地图误切成孤岛。
        /// </summary>
        private static bool StaticTerrainPassable(GameLocation loc, int x, int y)
        {
            if (!loc.isTileOnMap(x, y))
                return false;
            var p = new Point(x, y);
            if (Pathfinder.InteriorDoor(loc, p) is { } door)
                return Pathfinder.CanOpenDoor(loc, door);
            // 未进过室内地图前 loc.interiorDoors 为空，直接按 Buildings 层 Action="Door ..." 判断室内门是否可开
            if (!loc.isOutdoors.Value
                && loc.doesTileHaveProperty(x, y, "Action", "Buildings") is { } action
                && ArgUtility.SplitBySpace(action) is { Length: > 0 } doorParts
                && doorParts[0] == "Door")
                return Pathfinder.CanOpenDoor(loc, doorParts);
            if (!loc.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport))
                return false;
            // 普通水域不可走，但盖在水上的桥（Buildings 层有 Passable=T）能走
            if (loc.isWaterTile(x, y) && loc.doesTileHaveProperty(x, y, "Passable", "Buildings") is not ("T" or "True"))
                return false;
            return true;
        }

        /// <summary>计算二维网格的四邻域连通分量（可走格从 1 开始编号，不可走格为 0）。纯算法，供离线单测直接验证。</summary>
        internal static int[,] ComputeComponents(int width, int height, Func<int, int, bool> passable)
        {
            var grid = new int[width, height];
            var queue = new Queue<Point>();
            int nextId = 0;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (grid[x, y] != 0 || !passable(x, y))
                        continue;
                    nextId++;
                    grid[x, y] = nextId;
                    queue.Enqueue(new Point(x, y));
                    while (queue.Count > 0)
                    {
                        var cur = queue.Dequeue();
                        foreach (var d in Pathfinder.Dirs)
                        {
                            int nx = cur.X + d.X, ny = cur.Y + d.Y;
                            if (nx >= 0 && ny >= 0 && nx < width && ny < height && grid[nx, ny] == 0 && passable(nx, ny))
                            {
                                grid[nx, ny] = nextId;
                                queue.Enqueue(new Point(nx, ny));
                            }
                        }
                    }
                }
            }
            return grid;
        }

        /// <summary>
        /// 查 tile 所在的连通分量编号；如果 tile 在地图外（边缘传送点）或本身不可站（门/下水道口），
        /// 先夹回地图内再按优先南侧的顺序找半径 2 以内最近的可站格。找不到返回 0。
        /// </summary>
        internal static int AreaFromGrid(int[,] grid, int width, int height, Point tile)
        {
            if (width <= 0 || height <= 0)
                return 0;
            int cx = Math.Clamp(tile.X, 0, width - 1);
            int cy = Math.Clamp(tile.Y, 0, height - 1);
            if (grid[cx, cy] > 0)
                return grid[cx, cy];
            // 门通常从南侧进入，先查南侧，再查其余三向及半径 2
            var probes = new[] { new Point(0, 1), new Point(0, -1), new Point(1, 0), new Point(-1, 0) };
            for (int r = 1; r <= 2; r++)
            {
                foreach (var d in probes)
                {
                    int nx = cx + d.X * r, ny = cy + d.Y * r;
                    if (nx >= 0 && ny >= 0 && nx < width && ny < height && grid[nx, ny] > 0)
                        return grid[nx, ny];
                }
            }
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx >= 0 && ny >= 0 && nx < width && ny < height && grid[nx, ny] > 0)
                        return grid[nx, ny];
                }
            return 0;
        }

        /// <summary>火山跨层传送的落点按 VolcanoDungeon.resetLocalState 换算。执行传送时仍使用地图原坐标。</summary>
        internal static Point RouteArrivalTile(string fromName, GameLocation destination, Point warpTile)
        {
            if (destination is not VolcanoDungeon volcano)
                return warpTile;
            if (fromName == "Caldera" && volcano.level.Value == 0)
                return new Point(44, 50);
            return warpTile.Y switch
            {
                0 => volcano.endPosition ?? Point.Zero,
                1 => volcano.startPosition ?? Point.Zero,
                -1 when fromName == "Caldera" => volcano.endPosition ?? Point.Zero,
                _ => warpTile
            };
        }

        /// <param name="avoid">这次走不通的出口（ExitKey），规划时绕开。</param>
        /// <param name="avoidLocation">路过不了的地图（比如在布置节日的小镇），除非它就是目的地。</param>
        public static List<Exit> PlanRoute(GameLocation from, string targetName, ISet<string> avoid = null, string avoidLocation = null)
        {
            if (from.NameOrUniqueName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                return new List<Exit>();

            // 火山的层每层都要现生成地图，别为了找路把整座火山都生成一遍：
            // 只在要去火山里某层时往上找到那层为止，人在火山里时只往下走回入口
            // 火山口（Caldera）算第 10 层，它有一条直接回第 0 层的捷径；第 0 层只有一层，总是可以走
            bool fromVolcano = StardewValley.Locations.VolcanoDungeon.IsGeneratedLevel(from.NameOrUniqueName, out int fromLevel);
            if (from is StardewValley.Locations.Caldera)
            {
                fromVolcano = true;
                fromLevel = 10;
            }
            bool toVolcano = StardewValley.Locations.VolcanoDungeon.IsGeneratedLevel(targetName, out int toLevel);
            bool VolcanoOk(string name) =>
                !StardewValley.Locations.VolcanoDungeon.IsGeneratedLevel(name, out int n)
                || n == 0 || (toVolcano && n <= toLevel) || (fromVolcano && n < fromLevel);

            // 路线图的点是“地图#区域”：同一张地图被墙隔成互不相通的几块时（见 Area），只走同一块里的出口
            string Key(string n, int area) => n + "#" + area;
            var prev = new Dictionary<string, (string, Exit)>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string name, int area, int depth)>();
            int startArea = from == Game1.currentLocation && Game1.player != null ? Area(from, Game1.player.TilePoint) : 0;
            queue.Enqueue((from.NameOrUniqueName, startArea, 0));
            prev[Key(from.NameOrUniqueName, startArea)] = (null, null);

            while (queue.Count > 0)
            {
                var (name, area, depth) = queue.Dequeue();
                if (depth >= 24)
                    continue;
                var loc = Game1.getLocationFromName(name);
                if (loc == null)
                    continue;
                // 好感不够进不去的门（比如玛鲁房间的侧门）不走，同一个目标通常还有正门
                // 去同一个地方有正门也有侧门（比如玛鲁房间的侧门也通科学之家）时先走正门：要好感的门排在后面
                foreach (var e in Get(loc).Where(e => e.FriendshipOk(loc)).OrderBy(e => e.FriendNpc != null))
                {
                    int exitArea = Area(loc, e.Tile);
                    if (area != 0 && exitArea != 0 && exitArea != area)
                        continue;
                    if (avoid != null && avoid.Contains(ExitKey(name, e)))
                        continue;
                    if (!VolcanoOk(e.Target))
                        continue;
                    if (avoidLocation != null && e.Target.Equals(avoidLocation, StringComparison.OrdinalIgnoreCase)
                        && !e.Target.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int nextArea = 0;
                    if (e.Kind != ExitKind.BuildingDoor && (e.TargetTile != Point.Zero || VolcanoDungeon.IsGeneratedLevel(e.Target)))
                    {
                        var destination = Game1.getLocationFromName(e.Target);
                        var arrival = RouteArrivalTile(loc.Name, destination, e.TargetTile);
                        if (arrival != Point.Zero)
                            nextArea = Area(destination, arrival);
                    }
                    string next = Key(e.Target, nextArea);
                    if (prev.ContainsKey(next))
                        continue;
                    prev[next] = (Key(name, area), e);
                    if (e.Target.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        var route = new List<Exit>();
                        string cur = next;
                        while (prev[cur].Item2 != null)
                        {
                            route.Add(prev[cur].Item2);
                            cur = prev[cur].Item1;
                        }
                        route.Reverse();
                        return route;
                    }
                    queue.Enqueue((e.Target, nextArea, depth + 1));
                }
            }
            return null;
        }
    }
}
