using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;

partial class Program
{
    static void TestPrepareField(Type actions, JsonArray schema)
    {
        var fn = schema.Single(s => (string)s["function"]["name"] == "prepare_field")["function"];
        Check(new[] { "mode", "plan_id", "near", "count", "x1", "y1", "x2", "y2", "seed", "sprinkler", "scarecrow", "path" }
            .All(k => fn["parameters"]["properties"][k] != null), "整田工具声明区域、设备、留路和续跑参数");

        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        object Call(string name, params object[] args) => actions.GetMethod(name, flags).Invoke(null, args);
        object Prop(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
        List<Point> Tiles(object value, string name) => (List<Point>)Prop(value, name);
        List<Point> Square(int r) => (from y in Enumerable.Range(-r, 2 * r + 1)
                                      from x in Enumerable.Range(-r, 2 * r + 1)
                                      select new Point(x, y)).ToList();
        var cross = new List<Point> { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        var area = new Rectangle(0, 0, 12, 12);
        var walk = (from x in Enumerable.Range(-1, 14) from y in Enumerable.Range(-1, 14) select new Point(x, y)).ToHashSet();
        var usable = walk.Where(p => area.Contains(p)).ToHashSet();
        var entry = new Point(0, -1);
        var existing = new HashSet<Point>();
        var guards = new Dictionary<Point, int>();
        object Layout(List<Point> offsets, int radius, bool raised, int count) =>
            Call("LayoutField", area, usable, walk, offsets, existing, guards, radius, raised, raised, entry, count, usable, new List<Point>(), 0);
        bool Protect(Point c, int radius, Point p) => (bool)Call("CrowProtects", c, radius, p);

        foreach (var offsets in new[] { cross, Square(1), Square(2), Square(3) })
        {
            var plan = Layout(offsets, 0, false, 48);
            var plants = Tiles(plan, "Plants");
            var heads = Tiles(plan, "Sprinklers");
            var wetted = new HashSet<Point>();
            bool disjoint = true;
            foreach (var head in heads)
            {
                var footprint = offsets.Select(d => head + d).ToList();
                disjoint &= !wetted.Overlaps(footprint) && !heads.Any(p => p != head && footprint.Contains(p));
                wetted.UnionWith(footprint);
            }
            Check(plants.Count == 48 && disjoint && plants.All(wetted.Contains) && !plants.Intersect(heads).Any(),
                $"覆盖模板 {offsets.Count} 格：够目标、逐格去重、设备格不种（含扩大配件）");
            Check(plants.SequenceEqual(Tiles(Layout(offsets, 0, false, 48), "Plants")), "同一组输入重算布局一致");
        }

        var mixed = Call("LayoutField", area, usable, walk, Square(1), existing, guards, 0, false, false, entry,
            40, usable, Square(2), 1);
        var mixedWater = new HashSet<Point>();
        bool mixedDisjoint = true;
        foreach (var p in Tiles(mixed, "Sprinklers"))
        {
            var template = Tiles(mixed, "BoostedSites").Contains(p) ? Square(2) : Square(1);
            var cover = template.Select(d => p + d).ToList();
            mixedDisjoint &= !mixedWater.Overlaps(cover);
            mixedWater.UnionWith(cover);
        }
        Check(Tiles(mixed, "BoostedSites").Count == 1 && Tiles(mixed, "Sprinklers").Count > 1 && mixedDisjoint
            && Tiles(mixed, "Plants").All(mixedWater.Contains), "只有一只加压喷头时，其他预留位按同级无配件覆盖");
        Check(!Protect(Point.Zero, 5, new Point(3, 4)) && Protect(Point.Zero, 5, new Point(3, 3)), "稻草人半径边界严格小于");
        existing.UnionWith(Square(2).Select(d => new Point(6, 6) + d));
        usable.Remove(new Point(6, 6));
        var borrowed = Layout(Square(1), 0, false, 16);
        Check(Tiles(borrowed, "Sprinklers").Count == 0 && Tiles(borrowed, "Plants").All(existing.Contains), "可以只种已有喷头浇到的空格");
        var extended = Layout(Square(1), 0, false, 40);
        Check(Tiles(extended, "Sprinklers").All(h => !existing.Overlaps(Square(1).Select(d => h + d))), "扩田避让原有加压喷头的完整覆盖");
        guards[new Point(6, 6)] = 17;
        Check(Tiles(Layout(cross, 9, true, 24), "Crows").Count == 0, "已有大半径稻草人时不重复占地");
        guards.Clear();
        existing.Clear();

        foreach (bool raised in new[] { false, true })
        {
            foreach (var offsets in new[] { new List<Point>(), cross, Square(2) })
            {
                var plan = Layout(offsets, 4, raised, 40);
                var plants = Tiles(plan, "Plants");
                var devices = Tiles(plan, "Sprinklers").Concat(Tiles(plan, "Crows")).ToHashSet();
                var paths = Tiles(plan, "Paths").ToHashSet();
                var blocked = devices.ToHashSet();
                if (raised)
                    blocked.UnionWith(plants);
                var permanent = paths.ToHashSet();
                if (!raised)
                    permanent.UnionWith(plants);
                var reached = (Dictionary<Point, Point>)Call("FieldRoutes", entry, permanent, blocked);
                Check(plants.Count > 0 && plants.Concat(devices).All(t => cross.Any(d => reached.ContainsKey(t + d)))
                    && reached.Count == permanent.Count && !paths.Overlaps(devices) && !paths.Overlaps(plants),
                    $"从入口可到每个作物/设备的相邻站位，棚架={raised}，模板={offsets.Count}");
                Check(plants.All(p => Tiles(plan, "Crows").Any(c => Protect(c, 4, p))), "补放稻草人后所有种植格均受保护");
            }
        }

        // 墙两侧都是可锄地，入口在左侧。连通性失败的右侧不能被算进结果。
        walk.RemoveWhere(p => p.X == 4);
        usable.IntersectWith(walk);
        var divided = Layout(new List<Point>(), 0, false, 70);
        Check(Tiles(divided, "Plants").Count > 0 && Tiles(divided, "Plants").All(p => p.X < 4), "不规划隔墙够不到的地");
        var empty = LayoutFieldWithBlockedEntry();
        Check(Tiles(empty, "Plants").Count == 0, "入口被封不生成种植格");
        object LayoutFieldWithBlockedEntry()
        {
            walk.Remove(entry);
            return Layout(cross, 0, true, 20);
        }

        var bookType = actions.GetNestedType("FieldBook", BindingFlags.NonPublic);
        var jobType = actions.GetNestedType("FieldJob", BindingFlags.NonPublic);
        object book = Activator.CreateInstance(bookType);
        divided.GetType().GetProperty("Id").SetValue(divided, 1);
        var added = Call("RegisterField", book, divided);
        Check(((IList)Prop(book, "Plans")).Count == 0 && ((IList)Prop(added, "Plans")).Count == 1
            && (int)Prop(added, "NextId") == 2, "新增待办保留旧快照并推进编号");
        var jobs = (IList)Prop(divided, "Jobs");
        foreach (string task in new[] { "clear", "hoe", "sprinkler", "crow", "seed" })
            jobs.Add(Activator.CreateInstance(jobType, new object[] { new Point(4, 5), task, null }));
        var finished = new HashSet<string> { "clear", "hoe", "sprinkler" };
        Func<object, bool> done = j => finished.Contains((string)Prop(j, "Task"));
        Func<Point, bool> bare = p => false;
        var pending = (IList)Call("RemainingFieldWork", divided, done, bare);
        Check(jobs.Count == 5 && pending.Count == 2 && pending.Cast<object>().All(j => !done(j)), "实地完成的任务划掉，缺稻草人和种子的格子保留");
        divided.GetType().GetProperty("Jobs").SetValue(divided, pending);
        Check(((IList)Call("RemainingFieldWork", divided, done, bare)).Count == 2, "多次对账不重复扣减");
        Func<Point, bool> dried = p => true;
        Func<object, bool> unfinished = j => false;
        var rebuilt = ((IList)Call("RemainingFieldWork", divided, unfinished, dried)).Cast<object>().ToList();
        Check((string)Prop(rebuilt.First(), "Task") == "hoe" && rebuilt.Count == 3, "种子待办遇到退耕先补锄，按阶段顺序继续");
        divided.GetType().GetProperty("BeforeWork").SetValue(divided, jobs);
        divided.GetType().GetProperty("WorkDay").SetValue(divided, 12);
        var removed = Call("FinishField", added, 1);
        var reload = (IList)Prop(Call("RestoredFields", removed, 12), "Plans");
        Check(reload.Count == 1 && ((IList)Prop(reload[0], "Jobs")).Count == 5 && ((IList)Prop(divided, "Jobs")).Count == 2,
            "未过夜退出后恢复当日施工前清单，保持原快照供再次读档");
        Check(((IList)Prop(Call("RestoredFields", removed, 13), "Plans")).Count == 0, "过夜存档后不恢复已完工的田");
        Check(((IList)Prop(removed, "Plans")).Count == 0 && (int)Prop(removed, "NextId") == 2
            && ((IList)Prop(added, "Plans")).Count == 1, "完工移除待办，编号不回收");
    }
}
