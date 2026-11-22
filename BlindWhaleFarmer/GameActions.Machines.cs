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

        private static IEnumerable<GameLocation> FarmMachineLocations()
        {
            var pending = new Queue<GameLocation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(Game1.getFarm());
            // 农场洞穴不在 buildings 里；农舍的地下室也由游戏单独关联。
            pending.Enqueue(Game1.getLocationFromName("FarmCave"));
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
                    pending.Enqueue(building.GetIndoors());
                if (location is FarmHouse house && house.upgradeLevel >= 3 && house.GetCellarName() is string cellar)
                    pending.Enqueue(Game1.getLocationFromName(cellar));
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
            return string.Join("\n", lines);
        }

        public IEnumerable<object> ListMachines(string itemSpec)
        {
            string spec = itemSpec?.Trim();
            var lines = new List<string>();
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
            yield return ActionResult.Success(lines.Count > 0 ? string.Join("\n", lines)
                : string.IsNullOrEmpty(spec) ? "农场目前没有已摆放的机器。" : $"农场上没有已摆放的“{spec}”机器。");
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
