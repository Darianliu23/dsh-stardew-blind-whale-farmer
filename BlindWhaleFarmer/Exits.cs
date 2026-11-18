using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;

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
        private static readonly Dictionary<string, List<Exit>> TileExitCache = new();

        public static void ClearCache() => TileExitCache.Clear();

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
                if (w.npcOnly.Value || (!StardewValley.Locations.VolcanoDungeon.IsGeneratedLevel(target) && Game1.getLocationFromName(target) == null))
                    continue;
                list.Add(new Exit { Kind = ExitKind.Warp, Tile = new Point(w.X, w.Y), Target = target, TargetTile = new Point(w.TargetX, w.TargetY) });
            }

            // 去沙漠的巴士要等金库收集包（或 Joja 的巴士项目）完成、巴士修好以后才有
            // 威利的后屋（船坞）要收到威利的邀请信才进得去；去姜岛的船要修好才开
            list.AddRange(GetTileExits(loc).Where(e =>
                (e.Kind != ExitKind.Bus || e.Target != "Desert" || BusRepaired)
                && (e.Target != "BoatTunnel" || e.Kind != ExitKind.Door || Game1.player.mailReceived.Contains("willyBackRoomInvitation"))
                && (e.Kind != ExitKind.Bus || e.Target != "IslandSouth" || BoatRepaired)
                && (e.Target != "QiNutRoom" || StardewValley.Locations.IslandWest.IsQiWalnutRoomDoorUnlocked(out _))
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
            if (TileExitCache.TryGetValue(key, out var cached))
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
                    if (!string.IsNullOrEmpty(touch))
                    {
                        var parts = touch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        // 沙漠里停着的巴士：走到车门那一格就问“是否返回星露谷”
                        if (parts.Length >= 1 && parts[0] == "DesertBus" && Game1.getLocationFromName("BusStop") != null)
                            list.Add(new Exit { Kind = ExitKind.Bus, Tile = new Point(x, y), Target = "BusStop", IsTouch = true, Note = "（坐巴士回巴士站，免费）" });
                        // 姜岛南边码头的船：走上去问“要不要回去”，坐船回威利的船坞（免费）
                        if (parts.Length >= 1 && parts[0] == "LeaveIsland" && Game1.getLocationFromName("BoatTunnel") != null)
                            list.Add(new Exit { Kind = ExitKind.Bus, Tile = new Point(x, y), Target = "BoatTunnel", IsTouch = true, Note = "（坐威利的船回鹈鹕镇，免费）" });
                        if (parts.Length >= 4 && (parts[0] == "Warp" || parts[0] == "MagicWarp")
                            && Game1.getLocationFromName(parts[1]) != null
                            && int.TryParse(parts[2], out int tx) && int.TryParse(parts[3], out int ty))
                        {
                            list.Add(new Exit { Kind = ExitKind.Warp, Tile = new Point(x, y), Target = parts[1], TargetTile = new Point(tx, ty), IsTouch = true });
                        }
                    }
                }
            }
            TileExitCache[key] = list;
            return list;
        }

        private static Exit ParseDoorAction(string action, int x, int y)
        {
            var p = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0)
                return null;
            switch (p[0])
            {
                case "Warp":
                case "LockedDoorWarp":
                case "WarpMensLocker":
                case "WarpWomensLocker":
                    if (p.Length >= 4 && Game1.getLocationFromName(p[3]) != null
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
                case "WarpCommunityCenter":
                    return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "CommunityCenter" };
                // 皮埃尔家通往卡洛琳阳光房的门（GameLocation.performAction，和卡洛琳 2 心以上才开）
                case "Warp_Sunroom_Door" when Game1.getLocationFromName("Sunroom") != null:
                    return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "Sunroom", TargetTile = new Point(5, 13),
                        FriendNpc = "Caroline", FriendPoints = 500, Note = "（卡洛琳的阳光房，要和卡洛琳有 2 心好感）" };
                // 威利鱼店里通往后屋船坞的门（FishShop.performAction，收到邀请信才开）
                case "WarpBoatTunnel" when Game1.getLocationFromName("BoatTunnel") != null:
                    return new Exit { Kind = ExitKind.Door, Tile = new Point(x, y), Target = "BoatTunnel", TargetTile = new Point(6, 12), Note = "（威利的船坞）" };
                // 船坞的售票机：买票坐船去姜岛（BoatTunnel.checkAction，船修好以后）
                case "BoatTicket" when Game1.getLocationFromName("IslandSouth") != null:
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

        /// <param name="avoid">这次走不通的出口（ExitKey），规划时绕开。</param>
        public static List<Exit> PlanRoute(GameLocation from, string targetName, ISet<string> avoid = null)
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

            var prev = new Dictionary<string, (string, Exit)>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string name, int depth)>();
            queue.Enqueue((from.NameOrUniqueName, 0));
            prev[from.NameOrUniqueName] = (null, null);

            while (queue.Count > 0)
            {
                var (name, depth) = queue.Dequeue();
                if (depth >= 12)
                    continue;
                var loc = Game1.getLocationFromName(name);
                if (loc == null)
                    continue;
                // 好感不够进不去的门（比如玛鲁房间的侧门）不走，同一个目标通常还有正门
                foreach (var e in Get(loc).Where(e => e.FriendshipOk(loc)))
                {
                    if (avoid != null && avoid.Contains(ExitKey(name, e)))
                        continue;
                    if (prev.ContainsKey(e.Target) || !VolcanoOk(e.Target))
                        continue;
                    prev[e.Target] = (name, e);
                    if (e.Target.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        var route = new List<Exit>();
                        string cur = e.Target;
                        while (prev[cur].Item2 != null)
                        {
                            route.Add(prev[cur].Item2);
                            cur = prev[cur].Item1;
                        }
                        route.Reverse();
                        return route;
                    }
                    queue.Enqueue((e.Target, depth + 1));
                }
            }
            return null;
        }
    }
}
