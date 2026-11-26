using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;

partial class Program
{
    static void TestFruitTreeAndSprinkler(Assembly asm, Type actions, JsonArray schema)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        object Call(Type t, string name, params object[] args) => t.GetMethod(name, flags).Invoke(null, args);

        // ── 1. 果树显示名与收获摘要 ──
        var observer = asm.GetType("BlindWhaleFarmer.Observer");
        Check((string)Call(observer, "FormatFruitTreeLabel", "苹果", 3) == "苹果树（3 个果子）", "果树有果子时显示数量");
        Check((string)Call(observer, "FormatFruitTreeLabel", "苹果", 0) == "苹果树", "果树没果子时不加括号");
        Check((string)Call(observer, "FormatFruitTreeLabel", "", 2) == "果树（2 个果子）", "未知果树名有果子时仍显示数量");
        Check((string)Call(observer, "FormatFruitTreeLabel", "", 0) == "果树", "未知果树名没果子时显示“果树”");

        var mixedHarvest = (string)Call(actions, "FormatHarvestSummary", 10, 0, 2,
            new Dictionary<string, int> { ["苹果"] = 3, ["石榴"] = 2 });
        Check(mixedHarvest.Contains("作物 10 株") && mixedHarvest.Contains("果树 2 棵（苹果 3 个、石榴 2 个）"),
            "收获摘要分开报作物与果树明细：" + mixedHarvest);

        var treeOnlyHarvest = (string)Call(actions, "FormatHarvestSummary", 0, 0, 1,
            new Dictionary<string, int> { ["樱桃"] = 3 });
        Check(treeOnlyHarvest == "收获了：果树 1 棵（樱桃 3 个）", "只收果树时的摘要：" + treeOnlyHarvest);

        var allHarvest = (string)Call(actions, "FormatHarvestSummary", 5, 2, 1,
            new Dictionary<string, int> { ["橙子"] = 1 });
        Check(allHarvest.Contains("作物 5 株") && allHarvest.Contains("季节种子野菜 2 个") && allHarvest.Contains("果树 1 棵（橙子 1 个）"),
            "作物+野菜+果树全部列出：" + allHarvest);

        Check((string)Call(actions, "FormatHarvestSummary", 0, 0, 0, new Dictionary<string, int>()) == "收获了 0 株",
            "都没收到时的摘要");

        var harvestDesc = schema.First(t => t["function"]["name"].GetValue<string>() == "harvest_crops")["function"]["description"].GetValue<string>();
        Check(harvestDesc.Contains("果树") && harvestDesc.Contains("3 个"), "harvest_crops 工具说明提到果树和最多挂 3 个：" + harvestDesc);

        // ── 2. 洒水器范围内杂草/杂物工具映射与保护规则 ──
        string ToolFor(char ch) => (string)Call(actions, "SprinklerClutterTool", ch);
        Check(ToolFor('w') == "scythe" && ToolFor('"') == "scythe", "洒水器范围内杂草和草用镰刀");
        Check(ToolFor('o') == "pickaxe", "洒水器范围内小石头用镐");
        Check(ToolFor('/') == "axe", "洒水器范围内小树枝用斧");
        foreach (char keep in new[] { 'S', 'T', 't', 'F', 'c', 'v', '!', 'X', '&', '_', '?', 'b', '\0' })
            Check(ToolFor(keep) == null, $"洒水器范围内不动 '{keep}'");

        // ── 3. 草在 clear_area all=true 与洒水器范围内的处理 ──
        bool CutGrass(bool wholeMap, bool inSprinkler) => (bool)Call(actions, "ShouldCutGrassInClearArea", wholeMap, inSprinkler);
        Check(!CutGrass(true, false), "clear_area all=true 范围外不割草（留干草）");
        Check(CutGrass(true, true), "clear_area all=true 洒水器覆盖范围内可割草");
        Check(CutGrass(false, false) && CutGrass(false, true), "指定范围清理时割草");

        // ── 4. 缺少工具提示与 get_state 洒水器杂草提示 ──
        Check(Call(actions, "FormatMissingClutterTools", new Dictionary<string, int>()) == null, "不缺工具时不提示");
        var missing = (string)Call(actions, "FormatMissingClutterTools",
            new Dictionary<string, int> { ["scythe"] = 2, ["pickaxe"] = 1 });
        Check(missing != null && missing.Contains("跳过 3 格") && missing.Contains("镰刀（2 格）") && missing.Contains("镐子（1 格）"),
            "缺工具时列出缺什么工具和格数：" + missing);

        Check(Call(actions, "FormatSprinklerClutterHint", new List<(Point, int)>()) == null, "洒水器范围干净时不提示");
        var singleHint = (string)Call(actions, "FormatSprinklerClutterHint", new List<(Point, int)> { (new Point(40, 20), 3) });
        Check(singleHint == "洒水器 (40,20) 范围里有 3 格被杂草/杂物占着，till_area 会顺手清掉",
            "单个洒水器杂草提示格式：" + singleHint);
        var multiHint = (string)Call(actions, "FormatSprinklerClutterHint",
            new List<(Point, int)> { (new Point(40, 20), 4), (new Point(45, 20), 2) });
        Check(multiHint != null && multiHint.Contains("(40,20)") && multiHint.Contains("4 格")
            && multiHint.Contains("(45,20)") && multiHint.Contains("2 格") && multiHint.EndsWith("，till_area 会顺手清掉"),
            "多个洒水器杂草提示格式：" + multiHint);
    }
}
