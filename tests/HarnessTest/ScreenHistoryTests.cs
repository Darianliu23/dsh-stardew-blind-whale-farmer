using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

partial class Program
{
    static void TestScreenHistory(Assembly asm)
    {
        var type = asm.GetType("BlindWhaleFarmer.ScreenHistory");
        var history = Activator.CreateInstance(type, nonPublic: true);
        var hudChanged = type.GetMethod("HudChanged");
        bool Changed(bool known, int before, int after) =>
            (bool)hudChanged.Invoke(null, new object[] { known, ("石头", before), ("石头", after) });
        Check(Changed(false, 1, 1) && !Changed(true, 1, 1) && Changed(true, 1, 3),
            "HUD 新出现和数量叠加要入历史，同一条只刷新时去重");

        void Add(string text, object key = null) =>
            type.GetMethod("Add").Invoke(history, new object[] { "06:00", "提示", text, key });
        List<string> Take() => (List<string>)type.GetMethod("TakeRecent").Invoke(history, null);
        var stone = new object();
        Add("石头");
        Add("石头 ×2", stone);
        Add("任务更新");
        Add("石头 ×3", stone);
        var merged = Take();
        Check(merged.Count == 3 && merged[1].EndsWith("任务更新") && merged[2].EndsWith("石头 ×3") && Take().Count == 0,
            "同一 HUD 对象在两次读取之间只留最新数量，独立消息仍保留");
        Add("石头 ×4", stone);
        Check(Take().Single().EndsWith("石头 ×4"), "已经读过的 HUD 数量变化也会再次报告");
        for (int i = 0; i < 11; i++)
            Add($"第{i}条");
        Check(Take().Select(s => s.Split('：')[1]).SequenceEqual(Enumerable.Range(6, 5).Select(i => $"第{i}条")),
            "最近提示溢出后仍按顺序取最后五条");
        Add(new string('字', 170));
        Check(Take().Single().Split('：')[1].Length == 160, "历史文字限长");
        Add("读档前");
        type.GetMethod("Clear").Invoke(history, null);
        Check(Take().Count == 0, "清空历史时也清空读取进度");

        var extract = type.GetMethod("ExtractEventLines");
        var commands = new[]
        {
            "message \"这一句已经播过\"", "pause 100", "SPEAK Abigail \"等一下！$h#路在左边 / 右边。\"",
            "message \"你好，@。\"", "speak Sam \"Strings\\\\Events:Other\"", "message \"[LocalizedText X]\"",
            "speak Sam \"$q 1 2 问题\"", "message \"看，那里有光。\"", "end", "message \"结束后不读\"",
        };
        var lines = (List<string>)extract.Invoke(null, new object[] { commands, 1 });
        Check(lines.SequenceEqual(new[] { "Abigail：等一下！", "Abigail：路在左边 / 右边。", "看，那里有光。" }),
            "跳过剧情只提取剩余字面台词，略过分支标记、替换和值为翻译键的文字");
        lines = (List<string>)extract.Invoke(null, new object[] { Enumerable.Repeat("message \"剧情\"", 12).ToArray(), 0 });
        Check(lines.Count == 8, "跳过剧情最多列出八句");
    }
}
