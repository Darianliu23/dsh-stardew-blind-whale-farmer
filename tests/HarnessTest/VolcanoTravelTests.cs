using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;

partial class Program
{
    static void TestVolcanoTravel(Assembly asm)
    {
        var exits = asm.GetType("BlindWhaleFarmer.Exits");
        var arrivalTile = exits.GetMethod("RouteArrivalTile", Any);
        var gridFor = exits.GetMethod("GetAreaGrid", Any);
        // 火山构造函数需要运行中的游戏内容管理器。离线测试沿用普通地图的基础字段。
        var map = MakeLocation("VolcanoRouteTest", false, 8, 5);
        var volcano = (VolcanoDungeon)RuntimeHelpers.GetUninitializedObject(typeof(VolcanoDungeon));
        foreach (var field in typeof(GameLocation).GetFields(Any & ~BindingFlags.Static))
            field.SetValue(volcano, field.GetValue(map));
        volcano.level = new();
        volcano.dwarfGates = new();
        volcano.startPosition = new Point(3, 3);
        volcano.endPosition = new Point(5, 2);
        volcano.waterTiles = new WaterTiles(8, 5);

        Point Arrival(string from, Point warp) => (Point)arrivalTile.Invoke(null, new object[] { from, volcano, warp });
        Check(Arrival("Caldera", new Point(20, 30)) == new Point(44, 50), "火山口捷径按第 0 层机关门内侧的实际落点规划");
        Check(Arrival("VolcanoDungeon1", Point.Zero) == volcano.endPosition, "从上层下火山时，(0,0) 表示下一层的楼梯出口");
        Check(Arrival("IslandNorth", new Point(1, 1)) == volcano.startPosition, "从岛北正门进火山时，(1,1) 表示第 0 层入口");
        Check(Arrival("IslandNorth", new Point(6, 48)) == new Point(6, 48), "从岛北西侧进火山时，保留西侧的地图落点");

        // 模拟第 0 层：机关门和岩浆可处理，后面的整面岩壁仍隔开两块区域。
        for (int y = 0; y < 5; y++)
        {
            Wall(volcano, 2, y);
            Wall(volcano, 4, y);
            Wall(volcano, 6, y);
        }
        Water(volcano, 4, 2);
        volcano.waterTiles[4, 2] = true;
        int[,] Grid() => (int[,])gridFor.Invoke(null, new object[] { volcano });
        var beforeGateSync = Grid();
        Check(beforeGateSync[1, 2] != beforeGateSync[3, 2], "火山分区：还没有同步机关门时保留地图碰撞");
        var gate = new DwarfGate();
        gate.tilePosition.Value = new Point(2, 1);
        volcano.dwarfGates.Add(gate);
        var grid = Grid();
        Check(grid[1, 2] == grid[3, 2] && grid[3, 2] == grid[5, 2], "火山分区：纳入可踩开的机关门和可浇冷的岩浆");
        Check(grid[5, 2] != grid[7, 2], "火山分区：保留岩壁隔断");

        volcano.name.Value = "VolcanoSpringTest";
        volcano.level.Value = 5;
        grid = Grid();
        Check(grid[3, 2] != grid[5, 2], "火山分区：第 5 层泉水依旧挡路");
    }
}
