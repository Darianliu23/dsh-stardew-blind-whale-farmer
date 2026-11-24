using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;

partial class Program
{
    static void TestHayAndStorage(Type actions, JsonArray schema, HashSet<string> readOnly)
    {
        object Call(string name, params object[] args) => actions
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

        var a = new Point(1, 1);
        var b = new Point(3, 3);
        var c = new Point(5, 5);
        var lone = new Point(20, 20);
        var groups = (List<List<Point>>)Call("ClusterChestTiles", new[] { c, lone, a, b, a });
        Check(groups.Count == 2 && groups[0].ToHashSet().SetEquals(new[] { a, b, c })
            && groups[0].Count == 3 && groups[1].SequenceEqual(new[] { lone }),
            "箱子聚类：两格斜角能连成一组，重复坐标只算一次，远处单独成组");
        Check(((List<List<Point>>)Call("ClusterChestTiles", Array.Empty<Point>())).Count == 0, "没有箱子时没有储藏区");

        Check(Call("ChestDistanceHint", new Point(10, 10), new[] { new Point(10, 10) }) == null, "箱子距离提醒：只有自己时不提醒");
        Check(Call("ChestDistanceHint", new Point(10, 10), new[] { new Point(10, 10), new Point(13, 12) }) == null, "箱子距离提醒：3 格内不提醒");
        var farHint = (string)Call("ChestDistanceHint", new Point(10, 10), new[] { new Point(10, 10), new Point(15, 10), new Point(30, 30) });
        Check(farHint != null && farHint.Contains("(15,10)") && farHint.Contains("5 格") && farHint.Contains("storage_info"),
            "箱子距离提醒：超过 3 格时提醒最近箱子距离和 storage_info：" + farHint);

        var nearOrder = ((IEnumerable<Point>)Call("OrderAutoPlaceCandidates", new Point(10, 10), new[] { new Point(12, 10) })).ToList();
        Check(nearOrder[0] == new Point(11, 10), "自动找位：箱子在右侧时优先挑右侧紧邻已有箱子的格");
        var farOrder = ((IEnumerable<Point>)Call("OrderAutoPlaceCandidates", new Point(10, 10), new[] { new Point(50, 50) })).ToList();
        Check(farOrder[0] == new Point(10, 9), "自动找位：已有箱子很远时保持默认上下左右优先顺序");

        var allowed = new HashSet<Point> { new(8, 9), new(9, 9), new(8, 12), new(10, 11), new(7, 9), new(8, 10) };
        var edges = (Dictionary<string, List<Point>>)Call("StorageExpansionTiles", new Rectangle(8, 10, 2, 2),
            (Func<Point, bool>)allowed.Contains);
        Check(edges.Count == 4 && edges["上"].SequenceEqual(new[] { new Point(8, 9), new Point(9, 9) })
            && edges["下"].SequenceEqual(new[] { new Point(8, 12) }) && edges["左"].Count == 0
            && edges["右"].SequenceEqual(new[] { new Point(10, 11) }),
            "储藏区扩充：按上下左右列合规格子，范围内和角外的格子不计");

        var grass = new HashSet<Point> { new(5, 5), new(6, 5), new(7, 5), new(10, 10), new(13, 13) };
        bool Keep(Point p, params Point[] doors) => (bool)Call("KeepHayGrass", p, grass, doors);
        Check(Keep(new Point(5, 5), new Point(2, 2)) && !Keep(new Point(5, 5), new Point(1, 2))
            && Keep(new Point(7, 5)) && Keep(new Point(13, 13)),
            "留草：保留动物门三格内的草、固定留下的草和单独一格的草");

        var ship = new (string name, bool reserved)[]
        {
            ("防风草", true), ("防风草", false), ("沙丁鱼", true), ("花椰菜", false), ("土豆", false),
            (null, false), ("萝卜", false), ("草莓", false), ("郁金香", false), ("防风草", false)
        };
        var candidates = (List<(string name, int slot)>)Call("BedtimeShipmentCandidates", ship);
        Check(candidates.Select(i => i.slot).SequenceEqual(new[] { 1, 3, 4, 6, 7 }),
            "睡前出货：排除保留格和空格，同名只列一次，最多五样，并给可出货那格的格号");
        Check(((List<(string name, int slot)>)Call("BedtimeShipmentCandidates", new[] { ("沙丁鱼", true) })).Count == 0,
            "睡前出货：全部保留时没有候选");

        JsonNode Params(string name) => schema.Single(t => t["function"]["name"].GetValue<string>() == name)["function"]["parameters"];
        var hay = Params("cut_hay");
        Check(hay["required"] is JsonArray { Count: 0 } && new[] { "target", "max_tiles", "x1", "y1", "x2", "y2" }
            .All(p => hay["properties"][p]?["type"]?.GetValue<string>() == "integer"), "cut_hay 注册可选目标、格数和矩形坐标");
        Check(readOnly.Contains("storage_info") && Params("storage_info")["properties"]["near"] != null
            && Params("place_item")["properties"]["near"]?["type"]?.GetValue<string>() == "string",
            "storage_info 是只读工具，和 place_item 一样能传 near 锚点");

        // store_items / take_items 的 items 参数：支持数组、{名字: 数量}、对象数组、以及按数量存
        var tTools = actions.Assembly.GetType("BlindWhaleFarmer.Tools");
        var normItems = tTools.GetMethod("NormalizeItemsArg", BindingFlags.Static | BindingFlags.NonPublic);
        (bool ok, string val, string err) Norm(string json)
        {
            object[] args = { JsonNode.Parse(json), null, null };
            bool ok = (bool)normItems.Invoke(null, args);
            return (ok, (string)args[1], (string)args[2]);
        }
        var a1 = Norm("[\"铁矿石\",\"铜矿石\"]");
        Check(a1.ok && a1.val == "铁矿石,铜矿石", "items 接受字符串数组：" + a1.val);
        var o1 = Norm("{\"铁矿石\":5,\"铜矿石\":10}");
        Check(o1.ok && o1.val == "铁矿石x5,铜矿石x10", "items 接受 {名字: 数量}：" + o1.val);
        var o2 = Norm("[{\"name\":\"铁矿石\",\"count\":3},{\"item\":\"煤炭\"}]");
        Check(o2.ok && o2.val == "铁矿石x3,煤炭", "items 接受对象数组：" + o2.val);
        var s1 = Norm("\"[\\\"铁矿石\\\",\\\"铜矿石\\\"]\"");
        Check(s1.ok && s1.val == "铁矿石,铜矿石", "items 接受包成字符串的 JSON 数组");
        foreach (var bad in new[] { "[]", "{}", "[123]", "{\"铁矿石\":0}", "{\"铁矿石\":\"abc\"}", "123", "\"[铁矿石\"" })
        {
            var r = Norm(bad);
            Check(!r.ok && r.err.Contains("请传逗号分隔字符串") && r.err.Contains("[\"铁矿石\"") && r.err.Contains("{\"铁矿石\": 5"),
                $"items 传 {bad} 时报错写清正确写法");
        }

        var planStore = actions.GetMethod("PlanStoreCounts", BindingFlags.Static | BindingFlags.NonPublic);
        var bag = new (string name, string displayName, bool isTool, int stack)[]
        {
            ("Iron Ore", "铁矿石", false, 20),
            ("Copper Ore", "铜矿石", false, 15),
            ("Bamboo Pole", "竹鱼竿", true, 1),
        };
        var pArr = (Dictionary<int, int>)planStore.Invoke(null, new object[] { "铁矿石,铜矿石", bag });
        Check(pArr.Count == 2 && pArr[0] == 20 && pArr[1] == 15, "PlanStoreCounts 数组写法全存对应格，不存鱼竿");
        var pCnt = (Dictionary<int, int>)planStore.Invoke(null, new object[] { "铁矿石x5,竹鱼竿", bag });
        Check(pCnt.Count == 2 && pCnt[0] == 5 && pCnt[2] == 1, "PlanStoreCounts 指定数量只存 5 个，点全名的工具也存");
    }
}

