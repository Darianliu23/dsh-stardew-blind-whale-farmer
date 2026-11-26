using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using StardewValley;

partial class Program
{
    static void TestGiftInfoReverse(Type tActions, JsonArray schema, HashSet<string> readOnly)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // 1. Schema 与只读注册检查
        var giftFn = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "gift_info")?["function"];
        var giftParams = giftFn?["parameters"];
        var giftProps = giftParams?["properties"];
        Check(giftParams?["required"] is JsonArray { Count: 0 }
            && giftProps?["npc"]?["type"]?.GetValue<string>() == "string"
            && giftProps?["chests"]?["type"]?.GetValue<string>() == "boolean"
            && readOnly.Contains("gift_info"),
            "gift_info 的 schema：npc 和 chests 均可选，且注册为只读工具");
        string desc = giftFn?["description"]?.GetValue<string>() ?? "";
        Check(desc.Contains("npc") && desc.Contains("反查") && desc.Contains("chests"),
            "gift_info 工具说明写清传 npc 与不传反查两种用法：" + Shorten(desc));

        var npcType = tActions.GetNestedType("GiftCandidateNpc", Any);
        var itemType = tActions.GetNestedType("GiftCandidateItem", Any);
        var canGiftToday = tActions.GetMethod("CanGiftToday", Any);
        var buildSummary = tActions.GetMethod("BuildReverseGiftSummary", Any);

        object MakeNpc(string internalName, string displayName, string loc, bool canSocialize, bool isBirthday,
            bool isMarried, bool isDivorced, bool isMet, int giftsToday, int giftsThisWeek) =>
            Activator.CreateInstance(npcType, internalName, displayName, loc, canSocialize, isBirthday,
                isMarried, isDivorced, isMet, giftsToday, giftsThisWeek);

        object MakeItem(string key, string displayName, string source, bool isFromChest, bool isReserved, string reason = null) =>
            Activator.CreateInstance(itemType, key, displayName, source, isFromChest, isReserved, reason);

        object MakeList(Type elemType, IEnumerable<object> items)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType));
            foreach (var it in items)
                list.Add(it);
            return list;
        }

        Delegate MakeTasteFunc(Func<string, string, int> fn)
        {
            // Func<GiftCandidateNpc, GiftCandidateItem, int>
            var pNpc = System.Linq.Expressions.Expression.Parameter(npcType, "n");
            var pItem = System.Linq.Expressions.Expression.Parameter(itemType, "i");
            var npcName = System.Linq.Expressions.Expression.Property(pNpc, "InternalName");
            var itemKey = System.Linq.Expressions.Expression.Property(pItem, "Key");
            var invoke = System.Linq.Expressions.Expression.Invoke(
                System.Linq.Expressions.Expression.Constant(fn), npcName, itemKey);
            var delType = typeof(Func<,,>).MakeGenericType(npcType, itemType, typeof(int));
            return System.Linq.Expressions.Expression.Lambda(delType, invoke, pNpc, pItem).Compile();
        }

        string RunSummary(IEnumerable<object> npcs, IEnumerable<object> items, Func<string, string, int> tasteFn,
            bool includeChests = false, int maxItems = 12, int maxNpcs = 5) =>
            (string)buildSummary.Invoke(null, new object[]
            {
                MakeList(npcType, npcs),
                MakeList(itemType, items),
                MakeTasteFunc(tasteFn),
                includeChests,
                maxItems,
                maxNpcs
            });

        // 2. CanGiftToday 过滤规则
        var abigailBday = MakeNpc("Abigail", "阿比盖尔", "Town", true, true, false, false, true, 0, 2); // 生日且本周已送满 2 次：仍可送
        var maruOk = MakeNpc("Maru", "玛鲁", "ScienceHouse", true, false, false, false, true, 0, 1);
        var haleyDoneToday = MakeNpc("Haley", "海莉", "Town", true, false, false, false, true, 1, 1); // 今天送过：排除
        var samDoneWeek = MakeNpc("Sam", "山姆", "Town", true, false, false, false, true, 0, 2); // 本周满 2 次：排除
        var leahSpouse = MakeNpc("Leah", "莉亚", "FarmHouse", true, false, true, false, true, 0, 2); // 配偶本周满 2 次：仍可送
        var bdayAlreadyGifted = MakeNpc("Penny", "潘妮", "Town", true, true, false, false, true, 1, 2); // 生日但今天已送过：排除
        var nonSocial = MakeNpc("Marlon", "马龙", "AdventureGuild", false, false, false, false, true, 0, 0); // 不能社交：排除
        var divorced = MakeNpc("Shane", "谢恩", "Saloon", true, false, false, true, true, 0, 0); // 离婚：排除

        Check((bool)canGiftToday.Invoke(null, new[] { abigailBday }), "生日村民本周送满 2 次仍可送");
        Check((bool)canGiftToday.Invoke(null, new[] { maruOk }), "普通可社交村民今天未送且本周未满可送");
        Check(!(bool)canGiftToday.Invoke(null, new[] { haleyDoneToday }), "今天送过的村民不再出现");
        Check(!(bool)canGiftToday.Invoke(null, new[] { samDoneWeek }), "本周送满 2 次的非生日非配偶村民不再出现");
        Check((bool)canGiftToday.Invoke(null, new[] { leahSpouse }), "配偶不受本周 2 次限制");
        Check(!(bool)canGiftToday.Invoke(null, new[] { bdayAlreadyGifted }), "生日村民今天已送过不再出现");
        Check(!(bool)canGiftToday.Invoke(null, new[] { nonSocial }), "不能社交的村民不出现");
        Check(!(bool)canGiftToday.Invoke(null, new[] { divorced }), "已离婚的村民不出现");

        // 3. 反查输出：生日排最前、跳过已送/满次数/讨厌/留着物品、默认不看箱子、chests=true 算进箱子并标坐标
        var allNpcs = new[] { maruOk, haleyDoneToday, samDoneWeek, abigailBday, leahSpouse, nonSocial };
        var items = new[]
        {
            MakeItem("(O)72", "钻石", "背包", false, false),
            MakeItem("(O)66", "紫水晶", "Farm(60,15)", true, false),
            MakeItem("(O)18", "黄水仙", "背包", false, true, "玩家要求保留"),
            MakeItem("(O)168", "垃圾", "背包", false, false),
        };
        int Taste(string npc, string key) => (npc, key) switch
        {
            ("Abigail", "(O)66") => NPC.gift_taste_love,
            ("Abigail", "(O)72") => NPC.gift_taste_like,
            ("Maru", "(O)72") => NPC.gift_taste_love,
            ("Leah", "(O)72") => NPC.gift_taste_like,
            ("Haley", "(O)72") => NPC.gift_taste_love,
            ("Sam", "(O)72") => NPC.gift_taste_love,
            ("Maru", "(O)18") => NPC.gift_taste_like,
            (_, "(O)168") => NPC.gift_taste_hate,
            _ => NPC.gift_taste_neutral
        };

        string bagOnly = RunSummary(allNpcs, items, Taste, includeChests: false);
        Check(bagOnly.Contains("钻石") && bagOnly.Contains("最爱 → 玛鲁") && bagOnly.Contains("喜欢 → 阿比盖尔（今天生日）"),
            "不传 npc 时从背包反查并列出最爱和喜欢");
        Check(!bagOnly.Contains("海莉") && !bagOnly.Contains("山姆") && !bagOnly.Contains("马龙"),
            "今天送过、本周满 2 次、不可社交的村民不出现在结果里");
        Check(!bagOnly.Contains("垃圾") && !bagOnly.Contains("黄水仙") && bagOnly.Contains("已跳过 1 件交任务/【留着】物品"),
            "讨厌物品不列，【留着】/交任务物品跳过并说明");
        Check(!bagOnly.Contains("紫水晶") && !bagOnly.Contains("Farm(60,15)"),
            "默认 chests=false 不包含箱子里的物品");
        int bdayPos = bagOnly.IndexOf("阿比盖尔（今天生日", StringComparison.Ordinal);
        int itemPos = bagOnly.IndexOf("- 钻石", StringComparison.Ordinal);
        Check(bdayPos >= 0 && bdayPos < itemPos, "生日村民排在最前面并注明“今天生日”");
        int abigailInLike = bagOnly.IndexOf("喜欢 → 阿比盖尔（今天生日）、莉亚", StringComparison.Ordinal);
        Check(abigailInLike >= 0, "同一条喜欢列表里生日村民也排在最前面");

        string withChests = RunSummary(allNpcs, items, Taste, includeChests: true);
        Check(withChests.Contains("紫水晶（Farm(60,15)）") && withChests.Contains("最爱 → 阿比盖尔（今天生日）"),
            "chests=true 把箱子里的物品算进来并标明箱子坐标");
        int amethystLine = withChests.IndexOf("- 紫水晶（Farm(60,15)）", StringComparison.Ordinal);
        int diamondLine = withChests.IndexOf("- 钻石（背包）", StringComparison.Ordinal);
        Check(amethystLine >= 0 && diamondLine > amethystLine, "命中生日村民最爱的物品排在前面");

        // 4. 输出上限：物品行数上限 + 每件物品人数上限
        var manyNpcs = Enumerable.Range(1, 8)
            .Select(i => MakeNpc($"Npc{i}", $"村民{i}", "Town", true, i == 8, false, false, true, 0, 0))
            .ToList();
        var manyItems = Enumerable.Range(1, 15)
            .Select(i => MakeItem($"(O){1000 + i}", $"宝石{i:D2}", "背包", false, false))
            .ToList();
        string capped = RunSummary(manyNpcs, manyItems, (_, _) => NPC.gift_taste_love,
            includeChests: false, maxItems: 5, maxNpcs: 3);
        var itemLines = capped.Split('\n').Where(l => l.StartsWith("- ")).ToArray();
        Check(itemLines.Length == 5 && capped.Contains("还有 10 件没列，可传 npc 单独查"),
            "物品超过上限时截断并提示“还有 N 件没列，可传 npc 单独查”");
        Check(itemLines[0].Contains("村民8（今天生日）") && itemLines[0].Contains("等 8 人"),
            "每件物品列的人数有上限且生日村民排首位");
    }
}
