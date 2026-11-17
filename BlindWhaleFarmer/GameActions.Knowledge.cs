using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using xLocation = xTile.Dimensions.Location;

namespace BlindWhaleFarmer
{
    /// <summary>送礼、博物馆捐赠、社区中心收集包。</summary>
    internal sealed partial class GameActions
    {
        private static NPC FindVillager(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            name = name.Trim();
            NPC found = null;
            Utility.ForEachVillager(n =>
            {
                if (n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = n;
                    return false;
                }
                return true;
            });
            return found;
        }

        // ───────────────────────── 送礼 ─────────────────────────

        /// <summary>查询某个村民的喜好、生日、好感，以及背包里哪些东西适合送他。不移动、不花时间。</summary>
        public IEnumerable<object> GiftInfo(string npcName)
        {
            var npc = FindVillager(npcName);
            if (npc == null)
            {
                yield return ActionResult.Fail($"不认识叫“{npcName}”的村民（用英文名，如 Abigail、Penny、Linus）");
                yield break;
            }
            var lines = new List<string>();
            string birthday = npc.Birthday_Season != null ? $"{Util.SeasonName(npc.Birthday_Season)}{npc.Birthday_Day}日" : "未知";
            lines.Add($"{npc.displayName}（{npc.Name}）生日：{birthday}{(npc.isBirthday() ? "，就是今天！生日礼物好感 x8" : "")}，现在在 {npc.currentLocation?.NameOrUniqueName ?? "?"}");
            if (Game1.player.friendshipData.TryGetValue(npc.Name, out var f))
                lines.Add($"好感 {f.Points / 250} 心（{f.Points} 点），本周已送 {f.GiftsThisWeek}/2，今天{(f.GiftsToday > 0 ? "已经送过" : "还没送")}");
            else
                lines.Add("还没认识（先和他聊一次天）");

            var (love, like, hate) = Knowledge.GiftTasteLists(npc.Name);
            if (love.Count > 0)
                lines.Add("最爱：" + string.Join("、", love));
            if (like.Count > 0)
                lines.Add("喜欢：" + string.Join("、", like));
            if (hate.Count > 0)
                lines.Add("讨厌：" + string.Join("、", hate));
            lines.Add("（以上是他个人的喜好；另外大多数人都喜欢宝石类和多数料理，讨厌垃圾）");

            var inv = Knowledge.InventoryTastes(npc);
            foreach (int taste in new[] { NPC.gift_taste_love, NPC.gift_taste_like, NPC.gift_taste_neutral, NPC.gift_taste_dislike, NPC.gift_taste_hate })
            {
                if (inv.TryGetValue(taste, out var items))
                    lines.Add($"背包里他{Knowledge.TasteName(taste)}的：{string.Join("、", items)}");
            }
            yield return ActionResult.Success(string.Join("\n", lines));
        }

        private static string NpcName(string name) => Game1.getCharacterFromName(name)?.displayName ?? name;

