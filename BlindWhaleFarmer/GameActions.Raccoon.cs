using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Menus;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 1.6 森林大树桩与浣熊任务 ─────────────────────────

        internal static string NormalizeRaccoonAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action))
                return "status";
            return action.Trim().ToLowerInvariant() switch
            {
                "status" or "info" or "check" or "查看" or "状态" => "status",
                "fix" or "repair" or "修" or "修复" or "修树桩" => "fix",
                "donate" or "submit" or "deliver" or "give" or "交" or "提交" or "交付" => "donate",
                "shop" or "buy" or "商店" => "shop",
                _ => null
            };
        }

        internal static string FormatRaccoonState(bool treeFallen, bool stumpFixed, bool hasMrRaccoon, bool hasMrsRaccoon,
            int timesFed, int daysSinceLast, bool bundleReady,
            IReadOnlyList<(string label, int have, int need, bool done)> reqs, int hardwoodCount)
        {
            if (!treeFallen)
                return "煤矿森林北边的大树还没倒（完成茶水间收集包等前期进度后，暴风雨夜会吹倒它），目前还没有浣熊任务。";
            if (!stumpFixed)
                return $"煤矿森林北边大树已倒、大树桩待修复：需要 100 个硬木（当前背包 {hardwoodCount}/100）。够了就用 raccoon action=fix 去修（在 Forest (56,6)）。";
            if (!hasMrRaccoon)
                return "大树桩已经修好了！浣熊先生明天就会搬进来，明天再用 raccoon 查看需求。";

            var sb = new System.Text.StringBuilder();
            sb.Append($"浣熊家族进度：已完成 {timesFed} 轮");
            if (hasMrsRaccoon)
                sb.Append("（浣熊太太商店已解锁，可用 raccoon action=shop 打开以物易物商店）");
            if (timesFed > 0 && daysSinceLast < 7)
            {
                sb.Append($"。本轮刚交完不久，还需要再等 {7 - daysSinceLast} 天（满 7 天）才会发布下一轮需求。");
                return sb.ToString();
            }
            if (!bundleReady || reqs == null || reqs.Count == 0)
            {
                sb.Append("。新一轮需求还没点开查看：调用 raccoon action=status（会自动去 Forest 问浣熊先生）或 raccoon action=donate 即可查看并交付。");
                return sb.ToString();
            }
            sb.Append("。本轮需要 2 样东西：");
            sb.Append(string.Join("；", reqs.Select(r =>
                r.done ? $"{r.label} [已交]"
                : $"{r.label}（背包有 {r.have}/{r.need}）")));
            if (reqs.Any(r => !r.done && r.have >= r.need))
                sb.Append("。背包里已有可交的物品，用 raccoon action=donate 去交付！");
            return sb.ToString();
        }

        private static string RaccoonIngredientLabel(BundleIngredientDescription ing)
        {
            if (ing.preservesId != null)
            {
                try
                {
                    var item = Utility.CreateFlavoredItem(ing.id, ing.preservesId, ing.quality, ing.stack);
                    if (item != null && !item.DisplayName.Contains("Error", StringComparison.OrdinalIgnoreCase))
                        return ing.stack > 1 ? $"{item.DisplayName} x{ing.stack}" : item.DisplayName;
                }
                catch { }
            }
            return Knowledge.IngredientName(ing);
        }

        private static int CountMatchingForBundle(Bundle bundle, BundleIngredientDescription ing)
        {
            int count = 0;
            foreach (var it in Player.Items)
            {
                if (it == null)
                    continue;
                if (ing.completed)
                    continue;
                // IsValidItemForThisIngredientDescription 同时验证风味 preservesId、品质、品类和 ID
                if (bundle.IsValidItemForThisIngredientDescription(it, ing))
                    count += it.Stack;
            }
            return count;
        }

        private static IReadOnlyList<(string label, int have, int need, bool done)> ReadRaccoonBundleReqs()
        {
            var bundle = Raccoon.GetBundle();
            return bundle.ingredients.Select(ing =>
            {
                int have = ing.completed ? ing.stack : CountMatchingForBundle(bundle, ing);
                return (RaccoonIngredientLabel(ing), have, ing.stack, ing.completed);
            }).ToList();
        }

        public IEnumerable<object> RaccoonAction(string action)
        {
            string act = NormalizeRaccoonAction(action);
            if (act == null)
            {
                yield return ActionResult.Fail($"不认识 action=\"{action}\"，只支持 status（查看进度和所需物品）、fix（交100硬木修树桩）、donate（交付当前需求并领奖）、shop（开浣熊太太商店）");
                yield break;
            }

            var forest = Game1.getLocationFromName("Forest") as Forest;
            if (forest == null)
            {
                yield return ActionResult.Fail("找不到煤矿森林（Forest）地图");
                yield break;
            }

            bool treeFallen = Game1.MasterPlayer.mailReceived.Contains("raccoonTreeFallen");
            bool stumpFixed = forest.stumpFixed.Value;
            var mr = forest.getCharacterFromName("Raccoon") as Raccoon;
            var mrs = forest.getCharacterFromName("MrsRaccoon") as Raccoon;
            int timesFed = Game1.netWorldState.Value.TimesFedRaccoons;
            int daysSince = Game1.netWorldState.Value.Date.TotalDays - Game1.netWorldState.Value.DaysPlayedWhenLastRaccoonBundleWasFinished;
            bool cooling = timesFed > 0 && daysSince < 7;
            int hardwood = Player.Items.CountId("(O)709");

            var o = new Outcome();

            if (act == "fix")
            {
                if (!treeFallen)
                {
                    yield return ActionResult.Fail("煤矿森林的大树还没倒，现在修不了树桩（完成茶水间收集包后暴风雨夜会倒）");
                    yield break;
                }
                if (stumpFixed)
                {
                    yield return ActionResult.Success(FormatRaccoonState(treeFallen, stumpFixed, mr != null, mrs != null, timesFed, daysSince,
                        Game1.netWorldState.Value.SeasonOfCurrentRacconBundle >= 0, null, hardwood));
                    yield break;
                }
                if (hardwood < 100)
                {
                    yield return ActionResult.Fail($"修大树桩需要 100 个硬木，背包里只有 {hardwood} 个（还差 {100 - hardwood} 个；可从箱子拿 take_items 或去秘密森林砍大树桩）");
                    yield break;
                }
                if (Loc != forest)
                {
                    foreach (var s in TravelInner("Forest", o))
                        yield return s;
                    if (!o.Ok || Loc != forest)
                    {
                        yield return ActionResult.Fail($"走不到煤矿森林：{o.Message}");
                        yield break;
                    }
                }
                // 煤矿森林地图上可能有多个带 FixRaccoonStump 的格（含不可达的旧格），筛出旁边能站人的可达格
                var stumpByStand = new Dictionary<Point, Point>();
                int mw = Pathfinder.MapWidth(forest), mh = Pathfinder.MapHeight(forest);
                for (int tx = 0; tx < mw; tx++)
                    for (int ty = 0; ty < mh; ty++)
                        if (forest.doesTileHaveProperty(tx, ty, "Action", "Buildings")?.Trim() == "FixRaccoonStump")
                        {
                            var t = new Point(tx, ty);
                            foreach (var st in Pathfinder.AdjacentStandTiles(forest, t))
                                stumpByStand.TryAdd(st, t);
                        }
                Point stumpTile;
                if (stumpByStand.Count > 0)
                {
                    foreach (var s in GoToAny(stumpByStand.Keys, o, "森林大树桩"))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"走不到大树桩：{o.Message}");
                        yield break;
                    }
                    stumpTile = stumpByStand.TryGetValue(Player.TilePoint, out var matched) ? matched : new Point(56, 6);
                }
                else
                {
                    stumpTile = new Point(56, 6);
                    foreach (var s in GoAdjacent(stumpTile, o))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"走不到大树桩 ({stumpTile.X},{stumpTile.Y})：{o.Message}");
                        yield break;
                    }
                }
                Face(stumpTile);
                forest.checkAction(new xLocation(stumpTile.X, stumpTile.Y), Game1.viewport, Player);
                for (int t = 0; t < 60 && Game1.activeClickableMenu is not DialogueBox; t++)
                    yield return null;
                if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } ask)
                {
                    int yes = Array.FindIndex(ask.responses, r => r.responseKey == "Yes");
                    foreach (var s in Dialogue(yes < 0 ? 0 : yes))
                        if (s is not ActionResult)
                            yield return s;
                }
                for (int t = 0; t < 480 && (!forest.stumpFixed.Value || Game1.fadeToBlack || Game1.globalFade || Game1.freezeControls); t++)
                    yield return null;
                foreach (var s in WaitTicks(15))
                    yield return s;
                for (int k = 0; k < 5 && Game1.activeClickableMenu is DialogueBox { isQuestion: false }; k++)
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;
                if (forest.stumpFixed.Value)
                    yield return ActionResult.Success("交了 100 个硬木修好了森林大树桩！明天浣熊先生就会搬进来，明天再用 raccoon 查看它的需求");
                else
                    yield return ActionResult.Fail("和大树桩交互后还没修好，可能还在播剧情，可以稍后再看看");
                yield break;
            }

            if (act == "shop")
            {
                if (mrs == null)
                {
                    yield return ActionResult.Fail("浣熊太太还没在树桩旁边开店（要先完成浣熊先生的前几轮需求并等家人搬来）");
                    yield break;
                }
                if (Loc != forest)
                {
                    foreach (var s in TravelInner("Forest", o))
                        yield return s;
                    if (!o.Ok || Loc != forest)
                    {
                        yield return ActionResult.Fail($"走不到煤矿森林：{o.Message}");
                        yield break;
                    }
                }
                foreach (var s in GoAdjacent(mrs.TilePoint, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"走不到浣熊太太旁边：{o.Message}");
                    yield break;
                }
                Face(mrs.TilePoint);
                for (int t = 0; t < 30 && mrs.shakeTimer > 0; t++)
                    yield return null;
                mrs.checkAction(Player, forest);
                for (int t = 0; t < 90 && Game1.activeClickableMenu is not ShopMenu; t++)
                    yield return null;
                if (Game1.activeClickableMenu is ShopMenu)
                    yield return ActionResult.Success("已打开浣熊太太商店（用 buy 购买、close_menu 离开）：\n" + observer.DescribeMenu());
                else
                    yield return ActionResult.Fail("和浣熊太太交互后没有打开商店界面");
                yield break;
            }

            // 谈到 status 或 donate 之前：如果在等冷却、未修好或还没住进来，可以直接回状态
            if (!treeFallen || !stumpFixed || mr == null || cooling)
            {
                string stateMsg = FormatRaccoonState(treeFallen, stumpFixed, mr != null, mrs != null, timesFed, daysSince,
                    Game1.netWorldState.Value.SeasonOfCurrentRacconBundle >= 0, null, hardwood);
                yield return act == "status" ? ActionResult.Success(stateMsg) : ActionResult.Fail(stateMsg);
                yield break;
            }

            // 如果 SeasonOfCurrentRacconBundle 已初始化且只查 status，不用跑过去可以直接读
            if (act == "status" && Game1.netWorldState.Value.SeasonOfCurrentRacconBundle >= 0)
            {
                var reqs = ReadRaccoonBundleReqs();
                yield return ActionResult.Success(FormatRaccoonState(treeFallen, stumpFixed, true, mrs != null, timesFed, daysSince,
                    true, reqs, hardwood));
                yield break;
            }

            // 需要走到森林找浣熊先生对话，打开一次便条（初始化 SeasonOfCurrentRacconBundle 并/或提交物品）
            if (Loc != forest)
            {
                foreach (var s in TravelInner("Forest", o))
                    yield return s;
                if (!o.Ok || Loc != forest)
                {
                    yield return ActionResult.Fail($"走不到煤矿森林：{o.Message}");
                    yield break;
                }
            }
            mr = forest.getCharacterFromName("Raccoon") as Raccoon;
            if (mr == null)
            {
                yield return ActionResult.Fail("到森林后没找到浣熊先生");
                yield break;
            }
            foreach (var s in GoAdjacent(mr.TilePoint, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail($"走不到浣熊先生旁边：{o.Message}");
                yield break;
            }
            Face(mr.TilePoint);
            for (int t = 0; t < 30 && mr.shakeTimer > 0; t++)
                yield return null;
            mr.checkAction(Player, forest);
            for (int t = 0; t < 120 && Game1.activeClickableMenu is not (DialogueBox or JunimoNoteMenu); t++)
                yield return null;
            for (int k = 0; k < 6 && Game1.activeClickableMenu is DialogueBox; k++)
            {
                foreach (var s in Dialogue(null))
                    if (s is not ActionResult)
                        yield return s;
                for (int t = 0; t < 90 && Game1.activeClickableMenu is not (DialogueBox or JunimoNoteMenu); t++)
                    yield return null;
            }
            for (int t = 0; t < 90 && Game1.activeClickableMenu is not JunimoNoteMenu; t++)
                yield return null;

            if (Game1.activeClickableMenu is not JunimoNoteMenu menu)
            {
                if (Game1.netWorldState.Value.SeasonOfCurrentRacconBundle >= 0)
                {
                    var reqsFallback = ReadRaccoonBundleReqs();
                    string fallbackMsg = FormatRaccoonState(treeFallen, stumpFixed, true, mrs != null, timesFed, daysSince, true, reqsFallback, hardwood);
                    yield return act == "status" ? ActionResult.Success(fallbackMsg) : ActionResult.Fail("没有打开浣熊交付界面：" + fallbackMsg);
                }
                else
                    yield return ActionResult.Fail("和浣熊先生对话后没有打开需求界面，可以再试一次");
                yield break;
            }

            if (act == "status")
            {
                menu.exitThisMenu();
                var reqs = ReadRaccoonBundleReqs();
                yield return ActionResult.Success(FormatRaccoonState(treeFallen, stumpFixed, true, mrs != null, timesFed, daysSince,
                    true, reqs, hardwood));
                yield break;
            }

            // donate：只把非玩家明确标记【留着】的匹配物品放进浣熊需求格（单包界面里收集包在 currentPageBundle，bundles 列表为空）
            var bundle = menu.currentPageBundle ?? menu.bundles.FirstOrDefault();
            if (bundle == null)
            {
                menu.exitThisMenu();
                yield return ActionResult.Fail("浣熊界面里没有找到当前需求收集包");
                yield break;
            }
            var deposited = new List<string>();
            var reservedSkipped = new List<string>();
            for (int i = 0; i < Player.Items.Count; i++)
            {
                var item = Player.Items[i];
                if (item == null)
                    continue;
                foreach (var slot in menu.ingredientSlots)
                {
                    if (item == null || slot.item != null || !bundle.canAcceptThisItem(item, slot))
                        continue;
                    if (ReservedChecker()(item) == PlayerReserved)
                    {
                        reservedSkipped.Add(item.DisplayName);
                        break;
                    }
                    string label = item.DisplayName;
                    item = bundle.tryToDepositThisItem(item, slot, "LooseSprites\\raccoon_bundle_menu", menu);
                    Player.Items[i] = item;
                    deposited.Add(label);
                }
            }

            int beforeFed = Game1.netWorldState.Value.TimesFedRaccoons;
            static (string id, string name, int quality, int quantity)[] RaccoonSnap() =>
                InventoryDiff.Snapshot(Player.Items.Where(it => it != null)
                    .Select(it => (it.QualifiedItemId, it.DisplayName, it.Quality, it.Stack)));
            var snapBefore = RaccoonSnap();
            reflection.GetMethod(menu, "checkIfBundleIsComplete").Invoke();

            if (Game1.netWorldState.Value.TimesFedRaccoons > beforeFed || bundle.complete)
            {
                for (int t = 0; t < 360 && Game1.activeClickableMenu is JunimoNoteMenu; t++)
                    yield return null;
                for (int t = 0; t < 120 && Game1.activeClickableMenu is not DialogueBox; t++)
                    yield return null;
                for (int k = 0; k < 5 && Game1.activeClickableMenu is DialogueBox; k++)
                {
                    foreach (var s in Dialogue(null))
                        if (s is not ActionResult)
                            yield return s;
                    foreach (var s in WaitTicks(20))
                        yield return s;
                }
                foreach (var s in WaitPlayerFree(120))
                    yield return s;
                string gained = InventoryDiff.FormatGains(InventoryDiff.Difference(snapBefore, RaccoonSnap()));
                string rewardText = string.IsNullOrEmpty(gained) ? "" : "，收到奖励：" + gained;
                yield return ActionResult.Success($"完成了浣熊第 {Game1.netWorldState.Value.TimesFedRaccoons} 轮需求（这次交了 {string.Join("、", deposited)}）{rewardText}！7 天后会发布下一轮需求。");
                yield break;
            }

            if (Game1.activeClickableMenu == menu)
                menu.exitThisMenu();
            var reqsAfter = ReadRaccoonBundleReqs();
            string leftover = string.Join("；", reqsAfter.Select(r => r.done ? $"{r.label} [已交]" : $"{r.label}（缺，背包有 {r.have}/{r.need}）"));
            if (deposited.Count > 0)
                yield return ActionResult.Success($"向浣熊交了：{string.Join("、", deposited)}。本轮还差：{leftover}");
            else
            {
                string reserveNote = reservedSkipped.Count > 0 ? $"（跳过玩家保留：{string.Join("、", reservedSkipped.Distinct())}）" : "";
                yield return ActionResult.Fail($"背包里没有浣熊现在能收的东西{reserveNote}。本轮需要：{leftover}");
            }
        }
    }
}
