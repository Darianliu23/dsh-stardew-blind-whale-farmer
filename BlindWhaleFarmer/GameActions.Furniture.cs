using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 搬家具（列表 / 拿起 / 放下 / 移动 / 旋转） ─────────────────────────
        // 严格遵守游戏规则：BedFurniture.CanModifyBed（别人屋里的床只有屋主/配偶能动）、
        // Furniture.canBeRemoved、Furniture.canBePlacedHere、GetAdditionalFurniturePlacementStatus、
        // Utility.playerCanPlaceItemHere / Utility.tryToPlaceItem，不绕过任何检查。

        internal static string NormalizeFurnitureAction(string raw)
        {
            string key = (raw ?? "").Trim().ToLowerInvariant();
            return key switch
            {
                "" or "list" or "status" or "列表" or "查看" => "list",
                "pickup" or "pick" or "take" or "remove" or "拿起" or "收起" => "pickup",
                "place" or "put" or "放下" or "摆" or "摆放" => "place",
                "move" or "移动" or "搬" or "搬动" => "move",
                "rotate" or "turn" or "旋转" or "转向" => "rotate",
                _ => null,
            };
        }

        internal static string DescribeFurniturePlacementStatus(int status) => status switch
        {
            0 => null,
            1 => "这是墙面家具（画/窗户/壁饰等），必须摆在墙面上",
            2 => "目标位置被地图标为不可摆家具（NoFurniture）",
            3 => "地面家具不能摆在墙面上",
            4 => "当前地点不可装饰，不能摆这件家具",
            -1 => "不满足家具摆放条件（例如床旁需要留出下床空位，或挡到了建筑层）",
            _ => $"游戏拒绝在此摆放（状态码 {status}）",
        };

        public IEnumerable<object> ManageFurniture(string action, string name, int? x, int? y, int? toX, int? toY, int? times)
        {
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            string act = NormalizeFurnitureAction(action);
            if (act == null)
            {
                yield return ActionResult.Fail($"不认识的 action「{action}」，请传 list（列表）、pickup（拿起）、place（放下）、move（移动）或 rotate（旋转）");
                yield break;
            }

            switch (act)
            {
                case "list":
                    yield return ActionResult.Success(DescribeFurnitureHere());
                    yield break;

                case "pickup":
                {
                    foreach (var s in PickupFurnitureAction(name, x, y))
                        yield return s;
                    yield break;
                }

                case "place":
                {
                    foreach (var s in PlaceFurnitureAction(name, x, y, times ?? 0))
                        yield return s;
                    yield break;
                }

                case "move":
                {
                    foreach (var s in MoveFurnitureAction(name, x, y, toX, toY, times ?? 0))
                        yield return s;
                    yield break;
                }

                case "rotate":
                {
                    foreach (var s in RotateFurnitureAction(name, x, y, times ?? 1))
                        yield return s;
                    yield break;
                }
            }
        }

        private static string DescribeFurnitureHere()
        {
            var sb = new StringBuilder();
            var list = Loc.furniture;
            sb.Append($"当前地点 {Loc.NameOrUniqueName} 共有 {list.Count} 件家具");
            if (list.Count > 0)
            {
                sb.AppendLine("：");
                for (int i = 0; i < list.Count; i++)
                {
                    var f = list[i];
                    int fx = (int)f.TileLocation.X, fy = (int)f.TileLocation.Y;
                    int w = f.getTilesWide(), h = f.getTilesHigh();
                    var tags = new List<string>();
                    if (!f.isGroundFurniture())
                        tags.Add("墙面");
                    if (f.rotations.Value >= 2)
                        tags.Add($"朝向 {f.currentRotation.Value}/{f.rotations.Value}");
                    if (f is BedFurniture bed)
                    {
                        tags.Add(bed.bedType switch
                        {
                            BedFurniture.BedType.Double => "双人床",
                            BedFurniture.BedType.Child => "儿童床",
                            _ => "单人床",
                        });
                        if (!CanModifyBedSafely(bed))
                            tags.Add("别人屋里的床·不可动");
                        else if (bed.IsBeingSleptIn())
                            tags.Add("有人在睡");
                    }
                    else if (!f.canBeRemoved(Player))
                    {
                        tags.Add("当前不可拿起");
                    }
                    if (f.heldObject.Value != null)
                        tags.Add($"放着 {f.heldObject.Value.DisplayName}");
                    string tagStr = tags.Count > 0 ? " [" + string.Join("，", tags) + "]" : "";
                    sb.AppendLine($"  #{i + 1} {f.DisplayName} ({fx},{fy}) {w}×{h}{tagStr}");
                }
            }
            else
            {
                sb.AppendLine("。");
            }

            var invFurn = new List<string>();
            for (int i = 0; i < Player.Items.Count; i++)
            {
                if (Player.Items[i] is Furniture f)
                {
                    string rot = f.rotations.Value >= 2 ? $"，可旋转({f.rotations.Value}向)" : "";
                    string wall = !f.isGroundFurniture() ? "，墙面" : "";
                    invFurn.Add($"格{i + 1} {f.DisplayName}（{f.getTilesWide()}×{f.getTilesHigh()}{wall}{rot}）");
                }
            }
            if (invFurn.Count > 0)
                sb.Append("背包里的家具：" + string.Join("；", invFurn));
            else
                sb.Append("背包里没有家具。");
            return sb.ToString().TrimEnd();
        }

        private static Furniture FindFurnitureOnFloor(string name, int? x, int? y, out string error)
        {
            error = null;
            if (Loc.furniture.Count == 0)
            {
                error = $"当前地点 {Loc.NameOrUniqueName} 里没有家具";
                return null;
            }
            IEnumerable<Furniture> pool = Loc.furniture;
            if (x.HasValue && y.HasValue)
            {
                int px = x.Value * 64 + 32, py = y.Value * 64 + 32;
                var atTile = pool.Where(f => f.GetBoundingBox().Contains(px, py)
                    || ((int)f.TileLocation.X == x.Value && (int)f.TileLocation.Y == y.Value)).ToList();
                if (atTile.Count == 0 && Loc is DecoratableLocation dec)
                {
                    int wallTop = dec.GetWallTopY(x.Value, y.Value);
                    if (wallTop != -1)
                    {
                        int wy = wallTop * 64 + 32;
                        atTile = pool.Where(f => !f.isGroundFurniture() && f.GetBoundingBox().Contains(px, wy)).ToList();
                    }
                }
                if (atTile.Count == 0)
                {
                    error = $"在 ({x.Value},{y.Value}) 找不到家具（先用 furniture action=list 查看坐标）";
                    return null;
                }
                pool = atTile;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                string q = name.Trim();
                var exact = pool.Where(f => string.Equals(f.DisplayName, q, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, q, StringComparison.OrdinalIgnoreCase)).ToList();
                var fuzzy = exact.Count > 0 ? exact : pool.Where(f =>
                    (f.DisplayName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (f.Name?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0).ToList();
                if (fuzzy.Count == 0)
                {
                    error = x.HasValue && y.HasValue
                        ? $"在 ({x.Value},{y.Value}) 找不到叫「{name}」的家具"
                        : $"当前地图找不到叫「{name}」的家具（用 furniture action=list 看列表）";
                    return null;
                }
                pool = fuzzy;
            }
            else if (!x.HasValue || !y.HasValue)
            {
                error = "请提供家具的坐标 (x, y) 或名字 name（先用 furniture action=list 查看）";
                return null;
            }
            // 同坐标有地毯+桌椅时优先选非可通行家具，其次按距离玩家最近
            return pool.OrderBy(f => f.isPassable() ? 1 : 0)
                .ThenBy(f => Util.Manhattan(Player.TilePoint, new Point((int)f.TileLocation.X, (int)f.TileLocation.Y)))
                .First();
        }

        private static int FindFurnitureInInventory(string name, out Furniture found, out string error)
        {
            found = null;
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "请提供要摆放的家具名 name（用 furniture action=list 查看背包里的家具）";
                return -1;
            }
            string q = name.Trim();
            int fuzzyIdx = -1;
            for (int i = 0; i < Player.Items.Count; i++)
            {
                if (Player.Items[i] is not Furniture f)
                    continue;
                if (string.Equals(f.DisplayName, q, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, q, StringComparison.OrdinalIgnoreCase))
                {
                    found = f;
                    return i;
                }
                if (fuzzyIdx < 0 && ((f.DisplayName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (f.Name?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0))
                {
                    fuzzyIdx = i;
                }
            }
            if (fuzzyIdx >= 0)
            {
                found = (Furniture)Player.Items[fuzzyIdx];
                return fuzzyIdx;
            }
            error = $"背包里没有叫「{name}」的家具";
            return -1;
        }

        private static bool CanModifyBedSafely(BedFurniture bed)
        {
            if (Loc is FarmHouse fh && (fh.owner == null || fh.owner.team == null))
                return fh.owner == Player;
            return bed.CanModifyBed(Player);
        }

        private static bool CheckCanRemoveFurniture(Furniture f, out string reason)
        {
            reason = null;
            if (f == null || f.Location == null)
            {
                reason = "这件家具不在当前地图上";
                return false;
            }
            if (f is BedFurniture bed)
            {
                if (!CanModifyBedSafely(bed))
                {
                    reason = "不能动别人屋里的床（只有屋主或屋主的配偶才能搬动床）";
                    return false;
                }
                if (bed.IsBeingSleptIn())
                {
                    reason = "这张床正在被人睡着或被占用，现在不能搬";
                    return false;
                }
            }
            if (!f.AllowLocalRemoval)
            {
                reason = $"游戏规则不允许移除 {f.DisplayName}";
                return false;
            }
            if (f.HasSittingFarmers())
            {
                reason = $"有人正坐在 {f.DisplayName} 上，不能搬动";
                return false;
            }
            if (f.heldObject.Value != null)
            {
                reason = $"{f.DisplayName} 上面放着「{f.heldObject.Value.DisplayName}」，请先拿走上面的东西再搬";
                return false;
            }
            if (!f.canBeRemoved(Player))
            {
                reason = f.isPassable()
                    ? $"{f.DisplayName} 上面压着其他家具或物品，请先移开上面的东西再搬"
                    : $"游戏规则不允许拿起 {f.DisplayName}";
                return false;
            }
            return true;
        }

        private static bool CheckCanPlaceFurniture(Furniture f, int tx, int ty, out string reason)
        {
            reason = null;
            if (f is BedFurniture bed)
            {
                if (!CanModifyBedSafely(bed))
                {
                    reason = "不能在别人屋里摆放床（只有屋主或屋主的配偶才能改动床）";
                    return false;
                }
                if (Loc is FarmHouse fh && ((bed.bedType == BedFurniture.BedType.Child && fh.upgradeLevel < 2)
                    || (bed.bedType == BedFurniture.BedType.Double && fh.upgradeLevel < 1)))
                {
                    reason = $"这栋房子等级不够，不能摆 {f.DisplayName}（双人床需要升级 1 次，儿童床需要升级 2 次）";
                    return false;
                }
            }
            if (!Loc.CanPlaceThisFurnitureHere(f))
            {
                reason = $"当前地点 {Loc.NameOrUniqueName} 不允许摆放 {f.DisplayName}";
                return false;
            }
            var savedTile = f.TileLocation;
            int status = f.GetAdditionalFurniturePlacementStatus(Loc, tx * 64, ty * 64, Player);
            f.TileLocation = savedTile;
            if (status != 0)
            {
                reason = DescribeFurniturePlacementStatus(status);
                return false;
            }
            if (!f.canBePlacedHere(Loc, new Vector2(tx, ty)))
            {
                reason = $"({tx},{ty}) 放不下 {f.DisplayName}（空间不够、有障碍物/其他家具、人物挡着，或不符合墙面/地面规则）";
                return false;
            }
            if (!Utility.playerCanPlaceItemHere(Loc, f, tx * 64, ty * 64, Player))
            {
                reason = !Loc.CanFreePlaceFurniture() && !f.IsCloseEnoughToFarmer(Player, tx, ty)
                    ? $"离 ({tx},{ty}) 太远，走近一点再摆"
                    : $"游戏规则不允许在 ({tx},{ty}) 摆放 {f.DisplayName}";
                return false;
            }
            return true;
        }

        /// <summary>走到指定矩形范围旁边的一格（避免人物站在要摆放的位置上挡住非地毯家具，同时满足室外近距离要求）。</summary>
        private IEnumerable<object> WalkNearRect(int rx, int ry, int rw, int rh, bool avoidInside, Outcome o)
        {
            bool inside = Player.TilePoint.X >= rx && Player.TilePoint.X < rx + rw
                && Player.TilePoint.Y >= ry && Player.TilePoint.Y < ry + rh;
            bool closeEnough = Player.TilePoint.X >= rx - 1 && Player.TilePoint.X <= rx + rw
                && Player.TilePoint.Y >= ry - 1 && Player.TilePoint.Y <= ry + rh;
            if (closeEnough && (!avoidInside || !inside))
            {
                o.Succeed();
                yield break;
            }
            var goals = new List<Point>();
            for (int x = rx - 1; x <= rx + rw; x++)
            {
                for (int y = ry - 1; y <= ry + rh; y++)
                {
                    bool inRect = x >= rx && x < rx + rw && y >= ry && y < ry + rh;
                    if (avoidInside && inRect)
                        continue;
                    if (!Loc.isTileOnMap(x, y) || !Pathfinder.IsPassable(Loc, x, y))
                        continue;
                    goals.Add(new Point(x, y));
                }
            }
            if (goals.Count == 0)
            {
                o.Succeed();
                yield break;
            }
            var path = Pathfinder.FindPath(Loc, Player.TilePoint, goals, 6000);
            if (path == null)
            {
                o.Fail($"走不到 ({rx},{ry}) 附近");
                yield break;
            }
            foreach (var s in WalkPath(path, o))
                yield return s;
        }

        private IEnumerable<object> TryRemoveFurniture(Furniture f, Outcome o)
        {
            if (!Player.couldInventoryAcceptThisItem(f))
            {
                o.Fail("背包满了，腾出一个空位再拿家具");
                yield break;
            }
            if (!CheckCanRemoveFurniture(f, out string reason))
            {
                o.Fail(reason);
                yield break;
            }
            // 走到附近再操作（室外必须走近，室内也更自然）
            if (!Loc.CanFreePlaceFurniture() || !f.IsCloseEnoughToFarmer(Player))
            {
                var walk = new Outcome();
                foreach (var s in WalkNearRect((int)f.TileLocation.X, (int)f.TileLocation.Y, f.getTilesWide(), f.getTilesHigh(), avoidInside: false, walk))
                    yield return s;
                if (!walk.Ok && !Loc.CanFreePlaceFurniture())
                {
                    o.Fail(walk.Message);
                    yield break;
                }
            }
            if (!CheckCanRemoveFurniture(f, out reason))
            {
                o.Fail(reason);
                yield break;
            }

            bool done = false;
            bool removed = false;
            f.AttemptRemoval(removedFurn =>
            {
                if (removedFurn == null)
                    return;
                Guid guid = Loc.furniture.GuidOf(removedFurn);
                if (guid == Guid.Empty)
                    return;
                removedFurn.performRemoveAction();
                Loc.furniture.Remove(guid);
                Player.addItemToInventory(removedFurn);
                Loc.localSound("coin");
                removed = true;
                done = true;
            });

            // BedFurniture 的 AttemptRemoval 需要异步向主机申请 NetMutex 锁，最多等 2 秒
            for (int i = 0; i < 120 && !done && Loc.furniture.Contains(f); i++)
                yield return null;

            if (!removed && Loc.furniture.Contains(f))
            {
                o.Fail(f is BedFurniture
                    ? "没能获取床的锁（可能正在被其他玩家使用或搬动），稍后再试"
                    : $"没能拿起 {f.DisplayName}");
                yield break;
            }
            o.Succeed();
        }

        private static void ApplyRotations(Furniture f, int times)
        {
            if (f == null || f.rotations.Value < 2)
                return;
            int n = ((times % 4) + 4) % 4;
            for (int i = 0; i < n; i++)
                f.rotate();
        }

        private static void RestoreRotation(Furniture f, int targetRotation)
        {
            if (f == null || f.rotations.Value < 2)
                return;
            for (int i = 0; i < 4 && f.currentRotation.Value != targetRotation; i++)
                f.rotate();
        }

        private static int IndexOfFurnitureInstance(Furniture f)
        {
            for (int i = 0; i < Player.Items.Count; i++)
                if (ReferenceEquals(Player.Items[i], f))
                    return i;
            return -1;
        }

        private IEnumerable<object> TryPlaceHeldFurniture(Furniture f, int tx, int ty, Outcome o)
        {
            int slot = IndexOfFurnitureInstance(f);
            if (slot < 0)
            {
                o.Fail($"背包里找不到 {f.DisplayName}");
                yield break;
            }
            var walk = new Outcome();
            foreach (var s in WalkNearRect(tx, ty, f.getTilesWide(), f.getTilesHigh(), avoidInside: !f.isPassable(), walk))
                yield return s;
            if (!walk.Ok && !Loc.CanFreePlaceFurniture())
            {
                o.Fail(walk.Message);
                yield break;
            }

            if (!CheckCanPlaceFurniture(f, tx, ty, out string reason))
            {
                o.Fail(reason);
                yield break;
            }

            Player.CurrentToolIndex = slot;
            if (!Utility.tryToPlaceItem(Loc, f, tx * 64, ty * 64))
            {
                o.Fail($"在 ({tx},{ty}) 摆放 {f.DisplayName} 失败（未通过游戏放置检查）");
                yield break;
            }
            o.Succeed();
        }

        private IEnumerable<object> PickupFurnitureAction(string name, int? x, int? y)
        {
            var f = FindFurnitureOnFloor(name, x, y, out string err);
            if (f == null)
            {
                yield return ActionResult.Fail(err);
                yield break;
            }
            var orig = new Point((int)f.TileLocation.X, (int)f.TileLocation.Y);
            var o = new Outcome();
            foreach (var s in TryRemoveFurniture(f, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            yield return ActionResult.Success($"拿起了 {f.DisplayName}（原位置 ({orig.X},{orig.Y})），已放进背包");
        }

        private IEnumerable<object> PlaceFurnitureAction(string name, int? x, int? y, int rotateTimes)
        {
            if (!x.HasValue || !y.HasValue)
            {
                yield return ActionResult.Fail("放下家具必须指定目标坐标 x 和 y");
                yield break;
            }
            int slot = FindFurnitureInInventory(name, out Furniture f, out string err);
            if (slot < 0)
            {
                yield return ActionResult.Fail(err);
                yield break;
            }
            int origRot = f.currentRotation.Value;
            if (rotateTimes > 0)
                ApplyRotations(f, rotateTimes);

            var o = new Outcome();
            foreach (var s in TryPlaceHeldFurniture(f, x.Value, y.Value, o))
                yield return s;
            if (!o.Ok)
            {
                RestoreRotation(f, origRot);
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            yield return ActionResult.Success($"把 {f.DisplayName} 摆到了 ({(int)f.TileLocation.X},{(int)f.TileLocation.Y})");
        }

        private IEnumerable<object> MoveFurnitureAction(string name, int? x, int? y, int? toX, int? toY, int rotateTimes)
        {
            if (!toX.HasValue || !toY.HasValue)
            {
                yield return ActionResult.Fail("移动家具必须指定目标坐标 to_x 和 to_y");
                yield break;
            }
            var f = FindFurnitureOnFloor(name, x, y, out string err);
            if (f == null)
            {
                yield return ActionResult.Fail(err);
                yield break;
            }
            var origTile = new Point((int)f.TileLocation.X, (int)f.TileLocation.Y);
            int origRot = f.currentRotation.Value;

            var rm = new Outcome();
            foreach (var s in TryRemoveFurniture(f, rm))
                yield return s;
            if (!rm.Ok)
            {
                yield return ActionResult.Fail(rm.Message);
                yield break;
            }

            if (rotateTimes > 0)
                ApplyRotations(f, rotateTimes);

            var pl = new Outcome();
            foreach (var s in TryPlaceHeldFurniture(f, toX.Value, toY.Value, pl))
                yield return s;
            if (pl.Ok)
            {
                yield return ActionResult.Success($"把 {f.DisplayName} 从 ({origTile.X},{origTile.Y}) 移到了 ({(int)f.TileLocation.X},{(int)f.TileLocation.Y})");
                yield break;
            }

            // 放到新位置失败：恢复原朝向并放回原位
            RestoreRotation(f, origRot);
            var back = new Outcome();
            foreach (var s in TryPlaceHeldFurniture(f, origTile.X, origTile.Y, back))
                yield return s;
            string recover = back.Ok ? "，已放回原位" : "（未能自动放回原位，家具目前安全留在你的背包里）";
            yield return ActionResult.Fail($"没能把 {f.DisplayName} 移到 ({toX.Value},{toY.Value})：{pl.Message}{recover}");
        }

        private IEnumerable<object> RotateFurnitureAction(string name, int? x, int? y, int times)
        {
            int n = Math.Clamp(times, 1, 3);
            // 优先匹配地上家具；若没传坐标且地上找不到，再看背包里的家具
            var f = FindFurnitureOnFloor(name, x, y, out string floorErr);
            if (f == null)
            {
                if (!x.HasValue && !y.HasValue && FindFurnitureInInventory(name, out Furniture invF, out _) >= 0)
                {
                    if (invF.rotations.Value < 2)
                    {
                        yield return ActionResult.Fail($"{invF.DisplayName} 不支持旋转（只有 1 个朝向）");
                        yield break;
                    }
                    ApplyRotations(invF, n);
                    yield return ActionResult.Success($"已旋转背包里的 {invF.DisplayName}（当前朝向 {invF.currentRotation.Value}/{invF.rotations.Value}）");
                    yield break;
                }
                yield return ActionResult.Fail(floorErr);
                yield break;
            }

            if (f.rotations.Value < 2)
            {
                yield return ActionResult.Fail($"{f.DisplayName} 不支持旋转（只有 1 个朝向）");
                yield break;
            }

            var origTile = new Point((int)f.TileLocation.X, (int)f.TileLocation.Y);
            int origRot = f.currentRotation.Value;

            var rm = new Outcome();
            foreach (var s in TryRemoveFurniture(f, rm))
                yield return s;
            if (!rm.Ok)
            {
                yield return ActionResult.Fail(rm.Message);
                yield break;
            }

            ApplyRotations(f, n);
            var pl = new Outcome();
            foreach (var s in TryPlaceHeldFurniture(f, origTile.X, origTile.Y, pl))
                yield return s;
            if (pl.Ok)
            {
                yield return ActionResult.Success($"旋转了 ({origTile.X},{origTile.Y}) 的 {f.DisplayName}（朝向 {origRot} → {f.currentRotation.Value}）");
                yield break;
            }

            RestoreRotation(f, origRot);
            var back = new Outcome();
            foreach (var s in TryPlaceHeldFurniture(f, origTile.X, origTile.Y, back))
                yield return s;
            string recover = back.Ok ? "，已恢复原朝向放回原位" : "（未能自动放回原位，家具目前留在你的背包里）";
            yield return ActionResult.Fail($"({origTile.X},{origTile.Y}) 的 {f.DisplayName} 旋转后放不下：{pl.Message}{recover}");
        }
    }
}
