using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
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
        /// 拆出“要什么、要几个”：逗号/顿号分隔，每项可以带数量（铁锭x5、铁锭×5、铁锭*5、铁锭:5、铁锭 5）。
        /// 不写数量就是全拿；空或 all 是全部。
        /// </summary>
        private static List<(Func<Item, bool> filter, int? count)> ItemCounts(string items)
        {
            var list = new List<(Func<Item, bool>, int?)>();
            if (string.IsNullOrWhiteSpace(items) || items.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || items.Trim() == "全部")
            {
                list.Add((_ => true, null));
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
                name = Util.StripQuality(name);
                list.Add((i => i.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(name, StringComparison.OrdinalIgnoreCase), count));
            }
            return list;
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
            var filter = ItemFilter(items);
            // 工具和武器只存点了全名的（“剑”这种模糊的名字会把手上最好的武器也存掉）
            var toolNames = string.IsNullOrWhiteSpace(items) ? new HashSet<string>()
                : items.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Wanted(Item i) => i is Tool ? toolNames.Contains(i.DisplayName) || toolNames.Contains(i.Name) : filter(i);
            var stored = new List<string>();
            bool full = false;
            foreach (var item in Player.Items.Where(i => i != null && Wanted(i)).ToList())
            {
                int before = item.Stack;
                string name = item.DisplayName;
                var left = chest.addItem(item);
                if (left == null)
                {
                    Player.removeItemFromInventory(item);
                    stored.Add($"{name}x{before}");
                }
                else
                {
                    full = true;
                    if (left.Stack < before)
                        stored.Add($"{name}x{before - left.Stack}");
                }
            }
            chest.clearNulls();
            if (stored.Count == 0)
            {
                yield return ActionResult.Fail((full ? "箱子满了，" : "背包里没有符合的东西可存（工具要写全名才存），") + ChestSummary(chest));
                yield break;
            }
            yield return ActionResult.Success($"存进 ({tile.X},{tile.Y}) 的箱子：{string.Join("、", stored)}{(full ? $"；箱子满了，没放进去的还在背包里：{string.Join("、", Player.Items.Where(i => i != null && Wanted(i)).Select(i => $"{i.DisplayName}x{i.Stack}"))}" : "")}。{ChestSummary(chest)}");
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
            var filter = ItemFilter(items);
            // 工具和武器只存点了全名的（“剑”这种模糊的名字会把手上最好的武器也存掉）
            var toolNames = string.IsNullOrWhiteSpace(items) ? new HashSet<string>()
                : items.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Wanted(Item i) => i is Tool ? toolNames.Contains(i.DisplayName) || toolNames.Contains(i.Name) : filter(i);
            var toStore = Player.Items.Where(i => i != null && Wanted(i)).ToList();
            if (toStore.Count == 0)
            {
                yield return ActionResult.Fail("背包里没有符合的东西可存（工具要写全名才存）");
                yield break;
            }
            var stored = new List<string>();
            foreach (var s in StoreIntoChests(toStore, stored))
                yield return s;
            var left = toStore.Where(i => Player.Items.Contains(i)).Select(i => i.DisplayName).Distinct().ToList();
            string rest = left.Count > 0 ? $"；{string.Join("、", left)} 还在背包里：" + ChestRoomNote(Loc, toStore.Where(i => Player.Items.Contains(i)).ToList()) : "";
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
