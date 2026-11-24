using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using InventoryEntry = System.ValueTuple<string, string, int, int>;

namespace BlindWhaleFarmer
{
    /// <summary>动作协程的最终结果。协程 yield 它就表示结束。</summary>
    internal sealed class ActionResult
    {
        public bool Ok { get; }
        public string Message { get; }

        private ActionResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }

        public static ActionResult Success(string msg) => new ActionResult(true, "成功：" + msg);
        public static ActionResult Fail(string msg) => new ActionResult(false, "失败：" + msg);
    }

    /// <summary>把获得明细单独交给工具，在现有的提醒之后输出。</summary>
    internal readonly record struct ActionCompletion(string Message, string InventoryNote = null);

    /// <summary>子协程用来回传结果的容器（子协程只 yield null 等待）。</summary>
    internal sealed class Outcome
    {
        public bool Ok { get; private set; } = true;
        public string Message { get; private set; } = "";

        public void Fail(string msg)
        {
            Ok = false;
            Message = msg;
        }

        public void Succeed(string msg = "")
        {
            Ok = true;
            Message = msg;
        }
    }

    /// <summary>
    /// 把后台线程（调用大模型）里的请求切换到游戏主线程执行。
    /// 游戏状态只能在主线程读写，所以观察和动作都通过这里排队。
    /// </summary>
    internal sealed class MainThread
    {
        private readonly IMonitor monitor;
        private readonly ConcurrentQueue<Action> queue = new();

        private IEnumerator<object> current;
        private Func<IEnumerable<object>> currentFactory;
        private volatile TaskCompletionSource<ActionCompletion> currentTcs;
        private Action currentAfterAction;
        private IEnumerator<object> idleCurrent;
        private DateTime lastIdleOrActionEndUtc = DateTime.UtcNow;
        private bool currentTrackInventory;
        private InventoryEntry[] actionInventoryStart;
        private InventoryEntry[] outsideInventoryBaseline;
        private InventoryEntry[] outsideInventoryChanges = Array.Empty<InventoryEntry>();
        private int currentTicks;
        private int currentMaxTicks;
        /// <summary>正在跑的动作叫什么、什么时候开始的（“上一个动作还在执行”时告诉 AI）。</summary>
        private string currentName;
        private DateTime currentStartedUtc;
        /// <summary>动作开始时的生命值；-1 表示这个动作不因掉血中断。</summary>
        private int currentStartHealth = -1;

        /// <summary>读游戏状态；离线测试里没有游戏实例会抛异常，这时返回 fallback。</summary>
        private static T Safe<T>(Func<T> read, T fallback = default)
        {
            try
            {
                return read();
            }
            catch
            {
                return fallback;
            }
        }

        public MainThread(IMonitor monitor)
        {
            this.monitor = monitor;
        }

        public bool IsBusy => currentTcs != null;
        public bool IsIdleRunning => idleCurrent != null;
        public DateTime LastActivityEndedUtc => lastIdleOrActionEndUtc;

        /// <summary>在没有正式动作时启动一个随时可被打断的空闲协程（如联机空闲跟着玩家卖萌）。</summary>
        public bool StartIdle(Func<IEnumerable<object>> factory)
        {
            if (IsBusy || IsIdleRunning || factory == null)
                return false;
            idleCurrent = factory().GetEnumerator();
            return true;
        }

        /// <summary>立刻中止空闲协程（有新任务、有人说话、玩家接管、换图/回标题时调用），不报错、不阻塞正式动作。</summary>
        public void CancelIdle(bool haltPlayer = true)
        {
            if (idleCurrent == null)
                return;
            try
            {
                idleCurrent.Dispose();
            }
            catch
            {
                // 忽略空闲行为清理异常
            }
            idleCurrent = null;
            lastIdleOrActionEndUtc = DateTime.UtcNow;
            if (haltPlayer)
            {
                Safe(() =>
                {
                    StardewValley.Game1.player?.Halt();
                    return true;
                });
            }
        }

        private static InventoryEntry[] CaptureLocalInventory() => Safe(() =>
        {
            var items = Context.IsWorldReady ? Game1.player?.Items : null;
            if (items == null)
                return null;
            return InventoryDiff.Snapshot(items.Where(item => item != null).Select(item =>
            {
                string id = item.QualifiedItemId;
                return (id, Safe(() => item.DisplayName, id), item.Quality, item.Stack);
            }));
        });

        public void ResetInventory(bool worldReady = false)
        {
            actionInventoryStart = null;
            outsideInventoryBaseline = worldReady ? CaptureLocalInventory() : null;
            outsideInventoryChanges = Array.Empty<InventoryEntry>();
        }

        private void UpdateOutsideInventory(InventoryEntry[] now)
        {
            if (now == null)
                return;
            if (outsideInventoryBaseline != null)
                outsideInventoryChanges = InventoryDiff.Sum(outsideInventoryChanges.Concat(
                    InventoryDiff.Difference(outsideInventoryBaseline, now)));
            outsideInventoryBaseline = now;
        }

        public string TakeOutsideInventoryNote()
        {
            // 等过场时动作还没开始；动作已经在跑时，只读开始前攒下的变化。
            if (!IsBusy || !currentTrackInventory || currentFactory != null)
                UpdateOutsideInventory(CaptureLocalInventory());
            string gains = InventoryDiff.FormatGains(outsideInventoryChanges);
            outsideInventoryChanges = Array.Empty<InventoryEntry>();
            return gains == null ? null : "上次以来动作外获得：" + gains;
        }

        /// <summary>长动作自己报的进度（比如“矿洞第 12 层，已下 3/5 层”），动作开始时清空。</summary>
        public static volatile string Progress;

        /// <summary>正在跑的动作：名字、跑了几秒、进度。没在跑返回 null。（HTTP 线程读，字段都是一次性赋值，读到旧值也无妨）</summary>
        public string RunningSummary()
        {
            if (!IsBusy)
                return null;
            int sec = (int)(DateTime.UtcNow - currentStartedUtc).TotalSeconds;
            string p = Progress;
            return $"{currentName ?? "动作"} 已经执行 {sec / 60} 分 {sec % 60} 秒" + (string.IsNullOrEmpty(p) ? "" : $"，进度：{p}");
        }

        private readonly object lateLock = new();
        private string lateResult;

        /// <summary>调用方等不及先拿到了“还在执行”的回复：动作做完后的结果先存着，下次 get_state 放在最前面。</summary>
        public void StoreLateResult(string name, string result)
        {
            lock (lateLock)
                lateResult = $"{name}（{DateTime.Now:HH:mm:ss} 做完）：{result}";
        }

        public string TakeLateResult()
        {
            lock (lateLock)
            {
                string r = lateResult;
                lateResult = null;
                return r;
            }
        }

        public Task<T> Invoke<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private bool currentInterruptOnChat;
        /// <summary>动作还没开始跑就在等过场（游戏冻结操作）的帧数；开始跑以后为 -1。</summary>
        private int currentFreezeWait;
        /// <summary>途中冒出剧情就停下（开始时就在剧情里的不算）；连续看到剧情的帧数，防抖用。</summary>
        private bool currentStopOnScene;
        private int currentSceneTicks;

        /// <summary>
        /// 在主线程上逐帧运行一个动作协程，返回其结果文字。
        /// preemptWhenStuck：处理界面的动作（dialogue、skip_event 等）在剧情/界面打开时可以打断正在跑的动作——
        /// 那个动作被剧情挡住本来也走不动，不打断的话两边互相等，会卡死。
        /// </summary>
        public Task<ActionCompletion> RunAction(Func<IEnumerable<object>> factory, int maxSeconds, bool preemptWhenStuck = false,
            bool interruptOnDamage = false, string name = null, bool interruptOnChat = false, bool stopOnScene = false,
            bool trackInventory = true, Action afterAction = null)
        {
            var tcs = new TaskCompletionSource<ActionCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(() =>
            {
                if (IsIdleRunning)
                    CancelIdle(haltPlayer: true);
                if (IsBusy && preemptWhenStuck && Safe(() => Game1.eventUp || Game1.activeClickableMenu != null))
                    Finish("失败：被剧情/界面打断，已中止（先处理剧情或界面，再重新执行）");
                // 同一步里连发两个 wait（MCP 客户端会并行调用）：第二个不报“还在执行”，并进正在跑的那个，一起返回
                if (IsBusy && name == "wait" && currentName == "wait")
                {
                    waitFollowers.Add(tcs);
                    return;
                }
                if (IsBusy)
                {
                    int sec = (int)(DateTime.UtcNow - currentStartedUtc).TotalSeconds;
                    string what = currentName != null ? $"（{currentName}，已经执行 {sec / 60} 分 {sec % 60} 秒，最长 {currentMaxTicks / 3600} 分钟）" : "";
                    string p = Progress;
                    tcs.SetResult(new ActionCompletion($"失败：上一个动作{what}还在执行，它结束前其他动作都会被拒绝。"
                        + (string.IsNullOrEmpty(p) ? "" : $"进度：{p}。")
                        + "它做完后结果会出现在 get_state 最前面；可以用 get_state 看情况，过一会儿再试，或者 cancel_action 让它停下"));
                    return;
                }
                currentFactory = factory;
                currentAfterAction = afterAction;
                currentTrackInventory = trackInventory;
                currentName = name;
                currentStartedUtc = DateTime.UtcNow;
                Progress = null;
                currentTicks = 0;
                currentMaxTicks = Math.Max(1, maxSeconds) * 60;
                currentInterruptOnChat = interruptOnChat;
                currentFreezeWait = 0;
                currentStopOnScene = stopOnScene && !Safe(() => Game1.eventUp, false);
                currentSceneTicks = 0;
                currentStartHealth = interruptOnDamage ? Safe(() => Game1.player?.health ?? -1, -1) : -1;
                currentTcs = tcs;
            });
            return tcs.Task;
        }

        /// <summary>每帧由 ModEntry 调用。</summary>
        public void Tick(bool worldReady)
        {
            while (queue.TryDequeue(out var action))
                action();

            if (!IsBusy)
            {
                if (idleCurrent != null)
                {
                    if (!worldReady)
                    {
                        CancelIdle(haltPlayer: false);
                        return;
                    }
                    try
                    {
                        if (!idleCurrent.MoveNext() || idleCurrent.Current is ActionResult)
                            CancelIdle(haltPlayer: true);
                    }
                    catch (Exception ex)
                    {
                        monitor?.Log($"空闲互动出错，已停止：{ex.Message}", LogLevel.Trace);
                        CancelIdle(haltPlayer: true);
                    }
                }
                return;
            }

            if (!worldReady)
            {
                Finish("失败：游戏世界未就绪（可能回到了标题画面）");
                return;
            }

            // 社区中心修复房间之类的过场：游戏冻结操作（freezeControls），这时走路会被我们直接设位置硬拖着走、卡在半路。
            // 动作开始前先等过场播完（最多 60 秒，不算进动作自己的时限）
            if (currentFreezeWait >= 0)
            {
                if (currentFreezeWait < 60 * 60 && Safe(() => StardewValley.Game1.freezeControls && StardewValley.Game1.activeClickableMenu == null && !StardewValley.Game1.eventUp, false))
                {
                    currentFreezeWait++;
                    return;
                }
                currentFreezeWait = -1;
            }

            currentTicks++;
            if (currentTicks > currentMaxTicks)
            {
                Finish("失败：动作超时，已中止");
                return;
            }
            // 走进某个地方、和东西互动时触发了剧情（心事件、第一次进矿洞/社区中心……）：马上停下。
            // 接着走路、传送会把人带离放剧情的地图，剧情卡在原地、人困在地图外
            if (currentStopOnScene)
            {
                string scene = Safe(() => GameActions.SceneStartedNote(), null);
                if (scene == null)
                    currentSceneTicks = 0;
                else if (++currentSceneTicks >= 5)
                {
                    Finish("失败：" + scene);
                    return;
                }
            }
            // 联机时有人说话：先停下听（结果后面会附上说了什么）
            if (currentInterruptOnChat && Safe(() => GameActions.ChatWantsAttention(), false))
            {
                Finish("失败：有人说话了，已停下（动作没做完，听完可以接着做）");
                return;
            }
            // 非打怪动作途中掉血（被怪打了）：停下，让 AI 先清怪（结果后面会附上附近的怪）
            int health = currentStartHealth >= 0 ? Safe(() => StardewValley.Game1.player.health, currentStartHealth) : -1;
            if (currentStartHealth >= 0 && health < currentStartHealth)
            {
                string attacker = Safe(() => GameActions.NearestAttacker(), null);
                Finish($"失败：干活时被攻击了（生命 {currentStartHealth} → {health}），已停下。"
                    + (attacker != null ? $"最近的怪是{attacker}。" : "") + "先用 fight 清掉附近的怪再继续");
                return;
            }

            try
            {
                if (currentFactory is { } factory)
                {
                    if (currentTrackInventory)
                    {
                        actionInventoryStart = CaptureLocalInventory();
                        UpdateOutsideInventory(actionInventoryStart);
                    }
                    currentFactory = null;
                    current = factory().GetEnumerator();
                }
                if (!current.MoveNext())
                {
                    Finish("成功：完成");
                    return;
                }
                if (current.Current is ActionResult result)
                    Finish(result.Message);
            }
            catch (Exception ex)
            {
                monitor.Log($"动作执行出错：{ex}", LogLevel.Warn);
                Finish("错误：" + ex.Message);
            }
        }

        /// <summary>从任意线程中止正在跑的动作（在主线程下一帧执行）；那时已经换成别的动作就不动它。</summary>
        public void CancelCurrent(string reason)
        {
            var tcs = currentTcs;
            if (tcs == null)
                return;
            queue.Enqueue(() =>
            {
                if (currentTcs == tcs)
                    Finish("失败：" + reason);
            });
        }

        /// <summary>并进正在跑的 wait 的其他 wait 调用，等它结束时一起返回。</summary>
        private readonly List<TaskCompletionSource<ActionCompletion>> waitFollowers = new();

        /// <summary>中止正在执行的动作（cancel_action）：返回中止了什么、跑了多久。</summary>
        public Task<string> CancelForTool()
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(() =>
            {
                if (!IsBusy)
                {
                    tcs.SetResult("成功：现在没有正在执行的动作，不用中止");
                    return;
                }
                int sec = (int)(DateTime.UtcNow - currentStartedUtc).TotalSeconds;
                string what = currentName ?? "动作";
                Finish("失败：被 cancel_action 中止了，动作可能只做了一半。先 get_state 看看现在的情况");
                Safe(() =>
                {
                    StardewValley.Game1.player?.Halt();
                    return true;
                });
                tcs.SetResult($"成功：已中止 {what}（跑了 {sec / 60} 分 {sec % 60} 秒）。它可能只做了一半，先 get_state 看看现在的情况再决定接下来做什么");
            });
            return tcs.Task;
        }

        private void Finish(string message)
        {
            var tcs = currentTcs;
            try
            {
                current?.Dispose();
            }
            catch (Exception ex)
            {
                monitor?.Log($"动作清理出错：{ex}", LogLevel.Warn);
                message = "错误：" + ex.Message;
            }
            string inventoryNote = null;
            if (currentFactory == null)
            {
                try
                {
                    currentAfterAction?.Invoke();
                }
                catch (Exception ex)
                {
                    monitor?.Log($"动作后处理出错：{ex}", LogLevel.Trace);
                }
                if (currentTrackInventory)
                {
                    var now = CaptureLocalInventory();
                    if (actionInventoryStart != null && now != null)
                        inventoryNote = InventoryDiff.FormatGains(InventoryDiff.Difference(actionInventoryStart, now));
                    outsideInventoryBaseline = now;
                }
            }
            current = null;
            currentFactory = null;
            currentAfterAction = null;
            actionInventoryStart = null;
            currentTcs = null;
            lastIdleOrActionEndUtc = DateTime.UtcNow;
            tcs?.TrySetResult(new ActionCompletion(message, inventoryNote));
            if (waitFollowers.Count > 0)
            {
                string merged = message.StartsWith("成功：") ? "成功：（和同时发出的上一个 wait 合并成了一次）" + message.Substring(3) : message;
                foreach (var f in waitFollowers)
                    f.TrySetResult(new ActionCompletion(merged, inventoryNote));
                waitFollowers.Clear();
            }
        }
    }
}
