using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal static class Util
    {
        /// <summary>玩家箱子里有没有这样东西：有就返回“地点(x,y) 几个”（最多列 3 处），没有返回 null。</summary>
        internal static string ItemInChests(string qualifiedId)
        {
            var found = new System.Collections.Generic.List<string>();
            StardewValley.Utility.ForEachLocation(loc =>
            {
                foreach (var p in loc.objects.Pairs)
                    if (p.Value is StardewValley.Objects.Chest c && c.playerChest.Value && found.Count < 3 && !GameActions.IsOffMapChest(loc, p.Key, c))
                    {
                        int n = 0;
                        foreach (var i in c.GetItemsForPlayer())
                            if (i != null && i.QualifiedItemId == qualifiedId)
                                n += i.Stack;
                        if (n > 0)
                            found.Add($"{loc.NameOrUniqueName}({(int)p.Key.X},{(int)p.Key.Y}) {n} 个");
                    }
                return found.Count < 3;
            });
            return found.Count == 0 ? null : string.Join("、", found);
        }

        public static string Season => SeasonName(Game1.currentSeason);

        public static string SeasonName(string season) => season?.ToLowerInvariant() switch
        {
            "spring" => "春",
            "summer" => "夏",
            "fall" => "秋",
            "winter" => "冬",
            _ => season
        };

        public static string TimeString(int time)
        {
            int h = time / 100 % 24, m = time % 100;
            return $"{h:00}:{m:00}" + (time >= 2400 ? "(次日)" : "");
        }

        public static string Stamp() => $"第{Game1.year}年{Season}{Game1.dayOfMonth}日 {TimeString(Game1.timeOfDay)}";

        public static string DirName(int dir) => dir switch
        {
            0 => "上",
            1 => "右",
            2 => "下",
            3 => "左",
            _ => "?"
        };

        /// <summary>解析方向：up/right/down/left、上/右/下/左、北/东/南/西 或 0-3，无法识别返回 -1。</summary>
        public static int ParseDir(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "0" or "up" or "north" or "上" or "北" => 0,
            "1" or "right" or "east" or "右" or "东" => 1,
            "2" or "down" or "south" or "下" or "南" => 2,
            "3" or "left" or "west" or "左" or "西" => 3,
            _ => -1
        };

        public static string ItemLabel(Item item)
        {
            if (item == null)
                return "空";
            string s = item.DisplayName;
            if (item is WateringCan can)
                s += $"（水 {can.WaterLeft}/{can.waterCanMax}）";
            else if (item is Tool tool && tool.UpgradeLevel > 0 && item is not MeleeWeapon)
                s += $"（等级{tool.UpgradeLevel}）";
            if (item.Stack > 1)
                s += $" x{item.Stack}";
            if (item.Quality > 0)
                s += item.Quality switch { 1 => " 银星", 2 => " 金星", 4 => " 铱星", _ => "" };
            if (item is SObject o && !(item is Tool))
            {
                if (o.Edibility > 0 && o.Edibility != -300)
                    s += $" [可食用 +{o.staminaRecoveredOnConsumption()}体力]";
                if (o.Category == SObject.SeedsCategory)
                    s += " [种子]";
            }
            return s;
        }

        /// <summary>去掉状态里显示的品质后缀（“土豆 银星”→“土豆”），模型常把整段名字原样抄回来。</summary>
        /// <summary>名字末尾写的品质：铱星 4、金星 2、银星 1，没写返回 -1。</summary>
        public static int QualityFromSpec(string spec)
        {
            spec = (spec ?? "").Trim();
            return spec.EndsWith("铱星") ? 4 : spec.EndsWith("金星") || spec.EndsWith("金星+") ? 2 : spec.EndsWith("银星") || spec.EndsWith("银星+") ? 1 : -1;
        }

        public static string StripQuality(string name) =>
            System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s*(银星|金星|铱星)\+?$", "");

        /// <summary>按序号或名称在背包里找物品，返回背包下标，找不到返回 -1。同名不同品质时优先品质和名字都对上的。</summary>
        /// <summary>
        /// 名字有多像（0 = 不像）：去掉空格、下划线、点再比，不分大小写。
        /// 一个包含另一个最像；否则看共有的字（中文按字、英文按单词片段），共有的越多越像。
        /// 用在“找不到 X”的报错里给候选，不用来自动替换（猜错了会买错、扔错东西）。
        /// </summary>
        public static int NameScore(string want, string name)
        {
            static string Norm(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '.' or '-' or '（' or '）' or '(' or ')')).ToArray()).ToLowerInvariant();
            string a = Norm(want), b = Norm(name);
            if (a.Length == 0 || b.Length == 0)
                return 0;
            if (a == b)
                return 100;
            if (b.Contains(a) || a.Contains(b))
                return 80 - Math.Min(30, Math.Abs(a.Length - b.Length));
            // 共有的字：中文一个字算一个，英文 3 个字母一段
            bool cjk = a.Any(c => c > 0x2E80);
            var grams = cjk ? a.Select(c => c.ToString()).Distinct().ToList()
                : Enumerable.Range(0, Math.Max(1, a.Length - 2)).Select(i => a.Substring(i, Math.Min(3, a.Length - i))).Distinct().ToList();
            int hit = grams.Count(g => b.Contains(g));
            // 两个字的中文名只共有一个字不算像（真机：“鳀鱼”给出“竹鱼竿”，只因为都有“鱼”）
            if (cjk && grams.Count <= 2 && hit < grams.Count)
                return 0;
            if (hit == 0 || (cjk ? hit * 2 < grams.Count : hit * 3 < grams.Count))
                return 0;
            return 10 + 50 * hit / grams.Count;
        }

        /// <summary>背包里名字和 spec 最像的几样（给“背包里没有 X”的报错用），最多 max 个，没有返回空。</summary>
        public static List<string> ItemCandidates(string spec, Func<Item, bool> filter = null, int max = 3)
        {
            if (string.IsNullOrWhiteSpace(spec) || Game1.player == null)
                return new List<string>();
            string bare = StripQuality(spec.Trim());
            return Game1.player.Items
                .Where(i => i != null && (filter == null || filter(i)))
                .Select(i => (name: i.DisplayName, score: Math.Max(NameScore(bare, i.DisplayName), NameScore(bare, i.Name))))
                .Where(t => t.score > 0)
                .GroupBy(t => t.name).Select(g => g.First())
                .OrderByDescending(t => t.score)
                .Take(max).Select(t => t.name).ToList();
        }

        /// <summary>
        /// “背包里没有 X”后面接的一句：有名字像的就列出来（“是不是：A、B”），没有就列背包里能用的几样，背包空就不接。
        /// </summary>
        public static string NotFoundHint(string spec, Func<Item, bool> filter = null)
        {
            var near = ItemCandidates(spec, filter);
            if (near.Count > 0)
                return $"。名字相近的：{string.Join("、", near)}（照着写全名，或者写背包序号）";
            var some = Game1.player?.Items.Where(i => i != null && (filter == null || filter(i))).Select(i => i.DisplayName).Distinct().Take(6).ToList();
            return some is { Count: > 0 } ? $"。背包里能用的有：{string.Join("、", some)}" : "";
        }

        public static int FindItemIndex(string spec, Func<Item, bool> filter = null)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return -1;
            spec = spec.Trim();
            var items = Game1.player.Items;
            int quality = spec.EndsWith("铱星") ? 4 : spec.EndsWith("金星") || spec.EndsWith("金星+") ? 2 : spec.EndsWith("银星") || spec.EndsWith("银星+") ? 1 : -1;
            string bare = StripQuality(spec);
            if (quality >= 0 && bare != spec)
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i] is { } q && q.Quality == quality && (filter == null || filter(q))
                        && (q.DisplayName.Equals(bare, StringComparison.OrdinalIgnoreCase) || q.Name.Equals(bare, StringComparison.OrdinalIgnoreCase)))
                        return i;
                spec = bare;
            }
            if (int.TryParse(spec, out int idx))
            {
                if (idx >= 0 && idx < items.Count && items[idx] != null && (filter == null || filter(items[idx])))
                    return idx;
                return -1;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it == null || (filter != null && !filter(it)))
                        continue;
                    bool match = pass == 0
                        ? it.DisplayName.Equals(spec, StringComparison.OrdinalIgnoreCase) || it.Name.Equals(spec, StringComparison.OrdinalIgnoreCase)
                        : it.DisplayName.Contains(spec, StringComparison.OrdinalIgnoreCase) || it.Name.Contains(spec, StringComparison.OrdinalIgnoreCase);
                    if (match)
                        return i;
                }
            }
            return -1;
        }

        private static readonly Dictionary<string, Func<Item, bool>> ToolAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["axe"] = i => i is Axe,
            ["斧头"] = i => i is Axe,
            ["斧"] = i => i is Axe,
            ["hoe"] = i => i is Hoe,
            ["锄头"] = i => i is Hoe,
            ["锄"] = i => i is Hoe,
            ["pickaxe"] = i => i is Pickaxe,
            ["镐子"] = i => i is Pickaxe,
            ["镐"] = i => i is Pickaxe,
            ["wateringcan"] = i => i is WateringCan,
            ["watering_can"] = i => i is WateringCan,
            ["watering can"] = i => i is WateringCan,
            ["水壶"] = i => i is WateringCan,
            ["洒水壶"] = i => i is WateringCan,
            ["scythe"] = i => i is MeleeWeapon w && w.isScythe(),
            ["镰刀"] = i => i is MeleeWeapon w && w.isScythe(),
            ["weapon"] = i => i is MeleeWeapon w && !w.isScythe(),
            ["sword"] = i => i is MeleeWeapon w && !w.isScythe(),
            ["武器"] = i => i is MeleeWeapon w && !w.isScythe(),
            ["剑"] = i => i is MeleeWeapon w && !w.isScythe(),
            ["fishingrod"] = i => i is FishingRod,
            ["rod"] = i => i is FishingRod,
            ["鱼竿"] = i => i is FishingRod,
        };

        /// <summary>和 FindToolIndex 一样的匹配规则（别名 axe/pickaxe… 或名字），用来在箱子里找工具。</summary>
        public static Func<Item, bool> ToolMatcher(string spec)
        {
            if (spec != null && ToolAliases.TryGetValue(spec.Trim().Replace("-", "_"), out var pred))
                return pred;
            string bare = StripQuality(spec ?? "");
            return i => i is Tool && bare.Length > 0 && (i.DisplayName.Contains(bare, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(bare, StringComparison.OrdinalIgnoreCase));
        }

        public static int FindToolIndex(string spec)
        {
            if (spec != null && ToolAliases.TryGetValue(spec.Trim().Replace("-", "_"), out var pred))
            {
                var items = Game1.player.Items;
                for (int i = 0; i < items.Count; i++)
                    if (items[i] != null && pred(items[i]))
                        return i;
                return -1;
            }
            return FindItemIndex(spec, i => i is Tool);
        }

        public static int FindTool<T>(Func<T, bool> pred = null) where T : Tool
        {
            var items = Game1.player.Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i] is T t && (pred == null || pred(t)))
                    return i;
            return -1;
        }

        public static string ObjectName(string qualifiedOrUnqualifiedId)
        {
            if (string.IsNullOrEmpty(qualifiedOrUnqualifiedId))
                return "?";
            string id = qualifiedOrUnqualifiedId.StartsWith("(") ? qualifiedOrUnqualifiedId : "(O)" + qualifiedOrUnqualifiedId;
            return ItemRegistry.GetDataOrErrorItem(id).DisplayName;
        }

        /// <summary>把坐标列表压缩成短文字。</summary>
        public static string Coords(IEnumerable<Point> points, int max = 40)
        {
            var list = points.ToList();
            string s = string.Join(" ", list.Take(max).Select(p => $"({p.X},{p.Y})"));
            if (list.Count > max)
                s += $" …等共{list.Count}处";
            return s;
        }

        public static int Manhattan(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

        /// <summary>按“最近邻”顺序排列要访问的格子，减少来回跑。</summary>
        /// <summary>最近邻排好以后再做 2-opt（把路线里交叉、来回折返的一段翻过来），一块田从头走到尾不绕。起点固定。</summary>
        public static List<Point> ShortRoute(Point start, IEnumerable<Point> points)
        {
            var r = NearestOrder(start, points);
            int n = r.Count;
            if (n < 4 || n > 400)
                return r;
            Point At(int i) => i < 0 ? start : r[i];
            bool improved = true;
            for (int pass = 0; improved && pass < 30; pass++)
            {
                improved = false;
                for (int i = 0; i < n - 1; i++)
                    for (int j = i + 1; j < n; j++)
                    {
                        int before = Manhattan(At(i - 1), r[i]) + (j + 1 < n ? Manhattan(r[j], r[j + 1]) : 0);
                        int after = Manhattan(At(i - 1), r[j]) + (j + 1 < n ? Manhattan(r[i], r[j + 1]) : 0);
                        if (after < before)
                        {
                            r.Reverse(i, j - i + 1);
                            improved = true;
                        }
                    }
            }
            return r;
        }

        public static List<Point> NearestOrder(Point start, IEnumerable<Point> points)
        {
            var remaining = points.ToList();
            var result = new List<Point>();
            var cur = start;
            while (remaining.Count > 0)
            {
                int bi = 0, bd = int.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int d = Manhattan(cur, remaining[i]);
                    if (d < bd)
                    {
                        bd = d;
                        bi = i;
                    }
                }
                cur = remaining[bi];
                result.Add(cur);
                remaining.RemoveAt(bi);
            }
            return result;
        }
    }
}
