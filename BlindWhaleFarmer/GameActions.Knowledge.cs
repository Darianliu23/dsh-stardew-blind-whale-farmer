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
        /// <summary>
        /// 同名或同显示名村民的匹配优先级（越大越优先）：
        /// 解决 SVE 中原版隐藏影子 NPC（如位于 (-49,-49) 且不可社交的 Marlon / Gunther）与 SVE 可社交 NPC（MarlonFay / GuntherSilvian）重名的问题。
        /// </summary>
        internal static int NpcMatchRank(bool exactInternalName, bool canSocialize, bool invisible, bool onMap, bool inWarpRoom)
        {
            int score = 0;
            if (!invisible) score += 100;
            if (onMap) score += 80;
            if (!inWarpRoom) score += 60;
            if (canSocialize) score += 40;
            if (exactInternalName) score += 10;
            return score;
        }

        internal static NPC PickBestNpcMatch(IEnumerable<NPC> candidates, string name)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(name))
                return null;
            string q = name.Trim();
            NPC best = null;
            int bestRank = int.MinValue;
            foreach (var n in candidates)
            {
                if (n == null)
                    continue;
                bool exactName = n.Name.Equals(q, StringComparison.OrdinalIgnoreCase);
                bool exactDisplay = n.displayName != null && n.displayName.Equals(q, StringComparison.OrdinalIgnoreCase);
                if (!exactName && !exactDisplay)
                    continue;
                var pt = n.TilePoint;
                bool onMap = pt.X >= 0 && pt.Y >= 0;
                bool inWarpRoom = ModSupport.IsWarpRoom(n.currentLocation?.NameOrUniqueName);
                // SVE 的 GuntherSilvian 在解锁社交前 CanSocialize 也是 false，但有完整生日与送礼表；优先于无生日的原版后台影子 Gunther
                bool hasProfile = !string.IsNullOrEmpty(n.Birthday_Season) && n.Birthday_Day > 0;
                int rank = NpcMatchRank(exactName, n.CanSocialize, n.IsInvisible, onMap, inWarpRoom) + (hasProfile ? 20 : 0);
                if (rank > bestRank)
                {
                    bestRank = rank;
                    best = n;
                }
            }
            return best;
        }

        private static NPC FindVillager(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var all = new List<NPC>();
            Utility.ForEachVillager(n =>
            {
                all.Add(n);
                return true;
            });
            return PickBestNpcMatch(all, name);
        }

        /// <summary>
        /// 村民名没找到时的提示：把名字最像的几个村民（内部名和显示名一起）列出来。
        /// 真机上“Sophia”“索菲亚”都能查到，这里只在真写错时出现，别再说“用英文名”误导。
        /// </summary>
        internal static string VillagerHint(string wanted)
        {
            // 角色数据里有、但这会儿不在任何一张地图的角色列表里：多半正在走路换地图（联机时常见）。
            // 真机：11:20 查“索菲亚”“Sophia”都报不认识，12:10 同样的名字就查到了）。别说“不认识”，说等一会儿再试
            try
            {
                string w = wanted?.Trim() ?? "";
                var data = DataLoader.Characters(Game1.content);
                foreach (var (id, c) in data)
                {
                    string display = StardewValley.TokenizableStrings.TokenParser.ParseText(c.DisplayName) ?? id;
                    if (id.Equals(w, StringComparison.OrdinalIgnoreCase) || display.Equals(w, StringComparison.OrdinalIgnoreCase))
                        return $"。{display}（{id}）名字没错，但这会儿不在任何一张地图上（多半正在换地图，联机时常见）：等十几秒再试，或者先去他常在的地方";
                }
            }
            catch
            {
                // 角色数据读不出来就走下面的相近名字
            }
            var names = new List<(string internalName, string display)>();
            Utility.ForEachVillager(n =>
            {
                names.Add((n.Name, n.displayName));
                return true;
            });
            var near = names
                .Select(n => (n, score: Math.Max(Util.NameScore(wanted, n.internalName), Util.NameScore(wanted, n.display))))
                .Where(t => t.score > 0).OrderByDescending(t => t.score).Take(4)
                .Select(t => t.n.display == t.n.internalName ? t.n.display : $"{t.n.display}（{t.n.internalName}）").ToList();
            return near.Count > 0
                ? $"。名字相近的：{string.Join("、", near)}（中文显示名、英文内部名都行）"
                : "。中文显示名、英文内部名都行；get_state 的“人物”里列了所有村民";
        }

        // ───────────────────────── 送礼 ─────────────────────────

        internal const int ReverseGiftMaxItems = 12;
        internal const int ReverseGiftMaxNpcsPerTaste = 5;

        internal sealed record GiftCandidateNpc(
            string InternalName,
            string DisplayName,
            string LocationName,
            bool CanSocialize,
            bool IsBirthday,
            bool IsMarried,
            bool IsDivorced,
            bool IsMet,
            int GiftsToday,
            int GiftsThisWeek);

        internal sealed record GiftCandidateItem(
            string Key,
            string DisplayName,
            string Source,
            bool IsFromChest,
            bool IsReserved,
            string ReservedReason = null);

        /// <summary>这个村民今天还能不能送普通礼物（能社交、未离婚、今天没送过、本周未满 2 次或生日/配偶）。</summary>
        internal static bool CanGiftToday(GiftCandidateNpc npc) =>
            npc != null
            && npc.CanSocialize
            && !npc.IsDivorced
            && npc.GiftsToday <= 0
            && (npc.GiftsThisWeek < 2 || npc.IsBirthday || npc.IsMarried);

        private static string FormatGiftNpcLabel(GiftCandidateNpc npc) =>
            npc.DisplayName
            + (npc.IsBirthday ? "（今天生日）" : "")
            + (npc.IsMet ? "" : "(未认识)");

        private static string FormatGiftNpcList(IReadOnlyList<GiftCandidateNpc> npcs, int maxCount)
        {
            var shown = npcs.Take(maxCount).Select(FormatGiftNpcLabel);
            string joined = string.Join("、", shown);
            return npcs.Count > maxCount ? $"{joined} 等 {npcs.Count} 人" : joined;
        }

        /// <summary>从背包（可选含箱子）反查今天可送礼村民的最爱/喜欢匹配。</summary>
        internal static string BuildReverseGiftSummary(
            IReadOnlyList<GiftCandidateNpc> npcs,
            IReadOnlyList<GiftCandidateItem> items,
            Func<GiftCandidateNpc, GiftCandidateItem, int> getTaste,
            bool includeChests = false,
            int maxItemLines = ReverseGiftMaxItems,
            int maxNpcsPerTaste = ReverseGiftMaxNpcsPerTaste)
        {
            var eligibleNpcs = (npcs ?? Array.Empty<GiftCandidateNpc>())
                .Where(CanGiftToday)
                .OrderByDescending(n => n.IsBirthday)
                .ThenByDescending(n => n.IsMet)
                .ThenBy(n => n.DisplayName, StringComparer.Ordinal)
                .ToList();
            if (eligibleNpcs.Count == 0)
                return "今天没有还能送礼的村民了（今天都已送过或本周已送满 2 次）。";

            var usableItems = new List<GiftCandidateItem>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var skippedReservedKeys = new HashSet<string>(StringComparer.Ordinal);
            var orderedInput = (items ?? Array.Empty<GiftCandidateItem>())
                .Where(it => it != null && (includeChests || !it.IsFromChest))
                .OrderBy(it => it.IsFromChest);
            foreach (var item in orderedInput)
            {
                string key = string.IsNullOrEmpty(item.Key) ? item.DisplayName : item.Key;
                if (item.IsReserved)
                {
                    skippedReservedKeys.Add(key);
                    continue;
                }
                if (seenKeys.Add(key))
                    usableItems.Add(item);
            }

            // 预计算每个物品对可送村民的最爱/喜欢列表（保留 eligibleNpcs 顺序：生日最前）
            var matches = new List<(GiftCandidateItem item, List<GiftCandidateNpc> love, List<GiftCandidateNpc> like)>();
            foreach (var item in usableItems)
            {
                var love = new List<GiftCandidateNpc>();
                var like = new List<GiftCandidateNpc>();
                foreach (var npc in eligibleNpcs)
                {
                    int taste = getTaste(npc, item);
                    if (taste == NPC.gift_taste_love || taste == NPC.gift_taste_stardroptea)
                        love.Add(npc);
                    else if (taste == NPC.gift_taste_like)
                        like.Add(npc);
                }
                if (love.Count > 0 || like.Count > 0)
                    matches.Add((item, love, like));
            }

            string scope = includeChests ? "背包+箱子" : "背包";
            int skippedReserved = skippedReservedKeys.Count(k => !seenKeys.Contains(k));
            var lines = new List<string>
            {
                $"{scope}送礼反查（今天还能送 {eligibleNpcs.Count} 人，只列最爱/喜欢{(skippedReserved > 0 ? $"；已跳过 {skippedReserved} 件交任务/【留着】物品" : "")}）："
            };

            // 生日村民置顶汇总（不受本周 2 次限制，好感 x8）
            foreach (var bday in eligibleNpcs.Where(n => n.IsBirthday))
            {
                var bLove = matches.Where(m => m.love.Contains(bday))
                    .Select(m => includeChests ? $"{m.item.DisplayName}（{m.item.Source}）" : m.item.DisplayName)
                    .Take(4).ToList();
                var bLike = matches.Where(m => m.like.Contains(bday))
                    .Select(m => includeChests ? $"{m.item.DisplayName}（{m.item.Source}）" : m.item.DisplayName)
                    .Take(4).ToList();
                string who = $"{bday.DisplayName}（今天生日{(bday.IsMet ? "" : "，未认识")}，在 {bday.LocationName}）";
                var parts = new List<string>();
                if (bLove.Count > 0) parts.Add("最爱 " + string.Join("、", bLove));
                if (bLike.Count > 0) parts.Add("喜欢 " + string.Join("、", bLike));
                lines.Add(parts.Count > 0
                    ? $"★ {who}：{string.Join("；", parts)}"
                    : $"★ {who}：{scope}里没有最爱/喜欢的，可传 npc=\"{bday.InternalName}\" 查喜好");
            }

            if (matches.Count == 0)
            {
                lines.Add($"{scope}里没有今天可送村民最爱或喜欢的礼物。"
                    + (!includeChests ? "可传 chests=true 连箱子一起查，或传 npc 单独查。" : "可传 npc 单独查。"));
                return string.Join("\n", lines);
            }

            var orderedMatches = matches
                .OrderByDescending(m => m.love.Any(n => n.IsBirthday))
                .ThenByDescending(m => m.like.Any(n => n.IsBirthday))
                .ThenByDescending(m => m.love.Count > 0)
                .ThenBy(m => m.item.IsFromChest)
                .ThenByDescending(m => m.love.Count)
                .ThenByDescending(m => m.like.Count)
                .ThenBy(m => m.item.DisplayName, StringComparer.Ordinal)
                .ToList();

            int limit = Math.Max(1, maxItemLines);
            int npcLimit = Math.Max(1, maxNpcsPerTaste);
            foreach (var (item, love, like) in orderedMatches.Take(limit))
            {
                string itemLabel = includeChests ? $"{item.DisplayName}（{item.Source}）" : item.DisplayName;
                var parts = new List<string>();
                if (love.Count > 0) parts.Add("最爱 → " + FormatGiftNpcList(love, npcLimit));
                if (like.Count > 0) parts.Add("喜欢 → " + FormatGiftNpcList(like, npcLimit));
                lines.Add($"- {itemLabel}：{string.Join("；", parts)}");
            }
            if (orderedMatches.Count > limit)
                lines.Add($"还有 {orderedMatches.Count - limit} 件没列，可传 npc 单独查");
            else if (!includeChests)
                lines.Add("（默认只查背包；传 chests=true 可连箱子一起查，传 npc 单独查某人）");

            return string.Join("\n", lines);
        }

        private static bool IsReverseGiftCandidate(Item item) =>
            Knowledge.IsGiftable(item) && item.QualifiedItemId is not ("(O)458" or "(O)460");

        private IEnumerable<object> ReverseGiftInfo(bool includeChests)
        {
            var npcEntries = new List<(GiftCandidateNpc info, NPC raw)>();
            var seenNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Utility.ForEachVillager(n =>
            {
                if (n == null || !n.CanSocialize || n.currentLocation == null || n.IsInvisible)
                    return true;
                var pt = n.TilePoint;
                if (pt.X < 0 || pt.Y < 0 || ModSupport.IsWarpRoom(n.currentLocation.NameOrUniqueName))
                    return true;
                if (!seenNpcs.Add(n.Name))
                    return true;
                bool met = Game1.player.friendshipData.TryGetValue(n.Name, out var f);
                var info = new GiftCandidateNpc(
                    n.Name,
                    string.IsNullOrWhiteSpace(n.displayName) ? n.Name : n.displayName,
                    n.currentLocation.NameOrUniqueName,
                    n.CanSocialize,
                    n.isBirthday(),
                    met && f.IsMarried(),
                    met && f.IsDivorced(),
                    met,
                    met ? f.GiftsToday : 0,
                    met ? f.GiftsThisWeek : 0);
                npcEntries.Add((info, n));
                return true;
            });

            var reserved = ReservedChecker();
            var itemEntries = new List<(GiftCandidateItem info, Item raw)>();
            foreach (var item in Player.Items)
            {
                if (!IsReverseGiftCandidate(item))
                    continue;
                string why = reserved(item);
                itemEntries.Add((new GiftCandidateItem(item.QualifiedItemId, item.DisplayName, "背包", false, why != null, why), item));
            }
            if (includeChests)
            {
                foreach (var (loc, tile, chest) in AllChests())
                {
                    bool isFridge = loc is FarmHouse fh && chest == fh.fridge.Value;
                    if (!isFridge && chest.SpecialChestType is not (StardewValley.Objects.Chest.SpecialChestTypes.None
                        or StardewValley.Objects.Chest.SpecialChestTypes.BigChest
                        or StardewValley.Objects.Chest.SpecialChestTypes.JunimoChest))
                        continue;
                    string source = isFridge
                        ? (loc.NameOrUniqueName == "FarmHouse" ? "冰箱" : $"{loc.NameOrUniqueName}冰箱({tile.X},{tile.Y})")
                        : $"{loc.NameOrUniqueName}({tile.X},{tile.Y})";
                    foreach (var item in chest.GetItemsForPlayer())
                    {
                        if (!IsReverseGiftCandidate(item))
                            continue;
                        string why = reserved(item);
                        itemEntries.Add((new GiftCandidateItem(item.QualifiedItemId, item.DisplayName, source, true, why != null, why), item));
                    }
                }
            }

            var npcMap = new Dictionary<GiftCandidateNpc, NPC>(ReferenceEqualityComparer.Instance);
            foreach (var (info, raw) in npcEntries)
                npcMap[info] = raw;
            var itemMap = new Dictionary<GiftCandidateItem, Item>(ReferenceEqualityComparer.Instance);
            foreach (var (info, raw) in itemEntries)
                itemMap[info] = raw;
            string summary = BuildReverseGiftSummary(
                npcEntries.Select(e => e.info).ToList(),
                itemEntries.Select(e => e.info).ToList(),
                (n, it) => npcMap[n].getGiftTasteForThisItem(itemMap[it]),
                includeChests);
            yield return ActionResult.Success(summary);
        }

        /// <summary>查询某个村民的喜好、生日、好感，以及背包里哪些东西适合送他；不传 npc 时从背包（可选含箱子）反查今天可送谁。不移动、不花时间。</summary>
        public IEnumerable<object> GiftInfo(string npcName = null, bool includeChests = false)
        {
            if (string.IsNullOrWhiteSpace(npcName))
            {
                foreach (var s in ReverseGiftInfo(includeChests))
                    yield return s;
                yield break;
            }
            var npc = FindVillager(npcName);
            if (npc == null)
            {
                yield return ActionResult.Fail($"不认识叫“{npcName}”的村民" + VillagerHint(npcName));
                yield break;
            }
            var lines = new List<string>();
            string birthday = npc.Birthday_Season != null ? $"{Util.SeasonName(npc.Birthday_Season)}{npc.Birthday_Day}日" : "未知";
            lines.Add($"{npc.displayName}（{npc.Name}）生日：{birthday}{(npc.isBirthday() ? "，就是今天！生日礼物好感 x8" : "")}，现在在 {npc.currentLocation?.NameOrUniqueName ?? "?"}{(StardewModdingAPI.Context.IsMultiplayer && !Game1.IsMasterGame ? "（联机时你看到的村民位置可能是旧的，到了那里没人就去别处找，get_state 的“人物”只列同一张地图上的）" : "")}");
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
                // 没有交任务的人（马龙的“入门”打 10 只史莱姆就是这样，游戏里存的是字符串 "null"）：打够了自动完成
                case StardewValley.Quests.SlayMonsterQuest m when m.target.Value is null or "null" or "":
                    return $"只认接任务以后打倒的（现在 {m.numberKilled.Value}/{m.numberToKill.Value}），打够了自动完成，不用找人交";
                case StardewValley.Quests.SlayMonsterQuest m:
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
                        : $"{Util.ObjectName(l.ItemId.Value)}就在 {l.locationOfItem.Value} 的 ({l.tileX.Value},{l.tileY.Value}) 那一格：先 travel_to {l.locationOfItem.Value}（一定是这张图，别在旁边的地图里找），到了 interact x={l.tileX.Value} y={l.tileY.Value} 捡起来（周围被灌木挡住会自己找能够到的位置），再 give_gift 交给{NpcName(l.npcName.Value)}";
                case StardewValley.Quests.SocializeQuest:
                    return "和任务里的村民挨个说话（interact npc=名字），说完自动完成";
                case StardewValley.Quests.Quest when q.id.Value == "134":
                    return "带 100 个硬木去煤矿森林（Forest）北边找大树桩，用 raccoon action=fix 会自动定位并修好";
            }
            return null;
        }

        /// <summary>把这件东西交给他能不能完成某个任务或特别订单（按游戏自己的判断，只探测不交）。</summary>
        private static bool IsQuestDelivery(NPC npc, Item item) =>
            Player.questLog.Any(q => !q.completed.Value && Safe(() => q.OnItemOfferedToNpc(npc, item, probe: true)))
            || IsSpecialOrderDelivery(npc, item);

        /// <summary>特别订单里“交给某人”的目标（比如法师要灵外质）：游戏在 NPC.tryToReceiveActiveObject 里按 onItemDelivered 探测。</summary>
        private static bool IsSpecialOrderDelivery(NPC npc, Item item)
        {
            foreach (var order in Player.team.specialOrders)
            {
                if (order.questState.Value != StardewValley.SpecialOrders.SpecialOrderStatus.InProgress || order.onItemDelivered == null)
                    continue;
                foreach (var d in order.onItemDelivered.GetInvocationList())
                    if (Safe(() => ((Func<Farmer, NPC, Item, bool, int>)d)(Player, npc, item, true) > 0))
                        return true;
            }
            return false;
        }

        /// <summary>拿着任务要的东西走过去交给他（和送礼走同一条路，但不检查送礼次数和喜好）。</summary>
        private IEnumerable<object> DeliverQuestItem(NPC npc, int idx)
        {
            var item = Player.Items[idx];
            string itemName = item.DisplayName;
            int openBefore = Player.questLog.Count(q => !q.completed.Value);
            foreach (var _ in WaitPlayerFree())
                yield return null;
            // 路上别举着任务物品（会被路过的村民当礼物收走），到他旁边 Interact 再拿出来
            PutAwayHeldItem();
            var o = new Outcome();
            var npcLoc = npc.currentLocation;
            if (npcLoc != null && ModSupport.IsWarpRoom(npcLoc.NameOrUniqueName))
            {
                yield return ActionResult.Fail($"{npc.displayName} 这会儿还没在镇上出场（在后台等待区 {npcLoc.NameOrUniqueName}，进不去）。{NpcScheduleHint(npc)}");
                yield break;
            }
            if (npcLoc != null && npcLoc != Loc)
            {
                foreach (var s in TravelInner(npcLoc.NameOrUniqueName, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail($"去 {npc.displayName} 那里（{npcLoc.NameOrUniqueName}）交任务失败：{o.Message}");
                    yield break;
                }
            }
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
            bool done = Player.questLog.Count(q => !q.completed.Value) < openBefore || !Player.Items.Contains(item) || item.Stack <= 0;
            yield return done
                ? ActionResult.Success($"把{itemName}交给了 {npc.displayName}，任务完成（交任务不算送礼，今天还能再送他礼物）。{final?.Message}")
                : ActionResult.Fail($"没交成（{itemName} 还在背包里）。{final?.Message}");
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
                // 联机时 AI 常把给队友东西也叫成 give_gift：匹配到在线玩家就直接走到对方身边丢在脚边
                if (StardewModdingAPI.Context.IsMultiplayer)
                {
                    var onlineOthers = Game1.getOnlineFarmers().Where(f => f != null && !f.IsLocalPlayer).ToList();
                    if (MatchFarmer(onlineOthers, npcName) is Farmer otherPlayer)
                    {
                        foreach (var s in DropItem(itemSpec, 1, otherPlayer.Name, force))
                            yield return s;
                        yield break;
                    }
                }
                yield return ActionResult.Fail($"不认识叫“{npcName}”的村民" + VillagerHint(npcName)
                    + (StardewModdingAPI.Context.IsMultiplayer ? "（给其他玩家东西请用 drop_item to=玩家名）" : ""));
                yield break;
            }
            // 任务物品（灵外质之类）不能当礼物送，但能交任务
            int idx = Util.FindItemIndex(itemSpec, it => Knowledge.IsGiftable(it) || IsQuestDelivery(npc, it));
            if (idx < 0)
            {
                yield return ActionResult.Fail($"背包里没有可以送人的“{itemSpec}”" + Util.NotFoundHint(itemSpec, it => Knowledge.IsGiftable(it)));
                yield break;
            }
            // 玩家在设置里要留着的东西不送（交任务例外：任务要的照样交）
            if (ReservedChecker()(Player.Items[idx]) == PlayerReserved && !IsQuestDelivery(npc, Player.Items[idx]))
            {
                yield return ActionResult.Fail($"{Player.Items[idx].DisplayName} 是玩家要求保留的，不能送人，换别的礼物");
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
            // 交任务的东西：游戏先按任务算，不受每天/每周送礼次数、喜好、认不认识的限制，也不算送礼（GiftsToday 不变）。
            // 同名东西可能按品质分成几堆，游戏只看拿在手里的那一堆够不够数：挑一堆够数的
            {
                string wantId = Player.Items[idx].QualifiedItemId;
                int questIdx = -1;
                for (int i = 0; i < Player.Items.Count && questIdx < 0; i++)
                    if (Player.Items[i] is { } it && it.QualifiedItemId == wantId && IsQuestDelivery(npc, it))
                        questIdx = i;
                if (questIdx >= 0)
                {
                    foreach (var s in DeliverQuestItem(npc, questIdx))
                        yield return s;
                    yield break;
                }
                var shortQuest = Player.questLog.OfType<StardewValley.Quests.ItemDeliveryQuest>()
                    .FirstOrDefault(q => !q.completed.Value && q.target.Value == npc.Name && q.ItemId.Value == wantId);
                if (shortQuest != null)
                {
                    int biggest = Player.Items.Where(i => i?.QualifiedItemId == wantId).Max(i => i.Stack);
                    yield return ActionResult.Fail($"交给 {npc.displayName} 的任务要 {shortQuest.number.Value} 个{Player.Items[idx].DisplayName}，而且必须是同一堆（品质不同的分开放，不会叠在一起）；"
                        + $"现在最多的一堆只有 {biggest} 个。再弄几个同品质的凑够一堆再交");
                    yield break;
                }
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

            // 路上手里别拿着礼物：举着东西走，进门、开箱、碰到别的村民时一交互就会被当成送给他的礼物（群友报过交任务的东西被半路截胡）。
            // 走到他旁边后 Interact 才把这件东西拿到手里
            foreach (var _ in WaitPlayerFree())
                yield return null;
            PutAwayHeldItem();

            var o = new Outcome();
            var npcLoc = npc.currentLocation;
            if (npcLoc != null && ModSupport.IsWarpRoom(npcLoc.NameOrUniqueName))
            {
                yield return ActionResult.Fail($"{npc.displayName} 这会儿还没在镇上出场（在后台等待区 {npcLoc.NameOrUniqueName}，进不去）。{NpcScheduleHint(npc)}");
                yield break;
            }
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
                + (Game1.isRaining && Game1.timeOfDay < 1900 ? "今天下雨，老水手在海滩东边潮池（Beach，原版在 (80,5)）" : "下雨天 19:00 前去海滩东边潮池找老水手（Beach，原版在 (80,5)）")
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
                var dup = Player.Items.Where(i => i != null && LibraryMuseum.IsItemSuitableForDonation(i.QualifiedItemId, checkDonatedItems: false))
                    .Select(i => i.DisplayName).Distinct().ToList();
                yield return ActionResult.Fail("背包里没有可以捐给博物馆的东西（矿物、古物，而且博物馆还没有的），也没有待领的奖励"
                    + (dup.Count > 0 ? $"。背包里的 {string.Join("、", dup.Take(10))} 博物馆已经有了（每种只收一件），可以卖掉或留着送人" : "")
                    + $"。博物馆现在 {museum.museumPieces.Length}/{LibraryMuseum.totalArtifacts} 件");
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
                    int vaultCost = Knowledge.VaultCost(b);
                    if (vaultCost > 0)
                    {
                        string canPay = Player.Money >= vaultCost
                            ? "；钱够，可用 donate_bundles pay_vault=true 交钱"
                            : $"；当前 {Player.Money}g，还差 {vaultCost - Player.Money}g";
                        lines.Add($"  {b.DisplayName}：需要 {vaultCost}g{canPay}");
                        shown++;
                        continue;
                    }
                    // 鱼带上季节、天气、时间、在哪钓，免得在错的季节、错的水域干钓
                    var missing = b.Ingredients.Where(i => !i.completed).Select(i =>
                    {
                        string n = Knowledge.IngredientName(i);
                        string q = i.category.HasValue || i.id == null ? null : i.id.StartsWith("(") ? i.id : "(O)" + i.id;
                        return Knowledge.FishWhereWhen(q) is string fw ? $"{n}（{fw}）" : n;
                    }).ToList();
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

        /// <summary>按从便宜到贵、在余额和预算内挑选这一趟要交钱的金库收集包。</summary>
        internal static List<(int Index, string Name, int Cost)> PlanVaultPayments(
            IEnumerable<(int Index, string Name, int Cost)> candidates, int money, int? budget)
        {
            var plan = new List<(int Index, string Name, int Cost)>();
            if (candidates == null)
                return plan;
            int remaining = Math.Max(0, money);
            if (budget.HasValue)
                remaining = Math.Min(remaining, Math.Max(0, budget.Value));
            foreach (var item in candidates.Where(c => c.Cost > 0).OrderBy(c => c.Cost).ThenBy(c => c.Index))
            {
                if (item.Cost > remaining)
                    break;
                plan.Add(item);
                remaining -= item.Cost;
            }
            return plan;
        }

        /// <summary>
        /// 去社区中心，把背包里能交的材料交进对应收集包，领取完成奖励；payVault=true 时也给已解锁的金库（Vault）收集包交钱。
        /// 走的是祝尼魔便条界面（JunimoNoteMenu）自己的提交/购买逻辑，完成动画、奖励、修复房间都和手动一样。
        /// </summary>
        public IEnumerable<object> DonateBundles(bool payVault = false, int? budget = null, bool confirm = false)
        {
            string why = Knowledge.CommunityCenterUnavailable();
            if (why != null)
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            if (budget.HasValue && budget.Value < 0)
            {
                yield return ActionResult.Fail("budget 不能为负数");
                yield break;
            }
            var allBundles = Knowledge.Bundles();
            var targets = allBundles
                .Where(b => !b.Complete && Knowledge.IsBundleAvailable(b) && Knowledge.InventoryMatches(b).Count > 0)
                .Select(b => b.Area).Distinct().ToList();
            var vaultAvailable = allBundles
                .Where(b => !b.Complete && Knowledge.IsBundleAvailable(b) && Knowledge.VaultCost(b) > 0)
                .Select(b => (b.Index, Name: b.DisplayName, Cost: Knowledge.VaultCost(b)))
                .ToList();
            var vaultPlanned = payVault ? PlanVaultPayments(vaultAvailable, Player.Money, budget) : new List<(int Index, string Name, int Cost)>();
            var vaultAffordableHint = !payVault ? PlanVaultPayments(vaultAvailable, Player.Money, null) : new List<(int Index, string Name, int Cost)>();
            var cc = Game1.RequireLocation<CommunityCenter>("CommunityCenter");
            bool rewardsWaiting = cc.bundleRewards.Pairs.Any(p => p.Value);

            if (targets.Count == 0 && vaultPlanned.Count == 0 && !rewardsWaiting)
            {
                if (payVault)
                {
                    if (vaultAvailable.Count == 0)
                    {
                        yield return ActionResult.Fail(cc.areasComplete[CommunityCenter.AREA_Vault]
                            ? "金库（Vault）收集包已经全部完成，背包里也没有其他可交材料"
                            : "金库（Vault）还没解锁（先完成任意 4 个其他收集包才会出现金库便条），背包里也没有其他可交材料");
                    }
                    else
                    {
                        int cheapest = vaultAvailable.Min(v => v.Cost);
                        yield return ActionResult.Fail(budget.HasValue && budget.Value < cheapest
                            ? $"budget={budget.Value}g 不够交最便宜的金库收集包（需要 {cheapest}g）"
                            : $"当前 {Player.Money}g，不够交最便宜的金库收集包（需要 {cheapest}g，还差 {cheapest - Player.Money}g）");
                    }
                    yield break;
                }
                if (vaultAffordableHint.Count > 0)
                {
                    yield return ActionResult.Fail($"背包里没有能交给已解锁收集包的材料；金库（Vault）有钱够交的收集包（{string.Join("、", vaultAffordableHint.Select(v => $"{v.Name} {v.Cost}g"))}），要交钱请传 pay_vault=true");
                    yield break;
                }
                yield return ActionResult.Fail("背包里没有能交给已解锁收集包的材料（用 bundle_info 看看缺什么）");
                yield break;
            }

            string vaultSkippedNote = null;
            if (vaultPlanned.Count > 0)
            {
                int totalCost = vaultPlanned.Sum(v => v.Cost);
                string desc = $"社区中心金库收集包（{string.Join("、", vaultPlanned.Select(v => $"{v.Name} {v.Cost}g"))}）";
                if (!CheckSharedMoneySpend(totalCost, desc, confirm, out string moneyMsg))
                {
                    vaultPlanned.Clear();
                    if (targets.Count == 0 && !rewardsWaiting)
                    {
                        yield return ActionResult.Fail(moneyMsg);
                        yield break;
                    }
                    vaultSkippedNote = moneyMsg;
                }
            }

            // 如果之前手动点卷轴把收集包界面打开着，先关掉再走流程，避免寻路或 Blocked 被卡住
            bool closedOpenNote = false;
            if (Game1.activeClickableMenu is JunimoNoteMenu openNote && openNote.isReadyToCloseMenuOrBundle())
            {
                openNote.exitThisMenu();
                closedOpenNote = true;
                yield return null;
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
            if (vaultPlanned.Count > 0 && !targets.Contains(CommunityCenter.AREA_Vault))
                targets.Add(CommunityCenter.AREA_Vault);
            // 有奖励待领的区域也要打开一次便条
            var areas = targets.Concat(allBundles.Where(b => cc.bundleRewards.TryGetValue(b.Index, out bool r) && r).Select(b => b.Area))
                .Where(a => a >= 0 && a <= 5).Distinct().ToList();
            var plannedVaultIds = vaultPlanned.Select(v => v.Index).ToHashSet();
            int vaultBudgetLeft = budget.HasValue ? Math.Max(0, budget.Value) : int.MaxValue;

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

                // 之前刚关掉收集包界面时，CommunityCenter.UpdateWhenCurrentLocation 先调 bundleMutex.Update 再 ReleaseLock，
                // 导致释放锁那一帧 NetMutex.prevOwner 仍留在原持有者；必须等锁释放后再多等 2 帧让 prevOwner 同步为 -1，
                // 否则立即 RequestLock 会因为 owner == prevOwner 而漏调 onLockAcquired 回调，界面打不开。
                if (area >= 0 && area < cc.bundleMutexes.Count)
                {
                    bool wasLocked = cc.bundleMutexes[area].IsLocked();
                    for (int t = 0; t < 60 && cc.bundleMutexes[area].IsLocked(); t++)
                        yield return null;
                    if (wasLocked || closedOpenNote)
                    {
                        foreach (var s in WaitTicks(2))
                            yield return s;
                        closedOpenNote = false;
                    }
                }
                cc.checkBundle(area);
                JunimoNoteMenu menu = null;
                for (int t = 0; t < 120 && menu == null; t++)
                {
                    menu = Game1.activeClickableMenu as JunimoNoteMenu;
                    yield return null;
                }
                if (menu == null)
                    continue;

                if (menu.whichArea == CommunityCenter.AREA_Vault)
                {
                    if (plannedVaultIds.Count > 0)
                    {
                        foreach (var bundle in menu.bundles
                            .OrderBy(b => Knowledge.VaultCost(CommunityCenter.AREA_Vault, b.ingredients))
                            .ThenBy(b => b.bundleIndex)
                            .ToList())
                        {
                            if (bundle.complete || !bundle.depositsAllowed || Game1.activeClickableMenu != menu || !plannedVaultIds.Contains(bundle.bundleIndex))
                                continue;
                            int cost = Knowledge.VaultCost(CommunityCenter.AREA_Vault, bundle.ingredients);
                            if (cost <= 0 || Player.Money < cost || cost > vaultBudgetLeft)
                                continue;
                            reflection.GetMethod(menu, "setUpBundleSpecificPage").Invoke(bundle);
                            yield return null;
                            if (Game1.activeClickableMenu != menu || menu.purchaseButton == null)
                            {
                                if (Game1.activeClickableMenu == menu && menu.specificBundlePage)
                                    menu.takeDownBundleSpecificPage();
                                continue;
                            }
                            // 走原版 JunimoNoteMenu.receiveLeftClick 点击 purchaseButton：扣钱、标完成、放奖励、全部完成时设修复过场
                            menu.receiveLeftClick(menu.purchaseButton.bounds.Center.X, menu.purchaseButton.bounds.Center.Y);
                            for (int t = 0; t < 120 && Game1.activeClickableMenu == menu && menu.specificBundlePage; t++)
                                yield return null;
                            // 原版金库付款只置 bundles[id][0]=true（数组长 3，其余两位保持 false），cc.isBundleComplete 会误判为 false；看 bundle.complete 或首槽位
                            if (bundle.complete || (cc.bundles.TryGetValue(bundle.bundleIndex, out var vaultSlots) && vaultSlots.Length > 0 && vaultSlots[0]))
                            {
                                deposited.Add($"{cost}g→{bundle.label}");
                                completed.Add(bundle.label);
                                if (vaultBudgetLeft != int.MaxValue)
                                    vaultBudgetLeft -= cost;
                            }
                            if (Game1.activeClickableMenu == menu && menu.specificBundlePage)
                                menu.takeDownBundleSpecificPage();
                            yield return null;
                        }
                    }
                }
                else
                {
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
            msg.Add(deposited.Count > 0 ? $"交了 {deposited.Count} 项：{string.Join("、", deposited)}" : "没有交上材料或资金");
            if (keptForQuest.Count > 0)
                msg.Add("这些收集包也要、但先留给任务没交：" + string.Join("、", keptForQuest.Distinct()) + "。交完任务、或者再弄到更多，再 donate_bundles");
            if (vaultSkippedNote != null)
                msg.Add("金库未交钱：" + vaultSkippedNote);
            else if (!payVault && vaultAffordableHint.Count > 0)
                msg.Add($"金库还有钱够交的收集包（{string.Join("、", vaultAffordableHint.Select(v => $"{v.Name} {v.Cost}g"))}），要交钱请传 pay_vault=true");
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
