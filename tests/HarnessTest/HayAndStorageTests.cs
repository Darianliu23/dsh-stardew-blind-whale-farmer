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

        // ── 大箱子原地升级普通箱子 ──
        var specialTypeEnum = typeof(StardewValley.Objects.Chest.SpecialChestTypes);
        string UpgradeErr(string bigId, string targetId, bool playerChest = true, bool giftbox = false, bool fridge = false,
            StardewValley.Objects.Chest.SpecialChestTypes special = StardewValley.Objects.Chest.SpecialChestTypes.None, string targetName = "宝箱")
            => (string)Call("ChestUpgradeProblem", bigId, targetId, playerChest, giftbox, fridge, special, targetName);

        Check(UpgradeErr("(BC)BigChest", "(BC)130") == null, "大箱子升级普通木箱：允许");
        Check(UpgradeErr("(BC)BigStoneChest", "(BC)232", targetName: "石箱") == null, "大石箱升级普通石箱：允许");

        var woodWithStone = UpgradeErr("(BC)BigStoneChest", "(BC)130");
        Check(woodWithStone != null && woodWithStone.Contains("类型配不上") && woodWithStone.Contains("大箱子"),
            "大石箱升级木箱：类型不对拒绝：" + woodWithStone);
        var stoneWithWood = UpgradeErr("(BC)BigChest", "(BC)232", targetName: "石箱");
        Check(stoneWithWood != null && stoneWithWood.Contains("类型配不上") && stoneWithWood.Contains("大石箱"),
            "大箱子升级石箱：类型不对拒绝：" + stoneWithWood);

        var alreadyBig = UpgradeErr("(BC)BigChest", "(BC)BigChest", special: StardewValley.Objects.Chest.SpecialChestTypes.BigChest, targetName: "大箱子");
        Check(alreadyBig != null && alreadyBig.Contains("已经是大箱子"), "目标已经是大箱子：拒绝：" + alreadyBig);
        var alreadyBigStone = UpgradeErr("(BC)BigStoneChest", "(BC)BigStoneChest", special: StardewValley.Objects.Chest.SpecialChestTypes.BigChest, targetName: "大石箱");
        Check(alreadyBigStone != null && alreadyBigStone.Contains("已经是大箱子"), "目标已经是大石箱：拒绝：" + alreadyBigStone);

        var fridgeErr = UpgradeErr("(BC)BigChest", "(BC)216", fridge: true, targetName: "迷你冰箱");
        Check(fridgeErr != null && fridgeErr.Contains("冰箱"), "冰箱拒绝升级：" + fridgeErr);
        var junimoErr = UpgradeErr("(BC)BigChest", "(BC)256", special: StardewValley.Objects.Chest.SpecialChestTypes.JunimoChest, targetName: "祝尼魔箱");
        Check(junimoErr != null && junimoErr.Contains("特殊箱子"), "祝尼魔箱拒绝升级：" + junimoErr);
        var miniBinErr = UpgradeErr("(BC)BigChest", "(BC)248", special: StardewValley.Objects.Chest.SpecialChestTypes.MiniShippingBin, targetName: "迷你出货箱");
        Check(miniBinErr != null && miniBinErr.Contains("特殊箱子"), "迷你出货箱拒绝升级：" + miniBinErr);
        var giftErr = UpgradeErr("(BC)BigChest", "(BC)130", giftbox: true, targetName: "礼物盒");
        Check(giftErr != null && giftErr.Contains("特殊箱子"), "礼物箱拒绝升级：" + giftErr);
        var nonBigErr = UpgradeErr("(BC)130", "(BC)130");
        Check(nonBigErr != null && nonBigErr.Contains("不是大箱子"), "非大箱子物品拒绝升级：" + nonBigErr);

        // 箱子类型标签与储藏区汇总
        string TypeLabel(string id, StardewValley.Objects.Chest.SpecialChestTypes sp, int cap, bool fr = false)
            => (string)Call("FormatChestTypeLabel", id, sp, cap, fr);
        Check(TypeLabel("(BC)130", StardewValley.Objects.Chest.SpecialChestTypes.None, 36) == "普通箱子（36 格，可用大箱子升级）", "木箱标签");
        Check(TypeLabel("(BC)232", StardewValley.Objects.Chest.SpecialChestTypes.None, 36) == "石箱（36 格，可用大石箱升级）", "石箱标签");
        Check(TypeLabel("(BC)BigChest", StardewValley.Objects.Chest.SpecialChestTypes.BigChest, 70) == "大箱子（70 格）", "大箱子标签");
        Check(TypeLabel("(BC)BigStoneChest", StardewValley.Objects.Chest.SpecialChestTypes.BigChest, 70) == "大石箱（70 格）", "大石箱标签");
        Check(TypeLabel("(BC)216", StardewValley.Objects.Chest.SpecialChestTypes.None, 36, true) == "冰箱", "冰箱标签");

        var summary = (string)Call("FormatStorageChestSummary", new object[] { new[] { "普通箱子（36 格，可用大箱子升级）", "普通箱子（36 格，可用大箱子升级）", "大箱子（70 格）" } });
        Check(summary == "（普通箱子（36 格，可用大箱子升级）×2、大箱子（70 格））", "储藏区箱子类型汇总：" + summary);

        // 不强制提示：只有背包有大箱子且存在匹配的普通箱子时才给提示
        var woodHint = (string)Call("FormatBigChestUpgradeHint", true, false, 2, 0);
        Check(woodHint != null && woodHint.Contains("原地升级") && woodHint.Contains("不用搬") && woodHint.Contains("另放空位"),
            "有大箱子+普通箱子时给不强制提示：" + woodHint);
        var stoneHint = (string)Call("FormatBigChestUpgradeHint", false, true, 0, 1);
        Check(stoneHint != null && stoneHint.Contains("大石箱") && stoneHint.Contains("石箱"),
            "有大石箱+石箱时给不强制提示：" + stoneHint);
        Check(Call("FormatBigChestUpgradeHint", true, false, 0, 2) == null, "有大箱子但只有石箱时不给不匹配提示");
        Check(Call("FormatBigChestUpgradeHint", false, false, 3, 1) == null, "背包没有大箱子时不提示");

        // 离线验证原版升级时的物品与颜色保留（同 Chest.performObjectDropInAction 逻辑）
        var oldChest = new StardewValley.Objects.Chest();
        oldChest.playerChest.Value = true;
        oldChest.ItemId = "130";
        oldChest.playerChoiceColor.Value = Color.CornflowerBlue;
        oldChest.Tint = Color.Wheat;
        oldChest.modData["testKey"] = "keepMe";
        var item1 = new StardewValley.Object { ItemId = "388", Name = "Wood", Stack = 99 };
        var item2 = new StardewValley.Object { ItemId = "390", Name = "Stone", Stack = 42 };
        oldChest.Items.Add(item1);
        oldChest.Items.Add(item2);

        var newChest = new StardewValley.Objects.Chest();
        newChest.playerChest.Value = true;
        newChest.ItemId = "BigChest";
        newChest.SpecialChestType = StardewValley.Objects.Chest.SpecialChestTypes.BigChest;
        newChest.netItems.Value = oldChest.netItems.Value;
        newChest.playerChoiceColor.Value = oldChest.playerChoiceColor.Value;
        newChest.Tint = oldChest.Tint;
        newChest.modData.CopyFrom(oldChest.modData);

        Check(newChest.GetActualCapacity() == 70
            && newChest.Items.Count == 2
            && newChest.Items[0].ItemId == "388" && newChest.Items[0].Stack == 99
            && newChest.Items[1].ItemId == "390" && newChest.Items[1].Stack == 42
            && newChest.playerChoiceColor.Value == Color.CornflowerBlue
            && newChest.Tint == Color.Wheat
            && newChest.modData["testKey"] == "keepMe",
            "原地升级后大箱子容量 70 格且物品、数量、颜色和 modData 完整保留");

        // place_item 工具说明包含原地升级提示（不强制）
        var placeDesc = schema.Single(t => t["function"]["name"].GetValue<string>() == "place_item")["function"]["description"].GetValue<string>();
        Check(placeDesc.Contains("大箱子") && placeDesc.Contains("原地升级") && placeDesc.Contains("另放空位"),
            "place_item 说明包含大箱子原地升级且不强制：" + placeDesc);
    }
}

