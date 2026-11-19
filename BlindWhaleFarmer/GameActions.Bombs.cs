using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Monsters;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// 放炸弹：走到炸弹那格旁边放下，马上跑到爆炸范围外，等它炸完。
    ///
    /// 游戏里的规则（反编译源码）：
    /// - 樱桃炸弹 (O)286 半径 3、炸弹 (O)287 半径 5、超级炸弹 (O)288 半径 7（TemporaryAnimatedSprite 的 bombRadius）。
    /// - 引信：100 毫秒一帧、循环 24 次，约 2.4 秒；时间不走（开着界面）的时候引信也停。
    /// - 爆炸范围是以炸弹为中心、边长 2r+1 格的正方形（GameLocation.explode 的 rectangle）：
    ///   正方形里的怪按炸弹伤害打（倒地的木乃伊遇到爆炸直接死），人的碰撞框和正方形重叠就挨 r×3 点伤害（9/15/21）。
    ///   所以要横向或纵向离开炸弹超过 r 格才安全。
    /// </summary>
    internal sealed partial class GameActions
    {
        private static readonly (string id, int radius, string name)[] Bombs =
        {
            ("(O)286", 3, "樱桃炸弹"),
            ("(O)287", 5, "炸弹"),
            ("(O)288", 7, "超级炸弹"),
        };

        /// <summary>引信从放下到爆炸的帧数（2.4 秒）。</summary>
        private const int BombFuseTicks = 144;

        /// <summary>背包里有的炸弹（小的在前）。</summary>
        private static List<(int idx, string id, int radius, string name)> BombsInBag()
        {
            var list = new List<(int, string, int, string)>();
            foreach (var b in Bombs)
            {
                int idx = Player.Items.ToList().FindIndex(i => i?.QualifiedItemId == b.id);
                if (idx >= 0)
                    list.Add((idx, b.id, b.radius, b.name));
            }
            return list;
        }

        private static int Chebyshev(Point a, Point b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        /// <summary>能放炸弹的格子：在地图上、能走、上面没有东西。</summary>
        private static bool CanHoldBomb(GameLocation loc, Point p) =>
            loc.isTileOnMap(p.X, p.Y) && Pathfinder.IsPassable(loc, p.X, p.Y) && !loc.Objects.ContainsKey(new Vector2(p.X, p.Y));

        /// <summary>走一格要几帧（和 WalkPath 一样按移动速度算）。</summary>
        private static float TicksPerTile => 64f / Math.Clamp(Player.getMovementSpeed(), 2f, 10f);

        /// <summary>从 from 跑到爆炸范围外（离炸弹横纵都超过 radius 格，再多留 1 格余量）的最短路线；跑不出去返回 null。</summary>
        private static List<Point> EscapePath(GameLocation loc, Point from, Point bomb, int radius)
        {
            var goals = new List<Point>();
            int far = radius + 6;
            for (int x = bomb.X - far; x <= bomb.X + far; x++)
                for (int y = bomb.Y - far; y <= bomb.Y + far; y++)
                {
                    var p = new Point(x, y);
                    if (Chebyshev(p, bomb) >= radius + 2 && loc.isTileOnMap(x, y) && Pathfinder.IsPassable(loc, x, y))
                        goals.Add(p);
                }
            // 别跑到怪跟前去等：离会打人的怪 2 格以内的落脚点不要（全都挨着怪时才不挑）
            var threats = LiveMonsters(loc).Where(m => m.DamageToFarmer > 0 && !CannotHurt(m)).Select(m => m.TilePoint).ToList();
            var safe = goals.Where(g => threats.All(t => Chebyshev(g, t) > 2)).ToList();
            if (safe.Count > 0)
                goals = safe;
            return goals.Count == 0 ? null : Pathfinder.FindPath(loc, from, goals, 20000);
        }

        /// <summary>
        /// 在 bombTile 放一颗 bomb 并跑开、等它炸完。o.Ok=炸了；失败时 o.Message 说原因（没放下去、跑不出范围……），这时不放。
        /// 放之前先算好：站在炸弹旁边能不能在引信烧完前跑出范围（留 30 帧余量），跑不出去就不放。
        /// </summary>
        private IEnumerable<object> PlaceBombAndRun(Point bombTile, (int idx, string id, int radius, string name) bomb, Outcome o)
        {
            var loc = Loc;
            if (!CanHoldBomb(loc, bombTile))
            {
                o.Fail($"({bombTile.X},{bombTile.Y}) 放不了炸弹（不能走或者上面有东西）");
                yield break;
            }
            // 站的位置：炸弹旁边、离出口最近的那格。先算能不能跑掉
            var stands = Pathfinder.AdjacentStandTiles(loc, bombTile);
            var plan = stands.Select(s => (stand: s, escape: EscapePath(loc, s, bombTile, bomb.radius)))
                .Where(t => t.escape != null)
                .OrderBy(t => t.escape.Count)
                .FirstOrDefault();
            if (plan.escape == null || (plan.escape.Count - 1) * TicksPerTile > BombFuseTicks - 30)
            {
                o.Fail($"{bomb.name}放在 ({bombTile.X},{bombTile.Y}) 的话来不及跑出爆炸范围（半径 {bomb.radius} 格，引信约 2.4 秒），没有放");
                yield break;
            }
            foreach (var s in GoAdjacent(bombTile, o, plan.stand))
                yield return s;
            if (!o.Ok)
                yield break;
            if (Player.Items[bomb.idx]?.QualifiedItemId != bomb.id)
            {
                o.Fail($"背包里没有{bomb.name}了");
                yield break;
            }
            int before = Player.Items[bomb.idx].Stack;
            Player.CurrentToolIndex = bomb.idx;
            Face(bombTile);
            bool placed = Utility.tryToPlaceItem(loc, (SObject)Player.Items[bomb.idx], bombTile.X * 64 + 32, bombTile.Y * 64 + 32);
            int after = Player.Items[bomb.idx]?.QualifiedItemId == bomb.id ? Player.Items[bomb.idx].Stack : 0;
            if (!placed || after >= before)
            {
                o.Fail($"{bomb.name}没放下去（({bombTile.X},{bombTile.Y}) 可能被占了）");
                yield break;
            }
            int placedAt = Game1.ticks;
            // 放下了：马上跑。路线从现在站的地方重新算
            var run = EscapePath(loc, Player.TilePoint, bombTile, bomb.radius);
            if (run != null)
            {
                var walk = new Outcome();
                foreach (var s in WalkPath(run, walk))
                    yield return s;
            }
            // 等它炸完（引信 + 一点余量）；时间停着（开了界面）时引信也停，所以按“时间在走的帧”数。
            // 等的时候怪贴上来就挥武器打它（原地挥，不会走回爆炸范围里）
            int weapon = BestWeapon();

            for (int waited = 0, t = 0; waited < BombFuseTicks + 30 && t < 1200; t++)
            {
                if (weapon >= 0 && Game1.shouldTimePass() && NearestThreat(1) is Monster near)
                {
                    int t0 = Game1.ticks;
                    foreach (var s in SwingAt(weapon, near, DirToward(Player.StandingPixel, near.GetBoundingBox().Center)))
                        yield return s;
                    int spent = Math.Max(1, Game1.ticks - t0);
                    waited += spent;
                    t += spent;
                    continue;
                }
                if (Game1.shouldTimePass())
                    waited++;
                yield return null;
            }
            o.Succeed($"{bomb.name}在 ({bombTile.X},{bombTile.Y}) 炸了" + (Game1.ticks - placedAt > 0 && Chebyshev(Player.TilePoint, bombTile) <= bomb.radius ? "（没完全跑出范围，可能挨了一下）" : ""));
        }

        /// <summary>
        /// 炸倒在地上的木乃伊：挑一颗能把附近倒地木乃伊尽量都罩进去的炸弹和位置（小炸弹优先，省着用），放下跑开。
        /// 木乃伊快爬起来了（不到 4 秒）的不算，来不及。返回炸死了几只；没炸弹、没合适的位置返回 0（note 说明原因）。
        /// </summary>
        private IEnumerable<object> BombDownedMummies(int radius, FightStats st, List<string> notes)
        {
            var bombs = BombsInBag();
            if (bombs.Count == 0)
                yield break;
            var me = Player.TilePoint;
            var downed = Loc.characters.OfType<Mummy>()
                .Where(m => m.reviveTimer.Value > 4000 && Util.Manhattan(m.TilePoint, me) <= radius)
                .ToList();
            if (downed.Count == 0)
                yield break;

            // 每种炸弹、每个候选格：罩住几只倒地的木乃伊；先比罩住的数量，再用小炸弹，再离人近
            var best = (tile: Point.Zero, bomb: bombs[0], hits: 0, dist: int.MaxValue);
            foreach (var bomb in bombs)
            {
                var candidates = downed.SelectMany(m => Enumerable.Range(-bomb.radius, bomb.radius * 2 + 1)
                        .SelectMany(dx => Enumerable.Range(-bomb.radius, bomb.radius * 2 + 1).Select(dy => new Point(m.TilePoint.X + dx, m.TilePoint.Y + dy))))
                    .Distinct()
                    .Where(p => CanHoldBomb(Loc, p) && Chebyshev(p, me) <= 12);
                foreach (var p in candidates)
                {
                    int hits = downed.Count(m => Chebyshev(m.TilePoint, p) <= bomb.radius);
                    int dist = Util.Manhattan(p, me);
                    bool better = hits > best.hits || (hits == best.hits && bomb.radius < best.bomb.radius) || (hits == best.hits && bomb.radius == best.bomb.radius && dist < best.dist);
                    if (better)
                        best = (p, bomb, hits, dist);
                }
                if (best.hits == downed.Count)
                    break; // 小炸弹已经能全罩住，不用更大的
            }
            if (best.hits == 0)
                yield break;

            var o = new Outcome();
            foreach (var s in PlaceBombAndRun(best.tile, best.bomb, o))
                yield return s;
            if (!o.Ok)
            {
                notes.Add(o.Message);
                yield break;
            }
            int killed = downed.Count(m => m.Health <= 0 || !Loc.characters.Contains(m));
            st.Kills += killed;
            st.MummiesBombed += killed;
            st.BombsUsed++;
        }

        private static readonly Dictionary<int, HashSet<Point>> BlastShapes = new();

        /// <summary>
        /// 爆炸能炸掉东西的格子（相对炸弹的偏移）。伤害怪和人是正方形，但炸石头是个圆：照抄 GameLocation.explode 里
        /// 用 Game1.getCircleOutlineGrid 填圆的那段循环（i 是横向、j 是纵向，num 在每列开头按边框重置）。以前按正方形算，四个角上的石头其实炸不到。
        /// </summary>
        private static HashSet<Point> BlastShape(int radius)
        {
            if (BlastShapes.TryGetValue(radius, out var cached))
                return cached;
            var grid = Game1.getCircleOutlineGrid(radius);
            var set = new HashSet<Point>();
            int num = 0, size = radius * 2;
            for (int i = 0; i <= size; i++)
                for (int j = 0; j <= size; j++)
                {
                    bool hit = false;
                    if (i == 0 || j == 0 || i == size || j == size)
                        num = grid[i, j] ? 1 : 0;
                    else if (grid[i, j])
                    {
                        num += j <= radius ? 1 : -1;
                        if (num <= 0)
                            hit = true;
                    }
                    if (num >= 1)
                        hit = true;
                    if (hit)
                        set.Add(new Point(i - radius, j - radius));
                }
            BlastShapes[radius] = set;
            return set;
        }

        /// <summary>一个炸石头的位置：用哪颗炸弹、罩住几个矿石/宝石节点、一共几块石头。</summary>
        private struct BlastSpot
        {
            public Point Tile;
            public (int idx, string id, int radius, string name) Bomb;
            public int Ores, Stones;
            public bool Found => Stones > 0;
        }

        /// <summary>
        /// 身边 range 格内炸石头最划算的位置：矿石、宝石、晶球节点一个顶 4 块普通石头；大炸弹贵（樱桃炸弹 4 铜矿 + 1 煤，
        /// 炸弹 4 铁矿 + 1 煤，超级炸弹 4 金矿 + 太阳精华 + 虚空精华），要比小炸弹多罩住不少才用大的。skip 里的格子放过没成，不再挑。
        /// </summary>
        private static BlastSpot BestBlastSpot(GameLocation loc, List<(int idx, string id, int radius, string name)> bombs, int range, ICollection<Point> skip = null)
        {
            var me = Player.TilePoint;
            var nodes = loc.objects.Pairs.Where(p => p.Value.IsBreakableStone())
                .Select(p => (pt: new Point((int)p.Key.X, (int)p.Key.Y), ore: ValuableNodeIds.Contains(p.Value.ItemId)))
                .Where(n => Chebyshev(n.pt, me) <= range + 7)
                .ToList();
            var best = new BlastSpot();
            double bestScore = 0;
            if (nodes.Count == 0)
                return best;
            foreach (var bomb in bombs)
            {
                double cost = bomb.radius switch { 3 => 0, 5 => 6, _ => 14 };
                for (int dx = -range; dx <= range; dx++)
                    for (int dy = -range; dy <= range; dy++)
                    {
                        var p = new Point(me.X + dx, me.Y + dy);
                        if (skip?.Contains(p) == true || !CanHoldBomb(loc, p))
                            continue;
                        int ores = 0, stones = 0;
                        var shape = BlastShape(bomb.radius);
                        foreach (var n in nodes)
                            if (shape.Contains(new Point(n.pt.X - p.X, n.pt.Y - p.Y)))
                            {
                                stones++;
                                if (n.ore)
                                    ores++;
                            }
                        double score = ores * 4 + stones - cost - Util.Manhattan(p, me) * 0.05;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new BlastSpot { Tile = p, Bomb = bomb, Ores = ores, Stones = stones };
                        }
                    }
            }
            return best;
        }

        /// <summary>
        /// 在 spot 炸一颗，等它炸完，把炸出来的矿石、宝石、煤捡回来。o.Ok=炸了；blasted/ores 是炸掉的石头和其中的值钱节点。
        /// </summary>
        private IEnumerable<object> BlastStones(BlastSpot spot, Outcome o, List<string> notes, Action<int, int> done)
        {
            var loc = Loc;
            var before = loc.objects.Pairs.Where(p => p.Value.IsBreakableStone())
                .ToDictionary(p => new Point((int)p.Key.X, (int)p.Key.Y), p => ValuableNodeIds.Contains(p.Value.ItemId));
            foreach (var s in PlaceBombAndRun(spot.Tile, spot.Bomb, o))
                yield return s;
            if (!o.Ok)
                yield break;
            var gone = before.Where(kv => !loc.objects.TryGetValue(new Vector2(kv.Key.X, kv.Key.Y), out var obj) || !obj.IsBreakableStone()).ToList();
            done(gone.Count, gone.Count(kv => kv.Value));
            // 炸出来的东西散在爆炸范围里，走过去捡（背包满了会在 notes 里说）
            int r = spot.Bomb.radius + 1;
            var keep = o.Message;
            foreach (var s in CollectDebris(new Rectangle(spot.Tile.X - r, spot.Tile.Y - r, r * 2 + 1, r * 2 + 1), notes, 30))
                yield return s;
            o.Succeed(keep);
        }

        /// <summary>
        /// use_bomb：在指定的格子（或者自动挑）放一颗炸弹，跑开等它炸完。
        /// 自动挑：附近有倒地的木乃伊就炸它们，没有就找身边 8 格内矿石最多（其次石头最多）的地方，炸完把掉的东西捡回来。
        /// </summary>
        public IEnumerable<object> UseBomb(int? x, int? y, string kind)
        {
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var bombs = BombsInBag();
            if (bombs.Count == 0)
            {
                yield return ActionResult.Fail("背包里没有炸弹（樱桃炸弹、炸弹、超级炸弹）。采矿等级够了可以 craft，矮人和沙漠商人也卖");
                yield break;
            }
            kind = kind?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(kind) && kind != "auto")
            {
                var want = kind switch
                {
                    "cherry" or "樱桃" or "樱桃炸弹" or "small" => "(O)286",
                    "bomb" or "炸弹" or "normal" => "(O)287",
                    "mega" or "超级" or "超级炸弹" or "big" => "(O)288",
                    _ => null
                };
                if (want == null)
                {
                    yield return ActionResult.Fail($"不认识炸弹种类“{kind}”，可以写 cherry（樱桃炸弹，半径 3）、bomb（炸弹，半径 5）、mega（超级炸弹，半径 7），或者不传自动挑");
                    yield break;
                }
                bombs = bombs.Where(b => b.id == want).ToList();
                if (bombs.Count == 0)
                {
                    yield return ActionResult.Fail("背包里没有这种炸弹。有的是：" + string.Join("、", BombsInBag().Select(b => b.name)));
                    yield break;
                }
            }

            var st = new FightStats();
            var notes = new List<string>();
            var o = new Outcome();
            int blasted = 0, ores = 0;
            if (x.HasValue && y.HasValue)
            {
                foreach (var s in BlastStones(new BlastSpot { Tile = new Point(x.Value, y.Value), Bomb = bombs[0], Stones = 1 }, o, notes, (b, n) => (blasted, ores) = (b, n)))
                    yield return s;
                yield return o.Ok
                    ? ActionResult.Success(BlastSummary(o.Message, blasted, ores, notes))
                    : ActionResult.Fail(o.Message);
                yield break;
            }

            // 没给坐标：先看倒地的木乃伊
            if (Loc.characters.OfType<Mummy>().Any(m => m.reviveTimer.Value > 4000 && Util.Manhattan(m.TilePoint, Player.TilePoint) <= 12))
            {
                foreach (var s in BombDownedMummies(12, st, notes))
                    yield return s;
                if (st.BombsUsed > 0)
                {
                    yield return ActionResult.Success($"炸死了 {st.MummiesBombed} 只倒地的木乃伊。生命 {Player.health}/{Player.maxHealth}");
                    yield break;
                }
                if (notes.Count > 0)
                {
                    yield return ActionResult.Fail(string.Join("；", notes));
                    yield break;
                }
            }

            // 再看石头：身边 8 格内，矿石/宝石节点罩住得最多（其次石头最多）的位置；指定了炸弹种类就只用那种
            var spot = BestBlastSpot(Loc, bombs, 8);
            if (!spot.Found || (spot.Ores < 2 && spot.Stones < 3))
            {
                yield return ActionResult.Fail("附近没有倒地的木乃伊，矿石和石头也不多（一颗炸弹罩不住 2 个矿或 3 块石头），不值得炸。指定坐标就照放：use_bomb 传 x、y");
                yield break;
            }
            foreach (var s in BlastStones(spot, o, notes, (b, n) => (blasted, ores) = (b, n)))
                yield return s;
            yield return o.Ok
                ? ActionResult.Success(BlastSummary(o.Message, blasted, ores, notes))
                : ActionResult.Fail(o.Message);
        }

        private static string BlastSummary(string what, int blasted, int ores, List<string> notes) =>
            $"{what}，炸掉 {blasted} 块石头{(ores > 0 ? $"（其中矿石/宝石节点 {ores} 个）" : "")}，掉出来的都捡了。生命 {Player.health}/{Player.maxHealth}"
            + (notes.Count > 0 ? "。" + string.Join("；", notes.Distinct()) : "")
            + (Loc is StardewValley.Locations.MineShaft m && NearestMineTile(m, 173).HasValue ? "。楼梯露出来了" : "");
    }
}
