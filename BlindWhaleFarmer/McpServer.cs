using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// 本地 MCP 服务器（Streamable HTTP 传输；工具调用在客户端接受时用 SSE 回复并定时发心跳，其余用 JSON）。
    /// 让 WorkBuddy、Claude Code、Cursor 等 MCP 客户端直接调用和内置 AI 相同的工具来玩游戏，
    /// 思考由客户端完成，消耗的是客户端自己的额度。只监听 127.0.0.1。
    /// </summary>
    internal sealed class McpServer : IDisposable
    {
        private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

        private static string Instructions => Agent.SystemPrompt + McpNotes;

        /// <summary>只读取信息、不在游戏里执行动作的工具：不走 Tools、不记 transcript、结果不算“失败”。</summary>
        private static readonly HashSet<string> InfoTools = new() { "get_state", "get_guide" };

        /// <summary>只有 MCP 有的控制工具（无人值守用）：开关内置 AI、在标题画面读档。内置 AI 自己用不着。</summary>
        private static readonly HashSet<string> ControlTools = new() { "toggle_ai", "load_save" };

        private const string McpNotes = @"

## 通过 MCP 玩的额外说明
- 你看不到游戏，每次行动前先调用 get_state 读取当前状态（时间、界面、背包、地图、附近物体等），根据它决定下一步。
- 不确定该怎么玩时调用 get_guide 读攻略（游戏常识和第一年路线）。如果上面已经有“游戏常识”一节，就不用再读。
- 攻略还有专题（钓鱼、采矿、节日等），get_guide 传 topic 读：去钓鱼前看看哪片水域有值钱的鱼，下矿前看看要带什么，节日当天看看怎么参加。
- get_state 最前面的【你的长期笔记】是之前（可能是别的会话）用 update_notes 写下的目标和计划：接着它玩，计划有变化就用 update_notes 更新，换会话也不会丢。
- 一个工具执行完会返回结果文字；动作执行期间游戏时间照常流逝，你思考时游戏时间是冻结的。
- 长动作（fish、water_crops、wait 等）可能要几十秒到几分钟，fish 一次建议 casts 不超过 5。
- “内置 AI”是模组自带的另一个 AI：玩家在游戏里按 F8 启动，它会自己接管游戏，花的是玩家自己的 API 额度。你就是在玩游戏的 AI，不需要、也不要去启动它。它和你不能同时操作：它运行时你只能查信息（get_state、get_guide、gift_info、bundle_info），其他工具会返回“内置 AI 正在运行”。玩家让你来玩时，用 toggle_ai 传 on=false 停掉它，或请玩家按 F8。
- get_state 说游戏还在标题画面时，用 load_save 读取存档（不传参数读最近玩的那个）。
- 玩家让你玩一段时间（“玩半小时”“玩 4 个游戏日”）时，先用 play_for 记下（现实时间传 minutes，游戏日传 days），然后一件事做完直接接着做下一件、一天做完就 sleep 醒来接着玩，玩够之前不要停下来汇报或问玩家下一步；每个动作结果后面会写还剩多少。";

        private readonly ModEntry mod;
        private readonly HttpListener listener = new();
        private CancellationTokenSource cts;

        /// <summary>最近一次 MCP 请求的时间（UTC ticks），用于判断客户端是否在“玩”。</summary>
        private long lastActivityTicks;

        public int Port { get; }

        public McpServer(ModEntry mod, int port)
        {
            this.mod = mod;
            Port = port;
        }

        public bool EverActive => Interlocked.Read(ref lastActivityTicks) > 0;

        public DateTime LastActivityUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref lastActivityTicks);
                return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            }
        }

        public bool IsActive(int idleSeconds)
        {
            long last = Interlocked.Read(ref lastActivityTicks);
            return last > 0 && DateTime.UtcNow.Ticks - last < TimeSpan.FromSeconds(Math.Max(10, idleSeconds)).Ticks;
        }

        public void Start()
        {
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoop(cts.Token));
        }

        public void Dispose()
        {
            cts?.Cancel();
            try
            {
                listener.Close();
            }
            catch
            {
                // 进程退出时可能已经关了
            }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    if (ct.IsCancellationRequested || !listener.IsListening)
                        return;
                    continue;
                }
                // 每个请求单独处理：工具调用可能要几分钟，不能挡住 ping 等请求
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            try
            {
                // 防 DNS rebinding：浏览器发来的请求会带 Origin，只接受本机来源
                string origin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
                {
                    await Write(resp, 403, null);
                    return;
                }
                if (req.Url?.AbsolutePath.TrimEnd('/') == "/settings")
                {
                    await HandleSettings(req, resp);
                    return;
                }
                if (req.Url?.AbsolutePath.TrimEnd('/') == "/thought")
                {
                    await HandleThought(req, resp);
                    return;
                }
                if (req.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
                {
                    await Write(resp, 404, null);
                    return;
                }
                if (req.HttpMethod == "GET" || req.HttpMethod == "DELETE")
                {
                    // 不提供服务器主动推送的 SSE 流，也没有需要删除的会话
                    resp.AddHeader("Allow", "POST");
                    await Write(resp, 405, null);
                    return;
                }
                if (req.HttpMethod != "POST")
                {
                    await Write(resp, 405, null);
                    return;
                }

                string body;
                using (var rd = new StreamReader(req.InputStream, Encoding.UTF8))
                    body = await rd.ReadToEndAsync();
                Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

                JsonNode parsed;
                try
                {
                    parsed = JsonNode.Parse(body);
                }
                catch
                {
                    await Write(resp, 400, Error(null, -32700, "Parse error"));
                    return;
                }

                if (parsed is JsonArray batch)
                {
                    var results = new JsonArray();
                    foreach (var msg in batch)
                    {
                        var r = await Dispatch(msg as JsonObject);
                        if (r != null)
                            results.Add(r);
                    }
                    await Write(resp, results.Count > 0 ? 200 : 202, results.Count > 0 ? results : null);
                }
                else if (parsed is JsonObject call && call["method"]?.GetValue<string>() == "tools/call"
                         && (req.Headers["Accept"] ?? "").Contains("text/event-stream"))
                {
                    streamedCall.Value = true;
                    await WriteStreamed(resp, Dispatch(call));
                }
                else
                {
                    var r = await Dispatch(parsed as JsonObject);
                    await Write(resp, r != null ? 200 : 202, r);
                }
                // 工具调用结束时也算一次活动，避免长动作后立刻判定为空闲
                Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception ex)
            {
                // 这里绝不能再抛异常，否则响应发不出去，客户端会一直等到超时
                try
                {
                    mod.Monitor?.Log("MCP 请求处理出错：" + ex, LogLevel.Warn);
                }
                catch
                {
                    // 忽略日志失败
                }
                try
                {
                    await Write(resp, 500, Error(null, -32603, ex.Message));
                }
                catch
                {
                    // 连接可能已经断了
                }
            }
        }

        private static bool IsLocalOrigin(string origin)
        {
            return Uri.TryCreate(origin, UriKind.Absolute, out var u)
                && (u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]" || u.Host == "::1");
        }

        /// <summary>处理一条 JSON-RPC 消息；通知（没有 id）返回 null。</summary>
        internal async Task<JsonNode> Dispatch(JsonObject msg)
        {
            if (msg == null)
                return Error(null, -32600, "Invalid Request");
            string method = msg["method"]?.GetValue<string>();
            var id = msg["id"]?.DeepCloneValue();
            if (id == null)
                return null; // notifications/initialized 等通知，或客户端发来的响应
            var p = msg["params"] as JsonObject;

            switch (method)
            {
                case "initialize":
                {
                    // 新连接（可能是新会话）：状态里省略过的段落下次全部重给
                    mod.Observer?.ResetSeen();
                    string want = p?["protocolVersion"]?.GetValue<string>();
                    string version = Array.IndexOf(SupportedVersions, want) >= 0 ? want : SupportedVersions[0];
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "dsh-stardew-blind-whale-farmer", ["title"] = "大肥鱼种白饭 Blind Whale Farmer（星露谷）", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
                        ["instructions"] = Instructions
                    });
                }
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = ToolList() });
                case "tools/call":
                {
                    string name = p?["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name))
                        return Error(id, -32602, "缺少工具名");
                    if (!InfoTools.Contains(name) && !ControlTools.Contains(name) && !mod.Tools.IsKnown(name))
                        return Error(id, -32602, $"没有叫 {name} 的工具");
                    string text = await CallTool(name, p["arguments"] as JsonObject);
                    bool isError = !text.StartsWith("成功") && !InfoTools.Contains(name);
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
                        ["isError"] = isError
                    });
                }
                default:
                    return Error(id, -32601, "Method not found: " + method);
            }
        }

        /// <summary>
        /// section 能写的名字 → 段落标题里的关键词。中文写标题本身或常见说法都行（背包/物品栏、箱子/储物、地图/文字地图……），
        /// 英文不分大小写，单复数都认。
        /// </summary>
        private static readonly Dictionary<string, string> SectionAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["basic"] = "基本状态", ["status"] = "基本状态", ["state"] = "基本状态", ["基本"] = "基本状态", ["状态"] = "基本状态",
            ["menu"] = "当前界面", ["ui"] = "当前界面", ["dialog"] = "当前界面", ["dialogue"] = "当前界面", ["界面"] = "当前界面", ["对话"] = "当前界面",
            ["location"] = "位置", ["position"] = "位置", ["where"] = "位置", ["地点"] = "位置",
            ["inventory"] = "背包", ["bag"] = "背包", ["backpack"] = "背包", ["items"] = "背包", ["物品"] = "背包", ["物品栏"] = "背包", ["包"] = "背包",
            ["equipment"] = "穿戴", ["gear"] = "穿戴", ["equip"] = "穿戴", ["装备"] = "穿戴",
            ["chests"] = "箱子", ["chest"] = "箱子", ["storage"] = "箱子", ["储物"] = "箱子", ["储物箱"] = "箱子",
            ["exits"] = "出口", ["exit"] = "出口", ["doors"] = "出口", ["本地出口"] = "出口",
            ["people"] = "人物", ["npc"] = "人物", ["npcs"] = "人物", ["characters"] = "人物", ["animals"] = "人物", ["村民"] = "人物", ["动物"] = "人物",
            ["crops"] = "作物", ["crop"] = "作物", ["farm"] = "作物", ["fields"] = "作物", ["农田"] = "作物", ["庄稼"] = "作物",
            ["objects"] = "附近物体", ["object"] = "附近物体", ["nearby"] = "附近物体", ["附近"] = "附近物体", ["物体"] = "附近物体",
            ["map"] = "地图", ["文字地图"] = "地图",
            ["quests"] = "任务", ["quest"] = "任务", ["tasks"] = "任务", ["委托"] = "任务",
        };

        /// <summary>get_state 只留要看的几段（按“## 标题”切）。基本状态、当前界面和最近的提示台词总留着；第一个标题前的内容也留着。</summary>
        internal static string FilterSections(string text, string section)
        {
            var asked = section.Split(new[] { ',', '，', '、', ' ', ';', '；', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim('"', '\'', '“', '”', '【', '】', '[', ']', '#', ' ')).Where(s => s.Length > 0).ToList();
            var want = asked.Select(s => SectionAliases.TryGetValue(s, out var zh) ? zh : s).ToList();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            want.Add("基本状态");
            want.Add("当前界面");
            want.Add("最近的提示");
            var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?=^## )", System.Text.RegularExpressions.RegexOptions.Multiline);
            var kept = new List<string>();
            var names = new List<string>();
            foreach (var p in parts)
            {
                if (!p.StartsWith("## "))
                {
                    if (p.Trim().Length > 0)
                        kept.Add(p);
                    continue;
                }
                string title = p.Substring(3, Math.Max(0, p.IndexOf((char)10) - 3)).Trim();
                names.Add(title);
                // 只比标题括号前的部分（“穿戴（背包里有更好的鞋子…）”不算背包）
                string head = title.Split('（', '(')[0];
                var hits = want.Where(w => head.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count > 0)
                {
                    kept.Add(p);
                    matched.UnionWith(hits);
                }
            }
            // 写错的名字说一声，别让它以为那一段是空的
            var unknown = asked.Where(s => !matched.Contains(SectionAliases.TryGetValue(s, out var zh) ? zh : s)).ToList();
            string res = string.Join("", kept).TrimEnd();
            string all = string.Join("、", names.Select(n => n.Split('（', '(')[0]).Distinct());
            return res + Environment.NewLine + Environment.NewLine
                + (unknown.Count > 0 ? $"（section 里的 {string.Join("、", unknown)} 没有对应的段落，没给。" : "（")
                + $"只给了 section={section} 这几段；全部段落：{all}，中英文都能写，比如 背包/inventory、箱子/chests、任务/quests）";
        }

        private JsonArray ToolList()
        {
            var list = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "get_state",
                    ["description"] = "读取当前游戏状态的文字描述：时间天气、体力金钱、当前界面（对话/商店/信件等）、位置、背包、出口、人物和动物、作物、附近物体坐标、以你为中心的字符地图、任务。每次决定行动前先调用。"
                        + "箱子、穿戴、田地规划、出口、图例这几段和上次一样时只写“没变化”（10 分钟内），要全部重看传 full=true。"
                        + "只想看一两样（比如只看背包、只看任务）就传 section，省得每次读一大段。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["full"] = new JsonObject { ["type"] = "boolean", ["description"] = "true 时没变化的段落也全部给出（换了会话、记不清箱子里有什么时用）" },
                            ["section"] = new JsonObject { ["type"] = "string", ["description"] = "只看某几段，逗号分隔：基本状态、当前界面、位置、背包、穿戴、箱子、出口、人物、作物、附近物体、地图、任务（也认英文 basic/menu/location/inventory/equipment/chests/exits/people/crops/objects/map/quests）。基本状态和当前界面总会带上。不传就是全部" }
                        }
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_guide",
                    ["description"] = "读取攻略。不传 topic 读主攻略（游戏常识、装备、第一年路线）；传专题名读专题"
                                      + (Guide.Topics.Count > 0 ? $"（{string.Join("、", Guide.Topics)}）" : "")
                                      + "，比如去钓鱼前读“钓鱼”看哪里有什么鱼，下矿前读“采矿”，节日当天读“节日”。不花游戏时间。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["topic"] = new JsonObject { ["type"] = "string", ["description"] = "专题名，不传读主攻略" }
                        }
                    }
                },
                new JsonObject
                {
                    ["name"] = "toggle_ai",
                    ["description"] = "停止游戏里的内置 AI（模组自带的另一个 AI，玩家按 F8 启动，会接管游戏并花玩家自己的 API 额度）。它在运行、挡住你的动作时，传 on=false 停掉它（剧情里 F8 没反应也能用）。不传参数只查看它是否在运行。你自己就在玩游戏，不要启动它：on=true 默认会被拒绝。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["on"] = new JsonObject { ["type"] = "boolean", ["description"] = "false 停止；不传只查看状态；true 启动（默认不允许）" }
                        }
                    }
                },
                new JsonObject
                {
                    ["name"] = "load_save",
                    ["description"] = "游戏停在标题画面时读取存档（单人）。不传 save 读最近玩的那个；读档要十几秒，之后用 get_state 确认进了游戏。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["save"] = new JsonObject { ["type"] = "string", ["description"] = "存档名（农夫名字，或存档文件夹名的一部分），不传读最近玩的" }
                        }
                    }
                }
            };
            // Schema 是共用对象，复制一份再取用（JsonNode 只能有一个父节点）
            var schema = (JsonArray)JsonNode.Parse(mod.Tools.Schema.ToJsonString());
            foreach (var t in schema)
            {
                var fn = t["function"];
                list.Add(new JsonObject
                {
                    ["name"] = fn["name"].GetValue<string>(),
                    ["description"] = fn["description"].GetValue<string>(),
                    ["inputSchema"] = JsonNode.Parse(fn["parameters"].ToJsonString())
                });
            }
            return list;
        }

        private async Task<string> CallTool(string name, JsonObject args)
        {
            if (ControlTools.Contains(name))
            {
                string done;
                try
                {
                    done = await mod.MainThread.Invoke(() => name == "toggle_ai" ? ToggleAi(args?["on"]) : LoadSave(args?["save"]?.ToString())).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    done = $"错误：{ex.GetType().Name}: {ex.Message}";
                }
                mod.WriteTranscript($"[{mod.CurrentStamp}] [MCP] {name}({Shorten(args?.ToJsonString() ?? "{}", 200)}) → {Shorten(done, 400)}");
                return done;
            }
            if (mod.Agent.Running && !InfoTools.Contains(name))
            {
                // 只查信息的工具不打断内置 AI，直接放行
                if (Tools.ReadOnlyTools.Contains(name))
                    return await mod.MainThread.Invoke(() => mod.Tools.Peek(name, args?.ToJsonString())).ConfigureAwait(false);
                return "失败：游戏里的内置 AI 正在运行，它运行时只能用 get_state、get_guide、gift_info、bundle_info。"
                       + "要自己操作就先用 toggle_ai 传 on=false 停掉它（或在游戏里按 " + mod.Config.ToggleKey + "）";
            }
            if (mod.PlayerInControl && !InfoTools.Contains(name) && name is not ("update_notes" or "reserve_items" or "play_for"))
                return $"失败：玩家按 {mod.Config.TakeOverKey} 接管了游戏，现在由玩家自己操作，你的动作都会被拒绝。"
                       + $"不要再尝试游戏动作：停下来向玩家汇报刚才在做什么、遇到了什么问题，等玩家在游戏里再按 {mod.Config.TakeOverKey} 交还、并告诉你继续";

            string argText = args?.ToJsonString() ?? "{}";
            string result;
            try
            {
                if (name == "get_state")
                {
                    // part / which 这类别名换成 section（别名表在 Tools.cs）
                    Tools.ApplyInfoAliases(name, args);
                    if (args?["full"] is JsonNode fullArg && Tools.ParseBool(fullArg) == null)
                        return $"失败：get_state 的参数 full 要 true/false，你给的是 {Tools.ShowValue(fullArg)}。要看全部段落传 true，平常不传";
                    result = await mod.MainThread.Invoke(() =>
                        Context.IsWorldReady ? mod.Observer.Build(IsTrue(args?["full"]), includeRecentScreen: true) : "游戏还没有进入存档（在标题画面或正在加载）。请玩家先读取存档。").ConfigureAwait(false);
                    // section 写成字符串 "背包,任务" 或数组 ["背包","任务"] 都行
                    string section = args?["section"] switch
                    {
                        JsonValue sv when sv.TryGetValue(out string ss) && !string.IsNullOrWhiteSpace(ss) => ss,
                        JsonArray sa when sa.Count > 0 => string.Join(",", sa.Select(e => e is JsonValue ev && ev.TryGetValue(out string es) ? es : e?.ToJsonString())),
                        _ => null
                    };
                    if (section != null && Context.IsWorldReady)
                        result = FilterSections(result, section);
                    // 本次游玩进度和玩家的额外要求放最前面（它们可以在 DSH 设置里随时改，不在连接时的说明里）
                    var head = new List<string>();
                    if (mod.PlayerInControl)
                        head.Add($"【玩家接管中】玩家按 {mod.Config.TakeOverKey} 接管了游戏，你的游戏动作都会被拒绝。停下来向玩家汇报，等玩家交还");
                    if (mod.Session?.Status() is string session)
                        head.Add("【" + session + "】");
                    if (section == null && !string.IsNullOrWhiteSpace(mod.Config.ExtraInstructions))
                        head.Add("【玩家的额外要求】" + mod.Config.ExtraInstructions.Trim());
                    if (Context.IsWorldReady && GameActions.TakeExpiredReserveNote() is string expired)
                        head.Add("【留着的东西到日子了】" + expired);
                    if (section == null && Context.IsWorldReady && GameActions.ReservedSummary() is string keep)
                        head.Add("【玩家要留着的东西】" + keep);
                    // 笔记按存档保存（模组 data 文件夹），内置 AI 每步都能看到；MCP 客户端换了会话也要能接着上次的计划玩
                    if (section == null && Context.IsWorldReady)
                        head.Add("【你的长期笔记】" + (string.IsNullOrWhiteSpace(mod.Agent?.Notes)
                            ? "（空。建议用 update_notes 写下目标和计划，换会话后也能接着玩）"
                            : mod.Agent.Notes.Trim()));
                    if (mod.MainThread.TakeLateResult() is string late)
                        head.Insert(0, "【上一个动作的结果】" + late);
                    if (mod.MainThread.RunningSummary() is string running)
                        head.Insert(0, "【正在执行】" + running + "（做完前别的动作会被拒绝；做完的结果会出现在 get_state 最前面）");
                    if (head.Count > 0)
                        result = string.Join("\n", head) + "\n\n" + result;
                }
                else if (name == "get_guide")
                {
                    string topic = args?["topic"]?.GetValue<string>();
                    string text = Guide.Get(topic);
                    string list = Guide.Topics.Count > 0 ? $"可读的专题：{string.Join("、", Guide.Topics)}" : "";
                    result = text != null
                        ? text + (string.IsNullOrWhiteSpace(topic) && list != "" ? "\n\n" + list + "（get_guide 传 topic）" : "")
                        : string.IsNullOrWhiteSpace(topic)
                            ? "没有加载攻略文件（模组目录 guide/ 下的 .md，config.json 的 GuideFile 指定），按基本操作规则玩即可"
                            : $"没有叫“{topic}”的专题。{list}";
                }
                else
                {
                    mod.Hud.SetAction($"[MCP] {name} {argText}");
                    var exec = mod.Tools.Execute(name, argText);
                    // 不走 SSE 的客户端常见 60 秒就超时，超时后动作在游戏里接着跑、结果却丢了：
                    // 快到时先回一句“还在执行”和进度，做完的结果存起来，下次 get_state 放在最前面
                    if (!streamedCall.Value && await Task.WhenAny(exec, Task.Delay(EarlyReplyMs)).ConfigureAwait(false) != exec)
                    {
                        string toolName = name;
                        _ = exec.ContinueWith(t => mod.MainThread.StoreLateResult(toolName,
                            t.Status == TaskStatus.RanToCompletion ? t.Result : "出错：" + t.Exception?.GetBaseException().Message));
                        string running = mod.MainThread.RunningSummary();
                        result = $"成功：{name} 还在游戏里执行，先回你一句免得调用超时（{running ?? "快做完了"}）。"
                            + "它做完后的结果会出现在下一次 get_state 的最前面【上一个动作的结果】里。现在用 get_state 看进度；"
                            + "它结束前别的动作会被拒绝（改主意了用 cancel_action 让它停下）";
                    }
                    else
                        result = await exec.ConfigureAwait(false);
                    mod.Hud.SetAction($"[MCP] {name} → {Shorten(result, 80)}");
                }
            }
            catch (Exception ex)
            {
                result = $"错误：{ex.GetType().Name}: {ex.Message}";
            }
            if (!InfoTools.Contains(name))
                mod.WriteTranscript($"[{mod.CurrentStamp}] [MCP] {name}({Shorten(argText, 200)}) → {Shorten(result, 400)}");
            return result;
        }

        /// <summary>toggle_ai：和按 F8 一样，但不走按键（剧情、对话框开着时按键会被游戏吃掉）。在主线程上调用。</summary>
        private string ToggleAi(JsonNode on)
        {
            if (!Context.IsWorldReady)
                return "失败：游戏还没有进入存档，先用 load_save 读档";
            // 不传参数只报状态：以前是“切换”，模型随手调一次就会把内置 AI 打开
            if (on is not JsonValue v)
                return mod.Agent.Running
                    ? "成功：内置 AI 正在运行（它在玩游戏，你的动作会被拒绝）。要自己操作就传 on=false 停掉它"
                    : "成功：内置 AI 停着，你可以正常操作。不需要启动它";
            if (Tools.ParseBool(v) is not bool want)
                return $"失败：on 要 true/false，你给的是 {Tools.ShowValue(v)}。停掉内置 AI 传 false，只看状态就别传 on";
            if (want == mod.Agent.Running)
                return $"成功：内置 AI 本来就{(want ? "在运行" : "停着")}";
            // 内置 AI 会接管游戏、花玩家自己的 API 额度：MCP 默认只能停，启动要玩家自己按键或在配置里放行
            if (want && !mod.Config.McpCanStartAi)
                return $"失败：不能通过 MCP 启动内置 AI。它是另一个会接管游戏、消耗玩家 API 额度的 AI，你自己就在玩游戏，不需要它。"
                       + $"玩家想用它就在游戏里按 {mod.Config.ToggleKey}（要让 MCP 也能启动，把 config.json 的 McpCanStartAi 改成 true）";
            if (!want)
            {
                mod.Agent.Stop("MCP 的 toggle_ai");
                return "成功：内置 AI 已停止，现在可以通过 MCP 操作";
            }
            mod.Agent.Start();
            return mod.Agent.Running
                ? "成功：内置 AI 已启动。它运行时 MCP 只能查信息"
                : "失败：内置 AI 没启动起来（多半是没有配置 API Key：Mods/BlindWhaleFarmer/config.json 的 ApiKey）";
        }

        /// <summary>load_save：在标题画面读档，和在“读取游戏”里点存档一样（LoadGameMenu 的 SaveFileSlot.Activate）。在主线程上调用。</summary>
        private string LoadSave(string want)
        {
            if (Context.IsWorldReady)
                return $"成功：已经在存档里了（{Game1.player.Name} 的{Game1.player.farmName.Value}农场），不用读档";
            if (Game1.activeClickableMenu is not TitleMenu || Game1.gameMode != Game1.titleScreenGameMode)
                return "失败：现在不在标题画面（游戏正在启动或正在读档），等 10 秒再用 get_state 看看";
            // 标题菜单在 ticksUntilLanguageLoad 从 0 变为 -1 时才应用启动语言。
            if (TitleMenu.ticksUntilLanguageLoad >= 0)
                return "失败：游戏还在启动（正在加载语言设置），等 2 秒再读档，不然进去会是英文界面";
            var saves = new List<(string Slot, DateTime Played)>();
            if (Directory.Exists(Constants.SavesPath))
                foreach (string dir in Directory.GetDirectories(Constants.SavesPath))
                {
                    string slot = Path.GetFileName(dir);
                    string file = Path.Combine(dir, slot);
                    if (File.Exists(file))
                        saves.Add((slot, File.GetLastWriteTime(file)));
                }
            saves.Sort((a, b) => b.Played.CompareTo(a.Played));
            if (saves.Count == 0)
                return "失败：没有找到任何存档，请玩家先在游戏里新建一个";
            string list = string.Join("、", saves.ConvertAll(s => $"{s.Slot}（{s.Played:MM-dd HH:mm}）"));
            var pick = saves[0];
            if (!string.IsNullOrWhiteSpace(want))
            {
                int i = saves.FindIndex(s => s.Slot.Contains(want.Trim(), StringComparison.OrdinalIgnoreCase));
                if (i < 0)
                    return $"失败：没有名字里带“{want}”的存档。现有存档：{list}";
                pick = saves[i];
            }
            SaveGame.Load(pick.Slot);
            Game1.exitActiveMenu();
            return $"成功：正在读取存档 {pick.Slot}（上次游玩 {pick.Played:MM-dd HH:mm}），要十几秒。等 15 秒后用 get_state 确认进了游戏";
        }

        private static string Shorten(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static JsonObject Result(JsonNode id, JsonNode result) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JsonObject Error(JsonNode id, int code, string message) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

        /// <summary>这次游戏启动的标识：DSH 插件看到它变了（游戏重开过），就把设置再推一遍。</summary>
        private readonly string instance = Guid.NewGuid().ToString("N");

        /// <summary>DSH 插件上次推来的设置版本号（插件自己算的），用来判断要不要重推。</summary>
        private string lastRevision = "";

        /// <summary>
        /// /settings：给 DSH 的 dsh-stardew-blind-whale-farmer 插件用。GET 返回当前设置和本次游玩进度；POST {revision, settings} 应用设置。
        /// 和 /mcp 一样只监听 127.0.0.1，并拒绝网页来源（Origin 检查在 Handle 里）。
        /// </summary>
        private async Task HandleSettings(HttpListenerRequest req, HttpListenerResponse resp)
        {
            if (req.HttpMethod == "POST")
            {
                string body;
                using (var rd = new StreamReader(req.InputStream, Encoding.UTF8))
                    body = await rd.ReadToEndAsync();
                if (JsonNode.Parse(body) is not JsonObject msg || msg["settings"] is not JsonObject settings)
                {
                    await Write(resp, 400, new JsonObject { ["error"] = "需要 {revision, settings}" });
                    return;
                }
                string log = mod.ApplyRemoteSettings(settings);
                lastRevision = msg["revision"]?.GetValue<string>() ?? "";
                mod.Monitor?.Log(log, LogLevel.Info);
                await Write(resp, 200, SettingsSnapshot(log));
                return;
            }
            if (req.HttpMethod == "GET")
            {
                await Write(resp, 200, SettingsSnapshot(null));
                return;
            }
            resp.AddHeader("Allow", "GET, POST");
            await Write(resp, 405, null);
        }

        /// <summary>单条想法的最大长度（和内置 AI 记日志截断一致，HUD 最多画 4 行）。</summary>
        internal const int MaxThoughtLength = 200;

        /// <summary>把想法整理成单行：过滤控制字符、合并连续空白，超过上限截断并加省略号。</summary>
        internal static string SanitizeThought(string text, int maxLen = MaxThoughtLength)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var sb = new StringBuilder(Math.Min(text.Length, Math.Max(maxLen, 16) + 8));
            bool prevSpace = true;
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t' || char.IsWhiteSpace(c))
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                    continue;
                }
                if (char.IsControl(c))
                    continue;
                sb.Append(c);
                prevSpace = false;
            }
            string s = sb.ToString().Trim();
            if (maxLen > 0 && s.Length > maxLen)
                s = maxLen == 1 ? "…" : s.Substring(0, maxLen - 1) + "…";
            return s;
        }

        /// <summary>
        /// /thought：给 DSH 插件实时推送 AI 的想法和说的话，显示在游戏左上角 HUD 的“想法：”一栏（受 ShowHud 控制）。
        /// 只监听 127.0.0.1，并拒绝外部网页来源（Origin 检查在 Handle 里）。
        /// </summary>
        private async Task HandleThought(HttpListenerRequest req, HttpListenerResponse resp)
        {
            if (req.HttpMethod != "POST")
            {
                resp.AddHeader("Allow", "POST");
                await Write(resp, 405, null);
                return;
            }
            string body;
            using (var rd = new StreamReader(req.InputStream, Encoding.UTF8))
                body = await rd.ReadToEndAsync();
            JsonNode parsed;
            try
            {
                parsed = JsonNode.Parse(body);
            }
            catch
            {
                await Write(resp, 400, new JsonObject { ["error"] = "无效的 JSON" });
                return;
            }
            if (parsed is not JsonObject msg || msg["text"] is not JsonValue textVal || !textVal.TryGetValue<string>(out string raw))
            {
                await Write(resp, 400, new JsonObject { ["error"] = "需要 {\"text\": \"...\"}" });
                return;
            }
            string thought = SanitizeThought(raw);
            mod.Hud?.SetThought(thought);
            await Write(resp, 200, new JsonObject { ["ok"] = true, ["thought"] = thought });
        }

        private JsonObject SettingsSnapshot(string message)
        {
            var c = mod.Config;
            return new JsonObject
            {
                ["instance"] = instance,
                ["revision"] = lastRevision,
                ["message"] = message,
                ["settings"] = new JsonObject
                {
                    ["sessionDays"] = c.SessionDays,
                    ["sessionMinutes"] = c.SessionMinutes,
                    ["freezeTime"] = c.McpFreezeTime,
                    ["idleSeconds"] = c.McpIdleSeconds,
                    ["allowChat"] = c.AllowChat,
                    ["allowSpendSharedMoney"] = c.AllowSpendSharedMoney,
                    ["aiHomeLocation"] = c.AiHomeLocation,
                    ["coSleepDefault"] = c.CoSleepDefault,
                    ["passbyEmoteEnabled"] = c.PassbyEmoteEnabled,
                    ["passbyEmoteChance"] = c.PassbyEmoteChance,
                    ["passbyEmoteCooldownMinutes"] = c.PassbyEmoteCooldownMinutes,
                    ["idleFollowEnabled"] = c.IdleFollowEnabled,
                    ["idleFollowIntervalSeconds"] = c.IdleFollowIntervalSeconds,
                    ["idleFollowDurationSeconds"] = c.IdleFollowDurationSeconds,
                    ["proactiveGiftEnabled"] = c.ProactiveGiftEnabled,
                    ["proactiveGiftChance"] = c.ProactiveGiftChance,
                    ["proactiveGiftCooldownDays"] = c.ProactiveGiftCooldownDays,
                    ["extraInstructions"] = c.ExtraInstructions,
                    ["reservedItems"] = c.ReservedItems,
                    ["guideFile"] = c.GuideFile
                },
                ["session"] = mod.Session?.Status() ?? "不限时长",
                ["sessionEnded"] = mod.Session?.LimitReached == true,
                ["playerInControl"] = mod.PlayerInControl,
                ["agentRunning"] = mod.Agent?.Running == true,
                // 玩家在聊天里要求的游玩时长：插件在 AI 提前停下时据此自动续玩
                ["goal"] = mod.Session?.Goal is { } g
                    ? new JsonObject { ["minutes"] = g.Minutes, ["played"] = Math.Round(g.Played, 1), ["days"] = g.Days, ["daysDone"] = g.DaysDone, ["pending"] = mod.Session.GoalPending && !mod.PlayerInControl && mod.Agent?.Running != true }
                    : null
            };
        }

        private static bool IsTrue(JsonNode n) => Tools.ParseBool(n) == true;

        private const int KeepAliveMs = 15000;

        /// <summary>不走 SSE 的工具调用最多等多久先回复（MCP 客户端默认 60 秒超时，留点余量）。</summary>
        private const int EarlyReplyMs = 45000;

        /// <summary>这次请求是 SSE（客户端能一直等，每 15 秒有 keep-alive），不用提前回复。</summary>
        private static readonly AsyncLocal<bool> streamedCall = new();

        /// <summary>
        /// 工具调用用 SSE 回复：先发响应头，动作执行期间每 15 秒发一行注释（": keep-alive"），结束时发结果事件。
        /// 客户端用 Node 自带的 fetch 时，响应头 5 分钟内不到、或者 5 分钟没收到数据就会报 “fetch failed”，
        /// 而下矿、钓鱼最长要 15 分钟。客户端中途断开（比如用户按了停止）时，游戏里的动作也停下，不在没人看的情况下继续跑。
        /// </summary>
        private async Task WriteStreamed(HttpListenerResponse resp, Task<JsonNode> work)
        {
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream; charset=utf-8";
            resp.Headers["Cache-Control"] = "no-cache";
            resp.SendChunked = true;
            var output = resp.OutputStream;
            async Task Send(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await output.WriteAsync(bytes, 0, bytes.Length);
                await output.FlushAsync();
            }

            try
            {
                await Send(": start\n\n");
                while (await Task.WhenAny(work, Task.Delay(KeepAliveMs)) != work)
                {
                    await Send(": keep-alive\n\n");
                    Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
                }
                var result = await work;
                await Send("event: message\ndata: " + (result ?? Error(null, -32603, "no result")).ToJsonString() + "\n\n");
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException)
            {
                if (!work.IsCompleted)
                {
                    mod.MainThread?.CancelCurrent("MCP 客户端断开了连接，动作已中止");
                    mod.Monitor?.Log("MCP 客户端在动作执行中断开了连接，已中止当前动作。", LogLevel.Info);
                }
            }
            finally
            {
                try
                {
                    resp.Close();
                }
                catch
                {
                    // 连接已经断了
                }
            }
        }

        private static async Task Write(HttpListenerResponse resp, int status, JsonNode body)
        {
            resp.StatusCode = status;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            resp.Close();
        }
    }

    internal static class JsonNodeExtensions
    {
        /// <summary>复制 id（数字或字符串），net6 没有 DeepClone。</summary>
        public static JsonNode DeepCloneValue(this JsonNode node) => node == null ? null : JsonNode.Parse(node.ToJsonString());
    }
}