        /// <summary>
        /// 告示板/村民委托怎么完成、怎么交：哪些只认接任务以后新弄到的（箱子里现成的不算），交的时候用哪个工具。
        /// 以游戏源码里各任务的 OnFishCaught / OnMonsterSlain / OnItemReceived / OnNpcSocialized / OnItemOfferedToNpc 为准。
        /// </summary>
        internal static string QuestHowTo(StardewValley.Quests.Quest q)
        {
            switch (q)
            {
                case StardewValley.Quests.ItemDeliveryQuest d when d.target.Value != null:
                    return $"带着{(d.number.Value > 1 ? $" {d.number.Value} 个" : "")}{Util.ObjectName(d.ItemId.Value)}用 give_gift npc={d.target.Value} 交给{NpcName(d.target.Value)}（会先算交任务，不算送礼；箱子里现成的也行，先 take_items 拿上）";
                case StardewValley.Quests.FishingQuest f when f.target.Value != null:
                    return f.numberFished.Value >= f.numberToFish.Value
                        ? $"已经钓够了，interact npc={f.target.Value} 和{NpcName(f.target.Value)}说话就交了（不用带鱼，也别用 give_gift）"
                        : $"只认接任务以后亲手钓到的（现在 {f.numberFished.Value}/{f.numberToFish.Value}），箱子里、背包里现成的鱼不算，也不用交出去。钓够了 interact npc={f.target.Value} 和{NpcName(f.target.Value)}说话";
                case StardewValley.Quests.SlayMonsterQuest m when m.target.Value != null:
                    return m.numberKilled.Value >= m.numberToKill.Value
                        ? $"已经打够了，interact npc={m.target.Value} 和{NpcName(m.target.Value)}说话就交了"
                        : $"只认接任务以后打倒的（现在 {m.numberKilled.Value}/{m.numberToKill.Value}），打够了 interact npc={m.target.Value} 和{NpcName(m.target.Value)}说话";
                case StardewValley.Quests.ResourceCollectionQuest r when r.target.Value != null:
                    return r.numberCollected.Value >= r.number.Value
                        ? $"已经收集够了，interact npc={r.target.Value} 和{NpcName(r.target.Value)}说话就交了"
                        : $"只认接任务以后新采到/挖到的（现在 {r.numberCollected.Value}/{r.number.Value}），从箱子里拿出来的不算。够了 interact npc={r.target.Value} 和{NpcName(r.target.Value)}说话";
                case StardewValley.Quests.LostItemQuest l when l.npcName.Value != null:
                    return l.itemFound.Value
                        ? $"东西已经找到了，用 give_gift npc={l.npcName.Value} 把{Util.ObjectName(l.ItemId.Value)}交给{NpcName(l.npcName.Value)}"
                        : $"去 {l.locationOfItem.Value} 找{Util.ObjectName(l.ItemId.Value)}（大概在 ({l.tileX.Value},{l.tileY.Value}) 附近，可能藏在垃圾桶、灌木之类里），找到后 give_gift 交给{NpcName(l.npcName.Value)}";
                case StardewValley.Quests.SocializeQuest:
                    return "和任务里的村民挨个说话（interact npc=名字），说完自动完成";
            }
            return null;
        }

        /// <summary>这个村民身上有没有“说句话就能交”的任务（钓鱼、打怪、收集任务做够了）。</summary>
        private static bool HasQuestReadyToTurnIn(NPC npc) =>
            Player.questLog.Any(q => !q.completed.Value && q is StardewValley.Quests.FishingQuest or StardewValley.Quests.SlayMonsterQuest or StardewValley.Quests.ResourceCollectionQuest
                && Safe(() => q.OnNpcSocialized(npc, probe: true)));

