using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 使用背包里的东西 ─────────────────────────

        /// <summary>拿在手里“使用”就有用的东西：书（技能书、能力书）、秘密纸条、图腾、马笛。吃东西用 eat，晶球和神秘盒找克林特 crack_geodes。</summary>
        internal static bool IsUsable(Item item) =>
            item is SObject o && !o.bigCraftable.Value
            && (o.Category is SObject.booksCategory or SObject.skillBooksCategory
                || o.QualifiedItemId is "(O)79" or "(O)842" or "(O)911"
                || o.Name.Contains("Totem"));

        private static bool IsBook(Item item) => item is SObject { Category: SObject.booksCategory or SObject.skillBooksCategory };

        private static bool IsSecretNote(Item item) => item?.QualifiedItemId is "(O)79" or "(O)842";

        internal enum BookKind { None, SkillBook, PurpleBook, PowerBook }

        /// <summary>按 Object.readBook（SDV 1.6）区分书籍类型：SkillBook_* 和 PurpleBook 可重复读加经验，其余 booksCategory 是一次性能力书。</summary>
        internal static BookKind ClassifyBook(int category, string itemId)
        {
            if (string.Equals(itemId, "PurpleBook", System.StringComparison.Ordinal))
                return BookKind.PurpleBook;
            if (category == SObject.skillBooksCategory
                || (!string.IsNullOrEmpty(itemId) && itemId.StartsWith("SkillBook_", System.StringComparison.Ordinal)))
                return BookKind.SkillBook;
            if (category == SObject.booksCategory)
                return BookKind.PowerBook;
            return BookKind.None;
        }

        internal static bool IsZeroEffectOnRereadPowerBook(string itemId) =>
            string.Equals(itemId, "Book_PriceCatalogue", System.StringComparison.Ordinal)
            || string.Equals(itemId, "Book_AnimalCatalogue", System.StringComparison.Ordinal);

        /// <summary>商店商品列表里给书籍加的标注。</summary>
        internal static string FormatShopBookTag(int category, string itemId, bool alreadyRead) =>
            ClassifyBook(category, itemId) switch
            {
                BookKind.SkillBook => "（技能书，加250经验，可重复读）",
                BookKind.PurpleBook => "（星之书，五项各加250经验，可重复读）",
                BookKind.PowerBook when alreadyRead => IsZeroEffectOnRereadPowerBook(itemId)
                    ? "【已读过，再读无效果】"
                    : "【已读过】",
                BookKind.PowerBook => "（能力书，读一次永久生效）",
                _ => ""
            };

        /// <summary>买书拦截检查：已读能力书（或一次买多本能力书）默认拒绝，传 force=true 才放行；技能书和星之书不拦。</summary>
        internal static string CheckBuyBookBlock(int category, string itemId, string displayName, int count, bool alreadyRead, bool force)
        {
            if (force || ClassifyBook(category, itemId) != BookKind.PowerBook)
                return null;
            if (alreadyRead)
            {
                string detail = IsZeroEffectOnRereadPowerBook(itemId)
                    ? "再读没有任何效果（也不加经验）"
                    : "永久能力不会叠加，再读只给少量经验（100 点），花钱重买很不划算";
                return $"先别买：《{displayName}》已经读过了，{detail}。如果是要买来送人或交易，请传 force=true";
            }
            if (count > 1)
                return $"先别买多本：《{displayName}》是能力书，读 1 本就永久生效，多买的读了只给少量经验。只买 1 本即可；真要多买（如送人）请传 force=true";
            return null;
        }

        /// <summary>use_item 读背包里的已读能力书时拦截，避免误消耗；传 force=true 才放行。</summary>
        internal static string CheckUseBookBlock(int category, string itemId, string displayName, bool alreadyRead, bool force)
        {
            if (force || ClassifyBook(category, itemId) != BookKind.PowerBook || !alreadyRead)
                return null;
            string detail = IsZeroEffectOnRereadPowerBook(itemId)
                ? "再读没有任何效果（也不加经验），会白白消耗掉"
                : "永久能力不会叠加，再读只给少量经验（100 点），更适合卖掉、送人或在书商处以物易物";
            return $"先别读：《{displayName}》已经读过了，{detail}。确定要读请传 force=true";
        }

        /// <summary>背包里没读的书、秘密纸条（状态里提醒一句）；没有返回 null。</summary>
        public static string UnreadItemsHint()
        {
            var names = Player.Items
                .Where(i => i != null && (IsSecretNote(i) || IsBook(i) && (i.Category == SObject.skillBooksCategory || Player.stats.Get(i.ItemId) == 0)))
                .Select(i => i.DisplayName).Distinct().ToList();
            return names.Count == 0 ? null : $"背包里有没读的：{string.Join("、", names)}（用 use_item 读，书有永久加成或经验，秘密纸条有线索）";
        }

        /// <summary>
        /// 拿起背包里的一样东西“使用”，和玩家手持右键一样（Object.performUseAction，成功就消耗一个）：
        /// 读书、读秘密纸条、用图腾（传送、求雨、宝藏）、吹马笛。秘密纸条的内容直接读出来，信件界面自动关掉。
        /// </summary>
        public IEnumerable<object> UseItem(string itemSpec, bool force = false)
        {
            int idx = Util.FindItemIndex(itemSpec, IsUsable);
            if (idx < 0)
            {
                var usable = Player.Items.Where(IsUsable).Select(i => i.DisplayName).Distinct().ToList();
                yield return ActionResult.Fail($"背包里没有能直接使用的“{itemSpec}”。"
                    + (usable.Count > 0 ? "能用的：" + string.Join("、", usable) : "背包里没有能直接使用的东西（书、秘密纸条、图腾、马笛）")
                    + "。吃东西用 eat，晶球、神秘盒找克林特用 crack_geodes");
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            foreach (var s in WaitPlayerFree())
                yield return s;

            var obj = (SObject)Player.Items[idx];
            string name = obj.DisplayName;
            if (CheckUseBookBlock(obj.Category, obj.ItemId, name, Player.stats.Get(obj.ItemId) != 0, force) is string useBookBlock)
            {
                yield return ActionResult.Fail(useBookBlock);
                yield break;
            }
            // 传送图腾：农场 —— 走回去来得及就别浪费：走路要一个半小时以上、或者走到家已经过了 23:00 才值得用
            if (obj.QualifiedItemId == "(O)688" && !force && Loc is not Farm && EstimateMinutesToBed(ResolveAiHome()) is int walk && walk < 90)
            {
                int arrive = FromMinutes(ToMinutes(Game1.timeOfDay) + walk);
                if (arrive <= 2300)
                {
                    yield return ActionResult.Fail($"现在 {Game1.timeOfDay / 100}:{Game1.timeOfDay % 100:00}，从这里走回家大约 {walk} 分钟，{arrive / 100}:{arrive % 100:00} 前就能到，"
                        + $"没必要用{name}（图腾不便宜，留到深夜赶不回去、或者在很远的地方时再用）。走回去用 travel_to Farm 或 sleep；确实要现在用就加 force=true");
                    yield break;
                }
            }
            // 游戏按宽度折行时会在中文中间塞空格（“双倍木 头”），去掉
            string desc = System.Text.RegularExpressions.Regex.Replace(obj.getDescription()?.Replace("\n", " ").Trim() ?? "",
                @"(?<=\p{IsCJKUnifiedIdeographs})\s+(?=\p{IsCJKUnifiedIdeographs})", "");
            var bookKind = ClassifyBook(obj.Category, obj.ItemId);
            bool book = bookKind != BookKind.None, note = IsSecretNote(obj);
            bool xpBook = bookKind is BookKind.SkillBook or BookKind.PurpleBook;
            bool firstRead = bookKind == BookKind.PowerBook && Player.stats.Get(obj.ItemId) == 0;
            int notesBefore = Player.secretNotesSeen.Count;
            var locBefore = Loc;

            Player.CurrentToolIndex = idx;
            if (!obj.performUseAction(Loc))
            {
                yield return ActionResult.Fail(note
                    ? $"{name} 用不了：能读的秘密纸条都读过了"
                    : $"{name} 现在用不了（可能是在节日、剧情、水里，或者这里不能用）");
                yield break;
            }
            Player.reduceActiveItemByOne();

            // 等动作（读书、举图腾）和随后的传送、淡入淡出放完
            for (int i = 0; i < 600 && (Player.FarmerSprite.PauseForSingleAnimation || Player.UsingTool || Game1.isWarping || Game1.fadeToBlack || !Player.CanMove && Game1.activeClickableMenu == null); i++)
                yield return null;
            foreach (var s in WaitTicks(20))
                yield return s;

            if (note)
            {
                string text = Game1.activeClickableMenu is LetterViewerMenu ? observer.DescribeMenu().Replace("（读完用 close_menu 关闭，附带物品和任务会自动收下）", "").Replace("信件：", "").Replace("^", " ").Trim() : "";
                if (Game1.activeClickableMenu is LetterViewerMenu letter)
                {
                    letter.exitThisMenu(false);
                    foreach (var s in WaitTicks(10))
                        yield return s;
                }
                string got = Player.secretNotesSeen.Count > notesBefore ? "" : "（好像没读到新的）";
                yield return ActionResult.Success($"读了{name}{got}：\n{text}");
                yield break;
            }
            if (book)
            {
                yield return ActionResult.Success(xpBook
                    ? $"读了《{name}》，加了技能经验：{desc}"
                    : firstRead
                        ? $"读了《{name}》，获得永久效果：{desc}"
                        : $"读了《{name}》。这本以前读过，永久效果已经有了（未叠加）");
                yield break;
            }
            string moved = Loc != locBefore ? $"，现在在 {Loc.DisplayName}（{Loc.Name}）" : "";
            yield return ActionResult.Success($"用了 {name}{moved}。{desc}");
        }
    }
}
