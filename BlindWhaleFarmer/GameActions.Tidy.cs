using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 整理背包 ─────────────────────────

        /// <summary>出门会用到的工具：按同类分组，分数高的优先留在身上。</summary>
        private static (string kind, int score) TripTool(Item i) => i switch
        {
            FishingRod rod => ("鱼竿", RodScore(rod)),
            // 镰刀的品质由物品 ID 区分，升级等级一样。
            MeleeWeapon w when w.isScythe() => ("镰刀", w.ItemId switch
            {
                MeleeWeapon.iridiumScytheID => 2,
                MeleeWeapon.goldenScytheId => 1,
                _ => 0
            }),
            MeleeWeapon w => ("近战武器", w.minDamage.Value + w.maxDamage.Value),
            Slingshot sling => ("弹弓", sling.ItemId switch
            {
                Slingshot.galaxySlingshotId => 4,
                Slingshot.masterSlingshotId => 2,
                _ => 1
            }),
            Pickaxe t => ("镐子", t.UpgradeLevel),
            Axe t => ("斧头", t.UpgradeLevel),
            Hoe t => ("锄头", t.UpgradeLevel),
            WateringCan t => ("水壶", t.UpgradeLevel),
            _ => (null, 0)
        };

        /// <summary>
        /// 整理背包时每类工具只留最好的一把，其他东西按今天要做的事筛选。
        /// 没写目的就按通用规则留吃的、种子、肥料、鱼饵钓具、炸弹、任务物品和要摆放的机器。
        /// 点名要留的也留在身上。
        /// </summary>
        private static HashSet<Item> ItemsToKeep(Func<Item, bool> keepAlso, IReadOnlyList<string> purposes = null)
            => SelectItemsToKeep(Player.Items, keepAlso, purposes ?? Array.Empty<string>(), purposes?.Count > 0 ? ReservedChecker() : null);

        private static HashSet<Item> SelectItemsToKeep(IEnumerable<Item> inventory, Func<Item, bool> keepAlso,
            IReadOnlyList<string> purposes, Func<Item, string> reserved)
        {
            var items = inventory.Where(i => i != null).ToList();
            var keep = items.Where(i => purposes.Count > 0 || i is not Slingshot)
                .Select(i => (item: i, tool: TripTool(i)))
                .Where(x => x.tool.kind != null)
                .GroupBy(x => x.tool.kind)
                .Select(g => g.OrderByDescending(x => x.tool.score).First().item)
                .ToHashSet();
            void BestOf<T>(Func<T, int> score) where T : Item
            {
                var best = items.OfType<T>().OrderByDescending(score).FirstOrDefault();
                if (best != null)
                    keep.Add(best);
            }
            BestOf<Pan>(t => t.UpgradeLevel);
            BestOf<MilkPail>(_ => 0);
            BestOf<Shears>(_ => 0);
            foreach (var i in items)
            {
                if (i is Tool)
                    continue;
                bool usefulOnHand = i is SObject o && (
                    o.Category is SObject.CookingCategory or SObject.SeedsCategory or SObject.fertilizerCategory
                        or SObject.baitCategory or SObject.tackleCategory
                    || o.bigCraftable.Value                    // 熔炉、洒水器以外的机器、箱子：要摆的
                    || o.IsSprinkler()
                    || o.Name.Contains("Bomb")
                    || o.questItem.Value
                    || o.QualifiedItemId is "(O)458" or "(O)460");   // 花束和美人鱼吊坠：买来就是要送的
                if (usefulOnHand || i is Ring or Boots || purposes.Any(p => UsefulFor(p, i, includeHealingFood: false)))
                    keep.Add(i);
            }
            keep.UnionWith(items.Where(keepAlso));
            // 没带够回血的就下矿，打到一半只能撤退
            bool Heals(Item i) => i is SObject f && f.Edibility > 0 && f.healthRecoveredOnConsumption() > 0;
            var extraFood = items.Where(i => Heals(i) && !keep.Contains(i))
                .OrderBy(i => ((SObject)i).Category == SObject.FishCategory ? 1 : 0)
                .ThenByDescending(i => ((SObject)i).healthRecoveredOnConsumption())
                .Take(Math.Max(0, 2 - keep.Count(Heals)));
            keep.UnionWith(extraFood.ToList());

            // 给了出门目的：任一件事用得上的、任务要交的和点名要留的都留着。
            if (purposes.Count > 0)
            {
                keep.RemoveWhere(i => !purposes.Any(p => UsefulFor(p, i)) && !keepAlso(i)
                    && !(i is SObject q && (q.questItem.Value || reserved(i)?.StartsWith("任务") == true || reserved(i)?.StartsWith("特别订单") == true)));
                if (purposes.Contains("mine"))
                {
                    keep.UnionWith(items.Where(IsEscapeItem));
                    // 下矿多带点回血的：一路打下去两样不够吃
                    keep.UnionWith(items.Where(i => Heals(i) && reserved(i) == null)
                        .OrderBy(i => ((SObject)i).sellToStorePrice() / Math.Max(1, ((SObject)i).healthRecoveredOnConsumption()))
                        .Take(Math.Max(0, 5 - keep.Count(Heals))).ToList());
                }
            }
            return keep;
        }

        /// <summary>提醒这趟用不上、已经存进箱子且身上没有更好同类的工具。</summary>
        private static IEnumerable<Item> ToolsAway(IEnumerable<Item> stored, IEnumerable<Item> inventory, IReadOnlyList<string> purposes)
        {
            if (purposes.Count == 0)
                yield break;
            var carried = inventory.Select(TripTool).Where(t => t.kind != null).ToLookup(t => t.kind);
            foreach (var item in stored)
            {
                var tool = TripTool(item);
                if (tool.kind != null && !purposes.Any(p => UsefulFor(p, item))
                    && !carried[tool.kind].Any(t => t.score > tool.score))
                    yield return item;
            }
        }

        /// <summary>
        /// 整理背包：把用不上的东西存进农场（或当前地点）的箱子，一个箱子满了换下一个。
        /// 低级的鱼竿、多余的武器等重复工具也存起来，每类只留最好的一把。
        /// </summary>
        public IEnumerable<object> TidyBackpack(string keep, string purposeSpec = null)
        {
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            if (!TryParsePurposes(purposeSpec, out var purposes, out string error))
            {
                yield return ActionResult.Fail(error);
                yield break;
            }
            var keepFilter = string.IsNullOrWhiteSpace(keep) ? (_ => false) : ItemFilter(keep);
            var keepSet = ItemsToKeep(keepFilter, purposes);
            var toStore = Player.Items.Where(i => i != null && !keepSet.Contains(i)).ToList();
            // 这次要用、但现在在箱子里的东西（比如下矿要的炸弹、回血料理），整理完顺手提醒
            string missing = MissingForTrips(purposes);
            string prefix = purposes.Count > 0 ? $"按{TripPurposeName(purposes)}整理。" : "";
            if (toStore.Count == 0)
            {
                yield return ActionResult.Success(prefix + "背包已经很整齐了，没有需要存起来的东西" + (missing != null ? "。" + missing : ""));
                yield break;
            }

            // 这里没有箱子就回农场
            var o = new Outcome();
            if (!PlayerChests(Loc).Any())
            {
                foreach (var s in TravelInner("Farm", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("回农场失败：" + o.Message);
                    yield break;
                }
            }
            var chests = PlayerChests(Loc).OrderBy(c => Util.Manhattan(c.tile, Player.TilePoint)).ToList();
            if (chests.Count == 0)
            {
                yield return ActionResult.Fail("农场上没有箱子。先 craft Chest（50 木材），再 place_item 放在农场上");
                yield break;
            }

            var stored = new List<string>();
            var storedTools = new List<Item>();
            foreach (var s in StoreIntoChests(toStore, stored, storedTools))
                yield return s;

            var leftover = toStore.Where(i => Player.Items.Contains(i)).Select(i => i.DisplayName).ToList();
            var kept = Player.Items.Where(i => i != null).Select(i => i.DisplayName).ToList();
            string msg = stored.Count > 0 ? $"存起来了：{string.Join("；", stored)}" : "一样也没存进去";
            msg += $"。身上留着：{string.Join("、", kept)}（空位 {Player.freeSpotsInInventory()} 个）";
            if (leftover.Count > 0)
                msg += $"。还剩 {string.Join("、", leftover)} 没放下：" + ChestRoomNote(Loc, toStore.Where(i => Player.Items.Contains(i)).ToList());
            msg = prefix + msg;
            var toolsAway = ToolsAway(storedTools, Player.Items, purposes).Select(i => i.DisplayName).Distinct().ToList();
            if (toolsAway.Count > 0)
                msg += $"。注意：{string.Join("、", toolsAway)} 存进箱子了，今天还要用的话先 take_items 拿回来，或者重新 tidy_backpack，把今天要干的事都写进 purpose，要带的写进 keep";
            if (missing != null)
                msg += "。" + missing;
            yield return stored.Count > 0 ? ActionResult.Success(msg) : ActionResult.Fail(msg);
        }

        /// <summary>
        /// 把 toStore 里的东西存进当前地点的箱子，存进去的按箱子记在 stored 里（“(x,y)：铁矿石x6、铜矿石x4”）。
        /// 同样的东西归到一起：先放进已经有这种东西的箱子（能叠就叠），再放进装着同一类东西的箱子，都没有才放进最近的有空位的箱子。
        /// 按计划一个箱子一个箱子走过去放；计划的箱子放不下了（比如格子算错），剩下的再按远近找有空位的箱子。走不到的箱子跳过。
        /// </summary>
        private IEnumerable<object> StoreIntoChests(List<Item> toStore, List<string> stored, List<Item> storedTools = null, Dictionary<Item, int> maxCounts = null)
        {
            var o = new Outcome();
            var chests = PlayerChests(Loc).ToList();
            var plan = PlanChests(toStore.Where(i => Player.Items.Contains(i)).ToList(), chests);
            var perChest = new Dictionary<Chest, List<string>>();
            var tiles = chests.ToDictionary(c => c.chest, c => c.tile);

            // 第一轮按计划放：从近到远，走到一个箱子就把分给它的东西都放进去
            var order = plan.Keys.ToList();
            var at = Player.TilePoint;
            while (order.Count > 0)
            {
                var next = order.OrderBy(c => Util.Manhattan(tiles[c], at)).First();
                order.Remove(next);
                var items = plan[next].Where(i => Player.Items.Contains(i) && (maxCounts == null || !maxCounts.TryGetValue(i, out int rem) || rem > 0)).ToList();
                if (items.Count == 0)
                    continue;
                foreach (var s in GoAdjacent(tiles[next], o))
                    yield return s;
                if (!o.Ok)
                {
                    o.Succeed();
                    continue;
                }
                at = Player.TilePoint;
                PutInto(next, items, perChest, storedTools, maxCounts);
            }

            // 第二轮：计划里的箱子没放下的，按远近找还有空位的箱子
            foreach (var (tile, chest) in chests.OrderBy(c => Util.Manhattan(c.tile, Player.TilePoint)).ToList())
            {
                var pending = toStore.Where(i => Player.Items.Contains(i) && (maxCounts == null || !maxCounts.TryGetValue(i, out int rem) || rem > 0)).ToList();
                if (pending.Count == 0)
                    break;
                if (!pending.Any(p => ChestHasRoomFor(chest, p)))
                    continue;
                foreach (var s in GoAdjacent(tile, o))
                    yield return s;
                if (!o.Ok)
                {
                    o.Succeed();
                    continue;
                }
                PutInto(chest, pending, perChest, storedTools, maxCounts);
            }
            foreach (var (chest, names) in perChest)
                stored.Add($"{Loc.DisplayName}（{Loc.NameOrUniqueName}）({tiles[chest].X},{tiles[chest].Y}) 的箱子：{string.Join("、", names)}");
        }

        /// <summary>把单格物品（全部或按上限数量）放进箱子，返回实际放进去的数量和箱子是否满了。</summary>
        private static int PutItemIntoChest(Chest chest, Item item, int? maxCount, out bool chestFull)
        {
            chestFull = false;
            int take = maxCount.HasValue ? Math.Min(maxCount.Value, item.Stack) : item.Stack;
            if (take <= 0)
                return 0;
            Item left;
            int moved;
            if (take < item.Stack)
            {
                var piece = item.getOne();
                piece.Stack = take;
                left = chest.addItem(piece);
                moved = left == null ? take : take - left.Stack;
                if (moved > 0)
                {
                    item.Stack -= moved;
                    if (item.Stack <= 0)
                        Player.removeItemFromInventory(item);
                }
            }
            else
            {
                int before = item.Stack;
                left = chest.addItem(item);
                moved = left == null ? before : before - left.Stack;
                if (left == null)
                    Player.removeItemFromInventory(item);
            }
            if (left != null)
                chestFull = true;
            return moved;
        }

        /// <summary>把东西放进箱子，放进去多少记在 perChest 里。</summary>
        private static void PutInto(Chest chest, List<Item> items, Dictionary<Chest, List<string>> perChest, List<Item> storedTools, Dictionary<Item, int> maxCounts = null)
        {
            foreach (var item in items)
            {
                int? limit = maxCounts != null && maxCounts.TryGetValue(item, out int want) ? want : null;
                string name = item.DisplayName;
                int moved = PutItemIntoChest(chest, item, limit, out _);
                if (moved > 0)
                {
                    if (maxCounts != null && maxCounts.ContainsKey(item))
                        maxCounts[item] = Math.Max(0, maxCounts[item] - moved);
                    if (!perChest.TryGetValue(chest, out var names))
                        perChest[chest] = names = new List<string>();
                    names.Add(moved > 1 ? $"{name}x{moved}" : name);
                    if (!Player.Items.Contains(item) && item is Tool)
                        storedTools?.Add(item);
                }
            }
            chest.clearNulls();
        }

        /// <summary>箱子还放不放得下这样东西：有空格，或者里面有同样的东西还没叠满。</summary>
        private static bool ChestHasRoomFor(Chest chest, Item item)
        {
            var inside = chest.GetItemsForPlayer();
            return inside.Count(i => i != null) < chest.GetActualCapacity()
                || inside.Any(c => c != null && c.canStackWith(item) && c.Stack < c.maximumStackSize());
        }

        /// <summary>
        /// 每样东西放哪个箱子：已经有同样东西的箱子 → 装着同一类东西（同一个 Category）的箱子 → 离人最近的有空位的箱子。
        /// 一边分一边扣掉要占的空格，免得都分给一个其实放不下的箱子。放不下的不分（留给第二轮）。
        /// </summary>
        private static Dictionary<Chest, List<Item>> PlanChests(List<Item> items, List<(Point tile, Chest chest)> chests)
        {
            var plan = new Dictionary<Chest, List<Item>>();
            var free = chests.ToDictionary(c => c.chest, c => c.chest.GetActualCapacity() - c.chest.GetItemsForPlayer().Count(i => i != null));
            var me = Player.TilePoint;
            foreach (var item in items)
            {
                int category = item is SObject so ? so.Category : 0;
                bool Same((Point tile, Chest chest) c) => c.chest.GetItemsForPlayer().Any(i => i != null && i.canStackWith(item))
                    || (plan.TryGetValue(c.chest, out var planned) && planned.Any(p => p.canStackWith(item)));
                bool SameKind((Point tile, Chest chest) c) => category != 0
                    && c.chest.GetItemsForPlayer().Any(i => i is SObject other && other.Category == category);
                // 箱子按大类分工：这个箱子主要装的是同一大类 → 空箱子 → 混着有这一类 → 别的类的箱子（实在没地方才混放）
                string group = StoreGroup(item);
                int GroupRank((Point tile, Chest chest) c)
                {
                    var inside = c.chest.GetItemsForPlayer().Where(i => i != null)
                        .Concat(plan.TryGetValue(c.chest, out var p) ? p : Enumerable.Empty<Item>()).ToList();
                    if (inside.Count == 0)
                        return 3;
                    if (ChestGroup(inside) == group)
                        return 2;
                    return inside.Any(i => StoreGroup(i) == group) ? 4 : 5;
                }
                // 能直接叠进去（同样的东西还没叠满）就不占新格子
                bool Stacks((Point tile, Chest chest) c) => c.chest.GetItemsForPlayer().Any(i => i != null && i.canStackWith(item) && i.Stack + item.Stack <= i.maximumStackSize());
                var pick = chests
                    .Where(c => Stacks(c) || free[c.chest] > 0)
                    .OrderBy(c => Same(c) ? 0 : SameKind(c) ? 1 : GroupRank(c))
                    .ThenBy(c => Util.Manhattan(c.tile, me))
                    .Select(c => c.chest)
                    .FirstOrDefault();
                if (pick == null)
                    continue;
                bool stacks = chests.Any(c => c.chest == pick && Stacks(c));
                bool sharesPlanned = plan.TryGetValue(pick, out var already) && already.Any(p => p.canStackWith(item));
                if (!stacks && !sharesPlanned)
                    free[pick]--;
                if (!plan.TryGetValue(pick, out var list))
                    plan[pick] = list = new List<Item>();
                list.Add(item);
            }
            return plan;
        }

        /// <summary>收纳用的大类：箱子按这个分工（矿石资源一箱、作物一箱、鱼一箱……）。</summary>
        internal static string StoreGroup(Item item)
        {
            if (item is Tool || item is StardewValley.Objects.Ring || item is StardewValley.Objects.Boots || item is StardewValley.Objects.Hat || item is StardewValley.Objects.Clothing)
                return "装备";
            if (item is not SObject o)
                return "其他";
            if (o.bigCraftable.Value)
                return "机器家具";
            if (o.Type == "Arch")
                return "宝石矿物古物";
            return o.Category switch
            {
                -15 or -16 => "矿石资源",
                -2 or -12 => "宝石矿物古物",
                -75 or -79 or -80 => "作物",
                -26 or -27 => "加工品",
                -4 or -21 or -22 or -23 => "鱼和钓具",
                -81 => "采集品",
                -7 or -25 => "料理食材",
                -74 or -19 => "种子肥料",
                -5 or -6 or -18 => "动物产品",
                -28 or -20 => "怪物掉落杂物",
                _ => "其他"
            };
        }

        /// <summary>箱子主要装的大类（格子最多的那类）；空箱子返回 null。</summary>
        internal static string ChestGroup(IEnumerable<Item> items)
            => items.Where(i => i != null).GroupBy(StoreGroup).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        // ───────────────────────── 主动收纳 ─────────────────────────

        /// <summary>
        /// 出门攒下的战利品：矿石、宝石、晶球、石头木材、煤、怪物掉落、垃圾、野菜贝壳这类采集品、古物。
        /// 鱼、作物、料理、加工品不算（可能要出货、送礼、做菜），身上该留的（工具、种子、鱼饵、炸弹、回血食物……）
        /// 和任务、收集包要交的（背包里标【留着】的）也不算。
        /// </summary>
        internal static List<Item> LootToStore()
        {
            var keep = ItemsToKeep(_ => false);
            var reserved = ReservedChecker();
            return Player.Items.Where(i => i is SObject o && !keep.Contains(i) && reserved(i) == null && IsLoot(o)).ToList();
        }

        private static readonly HashSet<string> LootIds = new() { "(O)535", "(O)536", "(O)537", "(O)749", "(O)275", "(O)791", "(O)MysteryBox", "(O)GoldenMysteryBox" };

        private static bool IsLoot(SObject o) =>
            o.Category is SObject.GemCategory or SObject.mineralsCategory or SObject.metalResources or SObject.buildingResources
                or SObject.monsterLootCategory or SObject.junkCategory or SObject.GreensCategory or SObject.sellAtFishShopCategory
            || o.Type == "Arch"
            || LootIds.Contains(o.QualifiedItemId);

        /// <summary>该不该顺手收纳：背包空位不多了，或者战利品攒得多了。</summary>
        private static bool ShouldStoreLoot(List<Item> loot) =>
            loot.Count > 0 && (Player.freeSpotsInInventory() <= 4 || loot.Count >= 6);

        /// <summary>
        /// 回到农场（或在农舍里）时把战利品存进这里的箱子：返回说明，没存返回 null。
        /// 只在当前地点有箱子时做，不会为了收纳专门跑一趟。
        /// </summary>
        private IEnumerable<object> StoreLootHere(List<string> note)
        {
            if (Loc is not (Farm or StardewValley.Locations.FarmHouse) || !PlayerChests(Loc).Any())
                yield break;
            var loot = LootToStore();
            if (!ShouldStoreLoot(loot))
                yield break;
            var stored = new List<string>();
            foreach (var s in StoreIntoChests(loot, stored))
                yield return s;
            if (stored.Count > 0)
                note.Add($"🎒 回到农场顺手把战利品收进了箱子：{string.Join("；", stored)}（背包空位 {Player.freeSpotsInInventory()} 个）");
        }

        /// <summary>这次动作里顺手收纳了什么（travel_to 回农场、sleep 前），由 Tools 接在结果后面。</summary>
        private static readonly List<string> storedLootNotes = new();

        public static string TakeStoredLootNote()
        {
            if (storedLootNotes.Count == 0)
                return null;
            string text = string.Join("\n", storedLootNotes);
            storedLootNotes.Clear();
            return text;
        }

        private static string lastLootReminder;

        /// <summary>
        /// 在外面背包快满、身上又有战利品时提醒一句（同样的情况只提醒一次）。在农场上不提醒（回农场会自动收）。
        /// </summary>
        public static string LootReminder()
        {
            if (!StardewModdingAPI.Context.IsWorldReady || Loc is Farm or StardewValley.Locations.FarmHouse)
                return null;
            int free = Player.freeSpotsInInventory();
            var loot = LootToStore();
            if (free > 2 || loot.Count == 0)
            {
                lastLootReminder = null;
                return null;
            }
            string key = free + "|" + loot.Count;
            if (key == lastLootReminder)
                return null;
            lastLootReminder = key;
            return $"🎒 背包只剩 {free} 格，身上有 {string.Join("、", loot.Take(6).Select(i => i.DisplayName))}{(loot.Count > 6 ? $" 等 {loot.Count} 样" : "")}可以收进箱子。"
                + "用 travel_to Farm 回农场时会自动收进箱子；在外面实在装不下了，先回去一趟再出来";
        }

        /// <summary>某地点里玩家自己放的箱子（不含开局礼物箱）。</summary>
        /// <summary>
        /// 东西没放下时告诉 AI 还能放哪：这里还有空位却没走到的箱子、别的地方（屋里、棚子、地窖……）还有空位的箱子，
        /// 都没有才让它做新箱子。
        /// </summary>
        private static string ChestRoomNote(GameLocation here, List<Item> pending)
        {
            static int Free(Chest c) => c.GetActualCapacity() - c.GetItemsForPlayer().Count(i => i != null);
            var parts = new List<string>();
            var stuck = PlayerChests(here).Where(c => pending.Any(p => ChestHasRoomFor(c.chest, p))).ToList();
            if (stuck.Count > 0)
                parts.Add("这里这些箱子还有空位，但走不过去（被东西围住了？）：" + string.Join("、", stuck.Take(4).Select(c => $"({c.tile.X},{c.tile.Y}) 空 {Free(c.chest)} 格")));
            var elsewhere = new List<string>();
            Utility.ForEachLocation(loc =>
            {
                if (loc == here || elsewhere.Count >= 5)
                    return true;
                foreach (var (tile, chest) in PlayerChests(loc))
                {
                    int free = Free(chest);
                    if (free > 0)
                        elsewhere.Add($"{loc.DisplayName}（{loc.NameOrUniqueName}）({tile.X},{tile.Y}) 空 {free} 格");
                }
                return true;
            });
            if (elsewhere.Count > 0)
                parts.Add("别处还有空位的箱子：" + string.Join("、", elsewhere.Take(5)) + "。travel_to 过去再 tidy_backpack（或 store_items）");
            if (parts.Count == 0)
                parts.Add("所有箱子都满了：craft Chest（50 木材）再 place_item 放一个");
            return string.Join("；", parts);
        }

        private static IEnumerable<(Point tile, Chest chest)> PlayerChests(GameLocation loc) =>
            loc.objects.Pairs
                .Where(p => p.Value is Chest c && c.playerChest.Value && !c.giftbox.Value && !IsOffMapChest(loc, p.Key, c))
                .Select(p => (new Point((int)p.Key.X, (int)p.Key.Y), (Chest)p.Value));

        /// <summary>
        /// 别的模组藏在地图外面的箱子：只在列出、存取时跳过，游戏对象本身不动。
        /// 已知的是 Grandpa's Toolbelt：每个玩家一个“第二工具栏”箱子，放在 Farm 的 objects 里，坐标在地图外（-1000~-600 随机，
        /// 老版本固定 (-500,-500)），modData 带 GrandpasToolbelt/IsBelt 和 GrandpasToolbelt/OwnerID。
        /// 那是那个模组自己的工具栏，AI 往里存取会弄乱玩家的快捷栏；坐标也走不过去。
        /// 不认识的模组也一样：只要坐标在地图外就当成别人藏起来的，不报给 AI。
        /// </summary>
        internal static bool IsOffMapChest(GameLocation loc, Vector2 tile, Chest c) =>
            c.modData.ContainsKey(ToolbeltChestKey) || IsOffMapTile(loc, tile);

        /// <summary>Grandpa's Toolbelt 给它的工具栏箱子打的标记（GrandpasToolbelt.ModEntry.GetVoidChest）。</summary>
        internal const string ToolbeltChestKey = "GrandpasToolbelt/IsBelt";

        /// <summary>坐标在这张地图外面（没有地图时不判断，当在图内）。</summary>
        internal static bool IsOffMapTile(GameLocation loc, Vector2 tile) =>
            loc.Map != null && !loc.isTileOnMap((int)tile.X, (int)tile.Y);
    }
}
