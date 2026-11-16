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
            // 等它炸完（引信 + 一点余量）；时间停着（开了界面）时引信也停，所以按“时间在走的帧”数
            for (int waited = 0, t = 0; waited < BombFuseTicks + 30 && t < 1200; t++)
            {
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

        /// <summary>
        /// use_bomb：在指定的格子（或者自动挑）放一颗炸弹，跑开等它炸完。
        /// 自动挑：附近有倒地的木乃伊就炸它们，没有就找身边 8 格内石头最多的地方。
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
            if (x.HasValue && y.HasValue)
            {
                var bomb = bombs[0];
                var stonesBefore = Loc.objects.Pairs.Count(p => p.Value.IsBreakableStone());
                foreach (var s in PlaceBombAndRun(new Point(x.Value, y.Value), bomb, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
                int stones = stonesBefore - Loc.objects.Pairs.Count(p => p.Value.IsBreakableStone());
                yield return ActionResult.Success($"{o.Message}，炸掉 {stones} 块石头。生命 {Player.health}/{Player.maxHealth}。掉出来的矿石宝石记得捡（走过去就会捡起来）");
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

            // 再看石头：身边 8 格内，哪个能放炸弹的格子罩住的石头最多
            var me = Player.TilePoint;
            var smallest = bombs[0];
            var stonesNear = Loc.objects.Pairs.Where(p => p.Value.IsBreakableStone() && Chebyshev(new Point((int)p.Key.X, (int)p.Key.Y), me) <= 8)
                .Select(p => new Point((int)p.Key.X, (int)p.Key.Y)).ToList();
            var spot = Enumerable.Range(-8, 17).SelectMany(dx => Enumerable.Range(-8, 17).Select(dy => new Point(me.X + dx, me.Y + dy)))
                .Where(p => CanHoldBomb(Loc, p))
                .Select(p => (p, n: stonesNear.Count(s => Chebyshev(s, p) <= smallest.radius)))
                .OrderByDescending(t => t.n).ThenBy(t => Util.Manhattan(t.p, me))
                .FirstOrDefault();
            if (spot.n < 3)
            {
                yield return ActionResult.Fail("附近没有倒地的木乃伊，石头也不多（一颗炸弹罩不住 3 块以上），不值得炸。指定坐标就照放：use_bomb 传 x、y");
                yield break;
            }
            int before = Loc.objects.Pairs.Count(p => p.Value.IsBreakableStone());
            foreach (var s in PlaceBombAndRun(spot.p, smallest, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            int blasted = before - Loc.objects.Pairs.Count(p => p.Value.IsBreakableStone());
            yield return ActionResult.Success($"{o.Message}，炸掉 {blasted} 块石头。生命 {Player.health}/{Player.maxHealth}。掉出来的矿石宝石记得捡（走过去就会捡起来）");
        }
    }
}
