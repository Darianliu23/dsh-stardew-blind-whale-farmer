using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.Tiles;

// M11：map_info、move_to 挡路诊断、“位置”段地图尺寸。离线手拼一张小地图（不进游戏），通过反射调模组里的 internal 方法。
partial class Program
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>手拼一张 w x h 的地图：Back 层铺满地面，有 Buildings 层（墙用），能直接当 GameLocation 用。</summary>
    static GameLocation MakeLocation(string name, bool outdoors, int w, int h)
    {
        var loc = new GameLocation();
        var map = new Map(name);
        foreach (var layer in new[] { "Back", "Buildings", "Front" })
            map.AddLayer(new Layer(layer, map, new xTile.Dimensions.Size(w, h), new xTile.Dimensions.Size(64, 64)));
        var sheet = new TileSheet("t", map, "probe", new xTile.Dimensions.Size(4, 4), new xTile.Dimensions.Size(16, 16));
        map.AddTileSheet(sheet);
        var back = map.GetLayer("Back");
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                back.Tiles[x, y] = new StaticTile(back, sheet, BlendMode.Alpha, 0);
        loc.Map = map;
        loc.name.Value = name;
        loc.isOutdoors.Value = outdoors;
        return loc;
    }

    static void Wall(GameLocation loc, int x, int y)
    {
        var bl = loc.Map.GetLayer("Buildings");
        bl.Tiles[x, y] = new StaticTile(bl, loc.Map.TileSheets[0], BlendMode.Alpha, 1);
    }

    static void Water(GameLocation loc, int x, int y) => loc.Map.GetLayer("Back").Tiles[x, y].Properties["Water"] = "T";

    /// <summary>
    /// 地图状态的指纹：物件、地形、大型障碍、建筑、传送点、各层图块和属性。map_info 前后各算一次，一样就说明它没改游戏状态。
    /// </summary>
    static string Fingerprint(GameLocation loc)
    {
        var parts = new List<string>
        {
            loc.NameOrUniqueName, loc.IsOutdoors.ToString(),
            "obj:" + string.Join(",", loc.objects.Pairs.Select(p => $"{p.Key}={p.Value.QualifiedItemId}").OrderBy(s => s)),
            "tf:" + string.Join(",", loc.terrainFeatures.Pairs.Select(p => $"{p.Key}={p.Value.GetType().Name}").OrderBy(s => s)),
            "clump:" + string.Join(",", loc.resourceClumps.Select(c => $"{c.Tile}={c.parentSheetIndex.Value}")),
            "bld:" + loc.buildings.Count,
            "warp:" + string.Join(",", loc.warps.Select(w => $"{w.X},{w.Y}>{w.TargetName}@{w.TargetX},{w.TargetY}")),
        };
        foreach (var layer in loc.Map.Layers)
            for (int x = 0; x < layer.LayerWidth; x++)
                for (int y = 0; y < layer.LayerHeight; y++)
                    if (layer.Tiles[x, y] is { } t)
                        parts.Add($"{layer.Id}:{x},{y}:{t.TileIndex}:{string.Join("|", t.Properties.Select(kv => kv.Key + "=" + kv.Value))}");
        return string.Join(";", parts);
    }

    static async Task TestMapInfo(Assembly asm, JsonArray schema, Func<string, string, Task<string>> run)
    {
        var tMap = asm.GetType("BlindWhaleFarmer.MapInfo");
        var tEntry = asm.GetType("BlindWhaleFarmer.MapEntry");
        var tActions = asm.GetType("BlindWhaleFarmer.GameActions");
        var tObserver = asm.GetType("BlindWhaleFarmer.Observer");

        // ── 1. map_info 的 schema：只读工具、只有 section 一个可选参数 ──
        var fn = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "map_info")?["function"];
        var props = fn?["parameters"]?["properties"] as JsonObject;
        Check(fn != null && props?.Count == 2 && props["section"]?["type"]?.GetValue<string>() == "string" && props["all"]?["type"]?.GetValue<string>() == "boolean"
              && ((JsonArray)fn["parameters"]["required"]).Count == 0, "map_info 的 schema：可选的 section(string) 和 all(boolean)");
        var readOnly = (HashSet<string>)asm.GetType("BlindWhaleFarmer.Tools").GetField("ReadOnlyTools", Any).GetValue(null);
        Check(readOnly.Contains("map_info"), "map_info 在只读工具名单里（内置 AI 运行时 MCP 也能查）");

        // ── 2. section 解析 ──
        var parse = tMap.GetMethod("ParseSections", Any);
        string[] Parse(string s, out string bad)
        {
            var args = new object[] { s, null };
            var r = (string[])parse.Invoke(null, args);
            bad = (string)args[1];
            return r;
        }
        Check(Parse(null, out _).SequenceEqual(new[] { "exits", "bridges", "doors" }) && Parse("", out _).Length == 3, "map_info 不填 section 给全部三类");
        Check(Parse("bridges", out _).SequenceEqual(new[] { "bridges" }) && Parse("门,出口", out _).SequenceEqual(new[] { "doors", "exits" }) && Parse("EXITS", out _).SequenceEqual(new[] { "exits" }),
            "map_info 的 section 认英文、中文、大小写和多类");
        Check(Parse("roads", out string badSection) == null && badSection == "roads", "map_info 认不出的 section 返回 null 并报出是哪个");
        string rBadSection = await run("map_info", "{\"section\":\"roads\"}");
        Check(rBadSection.StartsWith("失败：map_info 不认识 section “roads”"), "map_info section 写错时说清楚：" + Shorten(rBadSection));
        string rNoMap = await run("map_info", "{}");
        Check(rNoMap.StartsWith("失败") && !rNoMap.Contains("不认识"), "map_info 没进游戏时安全失败：" + Shorten(rNoMap));

        // ── 3. map_info 返回结构：每条有名字、坐标、通向哪；三段标题都在 ──
        var listType = typeof(List<>).MakeGenericType(tEntry);
        var entries = (IList)Activator.CreateInstance(listType);
        object Entry(string section, string name, int x, int y, string target, string note = "", int? ex = null, int? ey = null)
        {
            var e = Activator.CreateInstance(tEntry);
            tEntry.GetField("Section").SetValue(e, section);
            tEntry.GetField("Name").SetValue(e, name);
            tEntry.GetField("Tile").SetValue(e, new Point(x, y));
            tEntry.GetField("Target").SetValue(e, target);
            tEntry.GetField("Note").SetValue(e, note);
            if (ex.HasValue)
                tEntry.GetField("TileEnd").SetValue(e, (Point?)new Point(ex.Value, ey.Value));
            return e;
        }
        foreach (var f in new[] { "Section", "Name", "Tile", "TileEnd", "Target", "Note" })
            Check(tEntry.GetField(f) != null, $"MapEntry 有字段 {f}");
        entries.Add(Entry("exits", "出口", 0, 54, "Town"));
        entries.Add(Entry("exits", "出口", 40, 0, "Mountain"));
        entries.Add(Entry("doors", "门", 43, 57, "SeedShop", "（营业 900-2100）"));
        entries.Add(Entry("bridges", "去潮池的木桥", 58, 13, "潮池（Beach 东边）", "（还断着）", 61, 14));
        var format = tMap.GetMethod("Format", Any);
        string text = (string)format.Invoke(null, new object[] { "Beach", "海滩", true, 104, 50, entries, new[] { "exits", "bridges", "doors" }, new Point(0, 54), false });
        Check(text.Contains("地点：Beach（海滩），室外，地图大小 104x50"), "map_info 开头有地点、室内外、地图大小");
        Check(text.Contains("## 出口") && text.Contains("## 桥") && text.Contains("## 门"), "map_info 三段标题都在");
        Check(text.Contains("- 出口 (0,54) → Town") && text.Contains("- 出口 (40,0) → Mountain"), "出口：名字 + 坐标 + 通向哪");
        Check(text.Contains("- 门 (43,57) → SeedShop（营业 900-2100）"), "门：名字 + 坐标 + 通向哪 + 营业时间");
        Check(text.Contains("- 去潮池的木桥 (58,13)-(61,14) → 潮池（Beach 东边）（还断着）"), "桥：名字 + 范围 + 通向哪 + 修没修好");
        string onlyDoors = (string)format.Invoke(null, new object[] { "Beach", "海滩", true, 104, 50, entries, new[] { "doors" }, new Point(0, 54), false });
        Check(onlyDoors.Contains("## 门") && !onlyDoors.Contains("## 出口") && !onlyDoors.Contains("Town"), "section=doors 只给门");
        var empty = (IList)Activator.CreateInstance(listType);
        string none = (string)format.Invoke(null, new object[] { "FarmHouse", null, false, 20, 12, empty, new[] { "exits", "bridges", "doors" }, new Point(3, 3), false });
        Check(none.Contains("室内，地图大小 20x12") && none.Contains("## 桥\n无") || none.Contains("## 桥\r\n无"), "map_info 没有条目时写“无”，室内也报大小");
        Check(text.Contains("只读"), "map_info 结果写明只读");

        // ── 4. 手拼地图上跑 Collect：传送点 → 出口；只读，前后状态不变 ──
        GameLocation town = null;
        try
        {
            town = MakeLocation("ProbeTown", true, 30, 20);
            town.warps.Add(new Warp(0, 10, "ProbeFarm", 79, 15, false));
        }
        catch (Exception ex)
        {
            Check(false, "拼测试地图：" + ex.GetBaseException().Message);
        }
        if (town != null)
        {
            string before = Fingerprint(town);
            object collected = null;
            Exception err = null;
            try
            {
                collected = tMap.GetMethod("Collect", Any).Invoke(null, new object[] { town });
            }
            catch (TargetInvocationException ex)
            {
                err = ex.InnerException;
            }
            // 离线没有游戏世界：Exits.Get 查目标地点在不在（Game1.getLocationFromName）会抛异常。能跑就查结构，跑不了至少证明没改状态
            if (collected is IList got)
                Check(got.Count == 0 || got.Cast<object>().All(e => tEntry.GetField("Name").GetValue(e) != null), "Collect 返回的条目都有名字");
            else
                Console.WriteLine("（离线没有游戏世界，Collect 查地点名时抛了 " + err?.GetType().Name + "，只检查没改状态）");
            Check(Fingerprint(town) == before, "map_info 的 Collect 只读：地图上的物件、地形、传送点、图块都没变");
        }

        // ── 5. “位置”段：室内、室外都有地图尺寸和坐标范围 ──
        var line = tObserver.GetMethod("LocationLine", Any);
        string outdoorLine = (string)line.Invoke(null, new object[] { "Farm", "农场", true, 80, 65 });
        string indoorLine = (string)line.Invoke(null, new object[] { "FarmHouse", "农舍", false, 20, 12 });
        Check(outdoorLine == "地点：Farm（农场），室外，地图大小 80x65（坐标 x 是 0~79，y 是 0~64）", "位置段（室外）有地图尺寸：" + outdoorLine);
        Check(indoorLine == "地点：FarmHouse（农舍），室内，地图大小 20x12（坐标 x 是 0~19，y 是 0~11）", "位置段（室内）有地图尺寸：" + indoorLine);
        string bigLine = (string)line.Invoke(null, new object[] { "Island", null, true, 163, 156 });
        Check(bigLine.Contains("地图大小 163x156") && !bigLine.Contains("（）"), "位置段：没有显示名时不留空括号：" + bigLine);
        if (town != null)
        {
            // 手拼的地图：尺寸从图层读出来，和 Pathfinder 用的一样
            var tPath = asm.GetType("BlindWhaleFarmer.Pathfinder");
            int w = (int)tPath.GetMethod("MapWidth", Any).Invoke(null, new object[] { town });
            int h = (int)tPath.GetMethod("MapHeight", Any).Invoke(null, new object[] { town });
            var house = MakeLocation("ProbeHouse", false, 12, 9);
            int hw = (int)tPath.GetMethod("MapWidth", Any).Invoke(null, new object[] { house });
            int hh = (int)tPath.GetMethod("MapHeight", Any).Invoke(null, new object[] { house });
            Check(w == 30 && h == 20 && hw == 12 && hh == 9, $"地图尺寸从图层读：室外 {w}x{h}，室内 {hw}x{hh}");
            Check(((string)line.Invoke(null, new object[] { house.NameOrUniqueName, null, house.IsOutdoors, hw, hh })).Contains("室内，地图大小 12x9"), "手拼的室内地图：位置段写室内和 12x9");
        }

        // ── 6. move_to 挡路诊断：构造挡路格，文案里有挡路类型和绕行点 ──
        var kindOf = tMap.GetMethod("BlockerKind", Any);
        var diagnose = tActions.GetMethod("BlockDiagnosis", Any);
        if (town != null)
        {
            // 东西两半中间一道墙（x=15 那一列，y 1~18），墙上 y=17 留个口子；水在 (5,5)；大石头、树桩各一块
            var wall = new HashSet<Point>();
            for (int y = 1; y <= 18; y++)
                if (y != 17)
                {
                    Wall(town, 15, y);
                    wall.Add(new Point(15, y));
                }
            Water(town, 5, 5);
            town.resourceClumps.Add(new StardewValley.TerrainFeatures.ResourceClump(672, 2, 2, new Vector2(20, 3)));
            town.resourceClumps.Add(new StardewValley.TerrainFeatures.ResourceClump(600, 2, 2, new Vector2(24, 3)));
            town.terrainFeatures.Add(new Vector2(10, 12), new StardewValley.TerrainFeatures.Tree("1", 5));
            string Kind(int x, int y) => (string)kindOf.Invoke(null, new object[] { town, new Point(x, y) });
            Check(Kind(15, 5) == "建筑", "挡路类型：Buildings 层的墙 → 建筑");
            Check(Kind(5, 5) == "水", "挡路类型：水面 → 水");
            Check(Kind(20, 3) == "石头" && Kind(21, 4) == "石头", "挡路类型：大石头（占 2x2）→ 石头");
            Check(Kind(24, 3) == "树", "挡路类型：大树桩 → 树");
            Check(Kind(10, 12) == "树", "挡路类型：树 → 树");
            Check(Kind(8, 8) == null, "挡路类型：空地认不出 → null");

            // 能走的格子：地图内、不是墙、不是水、不压着大石头树桩树
            var blocked = new HashSet<Point>(wall) { new Point(5, 5), new Point(10, 12) };
            foreach (var c in town.resourceClumps)
                for (int dx = 0; dx < 2; dx++)
                    for (int dy = 0; dy < 2; dy++)
                        blocked.Add(new Point((int)c.Tile.X + dx, (int)c.Tile.Y + dy));
            Func<Point, bool> passable = p => p.X >= 0 && p.Y >= 0 && p.X < 30 && p.Y < 20 && !blocked.Contains(p);
            string Diagnose(Point me, Point closest, Point goal) => (string)diagnose.Invoke(null, new object[] { town, me, closest, goal, passable });

            // 所有诊断文案都不能再出现这两种说法（真机发现的自相矛盾和废话方向）
            bool NoNonsense(string s) => !s.Contains("以外的方向接近") && !s.Contains("换个方向，从");
            // 清不掉的（建筑/水/围栏/认不出的）不许提“清掉”
            bool NoClearAdvice(string s) => !s.Contains("清掉") && !s.Contains("clear_area") && !s.Contains("use_tool");

            // 站在墙西边 (14,5)，想去墙东边 (16,5)：挡的是墙（建筑），绕行点要能走到
            string d1 = Diagnose(new Point(10, 5), new Point(14, 5), new Point(16, 5));
            Check(d1.Contains("挡路的是：建筑"), "move_to 被墙挡：文案里有挡路类型“建筑”：" + Shorten(d1));
            Check(d1.Contains("建筑和墙清不掉，只能绕"), "move_to 被墙挡：说清楚清不掉要绕");
            Check(NoClearAdvice(d1) && NoNonsense(d1), "move_to 被墙挡：不提清掉、不给废话方向：" + d1);
            var m = System.Text.RegularExpressions.Regex.Match(d1, @"往 \((\d+),(\d+)\) 绕一下");
            Check(m.Success, "move_to 被墙挡：给了“往 (x,y) 绕一下”：" + Shorten(d1.Substring(Math.Max(0, d1.IndexOf('；')))));
            if (m.Success)
            {
                var dp = new Point(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                Check(passable(dp) && !wall.Contains(dp), $"绕行点 ({dp.X},{dp.Y}) 本身能站");
            }

            // 想去水边 (5,5) 旁：挡的是水
            string d2 = Diagnose(new Point(2, 5), new Point(4, 5), new Point(5, 5));
            Check(d2.Contains("挡路的是：水") && d2.Contains("找桥"), "move_to 被水挡：文案里有“水”和找桥：" + Shorten(d2));
            Check(NoClearAdvice(d2) && NoNonsense(d2), "move_to 被水挡：不提清掉：" + d2);

            // 大石头：能清，给清的办法
            string d3 = Diagnose(new Point(18, 4), new Point(19, 4), new Point(22, 4));
            Check(d3.Contains("挡路的是：石头") && d3.Contains("镐子") && d3.Contains("clear_area"), "move_to 被大石头挡：文案里有“石头”和清的办法：" + Shorten(d3));
            Check(!d3.Contains("清不掉") && NoNonsense(d3), "move_to 被石头挡：不说清不掉：" + d3);

            // 树：能清，给清的办法（站在树西边、想去树东边）
            string d6 = Diagnose(new Point(8, 12), new Point(9, 12), new Point(11, 12));
            Check(d6.Contains("挡路的是：树") && d6.Contains("斧头"), "move_to 被树挡：文案里有“树”和斧头：" + Shorten(d6));
            Check(!d6.Contains("清不掉") && NoNonsense(d6), "move_to 被树挡：不说清不掉：" + d6);

            // 什么都认不出（只是 passable 说走不了的空地）：写“说不清是什么挡着”，当清不掉处理
            Func<Point, bool> invisible = p => p.X != 8 || p.Y != 15;
            string d4 = (string)diagnose.Invoke(null, new object[] { town, new Point(3, 15), new Point(7, 15), new Point(8, 15), invisible });
            Check(d4.Contains("说不清是什么挡着"), "认不出的挡路：写“说不清是什么挡着”：" + Shorten(d4));
            Check(d4.Contains("绕一下") && NoClearAdvice(d4) && NoNonsense(d4), "认不出的挡路：给绕行点，不提清掉：" + d4);

            // 四面被围死、挡的是墙：没有能绕的格子，直说绕不过去、先做别的
            Func<Point, bool> boxed = p => p == new Point(14, 9);
            string d5 = (string)diagnose.Invoke(null, new object[] { town, new Point(14, 9), new Point(14, 9), new Point(16, 9), boxed });
            Check(d5.Contains("绕不过去") && d5.Contains("先做别的") && !d5.Contains("绕一下"), "绕不过去：直说先做别的：" + d5);
            Check(NoClearAdvice(d5) && NoNonsense(d5), "绕不过去：不提清掉、不给废话方向");

            // 目标和周围 2 格都站不上去（真机撞到 (0,0)）：不给绕行点和方向，墙就直说走不到
            var unstand = tActions.GetMethod("UnstandableTarget", Any);
            Func<Point, bool> allBlocked = p => false;
            string u1 = (string)unstand.Invoke(null, new object[] { town, new Point(15, 5), allBlocked });
            Check(u1.Contains("周围 2 格都站不上去") && u1.Contains("建筑") && u1.Contains("先做别的"), "目标站不上去（墙）：说挡的是建筑、先做别的：" + u1);
            Check(!u1.Contains("绕") && NoClearAdvice(u1) && NoNonsense(u1), "目标站不上去（墙）：不说绕、不提清掉、不给方向：" + u1);
            string u2 = (string)unstand.Invoke(null, new object[] { town, new Point(21, 4), allBlocked });
            Check(u2.Contains("石头") && u2.Contains("镐子") && !u2.Contains("先做别的"), "目标站不上去（石头）：给清的办法：" + u2);
        }

        // ── 7. 同一目标连续失败计数：第 2 次起附出口摘要 ──
        var record = tMap.GetMethod("RecordMoveFailure", Any);
        var reset = tMap.GetMethod("ResetMoveFailures", Any);
        reset.Invoke(null, null);
        int n1 = (int)record.Invoke(null, new object[] { "Town", new Point(66, 64) });
        int n2 = (int)record.Invoke(null, new object[] { "Town", new Point(66, 64) });
        int n3 = (int)record.Invoke(null, new object[] { "Town", new Point(10, 10) });
        int n4 = (int)record.Invoke(null, new object[] { "Beach", new Point(10, 10) });
        Check(n1 == 1 && n2 == 2, "同一地点同一格连续失败：第 1 次 1、第 2 次 2");
        Check(n3 == 1 && n4 == 1, "换了目标格或换了地点：重新从 1 数");
        reset.Invoke(null, null);
        Check((int)record.Invoke(null, new object[] { "Town", new Point(10, 10) }) == 1, "move_to 成功后（Reset）重新从 1 数");
        reset.Invoke(null, null);

        // ── 8. 条数上限：默认每类最多 12 条、按离玩家远近排，all=true 全给；move_to 的出口摘要用同一个常量 ──
        int cap = (int)tMap.GetField("MaxEntriesPerSection", Any).GetValue(null);
        Check(cap == 12, "每类上限常量 MaxEntriesPerSection = 12");
        Check(tMap.GetMethod("ExitSummary", Any).GetParameters().Last().DefaultValue is int max && max == cap, "move_to 出口摘要默认条数和 map_info 上限是同一个常量");
        // 模拟真机 Town：9 条出口 + 15 条门
        var town24 = (IList)Activator.CreateInstance(listType);
        for (int i = 0; i < 9; i++)
            town24.Add(Entry("exits", "出口", i * 10, 0, "Exit" + i));
        for (int i = 0; i < 15; i++)
            town24.Add(Entry("doors", "门", i * 5, 50, "Door" + i));
        var me = new Point(40, 48);
        string capped = (string)format.Invoke(null, new object[] { "Town", "鹈鹕镇", true, 130, 110, town24, new[] { "exits", "bridges", "doors" }, me, false });
        int CountLines(string s, string prefix) => s.Split('\n').Count(l => l.TrimStart().StartsWith(prefix));
        Check(CountLines(capped, "- 出口 ") == 9 && CountLines(capped, "- 门 ") == cap, $"默认：出口 9 条全给、门截到 {cap} 条：出口 {CountLines(capped, "- 出口 ")} 门 {CountLines(capped, "- 门 ")}");
        Check(capped.Contains("还有 3 条离你更远的没列") && capped.Contains("all=true"), "截断了要说还有几条、怎么看全部");
        var doorLines = capped.Split('\n').Where(l => l.TrimStart().StartsWith("- 门 ")).ToList();
        Check(doorLines.First().Contains("(40,50)") && !capped.Contains("→ Door0\n") && !capped.Contains("→ Door0\r"), "门按离玩家 (40,48) 远近排，最远的 (0,50) 被截掉：" + doorLines.First().Trim());
        string full = (string)format.Invoke(null, new object[] { "Town", "鹈鹕镇", true, 130, 110, town24, new[] { "exits", "bridges", "doors" }, me, true });
        Check(CountLines(full, "- 门 ") == 15 && !full.Contains("没列"), "all=true：15 条门全给");
        var mapProps = schema.First(t => t["function"]["name"].GetValue<string>() == "map_info")["function"]["parameters"]["properties"];
        Check(mapProps["all"]?["type"]?.GetValue<string>() == "boolean", "map_info 的 schema 有 all(boolean)");
        string rAllBad = await run("map_info", "{\"all\":\"maybe\"}");
        Check(rAllBad.Contains("all 要 true/false"), "map_info 的 all 传错报开关类型错误：" + Shorten(rAllBad));

        // ── 9. 别的模组藏在地图外的箱子不报（Grandpa's Toolbelt 的“第二工具栏”箱子在 Farm 的 (-500,-500) 一带） ──
        // 离线建不了 Chest（要游戏的物品数据），所以分两半测：坐标判断用手拼的地图；modData 标记测键名和真机那个模组一致
        var offTile = tActions.GetMethod("IsOffMapTile", Any);
        if (town != null)
        {
            bool Off(int x, int y) => (bool)offTile.Invoke(null, new object[] { town, new Vector2(x, y) });
            Check(Off(-555, -500) && Off(-500, -500) && Off(-1, 0) && Off(30, 5) && Off(0, 20), "地图外的坐标（真机的 -555,-500、刚出界的 -1/30/20）都算藏起来的");
            Check(!Off(0, 0) && !Off(3, 3) && !Off(29, 19), "地图内的坐标（含四角）照常报");
        }
        Check((string)tActions.GetField("ToolbeltChestKey", Any).GetValue(null) == "GrandpasToolbelt/IsBelt", "Grandpa's Toolbelt 的箱子标记键名对得上（GetVoidChest 里写的 GrandpasToolbelt/IsBelt）");
        var offMap = tActions.GetMethod("IsOffMapChest", Any);
        Check(offMap != null && offMap.GetParameters().Length == 3, "IsOffMapChest(地点, 坐标, 箱子) 存在，列箱子、存取箱子都走它");

        // ── 10. 真机试玩发现的问题 ──
        // 地图边上的出口：(-1,54) 这种地图外坐标夹进地图里报
        var clamp = tMap.GetMethod("ClampToMap", Any);
        if (town != null)
        {
            (Point p, bool edge) Clamp(int x, int y)
            {
                var args = new object[] { town, new Point(x, y), false };
                var p = (Point)clamp.Invoke(null, args);
                return (p, (bool)args[2]);
            }
            var (c1, e1) = Clamp(-1, 10);
            var (c2, e2) = Clamp(15, -1);
            var (c3, e3) = Clamp(30, 20);
            var (c4, e4) = Clamp(7, 7);
            Check(c1 == new Point(0, 10) && e1 && c2 == new Point(15, 0) && e2 && c3 == new Point(29, 19) && e3,
                $"地图边上的出口夹进地图：(-1,10)→({c1.X},{c1.Y})、(15,-1)→({c2.X},{c2.Y})、(30,20)→({c3.X},{c3.Y})");
            Check(c4 == new Point(7, 7) && !e4, "地图里面的出口坐标原样报");
        }

        // skip_event 碰到普通对话框：说该用 dialogue，不只是“没有剧情事件”
        var noEvent = tActions.GetMethod("NoEventHint", Any);
        string none0 = (string)noEvent.Invoke(null, new object[] { null });
        Check(none0.Contains("没有剧情事件") && none0.Contains("没有开着的界面"), "skip_event 没剧情也没界面：说可以直接做别的：" + none0);
        Check(noEvent.GetParameters()[0].ParameterType.Name == "IClickableMenu", "NoEventHint 按开着的界面分情况（对话框→dialogue、信件→close_menu、其他界面→close_menu/menu_click）");

        // 背包满了：动作自己给了办法就不再补一整段，也不再附“🎒 背包只剩”
        var bagAdvice = asm.GetType("BlindWhaleFarmer.Tools").GetMethod("WithBagFullAdvice", Any);
        (string text, bool noted) Bag(string tool, string result)
        {
            var args = new object[] { tool, result, false };
            var r = (string)bagAdvice.Invoke(null, args);
            return (r, (bool)args[2]);
        }
        var fishFull = Bag("fish", "失败：背包满了，再钓拿不下的会掉在地上。想留在这里接着钓：钓上来的垃圾用 trash_item 扔掉就能腾出位置。或者整理（tidy_backpack）");
        Check(fishFull.text.Split("背包满了").Length == 2 && !fishFull.text.Contains("①"), "钓鱼自己给了办法：不再补“①②③”那段：" + Shorten(fishFull.text));
        Check(fishFull.noted, "说过背包满了：告诉调用方别再附“🎒 背包只剩”");
        var craftFull = Bag("craft", "失败：材料不够或背包满了。宝箱 需要：木材 x50");
        Check(craftFull.text.Contains("①") && craftFull.noted, "动作没给办法时照样补处理顺序");
        var storeFull = Bag("take_items", "失败：背包满了，箱子里还有木材");
        Check(!storeFull.text.Contains("①"), "存取箱子本身就在处理背包，不补");
        var notFull = Bag("fish", "成功：钓到 1 次：鳀鱼");
        Check(!notFull.noted && notFull.text == "成功：钓到 1 次：鳀鱼", "没说背包满了：原样返回，🎒 提醒照常");

        // craft / place_item 描述里的箱子名要和游戏一致（中文版叫“宝箱”，真机写“箱子”找不到）
        string Desc(string tool) => schema.First(t => t["function"]["name"].GetValue<string>() == tool)["function"]["description"].GetValue<string>();
        Check(Desc("craft").Contains("Chest / 宝箱") && !Desc("craft").Contains("Chest / 箱子"), "craft 描述用游戏里的名字“宝箱”");
        Check(Desc("place_item").Contains("Chest / 宝箱") && !Desc("place_item").Contains("Chest / 箱子"), "place_item 描述用游戏里的名字“宝箱”");

        // ── 11. 一次修完：名字候选、耗时、时限、HUD、今日、小屋、攻略 ──
        var tUtil = asm.GetType("BlindWhaleFarmer.Util");
        var score = tUtil.GetMethod("NameScore", Any);
        int Score(string a, string b) => (int)score.Invoke(null, new object[] { a, b });
        Check(Score("防风种子", "防风草种子") > 0 && Score("Parsnip Seed", "Parsnip Seeds") > 0 && Score("土豆种", "土豆种子") > 0, "名字候选：漏一个字、单复数都算像");
        Check(Score("防风种子", "斧头") == 0 && Score("Chest", "Pickaxe") == 0, "名字候选：不相干的不算像");
        Check(Score("宝箱", "宝箱") == 100 && Score("Chest", "Chest") == 100, "名字候选：一模一样最像");
        Check(Score("鳀鱼", "竹鱼竿") == 0 && Score("木头", "石头") == 0, "名字候选：两个字只共有一个字不算像（真机“鳀鱼”给出“竹鱼竿”）");
        var shopCand = tActions.GetMethod("ShopCandidates", Any);
        var spotType = tActions.GetNestedType("ShopSpot", Any);
        var spots = Activator.CreateInstance(typeof(List<>).MakeGenericType(spotType));
        var cands = (List<string>)shopCand.Invoke(null, new object[] { "HighlandsDwarf", new[] { "SeedShop", "FlashShifter.StardewValleyExpandedCP_HighlandsDwarfShop", "Blacksmith" }, spots });
        Check(cands.Count > 0 && cands[0].Contains("HighlandsDwarfShop"), "店名写错给候选：HighlandsDwarf → " + string.Join("、", cands));

        var tTools = asm.GetType("BlindWhaleFarmer.Tools");
        var cost = tTools.GetMethod("CostNote", Any);
        string Cost(int t0, int d0, float s0, int t1, int d1, float s1) => (string)cost.Invoke(null, new object[] { t0, d0, s0, t1, d1, s1 });
        string costLine = Cost(810, 5, 192, 920, 5, 126);
        Check(costLine == "⏱ 用时 1 小时 10 分（08:10 → 09:20），体力 192 → 126（-66）", "动作耗时和体力：" + costLine);
        Check(Cost(810, 5, 192, 810, 5, 192) == null, "时间没走、体力没变（查询、界面）：不写耗时");
        Check(Cost(700, 5, 100, 720, 5, 100) == "⏱ 用时 20 分钟（07:00 → 07:20）", "只走了时间：只写用时");
        Check(Cost(810, 5, 192, 810, 5, 180) == "⏱ 体力 192 → 180（-12）", "时间冻住（单机思考、节日）只写体力");
        Check(Cost(2350, 5, 50, 600, 6, 270).Contains("第二天"), "跨天只写时间点：" + Cost(2350, 5, 50, 600, 6, 270));

        var limit = tTools.GetMethod("WithTimeLimit", Any);
        string Lim(int sec) => (string)limit.Invoke(null, new object[] { "钓鱼。", sec });
        Check(Lim(900).Contains("最长约 15 分钟现实时间") && Lim(900).Contains("get_state"), "长动作描述写上最长多久：" + Lim(900));
        Check(Lim(20) == "钓鱼。" && Lim(60) == "钓鱼。", "60 秒以内的动作不写，省 token");
        Check(Desc("fish").Contains("最长约 15 分钟") && Desc("festival").Contains("最长约 10 分钟") && !Desc("craft").Contains("最长约"), "schema 里 fish/festival 有时限、craft 没有");

        var hud = asm.GetType("BlindWhaleFarmer.Hud").GetMethod("McpStatusLine", Any);
        string Hud(bool thinking, bool frozen) => (string)hud.Invoke(null, new object[] { thinking, frozen });
        Check(Hud(true, true).Contains("时间已冻结") && !Hud(true, false).Contains("冻结") && Hud(true, false).Contains("时间照常走"), "HUD：真冻住了才说时间已冻结，联机时说时间照常走");
        Check(Hud(false, false) == "[MCP 客户端操作中]", "HUD：操作中不提时间");

        var today = tObserver.GetMethod("TodayLine", Any);
        string Today(string[] b, string f) => (string)today.Invoke(null, new object[] { b, f });
        Check(Today(new[] { "艾米丽" }, null).StartsWith("今日：生日 艾米丽") && Today(new[] { "艾米丽" }, null).Contains("节日 无"), "今日一行：有生日就写名字：" + Today(new[] { "艾米丽" }, null));
        Check(Today(new string[0], "复活节") == "今日：生日 无；节日 复活节", "今日一行：没人生日、有节日");

        var cabin = tObserver.GetMethod("CabinLine", Any);
        string cl = (string)cabin.Invoke(null, new object[] { 74, 44, new Point(76, 47), 10 });
        Check(cl.StartsWith("你的小屋：农场 (74,44)") && cl.Contains("门口 (76,47)") && cl.Contains("10 格") && cl.Contains("shared=true"), "你的小屋：位置、门口、自己的田范围、shared：" + Shorten(cl));
        var fieldDist = tActions.GetMethod("FieldDistance", Any);
        Check(tActions.GetField("OwnFieldRadius", Any).GetValue(null) is int radius && radius == 10, "农场工人自己的田：门口 10 格内");
        var tillProps2 = schema.First(t => t["function"]["name"].GetValue<string>() == "till_area")["function"]["parameters"]["properties"];
        var plantProps = schema.First(t => t["function"]["name"].GetValue<string>() == "plant_seeds")["function"]["parameters"]["properties"];
        Check(tillProps2["shared"]?["type"]?.GetValue<string>() == "boolean" && plantProps["shared"]?["type"]?.GetValue<string>() == "boolean", "till_area / plant_seeds 有 shared(boolean)");

        // WorkBuddy 真机：gift_info 写 name、dialogue 写 npc 被整段拒绝。常见写法都认，用错工具时指路
        string Run0(string tool, string json) => run(tool, json).GetAwaiter().GetResult();
        foreach (var (tool, json) in new[]
        {
            ("gift_info", "{\"name\":\"Sophia\"}"), ("gift_info", "{\"villager\":\"Sophia\"}"), ("gift_info", "{\"character\":\"Sophia\"}"),
            ("gift_info", "{\"target\":\"Sophia\"}"), ("gift_info", "{\"who\":\"Sophia\"}"), ("gift_info", "{\"npc_name\":\"Sophia\"}"),
            ("give_gift", "{\"name\":\"Sophia\",\"item\":\"Daffodil\"}"), ("give_gift", "{\"npc\":\"Sophia\",\"gift\":\"Daffodil\"}"),
            ("interact", "{\"name\":\"Sophia\"}"), ("interact", "{\"villager\":\"Sophia\"}"),
            ("eat", "{\"food\":\"Salad\"}"), ("buy", "{\"name\":\"Parsnip Seeds\",\"quantity\":3}"), ("sell", "{\"name\":\"Parsnip\",\"quantity\":2}"),
            ("travel_to", "{\"place\":\"Town\"}"), ("travel_to", "{\"destination\":\"Town\"}"), ("fish", "{\"times\":3}"), ("say", "{\"message\":\"你好\"}"),
            ("open_shop", "{\"shop\":\"SeedShop\"}"), ("build", "{\"name\":\"Coop\"}"), ("cook", "{\"dish\":\"Salad\"}"),
        })
        {
            string rr = Run0(tool, json);
            Check(!rr.Contains("不认识参数"), $"{tool}{json} 别名被认：" + Shorten(rr));
        }
        string rDlg = Run0("dialogue", "{\"npc\":\"索菲亚\"}");
        Check(rDlg.Contains("不认识参数 npc") && rDlg.Contains("interact") && rDlg.Contains("npc=名字"), "dialogue 写 npc 是用错工具：指路 interact：" + Shorten(rDlg));
        string rUnknownGift = Run0("gift_info", "{\"colour\":\"red\"}");
        Check(rUnknownGift.Contains("不认识参数 colour") && !rUnknownGift.Contains("interact"), "别的工具写错参数名不乱指路");

        // 聊天开关：AI 在游戏聊天里说话由 AllowChat 控制（默认开），关着时 say 直接拒绝，也不催它回话
        var tConfig2 = asm.GetType("BlindWhaleFarmer.ModConfig");
        var cfg = Activator.CreateInstance(tConfig2);
        Check((bool)tConfig2.GetProperty("AllowChat").GetValue(cfg) == true, "config 的 AllowChat 默认是开");
        var allowField = tActions.GetField("AllowChat", Any);
        Check(allowField != null, "GameActions.AllowChat 存在，模组里所有 AI 发话都走它");
        allowField.SetValue(null, false);
        // 离线时工具调用会先排到主线程、再报“世界未就绪”，走不到 say 里的检查：直接调 Say
        var sayMethod = tActions.GetMethod("Say", Any);
        var gaInstance = Activator.CreateInstance(tActions, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { null, null }, null);
        var sayResults = ((System.Collections.IEnumerable)sayMethod.Invoke(gaInstance, new object[] { "你好" })).Cast<object>().ToList();
        string rSayOff = (string)sayResults.First(r => r.GetType().Name == "ActionResult").GetType().GetProperty("Message").GetValue(sayResults.First(r => r.GetType().Name == "ActionResult"));
        Check(rSayOff.StartsWith("失败：聊天已关闭") && rSayOff.Contains("别人说的话你照样听得到"), "聊天关着：say 直接拒绝并说明：" + Shorten(rSayOff));
        var reminder = tActions.GetMethod("TakeReplyReminder", Any);
        Check(reminder.Invoke(null, null) == null, "聊天关着：不再催“你还没用 say 回”");
        var advice = tActions.GetMethod("ModeAdvice", Any);
        string adviceOff = (string)advice.Invoke(null, null);
        Check(adviceOff.Contains("聊天已关闭") && !adviceOff.Contains("做完用 say 汇报"), "聊天关着：模式说明不再叫它 say 汇报：" + Shorten(adviceOff));
        allowField.SetValue(null, true);
        string adviceOn = (string)advice.Invoke(null, null);
        Check(adviceOn.Contains("say") && !adviceOn.Contains("聊天已关闭"), "聊天打开：模式说明照旧");
        allowField.SetValue(null, false);
        var aiSay = tActions.GetMethod("AiSay", Any);
        Check(aiSay != null && !(bool)aiSay.Invoke(null, new object[] { "好的" }), "AiSay 聊天关着返回 false、什么都不发");
        var chatSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(repo2().FullName, "BlindWhaleFarmer", "GameActions.Chat.cs"));
        Check(System.Text.RegularExpressions.Regex.Matches(chatSrc, @"chatBox[?]?\.textBoxEnter").Count == 2, "GameActions.Chat.cs 里直接发聊天的只剩 AiSay 和玩家要求发话的 say 两处");

        // sell / ship_items 的 count：同名东西分在几格（普通、银星……）时一起算，不再只卖第一格
        var planSell = tActions.GetMethod("PlanSell", Any);
        List<(int slot, int take)> Plan(int? count, params (int slot, int quality, int stack)[] stacks) =>
            (List<(int slot, int take)>)planSell.Invoke(null, new object[] { stacks, count });
        var p1 = Plan(99, (3, 0, 1), (9, 1, 1));
        Check(p1.Count == 2 && p1.Sum(t => t.take) == 2, "真机的情况：普通 1 + 银星 1，count=99 → 两格都卖，共 2 个：" + string.Join(",", p1));
        var p2 = Plan(null, (3, 0, 4), (9, 1, 2), (5, 2, 1));
        Check(p2.Sum(t => t.take) == 7 && p2.Count == 3, "不写 count：全部卖光（4+2+1=7）");
        var p3 = Plan(5, (3, 2, 3), (9, 0, 4), (5, 1, 2));
        Check(p3.Sum(t => t.take) == 5 && p3[0] == (9, 4) && p3[1] == (5, 1), "count=5：便宜的品质先卖（普通 4，再银星 1，金星留着）：" + string.Join(",", p3));
        var p4 = Plan(2, (3, 0, 10));
        Check(p4.Count == 1 && p4[0] == (3, 2), "只有一格时 count 照旧：10 个里卖 2 个");
        Check(Plan(0, (3, 0, 5)).Sum(t => t.take) == 1 && Plan(-3, (3, 0, 5)).Sum(t => t.take) == 1, "count 写 0 或负数：至少卖 1 个，和以前一样");
        var uq = tUtil.GetMethod("QualityFromSpec", Any);
        Check((int)uq.Invoke(null, new object[] { "沙丁鱼 金星" }) == 2 && (int)uq.Invoke(null, new object[] { "沙丁鱼" }) == -1 && (int)uq.Invoke(null, new object[] { "比目鱼铱星" }) == 4, "写了品质：金星 2、铱星 4，没写 -1");
        var sellProps = schema.First(t => t["function"]["name"].GetValue<string>() == "sell")["function"]["parameters"]["properties"];
        Check(sellProps["all"]?["type"]?.GetValue<string>() == "boolean" && sellProps["count"] != null, "sell 有 count 和 all(boolean) 两个参数");
        string rSellAll = Run0("sell", "{\"item\":\"沙丁鱼\",\"all\":true}");
        Check(!rSellAll.Contains("不认识参数"), "sell 写 all 不再被拒绝：" + Shorten(rSellAll));

        // 海草被 trash_item 扔了：能吃、体力又不满，就该吃，不扔
        var worth = tActions.GetMethod("IsWorthEating", Any);
        bool Worth(int edibility, bool cooking, bool reserved, int price) => (bool)worth.Invoke(null, new object[] { edibility, cooking, reserved, price });
        Check(Worth(5, false, false, 20), "海草（能吃、一个 20g）值得吃");
        Check(!Worth(5, false, false, 80), "沙丁鱼（一个 80g）不当零食吃，留着卖");
        Check(!Worth(5, true, false, 10), "料理留着当救命的，不算值得吃");
        Check(!Worth(5, false, true, 10), "标了【留着】的不吃（收集包要交）");
        Check(!Worth(0, false, false, 5) && !Worth(-300, false, false, 5), "吃不了的（Edibility 0 或 -300）不算");
        var low = tActions.GetMethod("StaminaLowEnoughToEat", Any);
        bool Low(float cur, float max) => (bool)low.Invoke(null, new object[] { cur, max });
        Check(Low(100, 270) && Low(215, 270) && !Low(216, 270) && !Low(270, 270), "体力低于 80% 才建议吃（满了吃是浪费）：216/270 刚好不算");
        Check(fishSrc().Contains("能吃的东西（海草、藻类）直接 eat"), "钓鱼背包满的提示先说能吃的直接 eat");

        // 种地要一整块：门口第一块田找大块方正的，扩田时让整块外框更方
        var cand = tActions.GetMethod("FieldCandidates", Any);
        var door = new Point(76, 23);
        var c8 = (List<Rectangle>)cand.Invoke(null, new object[] { door, 8 });
        Check(c8.Count > 0 && c8.All(r => r.Width == 8 && r.Height == 8), $"候选田都是 8x8（共 {c8.Count} 个）");
        Check(c8.All(r => Math.Max(Math.Max(0, Math.Max(r.Left - door.X, door.X - (r.Right - 1))), Math.Max(0, Math.Max(r.Top - door.Y, door.Y - (r.Bottom - 1)))) <= 10), "每个候选整块都在门口 10 格内");
        Check(c8.All(r => !(r.Contains(door.X, door.Y + 1) || r.Contains(door.X, door.Y))), "候选田不压着门口和门口正下方一格（留给进出）");
        Check(c8.Any(r => r.Top > door.Y) && c8.Any(r => r.Left > door.X) && c8.Any(r => r.Right <= door.X), "候选田在门口下方、右边、左边都有");
        var sq = tActions.GetMethod("Squareness", Any);
        double Sq(Rectangle a, Rectangle b) => (double)sq.Invoke(null, new object[] { a, b });
        var mainField = new Rectangle(75, 25, 5, 3);
        double down = Sq(mainField, new Rectangle(75, 28, 5, 2));    // 往下 2 行：5 x 5，正方形
        double right = Sq(mainField, new Rectangle(80, 25, 2, 3));   // 往右 2 列：7 x 3，长条
        Check(down < right && Math.Abs(down - 1.0) < 1e-9, $"往下扩出正方形（比值 {down:0.0}）比往右扩成长条（{right:0.0}）更方正");
        Check(Math.Abs(Sq(new Rectangle(0, 0, 6, 6), new Rectangle(0, 0, 6, 6)) - 1.0) < 1e-9, "正方形的方正度是 1");

        // SVE 的 JojaMart 有左右两处柜台（真机：(6,24)-(6,26) 没人看店，(10,24)-(10,26) 才开得了店）
        var groupCounters = tActions.GetMethod("GroupCounters", Any);
        var joja = new[] { new Point(6, 24), new Point(6, 25), new Point(6, 26), new Point(10, 24), new Point(10, 25), new Point(10, 26) };
        var grouped = (List<Point>)groupCounters.Invoke(null, new object[] { joja });
        Check(grouped.Count == 2 && grouped.Contains(new Point(6, 25)) && grouped.Contains(new Point(10, 25)), "JojaMart 六个柜台格并成两处，各取中间那格：" + string.Join(",", grouped));
        var one = (List<Point>)groupCounters.Invoke(null, new object[] { new[] { new Point(3, 4) } });
        Check(one.Count == 1 && one[0] == new Point(3, 4), "只有一格柜台就是它自己");
        var far = (List<Point>)groupCounters.Invoke(null, new object[] { new[] { new Point(1, 1), new Point(9, 9) } });
        Check(far.Count == 2, "隔得远的两个柜台算两处");

        // 真机：sell 把标着【留着：社区中心“海鱼”要交】的海星卖了。现在 sell 和 ship_items 一样拦，要卖传 force
        var sellProps2 = schema.First(t => t["function"]["name"].GetValue<string>() == "sell")["function"]["parameters"]["properties"];
        Check(sellProps2["force"]?["type"]?.GetValue<string>() == "boolean", "sell 有 force(boolean)：收集包要交的东西要卖得明说");
        var sellSig = tActions.GetMethod("Sell", Any);
        Check(sellSig.GetParameters().Select(p2 => p2.Name).SequenceEqual(new[] { "itemSpec", "count", "force" }), "GameActions.Sell(itemSpec, count, force)");
        string rSellForce = Run0("sell", "{\"item\":\"海星\",\"force\":true}");
        Check(!rSellForce.Contains("不认识参数"), "sell 写 force 不再被拒绝：" + Shorten(rSellForce));

        // 玩 3 天发现的问题：买了种不熟的种子、水槽灌不了水、说话的人明明在却说走开了、整理背包丢了农具
        var gaSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(repo2().FullName, "BlindWhaleFarmer", "GameActions.cs"));
        var tidySrc = System.IO.File.ReadAllText(System.IO.Path.Combine(repo2().FullName, "BlindWhaleFarmer", "GameActions.Tidy.cs"));
        Check(tActions.GetMethod("SeedWontRipen", Any) != null, "SeedWontRipen 存在：plant_seeds 和 buy 共用同一个“种不熟”判断");
        Check(gaSrc.Contains("SeedWontRipen(seedItem, Game1.getFarm()") && gaSrc.Contains("SeedWontRipen(seed, loc"), "buy 和 plant_seeds 都调 SeedWontRipen");
        var buySrc = gaSrc.Substring(gaSrc.IndexOf("public IEnumerable<object> Buy(string spec"));
        Check(buySrc.IndexOf("SeedWontRipen") < buySrc.IndexOf("Player.Money") || !buySrc.Substring(0, 4000).Contains("Player.Money -="), "buy 在花钱之前先判断种不熟");
        Check(gaSrc.Contains("for (int x = 0; x < w; x++)") && gaSrc.Contains("CanRefillWateringCanOnTile(x, y)") && !gaSrc.Contains("me.X - 50; x <= me.X + 50"), "灌水搜整张地图，不再只搜身边 50 格");
        Check(gaSrc.Contains("wait < 5 * 60") && gaSrc.Contains("c.Name == villager.Name") && gaSrc.Contains("追了几趟"), "interact 找人：到了新地图先等 5 秒、按名字找，等不到再追（最多 3 趟），追不到给日程提示");
        Check(tidySrc.Contains("今天还要用的话先 take_items 拿回来"), "tidy_backpack 把农具存起来时提醒拿回来");
        var shipSrc = gaSrc.Substring(gaSrc.IndexOf("public IEnumerable<object> ShipItems"), 1800);
        Check(shipSrc.Contains("改出没标的那格"), "ship_items 点名的格标了【留着】、同名另一格没标时，改出没标的那格");

        // 新攻略也要写明只覆盖原版
        var guideDir = System.IO.Path.Combine(repo2().FullName, "BlindWhaleFarmer", "guide");
        var vanilla = System.IO.Directory.GetFiles(guideDir, "*.md").Where(f => !System.IO.Path.GetFileName(f).StartsWith("SVE-")).ToList();
        Check(vanilla.Count == 10 && vanilla.Any(f => System.IO.Path.GetFileName(f) == "沙漠节.md") && vanilla.Any(f => System.IO.Path.GetFileName(f) == "种田.md") && vanilla.Where(f => !System.IO.Path.GetFileName(f).StartsWith("农场类型")).All(f => System.IO.File.ReadAllText(f).Contains("本篇只覆盖原版")), $"{vanilla.Count} 篇原版攻略都写明只覆盖原版");

        // DSH 读插件 package.json 时直接 JSON.parse，开头带 UTF-8 BOM 会解析失败、插件整个不加载（1.1.26/1.1.27 踩过）
        foreach (var rel in new[] { "package.json", System.IO.Path.Combine("BlindWhaleFarmer", "manifest.json"), System.IO.Path.Combine("release", "BlindWhaleFarmer", "manifest.json") })
        {
            var path = System.IO.Path.Combine(repo2().FullName, rel);
            if (!System.IO.File.Exists(path)) continue;
            var head = System.IO.File.ReadAllBytes(path);
            Check(!(head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF), $"{rel} 不带 UTF-8 BOM（带了 DSH 解析失败，插件不加载）");
        }

        // 商店、升级、砸晶球都要像玩家一样走到柜台按动作键：源码里不许再出现直接开界面的调用（注释除外）
        var srcDir = System.IO.Path.Combine(repo2().FullName, "BlindWhaleFarmer");
        var directOpen = System.IO.Directory.GetFiles(srcDir, "*.cs")
            .SelectMany(f => System.IO.File.ReadAllLines(f).Select((line, i) => (file: System.IO.Path.GetFileName(f), line, no: i + 1)))
            .Where(t => !t.line.TrimStart().StartsWith("//") && !t.line.TrimStart().StartsWith("///")
                && (t.line.Contains("TryOpenShopMenu(") || t.line.Contains("new GeodeMenu(") || t.line.Contains("new ShopMenu(")))
            .Select(t => $"{t.file}:{t.no}").ToList();
        Check(directOpen.Count == 0, "不再直接调 TryOpenShopMenu / new GeodeMenu / new ShopMenu 隔空开界面：" + (directOpen.Count == 0 ? "没有" : string.Join("、", directOpen)));

        // 姜岛睡觉就近选岛上小屋
        var preferIsland = tActions.GetMethod("ShouldPreferIslandHome", Any);
        bool PreferIsland(bool inIsland, bool restored, bool hasBed) => (bool)preferIsland.Invoke(null, new object[] { inIsland, restored, hasBed });
        Check(PreferIsland(true, true, true), "在姜岛、小屋修好且有床：睡岛上小屋");
        Check(!PreferIsland(false, true, true) && !PreferIsland(true, false, true) && !PreferIsland(true, true, false), "不在姜岛、小屋没修好或没床：回原本的家");

        // 铁匠铺取升级工具：只站在柜台南侧 Y > counter.Y，且 upgrade_tool 不传 tool 通过参数校验
        string rUpgradeNoTool = Run0("upgrade_tool", "{}");
        Check(rUpgradeNoTool.Contains("游戏世界未就绪"), "upgrade_tool 领取时不传 tool 通过参数校验");
        var filterStands = tActions.GetMethod("FilterCounterStands", Any);
        var bsStands = (List<Point>)filterStands.Invoke(null, new object[]
        {
            new Point(3, 14),
            new[] { new Point(3, 13), new Point(3, 15) },
            new[] { new Point(2, 13), new Point(4, 15) },
            true
        });
        Check(bsStands.SequenceEqual(new[] { new Point(3, 15) }), "铁匠铺柜台 (3,14) 只选南侧 (3,15)，不站北侧 (3,13)");
        var bsDiag = (List<Point>)filterStands.Invoke(null, new object[]
        {
            new Point(3, 14),
            new[] { new Point(3, 13) },
            new[] { new Point(2, 13), new Point(4, 15) },
            true
        });
        Check(bsDiag.SequenceEqual(new[] { new Point(4, 15) }), "南侧正格不可站时退回南侧斜角 (4,15)");

        // 花舞节舞伴：必须传 partner 或 none，不静默替玩家挑人
        var festProps = schema.First(t => t["function"]["name"].GetValue<string>() == "festival")["function"]["parameters"]["properties"];
        Check(festProps["partner"]?["type"]?.GetValue<string>() == "string", "festival schema 有 partner(string)");
        Check(Run0("festival", "{\"action\":\"start\",\"partner\":\"海莉\"}").Contains("游戏世界未就绪"), "festival start 传 partner 通过参数校验");
        var planDance = tActions.GetMethod("PlanDancePartnerChoice", Any);
        var danceActors = new (string name, string displayName, bool canDance, bool willAccept, int hearts, bool isSpouse, string rejectReason)[]
        {
            ("Haley", "海莉", true, true, 5, false, ""),
            ("Sebastian", "塞巴斯蒂安", true, true, 6, false, ""),
            ("Shane", "谢恩", true, false, 2, false, "和 谢恩 好感只有 2 心，花舞节要 4 心以上才会答应"),
        };
        var rNoPartner = ((string action, int matchIndex, string message))planDance.Invoke(null, new object[] { null, false, danceActors });
        Check(rNoPartner.action == "ask" && rNoPartner.message.Contains("海莉") && rNoPartner.message.Contains("塞巴斯蒂安") && !rNoPartner.message.Contains("谢恩"),
            "花舞节不传 partner 时列出可邀请的人，不静默挑人：" + Shorten(rNoPartner.message));
        var rPickHaley = ((string action, int matchIndex, string message))planDance.Invoke(null, new object[] { "海莉", false, danceActors });
        Check(rPickHaley.action == "invite" && rPickHaley.matchIndex == 0, "花舞节传 partner=海莉 选中海莉");
        var rRejectLow = ((string action, int matchIndex, string message))planDance.Invoke(null, new object[] { "谢恩", false, danceActors });
        Check(rRejectLow.action == "fail" && rRejectLow.message.Contains("2 心") && rRejectLow.message.Contains("海莉"), "选好感不够的人报错并列出候选");
        var rSkipNone = ((string action, int matchIndex, string message))planDance.Invoke(null, new object[] { "none", false, danceActors });
        Check(rSkipNone.action == "skip", "花舞节传 partner=none 跳过邀请");

        // till_area 规划与失败下一步提示
        var clipRect = tActions.GetMethod("ClipRect", Any);
        var clipped = ((int x1, int y1, int x2, int y2))clipRect.Invoke(null, new object[] { 90, 50, 10, -5, 80, 65 });
        Check(clipped == (10, 0, 79, 50), "ClipRect 自动规范坐标并裁剪到地图内");
        var orderTill = tActions.GetMethod("OrderTillTargets", Any);
        var tillPool = Enumerable.Range(0, 10).SelectMany(y => Enumerable.Range(0, 10).Select(x => new Point(x, y))).ToList();
        var sprCov = new HashSet<Point> { new(8, 8), new(8, 9), new(9, 8) };
        var orderedTill = (List<Point>)orderTill.Invoke(null, new object[] { tillPool, new Point(0, 0), sprCov, 80 });
        Check(orderedTill.Count == 80 && orderedTill.Take(3).All(sprCov.Contains), "till_area 尽量开满 80 格且优先洒水器覆盖格");
        var fmtTillFail = tActions.GetMethod("FormatTillFailure", Any);
        string failTilled = (string)fmtTillFail.Invoke(null, new object[] { 12, 0, 0, 0, 0, 0, 0 });
        Check(failTilled.Contains("12 格已经耕过") && failTilled.Contains("plant_seeds"), "till_area 全是空耕地时引导去 plant_seeds");

        // plant_seeds 不再把 <4 格零散地/异作物田当硬上限，顺延全放进 order，并汇报剩余种子与范围
        var planPlant = tActions.GetMethod("PlanPlantOrder", Any);
        var fieldType = tActions.GetNestedType("Field", Any);
        var fCtor = fieldType.GetConstructor(Any, null, Type.EmptyTypes, null);
        object MakeField(IEnumerable<Point> pts, int crops, params string[] seeds)
        {
            var f = fCtor.Invoke(null);
            ((List<Point>)fieldType.GetField("Tiles", Any).GetValue(f)).AddRange(pts);
            fieldType.GetField("Crops", Any).SetValue(f, crops);
            var sset = (HashSet<string>)fieldType.GetField("SeedIds", Any).GetValue(f);
            foreach (var sid in seeds) sset.Add(sid);
            return f;
        }
        var fMainSame = MakeField(new[] { new Point(10, 10), new Point(11, 10), new Point(10, 11), new Point(11, 11) }, 1, "472");
        var fMainEmpty = MakeField(new[] { new Point(20, 10), new Point(21, 10), new Point(20, 11), new Point(21, 11) }, 0);
        var fMainOther = MakeField(new[] { new Point(30, 10), new Point(31, 10), new Point(30, 11), new Point(31, 11) }, 2, "473");
        var fScattered = MakeField(new[] { new Point(40, 10), new Point(41, 10) }, 0);
        var emptyOnly = new[] { new Point(10, 10), new Point(20, 10), new Point(30, 10), new Point(40, 10), new Point(41, 10) };
        var arrFields = Array.CreateInstance(fieldType, 4);
        arrFields.SetValue(fScattered, 0);
        arrFields.SetValue(fMainOther, 1);
        arrFields.SetValue(fMainEmpty, 2);
        arrFields.SetValue(fMainSame, 3);
        var plantPlan = ((List<Point> order, int otherCropCount, int scatteredCount))planPlant.Invoke(null,
            new object[] { arrFields, emptyOnly.ToHashSet(), "472", new Point(0, 0) });
        Check(plantPlan.order.Count == 5 && plantPlan.order[0] == new Point(10, 10) && plantPlan.order[1] == new Point(20, 10)
            && plantPlan.order[2] == new Point(30, 10) && plantPlan.otherCropCount == 1 && plantPlan.scatteredCount == 2,
            "PlanPlantOrder 把他作物田与零散地排在后面而不硬跳过");
        var fmtPlant = tActions.GetMethod("FormatPlantSummary", Any);
        string plantSum = (string)fmtPlant.Invoke(null, new object[]
        {
            7, "防风草种子", 303, new List<Point> { new(40, 10), new(41, 11) }, 1, 2, 3
        });
        Check(plantSum.Contains("剩余种子 303 颗") && plantSum.Contains("剩余空耕地 2 格")
            && plantSum.Contains("x1=40,y1=10,x2=41,y2=11") && plantSum.Contains("clear_area"),
            "plant_seeds 返回剩余种子、剩余空耕地、下次范围与枯死提示：" + Shorten(plantSum));

        // clear_area 枯死作物与不伤活作物、till_area 枯死提示
        string failDead = (string)fmtTillFail.Invoke(null, new object[] { 0, 9, 0, 0, 0, 0, 0 });
        Check(failDead.Contains("9 格有枯死作物") && failDead.Contains("clear_area"), "till_area 遇到枯死作物明确提示先 clear_area：" + Shorten(failDead));
        var targetedScythe = tActions.GetMethod("ShouldUseTargetedScythe", Any);
        Check((bool)targetedScythe.Invoke(null, new object[] { 'd', false })
            && (bool)targetedScythe.Invoke(null, new object[] { 'w', true })
            && !(bool)targetedScythe.Invoke(null, new object[] { 'w', false }),
            "枯死作物或靠近活作物的杂草走定点镰刀，绝不误伤活作物");

        // crystal_cave 晶石洞谜题工具与顺序格式化
        Check(schema.Any(t => t["function"]["name"].GetValue<string>() == "crystal_cave"), "schema 注册了 crystal_cave");
        Check(Run0("crystal_cave", "{}").Contains("游戏世界未就绪"), "crystal_cave 通过参数校验");
        var fmtSeq = tActions.GetMethod("FormatCrystalSequence", Any);
        string seqFull = (string)fmtSeq.Invoke(null, new object[] { new[] { 0, 2, 4 }, 0 });
        Check(seqFull.Contains("#1(3,4)紫") && seqFull.Contains("#3(6,7)红") && seqFull.Contains("#5(9,4)橙"), "晶石序列格式化出编号、坐标与颜色：" + seqFull);
        string seqMid = (string)fmtSeq.Invoke(null, new object[] { new[] { 0, 2, 4 }, 1 });
        Check(!seqMid.Contains("#1(3,4)") && seqMid.Contains("#3(6,7)") && seqMid.Contains("已按 1 颗"), "晶石序列支持从中断步继续：" + seqMid);

        // gem_shrine 宝石鸟祭坛工具、宝石归一化与状态格式化
        var gemProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "gem_shrine")?["function"]["parameters"]["properties"] as JsonObject;
        Check(gemProps != null && gemProps["north"] != null && gemProps["south"] != null && gemProps["east"] != null && gemProps["west"] != null,
            "schema 注册了 gem_shrine 且包含 north/south/east/west");
        Check(Run0("gem_shrine", "{\"north\":\"绿宝石\"}").Contains("游戏世界未就绪"), "gem_shrine 通过参数校验");
        var normGem = tActions.GetMethod("NormalizeGemBirdGem", Any);
        Check((string)normGem.Invoke(null, new object[] { "Emerald" }) == "60"
            && (string)normGem.Invoke(null, new object[] { "海蓝宝石" }) == "62"
            && (string)normGem.Invoke(null, new object[] { "红宝石" }) == "64"
            && (string)normGem.Invoke(null, new object[] { "Amethyst" }) == "66"
            && (string)normGem.Invoke(null, new object[] { "黄水晶" }) == "68"
            && (string)normGem.Invoke(null, new object[] { "take" }) == "take"
            && normGem.Invoke(null, new object[] { "钻石" }) == null,
            "NormalizeGemBirdGem 只接受 5 种宝石鸟宝石或 take");

        // mermaid_puzzle 姜岛雨天美人鱼谜题、长笛块音高推算与 interact times 参数
        Check(schema.Any(t => t["function"]["name"].GetValue<string>() == "mermaid_puzzle"), "schema 注册了 mermaid_puzzle");
        Check(Run0("mermaid_puzzle", "{}").Contains("游戏世界未就绪"), "mermaid_puzzle 通过参数校验");
        var interactProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "interact")?["function"]["parameters"]["properties"] as JsonObject;
        Check(interactProps != null && interactProps["times"] != null, "interact 注册了 times 参数支持长笛块/鼓块连调");
        Check(Run0("interact", "{\"x\":16,\"y\":27,\"times\":11}").Contains("游戏世界未就绪"), "interact 带 times 通过参数校验");
        var nextPitch = tActions.GetMethod("NextFlutePitch", Any);
        Check((int)nextPitch.Invoke(null, new object[] { 0 }) == 100
            && (int)nextPitch.Invoke(null, new object[] { 2300 }) == 2400
            && (int)nextPitch.Invoke(null, new object[] { 2400 }) == 0,
            "NextFlutePitch 与原版 CheckForActionOnFluteBlock 25 档循环一致");
        var clicksNeeded = tActions.GetMethod("FluteClicksNeeded", Any);
        Check((int)clicksNeeded.Invoke(null, new object[] { 0, 200 }) == 2
            && (int)clicksNeeded.Invoke(null, new object[] { 200, 200 }) == 0
            && (int)clicksNeeded.Invoke(null, new object[] { 2300, 0 }) == 2
            && (int)clicksNeeded.Invoke(null, new object[] { 2400, 200 }) == 3
            && (int)clicksNeeded.Invoke(null, new object[] { 1100, 900 }) == 23,
            "FluteClicksNeeded 正确计算绕回 0~2400 所需点击数");
        var mermaidPitches = (int[])tActions.GetField("MermaidFlutePitches", Any).GetValue(null);
        Check(mermaidPitches.Length == 5
            && mermaidPitches[1] - mermaidPitches[0] == 900
            && mermaidPitches[2] - mermaidPitches[1] == -200
            && mermaidPitches[3] - mermaidPitches[2] == -400
            && mermaidPitches[4] - mermaidPitches[3] == 200,
            "MermaidFlutePitches 相对音差匹配 IslandSouthEast.OnFlutePlayed (+900,-200,-400,+200)");
        var fmtMermaid = tActions.GetMethod("FormatMermaidPuzzleStatus", Any);
        string mDone = (string)fmtMermaid.Invoke(null, new object[] { true, true, null });
        string mRain = (string)fmtMermaid.Invoke(null, new object[] { false, true, new[] { (new Microsoft.Xna.Framework.Point(16, 27), 200), (new Microsoft.Xna.Framework.Point(18, 27), 1100) } });
        string mDry = (string)fmtMermaid.Invoke(null, new object[] { false, false, null });
        Check(mDone.Contains("已完成") && mRain.Contains("mermaid_puzzle") && mRain.Contains("(16,27)=200[第2档]") && mDry.Contains("没下雨"),
            "FormatMermaidPuzzleStatus 准确反映完成/雨天已有长笛块/晴天状态：" + mRain);

        // raccoon 大树桩浣熊任务工具、动作与各阶段状态格式
        var racProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "raccoon")?["function"]["parameters"]["properties"] as JsonObject;
        Check(racProps != null && racProps["action"] != null, "schema 注册了 raccoon(action)");
        Check(Run0("raccoon", "{\"action\":\"status\"}").Contains("游戏世界未就绪"), "raccoon status 通过参数校验");
        Check(Run0("raccoon", "{\"action\":\"unknown\"}").Contains("游戏世界未就绪"), "raccoon 未知 action 通过 schema 进入动作校验");
        var normRac = tActions.GetMethod("NormalizeRaccoonAction", Any);
        Check((string)normRac.Invoke(null, new object[] { null }) == "status"
            && (string)normRac.Invoke(null, new object[] { "FIX" }) == "fix"
            && (string)normRac.Invoke(null, new object[] { "交付" }) == "donate"
            && (string)normRac.Invoke(null, new object[] { "shop" }) == "shop"
            && normRac.Invoke(null, new object[] { "bad" }) == null,
            "NormalizeRaccoonAction 识别 status/fix/donate/shop");
        var fmtRac = tActions.GetMethod("FormatRaccoonState", Any);
        string racNeedWood = (string)fmtRac.Invoke(null, new object[] { true, false, false, false, 0, 0, false, null, 45 });
        Check(racNeedWood.Contains("45/100") && racNeedWood.Contains("action=fix"), "浣熊大树桩待修时给出硬木进度与 fix 指引：" + racNeedWood);
        var racReqs = new[] { ("熏大海参", 1, 1, false), ("大蒜 x5", 2, 5, false) };
        string racActive = (string)fmtRac.Invoke(null, new object[] { true, true, true, true, 1, 8, true, racReqs, 100 });
        Check(racActive.Contains("熏大海参") && racActive.Contains("1/1") && racActive.Contains("大蒜 x5") && racActive.Contains("2/5") && racActive.Contains("donate"),
            "浣熊活跃轮次列出所需风味物品与背包数量：" + racActive);

        // 一起睡：sleep 参数、别名、双人床空位避让
        var sleepProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "sleep")?["function"]["parameters"]["properties"] as JsonObject;
        Check(sleepProps != null && sleepProps["with"] != null && sleepProps["location"] != null, "sleep schema 注册了 with 和 location 参数");
        Check(Run0("sleep", "{\"with\":\"小明\"}").Contains("游戏世界未就绪"), "sleep 传 with 通过参数校验");
        Check(Run0("sleep", "{\"player\":\"random\"}").Contains("游戏世界未就绪"), "sleep 别名 player=random 通过参数校验");
        Check(Run0("sleep", "{\"house\":\"FarmHouse\"}").Contains("游戏世界未就绪"), "sleep 别名 house=FarmHouse 通过参数校验");
        var orderBeds = tActions.GetMethod("OrderBedSpots", Any);
        var dblLeftBusy = (List<Point>)orderBeds.Invoke(null, new object[] { new Point(10, 5), 3, true, new[] { new Point(10, 6) } });
        Check(dblLeftBusy[0] == new Point(12, 6), "双人床左侧有人躺着时优先选右侧空位 (12,6)");
        var dblRightBusy = (List<Point>)orderBeds.Invoke(null, new object[] { new Point(10, 5), 3, true, new[] { new Point(12, 6) } });
        Check(dblRightBusy[0] == new Point(10, 6), "双人床右侧有人躺着时优先选左侧空位 (10,6)");
        var dblEmpty = (List<Point>)orderBeds.Invoke(null, new object[] { new Point(10, 5), 3, true, Array.Empty<Point>() });
        Check(dblEmpty[0] == new Point(11, 6), "双人床没人时默认选中间睡点 (11,6)");
        var sglBusy = (List<Point>)orderBeds.Invoke(null, new object[] { new Point(10, 5), 2, false, new[] { new Point(11, 6) } });
        Check(sglBusy[0] == new Point(10, 6), "单人床主睡点有人时优先避开选另一格 (10,6)");

        // 搬家具：furniture 工具注册、动作归一与状态码说明
        var furnProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "furniture")?["function"]["parameters"]["properties"] as JsonObject;
        Check(furnProps != null && furnProps["action"] != null && furnProps["to_x"] != null && furnProps["times"] != null, "schema 注册了 furniture 工具与参数");
        Check(Run0("furniture", "{\"action\":\"list\"}").Contains("游戏世界未就绪"), "furniture list 通过参数校验");
        Check(Run0("furniture", "{\"action\":\"place\",\"item\":\"单人床\",\"x\":5,\"y\":5}").Contains("游戏世界未就绪"), "furniture place 与别名 item 通过参数校验");
        var normFurn = tActions.GetMethod("NormalizeFurnitureAction", Any);
        Check((string)normFurn.Invoke(null, new object[] { null }) == "list"
            && (string)normFurn.Invoke(null, new object[] { "拿起" }) == "pickup"
            && (string)normFurn.Invoke(null, new object[] { "place" }) == "place"
            && (string)normFurn.Invoke(null, new object[] { "移动" }) == "move"
            && (string)normFurn.Invoke(null, new object[] { "rotate" }) == "rotate"
            && normFurn.Invoke(null, new object[] { "destroy" }) == null,
            "NormalizeFurnitureAction 识别 list/pickup/place/move/rotate");
        var descFurnStatus = tActions.GetMethod("DescribeFurniturePlacementStatus", Any);
        Check(descFurnStatus.Invoke(null, new object[] { 0 }) == null
            && ((string)descFurnStatus.Invoke(null, new object[] { 1 })).Contains("墙面")
            && ((string)descFurnStatus.Invoke(null, new object[] { 2 })).Contains("NoFurniture")
            && ((string)descFurnStatus.Invoke(null, new object[] { 3 })).Contains("地面家具")
            && ((string)descFurnStatus.Invoke(null, new object[] { -1 })).Contains("床旁"),
            "DescribeFurniturePlacementStatus 准确解释游戏放置错误码");

        // 路上遇到玩家发表情：开关、概率、冷却与防抖
        var passbyTrigger = tActions.GetMethod("ShouldTriggerPassbyEmote", Any);
        var nowUtc = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        bool TryPassby(bool en, float ch, int cd, DateTime? lastG, DateTime? lastM, double roll) =>
            (bool)passbyTrigger.Invoke(null, new object[] { en, ch, cd, nowUtc, lastG, lastM, roll });
        Check(!TryPassby(false, 1.0f, 5, null, null, 0.0), "PassbyEmoteEnabled=false 时不触发");
        Check(TryPassby(true, 0.35f, 5, null, null, 0.2), "概率命中且无冷却时触发");
        Check(!TryPassby(true, 0.35f, 5, null, null, 0.5), "概率未命中时不触发");
        Check(!TryPassby(true, 1.0f, 5, nowUtc.AddMinutes(-3), null, 0.0), "同一玩家冷却 5 分钟内不重复触发");
        Check(TryPassby(true, 1.0f, 5, nowUtc.AddMinutes(-6), null, 0.0), "冷却过后允许再次触发");
        Check(!TryPassby(true, 1.0f, 5, null, nowUtc.AddSeconds(-5), 0.0), "同一次路过未命中后 15 秒防抖不重复抽签");

        // 没事时跟着玩家卖萌：触发条件与 MainThread 抢占中止
        var idleTrigger = tActions.GetMethod("ShouldStartIdleFollow", Any);
        bool TryIdle(bool en = true, bool mp = true, bool takeOver = false, bool aiActive = true,
            bool busy = false, bool idleRun = false, bool thinking = false, bool chat = false, bool safe = true,
            int interval = 180, int idleSec = 200) =>
            (bool)idleTrigger.Invoke(null, new object[]
            {
                en, mp, takeOver, aiActive, busy, idleRun, thinking, chat, safe,
                interval, nowUtc, nowUtc.AddSeconds(-idleSec)
            });
        Check(TryIdle(), "联机空闲且满足间隔与安全条件时允许触发跟着玩家卖萌");
        Check(!TryIdle(en: false), "IdleFollowEnabled=false 时不触发");
        Check(!TryIdle(mp: false), "非联机时不触发");
        Check(!TryIdle(takeOver: true), "玩家接管时不触发");
        Check(!TryIdle(aiActive: false), "AI 未启用/未连接时不触发");
        Check(!TryIdle(busy: true), "有正式任务执行时不触发");
        Check(!TryIdle(thinking: true), "内置 AI 正在思考时不触发");
        Check(!TryIdle(chat: true), "有人在聊天说话时不触发");
        Check(!TryIdle(safe: false), "节日/剧情/菜单/钓鱼/战斗等不安全时机不触发");
        Check(!TryIdle(idleSec: 60), "空闲时间未达到触发间隔时不触发");

        // 换图途中（allowWarping=true）不算不安全，非换图时 freezeControls/warping 仍拦截
        var transitionSafe = tActions.GetMethod("IsSocialTransitionStateSafe", Any);
        bool TryTrans(bool allowWarping, bool locValid, bool warping, bool freeze) =>
            (bool)transitionSafe.Invoke(null, new object[] { allowWarping, locValid, warping, freeze });
        Check(!TryTrans(false, true, true, false), "默认不允许换图途中触发社交动作");
        Check(TryTrans(true, true, true, false), "空闲跟随允许换图途中继续");
        Check(TryTrans(true, false, true, true), "换图途中短暂 freezeControls/切图不中断空闲跟随");
        Check(!TryTrans(true, true, false, true), "非换图时 freezeControls 仍视为不安全");

        // MainThread 空闲协程被正式任务立刻抢占中止
        var mt = Activator.CreateInstance(asm.GetType("BlindWhaleFarmer.MainThread"), new object[] { null });
        var tMt = mt.GetType();
        bool idleDisposed = false;
        IEnumerable<object> DummyIdle()
        {
            try
            {
                while (true)
                    yield return null;
            }
            finally
            {
                idleDisposed = true;
            }
        }
        bool startedIdle = (bool)tMt.GetMethod("StartIdle", Any).Invoke(mt, new object[] { (Func<IEnumerable<object>>)DummyIdle });
        Check(startedIdle && (bool)tMt.GetProperty("IsIdleRunning", Any).GetValue(mt), "MainThread.StartIdle 启动空闲协程");
        tMt.GetMethod("Tick", Any).Invoke(mt, new object[] { true });
        Check((bool)tMt.GetProperty("IsIdleRunning", Any).GetValue(mt) && !(bool)tMt.GetProperty("IsBusy", Any).GetValue(mt), "空闲协程运行中不占用 IsBusy");
        IEnumerable<object> DummyReal() { yield break; }
        var realTask = (System.Threading.Tasks.Task)tMt.GetMethod("RunAction", Any).Invoke(mt, new object[]
        {
            (Func<IEnumerable<object>>)DummyReal, 5, false, false, "dummy", false, false, false, null
        });
        tMt.GetMethod("Tick", Any).Invoke(mt, new object[] { true });
        Check(idleDisposed && !(bool)tMt.GetProperty("IsIdleRunning", Any).GetValue(mt) && realTask.IsCompleted,
            "新任务到来时立刻中止空闲协程并顺利执行正式任务");

        // 秘密森林大树桩：全图 6 个大树桩（含超出 ScanRadius=16 的 5 个）在 AppendNearby 中全部列出，不会只看到入口最近的那 1 个
        if (town != null)
        {
            var woods = MakeLocation("Woods", true, 60, 32);
            var stumpCoords = new[] { new Point(46, 6), new Point(29, 7), new Point(26, 10), new Point(24, 6), new Point(34, 26), new Point(41, 26) };
            foreach (var p in stumpCoords)
                woods.resourceClumps.Add(new StardewValley.TerrainFeatures.ResourceClump(600, 2, 2, new Vector2(p.X, p.Y)));
            var obs = Activator.CreateInstance(tObserver, new object[] { Activator.CreateInstance(tConfig2), null, null });
            var appendNearby = tObserver.GetMethod("AppendNearby", Any);
            var sb = new System.Text.StringBuilder();
            appendNearby.Invoke(obs, new object[] { sb, woods, new Point(58, 15) });
            string nearby = sb.ToString();
            foreach (var p in stumpCoords)
                Check(nearby.Contains($"({p.X},{p.Y})"), $"秘密森林入口 (58,15) 也能看到大树桩 ({p.X},{p.Y})：" + Shorten(nearby));

            // 砍掉入口最近的 (46,6) 后站在 (46,5)，其余 5 个依然全部可见
            woods.resourceClumps.RemoveAt(0);
            sb.Clear();
            appendNearby.Invoke(obs, new object[] { sb, woods, new Point(46, 5) });
            string afterFirst = sb.ToString();
            Check(!afterFirst.Contains("(46,6)"), "砍掉的大树桩不再列出");
            foreach (var p in stumpCoords.Skip(1))
                Check(afterFirst.Contains($"({p.X},{p.Y})"), $"砍掉第一个后仍能看到其余大树桩 ({p.X},{p.Y})：" + Shorten(afterFirst));
        }

        // 下矿出发前背包检查与自动整理：地点判定、空位/可存物品/箱子空位阈值与接任务提醒
        var shouldTidyLoc = tActions.GetMethod("ShouldAutoTidyBeforeMineLocation", Any);
        bool TryTidyLoc(string locName, bool mineShaft = false, bool volcanoLoc = false, bool desertLoc = false, bool island = false, bool skull = false, bool volcano = false) =>
            (bool)shouldTidyLoc.Invoke(null, new object[] { locName, mineShaft, volcanoLoc, desertLoc, island, skull, volcano });
        Check(TryTidyLoc("Town", skull: true), "镇上接完任务去骷髅洞穴时允许出发前自动整理");
        Check(TryTidyLoc("FarmHouse", skull: true), "农舍出发去骷髅洞穴时允许出发前自动整理");
        Check(TryTidyLoc("BusStop", skull: true), "巴士站出发去骷髅洞穴时允许出发前自动整理");
        Check(TryTidyLoc("QiNutRoom", island: true, skull: true), "姜岛齐核桃房接完任务回主大陆去骷髅洞穴时允许出发前自动整理");
        Check(!TryTidyLoc("Desert", desertLoc: true, skull: true), "人已在沙漠时不折返整理");
        Check(!TryTidyLoc("SkullCave", skull: true), "人已在骷髅洞穴入口时不折返整理");
        Check(!TryTidyLoc("DesertFestival", desertLoc: true, skull: true), "人已在沙漠节时不折返整理");
        Check(!TryTidyLoc("UndergroundMine121", mineShaft: true, skull: true), "人已在矿洞内时不折返整理");
        Check(TryTidyLoc("Town", volcano: true), "主大陆出发去姜岛火山时允许出发前自动整理");
        Check(!TryTidyLoc("IslandNorth", island: true, volcano: true), "人已在姜岛去火山时不跨海折返整理");
        Check(TryTidyLoc("Town"), "镇上出发去普通矿洞时允许出发前自动整理");
        Check(!TryTidyLoc("Mine"), "人已在普通矿洞入口时不折返整理");

        var needsPreTidy = tActions.GetMethod("NeedsMinePreTidy", Any);
        bool TryNeedsTidy(int free, int storable, bool hasChest) =>
            (bool)needsPreTidy.Invoke(null, new object[] { free, storable, hasChest });
        Check(TryNeedsTidy(2, 1, true), "空位≤4且有可存物品和空箱时触发出发前自动整理");
        Check(TryNeedsTidy(4, 1, true), "空位=4且有1样可存物品时触发出发前自动整理");
        Check(TryNeedsTidy(6, 4, true), "空位=6且有≥4样可存杂物时触发出发前自动整理");
        Check(!TryNeedsTidy(5, 3, true), "空位=5且仅有3样可存杂物时不触发");
        Check(!TryNeedsTidy(8, 10, true), "空位充足(>6)时不触发");
        Check(!TryNeedsTidy(0, 0, true), "没有可存物品时不白跑农场");
        Check(!TryNeedsTidy(0, 10, false), "农场无箱子或箱子全满时不白跑农场");

        var questPackHint = tActions.GetMethod("MineQuestPackReminder", Any);
        string HintFor(int free, bool mining) => (string)questPackHint.Invoke(null, new object[] { free, mining });
        Check(HintFor(4, true).Contains("tidy_backpack"), "接下矿任务且背包空位≤6时提醒先整理背包");
        Check(HintFor(7, true) == "", "接下矿任务且背包空位>6时不啰嗦");
        Check(HintFor(2, false) == "", "非下矿任务不触发下矿背包提醒");
    }

    static System.IO.DirectoryInfo repo2()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "BlindWhaleFarmer", "manifest.json")))
            d = d.Parent;
        return d;
    }

    static string fishSrcCache;
    static string fishSrc() => fishSrcCache ??= System.IO.File.ReadAllText(System.IO.Path.Combine(repo2().FullName, "BlindWhaleFarmer", "GameActions.Fishing.cs"));
}
