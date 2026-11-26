using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

partial class Program
{
    static void TestMuseumInfo(Assembly asm, JsonArray schema, HashSet<string> readOnly)
    {
        var parameters = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "museum_info")?["function"]["parameters"];
        var props = parameters?["properties"];
        Check(parameters?["required"] is JsonArray { Count: 0 }
            && props?["section"]?["type"]?.GetValue<string>() == "string"
            && props["section"]["default"]?.GetValue<string>() == "missing"
            && props["section"]["enum"] is JsonArray sections
            && sections.Select(v => v.GetValue<string>()).SequenceEqual(new[] { "missing", "donated", "all" })
            && props["kind"]?["type"]?.GetValue<string>() == "string"
            && props["kind"]["enum"] is JsonArray kinds
            && kinds.Select(v => v.GetValue<string>()).SequenceEqual(new[] { "minerals", "artifacts" })
            && readOnly.Contains("museum_info"), "museum_info 的 schema 参数、默认值和只读注册");

        // 只传名字和已捐标记，离线不建物品。
        var knowledge = asm.GetType("BlindWhaleFarmer.Knowledge");
        var pieceType = knowledge.GetNestedType("MuseumPiece", BindingFlags.NonPublic);
        var pieces = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pieceType));
        void Add(string name, bool mineral, bool donated) =>
            pieces.Add(Activator.CreateInstance(pieceType, name, mineral, donated));
        Add("石英", true, true);
        Add("紫水晶", true, false);
        Add("翡翠", true, false);
        Add("矮人卷轴", false, false);
        var format = knowledge.GetMethod("MuseumCollectionText", BindingFlags.Static | BindingFlags.NonPublic);
        string Show(string section, string kind = null) =>
            (string)format.Invoke(null, new object[] { pieces, section, kind });

        string missing = Show("missing");
        Check(missing.Contains("博物馆已捐 1/4 件") && missing.Contains("矿物：已捐 1 件，还缺 2 件")
            && missing.Contains("紫水晶、翡翠") && missing.Contains("矮人卷轴") && !missing.Contains("石英"),
            "博物馆缺件名单按类别计数，同类名称放在一行");
        string donated = Show("donated", "minerals");
        Check(donated.Contains("博物馆已捐 1/4 件") && donated.Contains("已捐：石英")
            && !donated.Contains("文物") && !donated.Contains("紫水晶"), "只看已捐矿物时仍显示博物馆总进度");
        string all = Show("all", "artifacts");
        Check(all.Contains("还缺：矮人卷轴") && all.Contains("已捐：无") && !all.Contains("矿物"),
            "文物的全部名单包含缺件和空的已捐名单");

        for (int i = 0; i < 15; i++)
            Add($"矿物{i:D2}", true, false);
        var lines = Show("missing", "minerals").Split('\n').Where(line => line.StartsWith("  还缺：")).ToArray();
        Check(lines.Length == 2 && lines.Sum(line => line.Count(c => c == '、') + 1) == 17,
            "博物馆缺件较多时分行列全");
    }

    static void TestQuestLabels(Type actions)
    {
        var format = actions.GetMethod("QuestLabels", BindingFlags.Static | BindingFlags.NonPublic);
        string Show(bool timed, int days, bool? repeatable) =>
            (string)format.Invoke(null, new object[] { timed, days, repeatable });

        Check(Show(true, 2, true) == "【限时 剩2天】【可重复】", "限时和可重复标签可以同时显示");
        Check(Show(false, 0, false) == "【一次性】", "没有运行中期限时只显示一次性标签");
        Check(Show(true, 1, false) == "【限时 剩1天】【一次性】", "限时的一次性任务显示两个标签");
        Check(Show(true, -1, null) == "【限时 剩0天】【未知】", "期限不会显示负天数，重复性可标未知");
    }

    static void TestVaultBundles(Assembly asm, Type actions, JsonArray schema)
    {
        var parameters = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "donate_bundles")?["function"]["parameters"];
        var props = parameters?["properties"];
        Check(parameters?["required"] is JsonArray { Count: 0 }
            && props?["pay_vault"]?["type"]?.GetValue<string>() == "boolean"
            && props?["budget"]?["type"]?.GetValue<string>() == "integer"
            && props?["confirm"]?["type"]?.GetValue<string>() == "boolean",
            "donate_bundles 的 schema 包含 pay_vault / budget / confirm 可选参数");

        var knowledge = asm.GetType("BlindWhaleFarmer.Knowledge");
        var ingType = typeof(StardewValley.Menus.BundleIngredientDescription);
        var goldIng = new StardewValley.Menus.BundleIngredientDescription("-1", 2500, 2500, false);
        var itemIng = new StardewValley.Menus.BundleIngredientDescription("24", 1, 0, false);

        var vaultCost = knowledge.GetMethod("VaultCost", BindingFlags.Static | BindingFlags.Public,
            null, new[] { typeof(int), typeof(IReadOnlyList<StardewValley.Menus.BundleIngredientDescription>) }, null);
        Check((int)vaultCost.Invoke(null, new object[] { 4, new[] { goldIng } }) == 2500, "金库（area=4）的 -1 材料识别为 2500g");
        Check((int)vaultCost.Invoke(null, new object[] { 0, new[] { goldIng } }) == 0, "非金库区域不按交钱收集包算");
        Check((int)vaultCost.Invoke(null, new object[] { 4, new[] { itemIng } }) == 0, "非金币材料不按交钱收集包算");

        var ingName = knowledge.GetMethod("IngredientName", BindingFlags.Static | BindingFlags.Public);
        Check((string)ingName.Invoke(null, new object[] { goldIng }) == "2500g", "金库收集包材料名显示为 2500g");

        var plan = actions.GetMethod("PlanVaultPayments", BindingFlags.Static | BindingFlags.NonPublic);
        var candidates = new (int Index, string Name, int Cost)[]
        {
            (26, "25,000", 25000),
            (24, "5,000", 5000),
            (23, "2,500", 2500),
            (25, "10,000", 10000),
        };
        List<(int Index, string Name, int Cost)> Run(int money, int? budget) =>
            (List<(int Index, string Name, int Cost)>)plan.Invoke(null, new object[] { candidates, money, budget });

        var p1 = Run(8000, null);
        Check(p1.Select(x => x.Cost).SequenceEqual(new[] { 2500, 5000 }), "金库交钱按从便宜到贵挑选（8000g 选 2500+5000）");
        var p2 = Run(50000, 6000);
        Check(p2.Select(x => x.Cost).SequenceEqual(new[] { 2500 }), "金库交钱受 budget 上限限制（6000g 只选 2500）");
        var p3 = Run(1000, null);
        Check(p3.Count == 0, "钱不够最便宜的金库收集包时一个都不选");
        var p4 = Run(42500, null);
        Check(p4.Select(x => x.Cost).SequenceEqual(new[] { 2500, 5000, 10000, 25000 }), "42500g 一次按顺序选齐全部 4 个金库收集包");
    }
}
