using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Content;
using xTile;
using xTile.Layers;
using xTile.Tiles;

// 用法：mapdump <地图名> [x1 y1 x2 y2] [--props] [--reach sx sy gx gy] [--fish sx sy radius maxDist]
//       mapdump --data <资源名> [键前缀]
// 从本机游戏的 Content 读 xnb 地图/数据，按静态碰撞规则画出字符图、模拟寻路和钓点。不包含任何游戏资源。
// 游戏目录：环境变量 STARDEW_PATH，否则用编译时的 GamePath（见 Directory.Build.props）
string game = Environment.GetEnvironmentVariable("STARDEW_PATH")
    ?? typeof(Services).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GamePath")?.Value;
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var p = Path.Combine(game, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

var cm = new ContentManager(new Services(), Path.Combine(game, "Content"));
// --data <资源名> [键前缀]：读 Dictionary<string,string> 资源（例如 Data/Festivals/spring13、Data/mail）
if (args[0] == "--data")
{
    var dict = cm.Load<System.Collections.Generic.Dictionary<string, string>>(args[1]);
    foreach (var kv in dict.Where(kv => args.Length < 3 || kv.Key.StartsWith(args[2])))
        Console.WriteLine($"{kv.Key} = {(kv.Value.Length > 300 ? kv.Value.Substring(0, 300) + "…" : kv.Value)}");
    return;
}
// --json <资源名> <值类型全名> [键1,键2...]：读 Dictionary<string, 值类型> 资源并输出 JSON（例如 Data/Weapons StardewValley.GameData.Weapons.WeaponData）
if (args[0] == "--json")
{
    var valueType = typeof(StardewValley.GameData.Crops.CropData).Assembly.GetType(args[2], throwOnError: true);
    var dictType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), valueType);
    var load = typeof(ContentManager).GetMethod("Load").MakeGenericMethod(dictType);
    var data = (System.Collections.IDictionary)load.Invoke(cm, new object[] { args[1] });
    var keys = args.Length > 3 ? args[3].Split(',') : null;
    var picked = new System.Collections.Generic.Dictionary<string, object>();
    foreach (System.Collections.DictionaryEntry kv in data)
        if (keys == null || keys.Contains((string)kv.Key))
            picked[(string)kv.Key] = kv.Value;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(picked, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));
    return;
}
// --crops：列出所有作物（种子 → 季节、成熟天数、再生天数、产物售价），核对攻略里的数字用
if (args[0] == "--crops")
{
    var crops = cm.Load<System.Collections.Generic.Dictionary<string, StardewValley.GameData.Crops.CropData>>("Data/Crops");
    var objects = cm.Load<System.Collections.Generic.Dictionary<string, StardewValley.GameData.Objects.ObjectData>>("Data/Objects");
    foreach (var (seed, c) in crops.OrderBy(kv => string.Join(",", kv.Value.Seasons)))
    {
        objects.TryGetValue(seed, out var s);
        string harvest = c.HarvestItemId?.Replace("(O)", "");
        objects.TryGetValue(harvest ?? "", out var hv);
        Console.WriteLine($"{string.Join("/", c.Seasons),-14} {hv?.Name,-16} 种子{s?.Name}({s?.Price}g) 成熟{c.DaysInPhase.Sum()}天"
            + $"{(c.RegrowDays > 0 ? $" 再生{c.RegrowDays}天" : "")} 售价{hv?.Price}g{(c.HarvestMaxStack > 1 ? $" 每次{c.HarvestMinStack}-{c.HarvestMaxStack}个" : "")}{(c.IsRaised ? " 搭架" : "")}{(c.NeedsWatering ? "" : " 不用浇水")}");
    }
    return;
}
var map = cm.Load<Map>("Maps/" + args[0]);
var back = map.GetLayer("Back");
var bld = map.GetLayer("Buildings");
int w = back.LayerWidth, h = back.LayerHeight;
int x1 = 0, y1 = 0, x2 = w - 1, y2 = h - 1;
if (args.Length >= 5)
{
    x1 = int.Parse(args[1]); y1 = int.Parse(args[2]); x2 = int.Parse(args[3]); y2 = int.Parse(args[4]);
}
Console.WriteLine($"{args[0]} {w}x{h}");
Console.WriteLine("图例：. 可走  # Buildings 有图块  p Back 有 Passable(=挡)  ~ 水  D 门  A 其他 Action  T TouchAction  ' Buildings 有 Passable(可走)");

string Prop(Tile t, string k) =>
    t == null ? null : t.Properties.TryGetValue(k, out var v) ? v.ToString() : t.TileIndexProperties.TryGetValue(k, out var v2) ? v2.ToString() : null;

