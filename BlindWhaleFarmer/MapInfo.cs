using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    /// <summary>map_info 里的一条：出口、桥或门。坐标都是格子坐标，和 get_state 里“你站在 (x,y)”同一套。</summary>
    internal sealed class MapEntry
    {
        /// <summary>exits / bridges / doors。</summary>
        public string Section;
        public string Name;
        public Point Tile;
        /// <summary>桥这种占好几格的：右下角（含）。单格的为 null。</summary>
        public Point? TileEnd;
        /// <summary>通向哪个地点：出口、门写地点内部名（能直接给 travel_to）；桥写它连着的那片地方（同一张地图里），吊桥没有就为 null。</summary>
        public string Target;
        /// <summary>补充说明：营业时间、要几心、修没修好……</summary>
        public string Note = "";

        public string Coord => TileEnd is Point e && e != Tile ? $"({Tile.X},{Tile.Y})-({e.X},{e.Y})" : $"({Tile.X},{Tile.Y})";

        public string Describe() => $"{Name} {Coord}" + (Target != null ? $" → {Target}" : "") + Note;
    }

    /// <summary>
    /// 地图认知（M11）：map_info 工具和 move_to 失败时的挡路诊断。这里的代码只读游戏状态，不移动玩家、不改地图。
    /// </summary>
    internal static class MapInfo
    {
        public static readonly string[] Sections = { "exits", "bridges", "doors" };

        private static readonly Dictionary<string, string> SectionAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["exits"] = "exits", ["exit"] = "exits", ["出口"] = "exits", ["warps"] = "exits", ["warp"] = "exits",
            ["bridges"] = "bridges", ["bridge"] = "bridges", ["桥"] = "bridges",
            ["doors"] = "doors", ["door"] = "doors", ["门"] = "doors",
        };

        /// <summary>section 写法换成 exits/bridges/doors；空的是全部；认不出返回 null。</summary>
        public static string[] ParseSections(string section, out string unknown)
        {
            unknown = null;
            if (string.IsNullOrWhiteSpace(section) || section.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || section.Trim() == "全部")
                return Sections;
            var parts = section.Split(new[] { ',', '，', '、', ' ', ';', '；', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var picked = new List<string>();
            foreach (var p in parts)
            {
                if (!SectionAliases.TryGetValue(p, out var s))
                {
                    unknown = p;
                    return null;
                }
                if (!picked.Contains(s))
                    picked.Add(s);
            }
            return picked.Count == 0 ? Sections : picked.ToArray();
        }

        /// <summary>当前地图上的出口、桥、门。只读。</summary>
        public static List<MapEntry> Collect(GameLocation loc)
        {
            var list = new List<MapEntry>();
            if (loc?.Map == null)
                return list;
            foreach (var e in Exits.Get(loc))
            {
                // 走过去就换地图的算出口；要对着按交互的（店门、房门、建筑门）算门。巴士、船、矿车也是“走了就到别处”，算出口
                bool door = e.Kind is ExitKind.Door or ExitKind.BuildingDoor;
                string name = e.Kind switch
                {
                    ExitKind.Warp => "出口",
                    ExitKind.Door => "门",
                    ExitKind.BuildingDoor => "建筑门",
                    ExitKind.Bus => Exits.IsBoat(e) ? "船" : "巴士",
                    ExitKind.Minecart => "矿车",
                    _ => "出口"
                };
                string note = e.Note ?? "";
                if (e.FriendNpc != null)
                    note += $"（要和 {Game1.getCharacterFromName(e.FriendNpc)?.displayName ?? e.FriendNpc} 有 {e.FriendPoints / 250} 心才进得去）";
                // 地图边上的传送点在地图外一格（比如 (-1,54)），move_to 过去会报“不在当前地图内”。
                // 报贴着边、在地图里的那一格（和 travel_to 走出口时用的一样，TakeExit 也是这么夹进地图的）
                var tile = ClampToMap(loc, e.Tile, out bool edge);
                if (edge)
                    note += "（地图边上，走到这格再往外走一步就过去了；直接 travel_to 也行）";
                list.Add(new MapEntry { Section = door ? "doors" : "exits", Name = name, Tile = tile, Target = e.Target, Note = note });
            }
            list.AddRange(Bridges(loc));
            return list;
        }

        /// <summary>坐标在地图外就夹到最近的地图边上（edge=true）；在地图里原样返回。</summary>
        internal static Point ClampToMap(GameLocation loc, Point p, out bool edge)
        {
            int w = Pathfinder.MapWidth(loc), h = Pathfinder.MapHeight(loc);
            var c = new Point(Math.Clamp(p.X, 0, w - 1), Math.Clamp(p.Y, 0, h - 1));
            edge = c != p;
            return c;
        }

        /// <summary>
        /// 地图上的桥。游戏里没有统一的“桥”数据，只有几处会修好/断开的桥（山上采石场、海边去潮池、姜岛北边、火山入口），
        /// 和姜岛上的吊桥（SuspensionBridge）。这里只报这些，说清楚修没修好；地图上画的普通小桥本来就能走，不单独列。
        /// </summary>
        private static IEnumerable<MapEntry> Bridges(GameLocation loc)
        {
            var list = new List<MapEntry>();
            string master(string mail) => Game1.MasterPlayer?.mailReceived.Contains(mail) == true ? "yes" : null;
            switch (loc)
            {
                case Mountain:
                    {
                        // 采石场的桥：社区中心工艺室（或 Joja 的桥项目）完成后修好（Mountain.MakeMapModifications 看 ccCraftsRoom）
                        bool fixedNow = master("ccCraftsRoom") != null || master("jojaCraftsRoom") != null;
                        // Mountain.restoreBridge 把修好的桥贴在 (92,24) 开始的地方；只报这一格（桥头），不猜桥有多长
                        list.Add(new MapEntry
                        {
                            Section = "bridges", Name = "采石场的桥（桥头）", Tile = new Point(92, 24), Target = "采石场（Mountain 东北角）",
                            Note = fixedNow ? "（修好了，能走）" : "（还断着：社区中心工艺室收集包、或者 Joja 的桥项目完成后才修好）"
                        });
                        break;
                    }
                case Beach beach:
                    {
                        // 海边东边去潮池的木桥：优先按地图上的 BrokenBeachBridge 属性动态定位，兼容修改沙滩布局的模组
                        Point start = new Point(58, 13);
                        Point end = new Point(61, 14);
                        var broken = FindActionTiles(beach, "BrokenBeachBridge");
                        if (broken.Count > 0)
                        {
                            start = new Point(broken.Min(p => p.X), broken.Min(p => p.Y));
                            end = new Point(Math.Max(broken.Max(p => p.X), start.X + 3), Math.Max(broken.Max(p => p.Y), start.Y + 1));
                        }
                        list.Add(new MapEntry
                        {
                            Section = "bridges", Name = "去潮池的木桥", Tile = start, TileEnd = end, Target = "潮池（Beach 东边）",
                            Note = beach.bridgeFixed.Value ? "（修好了，能走）" : $"（还断着：对着 ({start.X},{start.Y}) interact，交 300 木材修好）"
                        });
                        break;
                    }
                case IslandNorth north:
                    list.Add(new MapEntry
                    {
                        Section = "bridges", Name = "去挖掘场的桥", Tile = new Point(31, 52), TileEnd = new Point(34, 54), Target = "挖掘场（IslandNorth 西边，化石考察站）",
                        Note = north.bridgeFixed.Value ? "（修好了，能走）" : "（还断着：给旁边的鹦鹉 10 个金核桃修好，feed_parrot Bridge）"
                    });
                    AddSuspensionBridges(list, north.suspensionBridges);
                    break;
                case IslandSecret secret:
                    AddSuspensionBridges(list, secret.suspensionBridges);
                    break;
                case VolcanoDungeon vd when vd.level.Value == 0:
                    // VolcanoDungeon.UpdateBridge 修桥时改的第一格是 (27,39)；只报这一格，不猜整座桥的范围
                    list.Add(new MapEntry
                    {
                        Section = "bridges", Name = "火山入口的桥（大致位置）", Tile = new Point(27, 39), Target = "火山第 0 层里面（近路）",
                        Note = master("Island_VolcanoBridge") != null ? "（修好了，能走）" : "（还没修：给鹦鹉 5 个金核桃，feed_parrot VolcanoBridge）"
                    });
                    break;
            }
            return list;
        }

        private static List<Point> FindActionTiles(GameLocation loc, string actionVerb)
        {
            var found = new List<Point>();
            var layer = loc?.Map?.GetLayer("Buildings");
            if (layer == null)
                return found;
            for (int x = 0; x < layer.LayerWidth; x++)
                for (int y = 0; y < layer.LayerHeight; y++)
                {
                    string action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
                    if (!string.IsNullOrWhiteSpace(action)
                        && action.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                            .Equals(actionVerb, StringComparison.OrdinalIgnoreCase))
                        found.Add(new Point(x, y));
                }
            return found;
        }

        private static void AddSuspensionBridges(List<MapEntry> list, IEnumerable<StardewValley.BellsAndWhistles.SuspensionBridge> bridges)
        {
            if (bridges == null)
                return;
            foreach (var b in bridges)
            {
                var r = b.bridgeBounds;
                list.Add(new MapEntry
                {
                    Section = "bridges", Name = "吊桥", Tile = new Point(r.Left / 64, r.Top / 64), TileEnd = new Point((r.Right - 1) / 64, (r.Bottom - 1) / 64),
                    Note = "（能走，走过去会晃）"
                });
            }
        }

        /// <summary>
        /// map_info 每类最多列几条（离玩家近的先）；move_to 连续失败时附的出口摘要也用这个数。
        /// 真机上 Town 有 9 条出口 + 15 条门，全列出来将近 900 字。要全部就传 all=true。
        /// </summary>
        public const int MaxEntriesPerSection = 12;

        private static int Distance(MapEntry e, Point from) => Math.Abs(e.Tile.X - from.X) + Math.Abs(e.Tile.Y - from.Y);

        /// <summary>
        /// map_info 的文字结果。entries 由调用方从 Collect 拿，测试可以直接传构造好的列表。
        /// 每类按离 from（玩家站的格子）远近排，最多 MaxEntriesPerSection 条，多出来的说一声“还有 N 条”；all=true 全给。
        /// </summary>
        public static string Format(string locName, string displayName, bool outdoors, int width, int height, IReadOnlyList<MapEntry> entries, string[] sections,
            Point from, bool all = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"地点：{locName}" + (string.IsNullOrEmpty(displayName) || displayName == locName ? "" : $"（{displayName}）")
                + $"，{(outdoors ? "室外" : "室内")}，地图大小 {width}x{height}（格子坐标，左上角 (0,0)，往右 x+1，往下 y+1，和 get_state 里“你站在 (x,y)”同一套）");
            bool cut = false;
            foreach (var s in sections)
            {
                var items = entries.Where(e => e.Section == s).OrderBy(e => Distance(e, from)).ThenBy(e => e.Tile.Y).ThenBy(e => e.Tile.X).ToList();
                string title = s switch { "exits" => "出口（走过去就换地图）", "bridges" => "桥", _ => "门（对着它 interact 或者 travel_to 那个地点就会开）" };
                sb.AppendLine($"## {title}");
                if (items.Count == 0)
                    sb.AppendLine(s == "bridges" ? "无（这张地图上没有会断/要修的桥；地图上画的普通小桥本来就能走）" : "无");
                var shown = all ? items : items.Take(MaxEntriesPerSection).ToList();
                foreach (var e in shown)
                    sb.AppendLine("- " + e.Describe());
                if (shown.Count < items.Count)
                {
                    sb.AppendLine($"- ……还有 {items.Count - shown.Count} 条离你更远的没列");
                    cut = true;
                }
            }
            sb.Append($"（按离你 ({from.X},{from.Y}) 远近排" + (cut ? $"，每类最多 {MaxEntriesPerSection} 条，要全部就传 all=true" : "")
                + "。只读，不花游戏时间。要去别的地点直接 travel_to 地点名，会自己找出口、开门）");
            return sb.ToString();
        }

        /// <summary>出口摘要（move_to 连续失败时附上）：离 from 最近的出口和门，条数和 map_info 每类上限一样。</summary>
        public static string ExitSummary(GameLocation loc, Point from, int max = MaxEntriesPerSection)
        {
            var items = Collect(loc).Where(e => e.Section != "bridges").OrderBy(e => Distance(e, from)).Take(max).ToList();
            return items.Count == 0 ? null : string.Join("；", items.Select(e => e.Describe()));
        }

        // ───────────────────────── move_to 挡路诊断 ─────────────────────────

        /// <summary>挡路的大类：作物 / 树 / 石头 / 建筑 / 围栏 / 水 / 其它；什么都认不出返回 null。</summary>
        public static string BlockerKind(GameLocation loc, Point p)
        {
            var v = new Vector2(p.X, p.Y);
            var rect = new Rectangle(p.X * 64, p.Y * 64, 64, 64);
            if (loc.objects.TryGetValue(v, out var obj))
            {
                if (obj is Fence)
                    return "围栏";
                if (obj.IsBreakableStone() || obj.Name.Contains("Stone") || obj.Name.Contains("Boulder"))
                    return "石头";
                if (obj.IsTwig())
                    return "树";
                return "其它";
            }
            if (loc.resourceClumps.FirstOrDefault(c => c.getBoundingBox().Intersects(rect)) is { } clump)
                return clump is GiantCrop || clump.parentSheetIndex.Value is 44 or 46 ? "作物"
                    : clump.parentSheetIndex.Value is 600 or 602 ? "树"
                    : "石头";
            if (loc.terrainFeatures.TryGetValue(v, out var tf))
            {
                if (tf is Tree or FruitTree or Bush)
                    return "树";
                if (tf is HoeDirt { crop: not null })
                    return "作物";
            }
            if (loc.largeTerrainFeatures.Any(l => l.getBoundingBox().Intersects(rect)))
                return "树";
            if (loc.buildings.Any(b => b.occupiesTile(v)))
                return "建筑";
            if (loc.furniture.Any(f => f.furniture_type.Value != 12 && f.GetBoundingBox().Intersects(rect)))
                return "其它";
            if (loc.isWaterTile(p.X, p.Y))
                return "水";
            // Buildings 层有图块的格子：墙、柜台、悬崖、房子（地图画出来的，不是能清的东西）
            if (loc.Map?.GetLayer("Buildings")?.Tiles[p.X, p.Y] != null)
                return "建筑";
            return null;
        }

        /// <summary>
        /// 能不能清掉：树、石头（还有杂草、树枝这类归到这两类里的）用工具能清；
        /// 建筑、墙、水、围栏、作物、其它（家具、机器、别人摆的东西）不建议清，只说绕。
        /// 围栏其实拆得掉，但多半是玩家自己围的，AI 拆了会放跑动物，所以也只说绕。
        /// </summary>
        public static bool IsClearable(string kind) => kind is "树" or "石头";

        /// <summary>每类挡路东西怎么处理。能清的（IsClearable）说怎么清；清不掉的只说绕，不提清掉。</summary>
        public static string BlockerAdvice(string kind) => kind switch
        {
            "树" => "树枝、小树能用 clear_area 或者 use_tool 斧头砍掉（大树要 clear_area include_trees=true，大树桩要升级过的斧头）",
            "石头" => "小石头能用 clear_area 或者 use_tool 镐子敲掉（大石头要升级过的镐子）",
            "作物" => "作物别踩，绕开田走",
            "建筑" => "建筑和墙清不掉，只能绕",
            "围栏" => "围栏别拆（多半是围动物的），只能绕，从围栏的门口进出",
            "水" => "水过不去，只能沿岸边绕，或者找桥（map_info section=bridges）",
            _ => "这东西不该清，只能绕"
        };

        /// <summary>
        /// 目标附近（离 closest 2 格内）走不了的格子挡的是什么：按类数格数，多的在前。都认不出返回 null。
        /// 认得出的写成“石头 3 格、树 1 格”，具体坐标给第一格。
        /// </summary>
        public static (string summary, string mainKind) DescribeBlockers(GameLocation loc, Point center, int radius, Func<Point, bool> passable)
        {
            var kinds = new List<(string kind, Point at)>();
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var p = new Point(center.X + dx, center.Y + dy);
                    if (!loc.isTileOnMap(p.X, p.Y) || passable(p))
                        continue;
                    if (BlockerKind(loc, p) is string k)
                        kinds.Add((k, p));
                }
            if (kinds.Count == 0)
                return (null, null);
            var groups = kinds.GroupBy(k => k.kind).OrderByDescending(g => g.Count()).ToList();
            string summary = string.Join("、", groups.Select(g => $"{g.Key} {g.Count()} 格（比如 ({g.First().at.X},{g.First().at.Y})）"));
            return (summary, groups[0].Key);
        }

        /// <summary>
        /// 绕行点：从 closest 往目标那边、但偏开挡路方向的一格能走的格子。先试目标方向两侧（绕开挡着的那排），再试更远一点的。
        /// 找不到返回 null。reachable 是从玩家能走到的格子（调用方算），保证给出的点真能走到。
        /// </summary>
        public static Point? DetourPoint(Point closest, Point target, Func<Point, bool> reachable)
        {
            int dx = Math.Sign(target.X - closest.X), dy = Math.Sign(target.Y - closest.Y);
            var tries = new List<Point>();
            for (int r = 2; r <= 6; r++)
            {
                // 目标主要在左右：上下绕；目标主要在上下：左右绕；两边都试，离目标近的先
                if (Math.Abs(target.X - closest.X) >= Math.Abs(target.Y - closest.Y))
                {
                    tries.Add(new Point(closest.X + dx, closest.Y - r));
                    tries.Add(new Point(closest.X + dx, closest.Y + r));
                    tries.Add(new Point(closest.X, closest.Y - r));
                    tries.Add(new Point(closest.X, closest.Y + r));
                }
                else
                {
                    tries.Add(new Point(closest.X - r, closest.Y + dy));
                    tries.Add(new Point(closest.X + r, closest.Y + dy));
                    tries.Add(new Point(closest.X - r, closest.Y));
                    tries.Add(new Point(closest.X + r, closest.Y));
                }
            }
            foreach (var p in tries.OrderBy(p => Math.Abs(p.X - target.X) + Math.Abs(p.Y - target.Y)))
                if (reachable(p))
                    return p;
            return null;
        }

        /// <summary>“往左上”这种方向字，from → to。</summary>
        public static string Direction(Point from, Point to)
        {
            string h = to.X > from.X ? "右" : to.X < from.X ? "左" : "";
            string v = to.Y > from.Y ? "下" : to.Y < from.Y ? "上" : "";
            return h + v == "" ? "原地" : h + v;
        }

        // ───────────────────────── 同一目标连续失败 ─────────────────────────

        private static string lastFailKey;
        private static int lastFailCount;

        /// <summary>记一次 move_to 失败，返回同一个目标（同一地点、同一格）连续失败了几次。</summary>
        public static int RecordMoveFailure(string location, Point target)
        {
            string key = $"{location}@{target.X},{target.Y}";
            if (key == lastFailKey)
                lastFailCount++;
            else
            {
                lastFailKey = key;
                lastFailCount = 1;
            }
            return lastFailCount;
        }

        /// <summary>move_to 成功了（或者换了目标）就从头数。</summary>
        public static void ResetMoveFailures()
        {
            lastFailKey = null;
            lastFailCount = 0;
        }
    }

    internal sealed partial class GameActions
    {
        /// <summary>map_info 工具：只读当前地图，不 yield 等待、不动玩家，一帧就返回。默认每类最多 MaxEntriesPerSection 条，all=true 全给。</summary>
        public IEnumerable<object> MapInfoTool(string section, bool all = false)
        {
            GameLocation loc = null;
            try
            {
                loc = Game1.currentLocation;
            }
            catch
            {
                // 游戏没启动完（离线测试）时读不到，当成没有地图
            }
            var sections = MapInfo.ParseSections(section, out string unknown);
            if (sections == null)
            {
                yield return ActionResult.Fail($"map_info 不认识 section “{unknown}”。能写 exits（出口）、bridges（桥）、doors（门），可以用逗号写几类，不填给全部");
                yield break;
            }
            if (loc?.Map == null)
            {
                yield return ActionResult.Fail("现在没有地图（游戏还没进存档，或者正在换地图），过一会儿再查");
                yield break;
            }
            yield return ActionResult.Success(MapInfo.Format(loc.NameOrUniqueName, loc.DisplayName, loc.IsOutdoors,
                Pathfinder.MapWidth(loc), Pathfinder.MapHeight(loc), MapInfo.Collect(loc), sections, Game1.player.TilePoint, all));
        }
    }
}
