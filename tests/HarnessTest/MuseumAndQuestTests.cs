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
}