        private static bool Safe(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        /// <summary>走到村民身边送礼（村民不在当前地点会自动过去）。送之前检查次数限制和喜好。</summary>
        public IEnumerable<object> GiveGift(string npcName, string itemSpec, bool force = false)
        {
            var npc = FindVillager(npcName);
            if (npc == null)
            {
                yield return ActionResult.Fail($"不认识叫“{npcName}”的村民");
                yield break;
            }
            int idx = Util.FindItemIndex(itemSpec, Knowledge.IsGiftable);
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有可以送人的“{itemSpec}”");
                yield break;
            }
            // 他身上有说句话就能交的任务（钓够了鱼之类）：先交任务。拿着东西过去游戏会当成送礼，任务反而没交
            if (HasQuestReadyToTurnIn(npc))
            {
                ActionResult turnIn = null;
                foreach (var s in Interact(null, null, npc.Name, null))
                {
                    if (s is ActionResult r)
                    {
                        turnIn = r;
                        break;
                    }
                    yield return s;
                }
                yield return ActionResult.Success($"{npc.displayName} 身上有做够了的任务，先和他说话把任务交了（礼物没送，还要送就再 give_gift）：{turnIn?.Message}");
                yield break;
            }
            if (!Game1.player.friendshipData.TryGetValue(npc.Name, out var f))
            {
                yield return ActionResult.Fail($"还没认识 {npc.displayName}，先用 interact 和他聊一次天");
                yield break;
            }
            var item = Player.Items[idx];
            // 花束和美人鱼吊坠不算普通礼物：不受送礼次数限制，也不会增加次数
            bool bouquet = item.QualifiedItemId == "(O)458", pendant = item.QualifiedItemId == "(O)460";
            if (bouquet || pendant)
            {
                if (RomanceRefusal(npc, f, pendant) is string refusal)
                {
                    yield return ActionResult.Fail(refusal);
                    yield break;
                }
            }
            else if (f.GiftsToday > 0)
            {
                yield return ActionResult.Fail($"今天已经给 {npc.displayName} 送过礼了，每人每天只能送一次");
                yield break;
            }
            else if (f.GiftsThisWeek >= 2 && !npc.isBirthday() && !f.IsMarried())
            {
                yield return ActionResult.Fail($"这周已经给 {npc.displayName} 送了 2 次，下周一再送（生日当天不受限制）");
                yield break;
            }

            string itemName = item.DisplayName;
            int tasteCode = npc.getGiftTasteForThisItem(item);
            string taste = Knowledge.TasteName(tasteCode);
            // 送他讨厌 / 不喜欢的东西是倒扣好感（讨厌 -40、不喜欢 -20，生日 8 倍）：先拦下，列出背包里能换的。
            // 任务要交给他的东西（背包里标【留着】的）不拦，交任务不按喜好算
            if (!force && !bouquet && !pendant && (tasteCode == NPC.gift_taste_hate || tasteCode == NPC.gift_taste_dislike)
                && ReservedChecker()(item) == null)
            {
                int loss = (tasteCode == NPC.gift_taste_hate ? 40 : 20) * (npc.isBirthday() ? 8 : 1);
                var inv = Knowledge.InventoryTastes(npc);
                var better = new List<string>();
                foreach (int t in new[] { NPC.gift_taste_love, NPC.gift_taste_like, NPC.gift_taste_neutral })
                    if (inv.TryGetValue(t, out var names))
                        better.Add($"{Knowledge.TasteName(t)}：{string.Join("、", names.Take(6))}");
                yield return ActionResult.Fail($"{npc.displayName}{taste}{itemName}，送了好感会掉 {loss} 点{(npc.isBirthday() ? "（今天是他生日，8 倍）" : "")}，没有送。"
                    + (better.Count > 0 ? $"背包里可以换：{string.Join("；", better)}" : "背包里没有他不讨厌的礼物，先用 gift_info 看看他喜欢什么")
                    + $"。{ForceHint}");
                yield break;
            }
            int pointsBefore = f.Points;

            // 一路上把礼物举在手里送过去（游戏里拿着物品走路会举过头顶），看着像专门去送礼的
            foreach (var _ in WaitPlayerFree())
                yield return null;
            Player.CurrentToolIndex = idx;

            var o = new Outcome();
            var npcLoc = npc.currentLocation;
            if (npcLoc != null && npcLoc != Loc)
            {
                foreach (var s in TravelInner(npcLoc.NameOrUniqueName, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"去 {npc.displayName} 那里（{npcLoc.NameOrUniqueName}）失败：{o.Message}");
                    yield break;
                }
            }

            // 背包顺序可能在路上变了，按名字重新找
            ActionResult final = null;
            foreach (var s in Interact(null, null, npc.Name, itemName))
            {
                if (s is ActionResult r)
                {
                    final = r;
                    break;
                }
                yield return s;
            }
            if (bouquet || pendant)
            {
                if (bouquet && f.IsDating())
                    yield return ActionResult.Success($"{npc.displayName} 收下了花束，你们开始恋爱了（好感上限升到 10 心）。{final?.Message}");
                else if (pendant && f.IsEngaged())
                    yield return ActionResult.Success($"{npc.displayName} 答应了求婚！{f.WeddingDate.TotalDays - Game1.Date.TotalDays} 天后早上举行婚礼。{final?.Message}");
                else
                    yield return ActionResult.Fail($"{npc.displayName} 没有收下{itemName}。{final?.Message}");
                yield break;
            }
            int gained = f.Points - pointsBefore;
            bool given = f.GiftsToday > 0;
            if (given)
                yield return ActionResult.Success($"送给 {npc.displayName} 一个 {itemName}（他{taste}），好感 {(gained >= 0 ? "+" : "")}{gained}。{final?.Message}");
            else
                yield return ActionResult.Fail($"没送出去（{taste}）。{final?.Message}");
        }

