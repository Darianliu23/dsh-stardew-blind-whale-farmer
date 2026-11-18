using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// 骑马赶路：travel_to 跨地图走室外的路时，马就在附近就先骑上（速度大约快一倍）。
    /// 骑着马用不了工具、开不了箱子、捡不了东西（GameLocation.checkAction 里骑马时直接返回或者变成下马），
    /// 所以到了目的地、要进门、坐车坐船之前都先下马，马留在原地（第二天游戏会把它送回马厩）。
    /// </summary>
    internal sealed partial class GameActions
    {
        /// <summary>马离人多远（格）以内才值得先走过去骑。</summary>
        private const int HorseMaxDetour = 25;

        /// <summary>剩下的路（格，直线距离加起来）至少这么长才骑：路短的话上下马的工夫都省不回来。</summary>
        private const int HorseMinTrip = 45;

        /// <summary>这次 travel 里上马没上成的原因（马没起名之类），接在结果后面，一次 travel 只说一次。</summary>
        private string horseNote;

        /// <summary>当前地图上她自己的马（没人骑、有马厩）。</summary>
        private static Horse MyHorseHere() =>
            Loc.characters.OfType<Horse>().FirstOrDefault(h => h.rider == null && !h.IsInvisible
                && (h.getOwner() == Player || h.getOwner() == null) && h.TryFindStable() != null);

        /// <summary>路线上剩下的路有多长（格，按直线距离加起来）。</summary>
        private static int RouteTiles(List<Exit> route)
        {
            Point cur = Player.TilePoint;
            int tiles = 0;
            foreach (var e in route)
            {
                tiles += Util.Manhattan(cur, e.Tile);
                cur = e.TargetTile;
            }
            return tiles;
        }

        /// <summary>要走室外的路、马就在旁边、路又够远：走过去骑上。上不了马不算失败，接着走路。</summary>
        private IEnumerable<object> MaybeMountHorse(List<Exit> route)
        {
            if (Player.isRidingHorse() || !Loc.IsOutdoors || Game1.eventUp || route.Count == 0)
                yield break;
            var horse = MyHorseHere();
            if (horse == null)
                yield break;
            // 只算能一路骑过去的路：室外连着的几段，再加上骑到第一扇门（巴士、进屋的出口）门口
            int rideable = route.TakeWhile(e => e.Kind == ExitKind.Warp && (Game1.getLocationFromName(e.Target)?.IsOutdoors ?? false)).Count();
            int trip = RouteTiles(route.Take(rideable + 1).ToList());
            var horseTile = horse.TilePoint;
            var toHorse = Pathfinder.FindPath(Loc, Player.TilePoint, Pathfinder.AdjacentStandTiles(Loc, horseTile));
            if (toHorse == null || toHorse.Count - 1 > HorseMaxDetour || trip < HorseMinTrip + toHorse.Count)
                yield break;
            if (horse.Name.Length == 0 || string.IsNullOrEmpty(Player.horseName.Value))
            {
                // 第一次骑会弹起名字的界面，这是玩家自己的事，别替她起
                horseNote ??= "（附近有马，但还没起名字：玩家去马旁边按一下给它起个名，以后赶路就会骑马）";
                yield break;
            }
            foreach (var s in MountHorse(horse, toHorse))
                yield return s;
        }

        /// <summary>走到马旁边骑上（path 是走到马旁边的路，null 就现找）。骑不上就算了，接着走路。</summary>
        private IEnumerable<object> MountHorse(Horse horse, List<Point> path = null)
        {
            var horseTile = horse.TilePoint;
            path ??= Pathfinder.FindPath(Loc, Player.TilePoint, Pathfinder.AdjacentStandTiles(Loc, horseTile));
            if (path == null)
                yield break;
            var o = new Outcome();
            foreach (var s in WalkPath(path, o))
                yield return s;
            if (!o.Ok || Loc != horse.currentLocation)
                yield break;
            // 手里拿着帽子或胡萝卜时，对马交互会变成给马戴帽子、喂胡萝卜：先换成别的
            if (Player.CurrentItem is Hat || Player.CurrentItem?.QualifiedItemId == "(O)Carrot")
            {
                int other = Enumerable.Range(0, Math.Min(12, Player.Items.Count))
                    .FirstOrDefault(i => Player.Items[i] is null or Tool, -1);
                if (other < 0)
                    yield break;
                Player.CurrentToolIndex = other;
            }
            Face(horseTile);
            horse.checkAction(Player, Loc);
            for (int t = 0; t < 180 && !(Player.mount != null && !Player.isAnimatingMount); t++)
            {
                if (Game1.activeClickableMenu is NamingMenu nm)
                {
                    nm.exitThisMenu(false);
                    horseNote ??= "（附近有马，但还没起名字：玩家去马旁边按一下给它起个名，以后赶路就会骑马）";
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>骑着马就下来（马留在旁边）。进门、坐车、到了目的地要干活之前用。</summary>
        private IEnumerable<object> Dismount()
        {
            var horse = Player.mount;
            if (horse == null)
                yield break;
            Player.Halt();
            horse.checkAction(Player, Loc);
            for (int t = 0; t < 120 && Player.mount != null; t++)
                yield return null;
            // 下马动画没走完（找不到空地落脚之类）：直接下
            if (Player.mount != null)
                Player.mount.dismount();
            Player.canMove = true;
            SnapToTile(Player.TilePoint);
        }
    }
}
