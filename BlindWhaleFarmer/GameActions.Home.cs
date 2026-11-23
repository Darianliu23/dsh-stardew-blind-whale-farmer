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

        private static List<Point> FindAiBedPath(GameLocation house, Point start)
        {
            // 原版按房屋升级等级选主床。主床不可用时，也可以走到别的成人床。
            var beds = house.furniture.OfType<BedFurniture>().Where(b => b.bedType != BedFurniture.BedType.Child);
            var primary = (house as FarmHouse)?.GetPlayerBed();
            foreach (var bed in beds.OrderBy(b => b == primary ? 0 : 1))
            {
                var spot = bed.GetBedSpot();
                var spots = bed.bedType == BedFurniture.BedType.Single ? new[] { spot, new Point(spot.X - 1, spot.Y) } : new[] { spot };
                var goals = spots.Where(p => house.isTileOnMap(p.X, p.Y) && BedFurniture.IsBedHere(house, p.X, p.Y)).ToArray();
                if (Pathfinder.FindPath(house, start, goals) is { } path)
                    return path;
            }
            return null;
        }
    }
}