        /// <summary>送花束或吊坠前先按游戏的条件查一遍（吊坠不到 10 心就送会掉好感）。能送返回 null。</summary>
        private string RomanceRefusal(NPC npc, Friendship f, bool pendant)
        {
            if (!npc.datable.Value)
                return $"{npc.displayName} 不能恋爱结婚";
            if (npc.isMarriedOrEngaged() && Player.spouse != npc.Name)
                return $"{npc.displayName} 已经有对象了";
            if (f.IsDivorced())
                return $"{npc.displayName} 和你离过婚，不会接受";
            if (!pendant)
            {
                if (f.IsDating())
                    return $"你们已经在恋爱了，不用再送花束";
                if (Player.isMarriedOrRoommates() || Player.isEngaged())
                    return "你已经订婚或结婚了，再送花束配偶会不高兴";
                return f.Points < 2000 ? $"{npc.displayName} 现在 {f.Points / 250} 心，要 8 心才收花束" : null;
            }
            if (Player.isMarriedOrRoommates() || Player.isEngaged())
                return "你已经订婚或结婚了";
            if (f.Points < 2500)
                return $"{npc.displayName} 现在 {f.Points / 250} 心，要 10 心才能求婚（先送花束恋爱，上限才到 10 心），不到就送会掉好感";
            return Player.houseUpgradeLevel.Value < 1 ? "求婚要先升级你的房子（upgrade_house）" : null;
        }

        /// <summary>恋爱对象到 10 心了：还差哪一步才能求婚。</summary>
        private static string ProposalHint(string name)
        {
            var me = Game1.player;
            if (me.Items.Any(i => i?.QualifiedItemId == "(O)460"))
                return me.houseUpgradeLevel.Value < 1
                    ? $"{name} 已经 10 心了，手上有吊坠，先升级房子（upgrade_house）才能求婚"
                    : $"{name} 已经 10 心了，想求婚就用 give_gift 送美人鱼吊坠";
            return $"{name} 已经 10 心了，想求婚要先买美人鱼吊坠（5000g）："
                + (Game1.isRaining && Game1.timeOfDay < 1900 ? "今天下雨，老水手在 Beach (80,5)" : "下雨天 19:00 前去 Beach (80,5) 找老水手")
                + (me.houseUpgradeLevel.Value < 1 ? "；还要升级房子（upgrade_house）" : "")
                + "（详见 get_guide 结婚）";
        }

        /// <summary>状态里显示的恋爱、婚约和下一步（都没有返回 null）。</summary>
        public static string RomanceLine()
        {
            var parts = new List<string>();
            string hint = null;
            bool taken = Game1.player.isMarriedOrRoommates() || Game1.player.isEngaged();
            foreach (var pair in Game1.player.friendshipData.Pairs)
            {
                var fr = pair.Value;
                // 村民有二十多个，每次读状态都要跑：够得上才去找人
                if (!fr.IsDating() && (fr.Points < 2000 || taken))
                    continue;
                var npc = Game1.getCharacterFromName(pair.Key);
                string name = npc?.displayName ?? pair.Key;
                if (fr.IsMarried())
                    parts.Add($"配偶：{name}");
                else if (fr.IsEngaged())
                    parts.Add($"已和 {name} 订婚，{fr.WeddingDate.TotalDays - Game1.Date.TotalDays} 天后早上举行婚礼（到时自动开始，用 dialogue 推进）");
                else if (fr.IsDating())
                {
                    parts.Add($"恋爱中：{name}（{fr.Points / 250} 心）");
                    if (fr.Points >= 2500)
                        hint = ProposalHint(name);
                }
                else if (hint == null && npc is { datable.Value: true } && !npc.isMarriedOrEngaged() && !fr.IsDivorced())
                    hint = $"{name} 已经 8 心了，想恋爱的话去皮埃尔杂货店买花束（200g），用 give_gift 送（详见 get_guide 结婚）";
            }
            if (hint != null)
                parts.Add(hint);
            // 和其他玩家的关系记在 team 上，不在 friendshipData 里
            foreach (var other in Game1.getOnlineFarmers().Where(p => !p.IsLocalPlayer))
            {
                var f = Game1.player.team.GetFriendship(Game1.player.UniqueMultiplayerID, other.UniqueMultiplayerID);
                if (f == null)
                    continue;
                if (f.IsMarried())
                    parts.Add($"配偶：{other.Name}（玩家）");
                else if (f.IsEngaged())
                    parts.Add($"已和 {other.Name}（玩家）订婚，{f.WeddingDate.TotalDays - Game1.Date.TotalDays} 天后早上举行婚礼（到时自动开始，用 dialogue 推进）");
            }
            return parts.Count > 0 ? string.Join("；", parts) : null;
        }

