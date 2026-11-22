using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

partial class Program
{
    static void TestMineNodeScoring(Type actions, JsonArray schema)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var parse = actions.GetMethod("ParseMineGoal", flags);
        var classify = actions.GetMethod("ClassifyMineStone", flags);
        var score = actions.GetMethod("ScoreMineNodeTier", flags);
        object Goal(string text) => parse.Invoke(null, new object[] { text });
        object Tier(string id, bool generatedMine = true) => classify.Invoke(null, new object[] { id, generatedMine });
        int Score(string id, string goal) => (int)score.Invoke(null, new[] { Tier(id), Goal(goal) });

        var parameters = schema.Single(t => t["function"]["name"].GetValue<string>() == "explore_mine")["function"]["parameters"];
        Check(parameters["properties"]["goal"]["type"].GetValue<string>() == "string"
            && !((JsonArray)parameters["required"]).Any(x => x.GetValue<string>() == "goal"),
            "explore_mine 的 goal 是可选字符串");
        Check(Goal(null)?.ToString() == "Balanced" && Goal(" ")?.ToString() == "Balanced"
            && Goal("balanced")?.ToString() == "Balanced" && Goal(" DESCENT ")?.ToString() == "Descent"
            && Goal("ore")?.ToString() == "Ore" && Goal("CoAl")?.ToString() == "Coal"
            && Goal("gems")?.ToString() == "Gems" && Goal("unknown") == null,
            "采矿目标：默认均衡，忽略大小写和首尾空格，拒绝未知目标");

        foreach (var (expected, ids) in new[]
        {
            ("Copper", "751 849"), ("Iron", "290 850"), ("Gold", "764 VolcanoGoldNode"), ("Iridium", "765"),
            ("Coal", "BasicCoalNode0 BasicCoalNode1 VolcanoCoalNode0 VolcanoCoalNode1"),
            ("CoalStone", "668 670 845 846 847"), ("LowCoalStone", "40 42"), ("Gem", "2 4 6 8 10 12 14 44"),
            ("Geode", "75"), ("FrozenGeode", "76"), ("MagmaGeode", "77"), ("OmniGeode", "819"),
            ("Mystic", "46"), ("Radioactive", "95"), ("CalicoEgg", "CalicoEggStone_0 CalicoEggStone_1 CalicoEggStone_2"),
            ("CinderShard", "843 844"), ("Mussel", "25"), ("Bone", "816 817"), ("Clay", "818")
        })
            Check(ids.Split(' ').All(id => Tier(id).ToString() == expected), $"石头编号 {ids} 的档位是 {expected}");

        Check(new[] { "40", "42" }.All(id => Tier(id, false).ToString() == "Stone")
            && new[] { "343", "450" }.All(id => Tier(id).ToString() == "Stone" && Tier(id, false).ToString() == "CoalStone"),
            "概率掉煤石按地点走对应的掉落分支");
        Check(new[] { "balanced", "descent", "ore", "coal", "gems" }.All(goal => Score("unknown", goal) == 0),
            "没有专属高价值掉落的石头按 0 分算");
        Check(Score("765", "ore") > Score("764", "ore") && Score("764", "ore") > Score("290", "ore")
            && Score("290", "ore") > Score("751", "ore"), "金属目标：铱 > 金 > 铁 > 铜");
        Check(Score("BasicCoalNode0", "coal") > Score("670", "coal") && Score("670", "coal") > Score("765", "coal")
            && Score("670", "coal") > Score("40", "coal") && Score("40", "coal") > Score("unknown", "coal"),
            "找煤目标：煤矿和概率掉煤石优先；小幅提高煤率的石头少加分");
        Check(Score("44", "gems") > Score("765", "gems") && Score("819", "gems") > Score("77", "gems")
            && Score("77", "gems") > Score("76", "gems") && Score("76", "gems") > Score("75", "gems"),
            "宝石目标：优先宝石，晶球按种类分档");
        Check(Score("46", "descent") > 0 && Score("765", "descent") > 0
            && Score("751", "descent") == 0 && Score("BasicCoalNode0", "descent") == 0,
            "下楼目标：只有少数高价值节点得分");
    }
}
