using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.Objects;
using StardewValley.Quests;
using StardewValley.SpecialOrders.Objectives;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 任务 / 收集包要交的东西 ─────────────────────────

        /// <summary>
        /// 返回一个判断函数：这件东西是不是任务、特别订单、社区中心收集包要交的（返回原因，不是返回 null）。
        /// 数量比要交的多时不算（多出来的可以吃、可以卖）。先把任务和收集包算好，背包几十格挨个查也不慢。
        /// </summary>
        internal static Func<Item, string> ReservedChecker()
        {
            var needs = new List<(Func<Item, bool> match, int count, string why)>();
            foreach (var q in Game1.player.questLog)
            {
                if (q.completed.Value)
                    continue;
                string name = SafeQuestName(q);
                switch (q)
                {
                    case ItemDeliveryQuest d when d.ItemId.Value != null:
                        string id = ItemRegistry.QualifyItemId(d.ItemId.Value) ?? d.ItemId.Value;
                        needs.Add((i => i.QualifiedItemId == id, Math.Max(1, d.number.Value), $"任务“{name}”要交"));
                        break;
                    case LostItemQuest l when l.ItemId.Value != null:
                        needs.Add((i => i.QualifiedItemId == l.ItemId.Value, 1, $"任务“{name}”要交"));
                        break;
                }
            }
            foreach (var order in Game1.player.team.specialOrders)
            {
                foreach (var obj in order.objectives)
                {
                    if (obj.IsComplete())
                        continue;
                    var tagSets = obj switch
                    {
                        DeliverObjective d => d.acceptableContextTagSets,
                        DonateObjective d => d.acceptableContextTagSets,
                        _ => null
                    };
                    if (tagSets == null || tagSets.Count == 0)
                        continue;
                    var sets = tagSets.ToList();
                    string why = $"特别订单“{SafeOrderName(order)}”要交";
                    needs.Add((i => sets.Any(set => set.Split(',').All(tag => ItemContextTagManager.DoAnyTagsMatch(tag.Trim().Split('/'), i.GetContextTags()))),
                        Math.Max(1, obj.GetMaxCount() - obj.GetCount()), why));
                }
            }
            if (Knowledge.CommunityCenterUnavailable() == null)
            {
                foreach (var b in Knowledge.Bundles().Where(b => !b.Complete))
                {
                    foreach (var ing in b.Ingredients.Where(i => !i.completed))
                    {
                        var ingredient = ing;
                        // 任意一类（比如“任意鸡蛋”）的不算：同类东西太多，留不过来
                        if (ingredient.category.HasValue)
                            continue;
                        needs.Add((i => Knowledge.Matches(i, ingredient, ignoreStack: true), ingredient.stack, $"社区中心“{b.DisplayName}”要交"));
                    }
                }
            }
            return item =>
            {
                if (item == null)
                    return null;
                if (item is SObject o && o.questItem.Value)
                    return "任务物品";
                foreach (var (match, count, why) in needs)
                {
                    try
                    {
                        if (match(item) && item.Stack <= count)
                            return why;
                    }
                    catch
                    {
                        // 个别物品取不到标签，跳过
                    }
                }
                return null;
            };
        }

        private static string SafeQuestName(Quest q)
        {
            try
            {
                return q.GetName();
            }
            catch
            {
                return q.id.Value;
            }
        }

        private static string SafeOrderName(StardewValley.SpecialOrders.SpecialOrder order)
        {
            try
            {
                return order.GetName();
            }
            catch
            {
                return order.questKey.Value;
            }
        }

        private const string ForceHint = "真要这么做就再调用一次并传 force=true";

        // ───────────────────────── 出门带什么 ─────────────────────────

        /// <summary>tidy_backpack 的 purpose：出门干什么。不同目的留在身上的东西不一样。</summary>
        private static string NormalizePurpose(string purpose)
        {
            if (string.IsNullOrWhiteSpace(purpose))
                return null;
            string p = purpose.Trim().ToLowerInvariant();
            if (p.Contains("mine") || p.Contains("矿") || p.Contains("skull") || p.Contains("战斗"))
                return "mine";
            if (p.Contains("fish") || p.Contains("钓"))
                return "fish";
            if (p.Contains("farm") || p.Contains("农") || p.Contains("种"))
                return "farm";
            if (p.Contains("town") || p.Contains("镇") || p.Contains("social") || p.Contains("送礼") || p.Contains("社交"))
                return "town";
            return null;
        }

        private static readonly Dictionary<string, string> PurposeName = new()
        {
            ["mine"] = "下矿", ["fish"] = "钓鱼", ["farm"] = "种地", ["town"] = "去镇上"
        };

        /// <summary>某个出门目的用得上的东西（purpose 为 null 时什么都不额外排除）。</summary>
        private static bool UsefulFor(string purpose, Item i)
        {
            // 戒指、鞋子到哪都带；武器除了去镇上都带（农场晚上、钓鱼的地方也会有怪）
            if (i is Ring or Boots || purpose != "town" && i is MeleeWeapon { } mw && !mw.isScythe())
                return true;
            switch (purpose)
            {
                case "mine":
                    return i is Pickaxe || (i is MeleeWeapon w && !w.isScythe()) || i is Slingshot
                        || i is SObject o && (o.Name.Contains("Bomb") || o.QualifiedItemId is "(O)71" || o.Edibility > 0 && o.healthRecoveredOnConsumption() > 0
                                              || o.Category == SObject.CookingCategory)
                        || i is Ring or Boots;
                case "fish":
                    return i is FishingRod || i is SObject f && (f.Category is SObject.baitCategory or SObject.tackleCategory
                        || f.Category == SObject.CookingCategory || f.QualifiedItemId == "(O)710"); // 蟹笼
                case "farm":
                    return i is Hoe or WateringCan or Axe or Pickaxe || (i is MeleeWeapon s && s.isScythe())
                        || i is SObject g && (g.Category is SObject.SeedsCategory or SObject.fertilizerCategory || g.IsSprinkler()
                                              || g.bigCraftable.Value || g.Category == SObject.CookingCategory);
                case "town":
                    return i is SObject t && (t.Category == SObject.CookingCategory || t.QualifiedItemId is "(O)458" or "(O)460");
                default:
                    return true;
            }
        }

        /// <summary>这次出门该带、背包里却没有的东西；箱子里有的话说在哪个箱子。都带齐了返回 null。</summary>
        private static string MissingForTrip(string purpose)
        {
            var need = new List<(string name, Func<Item, bool> match)>();
            switch (purpose)
            {
                case "mine":
                    need.Add(("武器", i => i is MeleeWeapon w && !w.isScythe()));
                    need.Add(("镐子", i => i is Pickaxe));
                    need.Add(("回血的食物", i => i is SObject o && o.Edibility > 0 && o.healthRecoveredOnConsumption() > 0));
                    break;
                case "fish":
                    need.Add(("鱼竿", i => i is FishingRod));
                    need.Add(("鱼饵", i => i is SObject o && o.Category == SObject.baitCategory));
                    break;
                case "farm":
                    need.Add(("锄头", i => i is Hoe));
                    need.Add(("水壶", i => i is WateringCan));
                    break;
            }
            var parts = new List<string>();
            List<(GameLocation loc, Point tile, Chest chest)> chests = null;
            foreach (var (name, match) in need)
            {
                if (Player.Items.Any(i => i != null && match(i)))
                    continue;
                chests ??= AllChests();
                var where = chests.FirstOrDefault(c => c.chest.GetItemsForPlayer().Any(i => i != null && match(i)));
                parts.Add(where.chest != null
                    ? $"{name}在 {where.loc.NameOrUniqueName} ({where.tile.X},{where.tile.Y}) 的箱子里（take_items 拿出来）"
                    : $"没有{name}");
            }
            return parts.Count == 0 ? null : $"{PurposeName[purpose]}还缺：" + string.Join("；", parts);
        }

        /// <summary>出门（下矿、钓鱼）时身上带着、但这次用不上的东西，给动作结果加一句提醒。</summary>
        private static string TripClutterHint(string purpose)
        {
            var reserved = ReservedChecker();
            var clutter = Player.Items.Where(i => i != null && !UsefulFor(purpose, i) && reserved(i) == null
                                                  && !(i is SObject o && o.Edibility > 0)).ToList();
            if (clutter.Count < 4 || Player.freeSpotsInInventory() > 6)
                return null;
            return $"背包空位只剩 {Player.freeSpotsInInventory()} 个，带着{PurposeName[purpose]}用不上的 {string.Join("、", clutter.Take(6).Select(i => i.DisplayName))}{(clutter.Count > 6 ? $" 等 {clutter.Count} 样" : "")}。"
                + $"下次出门前先在农场 tidy_backpack purpose={purpose}，空出位置装战利品";
        }

        // ───────────────────────── 农田 ─────────────────────────

        internal sealed class Field
        {
            public List<Point> Tiles = new();
            public int Crops, Empty;
            public Rectangle Box;
            public string Describe() =>
                $"({Box.Left},{Box.Top})-({Box.Right - 1},{Box.Bottom - 1}) 共 {Tiles.Count} 格耕地：作物 {Crops}，空 {Empty}";
        }

        /// <summary>
        /// 把耕地按“挨在一起”分成一块块田（隔一格也算同一块：洒水器、稻草人、小路占的格子不把田切开）。
        /// 大的在前。
        /// </summary>
        internal static List<Field> Fields(GameLocation loc)
        {
            var dirt = new HashSet<Point>(loc.terrainFeatures.Pairs.Where(p => p.Value is HoeDirt).Select(p => new Point((int)p.Key.X, (int)p.Key.Y)));
            var seen = new HashSet<Point>();
            var fields = new List<Field>();
            foreach (var start in dirt)
            {
                if (!seen.Add(start))
                    continue;
                var field = new Field();
                var queue = new Queue<Point>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    field.Tiles.Add(p);
                    for (int dx = -2; dx <= 2; dx++)
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            var n = new Point(p.X + dx, p.Y + dy);
                            if (Math.Abs(dx) + Math.Abs(dy) <= 2 && dirt.Contains(n) && seen.Add(n))
                                queue.Enqueue(n);
                        }
                }
                int x1 = field.Tiles.Min(p => p.X), y1 = field.Tiles.Min(p => p.Y);
                field.Box = new Rectangle(x1, y1, field.Tiles.Max(p => p.X) - x1 + 1, field.Tiles.Max(p => p.Y) - y1 + 1);
                foreach (var p in field.Tiles)
                {
                    var hd = (HoeDirt)loc.terrainFeatures[new Vector2(p.X, p.Y)];
                    if (hd.crop != null)
                        field.Crops++;
                    else if (!loc.Objects.ContainsKey(new Vector2(p.X, p.Y)))
                        field.Empty++;
                }
                fields.Add(field);
            }
            return fields.OrderByDescending(f => f.Tiles.Count).ToList();
        }

        /// <summary>和 till_area 一样的“能直接开垦”判断。</summary>
        private static bool Tillable(GameLocation loc, Point p) =>
            loc.isTileOnMap(p.X, p.Y)
            && loc.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back") != null
            && !loc.Objects.ContainsKey(new Vector2(p.X, p.Y))
            && !loc.terrainFeatures.ContainsKey(new Vector2(p.X, p.Y))
            && Pathfinder.IsPassable(loc, p.X, p.Y);

        /// <summary>
        /// 挨着最大那块田、能直接开垦的扩田位置：在田的上下左右各试一条同宽（或同高）的带子，挑能开垦格最多的。
        /// 没有田时返回 null。
        /// </summary>
        internal static string SuggestFieldExpansion(GameLocation loc, int want = 20)
        {
            var main = Fields(loc).FirstOrDefault(f => f.Tiles.Count >= 4);
            if (main == null)
                return null;
            var b = main.Box;
            var options = new List<(Rectangle r, string side)>();
            int rows = Math.Clamp((int)Math.Ceiling(want / (double)b.Width), 1, 4);
            int cols = Math.Clamp((int)Math.Ceiling(want / (double)b.Height), 1, 4);
            options.Add((new Rectangle(b.Left, b.Bottom, b.Width, rows), "下"));
            options.Add((new Rectangle(b.Left, b.Top - rows, b.Width, rows), "上"));
            options.Add((new Rectangle(b.Right, b.Top, cols, b.Height), "右"));
            options.Add((new Rectangle(b.Left - cols, b.Top, cols, b.Height), "左"));
            var best = options.Select(o => (o.r, o.side, n: o.r.Width * o.r.Height == 0 ? 0
                    : Enumerable.Range(o.r.Left, o.r.Width).SelectMany(x => Enumerable.Range(o.r.Top, o.r.Height).Select(y => new Point(x, y))).Count(p => Tillable(loc, p))))
                .OrderByDescending(o => o.n).First();
            if (best.n == 0)
                return $"最大的田 {main.Describe()}，四周没有能直接开垦的空地（先 clear_area 清一清田边）";
            return $"挨着最大的田往{best.side}扩：till_area {best.r.Left},{best.r.Top},{best.r.Right - 1},{best.r.Bottom - 1}（能直接开垦 {best.n} 格）";
        }

        /// <summary>矩形离某块田的距离（格，0 = 挨着或重叠）。</summary>
        private static int RectGap(Rectangle a, Rectangle b)
        {
            int dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right) + 1);
            int dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom) + 1);
            return Math.Max(dx, dy);
        }

        /// <summary>状态里的农田概况：有几块田、在哪、空多少；零散耕地多少；往哪扩。</summary>
        internal static string FieldOverview(GameLocation loc)
        {
            var fields = Fields(loc);
            if (fields.Count == 0)
                return null;
            var big = fields.Where(f => f.Tiles.Count >= 4).ToList();
            var sb = new StringBuilder();
            sb.Append("农田：");
            sb.Append(big.Count == 0 ? "还没有成片的田" : string.Join("；", big.Take(4).Select((f, i) => $"田{i + 1} {f.Describe()}")));
            var small = fields.Where(f => f.Tiles.Count < 4).ToList();
            int scattered = small.Sum(f => f.Tiles.Count), scatteredCrops = small.Sum(f => f.Crops);
            if (scattered > 0)
                sb.Append($"；零散耕地 {scattered} 格" + (scatteredCrops > 0 ? $"（其中 {scatteredCrops} 株作物，收完别再往这里种）" : "")
                    + "，plant_seeds 不会往零散耕地上种，空着的过几天会自己消失");
            sb.Append("。种子种进已有的田、新田挨着老田开，作物集中好浇水、好放洒水器");
            if (SuggestFieldExpansion(loc) is string ext && big.Sum(f => f.Empty) < 6)
                sb.Append("；" + ext);
            return sb.ToString();
        }

        // ───────────────────────── 建筑 / 机器摆放 ─────────────────────────

        /// <summary>
        /// 规划上的问题（放得下，但放了以后难用）：贴着别的建筑没留路、门口被挡、压着田边。没问题返回 null。
        /// 建筑的门在下面（鸡舍、畜棚、小屋的人门和动物门都朝南），所以下方两格要空出来。
        /// </summary>
        private static string BuildingLayoutProblem(Farm farm, Point tile, Point size, List<Rectangle> fieldBoxes = null)
        {
            var field = fieldBoxes ?? Fields(farm).Where(f => f.Tiles.Count >= 4).Select(f => f.Box).ToList();
            var footprint = new Rectangle(tile.X, tile.Y, size.X, size.Y);
            foreach (var box in field)
                if (RectGap(footprint, box) <= 2)
                    return $"紧挨着田 ({box.Left},{box.Top})-({box.Right - 1},{box.Bottom - 1})，以后没法往这边扩田，也挡洒水器";
            // 别的建筑门口（下方两行）也要空着
            foreach (var b in farm.buildings)
            {
                var door = new Rectangle(b.tileX.Value, b.tileY.Value + b.tilesHigh.Value, b.tilesWide.Value, 2);
                if (door.Intersects(footprint))
                    return $"堵在 ({b.tileX.Value},{b.tileY.Value}) 的 {b.buildingType.Value} 门口";
            }
            for (int x = tile.X - 1; x <= tile.X + size.X; x++)
                for (int y = tile.Y - 1; y <= tile.Y + size.Y + 2; y++)
                {
                    bool inside = x >= tile.X && x < tile.X + size.X && y >= tile.Y && y < tile.Y + size.Y;
                    if (inside)
                        continue;
                    var v = new Vector2(x, y);
                    var other = farm.buildings.FirstOrDefault(b => b.occupiesTile(v));
                    if (other != null)
                        return $"和 ({other.tileX.Value},{other.tileY.Value}) 的 {other.buildingType.Value} 挨得太近（四周要留一格路，门口下方要空两格）";
                    if (y > tile.Y + size.Y - 1 && x >= tile.X && x < tile.X + size.X && farm.isWaterTile(x, y))
                        return "门口下方是水，进不去";
                }
            return null;
        }

        /// <summary>自动选建筑位置：同类建筑排成一排（同一行或同一列、隔两格），没有同类就在农舍附近；不贴田、不堵门。</summary>
        private static Point? PlanBuildingSpot(Farm farm, BuildingData data, string buildingType)
        {
            var house = farm.GetMainFarmHouseEntry();
            // 同类放一起：鸡舍、畜棚、筒仓是一片（筒仓给它们供干草）；其他建筑（棚屋、水井……）跟同名的放一起
            bool AnimalArea(string type) => type.Contains("Coop") || type.Contains("Barn") || type == "Silo";
            var peers = farm.buildings.Where(b => AnimalArea(buildingType) ? AnimalArea(b.buildingType.Value) : b.buildingType.Value == buildingType)
                .Select(b => new Point(b.tileX.Value, b.tileY.Value)).ToList();
            int w = Pathfinder.MapWidth(farm), h = Pathfinder.MapHeight(farm);
            var boxes = Fields(farm).Where(f => f.Tiles.Count >= 4).Select(f => f.Box).ToList();
            var candidates = new List<(Point c, int score)>();
            for (int cx = 1; cx < w - data.Size.X; cx++)
                for (int cy = 1; cy < h - data.Size.Y - 2; cy++)
                {
                    var c = new Point(cx, cy);
                    int dHouse = Util.Manhattan(c, house);
                    if (dHouse < 6)
                        continue; // 别堵在家门口
                    int score = peers.Count > 0
                        ? peers.Min(p => Util.Manhattan(c, p) + (p.Y == c.Y || p.X == c.X ? 0 : 6)) * 2 + dHouse / 4
                        : dHouse;
                    candidates.Add((c, score));
                }
            foreach (var (c, _) in candidates.OrderBy(x => x.score))
                if (CanPlace(farm, data, c) && BuildingLayoutProblem(farm, c, data.Size, boxes) == null)
                    return c;
            return null;
        }

        /// <summary>农场上摆机器、箱子的规划问题：别压在田里、别堵建筑门口。洒水器、稻草人本来就该放田里。</summary>
        private static string PlacementProblem(GameLocation loc, Item item, Point tile)
        {
            if (loc is not Farm farm || item is not SObject o || !o.bigCraftable.Value || o.IsSprinkler() || o.IsScarecrow())
                return null;
            var v = new Vector2(tile.X, tile.Y);
            foreach (var f in Fields(farm).Where(f => f.Tiles.Count >= 4))
                if (f.Box.Contains(tile))
                    return $"({tile.X},{tile.Y}) 在田 ({f.Box.Left},{f.Box.Top})-({f.Box.Right - 1},{f.Box.Bottom - 1}) 里面，机器箱子放田里会占地、挡路";
            foreach (var b in farm.buildings)
            {
                int bx = b.tileX.Value, by = b.tileY.Value;
                if (tile.Y >= by + b.tilesHigh.Value && tile.Y <= by + b.tilesHigh.Value + 1 && tile.X >= bx && tile.X < bx + b.tilesWide.Value)
                    return $"({tile.X},{tile.Y}) 堵在 {b.buildingType.Value} 门口";
            }
            return null;
        }

        /// <summary>已经摆了机器/箱子的地方（同一片放一起好管理），给放置失败的提示用。</summary>
        private static string MachineAreaHint(GameLocation loc)
        {
            var spots = loc.objects.Pairs.Where(p => p.Value.bigCraftable.Value && !p.Value.IsSprinkler() && !p.Value.IsScarecrow())
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y)).ToList();
            if (spots.Count == 0)
                return "在出货箱或农舍旁边找块不碍路的空地，以后机器、箱子都放这一片";
            var center = spots.OrderBy(s => spots.Sum(t => Util.Manhattan(s, t))).First();
            return $"已经摆的机器/箱子集中在 ({center.X},{center.Y}) 一带，挨着它们放";
        }

        // ───────────────────────── 箱子总览 ─────────────────────────

        /// <summary>所有地点玩家放的箱子（农场、农舍、小屋、建筑里面……）。</summary>
        internal static List<(GameLocation loc, Point tile, Chest chest)> AllChests()
        {
            var list = new List<(GameLocation, Point, Chest)>();
            Utility.ForEachLocation(l =>
            {
                foreach (var (tile, chest) in PlayerChests(l))
                    list.Add((l, tile, chest));
                // 冰箱也是箱子
                if (l is StardewValley.Locations.FarmHouse fh && fh.fridge.Value is Chest fridge && fridge.Items.Any(i => i != null))
                    list.Add((l, fh.fridgePosition, fridge));
                return true;
            });
            return list;
        }

        /// <summary>状态里的箱子概况：每个箱子在哪、装了多少格、主要装了什么。</summary>
        internal static string ChestOverview(int maxChars = 900)
        {
            var chests = AllChests();
            if (chests.Count == 0)
                return null;
            var sb = new StringBuilder();
            foreach (var (loc, tile, chest) in chests)
            {
                var items = chest.GetItemsForPlayer().Where(i => i != null).ToList();
                bool fridge = loc is StardewValley.Locations.FarmHouse fh && chest == fh.fridge.Value;
                string head = $"- {loc.NameOrUniqueName} ({tile.X},{tile.Y}){(fridge ? " 冰箱" : "")} {items.Count}/{chest.GetActualCapacity()} 格：";
                string body = items.Count == 0 ? "空" : string.Join("、", items.OrderByDescending(i => i.Stack).Take(10).Select(i => $"{i.DisplayName}x{i.Stack}"))
                                                    + (items.Count > 10 ? $" 等 {items.Count} 种" : "");
                if (sb.Length + head.Length + body.Length > maxChars)
                {
                    sb.AppendLine($"- ……还有 {chests.Count - sb.ToString().Split('\n').Length + 1} 个箱子，用 list_chests 看全部");
                    break;
                }
                sb.AppendLine(head + body);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>list_chests：列出所有箱子里的东西；给了 item 就只找这样东西放在哪。不花游戏时间。</summary>
        public IEnumerable<object> ListChests(string item)
        {
            var chests = AllChests();
            if (chests.Count == 0)
            {
                yield return ActionResult.Fail("还没有放过箱子。craft Chest（50 木材）再 place_item 放在农场上");
                yield break;
            }
            var filter = string.IsNullOrWhiteSpace(item) ? null : ItemFilter(item);
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var (loc, tile, chest) in chests)
            {
                var items = chest.GetItemsForPlayer().Where(i => i != null && (filter == null || filter(i))).ToList();
                if (filter != null && items.Count == 0)
                    continue;
                hits += items.Sum(i => i.Stack);
                sb.AppendLine($"{loc.NameOrUniqueName} ({tile.X},{tile.Y}) {chest.GetItemsForPlayer().Count(i => i != null)}/{chest.GetActualCapacity()} 格："
                    + (items.Count == 0 ? "空" : string.Join("、", items.Select(Util.ItemLabel))));
            }
            if (filter != null && hits == 0)
            {
                var bag = Player.Items.Where(i => i != null && filter(i)).Sum(i => i.Stack);
                yield return ActionResult.Fail($"哪个箱子里都没有“{item}”" + (bag > 0 ? $"（背包里有 {bag} 个）" : ""));
                yield break;
            }
            yield return ActionResult.Success((filter != null ? $"“{item}”一共 {hits} 个，在：\n" : "")
                + sb.ToString().TrimEnd() + "\n（take_items 要先 travel_to 到箱子所在地点）");
        }
    }
}
