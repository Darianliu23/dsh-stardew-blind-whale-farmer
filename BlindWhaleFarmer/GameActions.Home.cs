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

        private static (FarmHouse house, Point bed, bool configured, string note) ResolveAiHome()
        {
            var registered = Utility.getHomeOfFarmer(Player);
            var fallback = (house: registered, bed: registered.GetPlayerBedSpot(), configured: false, note: (string)null);
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

        private static List<Point> FindAiBedPath(FarmHouse house, Point start)
        {
            // 原版按房屋升级等级选主床。主床不可用时，也可以走到别的成人床。
            var beds = house.furniture.OfType<BedFurniture>().Where(b => b.bedType != BedFurniture.BedType.Child);
            var primary = house.GetPlayerBed();
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
