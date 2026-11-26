using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 联机聊天 ─────────────────────────

        internal const string ModeSolo = "solo", ModeCommand = "command", ModeHybrid = "hybrid";

        /// <summary>联机时 AI 怎么安排自己。由 ModEntry 按配置设置，也可以在聊天里说一句切换。</summary>
        public static string Mode = ModeHybrid;

        /// <summary>聊天里切换了模式：ModEntry 记到 config.json。</summary>
        public static Action<string> ModeChanged;

        internal static string NormalizeMode(string mode) => mode is ModeSolo or ModeCommand ? mode : ModeHybrid;

        /// <summary>聊天里切换模式的话（去掉空格和标点、转小写后整句相等才算；拼音也认）。</summary>
        private static readonly Dictionary<string, string> ModePhrases = new()
        {
            ["你自己玩吧"] = ModeSolo, ["你自己玩"] = ModeSolo, ["自己玩吧"] = ModeSolo, ["自己玩"] = ModeSolo,
            ["nizijiwanba"] = ModeSolo, ["nizijiwan"] = ModeSolo, ["zijiwanba"] = ModeSolo, ["zijiwan"] = ModeSolo,
            ["听我的"] = ModeCommand, ["听我指挥"] = ModeCommand, ["主要听指令"] = ModeCommand, ["zhuyaotingzhiling"] = ModeCommand, ["tingwode"] = ModeCommand, ["tingwozhihui"] = ModeCommand,
            ["指令做完接着自己玩"] = ModeHybrid, ["zhilingzuowanjiezhezijiwan"] = ModeHybrid, ["做完自己玩"] = ModeHybrid, ["做完接着玩"] = ModeHybrid, ["zuowanzijiwan"] = ModeHybrid, ["zuowanjiezhewan"] = ModeHybrid,
        };

        private static string ModeName(string mode) => mode switch
        {
            ModeSolo => "自己玩",
            ModeCommand => "主要听指令",
            _ => "指令做完接着自己玩",
        };

        private static string ModeAdvice() => Mode switch
        {
            ModeSolo => AllowChat ? "按你自己的计划玩，不用放下手里的事。别人说话回一句，来不来帮忙看你自己的心情"
                : "按你自己的计划玩，不用放下手里的事。聊天已关闭，你不能在游戏里说话，别人的话你照样看得到",
            ModeCommand => AllowChat ? "主要听指令：有人吩咐就做，做完用 say 汇报，然后用 follow 或 wait_chat 等下一句，不要自己找事做"
                : "主要听指令：有人吩咐就做，然后用 follow 或 wait_chat 等下一句，不要自己找事做。聊天已关闭，不用 say 回话",
            _ => AllowChat ? "有人吩咐就先做，做完用 say 汇报，然后接着按自己的计划玩"
                : "有人吩咐就先做，然后接着按自己的计划玩。聊天已关闭，你不能在游戏里说话，不用 say 汇报",
        };

        /// <summary>有人说话、而且现在的模式要放下手里的事去听。</summary>
        internal static bool ChatWantsAttention() =>
            incomingChat.Count > 0 && Mode != ModeSolo && Context.IsMultiplayer;

        /// <summary>别人说的、还没告诉 AI 的话。只在主线程读写（游戏收消息也在主线程）。</summary>
        private static readonly List<string> incomingChat = new();

        /// <summary>
        /// 游戏 ChatBox.receiveChatMessage 的后置补丁：记下别的玩家说的话（公开和私聊），
        /// 自己说的、系统提示都不算。AI 可能好几步才看一次，太旧的丢掉。
        /// </summary>
        internal static void OnChatMessage(long sourceFarmer, int chatKind, string message)
        {
            if (sourceFarmer == Game1.player?.UniqueMultiplayerID || chatKind is not (ChatBox.chatMessage or ChatBox.privateMessage))
                return;
            var from = Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == sourceFarmer);
            string name = from?.Name ?? "其他玩家";
            string phrase = new string((message ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (ModePhrases.TryGetValue(phrase, out string mode))
            {
                Mode = mode;
                ModeChanged?.Invoke(mode);
                AnnounceMode(name + " ");
            }
            incomingChat.Add($"💬 {name}{(from?.IsMainPlayer == true ? "（主机）" : "")}对你说：{message}");
            if (incomingChat.Count > 20)
                incomingChat.RemoveAt(0);
        }

        /// <summary>模式变了：在聊天里说一声，也告诉 AI。who 是切换的人（比如“某某 ”）。主线程调用。</summary>
        internal static void AnnounceMode(string who)
        {
            incomingChat.Add($"（{who}把联机模式切换成了“{ModeName(Mode)}”）");
            AiSay($"好的，现在是“{ModeName(Mode)}”模式");
        }

        /// <summary>取出还没告诉 AI 的聊天（没有返回 null）。</summary>
        public static string TakeChat()
        {
            if (incomingChat.Count == 0)
                return null;
            string text = string.Join("\n", incomingChat);
            incomingChat.Clear();
            chatAwaitingReply |= text.Contains("💬");
            return text;
        }

        /// <summary>AI 收到别人的话以后还没用 say 回。AI 写在回复里的字只有 DSH 里看得到，游戏里的人看不到。</summary>
        private static bool chatAwaitingReply;

        /// <summary>收到话之后下一个动作还没 say：提醒一次（没有要提醒的返回 null）。</summary>
        public static string TakeReplyReminder()
        {
            if (!chatAwaitingReply || !AllowChat)
            {
                chatAwaitingReply = false;
                return null;
            }
            chatAwaitingReply = false;
            return "⚠ 刚才别人对你说的话，你还没用 say 回。写在回复里的字对方看不到，只有 say 说的话游戏里才看得到，现在补一句";
        }

        /// <summary>联机时要跟的人：有主机跟主机（AI 自己是主机时跟第一个别的玩家）。</summary>
        private static Farmer OtherPlayer() =>
            Game1.getOnlineFarmers().Where(f => !f.IsLocalPlayer).OrderBy(f => f.IsMainPlayer ? 0 : 1).FirstOrDefault();

        /// <summary>状态里显示的“别的玩家在哪”（单人时返回 null）。</summary>
        public static string OtherPlayersLine()
        {
            if (!Context.IsMultiplayer)
                return null;
            var others = Game1.getOnlineFarmers().Where(f => !f.IsLocalPlayer)
                .Select(f => $"{f.Name}{(f.IsMainPlayer ? "（主机）" : "")}在 {f.currentLocation?.NameOrUniqueName ?? "未知地点"} ({f.TilePoint.X},{f.TilePoint.Y})").ToList();
            bool ccLocked = !Game1.IsMasterGame && Game1.stats.DaysPlayed >= 5 && !Game1.MasterPlayer.mailReceived.Contains("ccDoorUnlock")
                && !Game1.MasterPlayer.mailReceived.Contains("JojaMember");
            return (others.Count > 0 ? "联机中，其他玩家：" + string.Join("、", others) : "联机中，现在只有你在线")
                + $"\n【联机模式：{ModeName(Mode)}】{ModeAdvice()}。别人要换模式就用 set_mode"
                + (ccLocked ? "\n" + Knowledge.FarmhandCommunityCenterNote : "");
        }

        /// <summary>别人用自己的话要换模式时（“换成自己玩”），整句匹配不上，由 AI 听懂了再切。</summary>
        public IEnumerable<object> SetMode(string mode)
        {
            if (!Context.IsMultiplayer)
            {
                yield return ActionResult.Fail("只有联机时才有联机模式");
                yield break;
            }
            string target = (mode ?? "").Trim().ToLowerInvariant();
            target = new[] { ModeSolo, ModeCommand, ModeHybrid }.FirstOrDefault(m => m == target || ModeName(m) == target);
            if (target == null)
            {
                yield return ActionResult.Fail($"没有“{mode}”这个模式，只有 {ModeSolo}（{ModeName(ModeSolo)}）、{ModeCommand}（{ModeName(ModeCommand)}）、{ModeHybrid}（{ModeName(ModeHybrid)}）");
                yield break;
            }
            Mode = target;
            ModeChanged?.Invoke(target);
            AiSay($"好的，现在是“{ModeName(target)}”模式");
            yield return ActionResult.Success($"已经切换成“{ModeName(target)}”。{ModeAdvice()}");
        }

        /// <summary>
        /// 允许 AI 说话吗（config.json 的 AllowChat，DSH 设置页 allowChat 会改它）。默认允许（v1.1.27 起）。
        /// 模组里所有“AI 自己往聊天里发话”的地方都走 AiSay，只有玩家自己输入的话（F6 输入框）不走这里。
        /// </summary>
        internal static bool AllowChat;

        static readonly string[] BrokenToolMarks = { "</text>", "DSML", "<｜", "</invoke", "</parameter", "</calls", "<invoke", "<parameter" };

        /// <summary>
        /// 聊天文字清理：开头的 / 会被聊天框当成游戏命令，去掉；模型吐坏的工具调用标记（</text>、DSML、invoke 等）
        /// 连同后面的内容一起截掉，不然会原样发进游戏聊天。清完是空的返回空串。
        /// </summary>
        internal static string CleanChatText(string text)
        {
            text = (text ?? "").Trim().TrimStart('/');
            int cut = -1;
            foreach (var m in BrokenToolMarks)
            {
                int i = text.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (cut < 0 || i < cut))
                    cut = i;
            }
            if (cut >= 0)
                text = text.Substring(0, cut);
            return text.Trim();
        }

        /// <summary>AI 往聊天框发一句话；聊天关着就什么都不发，返回 false。</summary>
        internal static bool AiSay(string text)
        {
            if (!AllowChat || Game1.chatBox == null)
                return false;
            text = CleanChatText(text);
            if (text.Length == 0)
                return false;
            Game1.chatBox.textBoxEnter(text);
            return true;
        }

        public IEnumerable<object> Say(string text)
        {
            if (!AllowChat)
            {
                yield return ActionResult.Fail("聊天已关闭：玩家不让你在游戏聊天里说话（别人说的话你照样听得到）。要说的事写在回复里就行，不用 say");
                yield break;
            }
            if (!Context.IsMultiplayer || Game1.chatBox == null)
            {
                yield return ActionResult.Fail("只有联机时才能说话");
                yield break;
            }
            text = CleanChatText(text);
            if (text.Length == 0)
            {
                yield return ActionResult.Fail("没有要说的内容");
                yield break;
            }
            if (text.Length > 300)
                text = text.Substring(0, 300);
            Game1.chatBox.textBoxEnter(text);
            chatAwaitingReply = false;
            yield return ActionResult.Success("说了：" + text);
        }

        public IEnumerable<object> WaitChat(int seconds)
        {
            if (!Context.IsMultiplayer)
            {
                yield return ActionResult.Fail("只有联机时才有人和你聊天");
                yield break;
            }
            seconds = Math.Clamp(seconds, 5, 300);
            for (int i = 0; i < seconds * 60 && incomingChat.Count == 0; i++)
            {
                if ((Game1.eventUp && Game1.CurrentEvent != null) || Game1.activeClickableMenu != null)
                {
                    yield return ActionResult.Success("等待被打断：出现了剧情或界面。当前界面：" + observer.DescribeMenu());
                    yield break;
                }
                yield return null;
            }
            // 消息由工具结果统一附在后面
            yield return ActionResult.Success(incomingChat.Count > 0 ? "有人说话了" : $"等了 {seconds} 秒，没有人说话");
        }

        /// <summary>拿结婚戒指向别的玩家求婚。对方自己的窗口里会弹问题，由对方决定，这里等到有结果为止。</summary>
        public IEnumerable<object> Propose()
        {
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var target = Context.IsMultiplayer ? OtherPlayer() : null;
            if (target == null)
            {
                yield return ActionResult.Fail("现在没有别的玩家在线，没有人可以求婚");
                yield break;
            }
            if (Player.isMarriedOrRoommates() || Player.isEngaged())
            {
                yield return ActionResult.Fail("你已经订婚或结婚了");
                yield break;
            }
            if (target.isMarriedOrRoommates() || target.isEngaged())
            {
                yield return ActionResult.Fail($"{target.Name} 已经订婚或结婚了");
                yield break;
            }
            if (Player.Items.FirstOrDefault(i => i?.QualifiedItemId == "(O)801") == null)
            {
                yield return ActionResult.Fail("背包里没有结婚戒指。配方和材料看 get_guide 的“结婚”专题");
                yield break;
            }

            // 对方可能在走动，走到旁边为止
            var o = new Outcome();
            for (int attempt = 0; attempt < 20; attempt++)
            {
                target = OtherPlayer();
                if (target == null)
                {
                    yield return ActionResult.Fail("对方下线了");
                    yield break;
                }
                var where = target.currentLocation;
                if (where?.NameOrUniqueName != Loc.NameOrUniqueName)
                {
                    if (where == null || Game1.isFestival() || where.IsTemporary)
                    {
                        yield return ActionResult.Fail($"{target.Name} 现在在节日或者临时的地方，等对方回来再求婚");
                        yield break;
                    }
                    foreach (var s in TravelInner(where.NameOrUniqueName, o, exactName: true))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"{target.Name} 在 {where.NameOrUniqueName}，走不过去：{o.Message}");
                        yield break;
                    }
                    yield return null;
                    continue;
                }
                if (Util.Manhattan(Player.TilePoint, target.TilePoint) <= 2)
                    break;
                var goals = Pathfinder.AdjacentStandTiles(Loc, target.TilePoint);
                var path = goals.Count > 0 ? Pathfinder.FindPath(Loc, Player.TilePoint, goals, 8000) : null;
                if (path == null || path.Count < 2)
                {
                    foreach (var s in WaitTicks(15))
                        yield return s;
                    continue;
                }
                foreach (var s in WalkPath(path.Take(8).ToList(), o))
                    yield return s;
                o.Succeed();
            }
            if (Blocked(out why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            if (target == null || target.currentLocation?.NameOrUniqueName != Loc.NameOrUniqueName || Util.Manhattan(Player.TilePoint, target.TilePoint) > 2)
            {
                yield return ActionResult.Fail($"没能走到 {target?.Name ?? "对方"} 旁边，请对方站着别动再试");
                yield break;
            }

            // 路上背包可能变了，这里重新找；游戏接受后会从手上扣掉戒指，所以要拿在手里
            int ringIdx = Player.Items.ToList().FindIndex(i => i?.QualifiedItemId == "(O)801");
            if (ringIdx < 0)
            {
                yield return ActionResult.Fail("背包里没有结婚戒指了");
                yield break;
            }
            Player.CurrentToolIndex = ringIdx;
            Player.Halt();
            Player.faceGeneralDirection(target.getStandingPosition(), 0, opposite: false, useTileCalculations: false);
            // 和游戏里按动作键后选“是”一样，只是省掉自己确认的那一问
            Player.team.SendProposal(target, ProposalType.Marriage, Player.CurrentItem.getOne());
            var pending = new PendingProposalDialog();
            Game1.activeClickableMenu = pending;

            bool timedOut = false;
            for (int i = 0; Game1.activeClickableMenu == pending; i++)
            {
                if (i == 120 * 60)
                {
                    timedOut = true;
                    pending.cancelProposal(Player);
                }
                if (i >= 150 * 60)
                {
                    yield return ActionResult.Fail($"{target.Name} 一直没回答，收回求婚也没有结果。界面还开着，等对方点一下才会关");
                    yield break;
                }
                yield return null;
            }
            foreach (var s in WaitTicks(15))
                yield return s;

            string menu = Game1.activeClickableMenu != null ? "\n当前界面：" + observer.DescribeMenu() : "";
            var friendship = Player.team.GetFriendship(Player.UniqueMultiplayerID, target.UniqueMultiplayerID);
            if (friendship?.IsEngaged() == true)
                yield return ActionResult.Success($"{target.Name} 答应了！{friendship.WeddingDate.TotalDays - Game1.Date.TotalDays} 天后早上举行婚礼。" + menu);
            else if (timedOut)
                yield return ActionResult.Fail($"{target.Name} 一直没回答，已经收回求婚，戒指还在你手上。" + menu);
            else
                yield return ActionResult.Fail($"{target.Name} 没有答应（拒绝了，或者正在忙，比如开着界面或看剧情），戒指还在你手上。" + menu);
        }

        public IEnumerable<object> Follow(int seconds)
        {
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            if (!Context.IsMultiplayer || OtherPlayer() == null)
            {
                yield return ActionResult.Fail("现在没有别的玩家在线，没有人可以跟");
                yield break;
            }
            seconds = Math.Clamp(seconds, 10, 600);
            var o = new Outcome();
            int start = Game1.ticks;
            while (Game1.ticks - start < seconds * 60)
            {
                if (incomingChat.Count > 0)
                {
                    yield return ActionResult.Success("有人说话了，先停下来");
                    yield break;
                }
                if (Blocked(out why))
                {
                    yield return ActionResult.Success("先不跟了：" + why);
                    yield break;
                }
                var target = OtherPlayer();
                if (target == null)
                {
                    yield return ActionResult.Fail("对方下线了");
                    yield break;
                }
                var where = target.currentLocation;
                string name = where?.NameOrUniqueName;
                if (name != Loc.NameOrUniqueName)
                {
                    if (name == null || Game1.isFestival() || where.IsTemporary)
                    {
                        yield return ActionResult.Fail($"{target.Name} 现在在节日或者临时的地方，跟不过去。要去节日用 festival enter");
                        yield break;
                    }
                    foreach (var s in TravelInner(name, o, exactName: true))
                        yield return s;
                    if (!o.Ok)
                    {
                        yield return ActionResult.Fail($"{target.Name} 在 {name}，走不过去：{o.Message}");
                        yield break;
                    }
                    // 已经在目的地时 TravelInner 不会等一帧，不能直接回到循环开头空转
                    yield return null;
                    continue;
                }

                var me = Player.TilePoint;
                var there = target.TilePoint;
                if (Util.Manhattan(me, there) > 2)
                {
                    var goals = Pathfinder.AdjacentStandTiles(Loc, there);
                    var path = goals.Count > 0 ? Pathfinder.FindPath(Loc, me, goals, 8000) : null;
                    if (path != null && path.Count >= 2)
                    {
                        // 只走几步就重新看，对方也在走
                        foreach (var s in WalkPath(path.Take(5).ToList(), o))
                            yield return s;
                        o.Succeed();
                        continue;
                    }
                }
                foreach (var s in WaitTicks(15))
                    yield return s;
            }
            var last = OtherPlayer();
            yield return ActionResult.Success($"跟了 {seconds} 秒" + (last != null ? $"，{last.Name} 在 {last.currentLocation?.NameOrUniqueName} ({last.TilePoint.X},{last.TilePoint.Y})" : ""));
        }

        internal static bool ProactiveGiftEnabled = true;
        internal static float ProactiveGiftChance = 0.5f;
        internal static int ProactiveGiftCooldownDays = 1;

        private static readonly Random ProactiveGiftRng = new();
        private static int lastProactiveGiftDay = int.MinValue;
        private static int lastProactiveGiftRollDay = int.MinValue;
        private static bool proactiveGiftActiveToday;

        internal static void ResetProactiveGiftState()
        {
            lastProactiveGiftDay = int.MinValue;
            lastProactiveGiftRollDay = int.MinValue;
            proactiveGiftActiveToday = false;
        }

        internal static void RecordProactiveGiftSent(int todayTotalDays)
        {
            lastProactiveGiftDay = todayTotalDays;
            proactiveGiftActiveToday = false;
        }

        /// <summary>判断是否应在联机状态下提示 AI 主动给玩家送一件小礼物。单机、关闭开关、无在线玩家、冷却期内都不触发。</summary>
        internal static bool ShouldPromptProactiveGift(
            bool enabled, bool isMultiplayer, bool hasOnlineOtherPlayer, bool safe,
            float chance, int cooldownDays, int todayTotalDays, int lastGiftDay,
            int lastRolledDay, bool alreadyActiveToday, double randomRoll)
        {
            if (!enabled || !isMultiplayer || !hasOnlineOtherPlayer || !safe)
                return false;
            int cd = Math.Clamp(cooldownDays, 1, 28);
            if (lastGiftDay != int.MinValue && todayTotalDays - lastGiftDay < cd)
                return false;
            if (lastRolledDay == todayTotalDays)
                return alreadyActiveToday;
            float p = Math.Clamp(chance, 0f, 1f);
            return p > 0f && randomRoll < p;
        }

        /// <summary>给背包物品做主动送礼优先级打分：料理 > 花 > 鱼 > 采集/水果/工匠品 > 其他可丢物品；不可丢或保留物返回 0。</summary>
        internal static int ScoreProactiveGiftItem(bool canDrop, bool isReserved, int category)
        {
            if (!canDrop || isReserved)
                return 0;
            return category switch
            {
                StardewValley.Object.CookingCategory => 5,
                StardewValley.Object.flowersCategory => 4,
                StardewValley.Object.FishCategory => 3,
                StardewValley.Object.GreensCategory or StardewValley.Object.FruitsCategory or StardewValley.Object.artisanGoodsCategory => 2,
                _ => 1,
            };
        }

        /// <summary>联机且有在线真人玩家时，按概率和冷却在 get_state 中给出主动送礼提示。</summary>
        public static string ProactiveGiftHint()
        {
            if (!ProactiveGiftEnabled || !Context.IsMultiplayer || !IsSafeForSocialBehavior(Loc))
                return null;
            var onlineOthers = Game1.getOnlineFarmers()
                .Where(f => f != null && f != Player && f.currentLocation != null && !f.currentLocation.IsTemporary)
                .ToList();
            if (onlineOthers.Count == 0)
                return null;

            int today = Game1.Date.TotalDays;
            bool should = ShouldPromptProactiveGift(
                ProactiveGiftEnabled, true, true, true,
                ProactiveGiftChance, ProactiveGiftCooldownDays,
                today, lastProactiveGiftDay, lastProactiveGiftRollDay, proactiveGiftActiveToday,
                lastProactiveGiftRollDay == today ? 0.0 : ProactiveGiftRng.NextDouble());
            if (lastProactiveGiftRollDay != today)
            {
                lastProactiveGiftRollDay = today;
                proactiveGiftActiveToday = should;
            }
            if (!should)
                return null;

            // 优先选同一地图或最近的在线玩家
            var target = onlineOthers
                .OrderBy(f => f.currentLocation?.NameOrUniqueName == Loc?.NameOrUniqueName ? 0 : 1)
                .ThenBy(f => Util.Manhattan(Player.TilePoint, f.TilePoint))
                .First();
            string targetName = string.IsNullOrWhiteSpace(target.Name) ? "小伙伴" : target.Name;

            var reservedCheck = ReservedChecker();
            var picks = Player.Items
                .Where(it => it != null)
                .Select(it => (item: it, score: ScoreProactiveGiftItem(it.canBeDropped(), reservedCheck(it) != null, it.Category)))
                .Where(x => x.score >= 2)
                .OrderByDescending(x => x.score)
                .Select(x => x.item.DisplayName)
                .Distinct()
                .Take(4)
                .ToList();
            string suggest = picks.Count > 0 ? $"（背包里像 {string.Join("、", picks)} 就不错）" : "";
            string chatStep = AllowChat
                ? $"先用 say 告诉 {targetName} 站着别动，再用 drop_item item=… count=1 to={targetName} message=\"一句介绍\" emote=heart 走过去送给对方并发表情"
                : $"用 drop_item item=… count=1 to={targetName} emote=heart 走过去送给对方并发表情";
            return $"【主动送礼灵感】有空可以挑一件有心意的小礼物送给 {targetName}：优先自己做的菜、捡的小花、钓的鱼{suggest}，别送【留着】的、交任务用的东西或工具。{chatStep}。";
        }

        private static readonly Random IdleFollowRng = new();

        internal static bool ShouldStartIdleFollow(
            bool enabled, bool isMultiplayer, bool playerInControl, bool aiActive,
            bool mainBusy, bool idleRunning, bool agentThinking, bool hasChat, bool safe,
            int intervalSeconds, DateTime nowUtc, DateTime lastActivityEndedUtc)
        {
            if (!enabled || !isMultiplayer || playerInControl || !aiActive)
                return false;
            if (mainBusy || idleRunning || agentThinking || hasChat || !safe)
                return false;
            int interval = Math.Clamp(intervalSeconds, 30, 3600);
            return (nowUtc - lastActivityEndedUtc).TotalSeconds >= interval;
        }

        /// <summary>
        /// 联机空闲（没有指派任务）时随机挑一位安全地图上的在线真人玩家跟一会儿，间或发表情卖萌。
        /// 复用 follow 跟随逻辑；绝不砍东西、不拿箱子、不进矿洞/火山；有新任务、有人说话或进入节日/剧情/菜单/战斗立刻停下。
        /// </summary>
        public IEnumerable<object> IdleFollow(int durationSeconds)
        {
            if (!IsSafeForSocialBehavior(Loc) || Blocked(out _))
                yield break;

            var candidates = Game1.getOnlineFarmers()
                .Where(f => f != null && f != Player
                    && f.currentLocation != null
                    && !f.currentLocation.IsTemporary
                    && !ModSupport.IsCombatZone(f.currentLocation))
                .ToList();
            if (candidates.Count == 0)
                yield break;

            var chosen = candidates[IdleFollowRng.Next(candidates.Count)];
            long targetId = chosen.UniqueMultiplayerID;
            int seconds = Math.Clamp(durationSeconds, 10, 180);
            int startTick = Game1.ticks;
            int nextEmoteTick = startTick + IdleFollowRng.Next(3 * 60, 7 * 60);
            var o = new Outcome();

            while (Game1.ticks - startTick < seconds * 60)
            {
                if (incomingChat.Count > 0 || !IsSafeForSocialBehavior(Loc) || Blocked(out _))
                    yield break;

                var target = Game1.getOnlineFarmers().FirstOrDefault(f => f != null && f.UniqueMultiplayerID == targetId);
                if (target == null)
                    yield break;

                var where = target.currentLocation;
                string name = where?.NameOrUniqueName;
                if (name == null || where.IsTemporary || ModSupport.IsCombatZone(where) || Game1.isFestival())
                    yield break;

                if (name != Loc.NameOrUniqueName)
                {
                    foreach (var s in TravelInner(name, o, exactName: true))
                    {
                        if (incomingChat.Count > 0 || !IsSafeForSocialBehavior(Loc, allowWarping: true))
                            yield break;
                        yield return s;
                    }
                    if (!o.Ok)
                        yield break;
                    o.Succeed();
                    yield return null;
                    continue;
                }

                var me = Player.TilePoint;
                var there = target.TilePoint;
                int dist = Util.Manhattan(me, there);
                if (dist > 2)
                {
                    var goals = Pathfinder.AdjacentStandTiles(Loc, there);
                    var path = goals.Count > 0 ? Pathfinder.FindPath(Loc, me, goals, 6000) : null;
                    if (path != null && path.Count >= 2)
                    {
                        foreach (var s in WalkPath(path.Take(5).ToList(), o))
                        {
                            if (incomingChat.Count > 0 || !IsSafeForSocialBehavior(Loc))
                                yield break;
                            yield return s;
                        }
                        o.Succeed();
                        continue;
                    }
                }
                else if (Game1.ticks >= nextEmoteTick && Player.CanEmote())
                {
                    Player.Halt();
                    Player.faceGeneralDirection(target.getStandingPosition(), 0, opposite: false, useTileCalculations: false);
                    TryPlayCuteEmote(IdleFollowRng);
                    nextEmoteTick = Game1.ticks + IdleFollowRng.Next(8 * 60, 14 * 60);
                }

                foreach (var s in WaitTicks(15))
                {
                    if (incomingChat.Count > 0 || !IsSafeForSocialBehavior(Loc))
                        yield break;
                    yield return s;
                }
            }
            Player.Halt();
        }
    }
}
