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
        }

        private static Farmer Player => Game1.player;
        private static GameLocation Loc => Game1.currentLocation;

        // ───────────────────────── 基础工具 ─────────────────────────

        private static bool Blocked(out string why)
        {
            // 暂停/背包菜单（E、Esc 打开的 GameMenu）对 AI 干活没用，被谁按出来的都直接关掉继续
            if (Game1.activeClickableMenu is GameMenu pause && pause.readyToClose())
                pause.exitThisMenu(playSound: false);
            if (Game1.activeClickableMenu != null)
            {
                why = "有界面打开了（" + Game1.activeClickableMenu.GetType().Name + "），请先处理界面";
                return true;
            }
            if (Game1.eventUp)
            {
                why = "正在播放剧情事件";
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
                o.Fail($"找不到通往{what}的路（{WhyUnreachable(goals)}）");
                yield break;
            }
            foreach (var x in WalkPath(path, o))
                yield return x;
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
            return $"被挡住了。从 ({me.X},{me.Y}) 能走到 {reachable} 格，其中离目标最近的是 ({closest.X},{closest.Y})，还差 {dist} 格";
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
        private IEnumerable<object> UseToolOnce(int toolIndex, Point target, Outcome o)
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

            // 升级过的工具会进入“蓄力”状态，等待松开按键；这里直接松开，只作用一格
            if (tool is not MeleeWeapon && Player.UsingTool && Player.canReleaseTool)
                Player.EndUsingTool();

            for (int i = 0; i < 180 && (Player.UsingTool || Player.FarmerSprite.isUsingWeapon()); i++)
                yield return null;
            foreach (var _ in WaitTicks(4))
                yield return null;
            o.Succeed();
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

        public IEnumerable<object> TravelTo(string name)
        {
            var o = new Outcome();
            foreach (var s in TravelInner(name, o))
                yield return s;
            yield return o.Ok ? ActionResult.Success(o.Message) : ActionResult.Fail(o.Message);
        }

        private IEnumerable<object> TravelInner(string name, Outcome o)
        {
            var target = ResolveLocation(name);
            if (target == null)
            {
                o.Fail($"不认识地点“{name}”，请使用英文内部名，例如 Farm、FarmHouse、Town、SeedShop、Beach、Mountain、Forest、BusStop");
                yield break;
            }
            string targetName = target.NameOrUniqueName;
            for (int hop = 0; hop < 15; hop++)
            {
                if (Loc.NameOrUniqueName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    var t = Player.TilePoint;
                    o.Succeed($"已到达 {targetName}，位置 ({t.X},{t.Y})");
                    yield break;
                }
                var route = Exits.PlanRoute(Loc, targetName);
                if (route == null || route.Count == 0)
                {
                    o.Fail($"找不到从 {Loc.NameOrUniqueName} 到 {targetName} 的路线");
                    yield break;
                }
                // 节日进行中走进节日地图会直接开始节日：时间停住、普通动作都用不了，所以这里先拦下
                var fest = Knowledge.TodaysFestival();
                if (fest != null && fest.EnteringStartsFestival
                    && route.Any(e => e.Target.Equals(fest.Location, StringComparison.OrdinalIgnoreCase)))
                {
                    o.Fail($"今天是{fest}，现在进 {fest.Location} 会直接进入节日（节日里时间停止，移动/干活的工具都不能用）。"
                        + $"这条路线要经过 {fest.Location}，请等到 {Util.TimeString(fest.End)} 以后再去，或者先做不经过 {fest.Location} 的事");
                    yield break;
                }
                foreach (var s in TakeExit(route[0], o))
                    yield return s;
                if (!o.Ok)
                    yield break;
            }
            o.Fail("路线太长，没有走到");
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
                    if (Game1.currentLocation == from && !Game1.isWarping && Game1.locationRequest == null)
                        Game1.warpFarmer(e.Target, e.TargetTile.X, e.TargetTile.Y, Player.FacingDirection);
                    break;
                }
                case ExitKind.Door:
                {
                    foreach (var s in GoAdjacent(e.Tile, o, new Point(e.Tile.X, e.Tile.Y + 1)))
                        yield return s;
                    if (!o.Ok)
                        yield break;
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
            }

            // 等待场景切换完成
            for (int i = 0; i < 600; i++)
            {
                yield return null;
                if (Game1.currentLocation != from && !Game1.isWarping && !Game1.fadeToBlack && Game1.locationRequest == null)
                {
                    foreach (var s in WaitTicks(10))
                        yield return s;
                    o.Succeed();
                    yield break;
                }
                if (Game1.activeClickableMenu is DialogueBox db && Game1.currentLocation == from)
                {
                    string text = db.getCurrentString();
                    db.closeDialogue();
                    o.Fail($"进不去 {e.Target}：{text}");
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
                if (!ok)
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
                yield return ActionResult.Fail($"({target.X},{target.Y}) 没有可以交互的东西");
        }

        public IEnumerable<object> PlaceItem(string itemSpec, int x, int y)
        {
            int idx = Util.FindItemIndex(itemSpec, i => i is SObject);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有可放置的“{itemSpec}”");
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
            bool placed = Utility.tryToPlaceItem(Loc, item, x * 64 + 32, y * 64 + 32);
            foreach (var s in WaitTicks(10))
                yield return s;
            yield return placed
                ? ActionResult.Success($"已在 ({x},{y}) 放置/种下 {name}")
                : ActionResult.Fail($"不能在 ({x},{y}) 放置 {name}（种子需要空耕地且当季可种；物品需要空地）");
        }

        // ───────────────────────── 批量农活 ─────────────────────────

        public IEnumerable<object> TillArea(int x1, int y1, int x2, int y2)
        {
            int hoe = Util.FindTool<Hoe>();
            if (hoe < 0)
            {
                yield return ActionResult.Fail("没有锄头");
                yield break;
            }
            var loc = Loc;
            var tiles = RectTiles(x1, y1, x2, y2, 80).Where(p =>
                loc.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back") != null
                && !loc.Objects.ContainsKey(new Vector2(p.X, p.Y))
                && !loc.terrainFeatures.ContainsKey(new Vector2(p.X, p.Y))
                && Pathfinder.IsPassable(loc, p.X, p.Y)).ToList();
            if (tiles.Count == 0)
            {
                yield return ActionResult.Fail("区域内没有可以开垦的空地（可能已经耕过，或者有杂物，先用 clear_area 清理）");
                yield break;
            }
            int done = 0;
            string err = null;
            var o = new Outcome();
            foreach (var p in Util.NearestOrder(Player.TilePoint, tiles))
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
                foreach (var s in UseToolOnce(hoe, p, o))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    break;
                }
                if (loc.terrainFeatures.TryGetValue(new Vector2(p.X, p.Y), out var tf) && tf is HoeDirt)
                    done++;
            }
            string msg = $"开垦了 {done}/{tiles.Count} 格，剩余体力 {(int)Player.Stamina}";
            yield return done > 0 ? ActionResult.Success(msg + (err != null ? "；部分失败：" + err : "")) : ActionResult.Fail(msg + "；" + err);
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
            int done = 0;
            string err = null;
            var o = new Outcome();
            foreach (var p in Util.NearestOrder(Player.TilePoint, tiles).Take(Math.Clamp(max, 1, 200)))
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
            int done = 0;
            string err = null;
            var o = new Outcome();
            foreach (var p in Util.NearestOrder(Player.TilePoint, tiles).Take(Math.Clamp(max, 1, 300)))
            {
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
                foreach (var s in UseToolOnce(canIdx, p, o))
                    yield return s;
                if (!o.Ok)
                {
                    err = o.Message;
                    break;
                }
                if (loc.terrainFeatures.TryGetValue(new Vector2(p.X, p.Y), out var tf) && tf is HoeDirt hd && hd.isWatered())
                    done++;
            }
            int left = loc.terrainFeatures.Pairs.Count(p => p.Value is HoeDirt hd && Observer.NeedsWater(hd));
            string msg = $"浇了 {done} 株，还剩 {left} 株没浇，剩余体力 {(int)Player.Stamina}";
            yield return done > 0 ? ActionResult.Success(msg + (err != null ? "；" + err : "")) : ActionResult.Fail(msg + "；" + err);
        }

        public IEnumerable<object> HarvestCrops(int max)
        {
            var loc = Loc;
            var tiles = loc.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt hd && hd.crop != null && hd.readyForHarvest())
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y)).ToList();
            if (tiles.Count == 0)
            {
                yield return ActionResult.Success("这里没有可以收获的作物");
                yield break;
            }
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
            string msg = $"收获了 {done} 株";
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

        private static bool CanAccept(Debris d) =>
            d.item != null ? Player.couldInventoryAcceptThisItem(d.item) : Player.couldInventoryAcceptThisItem(d.itemId.Value, 1, d.itemQuality);

        private static string DebrisName(Debris d) =>
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
                        : Pathfinder.NearestStandTiles(loc, tile, 1);
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
                    case 't': jobs.Add((p, axe, 4)); break;
                    case 'S': jobs.Add((p, axe, 10)); break;
                    case 'T': if (includeTrees) jobs.Add((p, axe, 25)); break;
                }
            }
            jobs.RemoveAll(j => j.tool < 0);
            if (jobs.Count == 0)
            {
                yield return ActionResult.Success("区域内没有可以清理的东西（大型障碍物 R 需要升级工具）");
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
            yield return cleared > 0 ? ActionResult.Success(msg + (err != null ? "；" + err : "")) : ActionResult.Fail(msg + "；" + err);
        }

        // ───────────────────────── 物品 ─────────────────────────

        public IEnumerable<object> ShipItems(string itemSpec, int? count)
        {
            int idx = Util.FindItemIndex(itemSpec);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有“{itemSpec}”");
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

        public IEnumerable<object> Eat(string itemSpec)
        {
            int idx = Util.FindItemIndex(itemSpec, i => i is SObject o && o.Edibility != -300);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有可以吃的“{itemSpec}”");
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
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

        public IEnumerable<object> Dialogue(int? choice)
        {
            if (Game1.activeClickableMenu is not DialogueBox db)
            {
                yield return ActionResult.Fail("现在没有对话框");
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

        public IEnumerable<object> MenuClick(int index)
        {
            var menu = Game1.activeClickableMenu;
            if (menu == null)
            {
                yield return ActionResult.Fail("没有打开的界面");
                yield break;
            }
            var comps = Observer.MenuComponents(menu);
            if (index < 0 || index >= comps.Count)
            {
                yield return ActionResult.Fail("没有这个元素");
                yield break;
            }
            var c = comps[index];
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
                default:
                    if (menu.readyToClose())
                        menu.exitThisMenu();
                    else
                        Game1.exitActiveMenu();
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
            if (!Game1.eventUp || ev == null)
            {
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
            yield return ActionResult.Success("已跳过事件");
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
            if (Game1.activeClickableMenu is ConfirmationDialog confirm)
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
            if (Loc is not FarmHouse)
            {
                foreach (var s in TravelInner("FarmHouse", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("回不了家：" + o.Message);
                    yield break;
                }
            }
            var house = (FarmHouse)Loc;
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

            for (int i = 0; i < 300 && !Game1.newDay && !Player.isInBed.Value && !Game1.fadeToBlack; i++)
                yield return null;
            yield return Game1.newDay || Player.isInBed.Value || Game1.fadeToBlack
                ? ActionResult.Success("上床睡觉了，正在进入新的一天（结算界面会自动确认）")
                : ActionResult.Fail("没能睡下");
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
            minutes = Math.Clamp(minutes, 10, 600);
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
                if (Game1.eventUp || Game1.activeClickableMenu != null)
                {
                    yield return ActionResult.Success($"等待被打断（{Util.TimeString(Game1.timeOfDay)}）：出现了剧情或界面，先处理它。\n{observer.DescribeMenu()}");
                    yield break;
                }
                yield return null;
            }
            yield return ActionResult.Success($"等待结束，现在是 {Util.TimeString(Game1.timeOfDay)}");
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
