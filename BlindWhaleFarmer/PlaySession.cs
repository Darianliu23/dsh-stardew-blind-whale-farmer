using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// “这一次玩多久”：按游戏日（SessionDays）和现实分钟（SessionMinutes）限制一次游玩。
    /// 从读档后的第一个动作开始计；到了上限就拒绝游戏动作，让 AI 收尾并向玩家汇报。
    /// 内置 AI（F8）和 MCP 客户端共用同一份计时。
    /// </summary>
    internal sealed class PlaySession
    {
        /// <summary>时间到了以后还允许的工具：处理界面、记笔记；分钟用完时另外允许 sleep 回家存档。</summary>
        private static readonly HashSet<string> AlwaysAllowed = new()
        {
            "dialogue", "close_menu", "menu_click", "skip_event", "choose_profession", "leave_festival", "update_notes", "reserve_items", "play_for"
        };

        private readonly ModConfig config;
        private readonly object sync = new();
        private DateTime? startedUtc;
        private int daysCompleted;
        /// <summary>分钟用完时已经过了几天；之后再睡一觉（天数变多）就彻底结束。-1 表示分钟还没用完。</summary>
        private int minutesUpAtDay = -1;

        public PlaySession(ModConfig config)
        {
            this.config = config;
        }

        // ── 玩家在聊天里说的“玩半小时”“玩 4 个游戏日”：要玩够的目标，和设置里的上限（最多玩多久）分开 ──
        // 读档、改设置时不清掉：AI 常常先 launch_game 读档再记目标，也可能反过来。

        private int goalMinutes;
        private DateTime goalStartedUtc;
        private int goalDays;
        private int goalDaysDone;
        /// <summary>上次数到的游戏日（Date.TotalDays）；-1 = 等下一次 DayStarted 再定（目标在标题画面记下，或刚读档）。</summary>
        private int goalLastDate = -1;
        private bool goalDoneNoted;

        /// <summary>目标进度。Minutes/Days 为 0 表示没按这一项要求。</summary>
        public readonly record struct GoalInfo(int Minutes, double Played, int Days, int DaysDone)
        {
            public bool MinutesDone => Minutes <= 0 || Played >= Minutes;
            public bool DaysReached => Days <= 0 || DaysDone >= Days;
            public bool Done => MinutesDone && DaysReached;
        }

        /// <summary>记下玩家要求的游玩时长：现实分钟、游戏日（可以只给一项），从现在开始算；两项都是 0 = 取消。</summary>
        public string SetGoal(int minutes, int days)
        {
            lock (sync)
            {
                if (minutes <= 0 && days <= 0)
                {
                    bool had = goalMinutes > 0 || goalDays > 0;
                    goalMinutes = goalDays = 0;
                    return had ? "成功：取消了游玩时长目标。这一轮做完就可以停下向玩家汇报" : "成功：本来就没有设游玩时长目标";
                }
                goalMinutes = Math.Clamp(minutes, 0, 24 * 60);
                goalDays = Math.Clamp(days, 0, 112);
                goalStartedUtc = DateTime.UtcNow;
                goalDaysDone = 0;
                goalLastDate = Context.IsWorldReady ? Game1.Date.TotalDays : -1;
                goalDoneNoted = false;
                var what = new List<string>();
                if (goalDays > 0)
                    what.Add($"{goalDays} 个游戏日（今天算第 1 天，睡醒 {goalDays} 次算玩够）");
                if (goalMinutes > 0)
                    what.Add($"{goalMinutes} 分钟（现实时间，从现在算）");
                var caps = new List<string>();
                if (config.SessionMinutes > 0 && config.SessionMinutes < goalMinutes)
                    caps.Add($"每次最多玩 {config.SessionMinutes} 分钟");
                if (config.SessionDays > 0 && config.SessionDays < goalDays)
                    caps.Add($"每次最多玩 {config.SessionDays} 个游戏日");
                string cap = caps.Count > 0 ? $"（注意：设置里{string.Join("、", caps)}，会先到那个上限）" : "";
                string sleep = goalDays > 0 ? "一天的活做完就 sleep，醒来直接接着玩新的一天；" : "";
                return $"成功：记下了，这次玩 {string.Join("、", what)}{cap}。"
                    + "没玩够之前，一个动作做完就接着做下一个，不要停下来向玩家汇报，也不要问玩家接下来做什么；" + sleep
                    + "每个动作结果后面会写还剩多少，玩够了会提醒你，那时再收尾汇报。";
            }
        }

        /// <summary>目标进度；没设目标时返回 null。</summary>
        public GoalInfo? Goal
        {
            get
            {
                lock (sync)
                    return goalMinutes > 0 || goalDays > 0
                        ? new GoalInfo(goalMinutes, goalMinutes > 0 ? (DateTime.UtcNow - goalStartedUtc).TotalMinutes : 0, goalDays, goalDaysDone)
                        : null;
            }
        }

        /// <summary>还没玩够目标（插件据此在 AI 提前停下时自动续玩）。到了设置里的上限就不算。</summary>
        public bool GoalPending
        {
            get
            {
                if (Goal is not { } g || g.Done)
                    return false;
                lock (sync)
                {
                    UpdateMinutes();
                    return minutesUpAtDay < 0 && !Ended;
                }
            }
        }

        private static string Progress(GoalInfo g)
        {
            var parts = new List<string>();
            if (g.Days > 0)
                parts.Add(g.DaysReached ? $"{g.Days} 个游戏日已经玩够" : $"要玩 {g.Days} 个游戏日，现在是第 {g.DaysDone + 1}/{g.Days} 天");
            if (g.Minutes > 0)
                parts.Add(g.MinutesDone ? $"{g.Minutes} 分钟已经玩够" : $"要玩 {g.Minutes} 分钟，已玩 {(int)g.Played} 分钟，还剩约 {Math.Max(1, (int)Math.Ceiling(g.Minutes - g.Played))} 分钟");
            return string.Join("；", parts);
        }

        private static string Ask(GoalInfo g) => string.Join("、",
            new[] { g.Days > 0 ? $"{g.Days} 个游戏日" : null, g.Minutes > 0 ? $"{g.Minutes} 分钟" : null }.Where(s => s != null));

        /// <summary>接在每个动作结果后面的提醒：还剩多少 / 玩够了（只说一次）。没设目标返回 null。</summary>
        public string GoalReminder()
        {
            if (Goal is not { } g)
                return null;
            lock (sync)
            {
                if (!g.Done)
                {
                    string sleep = g.Days > 0 && !g.DaysReached ? "；今天的活做完就 sleep，醒来接着玩" : "";
                    return $"⏱ 玩家的游玩要求：{Progress(g)}。没玩够，别停下汇报，直接接着做下一件事{sleep}。";
                }
                if (goalDoneNoted)
                    return null;
                goalDoneNoted = true;
                return $"⏱ 玩家要的 {Ask(g)}玩够了：把手上这件事收个尾，然后向玩家汇报这次做了什么。";
            }
        }

        private string GoalStatus()
        {
            if (Goal is not { } g)
                return null;
            return !g.Done
                ? $"玩家这次的游玩要求：{Progress(g)}。没玩够不要停下来汇报，一件事做完直接接着做下一件"
                : $"玩家要的 {Ask(g)}已经玩够了：收尾后向玩家汇报";
        }

        private bool HasLimit => config.SessionDays > 0 || config.SessionMinutes > 0;

        private double Minutes => startedUtc.HasValue ? (DateTime.UtcNow - startedUtc.Value).TotalMinutes : 0;

        /// <summary>读档、或者在 DSH 里改了时长时重新开始计。</summary>
        public void Reset()
        {
            lock (sync)
            {
                startedUtc = null;
                daysCompleted = 0;
                minutesUpAtDay = -1;
            }
        }

        /// <summary>读档时调用：游戏日目标从读进来的那天重新数起（不清掉已经玩过的天数）。</summary>
        public void OnSaveLoaded()
        {
            lock (sync)
                goalLastDate = -1;
        }

        /// <summary>每次执行工具时调用：第一次调用开始计时。</summary>
        public void Touch()
        {
            lock (sync)
                startedUtc ??= DateTime.UtcNow;
        }

        /// <summary>新的一天开始（睡醒）时调用。</summary>
        public void OnDayStarted()
        {
            lock (sync)
            {
                if (startedUtc.HasValue)
                    daysCompleted++;
                // 游戏日目标按日期数：睡一觉日期 +1；读档那次 DayStarted 只定起点不算
                if (goalDays > 0)
                {
                    int today = Game1.Date.TotalDays;
                    if (goalLastDate >= 0 && today > goalLastDate)
                        goalDaysDone += today - goalLastDate;
                    goalLastDate = today;
                }
            }
        }

        /// <summary>这次游玩是不是已经彻底结束（不只是“该回家睡觉”）。</summary>
        public bool Ended
        {
            get
            {
                lock (sync)
                {
                    UpdateMinutes();
                    return (config.SessionDays > 0 && daysCompleted >= config.SessionDays)
                        || (minutesUpAtDay >= 0 && daysCompleted > minutesUpAtDay);
                }
            }
        }

        /// <summary>本次游玩是否已到上限（已彻底结束，或分钟数已到、睡完这一觉就该停）。</summary>
        public bool LimitReached
        {
            get
            {
                lock (sync)
                {
                    UpdateMinutes();
                    return Ended || minutesUpAtDay >= 0;
                }
            }
        }

        private void UpdateMinutes()
        {
            if (config.SessionMinutes > 0 && minutesUpAtDay < 0 && Minutes >= config.SessionMinutes)
                minutesUpAtDay = daysCompleted;
        }

        /// <summary>执行工具前检查：返回拒绝理由，允许时返回 null。</summary>
        public string Gate(string tool)
        {
            if (!HasLimit || AlwaysAllowed.Contains(tool))
                return null;
            lock (sync)
            {
                UpdateMinutes();
                if (Ended)
                    return $"失败：本次游玩已经结束（{Describe()}）。不要再执行游戏动作，向玩家汇报这次做了什么、下次打算做什么。"
                        + "玩家可以在 DSH「插件」页 dsh-stardew-blind-whale-farmer 的配置里（或 config.json）调整时长，重新读档就开始新的一次。";
                if (minutesUpAtDay >= 0 && tool != "sleep")
                    return $"失败：本次设定的 {config.SessionMinutes} 分钟游玩时间到了。现在请执行 sleep（会自动回家睡觉、存档），然后向玩家汇报这次做了什么。";
            }
            return null;
        }

        private string Describe()
        {
            var parts = new List<string>();
            if (config.SessionDays > 0)
                parts.Add($"游戏日 {Math.Min(daysCompleted, config.SessionDays)}/{config.SessionDays} 天");
            if (config.SessionMinutes > 0)
                parts.Add($"已玩 {(int)Minutes}/{config.SessionMinutes} 分钟");
            return string.Join("，", parts);
        }

        /// <summary>给 AI 看的进度说明；没设上限时返回 null。</summary>
        public string Status()
        {
            string goal = GoalStatus();
            string limit = LimitStatus();
            return goal == null ? limit : limit == null ? goal : goal + "】【" + limit;
        }

        private string LimitStatus()
        {
            if (!HasLimit)
                return null;
            lock (sync)
            {
                UpdateMinutes();
                if (Ended)
                    return $"本次游玩已经结束（{Describe()}）：不要再执行游戏动作，向玩家汇报。";
                if (minutesUpAtDay >= 0)
                    return $"本次游玩时间到了（{Describe()}）：马上 sleep 回家存档，然后汇报。";
                string day = config.SessionDays > 0 ? $"今天是本次第 {daysCompleted + 1}/{config.SessionDays} 个游戏日" : null;
                string min = config.SessionMinutes > 0 ? $"已玩 {(int)Minutes}/{config.SessionMinutes} 分钟" : null;
                return "本次游玩：" + string.Join("，", new[] { day, min }.Where(s => s != null))
                    + "。快到上限时把今天的活收尾、留时间回家睡觉。";
            }
        }
    }
}
