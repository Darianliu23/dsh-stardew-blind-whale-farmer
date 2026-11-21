using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    /// <summary>姜岛：给鹦鹉交金核桃、捡不用解谜的金核桃。</summary>
    internal sealed partial class GameActions
    {
        /// <summary>鹦鹉解锁的东西：游戏里的 upgradeName → 中文说明和别名。</summary>
        private static readonly Dictionary<string, (string name, string what, string[] alias)> ParrotUpgrades = new()
        {
            ["Hut"] = ("第一只鹦鹉", "莱欧小屋里的鹦鹉，交了以后岛上其他鹦鹉才会出现", new[] { "hut", "first", "第一只", "小屋" }),
            ["Turtle"] = ("乌龟让路", "码头西边的乌龟挪开，能去岛西边的农场", new[] { "turtle", "乌龟", "west" }),
            ["Resort"] = ("度假村", "修好海滩度假村，村民会来岛上玩", new[] { "resort", "度假村" }),
            ["House"] = ("岛上农舍", "修好岛西边的农舍，能住、能种地", new[] { "house", "farmhouse", "农舍", "农场" }),
            ["House_Mailbox"] = ("农舍信箱", "岛上农舍的信箱", new[] { "mailbox", "信箱" }),
            ["Obelisk"] = ("农场方尖碑", "家里农场多一座方尖碑，传送到岛上农场", new[] { "obelisk", "方尖碑" }),
            ["ParrotPlatforms"] = ("鹦鹉快递", "岛上几处之间坐鹦鹉快速往返", new[] { "parrotplatforms", "express", "快递" }),
            ["Bridge"] = ("挖掘场的桥", "岛北边去挖掘场（化石办公室）的桥", new[] { "bridge", "桥", "挖掘场" }),
            ["Trader"] = ("岛上商人", "岛北边的以物易物商人", new[] { "trader", "商人" }),
            ["VolcanoBridge"] = ("火山里的桥", "火山入口那层的桥（近路）", new[] { "volcanobridge", "火山桥" }),
            ["VolcanoShortcutOut"] = ("火山出口捷径", "火山第 5 层直接回岛北的捷径", new[] { "volcanoshortcutout", "shortcut", "捷径" }),
        };

        private static string ParrotLabel(ParrotUpgradePerch p) =>
            ParrotUpgrades.TryGetValue(p.upgradeName.Value, out var u) ? u.name : p.upgradeName.Value;

        private static bool ParrotDone(ParrotUpgradePerch p) =>
            p.currentState.Value == ParrotUpgradePerch.UpgradeState.Complete || p.onUpdateCompletionStatus?.Invoke() == true;

        /// <summary>还能交核桃的鹦鹉：前置条件满足、还没解锁的（金鹦鹉不算）。火山里的只有人在那一层时才算得到。</summary>
        private static List<(GameLocation loc, ParrotUpgradePerch perch)> OpenParrots()
        {
            var list = new List<(GameLocation, ParrotUpgradePerch)>();
            var locs = Game1.locations.OfType<IslandLocation>().ToList();
            if (Loc is VolcanoDungeon vd && !locs.Contains(vd))
                locs.Add(vd);
            // 岛西边要等乌龟让路才过得去（农舍那几只鹦鹉游戏里不要求前置，但人走不到）
            bool westOpen = Game1.getLocationFromName("IslandSouth") is IslandSouth south && south.westernTurtleMoved.Value;
            var order = ParrotUpgrades.Keys.ToList();
            foreach (var loc in locs)
                foreach (var p in loc.parrotUpgradePerches)
                    if (p.upgradeName.Value != "GoldenParrot" && p.IsAvailable() && !ParrotDone(p) && (westOpen || loc is not IslandWest))
                        list.Add((loc, p));
            return list.OrderBy(t => order.IndexOf(t.Item2.upgradeName.Value) is int i && i >= 0 ? i : 99).ToList();
        }

        /// <summary>状态提示用：核桃够交的鹦鹉。</summary>
        internal static string AffordableParrotsHint()
        {
            int nuts = Game1.netWorldState.Value.GoldenWalnuts;
            var can = OpenParrots().Where(t => t.perch.requiredNuts.Value <= nuts).Select(t => $"{ParrotLabel(t.perch)}（{t.perch.requiredNuts.Value}）").ToList();
            return can.Count > 0 ? "金核桃够给鹦鹉了：" + string.Join("、", can) + "（feed_parrot）" : null;
        }

        private static string DescribeParrots(List<(GameLocation loc, ParrotUpgradePerch perch)> list)
        {
            int nuts = Game1.netWorldState.Value.GoldenWalnuts;
            return string.Join("\n", list.Select(t =>
            {
                ParrotUpgrades.TryGetValue(t.perch.upgradeName.Value, out var u);
                bool busy = t.perch.currentState.Value != ParrotUpgradePerch.UpgradeState.Idle;
                return $"- {ParrotLabel(t.perch)}（{t.perch.upgradeName.Value}，在 {t.loc.Name}）：{t.perch.requiredNuts.Value} 个核桃"
                    + (u.what != null ? $"，{u.what}" : "") + (busy ? "（正在修）" : nuts >= t.perch.requiredNuts.Value ? "" : "（核桃不够）");
            }));
        }

        /// <summary>
        /// 给姜岛上的鹦鹉交金核桃解锁东西。which 不传就列出现在能交的鹦鹉；传名字（中英文都行）就走过去交。
        /// 和手动一样：点鹦鹉 → 问要不要交 → 选“是”，鹦鹉们飞来修好。
        /// </summary>
        public IEnumerable<object> FeedParrot(string which)
        {
            int nuts = Game1.netWorldState.Value.GoldenWalnuts;
            var open = OpenParrots();
            if (string.IsNullOrWhiteSpace(which))
            {
                yield return open.Count == 0
                    ? ActionResult.Fail($"现在没有能交核桃的鹦鹉（要先上姜岛；有的要先解锁前一个）。金核桃现有 {nuts} 个")
                    : ActionResult.Success($"金核桃现有 {nuts} 个，一共找到 {Game1.netWorldState.Value.GoldenWalnutsFound} 个。能交的鹦鹉：\n{DescribeParrots(open)}\n（feed_parrot which=名字 去交；推荐顺序：第一只 → 乌龟 → 农舍 → 方尖碑/鹦鹉快递）");
                yield break;
            }
            string key = which.Trim().ToLowerInvariant();
            var pick = open.FirstOrDefault(t => t.perch.upgradeName.Value.Equals(which.Trim(), StringComparison.OrdinalIgnoreCase)
                || (ParrotUpgrades.TryGetValue(t.perch.upgradeName.Value, out var u) && (u.name.Contains(which.Trim()) || u.alias.Contains(key))));
            if (pick.perch == null)
            {
                yield return ActionResult.Fail($"没有叫“{which}”、现在能交核桃的鹦鹉。" + (open.Count > 0 ? "能交的有：\n" + DescribeParrots(open) : "现在一个都没有"));
                yield break;
            }
            var (loc, perch) = pick;
            int cost = perch.requiredNuts.Value;
            if (nuts < cost)
            {
                yield return ActionResult.Fail($"{ParrotLabel(perch)}要 {cost} 个金核桃，现在只有 {nuts} 个。collect_walnuts 能捡灌木里、地下埋着的核桃");
                yield break;
            }
            var o = new Outcome();
            if (Loc != loc)
            {
                foreach (var s in TravelInner(loc.NameOrUniqueName, o, exactName: true))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"去 {loc.Name} 找鹦鹉失败：{o.Message}");
                    yield break;
                }
            }
            var tile = perch.tilePosition.Value;
            foreach (var s in GoAdjacent(tile, o))
                yield return s;
            if (!o.Ok)
            {
                // 鹦鹉站的架子旁边不一定能站：站到 2 格内也行（游戏只看点的是哪一格）
                var near = Pathfinder.NearestStandTiles(Loc, tile, 2);
                o.Succeed();
                foreach (var s in GoToAny(near, o, "鹦鹉旁边"))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"走不到鹦鹉 ({tile.X},{tile.Y}) 旁边：{o.Message}");
                    yield break;
                }
            }
            Loc.checkAction(new xLocation(tile.X, tile.Y), Game1.viewport, Player);
            foreach (var s in WaitTicks(10))
                yield return s;
            if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask)
            {
                int yes = Array.FindIndex(ask.responses, r => r.responseKey == "Yes");
                foreach (var s in Dialogue(yes < 0 ? 0 : yes))
                    if (s is not ActionResult)
                        yield return s;
            }
            else if (Game1.activeClickableMenu != null)
            {
                foreach (var s in CloseMenu())
                    if (s is not ActionResult)
                        yield return s;
            }
            // 鹦鹉飞来修东西的动画大约 10 秒，期间人不能动；修完游戏把状态改成 Complete
            for (int t = 0; t < 900 && !ParrotDone(perch) && Game1.netWorldState.Value.GoldenWalnuts < nuts; t++)
                yield return null;
            for (int t = 0; t < 900 && !ParrotDone(perch) && perch.currentState.Value != ParrotUpgradePerch.UpgradeState.Idle; t++)
                yield return null;
            foreach (var s in WaitPlayerFree(300))
                yield return s;
            // 小屋的第一只鹦鹉交完会飞出去，接着放一段剧情
            for (int t = 0; t < 180 && perch.upgradeName.Value == "Hut" && Game1.CurrentEvent == null; t++)
                yield return null;
            string scene = Game1.CurrentEvent != null ? "。接着开始了一段剧情：用 dialogue 推进或 skip_event 跳过" : "";
            int after = Game1.netWorldState.Value.GoldenWalnuts;
            if (after >= nuts)
            {
                yield return ActionResult.Fail($"点了{ParrotLabel(perch)}，但核桃没交出去（现有 {after} 个）");
                yield break;
            }
            ParrotUpgrades.TryGetValue(perch.upgradeName.Value, out var info);
            yield return ActionResult.Success($"交给鹦鹉 {nuts - after} 个金核桃：{ParrotLabel(perch)}" + (ParrotDone(perch) ? "修好了" : "正在修")
                + (info.what != null ? $"（{info.what}）" : "") + $"。还剩 {after} 个核桃" + scene);
        }

        /// <summary>这张地图上还结着金核桃的灌木（size 4、还没摇过）。</summary>
        private static List<Point> WalnutBushes(GameLocation loc) =>
            loc.largeTerrainFeatures.OfType<Bush>()
                .Where(b => b.size.Value == Bush.walnutBush && b.tileSheetOffset.Value == 1)
                .Select(b => new Point((int)b.Tile.X, (int)b.Tile.Y)).ToList();

        /// <summary>这张地图上埋着金核桃、还没挖的格子（挖过的游戏记在 FoundBuriedNuts 里，埋点列表本身不变，见 Multiplayer._performNutDig）。</summary>
        private static List<Point> BuriedWalnuts(GameLocation loc) =>
            loc is IslandLocation il
                ? il.buriedNutPoints.Where(p => !Game1.netWorldState.Value.FoundBuriedNuts.Contains($"{loc.NameOrUniqueName}_{p.X}_{p.Y}")).ToList()
                : new List<Point>();

        /// <summary>
        /// 捡姜岛上不用解谜的金核桃：摇结着核桃的灌木、用锄头挖地下埋着的核桃，掉出来的捡起来。
        /// 不传 where 就在当前地图找；传了地点名先走过去。解谜的核桃（青蛙、化石办公室、音乐石之类）不在这里。
        /// </summary>
        public IEnumerable<object> CollectWalnuts(string where)
        {
            var o = new Outcome();
            if (!string.IsNullOrWhiteSpace(where) && !Loc.Name.Equals(where.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in TravelInner(where.Trim(), o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }
            if (Loc is not IslandLocation)
            {
                yield return ActionResult.Fail("金核桃只在姜岛上（IslandSouth、IslandEast、IslandWest、IslandNorth 等）");
                yield break;
            }
            int before = Game1.netWorldState.Value.GoldenWalnuts;
            var notes = new List<string>();
            int shook = 0, dug = 0;
            var skip = new HashSet<Point>();
            var st = new FightStats();
            for (int round = 0; round < 40; round++)
            {
                // 捡到核桃会弹“你获得了金色核桃！”（第一次、或者凑够一定数量时），关掉接着找
                for (int k = 0; k < 5 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; k++)
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;
                foreach (var s in WaitPlayerFree(120))
                    yield return s;
                if (Blocked(out string why))
                {
                    notes.Add("停下：" + why);
                    break;
                }
                // 岛上老虎史莱姆很多：靠近的先打掉（和下矿一样），血少了就停
                if (NearestThreat(4) != null)
                {
                    foreach (var s in FightInner(6, 0.4f, 20, st, o))
                        yield return s;
                    if (!o.Ok && o.Message.StartsWith("!"))
                    {
                        notes.Add("停下：" + o.Message.Substring(1));
                        break;
                    }
                    o.Succeed();
                    if (NearestThreat(2) != null)
                    {
                        notes.Add("停下：身边的怪打不掉（没有武器？）");
                        break;
                    }
                }
                // 地上已经躺着的金核桃（解谜奖励、以前摇灌木没捡走的）：先走过去捡，不管离灌木远近
                var lying = Loc.debris.Where(d => IsPickupDebris(d) && (d.item?.QualifiedItemId == "(O)73" || d.itemId.Value == "(O)73")
                    && !skip.Contains(DebrisTile(d))).Select(DebrisTile).Distinct().ToList();
                if (lying.Count > 0)
                {
                    var spot = lying.OrderBy(p => Util.Manhattan(p, Player.TilePoint)).First();
                    foreach (var s in CollectDebris(new Rectangle(spot.X - 1, spot.Y - 1, 3, 3), notes, 5))
                        yield return s;
                    skip.Add(spot);
                    continue;
                }
                var me = Player.TilePoint;
                var bush = WalnutBushes(Loc).Where(p => !skip.Contains(p)).OrderBy(p => Util.Manhattan(p, me)).Cast<Point?>().FirstOrDefault();
                var buried = BuriedWalnuts(Loc).Where(p => !skip.Contains(p)).OrderBy(p => Util.Manhattan(p, me)).Cast<Point?>().FirstOrDefault();
                if (bush == null && buried == null)
                    break;
                bool doBush = bush != null && (buried == null || Util.Manhattan(bush.Value, me) <= Util.Manhattan(buried.Value, me));
                var target = doBush ? bush.Value : buried.Value;
                if (!doBush && Util.FindTool<Hoe>() < 0)
                {
                    notes.Add("地下还埋着核桃，但背包里没有锄头");
                    skip.Add(target);
                    continue;
                }
                foreach (var s in GoAdjacent(target, o))
                    yield return s;
                if (!o.Ok)
                {
                    skip.Add(target);
                    o.Succeed();
                    continue;
                }
                if (doBush)
                {
                    Loc.checkAction(new xLocation(target.X, target.Y), Game1.viewport, Player);
                    foreach (var s in WaitTicks(20))
                        yield return s;
                    if (WalnutBushes(Loc).Contains(target))
                        skip.Add(target);
                    else
                        shook++;
                }
                else
                {
                    foreach (var s in UseToolOnce(Util.FindTool<Hoe>(), target, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        notes.Add(o.Message);
                        break;
                    }
                    if (BuriedWalnuts(Loc).Contains(target))
                        skip.Add(target);
                    else
                        dug++;
                }
                foreach (var s in CollectDebris(new Rectangle(target.X - 3, target.Y - 3, 7, 7), notes, 60))
                    yield return s;
            }
            int got = Game1.netWorldState.Value.GoldenWalnuts - before;
            string msg = $"在 {Loc.Name} 摇了 {shook} 棵核桃灌木、挖了 {dug} 处埋着的核桃，捡到 {got} 个金核桃（现有 {Game1.netWorldState.Value.GoldenWalnuts} 个，一共找到 {Game1.netWorldState.Value.GoldenWalnutsFound} 个）";
            if (st.Kills > 0)
                msg += $"；路上打倒 {st.Kills} 只怪";
            if (skip.Count > 0)
                msg += $"；有 {skip.Count} 处走不过去或弄不下来";
            if (notes.Count > 0)
                msg += "；" + string.Join("；", notes.Distinct());
            yield return got > 0 || shook + dug > 0 ? ActionResult.Success(msg) : ActionResult.Fail(msg + "。这张地图上没有能直接捡的核桃了，换个地方，或者看攻略“姜岛”一节的解谜");
        }
    }
}
