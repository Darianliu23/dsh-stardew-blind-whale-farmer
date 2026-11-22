using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

partial class Program
{
    static void TestPrepareFishing(Type actions, JsonArray schema)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var canSpend = actions.GetMethod("CanSpendSharedMoney", flags);
        foreach (bool multiplayer in new[] { false, true })
            foreach (bool shared in new[] { false, true })
                foreach (bool allow in new[] { false, true })
                    foreach (bool confirm in new[] { false, true })
                    {
                        bool needsConsent = multiplayer && shared && !allow && !confirm;
                        bool allowed = (bool)canSpend.Invoke(null, new object[] { multiplayer, shared, allow, confirm });
                        Check(allowed == !needsConsent, $"共用金币：联机 {multiplayer}，共用 {shared}，配置 {allow}，确认 {confirm}");
                    }

        var offerType = actions.GetNestedType("FishingOffer", BindingFlags.NonPublic);
        var loadoutType = actions.GetNestedType("FishingLoadout", BindingFlags.NonPublic);
        var planner = actions.GetMethod("PlanFishingPurchases", flags);

        Array TypedArray(Type type, object[] values)
        {
            var array = Array.CreateInstance(type, values.Length);
            for (int i = 0; i < values.Length; i++)
                array.SetValue(values[i], i);
            return array;
        }

        object Offer(int slot, int price, int count = 1) =>
            Activator.CreateInstance(offerType, slot, price, count);

        object Loadout(object rod, object bait, params object[] tackle) =>
            Activator.CreateInstance(loadoutType, rod, bait, TypedArray(offerType, tackle));

        var bait = Offer(3, 5, 30);
        var trap = Offer(4, 500);
        var cheapTackle = Offer(5, 200);
        var current = Loadout(null, bait);
        var iridium = Loadout(Offer(1, 7500), bait, trap, cheapTackle);
        var fiberglass = Loadout(Offer(2, 1800), bait);

        (int Slot, int Count)[] Plan(int budget, object existing, params object[] upgrades) =>
            ((Array)planner.Invoke(null, new[] { (object)budget, existing, TypedArray(loadoutType, upgrades) }))
                .Cast<object>()
                .Select(offer => ((int)offerType.GetProperty("Slot").GetValue(offer), (int)offerType.GetProperty("Count").GetValue(offer)))
                .ToArray();

        Check(Plan(1800, current, iridium, fiberglass).SequenceEqual(new[] { (2, 1) }), "先买得起的最好鱼竿，花完预算就不买鱼饵");
        Check(Plan(1814, current, iridium, fiberglass).SequenceEqual(new[] { (2, 1), (3, 2) }), "竿买完后，剩余预算只够两份鱼饵");
        Check(Plan(7499, current, iridium, fiberglass).SequenceEqual(new[] { (2, 1), (3, 30) }), "高一级买不起就选次一级，按新竿的槽位配附件");
        Check(Plan(7700, current, iridium, fiberglass).SequenceEqual(new[] { (1, 1), (3, 30) }), "钓具买得起也先补足鱼饵");
        Check(Plan(7850, current, iridium, fiberglass).SequenceEqual(new[] { (1, 1), (3, 30), (5, 1) }), "补完鱼饵后改选预算内的钓具");
        Check(Plan(8150, current, iridium, fiberglass).SequenceEqual(new[] { (1, 1), (3, 30), (4, 1) }), "预算够时按优先级买一个钓具");
        Check(Plan(500, current).SequenceEqual(new[] { (3, 30) }), "没有可买的升级就补现有竿的鱼饵");
        Check(Plan(500, Loadout(null, null, trap)).SequenceEqual(new[] { (4, 1) }), "鱼饵已够时直接补钓具");
        Check(Plan(21, Loadout(null, Offer(3, 5, 2))).SequenceEqual(new[] { (3, 2) }), "补饵量受缺口和库存限制");
        Check(Plan(0, current, iridium, fiberglass).Length == 0, "零预算不买收费装备");

        var args = schema.FirstOrDefault(t => t["function"]?["name"]?.GetValue<string>() == "prepare_fishing")?["function"]?["parameters"];
        Check(args?["required"] is JsonArray { Count: 0 } && args?["properties"] is JsonObject { Count: 4 } props
            && props["budget"]?["type"]?.GetValue<string>() == "integer"
            && props["bait_count"]?["type"]?.GetValue<string>() == "integer"
            && props["buy_rod"]?["type"]?.GetValue<string>() == "boolean"
            && props["confirm"]?["type"]?.GetValue<string>() == "boolean",
            "prepare_fishing 注册四个可选参数");
    }
}
