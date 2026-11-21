using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Extensions;
using StardewValley.GameData.Buildings;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.TokenizableStrings;
using StardewValley.Tools;
using SObject = StardewValley.Object;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    /// <summary>镇上的服务：工具升级（Clint）、告示板任务、买动物（Marnie）、建造和升级房子（Robin）。</summary>
    internal sealed partial class GameActions
    {
        /// <summary>店主在柜台的时间：开门、关门（游戏时间）和休息日（星期几的英文缩写）。</summary>
        private static readonly Dictionary<string, (int open, int close, string[] daysOff, string name)> ShopHours = new()
        {
            ["Clint"] = (900, 1600, Array.Empty<string>(), "Clint 的铁匠铺"),
            ["Marnie"] = (900, 1600, new[] { "Mon", "Tue" }, "Marnie 的牧场"),
            ["Robin"] = (900, 1700, new[] { "Tue" }, "Robin 的木匠铺"),
        };

        private static readonly Dictionary<string, string> DayNames = new()
        {
            ["Mon"] = "周一", ["Tue"] = "周二", ["Wed"] = "周三", ["Thu"] = "周四", ["Fri"] = "周五", ["Sat"] = "周六", ["Sun"] = "周日"
        };

        /// <summary>
        /// 营业时间里店主也会出门（比如去镇上办事、去健身课）：按她今天的日程说清楚现在在哪、几点回店里。
        /// early=true 是出发前用：她正在店里或者正往店里走、或者等她回来不超过半小时，返回 null（照常出发）。
        /// 看不出来（没有日程）返回 null。
        /// </summary>
        private static string ShopkeeperOut(string npc, string shop, int walk, bool early)
        {
            var who = Game1.getCharacterFromName(npc);
            if (who == null)
                return null;
            string here = who.currentLocation?.NameOrUniqueName;
            if (early && here != null && here.Equals(shop, StringComparison.OrdinalIgnoreCase))
                return null;
            var schedule = who.Schedule;
            int now = Game1.timeOfDay;
            int? back = null;
            if (schedule != null)
            {
                var past = schedule.Where(kv => kv.Key <= now).OrderByDescending(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();
                // 最近一段日程就是回店里：在路上了
                if (early && past != null && shop.Equals(past.targetLocationName, StringComparison.OrdinalIgnoreCase))
                    return null;
                // 下班以后才回家（比如晚上回卧室）不算回柜台
                int close = ShopHours.TryGetValue(npc, out var hrs) ? hrs.close : 2600;
                back = schedule.Where(kv => kv.Key > now && kv.Key < close && shop.Equals(kv.Value.targetLocationName, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => (int?)kv.Key).OrderBy(k => k).FirstOrDefault();
            }
            else if (early)
                return null;
            string name = NpcName(npc);
            string where = who.currentLocation == null ? "" : $"{name}现在在{who.currentLocation.DisplayName}（{here} {who.TilePoint.X},{who.TilePoint.Y}）";
            if (here != null && here.Equals(shop, StringComparison.OrdinalIgnoreCase))
                where = $"{name}在店里但不在柜台（{who.TilePoint.X},{who.TilePoint.Y}），可能刚回来还在走，过一会儿再试";
            if (back.HasValue)
            {
                if (early && Utility.CalculateMinutesBetweenTimes(now, back.Value) - walk <= DoorWaitMinutes)
                    return null;
                return $"{where}，按{name}今天的日程 {Util.TimeString(back.Value)} 回店里。先干别的，到时候再来（wait 可以写 until={Util.TimeString(back.Value)}）";
            }
            return schedule == null ? (where == "" ? null : where)
                : $"{where}，按{name}今天的日程下班前不会再回柜台了，明天再来";
        }

        /// <summary>出发前判断店主今天、现在会不会在柜台：不在就返回原因（没把握的情况返回 null，到了再看）。</summary>
        private static string ShopkeeperAway(string npc, int walk = 0)
        {
            if (!ShopHours.TryGetValue(npc, out var h))
                return null;
            if (GameLocation.AreStoresClosedForFestival())
                return $"今天是节日，镇上的店铺都关门，{h.name}今天不营业。先干别的（或者去参加节日），明天再来";
            string day = Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth);
            if (h.daysOff.Contains(day))
                return $"{h.name}{string.Join("、", h.daysOff.Select(d => DayNames[d]))}休息，今天是{DayNames[day]}，去了也没人。明天再来";
            int now = Game1.timeOfDay;
            string hours = $"营业 {Util.TimeString(h.open)}-{Util.TimeString(h.close)}，现在 {Util.TimeString(now)}";
            if (now >= h.close)
                return $"{h.name}今天已经下班了（{hours}），明天再来";
            if (now < h.open && Utility.CalculateMinutesBetweenTimes(now, h.open) - walk > DoorWaitMinutes)
                return $"{h.name}还没开门（{hours}），走过去大约 {walk} 分钟，到了还得干等 {Utility.CalculateMinutesBetweenTimes(now, h.open) - walk} 分钟，没有出发。"
                    + $"先干别的，{Util.TimeString(Utility.ModifyTime(h.open, -(walk + 10)))} 左右再出发";
            return null;
        }

        /// <summary>去某个店并确认店主在场。</summary>
        private IEnumerable<object> GoToShopkeeper(string location, string npc, string counterAction, Outcome o)
        {
            // 先看今天、现在店主在不在柜台，免得走过去才发现白跑
            var shopRoute = Loc.NameOrUniqueName.Equals(location, StringComparison.OrdinalIgnoreCase) ? null : Exits.PlanRoute(Loc, location);
            int walk = shopRoute is { Count: > 0 } ? RouteMinutes(shopRoute, shopRoute.Count - 1) : 0;
            if (!Loc.NameOrUniqueName.Equals(location, StringComparison.OrdinalIgnoreCase)
                && (ShopkeeperAway(npc, walk) ?? ShopkeeperOut(npc, location, walk, true)) is string away)
            {
                o.Fail(away);
                yield break;
            }
            if (!Loc.NameOrUniqueName.Equals(location, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in TravelInner(location, o))
                    yield return s;
                if (!o.Ok)
                    yield break;
            }
            // 提前到了：店主快上班了就在店里等一会儿（最多等到开门后半小时）
            if (!Loc.characters.Any(c => c.Name == npc) && ShopHours.TryGetValue(npc, out var hours)
                && Game1.timeOfDay < hours.open && Utility.CalculateMinutesBetweenTimes(Game1.timeOfDay, hours.open) <= DoorWaitMinutes)
            {
                int giveUp = Utility.ModifyTime(hours.open, 30);
                int closedDialogues = 0;
                for (int t = 0; t < 60 * 600 && Game1.timeOfDay < giveUp && !Loc.characters.Any(c => c.Name == npc); t++)
                {
                    // 普通对话框关掉接着等（最多 3 次），剧情和问题才停
                    if (!Game1.eventUp && Game1.activeClickableMenu is DialogueBox { isQuestion: false } chat && closedDialogues < 3)
                    {
                        chat.exitThisMenu(false);
                        closedDialogues++;
                        continue;
                    }
                    if ((Game1.eventUp && Game1.CurrentEvent != null) || Game1.activeClickableMenu != null)
                        break;
                    yield return null;
                }
            }
            if (!Loc.characters.Any(c => c.Name == npc))
            {
                o.Fail(ShopkeeperOut(npc, location, 0, false)
                    ?? $"{npc} 现在不在店里（看营业时间：Clint 9:00-16:00，Marnie 9:00-16:00 周一周二休息，Robin 9:00-17:00 周二休息）");
                yield break;
            }
            var counter = counterAction != null ? FindTileAction(Loc, counterAction) : null;
            if (counter.HasValue && !NextTo(counter.Value))
            {
                // 柜台是墙里的一格，上下左右常常站不了（铁匠铺柜台只能从斜下方点）：斜角也算
                var stands = NpcStandTiles(counter.Value);
                if (stands.Count == 0)
                {
                    o.Fail($"走不到柜台 ({counter.Value.X},{counter.Value.Y})：四周（含斜角）都没有能站的格子");
                    yield break;
                }
                foreach (var s in GoToAny(stands, o, $"柜台 ({counter.Value.X},{counter.Value.Y})"))
                    yield return s;
                if (!o.Ok)
                    yield break;
            }
            if (counter.HasValue)
                Face(counter.Value);
            o.Succeed();
        }

        private IEnumerable<object> CloseDialogues()
        {
            for (int i = 0; i < 10 && Game1.activeClickableMenu is DialogueBox; i++)
            {
                foreach (var s in Dialogue(null))
                    if (s is not ActionResult)
                        yield return s;
            }
        }

        // ───────────────────────── 砸晶球 ─────────────────────────

        /// <summary>
        /// 找 Clint 砸晶球：和玩家在 GeodeMenu 里拿起晶球、放到铁砧上一样（每个 25g，铁砧的动画结束后东西进背包）。
        /// </summary>
        public IEnumerable<object> CrackGeodes(string itemSpec, int? count)
        {
            var filter = ItemFilter(itemSpec);
            var geodes = Player.Items.Where(i => i != null && Utility.IsGeode(i) && filter(i)).ToList();
            if (geodes.Count == 0)
            {
                yield return ActionResult.Fail("背包里没有" + (string.IsNullOrWhiteSpace(itemSpec) ? "晶球" : $"符合“{itemSpec}”的晶球"));
                yield break;
            }

            var o = new Outcome();
            if (Game1.activeClickableMenu is not GeodeMenu)
            {
                if (Blocked(out string why))
                {
                    yield return ActionResult.Fail(why);
                    yield break;
                }
                foreach (var s in GoToShopkeeper("Blacksmith", "Clint", "Blacksmith", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
                // 和玩家一样对着柜台按动作键，在“买东西 / 升级 / 砸晶球 / 离开”里选砸晶球（以前直接 new GeodeMenu()，隔空打开）
                var geodeCounter = FindTileAction(Loc, "Blacksmith");
                if (geodeCounter.HasValue)
                {
                    Loc.checkAction(new xLocation(geodeCounter.Value.X, geodeCounter.Value.Y), Game1.viewport, Player);
                    for (int t = 0; t < 60 && Game1.activeClickableMenu == null; t++)
                        yield return null;
                    if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask && ask.responses != null)
                    {
                        int process = Array.FindIndex(ask.responses, r => r.responseKey == "Process");
                        if (process >= 0)
                        {
                            for (int t = 0; t < 60 && (ask.transitioning || ask.safetyTimer > 0); t++)
                                yield return null;
                            ask.selectedResponse = process;
                            ask.receiveLeftClick(0, 0);
                            for (int t = 0; t < 60 && Game1.activeClickableMenu is not GeodeMenu; t++)
                                yield return null;
                        }
                    }
                }
                if (Game1.activeClickableMenu is not GeodeMenu)
                {
                    string said = Game1.activeClickableMenu is DialogueBox d ? "：" + d.getCurrentString() : "";
                    Game1.activeClickableMenu?.exitThisMenu(false);
                    yield return ActionResult.Fail("Clint 没给砸晶球" + said);
                    yield break;
                }
            }
            var menu = (GeodeMenu)Game1.activeClickableMenu;

            int left = Math.Max(count ?? int.MaxValue, 1), cost = 0;
            var found = new List<string>();
            string stop = null;
            foreach (var geode in geodes)
            {
                while (left > 0 && stop == null && Player.Items.Contains(geode) && geode.Stack > 0)
                {
                    if (Player.Money < 25)
                    {
                        stop = "钱不够了（每个 25g）";
                        break;
                    }
                    // 拿起来放到铁砧上。剩下的马上放回原来的格子，开出来的东西才不会占了它
                    int slot = Player.Items.IndexOf(geode);
                    Player.Items[slot] = null;
                    menu.heldItem = geode;
                    var spot = menu.geodeSpot.bounds.Center;
                    menu.receiveLeftClick(spot.X, spot.Y);
                    bool started = menu.geodeAnimationTimer > 0;
                    Player.Items[slot] = menu.heldItem;
                    menu.heldItem = null;
                    if (!started)
                    {
                        stop = "背包满了，开出来的东西没地方放，先空出一格";
                        break;
                    }
                    cost += 25;
                    left--;

                    Item treasure = null;
                    for (int t = 0; t < 400 && menu.geodeAnimationTimer > 0; t++)
                    {
                        treasure = menu.geodeTreasure ?? treasure;
                        yield return null;
                    }
                    if (treasure != null)
                        found.Add(treasure.Stack > 1 ? $"{treasure.DisplayName}x{treasure.Stack}" : treasure.DisplayName);
                }
            }

            if (Game1.activeClickableMenu == menu)
            {
                if (menu.readyToClose())
                    menu.exitThisMenu();
                else
                {
                    menu.emergencyShutDown();
                    Game1.exitActiveMenu();
                }
            }
            for (int i = 0; i < 120 && Game1.activeClickableMenu == menu; i++)
                yield return null;

            if (cost == 0)
            {
                yield return ActionResult.Fail(stop ?? "一个也没砸开");
                yield break;
            }
            string msg = $"砸开 {cost / 25} 个晶球，花了 {cost}g，现在有 {Player.Money}g，开出来：{(found.Count > 0 ? string.Join("、", found) : "（没看到结果，查一下背包）")}，都在背包里";
            int remaining = Player.Items.Where(i => i != null && Utility.IsGeode(i)).Sum(i => i.Stack);
            if (remaining > 0)
                msg += $"；背包里还有 {remaining} 个晶球";
            if (stop != null)
                msg += "；停下原因：" + stop;
            yield return ActionResult.Success(msg);
        }

        // ───────────────────────── 工具升级 ─────────────────────────

        public static string ToolUpgradeStatus()
        {
            var t = Game1.player.toolBeingUpgraded.Value;
            if (t == null)
                return null;
            int days = Game1.player.daysLeftForToolUpgrade.Value;
            return days > 0 ? $"{t.DisplayName} 正在铁匠铺升级，还要 {days} 天" : $"{t.DisplayName} 已经升级好了，去铁匠铺（Blacksmith）用 upgrade_tool 领取";
        }

        public IEnumerable<object> UpgradeTool(string toolSpec)
        {
            var o = new Outcome();
            var pending = Player.toolBeingUpgraded.Value;
            if (pending != null && Player.daysLeftForToolUpgrade.Value > 0)
            {
                yield return ActionResult.Fail(ToolUpgradeStatus() + "，同一时间只能升级一件");
                yield break;
            }
            // 不是来领工具的：出门前先查工具带没带、钱和锭够不够，免得走到铁匠铺才发现
            if (pending == null && UpgradePrecheck(toolSpec) is string notReady)
            {
                yield return ActionResult.Fail(notReady + "（这次没有出门）");
                yield break;
            }

            foreach (var s in GoToShopkeeper("Blacksmith", "Clint", "Blacksmith", o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }

            // 领取升级好的工具：和走到柜台跟 Clint 说话一样
            if (pending != null)
            {
                if (Player.freeSpotsInInventory() == 0)
                {
                    yield return ActionResult.Fail("背包满了，空出一格再来领工具");
                    yield break;
                }
                var counter = FindTileAction(Loc, "Blacksmith");
                if (counter.HasValue)
                    Loc.checkAction(new xLocation(counter.Value.X, counter.Value.Y), Game1.viewport, Player);
                for (int t = 0; t < 120 && Player.toolBeingUpgraded.Value != null; t++)
                    yield return null;
                foreach (var s in WaitTicks(60))
                    yield return s;
                foreach (var s in CloseDialogues())
                    yield return s;
                yield return Player.toolBeingUpgraded.Value == null
                    ? ActionResult.Success($"领到了升级后的 {pending.DisplayName}")
                    : ActionResult.Fail("没领到工具（Clint 可能不在柜台）");
                yield break;
            }

            int idx = Util.FindToolIndex(toolSpec);
            if (idx < 0 || Player.Items[idx] is not Tool current)
            {
                yield return ActionResult.Fail($"背包里没有“{toolSpec}”（可升级：axe、pickaxe、hoe、watering_can）");
                yield break;
            }
            // 和玩家一样对着柜台按动作键，再在“买东西 / 升级 / 离开”里选升级（以前直接调 TryOpenShopMenu，隔空开升级界面）
            var upgradeCounter = FindTileAction(Loc, "Blacksmith");
            if (upgradeCounter.HasValue)
            {
                Loc.checkAction(new xLocation(upgradeCounter.Value.X, upgradeCounter.Value.Y), Game1.viewport, Player);
                for (int t = 0; t < 60 && Game1.activeClickableMenu == null; t++)
                    yield return null;
                if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask && ask.responses != null)
                {
                    int up = Array.FindIndex(ask.responses, r => r.responseKey == "Upgrade");
                    if (up >= 0)
                    {
                        for (int t = 0; t < 60 && (ask.transitioning || ask.safetyTimer > 0); t++)
                            yield return null;
                        ask.selectedResponse = up;
                        ask.receiveLeftClick(0, 0);
                        for (int t = 0; t < 60 && Game1.activeClickableMenu is not ShopMenu; t++)
                            yield return null;
                    }
                }
            }
            if (Game1.activeClickableMenu is not ShopMenu shop)
            {
                string said = Game1.activeClickableMenu is DialogueBox said0 ? "：" + said0.getCurrentString() : "";
                Game1.activeClickableMenu?.exitThisMenu(false);
                yield return ActionResult.Fail("Clint 的升级服务没开" + said);
                yield break;
            }
            yield return null;
            var offer = shop.forSale.OfType<Tool>()
                .FirstOrDefault(t => t.GetType() == current.GetType() && t.UpgradeLevel == current.UpgradeLevel + 1 && shop.itemPriceAndStock.ContainsKey(t));
            if (offer == null)
            {
                string list = string.Join("、", shop.forSale.Where(s => shop.itemPriceAndStock.ContainsKey(s)).Select(s =>
                {
                    var info = shop.itemPriceAndStock[s];
                    return $"{s.DisplayName} {info.Price}g + {info.TradeItemCount ?? 1}个{Util.ObjectName(info.TradeItem)}";
                }));
                shop.exitThisMenu(false);
                yield return ActionResult.Fail($"{current.DisplayName} 现在不能升级（已是最高级或者没解锁）。可升级项目：{list}");
                yield break;
            }
            var stock = shop.itemPriceAndStock[offer];
            string cost = $"{stock.Price}g + {stock.TradeItemCount ?? 1}个{Util.ObjectName(stock.TradeItem)}";
            // 钱或矿锭不够：直接算出差多少，别让 AI 自己去猜
            var gaps = new List<string>();
            if (Player.Money < stock.Price)
                gaps.Add($"钱差 {stock.Price - Player.Money}g（要 {stock.Price}g，你身上 {Player.Money}g）");
            if (stock.TradeItem != null)
            {
                string q = ItemRegistry.QualifyItemId(stock.TradeItem) ?? stock.TradeItem;
                int need = stock.TradeItemCount ?? 1;
                int have = Player.Items.Where(i => i != null && i.QualifiedItemId == q).Sum(i => i.Stack);
                if (have < need)
                {
                    string bar = Util.ObjectName(stock.TradeItem);
                    gaps.Add($"材料差 {need - have} 个{bar}（要 {need} 个，背包里有 {have} 个；可以先 list_chests item={bar} 看看箱子里有没有，矿石放进熔炉加煤炭能炼成锭）");
                }
            }
            if (gaps.Count > 0)
            {
                shop.exitThisMenu(false);
                yield return ActionResult.Fail($"升级 {current.DisplayName} → {offer.DisplayName} 需要 {cost}，现在还不够：{string.Join("；", gaps)}");
                yield break;
            }
            ActionResult buy = null;
            foreach (var s in Buy(shop.forSale.Where(x => shop.itemPriceAndStock.ContainsKey(x)).ToList().IndexOf(offer).ToString(), 1))
            {
                if (s is ActionResult r)
                {
                    buy = r;
                    break;
                }
                yield return s;
            }
            bool started = Player.toolBeingUpgraded.Value != null;
            foreach (var s in CloseDialogues())
                yield return s;
            if (Game1.activeClickableMenu is ShopMenu stillOpen)
                stillOpen.exitThisMenu(false);
            yield return started
                ? ActionResult.Success($"把 {current.DisplayName} 交给 Clint 升级成 {offer.DisplayName}（花费 {cost}），2 天后来铁匠铺用 upgrade_tool 领取。升级期间这件工具不能用！")
                : ActionResult.Fail($"升级失败（需要 {cost}）：{buy?.Message}");
        }

        /// <summary>升级工具出门前的检查：工具在不在背包（不在就说在哪个箱子）、钱和锭够不够。都齐了返回 null。</summary>
        private static string UpgradePrecheck(string toolSpec)
        {
            int idx = Util.FindToolIndex(toolSpec);
            if (idx < 0 || Player.Items[idx] is not Tool current)
            {
                var match = Util.ToolMatcher(toolSpec);
                foreach (var (loc, tile, chest) in AllChests())
                {
                    var found = chest.GetItemsForPlayer().FirstOrDefault(i => i != null && match(i));
                    if (found != null)
                        return $"要升级的“{found.DisplayName}”不在背包里，在 {loc.NameOrUniqueName} ({tile.X},{tile.Y}) 的箱子里。先 take_items items={found.DisplayName} x={tile.X} y={tile.Y} 拿上（在别的地图要先 travel_to {loc.NameOrUniqueName}）";
                }
                return $"背包里没有“{toolSpec}”，箱子里也没找到（可升级：axe、pickaxe、hoe、watering_can）";
            }
            Dictionary<ISalable, ItemStockInformation> stock;
            try
            {
                stock = StardewValley.Internal.ShopBuilder.GetShopStock("ClintUpgrade");
            }
            catch
            {
                return null; // 读不到价目表就到了再看
            }
            var offer = stock.FirstOrDefault(p => p.Key is Tool t && t.GetType() == current.GetType() && t.UpgradeLevel == current.UpgradeLevel + 1);
            if (offer.Key == null)
                return null; // 已是最高级之类：到店里会说清楚
            var info = offer.Value;
            var gaps = new List<string>();
            if (Player.Money < info.Price)
                gaps.Add($"钱差 {info.Price - Player.Money}g（要 {info.Price}g，你身上 {Player.Money}g）");
            if (info.TradeItem != null)
            {
                string q = ItemRegistry.QualifyItemId(info.TradeItem) ?? info.TradeItem;
                int need = info.TradeItemCount ?? 1;
                int have = Player.Items.CountId(q);
                if (have < need)
                {
                    string bar = Util.ObjectName(info.TradeItem);
                    gaps.Add($"材料差 {need - have} 个{bar}（要 {need} 个，背包里有 {have} 个；可以先 list_chests item={bar} 看看箱子里有没有，矿石放进熔炉加煤炭能炼成锭）");
                }
            }
            return gaps.Count > 0
                ? $"升级 {current.DisplayName} → {offer.Key.DisplayName} 还不够：{string.Join("；", gaps)}"
                : null;
        }

        /// <summary>
        /// 书商（Marcello）：每季只来镇上两天（Utility.getDaysOfBooksellerThisSeason），摊子在镇子东边、博物馆南边的路上，
        /// 地图上带 Action "Bookseller" 的那格。来的当天和前一天提醒一句。
        /// </summary>
        public static string BooksellerHint()
        {
            var days = Utility.getDaysOfBooksellerThisSeason();
            if (days == null || days.Count == 0)
                return null;
            string when = string.Join("、", days.OrderBy(d => d).Select(d => $"{d} 日"));
            if (days.Contains(Game1.dayOfMonth))
            {
                var town = Game1.getLocationFromName("Town");
                var stall = town != null ? FindTileAction(town, "Bookseller") : null;
                return $"📚 书商今天在镇上（这季来 {when}）：摊子在 Town {(stall is Point p ? $"({p.X},{p.Y})" : "东边博物馆南面的路上")}，"
                    + $"travel_to Town 后 interact {(stall is Point q ? $"x={q.X} y={q.Y}" : "那个摊子")} 打开书店（读过书以后会先问买书还是换书，选买书）。技能书、能力书只有他卖，钱够就买";
            }
            if (days.Contains(Game1.dayOfMonth + 1))
                return $"📚 明天书商来镇上（这季来 {when}），卖技能书和能力书";
            return null;
        }

        // ───────────────────────── 告示板任务 ─────────────────────────

        public static string DailyQuestHint()
        {
            var q = Game1.questOfTheDay;
            if (q == null || q.accepted.Value || !Game1.CanAcceptDailyQuest())
                return null;
            return $"镇上告示板（Town，皮埃尔杂货店门口）今天有委托任务：{q.GetName()}（用 quest_board 查看/接受）";
        }

        /// <summary>农场山洞（果蝠掉的水果 / 蘑菇箱）有东西可收时的提示。没有返回 null。</summary>
        public static string FarmCaveHint()
        {
            if (Game1.getLocationFromName("FarmCave") is not GameLocation cave || Game1.MasterPlayer.caveChoice.Value <= 0)
                return null;
            int fruit = cave.objects.Values.Count(o => o.IsSpawnedObject);
            var boxes = cave.objects.Pairs
                .Where(p => p.Value.QualifiedItemId == "(BC)128" && p.Value.heldObject.Value != null && p.Value.MinutesUntilReady <= 0)
                .Select(p => $"({(int)p.Key.X},{(int)p.Key.Y})").ToList();
            if (fruit == 0 && boxes.Count == 0)
                return null;
            var todo = new List<string>();
            if (fruit > 0)
                todo.Add($"地上有 {fruit} 个果蝠带来的水果，进洞后用 forage 捡");
            if (boxes.Count > 0)
                todo.Add($"{boxes.Count} 个蘑菇箱长好了，进洞后对每个 interact：{string.Join(" ", boxes)}");
            return "农场山洞（FarmCave，农舍北边的洞口）有东西可收：" + string.Join("；", todo) + "。只在看到这条提示时才去收，没这条提示就说明还没长好，别白跑";
        }

        public IEnumerable<object> QuestBoard(bool accept)
        {
            var o = new Outcome();
            if (!Loc.Name.Equals("Town", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in TravelInner("Town", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }
            // 每日任务的告示板动作是 "Billboard 3"（另一个 Billboard 是日历）
            Point? board = null;
            int w = Pathfinder.MapWidth(Loc), h = Pathfinder.MapHeight(Loc);
            for (int x = 0; x < w && board == null; x++)
                for (int y = 0; y < h && board == null; y++)
                    if (Loc.doesTileHaveProperty(x, y, "Action", "Buildings") is string a && a.Trim() == "Billboard 3")
                        board = new Point(x, y);
            if (board.HasValue)
            {
                foreach (var s in GoAdjacent(board.Value, o))
                    yield return s;
            }

            var q = Game1.questOfTheDay;
            if (q == null)
            {
                yield return ActionResult.Success("告示板今天没有委托任务");
                yield break;
            }
            string desc = $"委托：{q.GetName()}\n{q.GetDescription()}\n目标：{string.Join("；", q.GetObjectiveDescriptions() ?? new List<string>())}" +
                (q.GetMoneyReward() > 0 ? $"\n报酬：{q.GetMoneyReward()}g" : "");
            if (q.accepted.Value || !Game1.CanAcceptDailyQuest())
            {
                yield return ActionResult.Success("今天的委托已经接过了。\n" + desc);
                yield break;
            }
            if (!accept)
            {
                yield return ActionResult.Success(desc + "\n（要接受就用 quest_board accept=true）");
                yield break;
            }
            // 与告示板界面点“接受任务”按钮的逻辑一致
            q.dailyQuest.Value = true;
            q.dayQuestAccepted.Value = Game1.Date.TotalDays;
            q.accepted.Value = true;
            q.canBeCancelled.Value = true;
            q.daysLeft.Value = 2;
            Player.questLog.Add(q);
            Player.acceptedDailyQuest.Set(newValue: true);
            Game1.playSound("newArtifact");
            yield return ActionResult.Success("接受了委托（2 天内完成）。\n" + desc);
        }

        // ───────────────────────── 买动物 ─────────────────────────

        private long NewMultiplayerId()
        {
            try
            {
                var mp = reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
                return mp.getNewID();
            }
            catch
            {
                return Utility.RandomLong();
            }
        }

        private static string AnimalDisplayName(string key)
        {
            if (Game1.farmAnimalData.TryGetValue(key, out var data) && data.DisplayName != null)
                return TokenParser.ParseText(data.DisplayName);
            return key;
        }

        public IEnumerable<object> BuyAnimal(string typeSpec, string name, int count)
        {
            var o = new Outcome();
            foreach (var s in GoToShopkeeper("AnimalShop", "Marnie", "AnimalShop", o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }

            var farm = Game1.getFarm();
            var stock = Utility.getPurchaseAnimalStock(farm);
            string Label(SObject s) => $"{AnimalDisplayName(s.Name)}({s.Name}) {s.salePrice()}g" + (s.Type != null ? $"【{(s.Type.Length > 0 ? s.Type : "缺少需要的建筑")}】" : "");
            var pick = string.IsNullOrWhiteSpace(typeSpec) ? null :
                stock.FirstOrDefault(s => s.Name.Equals(typeSpec, StringComparison.OrdinalIgnoreCase) || AnimalDisplayName(s.Name).Equals(typeSpec, StringComparison.OrdinalIgnoreCase))
                ?? stock.FirstOrDefault(s => s.Name.Contains(typeSpec, StringComparison.OrdinalIgnoreCase) || AnimalDisplayName(s.Name).Contains(typeSpec, StringComparison.OrdinalIgnoreCase));
            if (pick == null)
            {
                yield return ActionResult.Fail("Marnie 这里能买的动物：" + string.Join("、", stock.Select(Label)));
                yield break;
            }
            if (pick.Type != null)
            {
                yield return ActionResult.Fail($"还不能买 {AnimalDisplayName(pick.Name)}：{(pick.Type.Length > 0 ? pick.Type : "需要先建对应的建筑")}（用 build 找 Robin 建）");
                yield break;
            }

            int price = pick.salePrice();
            var bought = new List<string>();
            string stop = null;
            for (int n = 0; n < Math.Clamp(count, 1, 12); n++)
            {
                if (Player.Money < price)
                {
                    stop = $"钱不够（{price}g 一只）";
                    break;
                }
                // 与 PurchaseAnimalsMenu 一致：有的动物会随机变成另一种颜色（比如白鸡/褐鸡）
                string type = pick.Name;
                if (Game1.farmAnimalData.TryGetValue(type, out var data) && data.AlternatePurchaseTypes != null)
                {
                    foreach (var alt in data.AlternatePurchaseTypes)
                    {
                        if (GameStateQuery.CheckConditions(alt.Condition))
                        {
                            type = Game1.random.ChooseFrom(alt.AnimalIds);
                            break;
                        }
                    }
                }
                var animal = new FarmAnimal(type, NewMultiplayerId(), Player.UniqueMultiplayerID);
                var home = farm.buildings.FirstOrDefault(b => !b.isUnderConstruction() && b.GetIndoors() is AnimalHouse h && !h.isFull() && animal.CanLiveIn(b));
                if (home == null)
                {
                    stop = $"没有能住 {animal.displayType} 的空位（鸡舍/畜棚满了或者没建）";
                    break;
                }
                string animalName = !string.IsNullOrWhiteSpace(name) && n == 0 && !Utility.areThereAnyOtherAnimalsWithThisName(name.Trim()) ? name.Trim() : null;
                for (int tries = 0; animalName == null && tries < 20; tries++)
                {
                    string candidate = StardewValley.Dialogue.randomName();
                    if (!Utility.areThereAnyOtherAnimalsWithThisName(candidate))
                        animalName = candidate;
                }
                animalName ??= type + Game1.random.Next(1000, 9999);
                animal.Name = animalName;
                animal.displayName = animalName;
                ((AnimalHouse)home.GetIndoors()).adoptAnimal(animal);
                Player.Money -= price;
                bought.Add($"{animal.displayType}“{animalName}”→{home.buildingType.Value}");
                Game1.playSound("purchaseClick");
                foreach (var s in WaitTicks(10))
                    yield return s;
            }
            string msg = bought.Count > 0 ? $"买了 {bought.Count} 只：{string.Join("、", bought)}，花了 {bought.Count * price}g，剩 {Player.Money}g" : "一只也没买成";
            if (stop != null)
                msg += "（" + stop + "）";
            yield return bought.Count > 0 ? ActionResult.Success(msg) : ActionResult.Fail(msg);
        }

        // ───────────────────────── 建造（Robin） ─────────────────────────

        private static string BlueprintCost(CarpenterMenu.BlueprintEntry bp) =>
            $"{bp.BuildCost}g" + (bp.BuildMaterials != null && bp.BuildMaterials.Count > 0
                ? " + " + string.Join(" + ", bp.BuildMaterials.Select(m => $"{m.Amount}个{Util.ObjectName(m.ItemId)}"))
                : "") + (bp.BuildDays > 0 ? $"，工期 {bp.BuildDays} 天" : bp.MagicalConstruction ? "，当场建好" : "，当天建好");

        /// <summary>建这个还差什么：钱差多少、每样材料身上有几个、差几个。</summary>
        private static string BlueprintMissing(CarpenterMenu.BlueprintEntry bp)
        {
            var miss = new List<string>();
            if (bp.BuildCost > Player.Money)
                miss.Add($"钱差 {bp.BuildCost - Player.Money}g（身上 {Player.Money}/{bp.BuildCost}）");
            foreach (var m in bp.BuildMaterials ?? new List<BuildingMaterial>())
            {
                int have = Player.Items.CountId(ItemRegistry.QualifyItemId(m.ItemId) ?? m.ItemId);
                if (have < m.Amount)
                    miss.Add($"{Util.ObjectName(m.ItemId)}差 {m.Amount - have}（身上 {have}/{m.Amount}）");
            }
            return miss.Count > 0 ? string.Join("、", miss) : "材料或钱";
        }

        /// <summary>这块地能不能放下这个建筑（和 buildStructure 的检查一致，必须在农场上调用）。</summary>
        private static bool CanPlace(GameLocation farm, BuildingData data, Point tile)
        {
            for (int dy = 0; dy < data.Size.Y; dy++)
                for (int dx = 0; dx < data.Size.X; dx++)
                {
                    var v = new Vector2(tile.X + dx, tile.Y + dy);
                    // 不压耕地、树等地形（游戏允许压草，但耕地和作物会被毁掉，这里保守一点）
                    if (farm.terrainFeatures.TryGetValue(v, out var tf) && tf is not Grass)
                        return false;
                    if (!farm.isBuildable(v))
                        return false;
                    if (Player.GetBoundingBox().Intersects(new Rectangle((int)v.X * 64, (int)v.Y * 64, 64, 64)))
                        return false;
                }
            if (data.AdditionalPlacementTiles != null)
                foreach (var extra in data.AdditionalPlacementTiles)
                    foreach (var p in extra.TileArea.GetPoints())
                        if (!farm.isBuildable(new Vector2(tile.X + p.X, tile.Y + p.Y), extra.OnlyNeedsToBePassable))
                            return false;
            return true;
        }

        public IEnumerable<object> Build(string buildingSpec, int? x, int? y, bool force = false)
        {
            var o = new Outcome();
            var farm = Game1.getFarm();
            // 用木匠界面对象拿到可建列表、价格和扣材料的逻辑（不显示界面）。
            // 法师（拿回魔法墨水以后）建方尖碑、黄金时钟、祝尼魔小屋这些，用法师塔里那本书，和 Robin 是两份图纸
            bool hasWizard = Player.mailReceived.Contains("hasPickedUpMagicInk") || Player.hasMagicInk;
            var menus = new List<(string builder, CarpenterMenu menu)> { ("Robin", new CarpenterMenu("Robin", farm)) };
            if (hasWizard)
                menus.Add(("Wizard", new CarpenterMenu("Wizard", farm)));
            string builder = null;
            CarpenterMenu menu = null;
            CarpenterMenu.BlueprintEntry bp = null;
            if (!string.IsNullOrWhiteSpace(buildingSpec))
            {
                foreach (bool exact in new[] { true, false })
                {
                    foreach (var (who, m) in menus)
                    {
                        bp = m.Blueprints.FirstOrDefault(b => exact
                            ? b.Id.Equals(buildingSpec, StringComparison.OrdinalIgnoreCase) || b.DisplayName.Equals(buildingSpec, StringComparison.OrdinalIgnoreCase)
                            : b.Id.Contains(buildingSpec, StringComparison.OrdinalIgnoreCase) || b.DisplayName.Contains(buildingSpec, StringComparison.OrdinalIgnoreCase));
                        if (bp != null)
                        {
                            (builder, menu) = (who, m);
                            break;
                        }
                    }
                    if (bp != null)
                        break;
                }
            }
            if (bp == null)
            {
                var sections = menus.Select(pair => (pair.builder == "Robin" ? "Robin 可以建：\n" : "法师可以建（魔法建筑，当场建好）：\n") + string.Join("\n", pair.menu.Blueprints.Select(b =>
                {
                    pair.menu.SetNewActiveBlueprint(b);
                    string ok = pair.menu.DoesFarmerHaveEnoughResourcesToBuild() ? "【材料够】" : "";
                    string up = b.IsUpgrade ? $"（由 {b.UpgradeFrom} 升级）" : "";
                    return $"{b.DisplayName}({b.Id}){up}{ok}：{BlueprintCost(b)}";
                })));
                string wizardNote = hasWizard ? "" : "\n（方尖碑、黄金时钟要找法师建：先做完法师给的魔法墨水任务，从女巫沼泽拿回墨水）";
                yield return ActionResult.Fail((string.IsNullOrWhiteSpace(buildingSpec) ? "" : $"没有叫“{buildingSpec}”的图纸。") + string.Join("\n", sections) + "\n（升级农舍用 upgrade_house）" + wizardNote);
                yield break;
            }
            bool wizard = builder == "Wizard";
            string builderName = wizard ? "法师" : "Robin";
            // 出门前先查：在施工、没这个图纸、材料或钱不够，都不用跑这一趟
            if (Game1.IsThereABuildingUnderConstruction(builder) || (!wizard && Player.daysUntilHouseUpgrade.Value > 0))
            {
                yield return ActionResult.Fail($"{builderName} 正在施工，同一时间只能建一个，等完工再来");
                yield break;
            }
            menu.SetNewActiveBlueprint(bp);
            if (!menu.DoesFarmerHaveEnoughResourcesToBuild())
            {
                yield return ActionResult.Fail($"{bp.DisplayName} 还缺：{BlueprintMissing(bp)}（需要 {BlueprintCost(bp)}）。这次没有出门；材料在箱子里就先 take_items 拿上");
                yield break;
            }

            if (wizard)
            {
                // 法师塔里那本建筑书，法师在不在都能用
                if (Loc.Name != "WizardHouse")
                    foreach (var s in TravelInner("WizardHouse", o))
                        yield return s;
                if (Loc.Name != "WizardHouse")
                {
                    yield return ActionResult.Fail("去法师塔失败：" + o.Message);
                    yield break;
                }
            }
            else
            {
                foreach (var s in GoToShopkeeper("ScienceHouse", "Robin", "Carpenter", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail(o.Message);
                    yield break;
                }
            }
            // 路上可能花了钱、用掉了材料：到了再核对一次
            menu.SetNewActiveBlueprint(bp);
            if (!menu.DoesFarmerHaveEnoughResourcesToBuild())
            {
                yield return ActionResult.Fail($"{bp.DisplayName} 还缺：{BlueprintMissing(bp)}（需要 {BlueprintCost(bp)}）");
                yield break;
            }

            // 和游戏一样：确认之后到农场上选位置
            if (Loc != farm)
            {
                foreach (var s in TravelInner("Farm", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("去农场失败：" + o.Message);
                    yield break;
                }
            }

            if (bp.IsUpgrade)
            {
                var target = farm.buildings.FirstOrDefault(b => b.buildingType.Value == bp.UpgradeFrom && !b.isUnderConstruction() && b.daysUntilUpgrade.Value <= 0);
                if (target == null)
                {
                    yield return ActionResult.Fail($"农场上没有可以升级的 {bp.UpgradeFrom}");
                    yield break;
                }
                // 与木匠界面“升级”操作一致
                menu.ConsumeResources();
                target.upgradeName.Value = bp.Id;
                target.daysUntilUpgrade.Value = Math.Max(bp.BuildDays, 1);
                target.showUpgradeAnimation(farm);
                Game1.playSound("axe");
                if (bp.BuildDays < 1)
                    target.FinishConstruction();
                else
                    Game1.netWorldState.Value.MarkUnderConstruction(builder, target);
                yield return ActionResult.Success($"{builderName} 开始把 ({target.tileX.Value},{target.tileY.Value}) 的 {bp.UpgradeFrom} 升级成 {bp.DisplayName}，{bp.BuildDays} 天后完工，花费 {BlueprintCost(bp)}");
                yield break;
            }

            var data = bp.Data;
            Point? spot = null;
            if (x.HasValue && y.HasValue)
            {
                if (!CanPlace(farm, data, new Point(x.Value, y.Value)))
                {
                    yield return ActionResult.Fail($"({x},{y}) 放不下 {bp.DisplayName}（{data.Size.X}x{data.Size.Y}，坐标是左上角；不能压到建筑、石头树木、耕地，必须在可建造区域）。不传坐标我会自动找位置");
                    yield break;
                }
                // 放得下但规划不好（贴着别的建筑、堵门、压田边）：说清楚，给出自动规划的位置
                if (!force && BuildingLayoutProblem(farm, new Point(x.Value, y.Value), data.Size) is string layout)
                {
                    var planned = PlanBuildingSpot(farm, data, bp.Id);
                    yield return ActionResult.Fail($"({x},{y}) 放 {bp.DisplayName} 不合适：{layout}。"
                        + (planned is Point p ? $"建议放在 ({p.X},{p.Y})（不传坐标就用这里）" : "不传坐标我会自动找位置")
                        + $"。{ForceHint}");
                    yield break;
                }
                spot = new Point(x.Value, y.Value);
            }
            else
            {
                // 自动规划：同类建筑排成一排、四周留路、门口空出来、不贴着田；实在没有这种位置再退回“放得下就行”
                spot = PlanBuildingSpot(farm, data, bp.Id);
                if (spot == null)
                {
                    var house = farm.GetMainFarmHouseEntry();
                    int w = Pathfinder.MapWidth(farm), h = Pathfinder.MapHeight(farm);
                    var candidates = new List<Point>();
                    for (int cx = 1; cx < w - data.Size.X; cx++)
                        for (int cy = 1; cy < h - data.Size.Y; cy++)
                            candidates.Add(new Point(cx, cy));
                    spot = candidates.Where(c => Util.Manhattan(c, house) >= 6).OrderBy(c => Util.Manhattan(c, house))
                        .Select(c => (Point?)c).FirstOrDefault(c => CanPlace(farm, data, c.Value));
                }
                if (spot == null)
                {
                    yield return ActionResult.Fail($"农场上找不到能放下 {bp.DisplayName}（{data.Size.X}x{data.Size.Y}）的空地，先 clear_area 清出一块");
                    yield break;
                }
            }

            if (!farm.buildStructure(bp.Id, new Vector2(spot.Value.X, spot.Value.Y), Player, out var constructed, bp.MagicalConstruction))
            {
                yield return ActionResult.Fail($"在 ({spot.Value.X},{spot.Value.Y}) 建造失败");
                yield break;
            }
            // 与木匠界面 tryToBuild 成功后的处理一致
            if (constructed.isUnderConstruction())
                Game1.netWorldState.Value.MarkUnderConstruction(builder, constructed);
            menu.ConsumeResources();
            Game1.playSound(wizard ? "wand" : "axe");
            yield return ActionResult.Success(constructed.isUnderConstruction()
                ? $"{builderName} 开始在农场 ({spot.Value.X},{spot.Value.Y}) 建 {bp.DisplayName}（{data.Size.X}x{data.Size.Y}），{bp.BuildDays} 天后完工，花费 {BlueprintCost(bp)}"
                : $"{builderName} 在农场 ({spot.Value.X},{spot.Value.Y}) 建好了 {bp.DisplayName}（{data.Size.X}x{data.Size.Y}），花费 {BlueprintCost(bp)}"
                  + (bp.Id.Contains("Obelisk") ? "。方尖碑 interact 一下就传送" : ""));
        }

        public IEnumerable<object> UpgradeHouse()
        {
            if (Player.daysUntilHouseUpgrade.Value > 0)
            {
                yield return ActionResult.Fail($"房子正在升级，还要 {Player.daysUntilHouseUpgrade.Value} 天");
                yield break;
            }
            var o = new Outcome();
            foreach (var s in GoToShopkeeper("ScienceHouse", "Robin", "Carpenter", o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail(o.Message);
                yield break;
            }
            if (Game1.IsThereABuildingUnderConstruction("Robin"))
            {
                yield return ActionResult.Fail("Robin 正在施工别的建筑，等完工再来");
                yield break;
            }
            int level = Player.houseUpgradeLevel.Value;
            string need = level switch
            {
                0 => "10000g + 450 木材（第一次升级：有厨房）",
                1 => "65000g + 100 硬木（第二次：育婴室）",
                2 => "100000g（第三次：地窖）",
                _ => null
            };
            if (need == null)
            {
                yield return ActionResult.Fail("房子已经升到最高级了");
                yield break;
            }
            int moneyBefore = Player.Money;
            // 游戏自己的“升级房子”确认逻辑：检查钱和材料、扣除、Robin 说话
            reflection.GetMethod(Loc, "houseUpgradeAccept").Invoke();
            foreach (var s in WaitTicks(20))
                yield return s;
            string said = Game1.activeClickableMenu is DialogueBox db ? db.getCurrentString() : "";
            foreach (var s in CloseDialogues())
                yield return s;
            yield return Player.daysUntilHouseUpgrade.Value > 0
                ? ActionResult.Success($"Robin 开始升级房子（花费 {moneyBefore - Player.Money}g 和材料），{Player.daysUntilHouseUpgrade.Value} 天后完工")
                : ActionResult.Fail($"没能升级（需要 {need}）：{said}");
        }
    }
}
