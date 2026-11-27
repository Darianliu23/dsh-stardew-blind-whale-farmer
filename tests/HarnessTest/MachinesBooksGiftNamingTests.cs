using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SObject = StardewValley.Object;

partial class Program
{
    static void TestMachinesBooksGiftNaming(Assembly asm, Type tActions, JsonArray schema, HashSet<string> readOnly, Func<string, string, Task<string>> run)
    {
        const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ── 1. collect_machines ──
        var collectSchema = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "collect_machines");
        Check(collectSchema != null, "schema 包含 collect_machines 工具");
        var collectParams = collectSchema?["function"]?["parameters"];
        Check(collectParams?["properties"] is JsonObject cProps && cProps["location"] != null && cProps["item"] != null
            && collectParams["required"] is JsonArray { Count: 0 },
            "collect_machines 参数为可选的 location 和 item");
        Check(!readOnly.Contains("collect_machines"), "collect_machines 不是只读工具");

        foreach (string json in new[] { "{}", "{\"location\":\"FarmCave\"}", "{\"place\":\"Farm\",\"machine\":\"小桶\"}" })
        {
            string rr = run("collect_machines", json).GetAwaiter().GetResult();
            Check(rr.Contains("游戏世界未就绪"), $"collect_machines 参数和别名通过校验 {json}：{Shorten(rr)}");
        }

        var matchFilter = tActions.GetMethod("MatchesMachineFilter", Any);
        bool MatchM(string spec, string mDisp, string mName, string mId, string hDisp, string hName, string hId) =>
            (bool)matchFilter.Invoke(null, new object[] { spec, mDisp, mName, mId, hDisp, hName, hId });

        Check(MatchM(null, "小桶", "Keg", "(BC)12", "葡萄酒", "Wine", "(O)348"), "collect_machines 空筛选匹配所有机器");
        Check(MatchM("小桶", "小桶", "Keg", "(BC)12", "葡萄酒", "Wine", "(O)348"), "collect_machines 按机器中文名筛选");
        Check(MatchM("keg", "小桶", "Keg", "(BC)12", "葡萄酒", "Wine", "(O)348"), "collect_machines 按机器英文名筛选");
        Check(MatchM("葡萄酒", "小桶", "Keg", "(BC)12", "葡萄酒", "Wine", "(O)348"), "collect_machines 按产物名筛选");
        Check(MatchM("紫蘑菇", "蘑菇盒", "Mushroom Box", "(BC)128", "紫蘑菇", "Purple Mushroom", "(O)422"), "collect_machines 匹配山洞蘑菇盒产物");
        Check(!MatchM("熔炉", "小桶", "Keg", "(BC)12", "葡萄酒", "Wine", "(O)348"), "collect_machines 不匹配其他机器");

        var fmtCollect = tActions.GetMethod("FormatCollectMachinesSummary", Any);
        string summary1 = (string)fmtCollect.Invoke(null, new object[]
        {
            8,
            new (string id, string name, int quality, int quantity)[]
            {
                ("(O)348", "葡萄酒", 0, 5),
                ("(O)422", "紫蘑菇", 4, 3),
            },
            0, 0, 0, false
        });
        Check(summary1.Contains("收了 8 台机器") && summary1.Contains("葡萄酒x5") && summary1.Contains("紫蘑菇（铱星）x3"),
            "collect_machines 汇总按产物和品质计数：" + summary1);

        string summaryFull = (string)fmtCollect.Invoke(null, new object[]
        {
            3,
            new (string id, string name, int quality, int quantity)[] { ("(O)348", "葡萄酒", 0, 3) },
            1, 0, 4, true
        });
        Check(summaryFull.Contains("跳过 1 台（走不到）") && summaryFull.Contains("背包满了") && summaryFull.Contains("还剩 4 台"),
            "collect_machines 背包满且有跳过时完整回报：" + summaryFull);

        // ── 2. 书籍分类、商店标注、防重复买与防误读 ──
        var fmtBookTag = tActions.GetMethod("FormatShopBookTag", Any);
        var checkBuyBook = tActions.GetMethod("CheckBuyBookBlock", Any);
        var checkUseBook = tActions.GetMethod("CheckUseBookBlock", Any);

        string Tag(int cat, string id, bool read) => (string)fmtBookTag.Invoke(null, new object[] { cat, id, read });
        string BuyBlk(int cat, string id, string name, int cnt, bool read, bool force) =>
            (string)checkBuyBook.Invoke(null, new object[] { cat, id, name, cnt, read, force });
        string UseBlk(int cat, string id, string name, bool read, bool force) =>
            (string)checkUseBook.Invoke(null, new object[] { cat, id, name, read, force });

