using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Tools;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 发表情（头顶气泡 / 动作） ─────────────────────────
        // 走 Farmer.netDoEmote：本机马上播，联机时其他玩家也看得到（和游戏里按 E 的表情菜单是同一条路）。

        private static readonly (string id, string cn)[] EmoteNames =
        {
            ("happy", "开心"), ("sad", "难过"), ("heart", "爱心"), ("exclamation", "感叹号"), ("note", "音符"),
            ("sleep", "睡觉"), ("game", "游戏"), ("question", "疑问"), ("x", "叉"), ("pause", "省略号"),
            ("angry", "生气"), ("yes", "点头"), ("no", "摇头"), ("sick", "不舒服"), ("laugh", "大笑"),
            ("surprised", "惊讶"), ("hi", "打招呼"), ("taunt", "挑衅"), ("uh", "呃"), ("music", "唱歌"),
        };

        /// <summary>路过打招呼 / 卖萌时选用的可爱表情池。</summary>
        internal static readonly string[] CuteEmoteIds = { "hi", "happy", "heart", "laugh", "note", "yes" };

        internal static volatile bool PassbyEmoteEnabled = true;
        internal static float PassbyEmoteChance = 0.35f;
        internal static volatile int PassbyEmoteCooldownMinutes = 5;

        private const int PassbyEmoteRadius = 4;
        private const int PassbyMissDebounceSeconds = 15;
        private static readonly Random SocialEmoteRng = new();
        private static readonly Dictionary<long, DateTime> LastPassbyGreetedUtc = new();
        private static readonly Dictionary<long, DateTime> LastPassbyMissUtc = new();

        internal static void ResetSocialState()
        {
            LastPassbyGreetedUtc.Clear();
            LastPassbyMissUtc.Clear();
            ResetProactiveGiftState();
        }

        internal static bool ShouldTriggerPassbyEmote(
            bool enabled, float chance, int cooldownMinutes,
            DateTime nowUtc, DateTime? lastGreetedUtc, DateTime? lastMissUtc, double roll)
        {
            if (!enabled || chance <= 0f)
                return false;
            int cdMin = Math.Max(1, cooldownMinutes);
            if (lastGreetedUtc.HasValue && (nowUtc - lastGreetedUtc.Value).TotalMinutes < cdMin)
                return false;
            if (lastMissUtc.HasValue && (nowUtc - lastMissUtc.Value).TotalSeconds < PassbyMissDebounceSeconds)
                return false;
            return roll < Math.Clamp(chance, 0f, 1f);
        }

        internal static bool IsSocialTransitionStateSafe(bool allowWarping, bool locValid, bool warping, bool freezeControls)
        {
            if (!allowWarping)
                return locValid && !warping && !freezeControls;
            return warping || (locValid && !freezeControls);
        }

        internal static bool IsSafeForSocialBehavior(GameLocation loc, bool allowWarping = false)
        {
            if (!Context.IsWorldReady || !Context.IsMultiplayer)
                return false;
            bool warping = Game1.isWarping || Game1.fadeToBlack || Game1.globalFade || Game1.locationRequest != null;
            bool locValid = loc != null && Game1.currentLocation == loc;
            if (!IsSocialTransitionStateSafe(allowWarping, locValid, warping, Game1.freezeControls))
                return false;
            if (Game1.isFestival() || Game1.eventUp)
                return false;
            if (Game1.activeClickableMenu != null)
                return false;
            if (Game1.timeOfDay >= 2430)
                return false;
            if (Player == null || Player.UsingTool || Player.isInBed.Value || Player.passedOut)
                return false;
            if (Player.CurrentTool is FishingRod rod && (rod.isFishing || rod.isCasting || rod.isTimingCast || rod.isReeling))
                return false;
            if (loc != null)
            {
                if (ModSupport.IsCombatZone(loc))
                    return false;
                if (loc.characters.OfType<Monster>().Any())
                    return false;
            }
            return true;
        }

        internal static bool TryPlayCuteEmote(Random rng = null)
        {
            var who = Player;
            if (who == null || !who.CanEmote())
                return false;
            var r = rng ?? SocialEmoteRng;
            string id = CuteEmoteIds[r.Next(CuteEmoteIds.Length)];
            who.netDoEmote(id);
            return true;
        }

        /// <summary>
        /// 走路途中经过其他真人玩家附近（同一地图、4 格内）时，温和地停下转身看对方，过约 1.2 秒发一个可爱表情再继续走。
        /// 绝不修改或影响外层动作的 Outcome/结果；遇节日/剧情/菜单/钓鱼/战斗/深夜立即跳过。
        /// </summary>
        private IEnumerable<object> MaybeGreetNearbyPlayer(GameLocation loc)
        {
            if (!PassbyEmoteEnabled || PassbyEmoteChance <= 0f || !IsSafeForSocialBehavior(loc))
                yield break;
            if (!Player.CanEmote())
                yield break;

            var nowUtc = DateTime.UtcNow;
            Farmer candidate = null;
            int bestDist = int.MaxValue;
            foreach (var f in loc.farmers)
            {
                if (f == null || f == Player)
                    continue;
                int dist = Util.Manhattan(Player.TilePoint, f.TilePoint);
                if (dist > PassbyEmoteRadius)
                    continue;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    candidate = f;
                }
            }
            if (candidate == null)
                yield break;

            long uid = candidate.UniqueMultiplayerID;
            DateTime? lastGreet = LastPassbyGreetedUtc.TryGetValue(uid, out var g) ? g : null;
            DateTime? lastMiss = LastPassbyMissUtc.TryGetValue(uid, out var m) ? m : null;
            if (!ShouldTriggerPassbyEmote(PassbyEmoteEnabled, PassbyEmoteChance, PassbyEmoteCooldownMinutes, nowUtc, lastGreet, lastMiss, SocialEmoteRng.NextDouble()))
            {
                // 同一次路过只掷一次骰子，避免每走一格重复抽签导致实际概率虚高
                if (!lastGreet.HasValue || (nowUtc - lastGreet.Value).TotalMinutes >= Math.Max(1, PassbyEmoteCooldownMinutes))
                    LastPassbyMissUtc[uid] = nowUtc;
                yield break;
            }

            LastPassbyGreetedUtc[uid] = nowUtc;
            LastPassbyMissUtc.Remove(uid);

            Player.Halt();
            Player.FarmerSprite.StopAnimation();
            Player.faceGeneralDirection(candidate.getStandingPosition(), 0, opposite: false, useTileCalculations: false);

            // 停下看着对方约 1.2 秒（72 帧），期间若出现剧情/菜单/怪物/换图就立刻退出
            for (int t = 0; t < 72; t++)
            {
                if (!IsSafeForSocialBehavior(loc) || Blocked(out _))
                    yield break;
                if (candidate.currentLocation == loc)
                    Player.faceGeneralDirection(candidate.getStandingPosition(), 0, opposite: false, useTileCalculations: false);
                yield return null;
            }

            if (!IsSafeForSocialBehavior(loc) || Blocked(out _) || !TryPlayCuteEmote(SocialEmoteRng))
                yield break;

            for (int t = 0; t < 75 && (Player.isEmoting || Player.isEmoteAnimating); t++)
            {
                if (!IsSafeForSocialBehavior(loc) || Blocked(out _))
                    yield break;
                yield return null;
            }
        }

        internal static bool TryPlayNamedEmote(string which, out string cnName)
        {
            cnName = null;
            string key = (which ?? "").Trim().ToLowerInvariant();
            if (key == "")
                return false;
            var hit = EmoteNames.FirstOrDefault(e => e.id == key || e.cn == key);
            if (hit.id == null)
                return false;
            var who = Player;
            if (who == null || !who.CanEmote())
                return false;
            who.netDoEmote(hit.id);
            cnName = hit.cn;
            return true;
        }

        public IEnumerable<object> Emote(string which)
        {
            string key = (which ?? "").Trim().ToLowerInvariant();
            if (key == "")
            {
                yield return ActionResult.Success("可用表情：" + string.Join("、", EmoteNames.Select(e => $"{e.id}（{e.cn}）"))
                    + "。传 emote=名字就在头顶冒出气泡；点头/摇头/大笑/打招呼/挑衅/唱歌等带动作，要站着不动才播得出来。");
                yield break;
            }
            var hit = EmoteNames.FirstOrDefault(e => e.id == key || e.cn == key);
            if (hit.id == null)
            {
                yield return ActionResult.Fail($"没有“{which}”这个表情。不传 emote 可以看列表");
                yield break;
            }
            var who = Player;
            if (!who.CanEmote())
            {
                yield return ActionResult.Fail("现在发不了表情（在过场、读档或正在做别的动作），等一下再试");
                yield break;
            }
            who.netDoEmote(hit.id);
            for (int i = 0; i < 90 && (who.isEmoting || who.isEmoteAnimating); i++)
                yield return null;
            yield return ActionResult.Success($"发了“{hit.cn}”表情");
        }
    }
}
