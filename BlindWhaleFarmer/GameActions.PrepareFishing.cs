using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        internal const int DefaultFishingBaitCount = 30;

        internal sealed record FishingOffer(int Slot, int Price, int Count);
        internal sealed record FishingLoadout(FishingOffer Rod, FishingOffer Bait, FishingOffer[] Tackle);

        /// <summary>按鱼竿、鱼饵、钓具的顺序用预算。候选鱼竿和钓具已分别按使用优先级排好。</summary>
        internal static FishingOffer[] PlanFishingPurchases(int budget, FishingLoadout current, IEnumerable<FishingLoadout> upgrades)
        {
            var loadout = upgrades.FirstOrDefault(option => option.Rod.Price <= budget) ?? current;
            var plan = new List<FishingOffer>();
            if (loadout.Rod is { } rod)
            {
                plan.Add(rod);
                budget -= rod.Price;
            }
            if (loadout.Bait is { } bait)
            {
                int count = bait.Price == 0 ? bait.Count : Math.Min(bait.Count, budget / bait.Price);
                if (count > 0)
                {
                    plan.Add(bait with { Count = count });
                    budget -= bait.Price * count;
                }
            }
            var tackle = loadout.Tackle.FirstOrDefault(option => option.Price <= budget);
            if (tackle != null)
                plan.Add(tackle);
            return plan.ToArray();
        }

        public IEnumerable<object> PrepareFishing(int? budget, bool buyRod, int baitCount, bool confirm)
        {
            const string shopId = "FishShop";
            if (budget < 0 || baitCount < 0 || baitCount > 999)
            {
                yield return ActionResult.Fail("budget 要大于等于 0，bait_count 要在 0～999 之间");
                yield break;
            }
            var shop = Game1.activeClickableMenu as ShopMenu;
            if ((shop?.ShopId != shopId || Loc.Name != shopId || shop.heldItem != null) && Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }

            try
            {
                var rod = BestFishingRod();
                if (rod == null && !buyRod)
                {
                    yield return ActionResult.Fail("背包里没有鱼竿，buy_rod=false 禁止买竿");
                    yield break;
                }
                PrepareRodAttachments(rod);
                int limit = Math.Min(budget ?? int.MaxValue, Math.Max(0, Player.Money));
                if (limit == 0 || (!buyRod && FishingBaitShortage(rod, baitCount) == 0 && !FishingTackleMissing(rod)))
                {
                    yield return rod == null ? ActionResult.Fail("背包里没有鱼竿，预算或余额为 0")
                        : ActionResult.Success(FishingEquipmentSummary(rod) + (limit == 0 ? "。预算或余额为 0，本次没有采购" : "。背包里的装备已配好"));
                    yield break;
                }

                if (shop == null)
                {
                    var travel = new Outcome();
                    foreach (var step in TravelInner(shopId, travel))
                        yield return step;
                    if (!travel.Ok)
                    {
                        yield return ActionResult.Fail(travel.Message + "。" + FishingEquipmentSummary(rod));
                        yield break;
                    }
                    ActionResult opened = null;
                    foreach (var step in OpenShop(shopId))
                    {
                        if (step is ActionResult result)
                            opened = result;
                        else
                            yield return step;
                    }
                    if (opened?.Ok != true || Game1.activeClickableMenu is not ShopMenu openedShop || openedShop.ShopId != shopId)
                    {
                        yield return ActionResult.Fail((opened?.Message ?? "没能打开威利鱼店") + "。" + FishingEquipmentSummary(rod));
                        yield break;
                    }
                    shop = openedShop;
                }

                if (shop.currency != 0 || shop.readOnly)
                {
                    yield return ActionResult.Fail("威利鱼店当前不能用金币购买。" + FishingEquipmentSummary(rod));
                    yield break;
                }

                rod = BestFishingRod();
                PrepareRodAttachments(rod);
                var catalog = shop.forSale.Where(shop.itemPriceAndStock.ContainsKey).ToArray();
                var offers = new List<(Item Item, FishingOffer Offer)>();
                for (int i = 0; i < catalog.Length; i++)
                {
                    if (catalog[i] is not Item { IsRecipe: false, Stack: 1 } item
                        || !CanBuyFishingOffer(shop, item, 1))
                        continue;
                    var stock = shop.itemPriceAndStock[item];
                    offers.Add((item, new FishingOffer(i, stock.Price, Math.Min(stock.Stock, item.maximumStackSize()))));
                }

                FishingLoadout Loadout(FishingRod target, FishingOffer upgrade)
                {
                    int missing = FishingBaitShortage(target, baitCount);
                    var bait = missing == 0 ? null : offers
                        .Where(o => o.Item is SObject obj && obj.Category == SObject.baitCategory && target.canThisBeAttached(obj))
                        .OrderBy(o => o.Offer.Price).Select(o => o.Offer with { Count = Math.Min(missing, o.Offer.Count) }).FirstOrDefault();
                    var tackle = !FishingTackleMissing(target) ? Array.Empty<FishingOffer>() : offers
                        .Where(o => o.Item is SObject obj && obj.Category == SObject.tackleCategory && target.canThisBeAttached(obj))
                        .OrderByDescending(o => o.Item.QualifiedItemId == "(O)694") // 优先陷阱浮标，钱不够就挑便宜的
                        .ThenBy(o => o.Offer.Price).Select(o => o.Offer with { Count = 1 }).ToArray();
                    return new FishingLoadout(upgrade, bait, tackle);
                }

                var upgrades = buyRod && Player.freeSpotsInInventory() > 0
                    ? offers.Where(o => o.Item is FishingRod option && (rod == null || RodScore(option) > RodScore(rod)))
                        .OrderByDescending(o => RodScore((FishingRod)o.Item)).ThenBy(o => o.Offer.Price)
                        .Select(o => Loadout((FishingRod)o.Item, o.Offer with { Count = 1 }))
                    : Enumerable.Empty<FishingLoadout>();
                limit = Math.Min(limit, Math.Max(0, Player.Money));
                var plan = PlanFishingPurchases(limit, Loadout(rod, null), upgrades);
                string items = string.Join("、", plan.Select(o => $"{catalog[o.Slot].DisplayName} ×{o.Count}"));
                int cost = plan.Sum(o => o.Price * o.Count);
                if (!CheckSharedMoneySpend(cost, items, confirm, out string message))
                {
                    yield return ActionResult.Fail(message + "。" + FishingEquipmentSummary(rod));
                    yield break;
                }

                var purchases = new List<string>();
                foreach (var offer in plan)
                {
                    var item = (Item)catalog[offer.Slot];
                    if (Game1.activeClickableMenu != shop || shop.currency != 0 || shop.heldItem != null
                        || !CanBuyFishingOffer(shop, item, offer.Count) || shop.itemPriceAndStock[item].Price != offer.Price
                        || Player.Money < offer.Price * offer.Count)
                    {
                        yield return ActionResult.Fail($"鱼店、库存、价格或余额变了，停在 {item.DisplayName}。"
                            + FishingPurchaseSummary(rod, purchases));
                        yield break;
                    }
                    if (Player.freeSpotsInInventory() == 0 && Player.Items
                        .Where(i => i?.canStackWith(item) == true).Sum(i => i.maximumStackSize() - i.Stack) < offer.Count)
                    {
                        yield return ActionResult.Fail($"背包放不下 {item.DisplayName} ×{offer.Count}，先腾出位置。"
                            + FishingPurchaseSummary(rod, purchases));
                        yield break;
                    }

                    // 前一笔买完后商品可能移出列表，重新找 buy 用的序号。
                    int slot = shop.forSale.Where(shop.itemPriceAndStock.ContainsKey).ToList().IndexOf(item);
                    ActionResult bought = null;
                    foreach (var step in Buy(slot.ToString(), offer.Count, force: true))
                    {
                        if (step is ActionResult result)
                            bought = result;
                        else
                            yield return step;
                    }
                    rod = BestFishingRod();
                    PrepareRodAttachments(rod);
                    if (bought?.Ok != true)
                    {
                        yield return ActionResult.Fail((bought?.Message ?? $"没买成 {item.DisplayName}") + "。"
                            + FishingPurchaseSummary(rod, purchases));
                        yield break;
                    }
                    purchases.Add($"{item.DisplayName} ×{offer.Count}（{offer.Price * offer.Count}g）");
                }

                string summary = FishingPurchaseSummary(rod, purchases);
                if (purchases.Count == 0)
                    summary += $"。预算上限 {limit}g，当前没有需要且买得起的商品，或背包没有买竿的空位";
                yield return rod == null ? ActionResult.Fail(summary) : ActionResult.Success(summary);
            }
            finally
            {
                // 配完装备、等待同意或中途停下时收起鱼店菜单，下一步可以直接走路或钓鱼。
                if (shop != null && Game1.activeClickableMenu == shop && shop.readyToClose())
                    shop.exitThisMenu(playSound: false);
            }
        }

        private static bool CanBuyFishingOffer(ShopMenu shop, Item item, int count)
        {
            int index = shop.forSale.IndexOf(item);
            return index >= 0 && !shop.readOnly && shop.itemPriceAndStock.TryGetValue(item, out var stock)
                && stock.Stock >= count && stock.Price >= 0 && stock.TradeItem == null
                && (item.IsInfiniteStock() || item.CanBuyItem(Player))
                && (shop.canPurchaseCheck == null || shop.canPurchaseCheck(index));
        }

        private static FishingRod BestFishingRod()
        {
            int index = BestRodIndex();
            return index < 0 ? null : (FishingRod)Player.Items[index];
        }

        private static IEnumerable<SObject> FishingAccessories(FishingRod rod, int category)
        {
            foreach (var item in Player.Items)
            {
                if (item is SObject obj && obj.Category == category && rod.canThisBeAttached(obj))
                    yield return obj;
                if (item is FishingRod other && other != rod)
                    foreach (var accessory in other.attachments)
                        if (accessory?.Category == category && rod.canThisBeAttached(accessory))
                            yield return accessory;
            }
        }

        private static int FishingBaitOwned(FishingRod rod) =>
            (rod.GetBait()?.Stack ?? 0) + FishingAccessories(rod, SObject.baitCategory).Sum(item => item.Stack);

        private static int FishingBaitShortage(FishingRod rod, int count) =>
            rod?.CanUseBait() == true ? Math.Max(0, count - FishingBaitOwned(rod)) : 0;

        private static bool FishingTackleMissing(FishingRod rod) =>
            rod?.CanUseTackle() == true && rod.GetTackle().All(t => t == null)
                && !FishingAccessories(rod, SObject.tackleCategory).Any();

        private static void PrepareRodAttachments(FishingRod rod)
        {
            if (rod == null)
                return;
            FillRodAttachments(rod);

            // 换了竿时也能沿用其他背包鱼竿上的饵和钓具，只拿能装进空槽的。
            foreach (var other in Player.Items.OfType<FishingRod>().Where(other => other != rod))
            {
                for (int i = 0; i < other.attachments.Length; i++)
                {
                    if (other.attachments[i] is not { } accessory || !rod.canThisBeAttached(accessory))
                        continue;
                    bool slotEmpty = accessory.Category == SObject.baitCategory && rod.GetBait() == null
                        || accessory.Category == SObject.tackleCategory && rod.GetTackle().All(t => t == null);
                    if (!slotEmpty)
                        continue;
                    other.attachments[i] = null;
                    other.attachments[i] = rod.attach(accessory);
                }
            }

            // 竿上已有同款鱼饵时补进当前槽。不同鱼饵留作备用。
            if (rod.GetBait() is not { } bait)
                return;
            for (int i = 0; i < Player.Items.Count && bait.Stack < bait.maximumStackSize(); i++)
            {
                if (Player.Items[i] is not SObject loose || !bait.canStackWith(loose))
                    continue;
                Player.Items[i] = null;
                Player.Items[i] = rod.attach(loose);
            }
        }

        private static string FishingEquipmentSummary(FishingRod rod)
        {
            if (rod == null)
                return "背包里没有鱼竿";
            string summary = $"当前鱼竿 {rod.DisplayName}";
            if (rod.CanUseBait())
                summary += $"，已装鱼饵 {rod.GetBait()?.Stack ?? 0} 个（背包和鱼竿合计 {FishingBaitOwned(rod)} 个）";
            if (rod.CanUseTackle())
                summary += "，钓具 " + (rod.GetTackle().FirstOrDefault(t => t != null)?.DisplayName ?? "未装");
            return summary;
        }

        private static string FishingPurchaseSummary(FishingRod rod, List<string> purchases) =>
            (purchases.Count == 0 ? "" : $"已买 {string.Join("、", purchases)}。") + FishingEquipmentSummary(rod);
    }
}