        // ───────────────────────── 博物馆 ─────────────────────────

        /// <summary>把背包里所有能捐的矿物/古物捐给博物馆，并领取捐赠奖励。</summary>
        public IEnumerable<object> DonateMuseum()
        {
            var museum = Knowledge.Museum;
            if (museum == null)
            {
                yield return ActionResult.Fail("找不到博物馆");
                yield break;
            }
            if (Knowledge.MuseumDonatable().Count == 0 && museum.getRewardsForPlayer(Player).Count == 0)
            {
                yield return ActionResult.Fail("背包里没有可以捐给博物馆的东西（矿物、古物，而且博物馆还没有的），也没有待领的奖励");
                yield break;
            }

            var o = new Outcome();
            if (Loc != museum)
            {
                foreach (var s in TravelInner("ArchaeologyHouse", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("去博物馆失败（开放时间 8:00-18:00）：" + o.Message);
                    yield break;
                }
            }

            // 走到冈瑟的柜台（和手动找他捐赠一样）
            var counter = FindTileAction(museum, "Gunther");
            if (counter.HasValue)
            {
                foreach (var s in GoAdjacent(counter.Value, o))
                    yield return s;
            }

            var donated = new List<string>();
            string stop = null;
            foreach (var item in Knowledge.MuseumDonatable())
            {
                int idx = Player.Items.IndexOf(item);
                if (idx < 0 || !museum.isItemSuitableForDonation(item))
                    continue;
                var spot = museum.getFreeDonationSpot();
                if (!museum.isTileSuitableForMuseumPiece((int)spot.X, (int)spot.Y))
                {
                    stop = "博物馆展台满了";
                    break;
                }
                // 与 MuseumMenu 放下展品时的逻辑一致
                museum.museumPieces.Add(spot, item.ItemId);
                Player.Items[idx] = item.ConsumeStack(1);
                Game1.player.completeQuest("24");
                Game1.stats.checkForArchaeologyAchievements();
                donated.Add(item.DisplayName);
                Game1.playSound("newArtifact");
                foreach (var s in WaitTicks(10))
                    yield return s;
            }

            // 领奖励：和奖励界面里拿走物品的效果一样（会标记已领取、学会配方等）
            var got = new List<string>();
            bool full = false;
            foreach (var reward in museum.getRewardsForPlayer(Player))
            {
                if (!Player.couldInventoryAcceptThisItem(reward))
                {
                    full = true;
                    continue;
                }
                string name = reward.IsRecipe ? reward.DisplayName + "（配方）" : Util.ItemLabel(reward);
                if (Player.addItemToInventoryBool(reward))
                {
                    museum.OnRewardCollected(reward, Player);
                    got.Add(name);
                }
            }

            int total = museum.museumPieces.Length;
            string msg = donated.Count > 0 ? $"捐赠了 {donated.Count} 件：{string.Join("、", donated)}" : "这次没有新的可捐物品";
            msg += $"；博物馆共 {total}/{LibraryMuseum.totalArtifacts} 件";
            if (got.Count > 0)
                msg += "；领到奖励：" + string.Join("、", got);
            if (full)
                msg += "；还有奖励因为背包满了没领，空出位置后再来";
            if (stop != null)
                msg += "；" + stop;
            yield return donated.Count > 0 || got.Count > 0 ? ActionResult.Success(msg) : ActionResult.Fail(msg);
        }

        private static Point? FindTileAction(GameLocation loc, string actionName)
        {
            int w = Pathfinder.MapWidth(loc), h = Pathfinder.MapHeight(loc);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    string a = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
                    if (a != null && a.Split(' ')[0].Equals(actionName, StringComparison.OrdinalIgnoreCase))
                        return new Point(x, y);
                }
            return null;
        }

        // ───────────────────────── 社区中心 ─────────────────────────

