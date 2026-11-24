using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        internal static volatile string AiHomeLocation;
        internal static volatile string CoSleepDefault;

        private static readonly Random CoSleepRng = new();

        private static bool IsAiHomeAlias(string name) =>
            name?.Trim() is { } key && (key == "回家" || key.Equals("FarmHouse", StringComparison.OrdinalIgnoreCase));

        private static bool IsIslandContext(GameLocation loc) =>
            loc != null && (loc.InIslandContext() || loc is IslandLocation);

        internal static bool ShouldPreferIslandHome(bool inIslandContext, bool farmhouseRestored, bool hasIslandBed) =>
            inIslandContext && farmhouseRestored && hasIslandBed;

        private static (GameLocation house, Point bed, bool configured, string note) ResolveSleepHome()
        {
            if (IsIslandContext(Loc) && ResolveIslandHome() is { } island)
                return (island.house, island.bed, false, null);
            return ResolveAiHome();
        }

        private static (IslandFarmHouse house, Point bed)? ResolveIslandHome()
        {
            if (Game1.getLocationFromName("IslandWest") is not IslandWest west || !west.farmhouseRestored.Value)
                return null;
            if (Game1.getLocationFromName("IslandFarmHouse") is not IslandFarmHouse house)
                return null;
            // 岛上小屋修好后，主机第一次进门才放床；之前（未 visited）只有主机进去才会立刻摆上 (22,3) 的床（睡点 (23,4)）。
            var beds = house.furniture.OfType<BedFurniture>().Where(b => b.bedType != BedFurniture.BedType.Child).ToList();
            bool hasBed = beds.Count > 0 || (!house.visited.Value && Game1.IsMasterGame);
            if (!ShouldPreferIslandHome(IsIslandContext(Loc), west.farmhouseRestored.Value, hasBed))
                return null;
            var from = Loc is MineShaft mine
                ? Game1.getLocationFromName(mine.getMineArea() == 121 ? "SkullCave" : "Mine")
                : Loc;
            if (from == null)
                return null;
            var route = from != house ? Exits.PlanRoute(from, house.NameOrUniqueName) : null;
            if (from != house && route is not { Count: > 0 })
                return null;
            if (beds.Count == 0)
                return (house, new Point(23, 4));
            Point? entry = Loc == house ? Player.TilePoint : ExitArrivalTile(house, route[^1]);
            if (entry is Point start && house.Map != null && house.isTileOnMap(start.X, start.Y)
                && FindAiBedPath(house, start) is { } path)
                return (house, path[^1]);
            return null;
        }

        private static (GameLocation house, Point bed, bool configured, string note) ResolveAiHome()
        {
            var registered = Utility.getHomeOfFarmer(Player);
            var fallback = ((GameLocation)registered, bed: registered.GetPlayerBedSpot(), configured: false, note: (string)null);
            string name = AiHomeLocation?.Trim();
            if (!Context.IsMultiplayer || string.IsNullOrEmpty(name))
                return fallback;

            var location = Game1.getLocationFromName(name);
            string reason;
            if (location == null || !location.NameOrUniqueName.Equals(name, StringComparison.OrdinalIgnoreCase))
                reason = "找不到这个唯一地图名";
            else if (location is not FarmHouse house)
                reason = "地点不是 FarmHouse 或 Cabin";
            else
            {
                // 普通矿层没有跨地图出口，原版从入口梯子先退回洞口。
                var from = Loc is MineShaft mine
                    ? Game1.getLocationFromName(mine.getMineArea() == 121 ? "SkullCave" : "Mine")
                    : Loc;
                var route = from != null && from != house ? Exits.PlanRoute(from, house.NameOrUniqueName) : null;
                Point? entry = Loc == house ? Player.TilePoint : route is { Count: > 0 } ? ExitArrivalTile(house, route[^1]) : null;
                if (entry is not Point start || house.Map == null || !house.isTileOnMap(start.X, start.Y))
                    reason = "找不到能进屋的路线";
                else if (!house.furniture.OfType<BedFurniture>().Any(b => b.bedType != BedFurniture.BedType.Child))
                    reason = "屋里没有可以睡的床";
                else if (FindAiBedPath(house, start) is { } path)
                    return (house, path[^1], true, null);
                else
                    reason = "屋里的床走不到";
            }

            fallback.note = $"AiHomeLocation「{name}」不可用（{reason}），改回游戏登记的家 {registered.NameOrUniqueName}";
            return fallback;
        }

        internal static List<Point> OrderBedSpots(Point bedTile, int tilesWide, bool isDouble, IReadOnlyCollection<Point> occupiedTiles)
        {
            int y = bedTile.Y + 1;
            int width = Math.Max(2, tilesWide);
            var candidates = new List<Point>();
            if (isDouble && width >= 3)
            {
                var left = new Point(bedTile.X, y);
                var center = new Point(bedTile.X + 1, y);
                var right = new Point(bedTile.X + 2, y);
                bool leftOrCenterBusy = occupiedTiles != null && occupiedTiles.Any(p => p.Y == y && p.X >= bedTile.X && p.X <= bedTile.X + 1);
                bool rightBusy = occupiedTiles != null && occupiedTiles.Any(p => p.Y == y && p.X >= bedTile.X + 1 && p.X <= bedTile.X + width - 1);
                if (leftOrCenterBusy && !rightBusy)
                {
                    candidates.Add(right);
                    candidates.Add(center);
                    candidates.Add(left);
                }
                else if (rightBusy && !leftOrCenterBusy)
                {
                    candidates.Add(left);
                    candidates.Add(center);
                    candidates.Add(right);
                }
                else
                {
                    candidates.Add(center);
                    candidates.Add(left);
                    candidates.Add(right);
                }
            }
            else
            {
                var spot = new Point(bedTile.X + 1, y);
                var alt = new Point(bedTile.X, y);
                bool spotBusy = occupiedTiles != null && occupiedTiles.Contains(spot);
                bool altBusy = occupiedTiles != null && occupiedTiles.Contains(alt);
                if (spotBusy && !altBusy)
                {
                    candidates.Add(alt);
                    candidates.Add(spot);
                }
                else
                {
                    candidates.Add(spot);
                    candidates.Add(alt);
                }
            }
            return candidates;
        }

        private static (GameLocation house, Point bed, bool configured, bool explicitTarget, string note, string error) ResolveSleepTarget(string withPlayer, string houseName)
        {
            string with = (withPlayer ?? "").Trim();
            string locName = (houseName ?? "").Trim();
            bool explicitTarget = with.Length > 0 || locName.Length > 0;

            if (!explicitTarget && Context.IsMultiplayer && !string.IsNullOrWhiteSpace(CoSleepDefault))
            {
                string def = CoSleepDefault.Trim();
                // 默认配置既可填玩家名 / random，也可填房子唯一地图名
                if (def.Equals("random", StringComparison.OrdinalIgnoreCase) || def == "随机")
                    with = def;
                else if (Game1.getLocationFromName(def) is FarmHouse or IslandFarmHouse)
                    locName = def;
                else
                    with = def;
            }

            if (with.Length == 0 && locName.Length == 0)
            {
                var defHome = ResolveSleepHome();
                return (defHome.house, defHome.bed, defHome.configured, false, defHome.note, null);
            }

            GameLocation targetHouse = null;
            string targetLabel = null;
            if (locName.Length > 0)
            {
                targetHouse = Game1.getLocationFromName(locName);
                if (targetHouse == null)
                {
                    string err = $"找不到名为「{locName}」的房子（请传 FarmHouse、IslandFarmHouse 或小屋的唯一地图名）";
                    return FallbackOrError(explicitTarget, err);
                }
                if (targetHouse is not FarmHouse && targetHouse is not IslandFarmHouse
                    && !targetHouse.furniture.OfType<BedFurniture>().Any(b => b.bedType != BedFurniture.BedType.Child))
                {
                    string err = $"「{locName}」不是可睡觉的房子，也没有成人床";
                    return FallbackOrError(explicitTarget, err);
                }
                targetLabel = targetHouse.NameOrUniqueName;
            }
            else
            {
                if (!Context.IsMultiplayer)
                    return FallbackOrError(explicitTarget, "当前不是联机模式，没有其他玩家可一起睡");

                var onlineOthers = Game1.getOnlineFarmers().Where(f => f != null && f != Player).ToList();
                Farmer targetFarmer = null;
                if (with.Equals("random", StringComparison.OrdinalIgnoreCase) || with == "随机")
                {
                    if (onlineOthers.Count == 0)
                    {
                        if (!explicitTarget)
                        {
                            var defHome = ResolveSleepHome();
                            return (defHome.house, defHome.bed, defHome.configured, false, defHome.note, null);
                        }
                        return (null, Point.Zero, false, true, null, "现在没有其他玩家在线，没法随机找人一起睡");
                    }
                    targetFarmer = onlineOthers[CoSleepRng.Next(onlineOthers.Count)];
                }
                else
                {
                    var allOthers = Game1.getAllFarmers().Where(f => f != null && f != Player).ToList();
                    targetFarmer = MatchFarmer(onlineOthers, with) ?? MatchFarmer(allOthers, with);
                    if (targetFarmer == null)
                    {
                        string names = onlineOthers.Count > 0
                            ? "当前在线的其他玩家：" + string.Join("、", onlineOthers.Select(f => f.Name))
                            : "当前没有其他玩家在线";
                        return FallbackOrError(explicitTarget, $"找不到叫「{with}」的玩家（{names}）");
                    }
                }

                // 如果对方已经在某间有成人床的屋里躺下了，优先去对方现在躺着的那间屋子
                if (targetFarmer.isInBed.Value && targetFarmer.currentLocation != null
                    && targetFarmer.currentLocation.furniture.OfType<BedFurniture>().Any(b => b.bedType != BedFurniture.BedType.Child))
                {
                    targetHouse = targetFarmer.currentLocation;
                }
                else
                {
                    targetHouse = Utility.getHomeOfFarmer(targetFarmer);
                }
                if (targetHouse == null)
                    return FallbackOrError(explicitTarget, $"找不到 {targetFarmer.Name} 的房子");
                targetLabel = $"{targetFarmer.Name} 的屋子（{targetHouse.NameOrUniqueName}）";
            }

            var from = Loc is MineShaft mine
                ? Game1.getLocationFromName(mine.getMineArea() == 121 ? "SkullCave" : "Mine")
                : Loc;
            var route = from != null && from != targetHouse ? Exits.PlanRoute(from, targetHouse.NameOrUniqueName) : null;
            Point? entry = Loc == targetHouse ? Player.TilePoint : route is { Count: > 0 } ? ExitArrivalTile(targetHouse, route[^1]) : null;
            if (entry is not Point start || targetHouse.Map == null || !targetHouse.isTileOnMap(start.X, start.Y))
                return FallbackOrError(explicitTarget, $"走不到 {targetLabel}（找不到进屋路线）");

            var adultBeds = targetHouse.furniture.OfType<BedFurniture>().Where(b => b.bedType != BedFurniture.BedType.Child).ToList();
            if (adultBeds.Count == 0)
            {
                string tip = $"{targetLabel}里没有可以睡的成人床（儿童床不能睡）。请屋主在屋里摆一张单人床或双人床（双人床可两人同睡），或者先改睡自己的家";
                return FallbackOrError(explicitTarget, tip);
            }

            var path = FindAiBedPath(targetHouse, start);
            if (path == null)
                return FallbackOrError(explicitTarget, $"{targetLabel}里的床被家具或杂物挡住了走不到，请屋主挪开床边障碍物，或改睡别的房子");

            string note = explicitTarget
                ? $"准备去 {targetLabel} 睡觉"
                : $"按 CoSleepDefault 设置去 {targetLabel} 睡觉";
            return (targetHouse, path[^1], true, explicitTarget, note, null);
        }

        private static (GameLocation house, Point bed, bool configured, bool explicitTarget, string note, string error) FallbackOrError(bool explicitTarget, string reason)
        {
            if (explicitTarget)
                return (null, Point.Zero, false, true, null, reason);
            var defHome = ResolveSleepHome();
            string note = $"CoSleepDefault「{CoSleepDefault}」不可用（{reason}），改回默认睡眠地点 {defHome.house.NameOrUniqueName}";
            if (!string.IsNullOrEmpty(defHome.note))
                note += "；" + defHome.note;
            return (defHome.house, defHome.bed, defHome.configured, false, note, null);
        }

        private static Farmer MatchFarmer(IReadOnlyList<Farmer> farmers, string query)
        {
            if (farmers == null || farmers.Count == 0 || string.IsNullOrWhiteSpace(query))
                return null;
            string q = query.Trim();
            return farmers.FirstOrDefault(f => string.Equals(f.Name, q, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.displayName, q, StringComparison.OrdinalIgnoreCase))
                ?? farmers.FirstOrDefault(f => (f.Name?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (f.displayName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
        }

        private static List<Point> FindAiBedPath(GameLocation house, Point start)
        {
            // 联机同睡时避开已经被其他玩家占用的床位；双人床有人躺着时优先选另一边空位。
            var beds = house.furniture.OfType<BedFurniture>().Where(b => b.bedType != BedFurniture.BedType.Child).ToList();
            if (beds.Count == 0)
                return null;
            var primary = (house as FarmHouse)?.GetPlayerBed();
            var otherFarmers = house.farmers.Where(f => f != null && f != Player).ToList();
            var occupiedTiles = new HashSet<Point>(otherFarmers.Select(f => f.TilePoint));

            int BedPriority(BedFurniture b)
            {
                var box = b.GetBoundingBox();
                int onBed = otherFarmers.Count(f => f.GetBoundingBox().Intersects(box));
                if (b.bedType == BedFurniture.BedType.Double)
                {
                    if (onBed == 1)
                        return 0; // 双人床有 1 人躺着：优先睡另一边（一起睡）
                    if (onBed == 0)
                        return b == primary ? 1 : 2;
                    return 5;
                }
                if (onBed == 0)
                    return b == primary ? 1 : 3;
                return 6; // 单人床已有人占着，排最后
            }

            foreach (var bed in beds.OrderBy(BedPriority))
            {
                var orderedSpots = OrderBedSpots(
                    new Point((int)bed.TileLocation.X, (int)bed.TileLocation.Y),
                    bed.getTilesWide(),
                    bed.bedType == BedFurniture.BedType.Double,
                    occupiedTiles);
                var validSpots = orderedSpots
                    .Where(p => house.isTileOnMap(p.X, p.Y) && BedFurniture.IsBedHere(house, p.X, p.Y))
                    .ToList();

                // 先逐个尝试未被占用的首选位置，避免寻路只挑最近但已重叠的那一侧
                foreach (var spot in validSpots.Where(p => !occupiedTiles.Contains(p)))
                {
                    if (Pathfinder.FindPath(house, start, new[] { spot }) is { } path)
                        return path;
                }
                if (validSpots.Count > 0 && Pathfinder.FindPath(house, start, validSpots.ToArray()) is { } fallbackPath)
                    return fallbackPath;
            }
            return null;
        }
    }
}