        Check(Tag(SObject.booksCategory, "Book_Trash", true).Contains("已读过"), "商店标注已读能力书");
        Check(Tag(SObject.booksCategory, "Book_PriceCatalogue", true).Contains("再读无效果"), "商店标注价格目录已读且再读无效果");
        Check(Tag(SObject.booksCategory, "Book_Trash", false).Contains("永久生效"), "商店标注未读能力书");
        Check(Tag(SObject.skillBooksCategory, "SkillBook_0", false).Contains("可重复读"), "商店标注技能书可重复读");
        Check(Tag(SObject.skillBooksCategory, "PurpleBook", false).Contains("星之书") && Tag(SObject.skillBooksCategory, "PurpleBook", false).Contains("可重复读"), "商店标注星之书（skillBooksCategory）可重复读");

        Check(BuyBlk(SObject.booksCategory, "Book_Trash", "小巷自助餐", 1, true, false) is string b1 && b1.Contains("force=true"),
            "buy 拒绝重复购买已读能力书");
        Check(BuyBlk(SObject.booksCategory, "Book_PriceCatalogue", "价格目录", 1, true, false) is string b2 && b2.Contains("没有任何效果"),
            "buy 拒绝重复购买价格目录并说明无效果");
        Check(BuyBlk(SObject.booksCategory, "Book_Trash", "小巷自助餐", 1, true, true) == null,
            "buy 传 force=true 允许购买已读能力书");
        Check(BuyBlk(SObject.booksCategory, "Book_Trash", "小巷自助餐", 1, false, false) == null,
            "buy 允许购买 1 本未读能力书");
        Check(BuyBlk(SObject.booksCategory, "Book_Trash", "小巷自助餐", 2, false, false) is string b3 && b3.Contains("force=true"),
            "buy 拒绝一次购买多本能力书");
        Check(BuyBlk(SObject.skillBooksCategory, "SkillBook_2", "伐木秘事", 3, true, false) == null,
            "buy 不拦技能书重复购买");
        Check(BuyBlk(SObject.booksCategory, "PurpleBook", "星之书", 2, false, false) == null,
            "buy 不拦星之书重复购买");

        Check(UseBlk(SObject.booksCategory, "Book_Trash", "小巷自助餐", true, false) is string u1 && u1.Contains("force=true"),
            "use_item 拒绝误读已读能力书");
        Check(UseBlk(SObject.booksCategory, "Book_AnimalCatalogue", "动物目录", true, false) is string u2 && u2.Contains("没有任何效果"),
            "use_item 拒绝误读已读动物目录");
        Check(UseBlk(SObject.booksCategory, "Book_Trash", "小巷自助餐", true, true) == null,
            "use_item 传 force=true 允许读已读能力书");
        Check(UseBlk(SObject.booksCategory, "Book_Trash", "小巷自助餐", false, false) == null,
            "use_item 允许读未读能力书");
        Check(UseBlk(SObject.skillBooksCategory, "SkillBook_0", "星露谷年历", true, false) == null,
            "use_item 允许重复读技能书");
        Check(UseBlk(SObject.booksCategory, "PurpleBook", "星之书", false, false) == null,
            "use_item 允许重复读星之书");

        // ── 3. 联机主动给玩家送礼 ──
        var shouldGift = tActions.GetMethod("ShouldPromptProactiveGift", Any);
        bool PromptGift(bool en, bool mp, bool other, bool safe, float chance, int cd, int today, int lastGift, int lastRoll, bool active, double roll) =>
            (bool)shouldGift.Invoke(null, new object[] { en, mp, other, safe, chance, cd, today, lastGift, lastRoll, active, roll });

        Check(!PromptGift(true, false, true, true, 1f, 1, 10, int.MinValue, int.MinValue, false, 0.0), "单机不触发主动送礼");
        Check(!PromptGift(false, true, true, true, 1f, 1, 10, int.MinValue, int.MinValue, false, 0.0), "关掉开关不触发主动送礼");
        Check(!PromptGift(true, true, false, true, 1f, 1, 10, int.MinValue, int.MinValue, false, 0.0), "无在线玩家不触发主动送礼");
        Check(!PromptGift(true, true, true, false, 1f, 1, 10, int.MinValue, int.MinValue, false, 0.0), "不安全场景不触发主动送礼");
        Check(!PromptGift(true, true, true, true, 1f, 1, 10, 10, int.MinValue, false, 0.0), "当天送过礼后冷却内不再触发");
        Check(!PromptGift(true, true, true, true, 1f, 3, 12, 10, int.MinValue, false, 0.0), "多日冷却未满不触发");
        Check(PromptGift(true, true, true, true, 0.5f, 3, 13, 10, int.MinValue, false, 0.2), "冷却满且命中概率时触发");
        Check(!PromptGift(true, true, true, true, 0.5f, 1, 13, 10, int.MinValue, false, 0.8), "未命中概率不触发");
        Check(!PromptGift(true, true, true, true, 1f, 1, 13, int.MinValue, 13, false, 0.0), "当天已摇出不触发则不再重复摇");
        Check(PromptGift(true, true, true, true, 0f, 1, 13, int.MinValue, 13, true, 0.99), "当天已触发则送出前保持提示");