        public IEnumerable<object> BundleInfo(bool all)
        {
            string why = Knowledge.CommunityCenterUnavailable();
            if (why != null)
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var lines = new List<string>();
            int shown = 0;
            foreach (var group in Knowledge.Bundles().Where(b => !b.Complete).GroupBy(b => b.Area))
            {
                bool available = group.Any(Knowledge.IsBundleAvailable);
                if (!available && !all)
                    continue;
                lines.Add($"【{Knowledge.AreaDisplayName(group.Key)}】{(available ? "" : "（还没解锁）")}");
                foreach (var b in group)
                {
                    var missing = b.Ingredients.Where(i => !i.completed).Select(Knowledge.IngredientName).ToList();
                    var have = Knowledge.InventoryMatches(b);
                    lines.Add($"  {b.DisplayName}：已交 {b.SlotsDone}/{b.SlotsNeeded}，可选材料（交够 {b.SlotsNeeded - b.SlotsDone} 种即可）：{string.Join("、", missing)}"
                        + (have.Count > 0 ? $"；背包里能交：{string.Join("、", have)}" : ""));
                    shown++;
                }
            }
            if (shown == 0)
                lines.Add("目前能看到的收集包都完成了");
            yield return ActionResult.Success(string.Join("\n", lines));
        }

