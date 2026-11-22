using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.FarmAnimals;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        /// <summary>农场上所有住着动物的建筑。</summary>
        public static List<(Building building, AnimalHouse house)> AnimalBuildings()
        {
            var list = new List<(Building, AnimalHouse)>();
            foreach (var b in Game1.getFarm().buildings)
            {
                if (b.isUnderConstruction() || b.GetIndoors() is not AnimalHouse house)
                    continue;
                if (house.animalsThatLiveHere.Count > 0)
                    list.Add((b, house));
            }
            return list;
        }

        /// <summary>让动物原地停一会儿，方便走过去。</summary>
        private static void FreezeAnimals(GameLocation loc)
        {
            foreach (var a in loc.animals.Values)
            {
                a.Halt();
                a.pauseTimer = Math.Max(a.pauseTimer, 4000);
            }
        }

        /// <summary>
        /// 照顾动物：喂干草、捡蛋/羊毛等地上的产物、清空自动采集器、摸摸每只动物、用挤奶桶/剪刀收产物。
        /// 会自动走进每个鸡舍/畜棚，最后回到农场上处理在外面的动物。
        /// </summary>
        public IEnumerable<object> CareAnimals(string door)
        {
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var buildings = AnimalBuildings();
            if (buildings.Count == 0 && Game1.getFarm().animals.Length == 0)
            {
                yield return ActionResult.Fail("你还没有动物（先在 Robin 那里建鸡舍/畜棚，再去 Marnie 的牧场 AnimalShop 买动物）");
                yield break;
            }

            var report = new List<string>();
            var stats = new CareStats();
            var o = new Outcome();

            // 按距离：如果已经在某个动物屋里，先处理它
            var ordered = buildings.OrderBy(b => b.house == Loc ? 0 : 1).ToList();
            foreach (var (building, house) in ordered)
            {
                string name = house.NameOrUniqueName;
                if (Loc != house)
                {
                    foreach (var s in TravelInner(name, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        report.Add($"{building.buildingType.Value}：进不去（{o.Message}）");
                        continue;
                    }
                }
                foreach (var s in CareInLocation(house, stats))
                    yield return s;

                // 喂干草：和手动从饲料槽旁取干草放进食槽效果一样，用的是筒仓里的干草
                int before = CountHay(house);
                house.feedAllAnimals();
                // 筒仓不够（或者没有筒仓、干草是在 Marnie 那里买进背包的）：用背包里的干草补满食槽
                FeedFromBackpack(house);
                int fed = CountHay(house) - before;
                if (fed > 0)
                    stats.Hay += fed;
                if (CountHay(house) < house.animalsThatLiveHere.Count)
                    stats.NoHay = true;
            }

            // 农场外面的动物 + 动物门
            bool outsideAnimals = Game1.getFarm().animals.Length > 0;
            bool doorChange = !string.IsNullOrEmpty(door) && door is "open" or "close" or "打开" or "关闭";
            if (outsideAnimals || doorChange)
            {
                if (Loc != Game1.getFarm())
                {
                    foreach (var s in TravelInner("Farm", o))
                        yield return s;
                }
                if (Loc == Game1.getFarm())
                {
                    if (outsideAnimals)
                    {
                        foreach (var s in CareInLocation(Loc, stats))
                            yield return s;
                    }
                    if (doorChange)
                    {
                        bool open = door is "open" or "打开";
                        foreach (var (building, _) in buildings)
                        {
                            if (building.animalDoorOpen.Value != open && building.animalDoor.Value.X >= 0)
                            {
                                building.ToggleAnimalDoor(Player);
                                stats.Doors++;
                            }
                        }
                    }
                }
            }

            report.Insert(0, stats.Describe(door));
            yield return ActionResult.Success(string.Join("；", report));
        }

        /// <summary>留住动物门附近的牧草、约四分之一的草格，以及已经长成单独一格的草。</summary>
        internal static bool KeepHayGrass(Point tile, IReadOnlySet<Point> grass, IReadOnlyList<Point> animalDoors)
            => ((tile.X + tile.Y) & 3) == 0
                || animalDoors.Any(door => Math.Max(Math.Abs(tile.X - door.X), Math.Abs(tile.Y - door.Y)) <= 3)
                || !Pathfinder.Dirs.Any(d => grass.Contains(tile + d));

        private static bool IsHayGrass(TerrainFeature feature) => feature is Grass grass && grass.grassType.Value is 1 or 7;

        private static List<GameLocation> HayLocations(Farm farm)
        {
            var locations = new List<GameLocation> { farm };
            Utility.ForEachLocation(loc =>
            {
                if (loc != farm && loc.buildings.Count > 0)
                    locations.Add(loc);
                return true;
            }, includeInteriors: false);
            return locations;
        }

        private static (int silos, int capacity, int hay, bool hasRoom) HayStorage(List<GameLocation> locations)
        {
            int silos = 0, capacity = 0, hay = 0;
            bool hasRoom = false;
            foreach (var loc in locations)
            {
                silos += loc.buildings.Count(b => b.hayCapacity.Value > 0 && b.daysOfConstructionLeft.Value <= 0);
                int limit = loc.GetHayCapacity();
                capacity += limit;
                hay += loc.piecesOfHay.Value;
                hasRoom |= limit > loc.piecesOfHay.Value;
            }
            return (silos, capacity, hay, hasRoom);
        }

        private static List<Point> HayAnimalDoors(Farm farm) => farm.buildings
            .Where(b => !b.isUnderConstruction() && b.animalDoor.X >= 0 && b.animalDoor.Y >= 0 && b.GetIndoors() is AnimalHouse)
            .Select(b => new Point(b.tileX.Value + b.animalDoor.X, b.tileY.Value + b.animalDoor.Y)).ToList();

        // 原版镰刀动画的几帧会扫过多格。只收窄本次 cut_hay 的命中范围，动画、掉落和入仓仍由游戏处理。
        private static readonly Dictionary<MeleeWeapon, (Farm farm, Farmer player, Point tile)> HaySwings = new();
        private static bool haySwingPatched;

        private static void LimitHaySwing(MeleeWeapon __instance, ref Rectangle __result)
        {
            if (HaySwings.TryGetValue(__instance, out var swing) && swing.farm == Loc && swing.player == Player)
                __result = new Rectangle(swing.tile.X * 64 + 32, swing.tile.Y * 64 + 32, 1, 1);
        }

        private static string PrepareHaySwing()
        {
            if (haySwingPatched)
                return null;
            try
            {
                new Harmony("Darian.BlindWhaleFarmer.CutHay").Patch(
                    AccessTools.Method(typeof(MeleeWeapon), nameof(MeleeWeapon.getAreaOfEffect)),
                    postfix: new HarmonyMethod(typeof(GameActions), nameof(LimitHaySwing)));
                haySwingPatched = true;
                return null;
            }
            catch (Exception ex)
            {
                return $"镰刀的单格范围没能启用：{ex.Message}";
            }
        }

        /// <summary>到主农场逐格用镰刀收干草，按筒仓读数决定何时停。</summary>
        public IEnumerable<object> CutHay(int? target, int? maxTiles, int? x1, int? y1, int? x2, int? y2)
        {
            int given = new[] { x1, y1, x2, y2 }.Count(v => v.HasValue);
            if (given != 0 && given != 4)
            {
                yield return ActionResult.Fail("cut_hay 的 x1、y1、x2、y2 要么四个都给，要么都不给（不给就在主农场找草）");
                yield break;
            }
            if (target is < 1 || maxTiles is < 1)
            {
                yield return ActionResult.Fail("cut_hay 的 target 和 max_tiles 要大于 0");
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }

            var farm = Game1.getFarm();
            var locations = HayLocations(farm);
            var start = HayStorage(locations);
            if (start.silos == 0)
            {
                yield return ActionResult.Fail("没筒仓割草不存干草，先建好筒仓或去 Marnie 那里买干草，本次没有割草");
                yield break;
            }
            int goal = Math.Min(target ?? start.capacity, start.capacity);
            string before = $"{start.silos} 座筒仓，干草 {start.hay}/{start.capacity}，目标 {goal}";
            if (!start.hasRoom || start.hay >= goal)
            {
                yield return ActionResult.Success(before + "，本批入仓 0 份" + (start.hasRoom ? "，已达到目标" : "，筒仓已满"));
                yield break;
            }

            int scythe = Util.FindTool<MeleeWeapon>(w => w.isScythe());
            if (scythe < 0)
            {
                yield return ActionResult.Fail(before + "，背包里没有镰刀");
                yield break;
            }
            var weapon = (MeleeWeapon)Player.Items[scythe];
            var grass = farm.terrainFeatures.Pairs.Where(pair => IsHayGrass(pair.Value))
                .Select(pair => new Point((int)pair.Key.X, (int)pair.Key.Y)).ToHashSet();
            var doors = HayAnimalDoors(farm);
            var candidates = grass.Where(p => !KeepHayGrass(p, grass, doors)
                && !farm.objects.ContainsKey(new Vector2(p.X, p.Y))
                && (given == 0 || (p.X >= Math.Min(x1.Value, x2.Value) && p.X <= Math.Max(x1.Value, x2.Value)
                    && p.Y >= Math.Min(y1.Value, y2.Value) && p.Y <= Math.Max(y1.Value, y2.Value)))).ToList();
            if (candidates.Count == 0)
            {
                yield return ActionResult.Success(before + "，本批入仓 0 份，区域内没有可割的草（牧草和草种留着）");
                yield break;
            }
            if (PrepareHaySwing() is string problem)
            {
                yield return ActionResult.Fail(problem);
                yield break;
            }

            var o = new Outcome();
            if (Loc != farm)
            {
                foreach (var s in TravelInner("Farm", o))
                    yield return s;
                if (!o.Ok || Loc != farm)
                {
                    yield return ActionResult.Fail(o.Message ?? "没有走到主农场");
                    yield break;
                }
            }

            int limit = Math.Min(maxTiles ?? 60, 300), worked = 0, cut = 0;
            string stop = null;
            var current = start;
            foreach (var p in Util.NearestOrder(Player.TilePoint, candidates))
            {
                current = HayStorage(locations);
                if (current.hay >= goal || !current.hasRoom || worked >= limit)
                    break;
                foreach (var s in GoAdjacent(p, o))
                    yield return s;
                if (!o.Ok)
                {
                    stop = o.Message;
                    if (Loc != farm || Blocked(out _))
                        break;
                    continue;
                }
                var v = new Vector2(p.X, p.Y);
                bool HasGrass(Point tile) => farm.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var f) && IsHayGrass(f);
                // 走过去期间队友可能割掉旁边的草，或把东西放到草上。挥刀前按现场重新判断。
                var nearby = Pathfinder.Dirs.Select(d => p + d).Where(HasGrass).ToHashSet();
                if (!HasGrass(p) || KeepHayGrass(p, nearby, HayAnimalDoors(farm)) || farm.objects.ContainsKey(v))
                    continue;

                bool swung = false;
                for (int hit = 0; hit < 4 && HasGrass(p) && !farm.objects.ContainsKey(v); hit++)
                {
                    current = HayStorage(locations);
                    if (!current.hasRoom || current.hay >= goal || Loc != farm)
                        break;
                    scythe = Player.Items.IndexOf(weapon);
                    if (scythe < 0)
                    {
                        o.Fail("背包里没有原来的镰刀了");
                        break;
                    }
                    HaySwings.Add(weapon, (farm, Player, p));
                    try
                    {
                        foreach (var s in UseToolOnce(scythe, p, o))
                            yield return s;
                    }
                    finally
                    {
                        HaySwings.Remove(weapon);
                    }
                    if (!o.Ok)
                        break;
                    swung = true;
                }
                if (swung)
                {
                    worked++;
                    if (!HasGrass(p))
                        cut++;
                }
                if (!o.Ok)
                {
                    stop = o.Message;
                    break;
                }
            }
            current = HayStorage(locations);
            string reason = !current.hasRoom ? "筒仓已满" : current.hay >= goal ? "已达到目标"
                : worked >= limit ? "达到本批割草格数上限" : "本批没有其他能割到的草";
            string msg = $"割完 {cut} 格草（挥刀 {worked} 格），本批筒仓净增 {current.hay - start.hay} 份干草，"
                + $"现有 {current.hay}/{current.capacity}（{current.silos} 座，起始 {start.hay}，目标 {goal}）。{reason}"
                + (stop != null ? $"。{stop}" : "");
            yield return worked == 0 && stop != null ? ActionResult.Fail(msg) : ActionResult.Success(msg);
        }

        private static string HayShortageHint()
        {
            var farm = Game1.getFarm();
            return HayStorage(HayLocations(farm)).silos > 0 && farm.terrainFeatures.Pairs.Any(p => IsHayGrass(p.Value))
                ? "有筒仓、农场上有草，先用 cut_hay 补干草，不够再去 Marnie 那里买"
                : "去 Marnie 那里买干草，有筒仓且农场有草时可以先用 cut_hay 补";
        }

        private sealed class CareStats
        {
            public int Petted, Harvested, Picked, Hay, Doors, TooLate;
            public bool NoHay;
            public readonly List<string> Problems = new();

            public string Describe(string door)
            {
                var parts = new List<string> { $"摸了 {Petted} 只", $"挤奶/剪毛 {Harvested} 次", $"捡起产物 {Picked} 个", $"放干草 {Hay} 份" };
                if (Doors > 0)
                    parts.Add($"{(door is "open" or "打开" ? "打开" : "关上")}了 {Doors} 扇动物门");
                if (TooLate > 0)
                    parts.Add($"{TooLate} 只已经睡了（19:00 后摸不了）");
                if (NoHay)
                    parts.Add($"筒仓和背包里的干草都不够，食槽没喂满（{HayShortageHint()}）");
                parts.AddRange(Problems.Distinct().Take(4));
                return string.Join("，", parts);
            }
        }

        private static int CountHay(AnimalHouse house) =>
            house.objects.Values.Count(obj => obj.QualifiedItemId == "(O)178");

        /// <summary>食槽的格子（Back 层有 Trough 属性），从左上往右下。</summary>
        internal static List<Point> TroughTiles(GameLocation house)
        {
            var list = new List<Point>();
            var back = house.map?.GetLayer("Back");
            if (back == null)
                return list;
            for (int y = 0; y < back.LayerHeight; y++)
                for (int x = 0; x < back.LayerWidth; x++)
                    if (house.doesTileHaveProperty(x, y, "Trough", "Back") != null)
                        list.Add(new Point(x, y));
            return list;
        }

        /// <summary>
        /// 把背包里的干草放进空食槽，最多放到每只动物一份（和 AnimalHouse.checkAction 手拿干草点食槽一样：
        /// 食槽格上放一个干草物品）。
        /// </summary>
        private static void FeedFromBackpack(AnimalHouse house)
        {
            int need = house.animalsThatLiveHere.Count - CountHay(house);
            foreach (var tile in TroughTiles(house))
            {
                if (need <= 0)
                    return;
                var key = new Vector2(tile.X, tile.Y);
                if (house.objects.ContainsKey(key))
                    continue;
                int idx = Player.Items.IndexOf(Player.Items.FirstOrDefault(i => i?.QualifiedItemId == "(O)178"));
                if (idx < 0 || Player.Items[idx] is not SObject hay)
                    return;
                house.objects.Add(key, (SObject)hay.getOne());
                Player.Items[idx] = hay.ConsumeStack(1);
                need--;
            }
        }

        private IEnumerable<object> CareInLocation(GameLocation loc, CareStats stats)
        {
            var o = new Outcome();

            // 1) 地上的蛋、羊毛、鸭毛等（会自动生成在地上的物品），以及自动采集器。
            //    在农场上只捡猪拱出来的松露，其他采集品不在这里满地图跑着捡
            bool inHouse = loc is AnimalHouse;
            var pickups = loc.objects.Pairs
                .Where(p => (p.Value.IsSpawnedObject && (inHouse || p.Value.QualifiedItemId == "(O)430"))
                    || (p.Value.QualifiedItemId == "(BC)165" && p.Value.heldObject.Value is Chest c && c.Items.Any(i => i != null)))
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y))
                .ToList();
            foreach (var tile in Util.NearestOrder(Player.TilePoint, pickups))
            {
                if (!loc.objects.ContainsKey(new Vector2(tile.X, tile.Y)))
                    continue;
                if (Player.freeSpotsInInventory() == 0)
                {
                    stats.Problems.Add("背包满了，有东西没捡");
                    break;
                }
                FreezeAnimals(loc);
                foreach (var s in GoAdjacent(tile, o))
                    yield return s;
                if (!o.Ok)
                    continue;
                bool wasThere = loc.objects.ContainsKey(new Vector2(tile.X, tile.Y));
                loc.checkAction(new xLocation(tile.X, tile.Y), Game1.viewport, Player);
                foreach (var s in WaitTicks(6))
                    yield return s;
                if (Game1.activeClickableMenu is ItemGrabMenu grab)
                {
                    int count = grab.ItemsToGrabMenu?.actualInventory.Count(i => i != null) ?? 0;
                    GrabAll(grab);
                    stats.Picked += count;
                }
                else if (wasThere && !loc.objects.ContainsKey(new Vector2(tile.X, tile.Y)))
                    stats.Picked++;
                foreach (var s in WaitPlayerFree())
                    yield return s;
            }

            // 2) 每只动物：先收产物（挤奶/剪毛），再摸
            foreach (var animal in loc.animals.Values.OrderBy(a => Util.Manhattan(a.TilePoint, Player.TilePoint)).ToList())
            {
                bool needPet = !animal.wasPet.Value;
                int toolIdx = -1;
                if (animal.isAdult() && animal.currentProduce.Value != null && animal.GetHarvestType() == FarmAnimalHarvestType.HarvestWithTool)
                {
                    toolIdx = Util.FindTool<Tool>(t => animal.CanGetProduceWithTool(t));
                    if (toolIdx < 0)
                        stats.Problems.Add($"{animal.displayName}可以收{(animal.GetAnimalData()?.HarvestTool == "Shears" ? "羊毛，需要剪刀" : "奶，需要挤奶桶")}（Marnie 那里买）");
                }
                if (!needPet && toolIdx < 0)
                    continue;
                if (needPet && Game1.timeOfDay >= 1900 && toolIdx < 0)
                {
                    stats.TooLate++;
                    continue;
                }

                FreezeAnimals(loc);
                var target = animal.TilePoint;
                foreach (var s in GoAdjacent(target, o))
                    yield return s;
                if (!o.Ok)
                {
                    stats.Problems.Add($"{animal.displayName}走不过去");
                    continue;
                }
                // 走路时动物可能挪了一点，重新瞄准
                target = animal.TilePoint;
                Face(target);

                if (toolIdx >= 0)
                {
                    if (Player.freeSpotsInInventory() == 0)
                        stats.Problems.Add("背包满了，收不了奶/毛");
                    else
                    {
                        // 没收成功（动物挪了位置、没打中）：重新走到旁边、对准，最多试 3 次
                        for (int attempt = 0; attempt < 3 && animal.currentProduce.Value != null; attempt++)
                        {
                            if (attempt > 0)
                            {
                                FreezeAnimals(loc);
                                target = animal.TilePoint;
                                var again = new Outcome();
                                foreach (var s in GoAdjacent(target, again))
                                    yield return s;
                                if (!again.Ok)
                                    break;
                                target = animal.TilePoint;
                                Face(target);
                            }
                            animal.pauseTimer = Math.Max(animal.pauseTimer, 3000);
                            foreach (var s in UseToolOnce(toolIdx, target, o))
                                yield return s;
                            foreach (var s in WaitPlayerFree())
                                yield return s;
                        }
                        if (animal.currentProduce.Value == null)
                            stats.Harvested++;
                        else
                            stats.Problems.Add($"{animal.displayName}试了 3 次还是没收成功");
                    }
                }

                if (needPet && Game1.timeOfDay < 1900 && Game1.activeClickableMenu == null)
                {
                    animal.pet(Player);
                    foreach (var s in WaitTicks(10))
                        yield return s;
                    if (animal.wasPet.Value)
                        stats.Petted++;
                    // 摸的时候有时会弹出动物信息框，关掉
                    if (Game1.activeClickableMenu is AnimalQueryMenu || Game1.activeClickableMenu is DialogueBox)
                        Game1.exitActiveMenu();
                }
                else if (needPet)
                    stats.TooLate++;
            }
        }
    }
}