Console.Write("     ");
for (int x = x1; x <= x2; x++) Console.Write(x % 10);
Console.WriteLine();
for (int y = y1; y <= y2; y++)
{
    Console.Write($"{y,4} ");
    for (int x = x1; x <= x2; x++)
    {
        var b = back.Tiles[x, y];
        var t = bld.Tiles[x, y];
        string act = Prop(t, "Action");
        char c = '.';
        if (b == null) c = ' ';
        else if (Prop(b, "Water") != null) c = '~';
        if (Prop(b, "Passable") != null) c = 'p';
        if (t != null) c = Prop(t, "Passable") != null || Prop(t, "Shadow") != null ? '\'' : '#';
        if (act != null) c = act.StartsWith("Door") ? 'D' : 'A';
        if (Prop(b, "TouchAction") != null) c = 'T';
        Console.Write(c);
    }
    Console.WriteLine();
}
if (args.Contains("--props"))
{
    for (int y = y1; y <= y2; y++)
        for (int x = x1; x <= x2; x++)
            foreach (var layer in map.Layers)
            {
                var t = layer.Tiles[x, y];
                if (t == null) continue;
                var props = t.Properties.Concat(t.TileIndexProperties).Select(kv => $"{kv.Key}={kv.Value}").ToList();
                if (props.Count > 0) Console.WriteLine($"({x},{y}) {layer.Id}: {string.Join("; ", props)}");
            }
}
// --reach sx sy gx gy：按游戏的静态碰撞规则（Buildings 没 Passable/Shadow 就挡、Back 有 Passable 就挡）做 BFS，
// 分别用旧矩形（y*64+32，下边缘压到下一行）和新矩形（y*64，格子上半截）
int ri = Array.IndexOf(args, "--reach");
if (ri >= 0)
{
    var s = (int.Parse(args[ri + 1]), int.Parse(args[ri + 2]));
    var g = (int.Parse(args[ri + 3]), int.Parse(args[ri + 4]));
    bool TileBlocks(int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= w || ty >= h) return true;
        var t = bld.Tiles[tx, ty];
        if (t != null && Prop(t, "Shadow") == null && Prop(t, "Passable") == null) return true;
        var b = back.Tiles[tx, ty];
        return b != null && Prop(b, "Passable") != null;
    }
    foreach (int topOffset in new[] { 32, 0 })
    {
        bool Passable(int x, int y)
        {
            int left = x * 64 + 8, top = y * 64 + topOffset, right = left + 48, bottom = top + 32;
            foreach (var (cx, cy) in new[] { (right / 64, top / 64), (left / 64, top / 64), (right / 64, bottom / 64), (left / 64, bottom / 64) })
                if (TileBlocks(cx, cy)) return false;
            return true;
        }
        var seen = new System.Collections.Generic.HashSet<(int, int)> { s };
        var q = new System.Collections.Generic.Queue<(int, int)>();
        q.Enqueue(s);
        while (q.Count > 0)
        {
            var (cx, cy) = q.Dequeue();
            foreach (var (dx, dy) in new[] { (0, 1), (1, 0), (0, -1), (-1, 0) })
            {
                var n = (cx + dx, cy + dy);
                if (!seen.Contains(n) && Passable(n.Item1, n.Item2)) { seen.Add(n); q.Enqueue(n); }
            }
        }
        Console.WriteLine($"矩形 top=y*64+{topOffset}：从 {s} 能走到 {seen.Count} 格，{(seen.Contains(g) ? "能" : "不能")}到 {g}");
    }
}
// --fish sx sy radius maxDist：从 sx,sy 出发能走到、且 radius 格内的站位里，按新旧两种规则各有多少个钓鱼点
// 可钓 = Back 有 Water、没有 NoFishing、Buildings 没图块（或 Buildings 有 Water），同 GameLocation.isTileFishable
int fi = Array.IndexOf(args, "--fish");
if (fi >= 0)
{
    int sx = int.Parse(args[fi + 1]), sy = int.Parse(args[fi + 2]), radius = int.Parse(args[fi + 3]), maxDist = int.Parse(args[fi + 4]);
    bool OnMap(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;
    bool Water(int x, int y) => OnMap(x, y) && Prop(back.Tiles[x, y], "Water") != null;
    bool Fishable(int x, int y) => Water(x, y) && Prop(back.Tiles[x, y], "NoFishing") == null
        && (bld.Tiles[x, y] == null || Prop(bld.Tiles[x, y], "Water") != null);
    bool Blocks(int x, int y)
    {
        if (!OnMap(x, y)) return true;
        var t = bld.Tiles[x, y];
        if (t != null && Prop(t, "Shadow") == null && Prop(t, "Passable") == null) return true;
        return back.Tiles[x, y] != null && Prop(back.Tiles[x, y], "Passable") != null;
    }
    bool Stand(int x, int y) => !Blocks(x, y) && !Water(x, y);
    var reach = new System.Collections.Generic.HashSet<(int, int)> { (sx, sy) };
    var bq = new System.Collections.Generic.Queue<(int, int)>();
    bq.Enqueue((sx, sy));
    while (bq.Count > 0)
    {
        var (cx, cy) = bq.Dequeue();
        foreach (var (dx, dy) in new[] { (0, 1), (1, 0), (0, -1), (-1, 0) })
        {
            var n = (cx + dx, cy + dy);
            if (!reach.Contains(n) && Stand(n.Item1, n.Item2)) { reach.Add(n); bq.Enqueue(n); }
        }
    }
    int oldCount = 0, newCount = 0;
    (int, int, int)? bestNew = null;
    foreach (var (x, y) in reach)
    {
        if (Math.Abs(x - sx) > radius || Math.Abs(y - sy) > radius) continue;
        foreach (var (dx, dy) in new[] { (0, 1), (1, 0), (0, -1), (-1, 0) })
        {
            if (Fishable(x + dx, y + dy) && Fishable(x + 2 * dx, y + 2 * dy)) oldCount++;
            if (Water(x + dx, y + dy) || Water(x + 2 * dx, y + 2 * dy))
                for (int k = maxDist; k >= 1; k--)
                    if (Fishable(x + k * dx, y + k * dy)) { newCount++; if (bestNew == null || k > bestNew.Value.Item3) bestNew = (x, y, k); break; }
        }
    }
    Console.WriteLine($"从 ({sx},{sy}) {radius} 格内：旧规则 {oldCount} 个钓点，新规则 {newCount} 个{(bestNew is { } b ? $"，例如站 ({b.Item1},{b.Item2}) 抛 {b.Item3} 格" : "")}");

    // 落点离岸多远（同 FishingRod.distanceToLand）：一圈圈往外找第一个不是水的格子，地图外不算陆地
    int Clear(int tx, int ty)
    {
        for (int r = 1; r <= 5; r++)
            for (int x = tx - r; x <= tx + r; x++)
                for (int y = ty - r; y <= ty + r; y++)
                    if (Math.Max(Math.Abs(x - tx), Math.Abs(y - ty)) == r && OnMap(x, y) && !Water(x, y))
                        return r - 1;
        return 5;
    }
    // maxDist 是左右的满力距离，上下少 1 格；力度再小也至少飞 2 格
    var spots = new System.Collections.Generic.List<(int x, int y, int dx, int dy, int k, int clear, int gap, int far)>();
    foreach (var (x, y) in reach)
    {
        if (Math.Abs(x - sx) > radius || Math.Abs(y - sy) > radius) continue;
        foreach (var (dx, dy) in new[] { (0, 1), (1, 0), (0, -1), (-1, 0) })
        {
            if (!Water(x + dx, y + dy) && !Water(x + 2 * dx, y + 2 * dy)) continue;
            int reachK = dx != 0 ? maxDist : maxDist - 1;
            int best = -1, bestK = 0, far = 0;
            for (int k = 2; k <= reachK; k++)
            {
                if (!Fishable(x + k * dx, y + k * dy)) continue;
                far = k;
                int c = Clear(x + k * dx, y + k * dy);
                if (c > best) { best = c; bestK = k; }
            }
            if (bestK > 0) spots.Add((x, y, dx, dy, bestK, best, Water(x + dx, y + dy) ? 0 : 1, far));
        }
    }
    string Show((int x, int y, int dx, int dy, int k, int clear, int gap, int far) s, int k, int clear) =>
        $"站 ({s.x},{s.y}) 朝 ({s.dx},{s.dy}) 抛 {k} 格，落点离岸 {clear}，和水隔 {s.gap} 格";
    if (spots.Count > 0)
    {
        int Near((int x, int y, int dx, int dy, int k, int clear, int gap, int far) s) => Math.Abs(s.x - sx) + Math.Abs(s.y - sy);
        var oldPick = spots.OrderByDescending(s => s.far).ThenBy(Near).First();
        var newPick = spots.OrderByDescending(s => s.clear).ThenBy(s => s.gap).ThenBy(Near).First();
        Console.WriteLine("旧选法（抛得最远）：" + Show(oldPick, oldPick.far, Clear(oldPick.x + oldPick.far * oldPick.dx, oldPick.y + oldPick.far * oldPick.dy)));
        Console.WriteLine("新选法（离岸最远）：" + Show(newPick, newPick.k, newPick.clear));
    }
}
Console.WriteLine("Map properties: " + string.Join("; ", map.Properties.Select(kv => $"{kv.Key}={kv.Value}")).Substring(0, Math.Min(2000, string.Join("; ", map.Properties.Select(kv => $"{kv.Key}={kv.Value}")).Length)));

class Services : IServiceProvider
{
    public object GetService(Type t) => null;
}