        /// <summary>
        /// 去社区中心，把背包里能交的材料交进对应收集包，领取完成奖励。
        /// 走的是祝尼魔便条界面（JunimoNoteMenu）自己的提交逻辑，完成动画、奖励、修复房间都和手动一样。
        /// </summary>
        public IEnumerable<object> DonateBundles()
        {
            string why = Knowledge.CommunityCenterUnavailable();
            if (why != null)
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var targets = Knowledge.Bundles()
                .Where(b => !b.Complete && Knowledge.IsBundleAvailable(b) && Knowledge.InventoryMatches(b).Count > 0)
                .Select(b => b.Area).Distinct().ToList();
            var cc = Game1.RequireLocation<CommunityCenter>("CommunityCenter");
            bool rewardsWaiting = cc.bundleRewards.Pairs.Any(p => p.Value);
            if (targets.Count == 0 && !rewardsWaiting)
            {
                yield return ActionResult.Fail("背包里没有能交给已解锁收集包的材料（用 bundle_info 看看缺什么）");
                yield break;
            }

            var o = new Outcome();
            if (Loc != cc)
            {
                foreach (var s in TravelInner("CommunityCenter", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("去社区中心失败：" + o.Message);
                    yield break;
                }
            }

            var deposited = new List<string>();
            var keptForQuest = new List<string>();
            var questReserve = QuestReserve();
            var completed = new List<string>();
            var rewards = new List<string>();
            var rewardDrops = new List<string>();
            // 有奖励待领的区域也要打开一次便条
            var areas = targets.Concat(Knowledge.Bundles().Where(b => cc.bundleRewards.TryGetValue(b.Index, out bool r) && r).Select(b => b.Area))
                .Where(a => a >= 0 && a <= 5).Distinct().ToList();

            foreach (int area in areas)
            {
                // 走到这个区域的便条旁边（找不到便条位置就原地打开）
                Point? note = null;
                try
                {
                    note = reflection.GetMethod(cc, "getNotePosition").Invoke<Point>(area);
                }
                catch
                {
                    // 忽略
                }
                if (note.HasValue)
                {
                    foreach (var s in GoAdjacent(note.Value, o, new Point(note.Value.X, note.Value.Y + 1)))
                        yield return s;
                }
                if (Blocked(out _))
                    break;

                cc.checkBundle(area);
                JunimoNoteMenu menu = null;
                for (int t = 0; t < 120 && menu == null; t++)
                {
                    menu = Game1.activeClickableMenu as JunimoNoteMenu;
                    yield return null;
                }
                if (menu == null)
                    continue;

                foreach (var bundle in menu.bundles.ToList())
                {
                    if (bundle.complete || !bundle.depositsAllowed || Game1.activeClickableMenu != menu)
                        continue;
                    // 先确认背包里有能交的，再打开这个收集包的页面
                    bool any = Player.Items.Any(it => it != null && bundle.GetBundleIngredientDescriptionIndexForItem(it) >= 0
                        && bundle.canAcceptThisItem(it, null, ignore_stack_count: false));
                    if (!any)
                        continue;
                    reflection.GetMethod(menu, "setUpBundleSpecificPage").Invoke(bundle);
                    yield return null;

                    for (int i = 0; i < Player.Items.Count; i++)
                    {
                        var item = Player.Items[i];
                        if (item == null)
                            continue;
                        foreach (var slot in menu.ingredientSlots)
                        {
                            if (item == null || slot.item != null || !bundle.canAcceptThisItem(item, slot))
                                continue;
                            // 任务、特别订单要交的先留够，只交多出来的
                            var (reserve, reserveWhy) = questReserve(item);
                            if (reserve > 0)
                            {
                                int take = bundle.ingredients.Where(d => !d.completed && bundle.IsValidItemForThisIngredientDescription(item, d))
                                    .Select(d => d.stack).DefaultIfEmpty(1).Max();
                                if (reserve == int.MaxValue || item.Stack - take < reserve)
                                {
                                    keptForQuest.Add($"{item.DisplayName}（{reserveWhy}）");
                                    break;
                                }
                            }
                            string name = item.DisplayName;
                            item = bundle.tryToDepositThisItem(item, slot, "LooseSprites\\JunimoNote", menu);
                            Player.Items[i] = item;
                            deposited.Add($"{name}→{bundle.label}");
                        }
                    }
                    reflection.GetMethod(menu, "checkIfBundleIsComplete").Invoke();
                    bool done = cc.isBundleComplete(bundle.bundleIndex);
                    if (done)
                    {
                        completed.Add(bundle.label);
                        // 完成时会播放动画，播完自己回到总览页
                        for (int t = 0; t < 300 && Game1.activeClickableMenu == menu && menu.specificBundlePage; t++)
                            yield return null;
                    }
                    if (Game1.activeClickableMenu == menu && menu.specificBundlePage)
                        menu.takeDownBundleSpecificPage();
                    yield return null;
                }

                // 领奖励（礼物按钮），走游戏自己的奖励界面，领取回调会标记已领
                if (Game1.activeClickableMenu == menu && menu.presentButton != null)
                {
                    reflection.GetMethod(menu, "openRewardsMenu").Invoke();
                    yield return null;
                    if (Game1.activeClickableMenu is ItemGrabMenu grab)
                    {
                        rewards.AddRange(grab.ItemsToGrabMenu.actualInventory.Where(i => i != null).Select(Util.ItemLabel));
                        rewardDrops.AddRange(GrabAll(grab));
                    }
                }

                // 关掉便条；如果整个区域完成了，关闭时会播放祝尼魔修复房间的过场
                if (Game1.activeClickableMenu is JunimoNoteMenu open)
                    open.exitThisMenu();
                // 修复过场是关掉便条以后才开始的，开始后游戏冻结操作（freezeControls）十几秒：先等它起来，再等它播完
                foreach (var s in WaitTicks(20))
                    yield return s;
                for (int t = 0; t < 60 * 60; t++)
                {
                    if (Game1.activeClickableMenu == null && !Game1.eventUp && !Game1.fadeToBlack && !Game1.globalFade && !Game1.freezeControls
                        && !Game1.isViewportOnCustomPath() && Player.CanMove)
                        break;
                    yield return null;
                }
            }

            var msg = new List<string>();
            msg.Add(deposited.Count > 0 ? $"交了 {deposited.Count} 样：{string.Join("、", deposited)}" : "没有交上材料");
            if (keptForQuest.Count > 0)
                msg.Add("这些收集包也要、但先留给任务没交：" + string.Join("、", keptForQuest.Distinct()) + "。交完任务、或者再弄到更多，再 donate_bundles");
            if (completed.Count > 0)
                msg.Add("完成收集包：" + string.Join("、", completed.Distinct()));
            if (rewards.Count > 0)
                msg.Add("领到奖励：" + string.Join("、", rewards));
            if (rewardDrops.Count > 0)
                msg.Add($"背包满了，{string.Join("、", rewardDrops)} 掉在脚边 ({Player.TilePoint.X},{Player.TilePoint.Y})，空出位置走过去能捡回来，今天不捡就没了");
            yield return deposited.Count > 0 || rewards.Count > 0 ? ActionResult.Success(string.Join("；", msg)) : ActionResult.Fail(string.Join("；", msg));
        }
    }
}