        var scoreGift = tActions.GetMethod("ScoreProactiveGiftItem", Any);
        int Score(bool canDrop, bool reserved, int cat) => (int)scoreGift.Invoke(null, new object[] { canDrop, reserved, cat });
        Check(Score(false, false, SObject.CookingCategory) == 0, "不可丢弃物品（如工具/任务物）不作为主动送礼候选");
        Check(Score(true, true, SObject.flowersCategory) == 0, "【留着】的物品不作为主动送礼候选");
        Check(Score(true, false, SObject.CookingCategory) > Score(true, false, SObject.flowersCategory)
            && Score(true, false, SObject.flowersCategory) > Score(true, false, SObject.FishCategory)
            && Score(true, false, SObject.FishCategory) > Score(true, false, SObject.GreensCategory),
            "主动送礼候选优先级：料理 > 花 > 鱼 > 采集品");

        var dropSchema = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "drop_item");
        var dropProps = dropSchema?["function"]?["parameters"]?["properties"] as JsonObject;
        Check(dropProps != null && dropProps["message"] != null && dropProps["emote"] != null,
            "drop_item 支持 message 和 emote 参数一步送礼说话发表情");

        // ── 4. 买动物/新生动物食物名字库起名 ──
        var cuteNames = (string[])tActions.GetField("CuteAnimalNames", Any).GetValue(null);
        var pickName = tActions.GetMethod("PickAnimalName", Any);
        string Pick(string pref, HashSet<string> taken, Random rng) =>
            (string)pickName.Invoke(null, new object[] { pref, (Func<string, bool>)(s => taken.Contains(s)), rng });

        var takenSet = new HashSet<string>(StringComparer.Ordinal);
        Check(Pick("饭团", takenSet, new Random(1)) == "饭团", "buy_animal 传了未重名名字时直接采用");
        takenSet.Add("饭团");
        Check(Pick("饭团", takenSet, new Random(1)) == "饭团2", "buy_animal 传了重名名字时自动加后缀");
        string auto1 = Pick(null, takenSet, new Random(42));
        Check(cuteNames.Contains(auto1) && !takenSet.Contains(auto1), "buy_animal 未传名字时从食物名字库挑不重名：" + auto1);

        var allTaken = new HashSet<string>(cuteNames, StringComparer.Ordinal);
        string overflow = Pick(null, allTaken, new Random(7));
        Check(!allTaken.Contains(overflow) && overflow.EndsWith("2"), "食物名字库全占满时自动加数字后缀：" + overflow);

        var fmtNaming = tActions.GetMethod("FormatNamingMenuPrompt", Any);
        string animalPrompt = (string)fmtNaming.Invoke(null, new object[] { false });
        string childPrompt = (string)fmtNaming.Invoke(null, new object[] { true });
        Check(animalPrompt.Contains("动物") && animalPrompt.Contains("饭团") && animalPrompt.Contains("set_name"),
            "新生动物起名提示鼓励 AI 自己起食物名：" + animalPrompt);
        Check(childPrompt.Contains("孩子") && !childPrompt.Contains("饭团"),
            "孩子起名提示保持现状：" + childPrompt);

        // ── 5. 未开通地窖不误扫预摆木桶 ──
        var isVanillaCellar = tActions.GetMethod("IsVanillaCellarName", Any);
        bool IsCellarName(string s) => (bool)isVanillaCellar.Invoke(null, new object[] { s });
        Check(IsCellarName("Cellar"), "IsVanillaCellarName 识别原版主地窖 Cellar");
        Check(IsCellarName("Cellar2") && IsCellarName("cellar12"), "IsVanillaCellarName 识别联机地窖 Cellar2/Cellar12");
        Check(!IsCellarName("Custom_Cellar") && !IsCellarName("CellarDoor") && !IsCellarName("FarmHouse") && !IsCellarName(""),
            "IsVanillaCellarName 不误伤自定义地图名或非地窖地图");

        var shouldScan = tActions.GetMethod("ShouldScanLocationDirectlyForMachines", Any);
        bool ScanDirect(bool temp, bool warp, bool cellar, bool farm, bool gh, int bld, bool hasM) =>
            (bool)shouldScan.Invoke(null, new object[] { temp, warp, cellar, farm, gh, bld, hasM });
        Check(!ScanDirect(false, false, true, false, false, 0, true),
            "原版地窖即使预摆了 33 个木桶也不从 Game1.locations 直接入队（只能由 3 级农舍/小屋引入）");
        Check(ScanDirect(false, false, false, false, false, 0, true),
            "SVE 等扩展模组的自定义酒窖/房间有机器时仍允许直接入队");
        Check(ScanDirect(false, false, false, true, false, 0, false) && ScanDirect(false, false, false, false, true, 0, false),
            "农场和温室即使暂时没机器也照常扫描");
        Check(!ScanDirect(true, false, false, true, false, 1, true) && !ScanDirect(false, true, false, true, false, 1, true),
            "临时地图和后台 WarpRoom 不参与机器扫描");
    }
}
