using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.SpecialOrders;
using StardewValley.SpecialOrders.Objectives;
using StardewValley.SpecialOrders.Rewards;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    /// <summary>特别订单：镇上刘易斯家门口的订单板、齐先生的核桃房订单板、订单的投递箱，以及奖券兑换机。</summary>
    internal sealed partial class GameActions
    {
        // ───────────────────────── 特别订单板 ─────────────────────────

        /// <summary>board 参数换成游戏里的订单类型："" = 镇上，"Qi" = 齐先生。</summary>
        private static string OrderType(string board) =>
            board?.Trim().ToLowerInvariant() is "qi" or "齐" or "齐先生" or "qichallenge" ? "Qi" : "";

        private static bool QiBoardUnlocked() => IslandWest.IsQiWalnutRoomDoorUnlocked(out _);

        /// <summary>正在做的订单（按订单板分）。</summary>
        private static List<SpecialOrder> ActiveOrders(string type) =>
            Player.team.specialOrders.Where(s => s.orderType.Value == type && s.questState.Value == SpecialOrderStatus.InProgress).ToList();

        /// <summary>
        /// 状态里的提示：订单板有新订单可接、要往投递箱里放东西（背包里正好有）、有奖券没领/没换。没事返回 null。
        /// </summary>
        public static string SpecialOrderHint()
        {
            var hints = new List<string>();
            if (SpecialOrder.IsSpecialOrdersBoardUnlocked() && !Player.team.acceptedSpecialOrderTypes.Contains("") && Player.team.GetAvailableSpecialOrder(0, "") != null)
                hints.Add("刘易斯家门口的特别订单板（Town）有新订单可以接（用 special_orders 查看/接受，一周只能接一个）");
            if (QiBoardUnlocked() && !Player.team.acceptedSpecialOrderTypes.Contains("Qi") && Player.team.GetAvailableSpecialOrder(0, "Qi") != null)
                hints.Add("齐先生的核桃房（姜岛西边）有新的齐挑战订单（special_orders board=qi）");
            foreach (var order in Player.team.specialOrders)
            {
                foreach (var d in order.objectives.OfType<DonateObjective>())
                {
                    if (d.IsComplete() || string.IsNullOrEmpty(d.dropBox.Value))
                        continue;
                    int have = Player.Items.Where(i => i != null && d.IsValidItem(i)).Sum(i => i.Stack);
                    if (have > 0)
                        hints.Add($"特别订单“{order.GetName()}”要往投递箱里放东西，背包里有 {have} 个能交（special_order_dropbox）");
                }
            }
            int pending = (int)Player.stats.Get("specialOrderPrizeTickets");
            int tickets = Player.Items.CountId("(O)PrizeTicket");
            if (pending > 0)
                hints.Add($"完成特别订单奖励的奖券还有 {pending} 张没领（订单板左边的小机器，prize_machine 会顺路领）");
            if (tickets > 0)
                hints.Add($"背包里有 {tickets} 张奖券，可以去刘易斯家的奖券机换奖品（prize_machine）");
            return hints.Count > 0 ? string.Join("；", hints) : null;
        }

        /// <summary>一个特别订单给 AI 看的说明：名字、委托人的话、目标、期限、报酬。</summary>
        private static string DescribeSpecialOrder(SpecialOrder order, bool progress)
        {
            var sb = new System.Text.StringBuilder();
            string who = order.requester.Value;
            var npc = string.IsNullOrEmpty(who) ? null : Game1.getCharacterFromName(who);
            sb.Append(order.GetName());
            if (npc != null)
                sb.Append($"（委托人：{npc.displayName}）");
            if (Player.team.completedSpecialOrders.Contains(order.questKey.Value))
                sb.Append("（以前完成过）");
            sb.AppendLine();
            string desc = order.GetDescription()?.Trim();
            if (!string.IsNullOrEmpty(desc))
                sb.AppendLine("  内容：" + desc.Replace("\n", " "));
            foreach (var obj in order.objectives)
            {
                // 目标文字是 [Pierre_Objective_0_Text] 这样的占位符，要用订单的 Parse 换成本地化文字（任务日志也是这么做的）
                string text = order.Parse(obj.GetDescription() ?? "")?.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;
                string where = obj is DonateObjective d && !string.IsNullOrEmpty(d.dropBox.Value)
                    ? $"；放进 {d.GetDropboxLocationName()} 的投递箱，用 special_order_dropbox" : "";
                sb.AppendLine(progress ? $"  目标：{text}（{obj.GetCount()}/{obj.GetMaxCount()}{(obj.IsComplete() ? "，已完成" : "")}{where}）" : $"  目标：{text}{(where != "" ? "（" + where.TrimStart('；') + "）" : "")}");
            }
            int days = order.GetDaysLeft();
            if (days > 0)
                sb.AppendLine($"  期限：{(progress ? "还剩 " : "")}{days} 天");
            var rewards = new List<string>();
            if (order.GetMoneyReward() > 0)
                rewards.Add($"{order.GetMoneyReward()}g");
            foreach (var r in order.rewards)
            {
                switch (r)
                {
                    case FriendshipReward f:
                        string target = string.IsNullOrEmpty(f.targetName.Value) ? who : f.targetName.Value;
                        string name = Game1.getCharacterFromName(target)?.displayName ?? target;
                        rewards.Add($"{name} 好感 +{f.amount.Value}");
                        break;
                    case GemsReward g:
                        rewards.Add($"齐钻 {g.amount.Value} 颗");
                        break;
                    case ObjectReward ob:
                        rewards.Add($"{ItemRegistry.GetDataOrErrorItem(ob.itemKey.Value).DisplayName} ×{ob.amount.Value}");
                        break;
                    case MailReward:
                        rewards.Add("解锁新东西（完成后会收到信）");
                        break;
                }
            }
            if (order.orderType.Value == "" && !order.questKey.Value.Contains("QiChallenge"))
                rewards.Add("奖券 1 张");
            if (rewards.Count > 0)
                sb.AppendLine("  报酬：" + string.Join("、", rewards));
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 去订单板：镇上的在刘易斯家门口（board 不传），齐先生的在姜岛西边的核桃房里（board=qi，找到 100 个金核桃后开门）。
        /// 列出左右两个订单的内容，accept=left/right（或订单名的一部分）接下其中一个。
        /// 接受的逻辑和订单板界面点“接受任务”一样（acceptedSpecialOrderTypes + AddSpecialOrder）。
        /// </summary>
        public IEnumerable<object> SpecialOrders(string accept, string board = null)
        {
            var team = Player.team;
            string type = OrderType(board);
            bool qi = type == "Qi";
            string boardName = qi ? "齐先生的订单板" : "特别订单板";
            var active = ActiveOrders(type);
            string activeText = active.Count > 0
                ? "正在做的" + (qi ? "齐挑战" : "特别订单") + "：\n" + string.Join("\n", active.Select(s => DescribeSpecialOrder(s, true)))
                : null;
            if (!qi && !SpecialOrder.IsSpecialOrdersBoardUnlocked())
            {
                yield return ActionResult.Fail("特别订单板还没开放（第一年秋季 2 日起，在刘易斯家门口）");
                yield break;
            }
            if (qi && !QiBoardUnlocked(out string qiWhy))
            {
                yield return ActionResult.Fail(qiWhy);
                yield break;
            }
            // 这周接过了：订单板上没东西可接，不用跑一趟，直接报正在做的进度
            if (team.acceptedSpecialOrderTypes.Contains(type))
            {
                string msg = $"这周已经接过{boardName}上的订单了，一周只能接一个（每周一刷新）。" + (activeText != null ? "\n" + activeText : "");
                yield return string.IsNullOrWhiteSpace(accept) ? ActionResult.Success(msg) : ActionResult.Fail(msg);
                yield break;
            }

            var o = new Outcome();
            string place = qi ? "QiNutRoom" : "Town";
            if (!Loc.Name.Equals(place, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in TravelInner(place, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }
            if (FindActionTile(Loc, qi ? "QiChallengeBoard" : "SpecialOrders") is Point boardTile)
            {
                foreach (var s in GoAdjacent(boardTile, o))
                    yield return s;
                o.Succeed();
            }

            var orders = new[] { team.GetAvailableSpecialOrder(0, type), team.GetAvailableSpecialOrder(1, type) };
            if (orders.All(x => x == null))
            {
                yield return ActionResult.Success($"{boardName}上现在没有订单（每周一刷新）" + (activeText != null ? "\n" + activeText : ""));
                yield break;
            }
            string list = string.Join("\n", orders.Select((x, i) => x == null ? null : $"{(i == 0 ? "left（左边）" : "right（右边）")}：{DescribeSpecialOrder(x, false)}").Where(x => x != null));
            string boardArg = qi ? " board=qi" : "";
            if (string.IsNullOrWhiteSpace(accept))
            {
                yield return ActionResult.Success($"{boardName}上的订单（一周只能接一个，接了不能取消，过期没完成也没有惩罚）：\n" + list
                    + $"\n（想接就用 special_orders{boardArg} accept=left 或 right；先想想期限内做不做得完）");
                yield break;
            }

            string key = accept.Trim().ToLowerInvariant();
            int pick = key is "left" or "左" or "左边" or "0" ? 0 : key is "right" or "右" or "右边" or "1" ? 1 : -1;
            if (pick < 0)
                for (int i = 0; i < orders.Length; i++)
                    if (orders[i] != null && orders[i].GetName().Contains(accept.Trim(), StringComparison.OrdinalIgnoreCase))
                        pick = i;
            if (pick < 0 || orders[pick] == null)
            {
                yield return ActionResult.Fail($"没找到要接的订单“{accept}”，accept 传 left 或 right。{boardName}上有：\n" + list);
                yield break;
            }
            var chosen = orders[pick];
            team.acceptedSpecialOrderTypes.Add(type);
            team.AddSpecialOrder(chosen.questKey.Value, chosen.generationSeed.Value);
            Game1.playSound("newArtifact");
            var added = team.specialOrders.LastOrDefault(s => s.questKey.Value == chosen.questKey.Value) ?? chosen;
            yield return ActionResult.Success("接下了订单：" + DescribeSpecialOrder(added, true)
                + $"\n（任务日志里能看到进度；special_orders{boardArg} 不带参数可以再查）");
        }

        private static bool QiBoardUnlocked(out string why)
        {
            if (IslandWest.IsQiWalnutRoomDoorUnlocked(out int found))
            {
                why = null;
                return true;
            }
            why = $"齐先生的核桃房还没开：要一共找到 100 个金核桃（现在找到 {found} 个），门在姜岛西边";
            return false;
        }

        // ───────────────────────── 投递箱 ─────────────────────────

        /// <summary>
        /// 把背包里特别订单要的东西放进投递箱（有些订单不是交给人，而是放进指定地点的箱子，比如 Gus 的冰箱、Robin 的木料箱）。
        /// 会自己走到箱子那里，打开箱子，把能交的都放进去，关上箱子算确认（和手动一样，交够数订单就完成）。
        /// </summary>
        public IEnumerable<object> SpecialOrderDropBox(string orderName)
        {
            var targets = Player.team.specialOrders
                .Where(s => s.questState.Value == SpecialOrderStatus.InProgress)
                .SelectMany(s => s.objectives.OfType<DonateObjective>().Where(d => !string.IsNullOrEmpty(d.dropBox.Value) && !d.IsComplete()).Select(d => (order: s, obj: d)))
                .ToList();
            if (!string.IsNullOrWhiteSpace(orderName))
                targets = targets.Where(t => t.order.GetName().Contains(orderName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0)
            {
                yield return ActionResult.Fail("没有要往投递箱里放东西的特别订单" + (string.IsNullOrWhiteSpace(orderName) ? "" : $"（名字含“{orderName}”的）")
                    + "。用 special_orders 看正在做的订单");
                yield break;
            }
            var (order, goal) = targets.FirstOrDefault(t => Player.Items.Any(i => i != null && t.obj.IsValidItem(i)));
            if (order == null)
            {
                var t0 = targets[0];
                yield return ActionResult.Fail($"背包里没有“{t0.order.GetName()}”要的东西：{t0.order.Parse(t0.obj.GetDescription() ?? "")}（{t0.obj.GetCount()}/{t0.obj.GetMaxCount()}）");
                yield break;
            }
            string locName = goal.GetDropboxLocationName();
            var o = new Outcome();
            if (!Loc.NameOrUniqueName.Equals(locName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in TravelInner(locName, o, exactName: true))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"去投递箱所在的 {locName} 失败：{o.Message}");
                    yield break;
                }
            }
            string box = goal.dropBox.Value;
            if (FindActionTile(Loc, "DropBox " + box) is not Point boxTile)
            {
                yield return ActionResult.Fail($"在 {locName} 没找到投递箱（{box}）");
                yield break;
            }
            foreach (var s in GoAdjacent(boxTile, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail($"走不到投递箱 ({boxTile.X},{boxTile.Y})：{o.Message}");
                yield break;
            }
            Loc.checkAction(new xLocation(boxTile.X, boxTile.Y), Game1.viewport, Player);
            for (int t = 0; t < 30 && Game1.activeClickableMenu is not QuestContainerMenu; t++)
                yield return null;
            if (Game1.activeClickableMenu is not QuestContainerMenu menu)
            {
                yield return ActionResult.Fail($"投递箱 ({boxTile.X},{boxTile.Y}) 没打开");
                yield break;
            }
            // 和在界面里点背包里的东西一样：TryToPlace 只放订单还要的数量，放完 onItemChanged 会更新订单进度
            var put = new List<string>();
            for (int i = 0; i < Player.Items.Count; i++)
            {
                var item = Player.Items[i];
                if (item == null || !order.HighlightAcceptableItems(item))
                    continue;
                int before = item.Stack;
                string label = item.DisplayName;
                Player.Items[i] = menu.TryToPlace(item, item.Stack);
                int gave = before - (Player.Items[i]?.Stack ?? 0);
                if (gave > 0)
                    put.Add($"{label}×{gave}");
            }
            foreach (var s in WaitTicks(10))
                yield return s;
            menu.exitThisMenu(); // 关上箱子时游戏确认交付（ConfirmCompleteDonations），交够数订单就完成
            foreach (var s in WaitTicks(20))
                yield return s;
            string progress = $"“{order.GetName()}”：{order.Parse(goal.GetDescription() ?? "")}（{goal.GetCount()}/{goal.GetMaxCount()}）";
            if (put.Count == 0)
            {
                yield return ActionResult.Fail("投递箱打开了，但没有放进去东西（订单已经收够了？）。" + progress);
                yield break;
            }
            bool done = order.questState.Value == SpecialOrderStatus.Complete || goal.IsComplete();
            yield return ActionResult.Success($"往投递箱里放了 {string.Join("、", put)}。{progress}"
                + (done ? "。这一项交够了" + (order.questState.Value == SpecialOrderStatus.Complete ? "，订单完成" : "") : "，还要继续凑"));
        }

        // ───────────────────────── 奖券 ─────────────────────────

        private static readonly string[] PrizeMachinePlaces = { "ManorHouse", "Town", "SeedShop" };

        /// <summary>
        /// 奖券：先去订单板左边的小机器领完成特别订单攒下的奖券，再去刘易斯家的奖券机换奖品（一张换一个，奖品按顺序排好的，前面几个可以先看）。
        /// count 不传 = 有几张换几张；look=true 只看接下来的奖品不换。
        /// </summary>
        public IEnumerable<object> PrizeMachine(int? count, bool look)
        {
            int Claimed() => (int)Game1.stats.Get("ticketPrizesClaimed");
            string Upcoming(int n) => string.Join("、", Enumerable.Range(0, n).Select(i => Util.ItemLabel(PrizeTicketMenu.getPrizeItem(Claimed() + i))));
            int pending = (int)Player.stats.Get("specialOrderPrizeTickets");
            int tickets = Player.Items.CountId("(O)PrizeTicket");
            if (look)
            {
                yield return ActionResult.Success($"背包里有 {tickets} 张奖券" + (pending > 0 ? $"，订单板那边还有 {pending} 张没领" : "")
                    + $"。奖券机接下来的奖品（按顺序）：{Upcoming(5)}");
                yield break;
            }
            if (tickets + pending == 0)
            {
                yield return ActionResult.Fail("没有奖券：完成刘易斯家门口的特别订单、或者帮人做完告示板的委托有时会给奖券。奖券机接下来的奖品：" + Upcoming(3));
                yield break;
            }
            var o = new Outcome();
            var notes = new List<string>();
            // 1) 订单板左边的小机器：特别订单完成的奖券要自己去领
            if (pending > 0)
            {
                if (!Loc.Name.Equals("Town", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in TravelInner("Town", o))
                        yield return s;
                }
                if (o.Ok && FindActionTile(Loc, "SpecialOrdersPrizeTickets") is Point disp)
                {
                    foreach (var s in GoAdjacent(disp, o))
                        yield return s;
                    int got = 0;
                    for (int k = 0; k < pending + 2 && o.Ok && Player.stats.Get("specialOrderPrizeTickets") > 0; k++)
                    {
                        uint before = Player.stats.Get("specialOrderPrizeTickets");
                        Loc.checkAction(new xLocation(disp.X, disp.Y), Game1.viewport, Player);
                        foreach (var s in WaitTicks(10))
                            yield return s;
                        if (Player.stats.Get("specialOrderPrizeTickets") >= before)
                            break; // 背包满了领不了
                        got++;
                    }
                    if (got > 0)
                        notes.Add($"在订单板旁领了 {got} 张奖券");
                    if (Player.stats.Get("specialOrderPrizeTickets") > 0)
                        notes.Add($"还有 {Player.stats.Get("specialOrderPrizeTickets")} 张没领到" + (Player.couldInventoryAcceptThisItem(ItemRegistry.Create("(O)PrizeTicket")) ? "（机器没反应，等会儿再试）" : "（背包满了）"));
                }
                else
                    notes.Add("没走到订单板旁的领奖券机器" + (o.Ok ? "" : "：" + o.Message));
                o.Succeed();
            }
            tickets = Player.Items.CountId("(O)PrizeTicket");
            if (tickets == 0)
            {
                yield return ActionResult.Fail(string.Join("；", notes.DefaultIfEmpty("没有奖券")));
                yield break;
            }
            // 2) 奖券机：在刘易斯家（ManorHouse）
            GameLocation machineLoc = null;
            Point machine = default;
            foreach (var name in PrizeMachinePlaces)
            {
                var l = Game1.getLocationFromName(name);
                if (l != null && FindActionTile(l, "PrizeMachine") is Point p)
                {
                    machineLoc = l;
                    machine = p;
                    break;
                }
            }
            if (machineLoc == null)
            {
                yield return ActionResult.Fail("没找到奖券机（应该在刘易斯家）。" + string.Join("；", notes));
                yield break;
            }
            if (Loc != machineLoc)
            {
                foreach (var s in TravelInner(machineLoc.NameOrUniqueName, o, exactName: true))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"去奖券机（{machineLoc.Name}）失败：{o.Message}。" + string.Join("；", notes));
                    yield break;
                }
            }
            foreach (var s in GoAdjacent(machine, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail($"走不到奖券机 ({machine.X},{machine.Y})：{o.Message}");
                yield break;
            }
            // 和奖券机界面按按钮一样：拿走排在最前面的奖品，扣一张奖券，奖品队列往前挪一格（PrizeTicketMenu.update）
            int want = Math.Clamp(count ?? tickets, 1, tickets);
            var prizes = new List<string>();
            bool dropped = false;
            for (int k = 0; k < want; k++)
            {
                var prize = PrizeTicketMenu.getPrizeItem(Claimed());
                string label = Util.ItemLabel(prize);
                if (!Player.addItemToInventoryBool(prize))
                {
                    Game1.createItemDebris(prize, Player.getStandingPosition(), 1, Loc);
                    dropped = true;
                }
                Player.Items.ReduceId("(O)PrizeTicket", 1);
                Game1.stats.Increment("ticketPrizesClaimed");
                Game1.playSound("coin");
                prizes.Add(label);
                foreach (var s in WaitTicks(20))
                    yield return s;
            }
            notes.Add($"用 {want} 张奖券换到：{string.Join("、", prizes)}" + (dropped ? "（背包满了，有的掉在脚边，走一步就捡起来）" : ""));
            int left = Player.Items.CountId("(O)PrizeTicket");
            if (left > 0)
                notes.Add($"还剩 {left} 张奖券");
            notes.Add("接下来的奖品：" + Upcoming(3));
            yield return ActionResult.Success(string.Join("；", notes));
        }
    }
}
