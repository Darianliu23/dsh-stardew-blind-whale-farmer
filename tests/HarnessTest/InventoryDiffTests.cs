using System.Linq;
using System.Reflection;
using InventoryEntry = System.ValueTuple<string, string, int, int>;

partial class Program
{
    static void TestInventoryDiff(Assembly asm)
    {
        var diff = asm.GetType("BlindWhaleFarmer.InventoryDiff");
        var snapshot = diff.GetMethod("Snapshot");
        var difference = diff.GetMethod("Difference");
        var sum = diff.GetMethod("Sum");
        var format = diff.GetMethod("FormatGains");
        InventoryEntry[] Take(params InventoryEntry[] items) => (InventoryEntry[])snapshot.Invoke(null, new object[] { items });
        InventoryEntry[] Changes(InventoryEntry[] before, InventoryEntry[] after) =>
            (InventoryEntry[])difference.Invoke(null, new object[] { before, after });
        string Gains(InventoryEntry[] changes) => (string)format.Invoke(null, new object[] { changes });

        var before = Take(("(O)378", "铜矿石", 0, 2), ("(O)378", "铜矿石", 0, 4), ("(O)268", "杨桃", 0, 2));
        Check(before.Length == 2 && before[0].Item4 == 6, "背包快照合并同 ID 同品质的多格物品");
        var after = Take(("(O)378", "铜矿石", 0, 8), ("(O)268", "杨桃", 2, 3), ("(O)378", "铜矿石", 0, 10), ("(O)268", "杨桃", 0, 1));
        Check(Gains(Changes(before, after)) == "铜矿石x12、杨桃（金星）x3", "背包只显示净增加，不同品质分别计数");

        var moved = Take(("(O)268", "杨桃", 0, 2), ("(O)378", "铜矿石", 0, 6));
        Check(Gains(Changes(before, moved)) == null, "背包调换格子、合并堆叠后没有净增加");
        Check(Gains(Changes(before, Take(("(O)378", "铜矿石", 0, 2)))) == null, "背包只有减少时省略明细");
        Check(Gains(Changes(Take(("(O)1", "同名物品", 0, 1)), Take(("(BC)1", "同名物品", 0, 1)))) == "同名物品x1",
            "相同名称和数字 ID 但类型不同的物品分别计数");
        Check(Gains(Changes(Take(), Take(("(O)268", "杨桃", 1, 2), ("(O)268", "杨桃", 4, 3)))) == "杨桃（银星）x2、杨桃（铱星）x3",
            "背包获得格式标出银星和铱星");

        // 两段动作间隙分别少了 2 个和多了 3 个，动作期间增加的数量不参与计算。
        var firstGap = Changes(before, Take(("(O)378", "铜矿石", 0, 4), ("(O)268", "杨桃", 0, 2)));
        var secondGap = Changes(Take(("(O)378", "铜矿石", 0, 14)), Take(("(O)378", "铜矿石", 0, 17)));
        var gaps = (InventoryEntry[])sum.Invoke(null, new object[] { firstGap.Concat(secondGap) });
        Check(Gains(gaps) == "铜矿石x1", "多个动作间隙的增减按净差抵消");

        var many = Take(Enumerable.Range(1, 8).Select(i => ($"(O){i}", $"物品{i}", 0, 1)).ToArray());
        Check(Gains(Changes(Take(), many)) == "物品1x1、物品2x1、物品3x1、物品4x1、物品5x1、物品6x1 等 8 样",
            "背包获得只列前六样，标出净增加的总样数");
    }
}
