using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // 计划只保存值和坐标，不保存游戏对象。施工与预览共用这份图。
        internal sealed class FieldPlan
        {
            public int Id { get; set; }
            public string Location { get; set; }
            public string RequestKey { get; set; }
            public Rectangle Area { get; set; }
            public Point Entry { get; set; }
            public int Target { get; set; }
            public int? RequestedCount { get; set; }
            public string Seed { get; set; }
            public string Sprinkler { get; set; }
            public int CrowRadius { get; set; }
            public bool Raised { get; set; }
            public List<Point> Template { get; set; } = new();
            public List<Point> BoostedTemplate { get; set; } = new();
            public List<Point> BoostedSites { get; set; } = new();
            public List<Point> Plants { get; set; } = new();
            internal List<Point> TemplateAt(Point p) => BoostedSites.Contains(p) ? BoostedTemplate : Template;
            public List<Point> Sprinklers { get; set; } = new();
            public List<Point> Crows { get; set; } = new();
            public List<Point> Paths { get; set; } = new();
            public List<FieldJob> Jobs { get; set; } = new();
            public int WorkDay { get; set; } = -1;
            public List<FieldJob> BeforeWork { get; set; } = new();

            internal FieldPlan WithJobs(List<FieldJob> jobs)
            {
                var copy = (FieldPlan)MemberwiseClone();
                copy.Jobs = jobs.ToList();
                return copy;
            }
        }

        internal sealed record FieldJob(Point Tile, string Task, string Expected = null);
        internal sealed class FieldBook
        {
            public int NextId { get; set; } = 1;
            public List<FieldPlan> Plans { get; set; } = new();
            public List<FieldPlan> FinishedToday { get; set; } = new();
        }

        internal static IEnumerable<Point> Neighbors(Point p) =>
            Pathfinder.Dirs.Select(d => new Point(p.X + d.X, p.Y + d.Y));

        // 四邻接搜索，同时保留从入口到每个站位的路径，施工后还要能回来。
        internal static Dictionary<Point, Point> FieldRoutes(Point entry, HashSet<Point> walk, HashSet<Point> blocked)
        {
            var parent = new Dictionary<Point, Point>();
            if (!walk.Contains(entry) || blocked.Contains(entry))
                return parent;
            var queue = new Queue<Point>();
            parent.Add(entry, entry);
            queue.Enqueue(entry);
            while (queue.TryDequeue(out var p))
                foreach (var n in Neighbors(p))
                    if (walk.Contains(n) && !blocked.Contains(n) && parent.TryAdd(n, p))
                        queue.Enqueue(n);
            return parent;
        }

        private static int Distance2(Point a, Point b) =>
            (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

        internal static bool CrowProtects(Point crow, int radius, Point plant) =>
            Distance2(crow, plant) < (long)radius * radius;

        private static IEnumerable<Point> Footprint(Point center, IEnumerable<Point> offsets) =>
            offsets.Select(d => new Point(center.X + d.X, center.Y + d.Y));

        // 可用格与可走格分开传：入口、田边的路可以站，不能占。已有喷头的完整覆盖也参与去重。
        internal static FieldPlan LayoutField(Rectangle area, HashSet<Point> usable, HashSet<Point> walk,
            List<Point> offsets, HashSet<Point> existingWater, Dictionary<Point, int> existingCrows,
            int radius, bool lanes, bool raised, Point entry, int target, HashSet<Point> deviceSites,
            List<Point> boosted, int boostedCount)
        {
            // 同一区域试中心起排和边角起排，避免紧凑矩形被第一只喷头切成零碎边角。
            var origins = new[] { area.Center, new Point(area.Left, area.Top) };
            return origins.Take(offsets.Count == 0 ? 1 : 2)
                .Select(origin => LayoutFrom(area, usable, walk, offsets, existingWater, existingCrows,
                    radius, lanes, raised, entry, target, origin, deviceSites, boosted, boostedCount))
                .OrderByDescending(f => f.Plants.Count).ThenBy(f => f.Sprinklers.Count).ThenBy(f => f.Crows.Count)
                .ThenBy(f => f.Plants.Count == 0 ? 0 : Math.Max(f.Plants.Max(p => p.X) - f.Plants.Min(p => p.X),
                    f.Plants.Max(p => p.Y) - f.Plants.Min(p => p.Y))).First();
        }

        private static FieldPlan LayoutFrom(Rectangle area, HashSet<Point> usable, HashSet<Point> walk,
            List<Point> offsets, HashSet<Point> existingWater, Dictionary<Point, int> existingCrows,
            int radius, bool lanes, bool raised, Point entry, int target, Point origin, HashSet<Point> deviceSites,
            List<Point> boosted, int boostedCount)
        {
            var plan = new FieldPlan
            {
                Area = area,
                Entry = entry,
                Template = offsets,
                BoostedTemplate = boosted,
                Target = target,
                CrowRadius = radius,
                Raised = raised
            };
            var blocked = new HashSet<Point>();
            var routes = FieldRoutes(entry, walk, blocked);
            var cells = usable.Where(routes.ContainsKey).OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
            var devices = new HashSet<Point>();
            bool Lane(Point p) => p == entry || (lanes && ((p.Y - area.Top) % 2 == 1 || p.X == entry.X));
            var beds = cells.Where(p => !Lane(p)).ToHashSet();
            var water = new HashSet<Point>(existingWater);
            bool Accessible(IEnumerable<Point> targets, Dictionary<Point, Point> paths) =>
                targets.All(t => Neighbors(t).Any(paths.ContainsKey));
            bool CanBlock(Point p, IEnumerable<Point> targets)
            {
                blocked.Add(p);
                var paths = FieldRoutes(entry, walk, blocked);
                if (Accessible(targets, paths) && Neighbors(p).Any(paths.ContainsKey))
                    return true;
                blocked.Remove(p);
                return false;
            }

            if (offsets.Count > 0)
            {
                var sites = cells.Where(p => !Lane(p) && deviceSites.Contains(p)).ToList();
                var normalHeads = sites.Select(p => (p, cover: Footprint(p, offsets).ToHashSet())).ToList();
                var boostedHeads = boostedCount > 0
                    ? sites.Select(p => (p, cover: Footprint(p, boosted).ToHashSet())).ToList() : normalHeads;
                int left = origin.X, right = left, top = origin.Y, bottom = top;
                while (beds.Count(water.Contains) < target)
                {
                    bool useBoost = plan.BoostedSites.Count < boostedCount;
                    var candidates = useBoost ? boostedHeads : normalHeads;
                    var ranked = candidates.Where(c => !devices.Contains(c.p) && !water.Contains(c.p)
                        && !c.cover.Overlaps(water) && !c.cover.Overlaps(devices))
                        .Select(c => (c.p, c.cover, gain: c.cover.Count(t => t != c.p && beds.Contains(t)),
                            width: Math.Max(right, c.p.X) - Math.Min(left, c.p.X),
                            height: Math.Max(bottom, c.p.Y) - Math.Min(top, c.p.Y)))
                        .Where(c => c.gain > 0).OrderByDescending(c => c.gain)
                        .ThenBy(c => Math.Max(c.width, c.height)).ThenBy(c => Math.Abs(c.width - c.height))
                        .ThenBy(c => Distance2(c.p, area.Center));
                    var best = ranked.FirstOrDefault(c => CanBlock(c.p, devices));
                    if (best.cover == null)
                    {
                        if (!useBoost)
                            break;
                        boostedCount = 0;
                        continue;
                    }
                    if (useBoost)
                        plan.BoostedSites.Add(best.p);
                    if (plan.Sprinklers.Count == 0)
                    {
                        left = right = best.p.X;
                        top = bottom = best.p.Y;
                    }
                    left = Math.Min(left, best.p.X);
                    right = Math.Max(right, best.p.X);
                    top = Math.Min(top, best.p.Y);
                    bottom = Math.Max(bottom, best.p.Y);
                    plan.Sprinklers.Add(best.p);
                    devices.Add(best.p);
                    beds.Remove(best.p);
                    water.UnionWith(best.cover);
                }
                beds.IntersectWith(water);
            }

            var plants = new List<Point>();
            routes = FieldRoutes(entry, walk, blocked);
            foreach (var p in beds.OrderBy(p => Distance2(p, area.Center)).ThenBy(p => p.Y).ThenBy(p => p.X))
            {
                if (!Neighbors(p).Any(routes.ContainsKey))
                    continue;
                if (raised && !CanBlock(p, devices.Concat(plants)))
                    continue;
                plants.Add(p);
                if (plants.Count >= target)
                    break;
            }

            if (radius > 0)
            {
                var unprotected = plants.Where(p => !existingCrows.Any(c => CrowProtects(c.Key, c.Value, p))).ToHashSet();
                while (unprotected.Count > 0)
                {
                    var ranked = cells.Where(p => !Lane(p) && deviceSites.Contains(p) && !devices.Contains(p))
                        .Select(p => (p, gain: unprotected.Count(t => CrowProtects(p, radius, t))))
                        .Where(c => c.gain > 0).OrderByDescending(c => c.gain)
                        .ThenBy(c => plants.Contains(c.p)).ThenBy(c => Distance2(c.p, area.Center));
                    Point? best = null;
                    foreach (var c in ranked)
                    {
                        // 棚架格已当墙；换成稻草人后仍是墙。
                        if (blocked.Contains(c.p) || CanBlock(c.p, devices.Concat(plants)))
                        {
                            best = c.p;
                            break;
                        }
                    }
                    if (best is not Point crow)
                    {
                        plants.RemoveAll(unprotected.Contains);
                        break;
                    }
                    devices.Add(crow);
                    plan.Crows.Add(crow);
                    plants.Remove(crow);
                    unprotected.RemoveWhere(p => CrowProtects(crow, radius, p));
                }
            }
            plan.Plants = plants;
            // 仅保留确实服务于作物或设备的路径；普通作物本身能走，不额外把它画成路。
            routes = FieldRoutes(entry, walk, blocked);
            var pathsUsed = new HashSet<Point> { entry };
            foreach (var p in plants.Concat(devices))
            {
                Point stand = Neighbors(p).First(routes.ContainsKey);
                while (pathsUsed.Add(stand))
                    stand = routes[stand];
            }
            pathsUsed.ExceptWith(plants);
            plan.Paths = pathsUsed.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
            return plan;
        }

        // 先写整份待办，再动地面。只划掉实地已完成的任务，已收获的格不会为补设备而重播。
        internal static FieldBook RegisterField(FieldBook book, FieldPlan plan)
        {
            if (plan.Id != book.NextId)
                throw new InvalidOperationException("计划编号已变化，请重新预览");
            return new FieldBook { NextId = book.NextId + 1, Plans = book.Plans.Append(plan).ToList(), FinishedToday = book.FinishedToday.ToList() };
        }

        internal static FieldBook FinishField(FieldBook book, int id) =>
            new()
            {
                NextId = book.NextId,
                Plans = book.Plans.Where(p => p.Id != id).ToList(),
                FinishedToday = book.FinishedToday.Concat(book.Plans.Where(p => p.Id == id)).ToList()
            };

        internal FieldBook FieldTodos { get; set; }
        internal Action<FieldBook> WriteFieldBook { get; set; }

        private static readonly Dictionary<string, string> FieldSprinklerIds = new()
        {
            ["none"] = null,
            ["basic"] = "(O)599",
            ["quality"] = "(O)621",
            ["iridium"] = "(O)645"
        };

        private static string SprinklerName(string kind) => kind switch
        {
            "basic" => "普通洒水器",
            "quality" => "优质洒水器",
            "iridium" => "铱制洒水器",
            _ => "手浇"
        };

        private static string JobName(FieldPlan plan, string task) => task switch
        {
            "clear" => "清障",
            "hoe" => "锄地",
            "seed" => "播种",
            "sprinkler" => SprinklerName(plan.Sprinkler),
            "crow" => "稻草人",
            _ => "核对覆盖和走道"
        };

        private static string Summary(FieldPlan plan) =>
            $"plan {plan.Id}（{plan.Location}）还差" + string.Join("、", plan.Jobs.GroupBy(j => JobName(plan, j.Task) + (j.Task == "sprinkler" && plan.BoostedSites.Contains(j.Tile) ? "（带增压喷头）" : ""))
                .Select(g => $"{g.Key}×{g.Count()}"))
            + $"（prepare_field mode=resume plan_id={plan.Id}）";

        internal string FieldSummary() => FieldTodos?.Plans.Count > 0
            ? "开田待办：" + string.Join("；", FieldTodos.Plans.Take(3).Select(Summary))
                + (FieldTodos.Plans.Count > 3 ? "；其他待办编号 " + string.Join("、", FieldTodos.Plans.Skip(3).Select(p => p.Id)) : "") : null;

        // 预览只看保留清单的快照，不触发 reserve_items 的到期清理和笔记写盘。
        private static Func<Item, bool> FieldReserved()
        {
            int today = Game1.Date.TotalDays;
            var names = SplitReservedNames(ReservedItems).Concat(SaveReservedSnapshot()
                .Where(r => r.UntilDay <= 0 || r.UntilDay > today).Select(r => r.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return i => i == null || names.Contains(i.DisplayName) || names.Contains(i.Name);
        }

        private static List<Point> FieldTemplate(SObject item)
        {
            var origin = item.TileLocation.ToPoint();
            return item.GetSprinklerTiles().Select(v => v.ToPoint() - origin).Distinct().ToList();
        }

        private static string ResolveFieldSeed(string spec)
        {
            int slot = Util.FindItemIndex(spec, i => i.Category == SObject.SeedsCategory);
            if (slot >= 0)
                return Player.Items[slot].ItemId;
            // 背包暂缺也能按游戏数据预留。只收能确定是哪一种作物的种子。
            foreach (string id in Game1.cropData.Keys)
            {
                var data = ItemRegistry.GetData("(O)" + id);
                if (data != null && new[] { id, data.QualifiedItemId, data.DisplayName, data.InternalName }
                    .Contains(spec, StringComparer.OrdinalIgnoreCase))
                    return id;
            }
            return null;
        }

        private static Rectangle? FieldRegion(GameLocation loc, int? x1, int? y1, int? x2, int? y2,
            Point? near, int count, out string error)
        {
            error = null;
            if (x1.HasValue || y1.HasValue || x2.HasValue || y2.HasValue)
            {
                if (near.HasValue || x1 == null || x2 == null || y1 == null || y2 == null
                    || !loc.isTileOnMap(x1.Value, y1.Value) || !loc.isTileOnMap(x2.Value, y2.Value)
                    || Math.Abs((long)x2 - x1.Value) >= 24 || Math.Abs((long)y2 - y1.Value) >= 24)
                {
                    error = "请给地图内的完整矩形，每边最多 24 格，和 near 二选一";
                    return null;
                }
                return new Rectangle(Math.Min(x1.Value, x2.Value), Math.Min(y1.Value, y2.Value),
                    Math.Abs(x2.Value - x1.Value) + 1, Math.Abs(y2.Value - y1.Value) + 1);
            }

            int side = Math.Min(22, (int)Math.Ceiling(Math.Sqrt(count * 2)) + 3);
            Point center = near ?? Player.TilePoint;
            if (!near.HasValue)
            {
                Point? door = loc is Farm ? MyCabinDoor() : null;
                if (loc is Farm && Context.IsMultiplayer && !Game1.IsMasterGame && door == null)
                {
                    error = "找不到自己小屋的门口，请明确指定区域";
                    return null;
                }
                var fields = Fields(loc).Where(f => f.Tiles.Count >= 4);
                var main = door is Point d ? fields.Where(f => FieldDistance(f, d) <= OwnFieldRadius)
                    .OrderBy(f => FieldDistance(f, d)).FirstOrDefault() : fields.FirstOrDefault();
                if (main != null)
                {
                    var next = BestExpansion(loc, main, count);
                    var box = main.Box;
                    center = next.side switch
                    {
                        "下" => new Point(box.Center.X, box.Bottom + side / 2),
                        "上" => new Point(box.Center.X, box.Top - (side + 1) / 2),
                        "右" => new Point(box.Right + side / 2, box.Center.Y),
                        _ => new Point(box.Left - (side + 1) / 2, box.Center.Y)
                    };
                }
                else if (door is Point cabin)
                    return FirstFieldNearDoor(loc, cabin, count);
            }
            if (!loc.isTileOnMap(center.X, center.Y))
                return null;
            return Rectangle.Intersect(new Rectangle(center.X - side / 2, center.Y - side / 2, side, side),
                new Rectangle(0, 0, Pathfinder.MapWidth(loc), Pathfinder.MapHeight(loc)));
        }

        private static bool FieldGround(GameLocation loc, Point p)
        {
            var v = p.ToVector2();
            return loc.isTileOnMap(p.X, p.Y) && loc.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back") != null
                && !loc.isWaterTile(p.X, p.Y) && loc.isTilePassable(v) && !Pathfinder.IsTouchWarp(loc, p.X, p.Y)
                && !loc.warps.Any(w => w.X == p.X && w.Y == p.Y)
                && !loc.IsTileOccupiedBy(v, CollisionMask.Buildings | CollisionMask.Furniture | CollisionMask.LocationSpecific)
                && !loc.resourceClumps.Any(c => c.occupiesTile(p.X, p.Y))
                && !(loc.largeTerrainFeatures?.Any(f => f.getBoundingBox().Intersects(new Rectangle(p.X * 64, p.Y * 64, 64, 64))) ?? false)
                && !loc.buildings.Any(b => b.humanDoor.Value.X >= 0 && b.tileX.Value + b.humanDoor.Value.X == p.X
                    && p.Y == b.tileY.Value + b.humanDoor.Value.Y + 1);
        }

        // 利用 clear_area 的分类，只接受单层、能定点处理的杂物。地上叠着机器/地板的格不动。
        private static string FieldSurface(GameLocation loc, Point p)
        {
            if (!FieldGround(loc, p))
                return "blocked";
            var v = p.ToVector2();
            loc.terrainFeatures.TryGetValue(v, out var tf);
            if (loc.objects.TryGetValue(v, out var obj))
            {
                if (tf != null)
                    return "blocked";
                var ch = Observer.ClassifyFeature(loc, v).ch;
                return ch is 'o' or '/' or 'w' or '?' ? ch + obj.QualifiedItemId : "blocked";
            }
            return tf switch
            {
                HoeDirt { crop: null } => "soil",
                HoeDirt h when h.crop.dead.Value => "dead",
                HoeDirt => "crop",
                Grass => "grass",
                Tree t when !t.tapped.Value && (t.stump.Value || t.growthStage.Value < 5) => "tree" + t.treeType.Value,
                null when Pathfinder.IsPassable(loc, p.X, p.Y) => "bare",
                _ => "blocked"
            };
        }

        private static bool NeedsClear(string surface) => surface is not ("bare" or "soil" or "blocked" or "crop");

        private static List<Point> AreaTiles(Rectangle box) =>
            RectTiles(box.Left, box.Top, box.Right - 1, box.Bottom - 1, box.Width * box.Height);

        private FieldPlan SurveyField(GameLocation loc, Rectangle area, int target, string seed, string sprinkler,
            bool scarecrow, bool lanes, bool raised, bool automatic, out string error)
        {
            error = null;
            var usable = new HashSet<Point>();
            var walk = new HashSet<Point>();
            var obstacles = new Dictionary<Point, string>();
            var halo = area;
            halo.Inflate(1, 1);
            var otherFields = automatic && loc is Farm && MyCabinDoor() is Point door
                ? Fields(loc).Where(f => FieldDistance(f, door) > OwnFieldRadius).Select(f =>
                {
                    var box = f.Box;
                    box.Inflate(2, 2); // Fields 会把中间只隔一格的耕地归入同一块。
                    return box;
                }).ToList() : new List<Rectangle>();
            var occupied = FieldTodos.Plans.Where(f => f.Location == loc.NameOrUniqueName).ToList();
            var reserved = occupied.SelectMany(f => f.Plants.Concat(f.Paths).Concat(f.Sprinklers).Concat(f.Crows)).ToHashSet();
            foreach (var p in AreaTiles(halo))
            {
                if (!loc.isTileOnMap(p.X, p.Y) || Pathfinder.IsTouchWarp(loc, p.X, p.Y)
                    || loc.warps.Any(w => w.X == p.X && w.Y == p.Y))
                    continue;
                bool open = Pathfinder.IsPassable(loc, p.X, p.Y);
                if (open && !occupied.Any(f => f.Sprinklers.Contains(p) || f.Crows.Contains(p) || f.Raised && f.Plants.Contains(p)))
                    walk.Add(p);
                if (!area.Contains(p) || reserved.Contains(p) || otherFields.Any(b => b.Contains(p))
                    || sprinkler != "none" && NoSprinklers(loc, p.ToVector2()))
                    continue;
                string surface = FieldSurface(loc, p);
                if (surface is "crop" or "blocked" || !loc.CanPlantSeedsHere(seed ?? "472", p.X, p.Y, false, out _))
                    continue;
                usable.Add(p);
                walk.Add(p);
                if (NeedsClear(surface))
                    obstacles.Add(p, surface);
            }
            var entries = walk.Where(p => !area.Contains(p) && Pathfinder.IsPassable(loc, p.X, p.Y)).ToList();
            var approach = Pathfinder.FindPath(loc, Player.TilePoint, entries);
            if (approach == null || approach.Count == 0)
            {
                error = "走不到田边入口，换个区域或先打通进田的路";
                return null;
            }
            Point entry = approach.Last();
            // 已装配件的实物按自身模板，缺货时按无配件的同等级物品预留。
            var offsets = new List<Point>();
            var boosted = new List<Point>();
            int boostedCount = 0;
            var reservedItems = FieldReserved();
            if (FieldSprinklerIds[sprinkler] is string id)
            {
                offsets = FieldTemplate(ItemRegistry.Create<SObject>(id));
                var nozzles = Player.Items.OfType<SObject>().Where(i => i.QualifiedItemId == id && !reservedItems(i)
                    && i.heldObject.Value?.QualifiedItemId == "(O)915").ToList();
                boostedCount = nozzles.Sum(i => i.Stack);
                if (boostedCount > 0)
                    boosted = FieldTemplate(nozzles[0]);
            }
            var existing = loc.objects.Pairs.Where(k => k.Value.IsSprinkler())
                .SelectMany(k => k.Value.GetSprinklerTiles().Append(k.Key)).Select(v => v.ToPoint()).ToHashSet();
            // 其他未完计划的预留覆盖不能当作现有水源，也不能再放重叠的喷头。
            var futureWater = occupied.SelectMany(f => f.Sprinklers.SelectMany(p => Footprint(p, f.TemplateAt(p)).Append(p))).ToHashSet();
            var crows = loc.objects.Pairs.Where(k => k.Value.IsScarecrow()).ToDictionary(k => k.Key.ToPoint(), k => k.Value.GetRadiusForScarecrow());
            var crow = scarecrow ? Player.Items.OfType<SObject>().Where(i => i.IsScarecrow() && !reservedItems(i))
                .OrderBy(i => i.GetRadiusForScarecrow()).FirstOrDefault() ?? ItemRegistry.Create<SObject>("(BC)8") : null;
            int radius = crow?.GetRadiusForScarecrow() ?? 0;
            if (offsets.Count > 0)
            {
                usable.ExceptWith(futureWater);
                existing.UnionWith(futureWater);
            }
            var deviceSites = usable.Where(p => loc.isTilePlaceable(p.ToVector2())).ToHashSet();
            var plan = LayoutField(area, usable, walk, offsets, existing, crows, radius, lanes, raised, entry, target,
                deviceSites, boosted, boostedCount);
            if (plan.Plants.Count == 0)
            {
                error = "这块地没有满足覆盖、入口和站位要求的种植格；请换区域";
                return null;
            }
            plan.Id = FieldTodos.NextId;
            plan.Location = loc.NameOrUniqueName;
            plan.Sprinkler = sprinkler;
            plan.Seed = seed;
            var used = plan.Plants.Concat(plan.Sprinklers).Concat(plan.Crows).Concat(plan.Paths).ToHashSet();
            var approachRoutes = FieldRoutes(entry, used, new HashSet<Point>());
            int StepsFromEntry(Point p)
            {
                int steps = 0;
                while (p != entry)
                {
                    p = approachRoutes[p];
                    steps++;
                }
                return steps;
            }
            // 清障按沿通路的距离向里推进，不能按直线距离先选中围在石堆里的目标。
            plan.Jobs.AddRange(used.Where(obstacles.ContainsKey).OrderBy(StepsFromEntry)
                .Select(p => new FieldJob(p, "clear", obstacles[p])));
            plan.Jobs.AddRange(plan.Plants.OrderBy(p => Distance2(p, area.Center)).Select(p => new FieldJob(p, "hoe")));
            plan.Jobs.AddRange(plan.Sprinklers.Select(p => new FieldJob(p, "sprinkler")));
            plan.Jobs.AddRange(plan.Crows.Select(p => new FieldJob(p, "crow")));
            if (seed != null)
                plan.Jobs.AddRange(plan.Plants.OrderBy(p => p.Y).ThenBy(p => p.X).Select(p => new FieldJob(p, "seed")));
            return plan;
        }

        private static string DrawField(FieldPlan plan)
        {
            var map = new StringBuilder();
            var box = plan.Area;
            box.Inflate(1, 1);
            for (int y = box.Top; y < box.Bottom; y++)
            {
                for (int x = box.Left; x < box.Right; x++)
                {
                    var p = new Point(x, y);
                    map.Append(p == plan.Entry ? 'E' : plan.Sprinklers.Contains(p) ? 'S' : plan.Crows.Contains(p) ? 'C'
                        : plan.Plants.Contains(p) ? '+' : plan.Paths.Contains(p) ? '=' : '.');
                }
                map.AppendLine();
            }
            return $"左上角 ({box.Left},{box.Top})，向右为东、向下为南。E 入口，= 走道，+ 种植，S 喷头，C 稻草人，. 保持原状\n{map}"
                + $"种植 {plan.Plants.Count}/{plan.Target} 格；{SprinklerName(plan.Sprinkler)}"
                + (plan.Sprinkler == "none" ? "，需逐日浇水" : $"预留 {plan.Sprinklers.Count} 个，其中带增压喷头 {plan.BoostedSites.Count} 个，含已有设备预计浇到全部种植格")
                + $"；稻草人预留 {plan.Crows.Count} 个" + (plan.CrowRadius > 0 ? $"（半径 {plan.CrowRadius}），含已有稻草人预计全部受保护" : "")
                + $"；走道 {plan.Paths.Count} 格，清障 {plan.Jobs.Count(j => j.Task == "clear")} 格";
        }

        private static bool FieldDone(GameLocation loc, FieldPlan plan, FieldJob job)
        {
            var v = job.Tile.ToVector2();
            loc.objects.TryGetValue(v, out var obj);
            loc.terrainFeatures.TryGetValue(v, out var tf);
            var soil = tf as HoeDirt;
            return job.Task switch
            {
                "verify" => true,
                "hoe" => obj == null && soil != null && (soil.crop == null || MatchingCrop(soil.crop, plan.Seed)),
                "seed" => obj == null && MatchingCrop(soil?.crop, plan.Seed),
                "sprinkler" => obj?.QualifiedItemId == FieldSprinklerIds[plan.Sprinkler]
                    && FieldTemplate(obj).ToHashSet().IsSupersetOf(plan.TemplateAt(job.Tile)),
                "crow" => obj?.IsScarecrow() == true && obj.GetRadiusForScarecrow() >= plan.CrowRadius,
                _ => FieldSurface(loc, job.Tile) is "bare" or "soil" || MatchingCrop(soil?.crop, plan.Seed)
                    || plan.Sprinklers.Contains(job.Tile) && FieldDone(loc, plan, new FieldJob(job.Tile, "sprinkler"))
                    || plan.Crows.Contains(job.Tile) && FieldDone(loc, plan, new FieldJob(job.Tile, "crow"))
            };
        }

        private static bool MatchingCrop(Crop crop, string seed) => crop != null && !crop.dead.Value && seed != null
            && (crop.netSeedIndex.Value == seed || crop.whichForageCrop.Value == seed);

        private static string WorkLimit(GameLocation loc)
        {
            if (!Context.IsWorldReady || Loc != loc)
                return "地点发生变化";
            if (Blocked(out string why))
                return why;
            if (Game1.isDarkOut(loc))
                return "天黑了";
            return Player.Stamina < 10 ? "体力不足 10" : null;
        }

        private static SObject FieldMaterial(FieldPlan plan, FieldJob job)
        {
            var reserved = FieldReserved();
            return Player.Items.OfType<SObject>().Where(i => !reserved(i)).FirstOrDefault(i => job.Task switch
            {
                "seed" => i.ItemId == plan.Seed && i.Category == SObject.SeedsCategory,
                "crow" => i.IsScarecrow() && i.GetRadiusForScarecrow() >= plan.CrowRadius,
                "sprinkler" => i.QualifiedItemId == FieldSprinklerIds[plan.Sprinkler]
                    && FieldTemplate(i).ToHashSet().SetEquals(plan.TemplateAt(job.Tile)),
                _ => false
            });
        }

        private static string FieldConflict(GameLocation loc, FieldPlan plan)
        {
            // 每批和续跑都重算原地设备；曾借用的喷头/稻草人被搬走，或有人新放了重叠的设备，都暂停。
            var water = new HashSet<Point>();
            foreach (var pair in loc.objects.Pairs.Where(k => k.Value.IsSprinkler() && !plan.Sprinklers.Contains(k.Key.ToPoint())))
                water.UnionWith(pair.Value.GetSprinklerTiles().Append(pair.Key).Select(v => v.ToPoint()));
            foreach (var p in plan.Sprinklers)
            {
                var cover = loc.objects.TryGetValue(p.ToVector2(), out var installed) && installed.IsSprinkler()
                    ? installed.GetSprinklerTiles().Select(v => v.ToPoint()).ToList() : Footprint(p, plan.TemplateAt(p)).ToList();
                if (water.Contains(p) || cover.Any(water.Contains))
                    return $"({p.X},{p.Y}) 的洒水范围与现场喷头重叠";
                water.Add(p);
                water.UnionWith(cover);
            }
            if (plan.Sprinkler != "none" && plan.Plants.Any(p => !water.Contains(p) || NoSprinklers(loc, p.ToVector2())))
                return "现场洒水范围变化，有种植格失去覆盖";
            if (plan.CrowRadius > 0)
            {
                var crows = loc.objects.Pairs.Where(k => k.Value.IsScarecrow())
                    .Select(k => (tile: k.Key.ToPoint(), radius: k.Value.GetRadiusForScarecrow()))
                    .Concat(plan.Crows.Select(p => (tile: p, radius: plan.CrowRadius))).ToList();
                if (plan.Plants.Any(p => !crows.Any(c => CrowProtects(c.tile, c.radius, p))))
                    return "现场稻草人已移动，有种植格失去保护";
            }
            var clearing = plan.Jobs.Where(j => j.Task == "clear").ToDictionary(j => j.Tile, j => j.Expected);
            foreach (var p in plan.Paths.Concat(plan.Plants).Concat(plan.Sprinklers).Concat(plan.Crows))
            {
                if (clearing.TryGetValue(p, out var expected) && FieldSurface(loc, p) == expected)
                    continue;
                if (plan.Paths.Contains(p))
                {
                    if (Pathfinder.IsPassable(loc, p.X, p.Y) && !Pathfinder.IsTouchWarp(loc, p.X, p.Y))
                        continue;
                }
                else
                {
                    string task = plan.Sprinklers.Contains(p) ? "sprinkler" : plan.Crows.Contains(p) ? "crow" : "seed";
                    if (FieldDone(loc, plan, new FieldJob(p, task)) || FieldSurface(loc, p) is "bare" or "soil")
                        continue;
                    // 已经播完、后来收割或换种的格子归玩家。棚架仍需遵守原走道。
                    if (task == "seed" && !plan.Jobs.Any(j => j.Tile == p && j.Task is "hoe" or "seed")
                        && (plan.Raised || Pathfinder.IsPassable(loc, p.X, p.Y)))
                        continue;
                }
                return $"({p.X},{p.Y}) 有新障碍或作物，保留现场";
            }
            return null;
        }

        private static int FieldTool(FieldJob job)
        {
            if (job.Task == "hoe" || job.Expected.StartsWith("?"))
                return Util.FindTool<Hoe>();
            if (job.Expected.StartsWith("o"))
                return Util.FindTool<Pickaxe>();
            if (job.Expected.StartsWith("/") || job.Expected.StartsWith("tree"))
                return Util.FindTool<Axe>();
            return Util.FindTool<MeleeWeapon>(w => w.isScythe());
        }

        private static void CutFieldTile(GameLocation loc, Point p, MeleeWeapon scythe)
        {
            // 原版挥镰会扫过多格；只把原版命中回调分派给计划中的这一格，避免误割隔壁作物。
            var v = p.ToVector2();
            scythe.lastUser = Player;
            if (loc.terrainFeatures.TryGetValue(v, out var feature) && feature.performToolAction(scythe, 0, v))
                loc.terrainFeatures.Remove(v);
            if (loc.objects.TryGetValue(v, out var obj) && obj.performToolAction(scythe))
                loc.objects.Remove(v);
        }

        private IEnumerable<object> WorkFieldTile(GameLocation loc, FieldPlan plan, FieldJob job, Outcome result)
        {
            Point p = job.Tile;
            var stands = Pathfinder.AdjacentStandTiles(loc, p).Where(t => !Pathfinder.IsTouchWarp(loc, t.X, t.Y)).ToList();
            // 放设备、种棚架时从永久可走的一侧操作，最后一颗种子也不会把自己关在田中间。
            if (job.Task is "sprinkler" or "crow" || job.Task == "seed" && plan.Raised)
                stands.RemoveAll(t => !plan.Paths.Contains(t) && (plan.Raised || !plan.Plants.Contains(t)));
            var route = Pathfinder.FindPath(loc, Player.TilePoint, stands);
            if (route == null)
            {
                result.Fail("走不到相邻操作格");
                yield break;
            }
            foreach (var tick in WalkPath(route, result, fieldWork: true))
                yield return tick;
            if (!result.Ok)
                yield break;
            foreach (var tick in WaitPlayerFree())
                yield return tick;
            for (int hit = 0; hit < 12; hit++)
            {
                if (WorkLimit(loc) is string limit)
                {
                    result.Fail(limit);
                    yield break;
                }
                if (FieldDone(loc, plan, job))
                {
                    result.Succeed();
                    yield break;
                }
                if (Util.Manhattan(Player.TilePoint, p) != 1 || loc.IsTileOccupiedBy(p.ToVector2(), CollisionMask.Farmers | CollisionMask.Characters))
                {
                    result.Fail("站位有变化，或有人、动物挡住了目标格");
                    yield break;
                }
                string surface = FieldSurface(loc, p);
                if (job.Task == "clear" ? surface != job.Expected : job.Task == "hoe" ? surface != "bare"
                    : job.Task == "seed" ? surface != "soil" : surface is not ("soil" or "bare"))
                {
                    result.Fail("现场格子发生变化");
                    yield break;
                }
                Face(p);
                if (job.Task is "clear" or "hoe")
                {
                    int slot = FieldTool(job);
                    if (slot < 0)
                    {
                        result.Fail("缺少这一格需要的工具");
                        yield break;
                    }
                    Player.CurrentToolIndex = slot;
                    if (Player.CurrentTool is MeleeWeapon scythe)
                        CutFieldTile(loc, p, scythe);
                    else if (Context.IsMultiplayer)
                    {
                        // 联机时核对与命中在同一个主线程帧完成，避免挥动动画期间别人刚放下的东西挨打。
                        Player.toolPower.Value = 0;
                        Player.CurrentTool.DoFunction(loc, p.X * 64 + 32, p.Y * 64 + 32, 1, Player);
                    }
                    else
                    {
                        foreach (var tick in UseToolOnce(slot, p, result))
                            yield return tick;
                        if (!result.Ok)
                            yield break;
                    }
                }
                else
                {
                    var item = FieldMaterial(plan, job);
                    string reason = item == null ? "背包缺少可用的" + JobName(plan, job.Task)
                        : PlaceableProblem(loc, item, p);
                    if (reason == null && (job.Task != "seed" || plan.Raised))
                        reason = FieldConflict(loc, plan);
                    if (reason == null && (job.Task != "seed" || plan.Raised))
                        reason = BlockageProblem(loc, p);
                    if (reason == null && job.Task == "seed")
                    {
                        var dirt = (HoeDirt)loc.terrainFeatures[p.ToVector2()];
                        if (!Crop.IsInSeason(loc, plan.Seed) || !loc.CanPlantSeedsHere(plan.Seed, p.X, p.Y, false, out _)
                            || !dirt.canPlantThisSeedHere(plan.Seed) || SeedWontRipen(item, loc, out _, out _))
                            reason = "此处或本季不适合播种，或季末前来不及成熟";
                    }
                    if (reason != null)
                    {
                        result.Fail(reason);
                        yield break;
                    }
                    Player.CurrentToolIndex = Player.Items.IndexOf(item);
                    Utility.tryToPlaceItem(loc, item, p.X * 64 + 32, p.Y * 64 + 32);
                }
                foreach (var tick in WaitTicks(12))
                    yield return tick;
                if (job.Task != "clear" || FieldDone(loc, plan, job))
                    break;
            }
            if (Loc == loc && FieldDone(loc, plan, job))
                result.Succeed();
            else
                result.Fail("原版动作后未看到预期结果");
        }

        private void SaveFieldBook(FieldBook book)
        {
            book.FinishedToday.RemoveAll(p => p.WorkDay < Game1.Date.TotalDays);
            WriteFieldBook(book);
            FieldTodos = book;
        }

        internal static List<FieldJob> RemainingFieldWork(FieldPlan plan, Func<FieldJob, bool> done, Func<Point, bool> bare)
        {
            // 空耕地隔天可能消失。只有还欠播种的格才补锄，已经收割的地不重开。
            var jobs = plan.Jobs.ToList();
            foreach (var j in jobs.Where(j => j.Task == "seed").ToList())
                if (bare(j.Tile) && !jobs.Any(h => h.Task == "hoe" && h.Tile == j.Tile))
                    jobs.Add(new FieldJob(j.Tile, "hoe"));
            foreach (var j in plan.Sprinklers.Select(p => new FieldJob(p, "sprinkler"))
                .Concat(plan.Crows.Select(p => new FieldJob(p, "crow"))))
                if (!jobs.Contains(j))
                    jobs.Add(j);
            return jobs.Where(j => !done(j))
                .OrderBy(j => j.Task switch { "clear" => 0, "hoe" => 1, "seed" => 3, _ => 2 })
                .ThenBy(j => j.Task == "hoe" ? Distance2(j.Tile, plan.Area.Center) : 0).ToList();
        }

        private void ReconcileField(GameLocation loc, FieldPlan plan)
        {
            if (plan.WorkDay != Game1.Date.TotalDays)
            {
                plan.WorkDay = Game1.Date.TotalDays;
                plan.BeforeWork = plan.Jobs.ToList();
            }
            var pending = RemainingFieldWork(plan, j => FieldDone(loc, plan, j), p => FieldSurface(loc, p) == "bare");
            if (pending.Count == 0 && FieldConflict(loc, plan) != null)
                pending.Add(new FieldJob(plan.Entry, "verify"));
            if (pending.Count == 0)
                SaveFieldBook(FinishField(FieldTodos, plan.Id));
            else if (!plan.Jobs.SequenceEqual(pending))
            {
                var previous = plan.Jobs;
                plan.Jobs = pending;
                try { SaveFieldBook(FieldTodos); }
                catch { plan.Jobs = previous; throw; }
            }
        }

        private IEnumerable<object> BuildField(GameLocation loc, FieldPlan plan)
        {
            try
            {
                foreach (var step in RunFieldBatches(loc, plan))
                    yield return step;
            }
            finally
            {
                // 取消、剧情或被攻击时动作队列会 Dispose 协程；在退出前也记下本批已经做完的格。
                if (Context.IsWorldReady && Loc == loc && FieldTodos?.Plans.Contains(plan) == true)
                    ReconcileField(loc, plan);
            }
        }

        private IEnumerable<object> RunFieldBatches(GameLocation loc, FieldPlan plan)
        {
            int completed = 0;
            while (true)
            {
                if (Loc != loc || !Context.IsWorldReady)
                {
                    yield return ActionResult.Fail("已离开计划地点。" + Summary(plan));
                    yield break;
                }
                ReconcileField(loc, plan);
                if (!FieldTodos.Plans.Contains(plan))
                {
                    yield return ActionResult.Success($"开田 plan {plan.Id} 已完成，待办已划掉。" + DrawField(plan));
                    yield break;
                }
                string stop = WorkLimit(loc) ?? FieldConflict(loc, plan);
                // 没带设备时保留位置，继续做后面的播种；缺种子时原地暂停。
                var batch = plan.Jobs.Where(j => j.Task is not ("crow" or "sprinkler") || FieldMaterial(plan, j) != null)
                    .Take(8).ToList();
                if (batch.Count == 0)
                    stop ??= "预留位置还缺相应设备";
                FieldJob at = batch.FirstOrDefault() ?? plan.Jobs.First();
                if (stop == null)
                {
                    foreach (var j in batch)
                    {
                        at = j;
                        MainThread.Progress = $"plan {plan.Id}，{JobName(plan, j.Task)} ({j.Tile.X},{j.Tile.Y})，prepare_field mode=resume plan_id={plan.Id}";
                        if (FieldDone(loc, plan, j))
                            continue;
                        stop = WorkLimit(loc);
                        if (stop != null)
                            break;
                        if (j.Task is "crow" or "sprinkler" && FieldMaterial(plan, j) == null)
                            continue;
                        var result = new Outcome();
                        foreach (var tick in WorkFieldTile(loc, plan, j, result))
                            yield return tick;
                        if (!result.Ok)
                        {
                            stop = result.Message;
                            break;
                        }
                        completed++;
                    }
                }
                if (stop != null)
                {
                    if (Loc == loc && Context.IsWorldReady)
                        ReconcileField(loc, plan);
                    yield return ActionResult.Fail($"本次做了 {completed} 项，停在{JobName(plan, at.Task)} ({at.Tile.X},{at.Tile.Y})：{stop}。" + Summary(plan));
                    yield break;
                }
                yield return null;
            }
        }

        private FieldPlan previewField;
        private string previewFieldKey;
        internal static FieldBook RestoredFields(FieldBook book, int today)
        {
            // 游戏只在过夜时存盘。当天退出重读，JSON 比地面领先，要拿当天施工前的清单重新对账。
            return new FieldBook
            {
                NextId = book.NextId,
                Plans = book.Plans.Concat(book.FinishedToday.Where(p => p.WorkDay == today))
                    .Select(p => p.WorkDay == today ? p.WithJobs(p.BeforeWork) : p).ToList()
            };
        }

        internal void LoadFields(FieldBook book)
        {
            FieldTodos = book == null ? null : RestoredFields(book, Game1.Date.TotalDays);
            if (FieldTodos != null)
                foreach (var plan in FieldTodos.Plans.Where(p => p.Location == Loc?.NameOrUniqueName).ToList())
                    ReconcileField(Loc, plan);
            previewField = null;
            previewFieldKey = null;
        }

        public IEnumerable<object> PrepareField(string mode, int? id, Point? near, int? x1, int? y1, int? x2, int? y2,
            int? count, string seedSpec, string sprinkler, bool scarecrow, string path)
        {
            var loc = Loc;
            if (FieldTodos == null)
            {
                yield return ActionResult.Fail("本存档开田待办未载入");
                yield break;
            }
            if (mode == "resume")
            {
                var saved = FieldTodos.Plans.FirstOrDefault(p => p.Id == id);
                if (saved == null || saved.Location != loc.NameOrUniqueName)
                {
                    yield return ActionResult.Fail(saved == null ? $"没有这个开田待办 plan_id={id}" : $"先到 {saved.Location} 再续跑 plan {id}");
                    yield break;
                }
                foreach (var tick in BuildField(loc, saved))
                    yield return tick;
                yield break;
            }
            if (mode is not ("execute" or "preview") || id.HasValue || !FieldSprinklerIds.ContainsKey(sprinkler)
                || path is not ("auto" or "rows") || count is < 1 or > 80)
            {
                yield return ActionResult.Fail("请检查 mode、sprinkler、path 和 count（1～80）；plan_id 只用于 resume");
                yield break;
            }
            int target = count ?? (x1.HasValue ? 80 : 20);
            string seed = seedSpec == null ? null : ResolveFieldSeed(seedSpec);
            bool raised = false;
            if (seedSpec != null)
            {
                if (seed == null || !Crop.TryGetData(seed, out var data))
                {
                    yield return ActionResult.Fail("无法从作物数据确定种子和棚架类型，请指定一种确定的作物种子");
                    yield break;
                }
                raised = data.IsRaised;
            }
            string sizing = "";
            if (sprinkler == "none")
            {
                var can = Player.Items.OfType<WateringCan>().OrderByDescending(w => w.WaterLeft).FirstOrDefault();
                var reserved = FieldReserved();
                int seeds = Player.Items.Where(i => i?.Category == SObject.SeedsCategory && !reserved(i) && (seed == null || i.ItemId == seed))
                    .Sum(i => i.Stack);
                int water = can == null ? 0 : Math.Min(can.WaterLeft, 12 * (1 + can.UpgradeLevel));
                if (water <= 0)
                {
                    yield return ActionResult.Fail("手浇田需要有水的水壶，请带上或先装水");
                    yield break;
                }
                target = Math.Min(target, Math.Min(water, seeds > 0 ? seeds : 4));
                sizing = $"。手浇预算按现有水量和每级 12 格控制；背包可用种子 {seeds}，零库存最多预备 4 格，本次上限 {target}";
            }

            string key = $"{loc.NameOrUniqueName}|{near}|{x1},{y1},{x2},{y2}|{count}|{seed}|{sprinkler}|{scarecrow}|{path}";
            if (mode == "execute" && FieldTodos.Plans.FirstOrDefault(f => f.RequestKey == key) is FieldPlan ongoing)
            {
                yield return ActionResult.Fail("已有同一请求的待办。" + Summary(ongoing));
                yield break;
            }
            FieldPlan plan = mode == "execute" && previewFieldKey == key ? previewField : null;
            if (plan != null && sprinkler == "none" && plan.Plants.Count > target)
            {
                yield return ActionResult.Fail("水壶或种子预算已变，原图超出手浇上限；请重新 preview");
                yield break;
            }
            if (plan == null)
            {
                var region = FieldRegion(loc, x1, y1, x2, y2, near, target, out string error);
                if (region is Rectangle box)
                    plan = SurveyField(loc, box, target, seed, sprinkler, scarecrow, raised || path == "rows", raised,
                        near == null && x1 == null, out error);
                if (plan == null)
                {
                    yield return ActionResult.Fail(error ?? "找不到可用区域；请用 near 或矩形指定");
                    yield break;
                }
            }
            plan.RequestedCount = count;
            if (mode == "preview")
            {
                previewFieldKey = key;
                previewField = plan;
                yield return ActionResult.Success("开田预览（同参数 execute 使用这份图）\n" + DrawField(plan) + sizing
                    + (seed != null && !Crop.IsInSeason(loc, seed) ? "。当前季节不适合播种" : ""));
                yield break;
            }
            if (FieldConflict(loc, plan) is string changed)
            {
                previewField = null;
                previewFieldKey = null;
                yield return ActionResult.Fail(changed + "；请重新 preview");
                yield break;
            }
            plan.RequestKey = key;
            plan.WorkDay = Game1.Date.TotalDays;
            plan.BeforeWork = plan.Jobs.ToList();
            SaveFieldBook(RegisterField(FieldTodos, plan));
            previewField = null;
            previewFieldKey = null;
            foreach (var tick in BuildField(loc, plan))
                yield return tick;
        }
    }
}
