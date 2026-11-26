using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 大箱子原地升级 ─────────────────────────

        internal const string WoodChestId = "(BC)130";
        internal const string StoneChestId = "(BC)232";
        internal const string BigWoodChestId = "(BC)BigChest";
        internal const string BigStoneChestId = "(BC)BigStoneChest";

        internal static bool IsBigChestId(string qualifiedId)
            => qualifiedId is BigWoodChestId or BigStoneChestId;

        internal static bool IsBigChestItem(Item item)
            => item != null && IsBigChestId(item.QualifiedItemId);

        internal static bool IsUpgradableNormalChestId(string qualifiedId)
            => qualifiedId is WoodChestId or StoneChestId;

        internal static string FormatChestTypeLabel(string qualifiedId, Chest.SpecialChestTypes specialType, int capacity, bool isFridge)
        {
            if (isFridge)
                return "冰箱";
            if (qualifiedId == BigWoodChestId)
                return $"大箱子（{capacity} 格）";
            if (qualifiedId == BigStoneChestId)
                return $"大石箱（{capacity} 格）";
            if (specialType == Chest.SpecialChestTypes.BigChest)
                return $"大箱子（{capacity} 格）";
            if (specialType == Chest.SpecialChestTypes.JunimoChest)
                return $"祝尼魔箱（{capacity} 格）";
            if (specialType == Chest.SpecialChestTypes.MiniShippingBin)
                return $"迷你出货箱（{capacity} 格）";
            if (specialType == Chest.SpecialChestTypes.AutoLoader)
                return $"加料器（{capacity} 格）";
            if (qualifiedId == WoodChestId && specialType == Chest.SpecialChestTypes.None)
                return $"普通箱子（{capacity} 格，可用大箱子升级）";
            if (qualifiedId == StoneChestId && specialType == Chest.SpecialChestTypes.None)
                return $"石箱（{capacity} 格，可用大石箱升级）";
            return $"箱子（{capacity} 格）";
        }

        internal static string ChestTypeLabel(Chest chest, bool isFridge = false)
            => FormatChestTypeLabel(chest.QualifiedItemId, chest.SpecialChestType, chest.GetActualCapacity(), isFridge || chest.fridge.Value);

        internal static string FormatChestShortKind(Chest chest, bool isFridge = false)
        {
            if (isFridge || chest.fridge.Value)
                return " 冰箱";
            return chest.QualifiedItemId switch
            {
                BigWoodChestId => " 大箱子",
                BigStoneChestId => " 大石箱",
                WoodChestId when chest.SpecialChestType == Chest.SpecialChestTypes.None => " 普通箱子",
                StoneChestId when chest.SpecialChestType == Chest.SpecialChestTypes.None => " 石箱",
                _ => chest.SpecialChestType switch
                {
                    Chest.SpecialChestTypes.BigChest => " 大箱子",
                    Chest.SpecialChestTypes.JunimoChest => " 祝尼魔箱",
                    Chest.SpecialChestTypes.MiniShippingBin => " 迷你出货箱",
                    _ => ""
                }
            };
        }

        internal static string FormatStorageChestSummary(IEnumerable<string> typeLabels)
        {
            var counts = typeLabels.GroupBy(l => l).Select(g => g.Count() == 1 ? g.Key : $"{g.Key}×{g.Count()}").ToList();
            return counts.Count == 0 ? "" : $"（{string.Join("、", counts)}）";
        }

        /// <summary>
        /// 校验大箱子原地升级目标箱子是否合法。合法返回 null，否则返回给 AI 看的明确原因。
        /// 规则：木箱 (BC)130 只能用大箱子 (BC)BigChest 升级；石箱 (BC)232 只能用大石箱 (BC)BigStoneChest 升级；
        /// 已是大箱子或特殊箱子（冰箱、祝尼魔箱、迷你出货箱、加料器、礼物箱等）均拒绝。
        /// </summary>
        internal static string ChestUpgradeProblem(
            string bigChestId,
            string targetId,
            bool targetPlayerChest,
            bool targetGiftbox,
            bool targetFridge,
            Chest.SpecialChestTypes targetSpecialType,
            string targetName)
        {
            if (!IsBigChestId(bigChestId))
                return "背包里选中的物品不是大箱子（需要大箱子或大石箱）";
            string label = string.IsNullOrWhiteSpace(targetName) ? "该箱子" : targetName;
            if (targetFridge)
                return $"{label}是冰箱（特殊箱子），不能用大箱子原地升级（只能升级普通木箱或石箱）";
            if (!targetPlayerChest || targetGiftbox
                || targetSpecialType is Chest.SpecialChestTypes.MiniShippingBin
                    or Chest.SpecialChestTypes.JunimoChest
                    or Chest.SpecialChestTypes.AutoLoader
                    or Chest.SpecialChestTypes.Enricher)
                return $"{label}是特殊箱子，不能用大箱子原地升级（只能升级普通木箱或石箱）";
            if (targetSpecialType == Chest.SpecialChestTypes.BigChest || IsBigChestId(targetId))
                return $"{label}已经是大箱子（70 格），不需要再升级";
            if (targetId == WoodChestId)
                return bigChestId == BigWoodChestId
                    ? null
                    : "木箱和石箱类型配不上：目标是普通木箱，只能用大箱子升级，不能用大石箱";
            if (targetId == StoneChestId)
                return bigChestId == BigStoneChestId
                    ? null
                    : "木箱和石箱类型配不上：目标是石箱，只能用大石箱升级，不能用大箱子";
            return $"{label}是特殊箱子，不能用大箱子原地升级（只能升级普通木箱或石箱）";
        }

        internal static string FormatBigChestUpgradeHint(bool hasBigWood, bool hasBigStone, int woodChests, int stoneChests)
        {
            bool canWood = hasBigWood && woodChests > 0;
            bool canStone = hasBigStone && stoneChests > 0;
            if (!canWood && !canStone)
                return null;
            string what = canWood && canStone ? "大箱子/大石箱" : canWood ? "大箱子" : "大石箱";
            string target = canWood && canStone ? "对应的普通箱子/石箱" : canWood ? "普通箱子" : "石箱";
            return $"提示：背包里有{what}，可用 place_item 传已有{target}的 x,y 原地升级（36→70 格，东西和颜色自动保留不用搬），也可另放空位。";
        }

        internal static string BigChestUpgradeHint()
        {
            bool hasBigWood = false, hasBigStone = false;
            foreach (var item in Player.Items)
            {
                if (item == null) continue;
                if (item.QualifiedItemId == BigWoodChestId) hasBigWood = true;
                else if (item.QualifiedItemId == BigStoneChestId) hasBigStone = true;
            }
            if (!hasBigWood && !hasBigStone)
                return null;
            int woodChests = 0, stoneChests = 0;
            foreach (var (loc, _, chest) in AllChests())
            {
                if (chest.fridge.Value || chest == loc.GetFridge() || !chest.playerChest.Value || chest.giftbox.Value || chest.SpecialChestType != Chest.SpecialChestTypes.None)
                    continue;
                if (chest.QualifiedItemId == WoodChestId) woodChests++;
                else if (chest.QualifiedItemId == StoneChestId) stoneChests++;
            }
            return FormatBigChestUpgradeHint(hasBigWood, hasBigStone, woodChests, stoneChests);
        }

        /// <summary>
        /// 用大箱子原地升级指定格子的普通箱子（走过去、调用原版 performObjectDropInAction 替换、扣掉手上的大箱子）。
        /// </summary>
        private IEnumerable<object> UpgradeChestAt(int itemIdx, int x, int y)
        {
            var loc = Loc;
            var target = new Point(x, y);
            var v = new Vector2(x, y);
            var selected = Player.Items[itemIdx];

            // 先检查冰箱、收纳家具（走过去之前直接拒，不白跑）
            var fridge = loc.GetFridge();
            if (fridge != null && loc.GetFridgePosition() is Point fp && (fp == target || (fp.X == x && fp.Y - 1 == y)))
            {
                yield return ActionResult.Fail($"({x},{y}) 是冰箱（特殊箱子），不能用大箱子原地升级（只能升级普通木箱或石箱）");
                yield break;
            }
            var storageFurn = loc.furniture.FirstOrDefault(f => f is StorageFurniture && f.GetBoundingBox().Contains(x * 64 + 32, y * 64 + 32));
            if (storageFurn != null)
            {
                yield return ActionResult.Fail($"({x},{y}) 是{storageFurn.DisplayName}（收纳箱/家具，特殊容器），不能用大箱子原地升级（只能升级普通木箱或石箱）");
                yield break;
            }
            if (!loc.Objects.TryGetValue(v, out var obj) || obj is not Chest targetChest)
            {
                yield return ActionResult.Fail($"({x},{y}) 没有可升级的箱子");
                yield break;
            }
            if (ChestUpgradeProblem(selected.QualifiedItemId, targetChest.QualifiedItemId, targetChest.playerChest.Value,
                targetChest.giftbox.Value, targetChest.fridge.Value, targetChest.SpecialChestType, targetChest.DisplayName) is string problem)
            {
                yield return ActionResult.Fail($"不能升级 ({x},{y})：{problem}");
                yield break;
            }

            var o = new Outcome();
            foreach (var s in GoAdjacent(target, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }

            // 到场后再复核一次背包和目标箱子
            int curIdx = Player.Items.IndexOf(selected);
            if (curIdx < 0)
            {
                yield return ActionResult.Fail("到场后背包里没有大箱子了");
                yield break;
            }
            if (Loc != loc || !loc.Objects.TryGetValue(v, out var nowObj) || nowObj is not Chest nowChest)
            {
                yield return ActionResult.Fail($"到场后 ({x},{y}) 的箱子不见了");
                yield break;
            }
            if (ChestUpgradeProblem(selected.QualifiedItemId, nowChest.QualifiedItemId, nowChest.playerChest.Value,
                nowChest.giftbox.Value, nowChest.fridge.Value, nowChest.SpecialChestType, nowChest.DisplayName) is string problemNow)
            {
                yield return ActionResult.Fail($"到场后不能升级 ({x},{y})：{problemNow}");
                yield break;
            }

            string oldName = nowChest.DisplayName;
            Player.CurrentToolIndex = curIdx;
            var activeItem = Player.Items[curIdx];
            bool ok = nowChest.performObjectDropInAction(activeItem, probe: false, Player, returnFalseIfItemConsumed: true);
            if (!ok)
            {
                yield return ActionResult.Fail($"未能原地升级 ({x},{y}) 的{oldName}（可能正被占用或不支持替换），原箱子和里面的物品未改动");
                yield break;
            }
            Player.reduceActiveItemByOne();
            foreach (var s in WaitTicks(15))
                yield return s;

            if (loc.Objects.TryGetValue(v, out var afterObj) && afterObj is Chest afterChest)
            {
                int itemSlots = afterChest.GetItemsForPlayer().Count(i => i != null);
                yield return ActionResult.Success($"已将 ({x},{y}) 的{oldName}原地升级为{afterChest.DisplayName}（容量 {afterChest.GetActualCapacity()} 格，原有 {itemSlots} 格物品和颜色已保留，旧箱子已退回）");
            }
            else
            {
                yield return ActionResult.Success($"已将 ({x},{y}) 的{oldName}原地升级为{activeItem.DisplayName}");
            }
        }

        // ───────────────────────── 储藏区 ─────────────────────────

        /// <summary>同地图的箱子沿横竖和斜角两格以内相连，连成一片就算一组。</summary>
        internal static List<List<Point>> ClusterChestTiles(IEnumerable<Point> tiles)
            => MachineGroups(tiles.OrderBy(p => p.Y).ThenBy(p => p.X).ToList());

        /// <summary>统计每侧紧贴范围的合规格子，四个角以外的斜格不计。</summary>
        internal static Dictionary<string, List<Point>> StorageExpansionTiles(Rectangle bounds, Func<Point, bool> canPlace)
        {
            List<Point> Scan(int length, Func<int, Point> tile)
                => Enumerable.Range(0, length).Select(tile).Where(canPlace).ToList();
            return new Dictionary<string, List<Point>>
            {
                ["上"] = Scan(bounds.Width, i => new Point(bounds.Left + i, bounds.Top - 1)),
                ["下"] = Scan(bounds.Width, i => new Point(bounds.Left + i, bounds.Bottom)),
                ["左"] = Scan(bounds.Height, i => new Point(bounds.Left - 1, bounds.Top + i)),
                ["右"] = Scan(bounds.Height, i => new Point(bounds.Right, bounds.Top + i))
            };
        }

        private static bool IsStorageChest(Item item)
            => item is Chest chest
                ? chest.playerChest.Value && !chest.giftbox.Value
                    && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest or Chest.SpecialChestTypes.JunimoChest
                : item != null && ChestIds.Contains(item.QualifiedItemId);

        private static IEnumerable<IGrouping<GameLocation, (GameLocation loc, Point tile, Chest chest)>> StorageChests()
            => AllChests().Where(c => IsStorageChest(c.chest) && c.chest != c.loc.GetFridge()).GroupBy(c => c.loc);

        internal static string StorageSpreadHint()
        {
            var groups = StorageChests().SelectMany(g => ClusterChestTiles(g.Select(c => c.tile))).ToList();
            return groups.Count >= 2 && groups.Any(g => g.Count == 1)
                ? $"箱子分散在 {groups.Count} 处，可以集中放（用 storage_info 看能往哪边扩）"
                : null;
        }

        private static bool TryStorageAnchor(string near, out Point anchor)
        {
            var parts = near?.Split(new[] { ',', '，' });
            if (parts is { Length: 2 } && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
            {
                anchor = new Point(x, y);
                return true;
            }
            anchor = default;
            return false;
        }

        private static List<Point> StorageGroupAt(List<List<Point>> groups, Point anchor)
            => groups.FirstOrDefault(g => g.Contains(anchor)) ?? new List<Point> { anchor };

        private static Dictionary<string, List<Point>> StorageExpansion(List<Point> group, Func<Point, bool> canPlace)
            => StorageExpansionTiles(BoundingBox(group), tile => group.Any(p => Math.Max(Math.Abs(p.X - tile.X), Math.Abs(p.Y - tile.Y)) <= 2) && canPlace(tile));

        private static Point? StorageEntry(GameLocation loc)
        {
            if (loc == Loc)
                return Player.TilePoint;
            if (Exits.PlanRoute(Loc, loc.NameOrUniqueName) is not { Count: > 0 } route)
                return null;
            return ExitArrivalTile(loc, route[^1]);
        }

        private static Func<Point, bool> StoragePlacementCheck(GameLocation loc, Item item, Point start)
        {
            if (Utility.isPlacementForbiddenHere(loc))
                return _ => false;
            var exits = Exits.Get(loc).Select(e => e.Tile).ToHashSet();
            var cache = new Dictionary<Point, bool>();
            var reachable = ReachableTiles(loc, start, null, cache);
            return tile => reachable.Contains(tile) && tile != start
                && !exits.Any(exit => Util.Manhattan(exit, tile) <= 1)
                && !Pathfinder.IsTouchWarp(loc, tile.X, tile.Y)
                && Pathfinder.InteriorDoor(loc, tile) == null
                && !Pathfinder.Dirs.Any(d => Pathfinder.InteriorDoor(loc, tile + d) != null)
                && (!loc.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var feature) || feature is not HoeDirt)
                && PlaceableProblem(loc, item, tile) == null
                && (loc is not Farm farm || FarmPlacementProblem(farm, (SObject)item, tile) == null)
                && BlockageProblem(loc, tile, cache, reachable, start) == null
                && Pathfinder.FindPath(loc, start, Pathfinder.AdjacentStandTiles(loc, tile)
                    .Where(p => !Pathfinder.IsTouchWarp(loc, p.X, p.Y)).ToList()) != null;
        }

        public IEnumerable<object> StorageInfo(string location, string near)
        {
            GameLocation map = null;
            if (!string.IsNullOrWhiteSpace(location))
            {
                map = Game1.getLocationFromName(location.Trim());
                if (map == null)
                {
                    yield return ActionResult.Fail($"找不到地点“{location}”，用 get_state 或 list_chests 里的地点名");
                    yield break;
                }
            }
            Point anchor = default;
            bool hasAnchor = near != null;
            if (hasAnchor)
            {
                map ??= Loc;
                if (!TryStorageAnchor(near, out anchor) || !map.isTileOnMap(anchor.X, anchor.Y))
                {
                    yield return ActionResult.Fail("storage_info 的 near 要写成该地图上的格子坐标，例如 \"60,15\"");
                    yield break;
                }
            }

            var maps = StorageChests().Where(g => map == null || g.Key == map)
                .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.tile, c => c.chest));
            if (hasAnchor && !maps.ContainsKey(map))
                maps.Add(map, new Dictionary<Point, Chest>());
            if (maps.Count == 0)
            {
                yield return ActionResult.Success("还没有储藏箱。选好地图和锚点后，用 storage_info near=\"x,y\" 查看周围能放的格子");
                yield break;
            }

            var probe = ItemRegistry.Create<SObject>("(BC)130");
            var lines = new List<string>();
            foreach (var (loc, chests) in maps)
            {
                if (loc.Map?.Layers.Count is not > 0)
                {
                    lines.Add($"{loc.NameOrUniqueName}：地图还没加载，到了当地再查");
                    continue;
                }
                var groups = ClusterChestTiles(chests.Keys);
                if (hasAnchor)
                    groups = new List<List<Point>> { StorageGroupAt(groups, anchor) };
                var entry = StorageEntry(loc);
                var canPlace = entry is Point start && loc.isTileOnMap(start.X, start.Y) ? StoragePlacementCheck(loc, probe, start) : null;
                foreach (var group in groups)
                {
                    var box = BoundingBox(group);
                    var members = group.Where(chests.ContainsKey).Select(p => chests[p]).ToList();
                    // 祝尼魔箱子等可能共用同一份格子。组内共用的容量只算一次。
                    var inventories = members.Select(c => (items: c.GetItemsForPlayer(), capacity: c.GetActualCapacity()))
                        .DistinctBy(c => c.items, ReferenceEqualityComparer.Instance).ToList();
                    int capacity = inventories.Sum(c => c.capacity);
                    int free = inventories.Sum(c => Math.Max(0, c.capacity - c.items.Count(i => i != null)));
                    var first = group[0];
                    string range = $"({box.Left},{box.Top})-({box.Right - 1},{box.Bottom - 1})";
                    string chestSummary = FormatStorageChestSummary(members.Select(c => ChestTypeLabel(c)));
                    string head = $"{loc.NameOrUniqueName} {range}，near=\"{first.X},{first.Y}\"，{members.Count} 个箱子{chestSummary}，空 {free}/{capacity} 格";
                    if (canPlace == null)
                    {
                        lines.Add(head + "。算不出进图位置，到了当地再查可扩空位");
                        continue;
                    }
                    var sides = StorageExpansion(group, canPlace);
                    lines.Add(head + "。可扩 " + string.Join("；", sides.Select(side => $"{side.Key} {side.Value.Count} 格"
                        + (side.Value.Count > 0 ? $"（{string.Join("、", side.Value.Select(p => $"({p.X},{p.Y})"))}）" : ""))));
                }
            }
            yield return ActionResult.Success(string.Join("\n", lines) + "\n空位按一次放一个箱子计算，放完可再查。到目标地图后，用 place_item near=\"x,y\" 扩这一组，也可以自行选坐标。");
        }

        private IEnumerable<object> PlaceChestNear(string itemSpec, string near, bool explicitTile)
        {
            if (explicitTile || !TryStorageAnchor(near, out var anchor) || !Loc.isTileOnMap(anchor.X, anchor.Y))
            {
                yield return ActionResult.Fail("place_item 的 near 要写当前地图上的锚点坐标，例如 \"60,15\"，和直接放置的 x,y 二选一");
                yield break;
            }
            int index = Util.FindItemIndex(itemSpec, IsStorageChest);
            if (index < 0)
            {
                yield return ActionResult.Fail($"背包里没有可用 near 摆放的箱子“{itemSpec}”");
                yield break;
            }
            var groups = ClusterChestTiles(PlayerChests(Loc).Where(c => IsStorageChest(c.chest)).Select(c => c.tile));
            var canPlace = StoragePlacementCheck(Loc, Player.Items[index], Player.TilePoint);
            var spot = StorageExpansion(StorageGroupAt(groups, anchor), canPlace).Values.SelectMany(tiles => tiles)
                .OrderBy(p => Util.Manhattan(p, Player.TilePoint)).Select(p => (Point?)p).FirstOrDefault();
            if (spot is not Point tile)
            {
                yield return ActionResult.Fail($"锚点 ({anchor.X},{anchor.Y}) 这一片周围没有能放箱子的空位，用 storage_info 查位置或换个锚点");
                yield break;
            }
            foreach (var s in PlaceItem(index.ToString(), tile.X, tile.Y, nearStorage: true))
                yield return s;
        }

        // ───────────────────────── 箱子存取 ─────────────────────────

        /// <summary>
        /// 当前地点里玩家放的箱子：给了坐标就用那一格，否则用最近的一个。
        /// 开局礼物箱（giftbox）不算，它打开就消失。
        /// </summary>
        private static Chest FindChest(int? x, int? y, out Point tile, out string error)
        {
            tile = default;
            error = null;
            // 地上的箱子 + 农舍/姜岛小屋的冰箱（冰箱不在 objects 里，要单独加）
            var chests = PlayerChests(Loc).ToList();
            var fridge = Loc.GetFridge();
            if (fridge != null && Loc.GetFridgePosition() is Point fp)
                chests.Add((fp, fridge));
            if (x.HasValue && y.HasValue)
            {
                var want = tile = new Point(x.Value, y.Value);
                // 冰箱占两格高，点上面那格也算
                var hit = chests.FirstOrDefault(c => c.tile == want ||c.chest == fridge && c.tile.X == x.Value && c.tile.Y - 1 == y.Value);
                if (hit.chest != null)
                {
                    tile = hit.tile;
                    return hit.chest;
                }
                // 说清楚那格其实是什么、这里的箱子都在哪，AI 一步就能改对
                string here = WhatIsAt(tile);
                var list = chests.Select(c => $"({c.tile.X},{c.tile.Y}){(c.chest == fridge ? "冰箱" : "")}").ToList();
                error = $"({x},{y}) 不是箱子，" + (here != null ? $"那里是{here}" : "那里什么都没有")
                    + (list.Count > 0 ? $"。{Loc.NameOrUniqueName} 的箱子在：{string.Join("、", list.Take(8))}（不传坐标就用最近的一个）"
                        : $"。{Loc.NameOrUniqueName} 里没有箱子（箱子的坐标只在放它的那张地图上有效，先 travel_to 过去）");
                return null;
            }
            var me = Player.TilePoint;
            var nearest = chests.OrderBy(c => Math.Abs(c.tile.X - me.X) + Math.Abs(c.tile.Y - me.Y)).FirstOrDefault();
            if (nearest.chest == null)
            {
                error = "这里没有箱子。先 craft Chest（50 木材），再 place_item 放在农场上（比如出货箱旁边）";
                return null;
            }
            tile = nearest.tile;
            return nearest.chest;
        }

        /// <summary>items 为空或 "all" 表示全部；否则用逗号/顿号分隔的名字（包含匹配）。</summary>
        private static Func<Item, bool> ItemFilter(string items)
        {
            if (string.IsNullOrWhiteSpace(items) || items.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || items.Trim() == "全部")
                return _ => true;
            var names = items.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Util.StripQuality).ToArray();
            return i => names.Any(n => i.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 拆出“原名、匹配函数、要几个”：逗号/顿号分隔，每项可以带数量（铁锭x5、铁锭×5、铁锭*5、铁锭:5、铁锭 5）。
        /// 不写数量就是全拿/全存；空或 all 是全部。
        /// </summary>
        private static List<(string rawName, Func<Item, bool> filter, int? count)> ItemSpecs(string items)
        {
            var list = new List<(string, Func<Item, bool>, int?)>();
            if (string.IsNullOrWhiteSpace(items) || items.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || items.Trim() == "全部")
            {
                list.Add((null, _ => true, null));
                return list;
            }
            foreach (var part in items.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string name = part;
                int? count = null;
                var m = System.Text.RegularExpressions.Regex.Match(part, @"^(.+?)\s*(?:[xX×*:：]\s*|\s+)(\d+)\s*个?$");
                if (m.Success && int.TryParse(m.Groups[2].Value, out int c) && c > 0)
                {
                    name = m.Groups[1].Value;
                    count = c;
                }
                string raw = name.Trim();
                string clean = Util.StripQuality(raw);
                list.Add((raw, i => i.DisplayName.Contains(clean, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(clean, StringComparison.OrdinalIgnoreCase), count));
            }
            return list;
        }

        private static List<(Func<Item, bool> filter, int? count)> ItemCounts(string items) =>
            ItemSpecs(items).Select(s => (s.filter, s.count)).ToList();

        private static bool IsAllItemsSpec(string items) =>
            string.IsNullOrWhiteSpace(items) || items.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || items.Trim() == "全部";

        /// <summary>纯数据版数量分配：按 specs 计算背包每格最多存多少，供离线测试和 StoreItems 共用。</summary>
        internal static Dictionary<int, int> PlanStoreCounts(string items, IReadOnlyList<(string name, string displayName, bool isTool, int stack)> inventory)
        {
            var planned = new Dictionary<int, int>();
            if (IsAllItemsSpec(items))
            {
                for (int idx = 0; idx < inventory.Count; idx++)
                    if (inventory[idx].stack > 0 && !inventory[idx].isTool)
                        planned[idx] = inventory[idx].stack;
                return planned;
            }
            foreach (var part in items.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string name = part;
                int? want = null;
                var m = System.Text.RegularExpressions.Regex.Match(part, @"^(.+?)\s*(?:[xX×*:：]\s*|\s+)(\d+)\s*个?$");
                if (m.Success && int.TryParse(m.Groups[2].Value, out int c) && c > 0)
                {
                    name = m.Groups[1].Value;
                    want = c;
                }
                string raw = name.Trim();
                string clean = Util.StripQuality(raw);
                int left = want ?? int.MaxValue;
                for (int idx = 0; idx < inventory.Count; idx++)
                {
                    if (left <= 0)
                        break;
                    var slot = inventory[idx];
                    if (slot.stack <= 0)
                        continue;
                    bool hit = slot.isTool
                        ? (raw.Equals(slot.displayName, StringComparison.OrdinalIgnoreCase) || raw.Equals(slot.name, StringComparison.OrdinalIgnoreCase))
                        : ((slot.displayName ?? "").Contains(clean, StringComparison.OrdinalIgnoreCase)
                           || (slot.name ?? "").Contains(clean, StringComparison.OrdinalIgnoreCase));
                    if (!hit)
                        continue;
                    int used = planned.GetValueOrDefault(idx, 0);
                    int avail = slot.stack - used;
                    if (avail <= 0)
                        continue;
                    int take = Math.Min(left, avail);
                    planned[idx] = used + take;
                    left -= take;
                }
            }
            return planned;
        }

        private static (List<Item> toStore, Dictionary<Item, int> maxCounts) PlanStoreItems(string items)
        {
            var inv = Player.Items.Select(i => i == null
                ? (name: "", displayName: "", isTool: false, stack: 0)
                : (name: i.Name ?? "", displayName: i.DisplayName ?? "", isTool: i is Tool, stack: i.Stack)).ToList();
            var counts = PlanStoreCounts(items, inv);
            var toStore = new List<Item>();
            var maxCounts = new Dictionary<Item, int>();
            foreach (var (slot, qty) in counts.OrderBy(kv => kv.Key))
            {
                var item = Player.Items[slot];
                if (item != null && qty > 0)
                {
                    toStore.Add(item);
                    maxCounts[item] = qty;
                }
            }
            return (toStore, maxCounts);
        }

        private static string ChestSummary(Chest chest)
        {
            var items = chest.GetItemsForPlayer().Where(i => i != null).ToList();
            return items.Count == 0
                ? "箱子是空的"
                : $"箱子里（{items.Count}/{chest.GetActualCapacity()} 格）：{string.Join("、", items.Select(i => $"{i.DisplayName}x{i.Stack}"))}";
        }

        /// <summary>把背包里的东西存进箱子（工具和武器只存点了全名的，比如“训练用鱼竿”）。和箱子界面里点背包物品一样：chest.addItem，全放进去才从背包移除。</summary>
        public IEnumerable<object> StoreItems(string items, int? x, int? y)
        {
            // 没指定箱子：同样的东西存到已经放着它的箱子里，不是一律塞进最近的那个
            if (!x.HasValue || !y.HasValue)
            {
                foreach (var s in StoreItemsSorted(items))
                    yield return s;
                yield break;
            }
            var chest = FindChest(x, y, out var tile, out string error);
            if (chest == null)
            {
                yield return ActionResult.Fail(error);
                yield break;
            }
            var o = new Outcome();
            foreach (var s in GoAdjacent(tile, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            var (toStore, maxCounts) = PlanStoreItems(items);
            var stored = new List<string>();
            bool full = false;
            foreach (var item in toStore)
            {
                int want = maxCounts[item];
                string name = item.DisplayName;
                int moved = PutItemIntoChest(chest, item, want, out bool chestFull);
                if (chestFull)
                    full = true;
                if (moved > 0)
                {
                    stored.Add($"{name}x{moved}");
                    maxCounts[item] = Math.Max(0, want - moved);
                }
            }
            chest.clearNulls();
            if (stored.Count == 0)
            {
                yield return ActionResult.Fail((full ? "箱子满了，" : "背包里没有符合的东西可存（工具要写全名才存），") + ChestSummary(chest));
                yield break;
            }
            var notStored = toStore.Where(i => maxCounts.GetValueOrDefault(i) > 0 && Player.Items.Contains(i))
                .Select(i => $"{i.DisplayName}x{maxCounts[i]}").ToList();
            yield return ActionResult.Success($"存进 ({tile.X},{tile.Y}) 的箱子：{string.Join("、", stored)}{(notStored.Count > 0 ? $"；箱子满了，没放进去的还在背包里：{string.Join("、", notStored)}" : "")}。{ChestSummary(chest)}");
        }

        /// <summary>
        /// 不指定箱子的 store_items：每样东西放进已经有它的箱子（能叠就叠），没有就放进装着同一类东西的箱子，再没有才放最近的有空位的箱子。
        /// </summary>
        private IEnumerable<object> StoreItemsSorted(string items)
        {
            if (!PlayerChests(Loc).Any())
            {
                yield return ActionResult.Fail("这里没有箱子。先 craft Chest（50 木材），再 place_item 放在农场上（比如出货箱旁边）；箱子在别的地方就先 travel_to 过去");
                yield break;
            }
            var (toStore, maxCounts) = PlanStoreItems(items);
            if (toStore.Count == 0)
            {
                yield return ActionResult.Fail("背包里没有符合的东西可存（工具要写全名才存）");
                yield break;
            }
            var stored = new List<string>();
            foreach (var s in StoreIntoChests(toStore, stored, maxCounts: maxCounts))
                yield return s;
            var remaining = toStore.Where(i => Player.Items.Contains(i) && maxCounts.GetValueOrDefault(i) > 0).ToList();
            var left = remaining.Select(i => i.DisplayName).Distinct().ToList();
            string rest = left.Count > 0 ? $"；{string.Join("、", left)} 还在背包里：" + ChestRoomNote(Loc, remaining) : "";
            yield return stored.Count > 0
                ? ActionResult.Success($"存进了 {string.Join("；", stored)}{rest}。背包空位 {Player.freeSpotsInInventory()} 个")
                : ActionResult.Fail("一样也没存进去" + rest);
        }

        /// <summary>从箱子取东西回背包。和箱子界面里点箱子物品一样：先确认背包装得下再从箱子移除。</summary>
        public IEnumerable<object> TakeItems(string items, int? x, int? y, int? count = null)
        {
            // 别的工具（buy、sell、ship_items）数量都叫 count，这里也认：只取一样东西时换成“干草x3”的写法
            if (count.HasValue)
            {
                var parts = (items ?? "").Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool all = parts.Length == 0 || parts.Length == 1 && (parts[0].Equals("all", StringComparison.OrdinalIgnoreCase) || parts[0] == "全部");
                if (count <= 0 || all || parts.Length > 1)
                {
                    yield return ActionResult.Fail(count <= 0 ? "count 要大于 0"
                        : all ? "传了 count 就要在 items 里写要取什么（比如 items=\"干草\" count=3）"
                        : "count 只能配一样东西。取好几样时把数量写在 items 里，比如“铁锭x5，煤炭x10”");
                    yield break;
                }
                if (ItemCounts(parts[0])[0].count == null)
                    items = $"{parts[0]}x{count}";
            }
            // 要的东西不在这张地图的箱子里（或者给的坐标是别的地图上的箱子）：在所有地点的箱子里找，找到就自己走过去拿
            bool specific = !string.IsNullOrWhiteSpace(items) && !items.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) && items.Trim() != "全部";
            if (specific)
            {
                var wanted = ItemCounts(items);
                bool Has(Chest c) => c.GetItemsForPlayer().Any(i => i != null && wanted.Any(w => w.filter(i)));
                var here = FindChest(x, y, out _, out _);
                if (here == null || !Has(here) && !x.HasValue)
                {
                    var all = AllChests().Where(c => c.loc.objects.TryGetValue(new Vector2(c.tile.X, c.tile.Y), out var o) && o == c.chest || c.chest == c.loc.GetFridge()).ToList();
                    var pick = all
                        .Where(c => (!x.HasValue || (c.tile.X == x && c.tile.Y == y)) && Has(c.chest))
                        .OrderBy(c => c.loc == Loc ? 0 : 1)
                        .FirstOrDefault();
                    if (pick.chest != null && pick.chest != here)
                    {
                        if (pick.loc != Loc)
                        {
                            var go = new Outcome();
                            foreach (var s in TravelInner(pick.loc.NameOrUniqueName, go))
                                yield return s;
                            if (!go.Ok)
                            {
                                yield return ActionResult.Fail($"要的东西在 {pick.loc.NameOrUniqueName} ({pick.tile.X},{pick.tile.Y}) 的箱子里，但没走过去：{go.Message}");
                                yield break;
                            }
                        }
                        x = pick.tile.X;
                        y = pick.tile.Y;
                    }
                    else if (here == null || !Has(here))
                    {
                        yield return ActionResult.Fail($"所有地点的箱子里都没有“{items}”" + (here != null ? "。" + ChestSummary(here) : ""));
                        yield break;
                    }
                }
            }
            var chest = FindChest(x, y, out var tile, out string error);
            if (chest == null)
            {
                yield return ActionResult.Fail(error);
                yield break;
            }
            var o = new Outcome();
            foreach (var s in GoAdjacent(tile, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            var taken = new List<string>();
            bool bagFull = false;
            // 每一项：名字 + 要几个（没写数量就是整摞全拿）
            foreach (var (filter, want) in ItemCounts(items))
            {
                int left = want ?? int.MaxValue;
                foreach (var item in chest.GetItemsForPlayer().Where(i => i != null && filter(i)).ToList())
                {
                    if (left <= 0)
                        break;
                    int take = Math.Min(left, item.Stack);
                    // 只拿一部分：和箱子界面里右键拆堆一样，拆一份出来，剩下的留在箱子里
                    var piece = take < item.Stack ? item.getOne() : item;
                    if (piece != item)
                        piece.Stack = take;
                    if (!Player.couldInventoryAcceptThisItem(piece))
                    {
                        bagFull = true;
                        continue;
                    }
                    if (piece == item)
                        chest.GetItemsForPlayer().Remove(item);
                    else
                        item.Stack -= take;
                    var rest = Player.addItemToInventory(piece);
                    int got = take - (rest?.Stack ?? 0);
                    if (rest != null)
                    {
                        if (piece == item)
                            chest.addItem(rest);
                        else
                            item.Stack += rest.Stack;
                        bagFull = true;
                    }
                    if (got > 0)
                        taken.Add($"{item.DisplayName}x{got}");
                    left -= got;
                }
                if (want.HasValue && left > 0 && left < want.Value)
                    taken[^1] += $"（要 {want} 个，箱子里只够这些）";
            }
            chest.clearNulls();
            if (taken.Count == 0)
            {
                yield return ActionResult.Fail((bagFull ? "背包满了，" : "箱子里没有符合的东西，") + ChestSummary(chest));
                yield break;
            }
            yield return ActionResult.Success($"从箱子取出：{string.Join("、", taken)}{(bagFull ? "；背包满了，还有的没取" : "")}。{ChestSummary(chest)}");
        }
    }
}
