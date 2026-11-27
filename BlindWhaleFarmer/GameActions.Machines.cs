using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Locations;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        private enum MachineState { Idle, Working, Ready }

        private sealed record FarmMachine(GameLocation Location, Point Tile, SObject Object, MachineData Data)
        {
            public MachineState State => GetMachineState(Object);
            public string Position => $"{Location.NameOrUniqueName} ({Tile.X},{Tile.Y})";
        }

        private static MachineState GetMachineState(SObject machine)
        {
            if (machine.readyForHarvest.Value && machine.heldObject.Value != null)
                return MachineState.Ready;
            return machine.heldObject.Value != null || machine.MinutesUntilReady > 0
                ? MachineState.Working : MachineState.Idle;
        }

        private static MachineData GetFarmMachineData(SObject machine)
        {
            if (machine is not { bigCraftable.Value: true } || machine is Chest)
                return null;
            var data = machine.GetMachineData();
            return data != null && !machine.IsSprinkler() && !machine.IsScarecrow() ? data : null;
        }

        private static IEnumerable<FarmMachine> EnumerateMachines(GameLocation location)
        {
            foreach (var (tile, obj) in location.objects.Pairs)
                if (GetFarmMachineData(obj) is { } data)
                    yield return new FarmMachine(location, new Point((int)tile.X, (int)tile.Y), obj, data);
        }

        /// <summary>
        /// 判断地图名是不是原版农舍/联机小屋对应的地窖（Cellar、Cellar2、Cellar3…）。
        /// 原版地窖从第 1 天起就存在于 Game1.locations 并预摆 33 个木桶，但房子升到 3 级前没有楼梯传送点、进不去。
        /// </summary>
        internal static bool IsVanillaCellarName(string locationName)
        {
            if (string.IsNullOrEmpty(locationName) || !locationName.StartsWith("Cellar", StringComparison.OrdinalIgnoreCase))
                return false;
            if (locationName.Length == 6)
                return true;
            for (int i = 6; i < locationName.Length; i++)
                if (!char.IsDigit(locationName[i]))
                    return false;
            return true;
        }

        /// <summary>原版地窖是否已有对应的 3 级农舍或联机小屋开通。</summary>
        internal static bool IsVanillaCellarUnlocked(string cellarName)
        {
            if (string.IsNullOrWhiteSpace(cellarName))
                return false;
            bool unlocked = false;
            Utility.ForEachLocation(loc =>
            {
                if (loc is FarmHouse house && house.upgradeLevel >= 3
                    && string.Equals(house.GetCellarName(), cellarName, StringComparison.OrdinalIgnoreCase))
                {
                    unlocked = true;
                    return false;
                }
                return true;
            }, includeInteriors: true, includeGenerated: false);
            return unlocked;
        }

        /// <summary>
        /// 扫描 Game1.locations 寻找机器时，是否直接把这张图加入候选队列。
        /// 原版地窖（Cellar/Cellar2…）不能因预摆了木桶就直接入队，必须只从 upgradeLevel >= 3 的农舍/小屋按 GetCellarName() 引入。
        /// </summary>
        internal static bool ShouldScanLocationDirectlyForMachines(
            bool isTemporary,
            bool isWarpRoom,
            bool isVanillaCellar,
            bool isFarm,
            bool isGreenhouse,
            int buildingCount,
            bool hasFarmMachine)
        {
            if (isTemporary || isWarpRoom || isVanillaCellar)
                return false;
            return isFarm || isGreenhouse || buildingCount > 0 || hasFarmMachine;
        }

        private static IEnumerable<GameLocation> FarmMachineLocations()
        {
            var pending = new Queue<GameLocation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Enqueue(GameLocation loc)
            {
                if (loc != null && !loc.IsTemporary && !ModSupport.IsWarpRoom(loc.NameOrUniqueName))
                    pending.Enqueue(loc);
            }
            Enqueue(Game1.getFarm());
            Enqueue(Game1.getLocationFromName("FarmHouse"));
            // 农场洞穴不在 buildings 里；农舍的地下室也由游戏单独关联。
            Enqueue(Game1.getLocationFromName("FarmCave"));
            // 兼容 SVE 等模组的额外农场/温室/小屋/酒窖（如爷爷小屋、极光葡萄园、Grampleton Fields）以及温室/姜岛
            foreach (var loc in Game1.locations)
            {
                if (loc == null)
                    continue;
                bool isVanillaCellar = loc is Cellar && IsVanillaCellarName(loc.NameOrUniqueName);
                if (ShouldScanLocationDirectlyForMachines(
                    loc.IsTemporary,
                    ModSupport.IsWarpRoom(loc.NameOrUniqueName),
                    isVanillaCellar,
                    loc.IsFarm,
                    loc.IsGreenhouse,
                    loc.buildings.Count,
                    !isVanillaCellar && loc.objects.Values.Any(o => GetFarmMachineData(o) != null)))
                    Enqueue(loc);
            }
            while (pending.Count > 0)
            {
                var location = pending.Dequeue();
                if (location == null || !seen.Add(location.NameOrUniqueName))
                    continue;
                // 联机时优先读玩家当前所在的地图实例。
                if (Loc?.NameOrUniqueName == location.NameOrUniqueName)
                    location = Loc;
                yield return location;
                foreach (var building in location.buildings)
                    Enqueue(building.GetIndoors());
                if (location is FarmHouse house && house.upgradeLevel >= 3 && house.GetCellarName() is string cellar)
                    Enqueue(Game1.getLocationFromName(cellar));
            }
        }

        private static IEnumerable<IGrouping<string, FarmMachine>> EnumerateFarmMachines() =>
            FarmMachineLocations().SelectMany(EnumerateMachines).GroupBy(m => m.Object.QualifiedItemId);

        internal static string FarmMachineOverview()
        {
            const int maxTypes = 12;
            int positionsLeft = 6;
            var groups = EnumerateFarmMachines().OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (groups.Count == 0)
                return "农场目前没有已摆放的机器。";

            var lines = new List<string>();
            int totalReady = 0;
            foreach (var group in groups)
                totalReady += group.Count(m => m.State == MachineState.Ready);
            foreach (var group in groups.Take(maxTypes))
            {
                var idle = group.Where(m => m.State == MachineState.Idle).ToList();
                var ready = group.Where(m => m.State == MachineState.Ready).ToList();
                int count = group.Count();
                string line = $"- {group.First().Object.DisplayName} {count} 台（空闲 {idle.Count}、加工中 {count - idle.Count - ready.Count}、待收 {ready.Count}）";
                foreach (var (machines, label) in new[] { (idle, "空闲"), (ready, "待收") })
                {
                    if (positionsLeft == 0 || machines.Count == 0)
                        continue;
                    var first = machines.OrderBy(m => m.Location.NameOrUniqueName, StringComparer.Ordinal)
                        .ThenBy(m => m.Tile.Y).ThenBy(m => m.Tile.X).First();
                    line += $"，{label}例：{first.Position}";
                    positionsLeft--;
                }
                lines.Add(line);
            }
            if (groups.Count > maxTypes)
                lines.Add($"还有 {groups.Count - maxTypes} 种机器，用 list_machines 查看。");
            if (totalReady > 0)
                lines.Add($"共有 {totalReady} 台机器做好了，可用 collect_machines 一次收完。");
            return string.Join("\n", lines);
        }

        public IEnumerable<object> ListMachines(string itemSpec)
        {
            string spec = itemSpec?.Trim();
            var lines = new List<string>();
            int totalReady = 0;
            foreach (var group in EnumerateFarmMachines())
            {
                var machine = group.First().Object;
                if (!string.IsNullOrEmpty(spec) && !machine.DisplayName.Contains(spec, StringComparison.OrdinalIgnoreCase)
                    && !machine.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)
                    && !group.Key.Equals(spec, StringComparison.OrdinalIgnoreCase))
                    continue;
                lines.Add($"{machine.DisplayName}（{group.Key}）{group.Count()} 台");
                foreach (var entry in group.OrderBy(m => m.Location.NameOrUniqueName, StringComparer.Ordinal).ThenBy(m => m.Tile.Y).ThenBy(m => m.Tile.X))
                {
                    if (entry.State == MachineState.Ready)
                        totalReady++;
                    string state = entry.State switch
                    {
                        MachineState.Idle => "空闲",
                        MachineState.Ready => $"待收：{entry.Object.heldObject.Value.DisplayName}",
                        _ when entry.Object.MinutesUntilReady > 0 => $"加工中，约剩 {entry.Object.MinutesUntilReady} 分钟（游戏时间）",
                        _ => entry.Data.OnlyCompleteOvernight ? "加工中，等待过夜结算" : "加工中，等待完成"
                    };
                    lines.Add($"- {entry.Position} {state}");
                }
            }
            if (totalReady > 0)
                lines.Add($"共有 {totalReady} 台机器做好了，可用 collect_machines 一次收完。");
            yield return ActionResult.Success(lines.Count > 0 ? string.Join("\n", lines)
                : string.IsNullOrEmpty(spec) ? "农场目前没有已摆放的机器。" : $"农场上没有已摆放的“{spec}”机器。");
        }

        internal static bool MatchesMachineFilter(
            string spec,
            string machineDisplayName, string machineName, string machineQualifiedId,
            string heldDisplayName, string heldName, string heldQualifiedId)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return true;
            string s = spec.Trim();
            return (!string.IsNullOrEmpty(machineDisplayName) && machineDisplayName.Contains(s, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(machineName) && machineName.Contains(s, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(machineQualifiedId) && machineQualifiedId.Equals(s, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(heldDisplayName) && heldDisplayName.Contains(s, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(heldName) && heldName.Contains(s, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(heldQualifiedId) && heldQualifiedId.Equals(s, StringComparison.OrdinalIgnoreCase));
        }

        internal static string FormatCollectMachinesSummary(
            int collected,
            IEnumerable<(string id, string name, int quality, int quantity)> gains,
            int skippedUnreachable,
            int skippedOther,
            int remainingFull,
            bool stoppedFull)
        {
            string gainedText = InventoryDiff.FormatGains(gains);
            string head = !string.IsNullOrEmpty(gainedText)
                ? $"收了 {collected} 台机器：{gainedText}"
                : $"收了 {collected} 台机器";
            var notes = new List<string>();
            int skipped = skippedUnreachable + skippedOther;
            if (skipped > 0)
                notes.Add(skippedUnreachable > 0 && skippedOther == 0
                    ? $"跳过 {skippedUnreachable} 台（走不到）"
                    : $"跳过 {skipped} 台");
            if (stoppedFull)
                notes.Add($"背包满了，还剩 {remainingFull} 台待收机器没收");
            return notes.Count > 0 ? $"{head}（{string.Join("；", notes)}）" : head;
        }

        private static (string id, string name, int quality, int quantity)[] SnapshotPlayerItems() =>
            InventoryDiff.Snapshot(Player.Items.Where(it => it != null)
                .Select(it => (it.QualifiedItemId, it.DisplayName, it.Quality, it.Stack)));

        private static int CountRemainingReadyMachines(GameLocation loc, List<Point> ordered, int fromIndex, string itemSpec)
        {
            int count = 0;
            for (int j = fromIndex; j < ordered.Count; j++)
            {
                var pt = ordered[j];
                if (loc.objects.TryGetValue(new Vector2(pt.X, pt.Y), out var obj)
                    && GetFarmMachineData(obj) != null
                    && GetMachineState(obj) == MachineState.Ready
                    && MatchesMachineFilter(itemSpec, obj.DisplayName, obj.Name, obj.QualifiedItemId,
                        obj.heldObject.Value?.DisplayName, obj.heldObject.Value?.Name, obj.heldObject.Value?.QualifiedItemId))
                    count++;
            }
            return count;
        }

        /// <summary>批量收取当前地点（或指定地点）所有已经做好的机器产物（含农场山洞蘑菇箱等），不自动补料。</summary>
        public IEnumerable<object> CollectMachines(string location, string itemSpec)
        {
            var o = new Outcome();
            if (!string.IsNullOrWhiteSpace(location) && !Loc.NameOrUniqueName.Equals(location, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in TravelInner(location, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }

            var loc = Loc;
            var allMachines = EnumerateMachines(loc).ToList();
            if (allMachines.Count == 0)
            {
                yield return ActionResult.Fail($"{loc.NameOrUniqueName} 没有已摆放的机器");
                yield break;
            }

            var allReady = allMachines.Where(m => m.State == MachineState.Ready).ToList();
            var matched = allReady.Where(m => MatchesMachineFilter(
                itemSpec,
                m.Object.DisplayName, m.Object.Name, m.Object.QualifiedItemId,
                m.Object.heldObject.Value?.DisplayName, m.Object.heldObject.Value?.Name, m.Object.heldObject.Value?.QualifiedItemId)).ToList();

            if (matched.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(itemSpec))
                {
                    string extra = allReady.Count > 0 ? $"（这里另有 {allReady.Count} 台其他机器待收）" : "";
                    yield return ActionResult.Fail($"{loc.NameOrUniqueName} 没有匹配“{itemSpec.Trim()}”的待收机器{extra}");
                }
                else
                {
                    int working = allMachines.Count(m => m.State == MachineState.Working);
                    int idle = allMachines.Count - working;
                    yield return ActionResult.Fail($"{loc.NameOrUniqueName} 目前没有做好的机器产物（加工中 {working} 台，空闲 {idle} 台）");
                }
                yield break;
            }

            var ordered = Util.NearestOrder(Player.TilePoint, matched.Select(m => m.Tile));
            int collected = 0, skippedUnreachable = 0, skippedOther = 0, remainingFull = 0;
            bool stoppedFull = false;
            var gains = new List<(string id, string name, int quality, int quantity)>();

            for (int i = 0; i < ordered.Count; i++)
            {
                var tile = ordered[i];
                var v = new Vector2(tile.X, tile.Y);
                if (!loc.objects.TryGetValue(v, out var obj) || GetFarmMachineData(obj) == null || GetMachineState(obj) != MachineState.Ready)
                {
                    skippedOther++;
                    continue;
                }
                if (!Player.couldInventoryAcceptThisItem(obj.heldObject.Value))
                {
                    stoppedFull = true;
                    remainingFull = CountRemainingReadyMachines(loc, ordered, i, itemSpec);
                    break;
                }

                foreach (var s in GoAdjacent(tile, o))
                    yield return s;
                if (!o.Ok)
                {
                    skippedUnreachable++;
                    continue;
                }

                if (!loc.objects.TryGetValue(v, out obj) || GetFarmMachineData(obj) == null || GetMachineState(obj) != MachineState.Ready)
                {
                    skippedOther++;
                    continue;
                }

                int safe = Util.FindTool<Tool>();
                if (safe >= 0)
                    Player.CurrentToolIndex = safe;

                var before = SnapshotPlayerItems();
                bool ok = obj.checkForAction(Player);
                foreach (var s in WaitTicks(4))
                    yield return s;
                var after = SnapshotPlayerItems();

                if (!ok && obj.readyForHarvest.Value && obj.heldObject.Value != null)
                {
                    stoppedFull = true;
                    remainingFull = CountRemainingReadyMachines(loc, ordered, i, itemSpec);
                    break;
                }

                var diff = InventoryDiff.Difference(before, after);
                if (diff.Any(d => d.quantity > 0) || !obj.readyForHarvest.Value)
                {
                    collected++;
                    gains.AddRange(diff);
                }
                else
                {
                    skippedOther++;
                }
            }

            if (collected == 0)
            {
                if (stoppedFull)
                    yield return ActionResult.Fail($"背包满了，还剩 {remainingFull} 台待收机器没收");
                else if (skippedUnreachable > 0)
                    yield return ActionResult.Fail($"有 {skippedUnreachable} 台待收机器走不到，未能收取");
                else
                    yield return ActionResult.Fail("没有收到任何机器产物");
                yield break;
            }

            yield return ActionResult.Success(FormatCollectMachinesSummary(
                collected, InventoryDiff.Sum(gains), skippedUnreachable, skippedOther, remainingFull, stoppedFull));
        }

        // 只支持普通投料规则。自定义产物方法可能在规则匹配后拒绝原料或执行额外逻辑。
        private static bool HasOrdinaryOutput(MachineOutputRule rule) =>
            rule.OutputItem is { Count: > 0 } && rule.OutputItem.All(output => output.OutputMethod == null);

        private static bool CanProbeMachine(SObject machine) =>
            machine.GetType() == typeof(SObject) && !machine.isTemporarilyInvisible && machine.Type is "Crafting" or "interactive";

        private static bool TryProbeMachine(FarmMachine entry, Item item, out bool matches)
        {
            var machine = entry.Object;
            matches = CanProbeMachine(machine)
                && MachineDataUtility.TryGetMachineOutputRule(machine, entry.Data, MachineOutputTrigger.ItemPlacedInMachine,
                    item, Player, entry.Location, out var rule, out _, out _, out _)
                && HasOrdinaryOutput(rule)
                && MachineDataUtility.HasAdditionalRequirements(Player.Items, entry.Data.AdditionalConsumedItems, out _);
            // 忙着的机器只查规则，用于区分“都在忙”和“材料不合适”。空闲机用游戏的静默探测核实。
            return matches && entry.State == MachineState.Idle
                && machine.PlaceInMachine(entry.Data, item, probe: true, who: Player, showMessages: false, playSounds: false);
        }

        private static FarmMachine FindNearestAcceptingMachine(Item item, out string why)
        {
            if (item is not SObject)
            {
                why = "普通机器只接受可以拿在手里的物品";
                return null;
            }

            bool busy = false, hasOrdinaryMachine = false;
            bool Accepts(FarmMachine entry)
            {
                hasOrdinaryMachine |= CanProbeMachine(entry.Object)
                    && entry.Data.OutputRules?.Any(rule => HasOrdinaryOutput(rule)
                        && rule.Triggers?.Any(t => t.Trigger.HasFlag(MachineOutputTrigger.ItemPlacedInMachine)) == true) == true;
                if (TryProbeMachine(entry, item, out bool matches))
                    return true;
                busy |= matches && entry.State != MachineState.Idle;
                return false;
            }

            var me = Player.TilePoint;
            var current = EnumerateMachines(Loc).OrderBy(m => Util.Manhattan(m.Tile, me)).FirstOrDefault(Accepts);
            if (current != null)
            {
                why = null;
                return current;
            }

            FarmMachine closest = null;
            int bestDistance = int.MaxValue;
            bool foundIdle = false;
            var farms = EnumerateFarmMachines().SelectMany(g => g)
                .Where(m => m.Location.NameOrUniqueName != Loc.NameOrUniqueName && Accepts(m))
                .GroupBy(m => m.Location.NameOrUniqueName);
            foreach (var location in farms)
            {
                foundIdle = true;
                var route = Exits.PlanRoute(Loc, location.Key);
                if (route is not { Count: > 0 })
                    continue;
                int distance = 0;
                var from = me;
                foreach (var exit in route)
                {
                    distance += Util.Manhattan(from, exit.Tile) + 1;
                    // 建筑门的出口记录没有落点，用游戏进门时采用的室内第一处 warp。
                    var warps = exit.Building?.GetIndoors()?.warps;
                    from = warps is { Count: > 0 } ? new Point(warps[0].X, warps[0].Y - 1) : exit.TargetTile;
                }
                foreach (var machine in location)
                {
                    int steps = distance + Util.Manhattan(from, machine.Tile);
                    if (steps < bestDistance)
                    {
                        bestDistance = steps;
                        closest = machine;
                    }
                }
            }

            why = closest != null ? null
                : foundIdle ? "有空闲机器能接收，但找不到通往它所在地点的路线"
                : busy ? "能接收这种原料的机器都在加工或等收产物"
                : !hasOrdinaryMachine ? "当前地点和农场上没有可自动投料的普通机器"
                : $"当前地点和农场上没有普通机器接受“{item.DisplayName}”（也可能是原料或辅料数量不够）";
            return closest;
        }
    }
}
