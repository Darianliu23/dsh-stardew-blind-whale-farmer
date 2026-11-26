using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Minigames;
using SObject = StardewValley.Object;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 姜岛：晶石洞（IslandWestCave1） ─────────────────────────

        private static readonly Point[] CaveCrystalTiles =
        {
            new(3, 4), // 1 紫
            new(4, 6), // 2 绿
            new(6, 7), // 3 红
            new(8, 6), // 4 蓝
            new(9, 4), // 5 橙
        };

        private static readonly string[] CaveCrystalNames = { "紫", "绿", "红", "蓝", "橙" };

        internal static Point? CrystalTileById(int crystalId1To5) =>
            crystalId1To5 >= 1 && crystalId1To5 <= CaveCrystalTiles.Length ? CaveCrystalTiles[crystalId1To5 - 1] : null;

        internal static string FormatCrystalSequence(IReadOnlyList<int> zeroBasedSeq, int nextIndex = 0)
        {
            if (zeroBasedSeq == null || zeroBasedSeq.Count == 0)
                return "暂无顺序";
            var steps = new List<string>();
            int start = Math.Clamp(nextIndex, 0, zeroBasedSeq.Count);
            for (int i = start; i < zeroBasedSeq.Count; i++)
            {
                int idx = zeroBasedSeq[i];
                string item = idx >= 0 && idx < CaveCrystalTiles.Length
                    ? $"#{idx + 1}({CaveCrystalTiles[idx].X},{CaveCrystalTiles[idx].Y}){CaveCrystalNames[idx]}"
                    : $"#{idx + 1}";
                steps.Add(item);
            }
            string head = start > 0 ? $"共 {zeroBasedSeq.Count} 颗，已按 {start} 颗，接下来：" : $"共 {zeroBasedSeq.Count} 颗：";
            return head + string.Join(" → ", steps);
        }

        /// <summary>去姜岛晶石洞（IslandWestCave1），启动狮子雕像并按闪光顺序逐轮点亮 5 颗晶石，解开后捡走 3 颗金核桃。</summary>
        public IEnumerable<object> CrystalCave()
        {
            var o = new Outcome();
            if (Loc is not IslandWestCave1)
            {
                foreach (var s in TravelInner("IslandWestCave1", o))
                    yield return s;
                if (!o.Ok || Loc is not IslandWestCave1)
                {
                    yield return ActionResult.Fail($"走不到姜岛晶石洞（IslandWestCave1）：{o.Message}（需要先在姜岛南边喂乌龟 10 颗金核桃解锁岛西）");
                    yield break;
                }
            }

            var cave = (IslandWestCave1)Loc;
            int walnutsBefore = Game1.netWorldState.Value.GoldenWalnuts;
            var pickupNotes = new List<string>();
            if (cave.completed.Value)
            {
                foreach (var s in CollectDebris(new Rectangle(4, 0, 5, 5), pickupNotes, 45))
                    yield return s;
                int extra = Game1.netWorldState.Value.GoldenWalnuts - walnutsBefore;
                yield return ActionResult.Success(extra > 0
                    ? $"晶石洞谜题早已解开，顺手捡起地上剩下的 {extra} 颗金核桃（现有 {Game1.netWorldState.Value.GoldenWalnuts} 个）"
                    : $"晶石洞谜题已经解开了（3 颗金核桃已拿到）");
                yield break;
            }

            for (int attempt = 0; attempt < 14 && !cave.completed.Value; attempt++)
            {
                if (Blocked(out string why))
                {
                    yield return ActionResult.Fail($"在晶石洞解谜被打断：{why}");
                    yield break;
                }
                for (int k = 0; k < 5 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; k++)
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;

                if (!cave.isActivated.Value || cave.netPhase.Value == IslandWestCave1.PHASE_NOTHING)
                {
                    var statue = FindActionTile(cave, "CrystalCaveActivate") ?? new Point(6, 2);
                    foreach (var s in GoAdjacent(statue, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"走不到激活雕像 ({statue.X},{statue.Y})：{o.Message}");
                        yield break;
                    }
                    Face(statue);
                    cave.checkAction(new xLocation(statue.X, statue.Y), Game1.viewport, Player);
                    foreach (var s in WaitTicks(6))
                        yield return s;
                }

                // 等介绍/播放动画结束进入等待点击阶段（PHASE_WAIT_FOR_PLAYER_INPUT == 2）
                for (int t = 0; t < 1500 && Loc == cave && !cave.completed.Value
                    && cave.netPhase.Value != IslandWestCave1.PHASE_WAIT_FOR_PLAYER_INPUT
                    && cave.netPhase.Value != IslandWestCave1.PHASE_OUTRO; t++)
                {
                    if (cave.netPhase.Value == IslandWestCave1.PHASE_NOTHING && !cave.isActivated.Value)
                        break;
                    if (Blocked(out why))
                    {
                        yield return ActionResult.Fail($"等待晶石闪光时被打断：{why}");
                        yield break;
                    }
                    yield return null;
                }

                if (cave.completed.Value || cave.netPhase.Value == IslandWestCave1.PHASE_OUTRO)
                    break;
                if (cave.netPhase.Value != IslandWestCave1.PHASE_WAIT_FOR_PLAYER_INPUT)
                    continue;

                var seq = cave.currentCrystalSequence.ToList();
                int idx = cave.currentCrystalSequenceIndex.Value;
                for (int i = idx; i < seq.Count; i++)
                {
                    if (Loc != cave || cave.completed.Value || cave.netPhase.Value != IslandWestCave1.PHASE_WAIT_FOR_PLAYER_INPUT)
                        break;
                    int crystalId = seq[i] + 1;
                    Point ctile = FindActionTile(cave, $"Crystal {crystalId}") ?? CrystalTileById(crystalId) ?? new Point(-1, -1);
                    if (ctile.X < 0)
                    {
                        yield return ActionResult.Fail($"找不到 {crystalId} 号晶石位置");
                        yield break;
                    }
                    foreach (var s in GoAdjacent(ctile, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"走不到晶石 #{crystalId} ({ctile.X},{ctile.Y})：{o.Message}");
                        yield break;
                    }
                    if (cave.netPhase.Value != IslandWestCave1.PHASE_WAIT_FOR_PLAYER_INPUT)
                        break;
                    int beforeIdx = cave.currentCrystalSequenceIndex.Value;
                    int beforePhase = cave.netPhase.Value;
                    Face(ctile);
                    cave.checkAction(new xLocation(ctile.X, ctile.Y), Game1.viewport, Player);
                    for (int w = 0; w < 90 && Loc == cave
                        && cave.currentCrystalSequenceIndex.Value == beforeIdx
                        && cave.netPhase.Value == beforePhase
                        && cave.isActivated.Value
                        && !cave.completed.Value; w++)
                        yield return null;
                    foreach (var s in WaitTicks(8))
                        yield return s;
                }
            }

            // 等过关动画结束并掉出 3 颗金核桃
            for (int t = 0; t < 240 && Loc == cave && !cave.completed.Value; t++)
            {
                if (Blocked(out _))
                    break;
                yield return null;
            }
            if (!cave.completed.Value)
            {
                yield return ActionResult.Fail($"晶石洞谜题还没解完（当前状态 phase={cave.netPhase.Value}），可以再调用一次 crystal_cave");
                yield break;
            }
            foreach (var s in WaitTicks(30))
                yield return s;
            for (int k = 0; k < 5 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; k++)
                foreach (var s in Dialogue(null))
                    if (s is not ActionResult)
                        yield return s;
            foreach (var s in CollectDebris(new Rectangle(4, 0, 5, 5), pickupNotes, 90))
                yield return s;
            int got = Game1.netWorldState.Value.GoldenWalnuts - walnutsBefore;
            yield return ActionResult.Success($"解开了姜岛晶石洞谜题！拿到 {Math.Max(got, 3)} 颗金核桃（现有 {Game1.netWorldState.Value.GoldenWalnuts} 个，一共找到 {Game1.netWorldState.Value.GoldenWalnutsFound} 个）");
        }

        // ───────────────────────── 姜岛：宝石鸟祭坛（IslandShrine） ─────────────────────────

        internal static readonly (string key, string label, string islandLoc, Point tile)[] GemShrineSlots =
        {
            ("north", "北", "IslandNorth", new Point(24, 25)),
            ("south", "南", "IslandSouth", new Point(24, 28)),
            ("east",  "东", "IslandEast",  new Point(27, 27)),
            ("west",  "西", "IslandWest",  new Point(21, 27)),
        };

        internal static string NormalizeGemBirdGem(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return null;
            string s = spec.Trim().Replace("(O)", "", StringComparison.OrdinalIgnoreCase);
            return s.ToLowerInvariant() switch
            {
                "none" or "take" or "clear" or "取" or "取下" or "拿走" or "清空" => "take",
                "60" or "emerald" or "绿宝石" => "60",
                "62" or "aquamarine" or "海蓝宝石" => "62",
                "64" or "ruby" or "红宝石" => "64",
                "66" or "amethyst" or "紫水晶" => "66",
                "68" or "topaz" or "黄水晶" => "68",
                _ => null
            };
        }

        internal static string GemBirdDisplayName(string itemId) => itemId?.Replace("(O)", "") switch
        {
            "60" => "绿宝石",
            "62" => "海蓝宝石",
            "64" => "红宝石",
            "66" => "紫水晶",
            "68" => "黄水晶",
            _ => itemId ?? "未知宝石"
        };

        internal static string GemBirdNameByLocation(string locName) => GemShrineSlots.FirstOrDefault(s => s.islandLoc.Equals(locName, StringComparison.OrdinalIgnoreCase)).label is { Length: > 0 } lbl
            ? $"{lbl}（{locName}）"
            : locName;

        internal static string FormatGemShrineStatus(bool finished, IReadOnlyList<(string label, string islandLoc, Point tile, string heldId)> slots,
            (string locName, Point tile, string gemId)? activeBird = null)
        {
            string body = string.Join("；", slots.Select(s =>
                $"{s.label}/{s.islandLoc} ({s.tile.X},{s.tile.Y})：{(string.IsNullOrEmpty(s.heldId) ? "空" : GemBirdDisplayName(s.heldId))}"));
            string msg = finished ? $"宝石鸟祭坛谜题已完成：{body}" : $"宝石鸟祭坛（未完成）：{body}";
            if (!finished && activeBird is { } b)
                msg += $"\n当前岛上下雨天发现一只宝石鸟：{b.locName} ({b.tile.X},{b.tile.Y})，对应宝石【{GemBirdDisplayName(b.gemId)}】（走过去触发它会掉落宝石）";
            return msg;
        }

        private static (string locName, Point tile, string gemId)? FindActiveGemBirdOnIsland()
        {
            foreach (var loc in Game1.locations.OfType<IslandLocation>())
            {
                var bird = loc.locationGemBird.Value;
                if (bird != null && !bird.flying.Value)
                    return (loc.Name, new Point((int)(bird.position.X / 64f), (int)(bird.position.Y / 64f)), bird.itemIndex.Value);
            }
            return null;
        }

        /// <summary>去姜岛宝石鸟祭坛（IslandShrine），查看各方位基座状态，或把绿宝石/海蓝宝石/红宝石/紫水晶/黄水晶放到对应方位基座上。</summary>
        public IEnumerable<object> GemShrine(string north, string south, string east, string west)
        {
            var inputs = new (string key, string raw)[] { ("north", north), ("south", south), ("east", east), ("west", west) };
            var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, raw) in inputs)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string norm = NormalizeGemBirdGem(raw);
                if (norm == null)
                {
                    yield return ActionResult.Fail($"宝石鸟祭坛 {key}=\"{raw}\" 认不出来：只支持 绿宝石(Emerald)、海蓝宝石(Aquamarine)、红宝石(Ruby)、紫水晶(Amethyst)、黄水晶(Topaz)，或 take 取下");
                    yield break;
                }
                plan[key] = norm;
            }

            var o = new Outcome();
            if (Loc is not StardewValley.Locations.IslandShrine)
            {
                foreach (var s in TravelInner("IslandShrine", o))
                    yield return s;
                if (!o.Ok || Loc is not StardewValley.Locations.IslandShrine)
                {
                    yield return ActionResult.Fail($"走不到姜岛宝石鸟祭坛（IslandShrine，从 IslandEast 往东进入）：{o.Message}");
                    yield break;
                }
            }

            var shrine = (StardewValley.Locations.IslandShrine)Loc;
            int walnutsBefore = Game1.netWorldState.Value.GoldenWalnuts;
            var pickupNotes = new List<string>();
            if (shrine.puzzleFinished.Value)
            {
                foreach (var s in CollectDebris(new Rectangle(21, 17, 7, 6), pickupNotes, 45))
                    yield return s;
                int extra = Game1.netWorldState.Value.GoldenWalnuts - walnutsBefore;
                yield return ActionResult.Success(extra > 0
                    ? $"宝石鸟祭坛谜题早已完成，顺手捡走地上剩下的 {extra} 颗金核桃（现有 {Game1.netWorldState.Value.GoldenWalnuts} 个）"
                    : "宝石鸟祭坛谜题早已解开了（5 颗金核桃已拿到）");
                yield break;
            }

            StardewValley.Objects.ItemPedestal GetPedestal(string key) => key switch
            {
                "north" => shrine.northPedestal,
                "south" => shrine.southPedestal,
                "east" => shrine.eastPedestal,
                "west" => shrine.westPedestal,
                _ => null
            };
            List<(string label, string islandLoc, Point tile, string heldId)> SnapshotSlots() =>
                GemShrineSlots.Select(s => (s.label, s.islandLoc, s.tile, GetPedestal(s.key)?.heldObject.Value?.ItemId)).ToList();

            var birdNow = FindActiveGemBirdOnIsland();
            if (plan.Count == 0)
            {
                yield return ActionResult.Success(FormatGemShrineStatus(shrine.puzzleFinished.Value, SnapshotSlots(), birdNow)
                    + "\n提示：姜岛下雨天到 IslandNorth/South/East/West 找到对应的宝石鸟并拾取宝石后，用 gem_shrine north=... south=... east=... west=... 放到对应基座");
                yield break;
            }

            // 先把需要清空或替换的基座取下来，这样如果只是两个方位放反了可以直接互换
            foreach (var slot in GemShrineSlots)
            {
                if (!plan.TryGetValue(slot.key, out string targetGem))
                    continue;
                var ped = GetPedestal(slot.key);
                string cur = ped?.heldObject.Value?.ItemId;
                if (ped != null && !ped.locked.Value && cur != null && (targetGem == "take" || cur != targetGem))
                {
                    foreach (var s in GoAdjacent(slot.tile, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"走不到{slot.label}基座 ({slot.tile.X},{slot.tile.Y})：{o.Message}");
                        yield break;
                    }
                    Face(slot.tile);
                    ped.checkForAction(Player);
                    foreach (var s in WaitTicks(8))
                        yield return s;
                }
            }

            // 核对背包里是否齐备要放的宝石（取下来的已经进背包）
            var missing = new List<string>();
            var needCounts = new Dictionary<string, int>();
            foreach (var slot in GemShrineSlots)
            {
                if (!plan.TryGetValue(slot.key, out string targetGem) || targetGem == "take")
                    continue;
                if (GetPedestal(slot.key)?.heldObject.Value?.ItemId == targetGem)
                    continue;
                needCounts[targetGem] = needCounts.GetValueOrDefault(targetGem) + 1;
            }
            foreach (var (gemId, need) in needCounts)
            {
                int have = Player.Items.CountId("(O)" + gemId);
                if (have < need)
                    missing.Add($"{GemBirdDisplayName(gemId)} x{need}（背包里只有 {have}）");
            }
            if (missing.Count > 0)
            {
                yield return ActionResult.Fail($"背包里缺少要放的宝石：{string.Join("、", missing)}。"
                    + FormatGemShrineStatus(shrine.puzzleFinished.Value, SnapshotSlots(), FindActiveGemBirdOnIsland()));
                yield break;
            }

            // 逐个走到基座旁放上宝石
            foreach (var slot in GemShrineSlots)
            {
                if (!plan.TryGetValue(slot.key, out string targetGem) || targetGem == "take")
                    continue;
                var ped = GetPedestal(slot.key);
                if (ped == null || ped.locked.Value || ped.heldObject.Value?.ItemId == targetGem)
                    continue;
                int itemIdx = -1;
                for (int i = 0; i < Player.Items.Count; i++)
                    if (Player.Items[i] is SObject it && it.QualifiedItemId == "(O)" + targetGem)
                    {
                        itemIdx = i;
                        break;
                    }
                if (itemIdx < 0)
                    continue;
                foreach (var s in GoAdjacent(slot.tile, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"走不到{slot.label}基座 ({slot.tile.X},{slot.tile.Y})：{o.Message}");
                    yield break;
                }
                Player.CurrentToolIndex = itemIdx;
                Face(slot.tile);
                shrine.checkAction(new xLocation(slot.tile.X, slot.tile.Y), Game1.viewport, Player);
                foreach (var s in WaitTicks(10))
                    yield return s;
            }

            // 等 IslandShrine.UpdateWhenCurrentLocation 检查 match 并掉核桃
            for (int t = 0; t < 45 && !shrine.puzzleFinished.Value; t++)
                yield return null;

            if (shrine.puzzleFinished.Value)
            {
                foreach (var s in WaitTicks(30))
                    yield return s;
                for (int k = 0; k < 5 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; k++)
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;
                foreach (var s in CollectDebris(new Rectangle(21, 17, 7, 6), pickupNotes, 90))
                    yield return s;
                int got = Game1.netWorldState.Value.GoldenWalnuts - walnutsBefore;
                yield return ActionResult.Success($"宝石鸟祭坛谜题解开了！拿到 {Math.Max(got, 5)} 颗金核桃（现有 {Game1.netWorldState.Value.GoldenWalnuts} 个，一共找到 {Game1.netWorldState.Value.GoldenWalnutsFound} 个）");
                yield break;
            }

            yield return ActionResult.Success("已按指定方位放好宝石，但祭坛还没解开（可能还有方向没放对或没放满）。"
                + FormatGemShrineStatus(shrine.puzzleFinished.Value, SnapshotSlots(), FindActiveGemBirdOnIsland()));
        }

        // ───────────────────────── 姜岛：雨天美人鱼与长笛块（IslandSouthEast） ─────────────────────────

        // 东南海滩左起 5 堆石头暗示 2、11、9、5、7（相对音差 +900, -200, -400, +200），对应音高 200/1100/900/500/700
        internal static readonly int[] MermaidFlutePitches = { 200, 1100, 900, 500, 700 };

        private static readonly Point[] DefaultMermaidFluteTiles =
        {
            new(16, 27),
            new(18, 27),
            new(20, 27),
            new(22, 27),
            new(24, 27),
        };

        /// <summary>读取长笛块（(O)464）当前音高（0~2400，每档 100）。</summary>
        internal static int ParseFlutePitch(SObject obj) =>
            obj != null && int.TryParse(obj.preservedParentSheetIndex.Value, out int p) ? p : 0;

        /// <summary>读取鼓块（(O)463）当前音色编号（0~6）。</summary>
        internal static int ParseDrumSound(SObject obj) =>
            obj != null && int.TryParse(obj.preservedParentSheetIndex.Value, out int s) ? s : 0;

        /// <summary>按原版 CheckForActionOnFluteBlock 的规则推算调音 1 下后的音高（0→100→…→2300→2400→0）。</summary>
        internal static int NextFlutePitch(int pitch) => pitch switch
        {
            2300 => 2400,
            2400 => 0,
            _ => (pitch + 100) % 2400,
        };

        /// <summary>计算把长笛块从 currentPitch 调到 targetPitch（0~2400 整百）需要交互多少下。</summary>
        internal static int FluteClicksNeeded(int currentPitch, int targetPitch)
        {
            int cur = currentPitch;
            for (int clicks = 0; clicks < 25; clicks++)
            {
                if (cur == targetPitch)
                    return clicks;
                cur = NextFlutePitch(cur);
            }
            return 0;
        }

        internal static string FormatMermaidPuzzleStatus(bool finished, bool mermaidHere, IReadOnlyList<(Point tile, int pitch)> flutes)
        {
            if (finished)
                return "美人鱼谜题：已完成（5 颗金核桃奖励已解锁）";
            string head = mermaidHere
                ? "美人鱼谜题：今天姜岛下雨，美人鱼就在东南海滩（未完成，可直接用 mermaid_puzzle 自动摆放/调音并走过吹响，拿 5 颗金核桃）"
                : "美人鱼谜题：未完成（今天姜岛没下雨，美人鱼不在，需等姜岛下雨天再来用 mermaid_puzzle）";
            if (flutes == null || flutes.Count == 0)
                return head;
            string list = string.Join("、", flutes.Select(f => $"({f.tile.X},{f.tile.Y})={f.pitch}[第{f.pitch / 100}档]"));
            return $"{head}\n海滩上已放置 {flutes.Count} 个长笛块：{list}（目标从左到右音高：200/1100/900/500/700，即 2/11/9/5/7 档）";
        }

        private static bool TryFindExistingMermaidFluteRow(IslandSouthEast se, out Point[] tiles, out int walkY)
        {
            var byRow = se.Objects.Pairs
                .Where(p => p.Value.QualifiedItemId == "(O)464")
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y))
                .GroupBy(p => p.Y)
                .OrderBy(g => Math.Abs(g.Key - 27));

            foreach (var group in byRow)
            {
                var ordered = group.OrderBy(p => p.X).ToList();
                if (ordered.Count < 5)
                    continue;
                int y = group.Key;
                for (int start = 0; start <= ordered.Count - 5; start++)
                {
                    var slice = ordered.GetRange(start, 5);
                    foreach (int wy in new[] { y + 1, y - 1 })
                    {
                        if (IsClearMermaidWalkRow(se, slice, wy))
                        {
                            tiles = slice.ToArray();
                            walkY = wy;
                            return true;
                        }
                    }
                }
            }
            tiles = null;
            walkY = 0;
            return false;
        }

        private static bool IsClearMermaidWalkRow(IslandSouthEast se, IReadOnlyList<Point> fluteTiles, int walkY)
        {
            int minX = fluteTiles[0].X;
            int maxX = fluteTiles[fluteTiles.Count - 1].X;
            int oppositeY = 2 * walkY - fluteTiles[0].Y;
            for (int x = minX; x <= maxX; x++)
            {
                if (!Pathfinder.IsPassable(se, x, walkY))
                    return false;
                // 避免走过路径另一侧还有多余长笛块把旋律打乱
                if (se.Objects.TryGetValue(new Vector2(x, oppositeY), out var opp) && opp.QualifiedItemId == "(O)464")
                    return false;
            }
            return true;
        }

        private static bool TryChooseMermaidFluteSlots(IslandSouthEast se, out Point[] tiles, out int walkY)
        {
            if (TryFindExistingMermaidFluteRow(se, out tiles, out walkY))
                return true;

            bool CanUseSlot(Point p) =>
                (se.Objects.TryGetValue(new Vector2(p.X, p.Y), out var ex) && ex.QualifiedItemId == "(O)464")
                || (!se.Objects.ContainsKey(new Vector2(p.X, p.Y)) && se.CanItemBePlacedHere(new Vector2(p.X, p.Y)));

            if (DefaultMermaidFluteTiles.All(CanUseSlot) && IsClearMermaidWalkRow(se, DefaultMermaidFluteTiles, 28))
            {
                tiles = DefaultMermaidFluteTiles;
                walkY = 28;
                return true;
            }

            // 默认点位被占时，在沙滩附近挑一行 5 个可放（或已有长笛块）的格子
            for (int y = 27; y <= 30; y++)
            {
                foreach (int wy in new[] { y + 1, y - 1 })
                {
                    var found = new List<Point>();
                    for (int x = 15; x <= 26 && found.Count < 5; x++)
                    {
                        var p = new Point(x, y);
                        if (CanUseSlot(p))
                            found.Add(p);
                    }
                    if (found.Count == 5 && IsClearMermaidWalkRow(se, found, wy))
                    {
                        tiles = found.ToArray();
                        walkY = wy;
                        return true;
                    }
                }
            }

            tiles = null;
            walkY = 0;
            return false;
        }

        /// <summary>去姜岛东南海滩（IslandSouthEast），在雨天摆放（或复用）5 个长笛块、按 2/11/9/5/7 档调音后从左向右走过吹响，捡走 5 颗金核桃。</summary>
        public IEnumerable<object> MermaidPuzzle()
        {
            var o = new Outcome();
            if (Loc is not IslandSouthEast)
            {
                foreach (var s in TravelInner("IslandSouthEast", o))
                    yield return s;
                if (!o.Ok || Loc is not IslandSouthEast)
                {
                    yield return ActionResult.Fail($"走不到姜岛东南海滩（IslandSouthEast）：{o.Message}（需要先在姜岛南边喂 20 颗金核桃解锁度假村）");
                    yield break;
                }
            }

            var se = (IslandSouthEast)Loc;
            int walnutsBefore = Game1.netWorldState.Value.GoldenWalnuts;
            var pickupNotes = new List<string>();
            if (se.mermaidPuzzleFinished.Value)
            {
                foreach (var s in CollectDebris(new Rectangle(24, 24, 14, 14), pickupNotes, 45))
                    yield return s;
                int extra = Game1.netWorldState.Value.GoldenWalnuts - walnutsBefore;
                yield return ActionResult.Success(extra > 0
                    ? $"美人鱼谜题早已解开，顺手捡起地上剩下的 {extra} 颗金核桃（现有 {Game1.netWorldState.Value.GoldenWalnuts} 个）"
                    : "美人鱼谜题已经解开了（5 颗金核桃已拿到）");
                yield break;
            }

            if (!se.MermaidIsHere())
            {
                yield return ActionResult.Fail("今天姜岛没有下雨，美人鱼不在东南海滩（需等姜岛下雨天再来调用 mermaid_puzzle）");
                yield break;
            }

            if (!TryChooseMermaidFluteSlots(se, out var slots, out int walkY))
            {
                yield return ActionResult.Fail("东南海滩 (15,27)~(26,30) 找不到能排下 5 个长笛块并从旁边走过的空行，先清理一下沙滩上的杂物再试");
                yield break;
            }

            int alreadyPlaced = slots.Count(p => se.Objects.TryGetValue(new Vector2(p.X, p.Y), out var ex) && ex.QualifiedItemId == "(O)464");
            int needPlace = 5 - alreadyPlaced;
            int bagFlutes = Player.Items.CountId("(O)464");
            if (bagFlutes < needPlace)
            {
                yield return ActionResult.Fail($"长笛块不够：还需要放 {needPlace} 个，但背包里只有 {bagFlutes} 个（沙滩上已就位 {alreadyPlaced} 个）。长笛块配方（罗宾 6 心事件解锁）：木头×2 + 铜矿石×1 + 纤维×20");
                yield break;
            }

            // 1. 补齐还没放的长笛块
            int placedNow = 0;
            foreach (var tile in slots)
            {
                if (se.Objects.TryGetValue(new Vector2(tile.X, tile.Y), out var ex) && ex.QualifiedItemId == "(O)464")
                    continue;
                int idx = -1;
                for (int i = 0; i < Player.Items.Count; i++)
                    if (Player.Items[i] is SObject it && it.QualifiedItemId == "(O)464")
                    {
                        idx = i;
                        break;
                    }
                if (idx < 0)
                {
                    yield return ActionResult.Fail("背包里的长笛块数量不足，无法放齐 5 个");
                    yield break;
                }
                foreach (var s in GoAdjacent(tile, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"走不到放置点 ({tile.X},{tile.Y})：{o.Message}");
                    yield break;
                }
                Player.CurrentToolIndex = idx;
                var item = (SObject)Player.Items[idx];
                bool placed = Utility.tryToPlaceItem(se, item, tile.X * 64 + 32, tile.Y * 64 + 32);
                foreach (var s in WaitTicks(8))
                    yield return s;
                if (!placed || !se.Objects.TryGetValue(new Vector2(tile.X, tile.Y), out var check) || check.QualifiedItemId != "(O)464")
                {
                    yield return ActionResult.Fail($"在 ({tile.X},{tile.Y}) 放置长笛块失败");
                    yield break;
                }
                placedNow++;
            }

            // 2. 逐个调音到目标音高 200 / 1100 / 900 / 500 / 700（2 / 11 / 9 / 5 / 7 档）
            PutAwayHeldItem();
            int totalClicks = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var tile = slots[i];
                int targetPitch = MermaidFlutePitches[i];
                if (!se.Objects.TryGetValue(new Vector2(tile.X, tile.Y), out var flute) || flute.QualifiedItemId != "(O)464")
                {
                    yield return ActionResult.Fail($"({tile.X},{tile.Y}) 上的长笛块不见了");
                    yield break;
                }
                int clicks = FluteClicksNeeded(ParseFlutePitch(flute), targetPitch);
                if (clicks <= 0)
                    continue;
                foreach (var s in GoAdjacent(tile, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"走不到长笛块 ({tile.X},{tile.Y}) 旁边调音：{o.Message}");
                    yield break;
                }
                PutAwayHeldItem();
                Face(tile);
                for (int c = 0; c < clicks; c++)
                {
                    if (Blocked(out string why))
                    {
                        yield return ActionResult.Fail($"给长笛块调音时被打断：{why}");
                        yield break;
                    }
                    flute.checkForAction(Player);
                    totalClicks++;
                    foreach (var s in WaitTicks(2))
                        yield return s;
                }
                int after = ParseFlutePitch(flute);
                if (after != targetPitch)
                {
                    yield return ActionResult.Fail($"({tile.X},{tile.Y}) 的长笛块调音后音高为 {after}，未能调到目标 {targetPitch}");
                    yield break;
                }
            }

            // 3. 从左向右依次走过 5 个长笛块吹响（若此前误触残留 songIndex，最多走两遍）
            for (int pass = 0; pass < 2 && !se.mermaidPuzzleFinished.Value; pass++)
            {
                if (Blocked(out string why))
                {
                    yield return ActionResult.Fail($"走向长笛块时被打断：{why}");
                    yield break;
                }
                // 先退到最左长笛块斜角（不触发正交相邻吹响），等所有长笛块的 1 秒冷却结束
                var stagingCandidates = new[]
                {
                    new Point(slots[0].X - 1, walkY),
                    new Point(slots[0].X, walkY + (walkY > slots[0].Y ? 1 : -1)),
                    new Point(slots[0].X - 2, walkY),
                };
                Point? staging = stagingCandidates.FirstOrDefault(p => Pathfinder.IsPassable(se, p.X, p.Y));
                if (staging is { X: > 0, Y: > 0 } st)
                {
                    foreach (var s in GoToAny(new[] { st }, o, "长笛块起点"))
                        yield return s;
                }
                foreach (var s in WaitTicks(70))
                    yield return s;

                for (int i = 0; i < slots.Length && !se.mermaidPuzzleFinished.Value; i++)
                {
                    var stepTile = new Point(slots[i].X, walkY);
                    foreach (var s in GoToAny(new[] { stepTile }, o, $"第 {i + 1} 个长笛块前 ({stepTile.X},{stepTile.Y})"))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"走过第 {i + 1} 个长笛块 ({stepTile.X},{stepTile.Y}) 失败：{o.Message}");
                        yield break;
                    }
                    foreach (var s in WaitTicks(5))
                        yield return s;
                }

                for (int w = 0; w < 60 && Loc == se && !se.mermaidPuzzleFinished.Value; w++)
                    yield return null;
            }

            if (!se.mermaidPuzzleFinished.Value)
            {
                yield return ActionResult.Fail("5 个长笛块已调到 200/1100/900/500/700 并从左到右走过，但美人鱼谜题尚未判定完成，可以再调用一次 mermaid_puzzle");
                yield break;
            }

            foreach (var s in WaitTicks(45))
                yield return s;
            foreach (var s in CollectDebris(new Rectangle(24, 24, 14, 14), pickupNotes, 90))
                yield return s;
            int got = Game1.netWorldState.Value.GoldenWalnuts - walnutsBefore;
            string detail = (placedNow > 0 ? $"放了 {placedNow} 个长笛块、" : "") + (totalClicks > 0 ? $"调音 {totalClicks} 下、" : "");
            yield return ActionResult.Success($"解开了姜岛美人鱼谜题（{detail}从左到右吹响 2/11/9/5/7 档）！拿到 {Math.Max(got, 5)} 颗金核桃（现有 {Game1.netWorldState.Value.GoldenWalnuts} 个，一共找到 {Game1.netWorldState.Value.GoldenWalnutsFound} 个）");
        }

        // ───────────────────────── 姜岛：海盗湾投飞镖小游戏（IslandSouthEastCave） ─────────────────────────

        // 与原版 StardewValley.Minigames.Darts.GetPointsForAim 一致的 20 个扇区顺序（从正上方 20 顺时针排列）
        internal static readonly int[] DartsSectors =
        {
            20, 1, 18, 4, 13, 6, 10, 15, 2, 17,
            3, 19, 7, 16, 8, 11, 14, 9, 12, 5,
        };

        private static bool TrySingleDartFinish(int points, out (int baseNumber, int multiplier, int score) hit)
        {
            if (points == 50) { hit = (50, 1, 50); return true; }
            if (points == 25) { hit = (25, 1, 25); return true; }
            if (points >= 1 && points <= 20) { hit = (points, 1, points); return true; }
            if (points >= 2 && points <= 40 && points % 2 == 0) { hit = (points / 2, 2, points); return true; }
            if (points >= 3 && points <= 60 && points % 3 == 0) { hit = (points / 3, 3, points); return true; }
            hit = default;
            return false;
        }

        /// <summary>按剩余分数（1~301）选这一镖的瞄准目标，确保不爆分且用最少镖数归零（301 分只需 6 镖：5×T20 + S1）。</summary>
        internal static (int baseNumber, int multiplier, int score) ChooseDartsTarget(int remainingPoints)
        {
            if (remainingPoints <= 0)
                return (20, 1, 20);
            if (remainingPoints >= 60)
                return (20, 3, 60);
            if (TrySingleDartFinish(remainingPoints, out var direct))
                return direct;
            for (int s = 20; s >= 1; s--)
                if (remainingPoints - s > 0 && TrySingleDartFinish(remainingPoints - s, out _))
                    return (s, 1, s);
            return (Math.Min(remainingPoints, 20), 1, Math.Min(remainingPoints, 20));
        }

        /// <summary>把目标分值换算成 Darts 小游戏 320×320 棋盘坐标系下的精确落点。</summary>
        internal static Vector2 DartsAimPosition(int baseNumber, int multiplier, Vector2 dartBoardCenter)
        {
            if (baseNumber == 50)
                return dartBoardCenter;
            if (baseNumber == 25)
                return dartBoardCenter - new Vector2(0f, 8f);
            int idx = Array.IndexOf(DartsSectors, baseNumber);
            if (idx < 0)
                return dartBoardCenter;
            double rad = (idx * 18.0 + 90.0) * Math.PI / 180.0;
            float radius = multiplier switch
            {
                3 => 50.5f, // 三倍环 [46, 55) 中点
                2 => 83.5f, // 两倍环 [79, 88] 中点
                _ => 67.0f, // 外单倍环 [55, 79) 中点
            };
            return dartBoardCenter - new Vector2((float)(Math.Cos(rad) * radius), (float)(Math.Sin(rad) * radius));
        }

        internal static string FormatPirateDartsStatus(bool pirateNight, int wonNuts, int dayOfMonth, int timeOfDay, bool raining)
        {
            string progress = wonNuts >= 3
                ? "3 颗飞镖金核桃已全部拿到（仍可用 darts_game 游玩）"
                : $"已拿到 {wonNuts}/3 颗飞镖金核桃（剩余 {3 - wonNuts} 颗，分别需 20/15/10 镖内归零）";
            if (pirateNight)
                return $"海盗湾今晚营业中：{progress}；直接调用 darts_game 自动开局并投完。";
            var reasons = new List<string>();
            if (dayOfMonth % 2 != 0) reasons.Add($"偶数日才开（今天第 {dayOfMonth} 天）");
            if (raining) reasons.Add("姜岛下雨天不开");
            if (timeOfDay < 2000) reasons.Add($"晚上 20:00 后才开（现在 {timeOfDay / 100:D2}:{timeOfDay % 100:D2}）");
            return $"海盗湾现在没海盗（{string.Join("，", reasons)}）。{progress}。";
        }

        /// <summary>去姜岛海盗湾（IslandSouthEastCave）自动开始并完成一局投飞镖小游戏（301 分归零），领走金核桃奖励；若已在投飞镖小游戏里则直接接管投完。</summary>
        public IEnumerable<object> DartsGame()
        {
            var o = new Outcome();
            int walnutsBefore = Game1.netWorldState.Value.GoldenWalnuts;
            int nutsDroppedBefore = Player.team.GetDroppedLimitedNutCount("Darts");

            // 如果已经在投飞镖小游戏里，直接接管打完；如果是别的小游戏则报错
            if (Game1.currentMinigame != null && Game1.currentMinigame is not Darts)
            {
                string mgName = Game1.currentMinigame.minigameId() ?? Game1.currentMinigame.GetType().Name;
                yield return ActionResult.Fail($"当前正在运行其他小游戏（{mgName}），请先用 close_menu 退出");
                yield break;
            }

            if (Game1.currentMinigame is not Darts)
            {
                bool alreadyAskedDarts = Loc is IslandSouthEastCave
                    && Game1.activeClickableMenu is DialogueBox { isQuestion: true }
                    && Loc.lastQuestionKey == "DartsGame";

                if (!alreadyAskedDarts && Blocked(out string why))
                {
                    yield return ActionResult.Fail($"无法开始投飞镖小游戏：{why}");
                    yield break;
                }

                if (Loc is not IslandSouthEastCave)
                {
                    foreach (var s in TravelInner("IslandSouthEastCave", o))
                        yield return s;
                    if (!o.Ok || Loc is not IslandSouthEastCave)
                    {
                        yield return ActionResult.Fail($"走不到姜岛海盗湾（IslandSouthEastCave）：{o.Message}（需要先在姜岛南边喂 20 颗金核桃解锁度假村，从东南海滩进入）");
                        yield break;
                    }
                }

                var cave = (IslandSouthEastCave)Loc;
                if (!IslandSouthEastCave.isPirateNight())
                {
                    yield return ActionResult.Fail(FormatPirateDartsStatus(
                        pirateNight: false,
                        wonNuts: nutsDroppedBefore,
                        dayOfMonth: Game1.dayOfMonth,
                        timeOfDay: Game1.timeOfDay,
                        raining: Game1.IsRainingHere(cave)));
                    yield break;
                }

                // 20:00 前就在洞里时，地图还没切成海盗版本，游戏会自动把人传回东南海滩；等传出或主动出去再进一次
                if (!cave.wasPirateCaveOnLoad)
                {
                    for (int w = 0; w < 30 && Loc == cave; w++)
                        yield return null;
                    if (Loc == cave)
                    {
                        foreach (var s in TravelInner("IslandSouthEast", o))
                            yield return s;
                    }
                    foreach (var s in TravelInner("IslandSouthEastCave", o))
                        yield return s;
                    if (!o.Ok || Loc is not IslandSouthEastCave reloadedCave || !reloadedCave.wasPirateCaveOnLoad)
                    {
                        yield return ActionResult.Fail("海盗湾地图尚未切换到夜晚版本，请先走出洞口到 IslandSouthEast 再重新进入 IslandSouthEastCave");
                        yield break;
                    }
                    cave = reloadedCave;
                }

                if (!alreadyAskedDarts)
                {
                    var dartsTile = FindActionTile(cave, "DartsGame") ?? new Point(30, 8);
                    foreach (var s in GoAdjacent(dartsTile, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"走不到投飞镖海盗 ({dartsTile.X},{dartsTile.Y}) 旁边：{o.Message}");
                        yield break;
                    }
                    PutAwayHeldItem();
                    Face(dartsTile);
                    cave.checkAction(new xLocation(dartsTile.X, dartsTile.Y), Game1.viewport, Player);
                    for (int w = 0; w < 60 && Game1.activeClickableMenu is not DialogueBox { isQuestion: true } && Game1.currentMinigame is not Darts; w++)
                        yield return null;
                }

                if (Game1.currentMinigame is not Darts && Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask)
                {
                    int yesIdx = 0;
                    if (ask.responses != null)
                    {
                        for (int i = 0; i < ask.responses.Length; i++)
                        {
                            if (ask.responses[i]?.responseKey == "Yes")
                            {
                                yesIdx = i;
                                break;
                            }
                        }
                    }
                    foreach (var s in Dialogue(yesIdx))
                        if (s is not ActionResult)
                            yield return s;
                    for (int w = 0; w < 120 && Game1.currentMinigame is not Darts; w++)
                        yield return null;
                }

                if (Game1.currentMinigame is not Darts)
                {
                    yield return ActionResult.Fail("未能进入投飞镖小游戏（与 (30,8) 海盗对话后未启动 Darts）。当前界面：" + observer.DescribeMenu());
                    yield break;
                }
            }

            // 接管 Darts 小游戏：每次进入 Aiming 状态时瞄准最优目标并精准投出，由原版 tick 完成飞行、计分与结算
            int lastPoints = 301;
            int lastThrows = 0;
            int aimingWait = 0;
            for (int tick = 0; tick < 6000 && Game1.currentMinigame is Darts darts; tick++)
            {
                lastPoints = darts.points;
                lastThrows = darts.throwsCount;
                if (darts.currentGameState == Darts.GameState.Aiming && darts.dartCount > 0 && darts.points > 0)
                {
                    var target = ChooseDartsTarget(darts.points);
                    Vector2 aim = DartsAimPosition(target.baseNumber, target.multiplier, darts.dartBoardCenter);
                    darts.aimPosition = aim;
                    aimingWait++;
                    if (aimingWait >= 12)
                    {
                        darts.chargeTime = 0f;
                        darts.dartCount--;
                        darts.throwsCount++;
                        darts.FireDart(0f);
                        aimingWait = 0;
                    }
                }
                else
                {
                    aimingWait = 0;
                }
                yield return null;
            }

            if (Game1.currentMinigame is Darts stuck)
            {
                stuck.QuitGame();
                yield return ActionResult.Fail($"投飞镖小游戏超时未结算（剩余 {stuck.points} 分，已退出）");
                yield break;
            }

            // 等海盗的胜负对话框弹出并点完（金核桃在对话结束后的 Game1.afterDialogues 才掉落）
            for (int w = 0; w < 45 && Game1.activeClickableMenu is not DialogueBox; w++)
                yield return null;
            for (int k = 0; k < 6 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; k++)
                foreach (var s in Dialogue(null))
                    if (s is not ActionResult)
                        yield return s;

            bool won = lastPoints == 0;
            if (!won)
            {
                yield return ActionResult.Fail($"本局投飞镖未能在剩余镖数内归零（结束时剩 {lastPoints} 分，共投 {lastThrows} 镖），可再调 darts_game 重开一局");
                yield break;
            }

            // 捡起掉落的金核桃（原版掉在 (1984, 512) 即图块 (31, 8)）
            foreach (var s in WaitTicks(45))
                yield return s;
            var pickupNotes = new List<string>();
            if (Loc is IslandSouthEastCave)
            {
                foreach (var s in CollectDebris(new Rectangle(27, 5, 9, 8), pickupNotes, 90))
                    yield return s;
            }

            int nutsDroppedAfter = Player.team.GetDroppedLimitedNutCount("Darts");
            int gotWalnuts = Game1.netWorldState.Value.GoldenWalnuts - walnutsBefore;
            bool gotNewPrize = nutsDroppedAfter > nutsDroppedBefore || gotWalnuts > 0;
            string perfect = lastThrows <= 6 ? "（完美通关！）" : "";
            string nutInfo = gotNewPrize
                ? $"拿到 {Math.Max(gotWalnuts, 1)} 颗金核桃（海盗湾飞镖奖励进度 {nutsDroppedAfter}/3，现有 {Game1.netWorldState.Value.GoldenWalnuts} 个"
                  + (nutsDroppedAfter < 3 ? "；可再调 darts_game 拿剩下的奖励" : "，已全部拿齐") + "）"
                : $"海盗湾飞镖的 3 颗金核桃奖励已全部拿过（当前进度 {nutsDroppedAfter}/3）";
            yield return ActionResult.Success($"赢了海盗湾投飞镖小游戏！共投 {lastThrows} 镖将 301 分精准归零{perfect}，{nutInfo}");
        }
    }
}
