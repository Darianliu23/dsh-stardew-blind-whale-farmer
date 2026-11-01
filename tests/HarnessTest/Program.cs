using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// 通过反射调用模组里的 internal 类型，在不启动游戏的情况下测试：工具 schema、参数解析、DeepSeek 客户端收发。
class Program
{
    static int failures = 0;
    static string Shorten(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "…";

    static void Check(bool cond, string what) { Console.WriteLine((cond ? "PASS " : "FAIL ") + what); if (!cond) failures++; }

    static async Task<int> Main()
    {
        var asm = Assembly.Load("BlindWhaleFarmer");
        var tMain = asm.GetType("BlindWhaleFarmer.MainThread");
        var tActions = asm.GetType("BlindWhaleFarmer.GameActions");
        var tTools = asm.GetType("BlindWhaleFarmer.Tools");
        var tConfig = asm.GetType("BlindWhaleFarmer.ModConfig");
        var tClient = asm.GetType("BlindWhaleFarmer.DeepSeekClient");

        var main = Activator.CreateInstance(tMain, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { null }, null);
        var actions = Activator.CreateInstance(tActions, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { null, null }, null);
        Func<string, string> notes = s => "成功：笔记已更新(" + s + ")";
        var tools = Activator.CreateInstance(tTools, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { main, actions, notes }, null);
        var schema = (JsonArray)tTools.GetProperty("Schema").GetValue(tools);

        Check(schema.Count >= 24, $"schema 有 {schema.Count} 个工具");
        foreach (var t in schema)
        {
            var fn = t["function"];
            string name = fn["name"].GetValue<string>();
            bool ok = t["type"].GetValue<string>() == "function" && fn["parameters"]["type"].GetValue<string>() == "object";
            foreach (var r in (JsonArray)fn["parameters"]["required"])
                ok &= fn["parameters"]["properties"][r.GetValue<string>()] != null;
            Check(ok, "schema 合法：" + name);
        }
        Console.WriteLine("schema 字节数：" + schema.ToJsonString().Length);

        var exec = tTools.GetMethod("Execute");
        string r1 = await (Task<string>)exec.Invoke(tools, new object[] { "update_notes", "{\"notes\":\"种防风草\"}" });
        Check(r1.Contains("种防风草"), "update_notes 直接执行：" + r1);
        string r2 = await (Task<string>)exec.Invoke(tools, new object[] { "no_such_tool", "{}" });
        Check(r2.StartsWith("失败"), "未知工具报错：" + r2);
        string r3 = await (Task<string>)exec.Invoke(tools, new object[] { "move_to", "{bad json" });
        Check(r3.StartsWith("失败"), "非法 JSON 报错：" + r3);

        // move_to 会排队到主线程；手动 Tick 一下。世界未就绪时应当失败而不是崩溃
        var pending = (Task<string>)exec.Invoke(tools, new object[] { "move_to", "{\"x\":\"5\",\"y\":6}" });
        tMain.GetMethod("Tick").Invoke(main, new object[] { false });
        tMain.GetMethod("Tick").Invoke(main, new object[] { false });
        string r4 = await pending;
        Check(r4.StartsWith("失败") || r4.StartsWith("错误"), "世界未就绪时动作安全失败：" + r4);

        // ── DeepSeek 客户端：本地假服务器 ──
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18765/");
        listener.Start();
        string received = null, auth = null;
        var server = Task.Run(async () =>
        {
            // 两次请求：第一次带文字内容，第二次 content 为 null（只调用工具，DeepSeek 常见）
            for (int n = 0; n < 2; n++)
            {
                var ctx = await listener.GetContextAsync();
                auth = ctx.Request.Headers["Authorization"];
                using (var rd = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8)) received = await rd.ReadToEndAsync();
                string contentJson = n == 0 ? "\"先浇水。\"" : "null";
                string resp = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + contentJson + ",\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"water_crops\",\"arguments\":\"{}\"}},{\"id\":\"call_2\",\"type\":\"function\",\"function\":{\"name\":\"move_to\",\"arguments\":\"{\\\"x\\\":3,\\\"y\\\":4}\"}}]}}],\"usage\":{\"prompt_tokens\":1234,\"completion_tokens\":56}}";
                var bytes = Encoding.UTF8.GetBytes(resp);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        });

        var config = Activator.CreateInstance(tConfig);
        tConfig.GetProperty("ApiKey").SetValue(config, "sk-test");
        tConfig.GetProperty("BaseUrl").SetValue(config, "http://127.0.0.1:18765");
        var client = Activator.CreateInstance(tClient, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { config }, null);
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = "sys" }, new JsonObject { ["role"] = "user", ["content"] = "状态" } };
        // 和 Agent 一样，每次都传同一个 Schema 对象
        var chatTask = (Task)tClient.GetMethod("Chat").Invoke(client, new object[] { messages, schema, CancellationToken.None });
        await chatTask;
        var reply = chatTask.GetType().GetProperty("Result").GetValue(chatTask);
        var tReply = reply.GetType();
        string content = (string)tReply.GetField("Content").GetValue(reply);
        var calls = (System.Collections.IList)tReply.GetField("ToolCalls").GetValue(reply);
        Check(auth == "Bearer sk-test", "Authorization 头正确");
        var body = JsonNode.Parse(received);
        Check(body["model"].GetValue<string>() == "deepseek-v4-flash", "请求体 model 正确");
        Check(((JsonArray)body["tools"]).Count == schema.Count, "请求体包含全部工具");
        Check(content == "先浇水。", "解析回复内容");
        Check(calls.Count == 2, "解析出 2 个工具调用");
        var c2 = calls[1];
        Check((string)c2.GetType().GetField("Name").GetValue(c2) == "move_to", "第二个调用名 move_to");
        Check(((int)tReply.GetField("PromptTokens").GetValue(reply)) == 1234, "解析 token 用量");

        string err2 = null;
        object reply2 = null;
        try
        {
            var chat2 = (Task)tClient.GetMethod("Chat").Invoke(client, new object[] { messages, schema, CancellationToken.None });
            await chat2;
            reply2 = chat2.GetType().GetProperty("Result").GetValue(chat2);
        }
        catch (Exception ex)
        {
            err2 = ex.Message;
        }
        Check(err2 == null, "同一个 schema 连续第二次请求不报错" + (err2 != null ? "：" + err2 : ""));
        Check(reply2 != null && (string)tReply.GetField("Content").GetValue(reply2) == "", "content 为 null 时解析成空字符串");
        await server;
        listener.Stop();

        await TestMcp(asm, tMain, tTools, tActions, tConfig, notes);

        Console.WriteLine(failures == 0 ? "\n全部通过" : $"\n{failures} 项失败");
        return failures;
    }

    // ── MCP 服务器：真实启动 HttpListener，用 HTTP 发 JSON-RPC，和 WorkBuddy 等客户端一样 ──
    static async Task TestMcp(Assembly asm, Type tMain, Type tTools, Type tActions, Type tConfig, Func<string, string> notes)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var tMod = asm.GetType("BlindWhaleFarmer.ModEntry");
        var mod = Activator.CreateInstance(tMod);
        var config = Activator.CreateInstance(tConfig);
        var main = Activator.CreateInstance(tMain, all, null, new object[] { null }, null);
        var actions = Activator.CreateInstance(tActions, all, null, new object[] { null, null }, null);
        var tools = Activator.CreateInstance(tTools, all, null, new object[] { main, actions, notes }, null);
        var agent = Activator.CreateInstance(asm.GetType("BlindWhaleFarmer.Agent"), all, null, new object[] { mod }, null);
        tMod.GetField("Config", all).SetValue(mod, config);
        tMod.GetField("MainThread", all).SetValue(mod, main);
        tMod.GetField("Tools", all).SetValue(mod, tools);
        tMod.GetField("Agent", all).SetValue(mod, agent);

        // ── 本次游玩时长（PlaySession）：玩满 1 个游戏日后拒绝游戏动作，界面类工具照常；读档重置 ──
        var tSession = asm.GetType("BlindWhaleFarmer.PlaySession");
        var limitCfg = Activator.CreateInstance(tConfig);
        tConfig.GetProperty("SessionDays").SetValue(limitCfg, 1);
        var session = Activator.CreateInstance(tSession, all, null, new object[] { limitCfg }, null);
        string Gate(string tool) => (string)tSession.GetMethod("Gate").Invoke(session, new object[] { tool });
        tSession.GetMethod("Touch").Invoke(session, null);
        Check(Gate("move_to") == null, "时长限制：第一天可以正常行动");
        tSession.GetMethod("OnDayStarted").Invoke(session, null);
        Check((Gate("move_to") ?? "").StartsWith("失败：本次游玩已经结束"), "时长限制：玩满 1 天后拒绝游戏动作：" + Shorten(Gate("move_to") ?? "null"));
        Check(Gate("dialogue") == null && Gate("close_menu") == null, "时长限制：结束后仍可处理界面");
        tSession.GetMethod("Reset").Invoke(session, null);
        Check(Gate("move_to") == null, "时长限制：读档后重新计");
        var noLimit = Activator.CreateInstance(tSession, all, null, new object[] { Activator.CreateInstance(tConfig) }, null);
        tSession.GetMethod("OnDayStarted").Invoke(noLimit, null);
        Check(tSession.GetMethod("Status").Invoke(noLimit, null) == null, "不设上限时不显示进度");
        tMod.GetField("Session", all).SetValue(mod, Activator.CreateInstance(tSession, all, null, new object[] { config }, null));

        // 加载仓库里真实的攻略文件（从测试输出目录往上找 BlindWhaleFarmer/guide），这样下面的大小检查量的是完整说明
        var repo = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !System.IO.Directory.Exists(System.IO.Path.Combine(repo.FullName, "BlindWhaleFarmer", "guide")))
            repo = repo.Parent;
        string guideLog = (string)asm.GetType("BlindWhaleFarmer.Guide").GetMethod("Load")
            .Invoke(null, new object[] { System.IO.Path.Combine(repo?.FullName ?? "", "BlindWhaleFarmer"), "" });
        Check(guideLog.StartsWith("已加载攻略"), "读取攻略文件：" + guideLog);

        const int port = 18799;
        var tServer = asm.GetType("BlindWhaleFarmer.McpServer");
        var server = Activator.CreateInstance(tServer, all, null, new object[] { mod, port }, null);
        tServer.GetMethod("Start").Invoke(server, null);

        // 模拟游戏主线程：不停 Tick（世界未就绪）
        var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    tMain.GetMethod("Tick").Invoke(main, new object[] { false });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("主线程模拟出错：" + (ex.InnerException ?? ex));
                }
                await Task.Delay(16);
            }
        });

        using var http = new System.Net.Http.HttpClient();
        string url = $"http://127.0.0.1:{port}/mcp";
        string lastContentType = null, lastRaw = null;
        async Task<(int code, JsonNode body)> Post(string json, string origin = null)
        {
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
            {
                Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Accept", "application/json, text/event-stream");
            if (origin != null)
                req.Headers.Add("Origin", origin);
            var resp = await http.SendAsync(req);
            string text = await resp.Content.ReadAsStringAsync();
            lastContentType = resp.Content.Headers.ContentType?.MediaType;
            lastRaw = text;
            // 工具调用用 SSE 回复：取 data: 行里的 JSON-RPC 消息
            if (lastContentType == "text/event-stream")
                text = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l.Substring(5).Trim()));
            return ((int)resp.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
        }

        var (c1, init) = await Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}");
        Check(c1 == 200 && init?["result"]?["protocolVersion"]?.GetValue<string>() == "2025-03-26", "MCP initialize 协商协议版本");
        Check(init?["result"]?["capabilities"]?["tools"] != null, "MCP 声明 tools 能力");
        string instructions = init?["result"]?["instructions"]?.GetValue<string>() ?? "";
        Check(instructions.Contains("get_state") && instructions.Contains("行动原则"), "MCP instructions 带操作规则");
        Check(instructions.Contains("游戏常识") && instructions.Contains("第一年春季路线"), "MCP instructions 带攻略");

        var (c2, _) = await Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Check(c2 == 202, $"MCP 通知返回 202（实际 {c2}）");

        var (c3, list) = await Post("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"tools/list\"}");
        var toolArr = list?["result"]?["tools"] as JsonArray;
        Check(c3 == 200 && toolArr != null && toolArr.Count >= 27, $"MCP tools/list 返回 {toolArr?.Count} 个工具（含 get_state）");
        bool shapeOk = toolArr != null;
        foreach (var t in toolArr ?? new JsonArray())
            shapeOk &= t["name"] != null && t["inputSchema"]?["type"]?.GetValue<string>() == "object";
        Check(shapeOk, "MCP 工具都有 name 和 inputSchema");
        Check(list?["id"]?.GetValue<string>() == "a", "MCP 原样返回字符串 id");
        // DSH 的 mcp-client 默认只接受 32KB 以内的 instructions（含它加的标题），超了整个服务器连不上
        int instrBytes = System.Text.Encoding.UTF8.GetByteCount("### MCP server: stardew\n\n" + (init?["result"]?["instructions"]?.GetValue<string>() ?? ""));
        Check(instrBytes <= 32768, $"MCP instructions {instrBytes} 字节，在 DSH 默认上限 32768 以内");
        // 设置 MCP_DUMP=目录 时导出 initialize / tools/list，方便用客户端自己的校验器离线检查
        var dump = Environment.GetEnvironmentVariable("MCP_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dump, "mcp-initialize.json"), init?.ToJsonString() ?? "");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dump, "mcp-tools.json"), list?.ToJsonString() ?? "");
        }

        // 连续两次 tools/list：确认共用 Schema 不会触发“已有父节点”
        var (c3b, _) = await Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        Check(c3b == 200, "MCP 第二次 tools/list 正常");

        Check(toolArr?.Any(t => t["name"]?.GetValue<string>() == "get_guide") == true, "MCP tools/list 含 get_guide");
        var (cg, guide) = await Post("{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"get_guide\",\"arguments\":{}}}");
        string guideText = guide?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Check(cg == 200 && guideText.Contains("第一年春季路线") && guide?["result"]?["isError"]?.GetValue<bool>() == false, "MCP get_guide 返回攻略全文且不算出错");
        // 长动作超过 5 分钟时 Node 的 fetch 会断开：工具调用必须用 SSE 先发响应头、动作期间发心跳
        Check(lastContentType == "text/event-stream" && lastRaw.StartsWith(": start") && lastRaw.Contains("event: message"),
            "MCP 工具调用用 SSE 回复（先发响应头，结果是 message 事件）");
        var (cj, initAgain) = await Post("{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"ping\"}");
        Check(cj == 200 && lastContentType == "application/json", "MCP 非工具调用仍然用普通 JSON 回复");

        // ── /settings：DSH 插件推设置 ──
        var post = new System.Net.Http.StringContent(
            "{\"revision\":\"r1\",\"settings\":{\"sessionDays\":3,\"sessionMinutes\":45,\"freezeTime\":false,\"idleSeconds\":90,\"extraInstructions\":\"专心钓鱼\"}}",
            Encoding.UTF8, "application/json");
        var sp = await http.PostAsync($"http://127.0.0.1:{port}/settings", post);
        var spBody = JsonNode.Parse(await sp.Content.ReadAsStringAsync());
        Check((int)sp.StatusCode == 200 && spBody?["settings"]?["sessionDays"]?.GetValue<int>() == 3
              && spBody?["settings"]?["sessionMinutes"]?.GetValue<int>() == 45
              && spBody?["settings"]?["freezeTime"]?.GetValue<bool>() == false,
            "POST /settings 应用设置：" + Shorten(spBody?["message"]?.GetValue<string>() ?? ""));
        Check((int)tConfig.GetProperty("SessionMinutes").GetValue(config) == 45 && (string)tConfig.GetProperty("ExtraInstructions").GetValue(config) == "专心钓鱼",
            "设置写进了模组配置");
        var sg = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/settings"));
        Check(sg?["revision"]?.GetValue<string>() == "r1" && !string.IsNullOrEmpty(sg?["instance"]?.GetValue<string>()),
            "GET /settings 返回版本号和游戏实例标识（插件靠它判断要不要重推）");
        var badSettings = await http.PostAsync($"http://127.0.0.1:{port}/settings", new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json"));
        Check((int)badSettings.StatusCode == 400, "POST /settings 格式不对返回 400");

        var (c4, st) = await Post("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_state\",\"arguments\":{}}}");
        string stText = st?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        // 测试环境没有 SMAPI 运行时，读 Context 会抛异常；这里要求的是：不卡住、返回正常的 JSON-RPC 结果
        Check(c4 == 200 && (stText.Contains("存档") || stText.StartsWith("错误：TypeInitialization")),
            "MCP get_state 不会卡住，异常变成普通结果：" + Shorten(stText));

        var (c5, mv) = await Post("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"move_to\",\"arguments\":{\"x\":3,\"y\":4}}}");
        Check(c5 == 200 && mv?["result"]?["isError"]?.GetValue<bool>() == true, "MCP 动作失败时 isError=true：" + mv?["result"]?["content"]?[0]?["text"]);

        var (c6, bad) = await Post("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"no_such\",\"arguments\":{}}}");
        Check(bad?["error"]?["code"]?.GetValue<int>() == -32602, "MCP 未知工具返回 -32602");

        var (c7, nf) = await Post("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"foo/bar\"}");
        Check(nf?["error"]?["code"]?.GetValue<int>() == -32601, "MCP 未知方法返回 -32601");

        var (c8, pong) = await Post("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");
        Check(c8 == 200 && pong?["result"] != null, "MCP ping");

        var (c9, _) = await Post("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"ping\"}", origin: "http://evil.example.com");
        Check(c9 == 403, $"MCP 拒绝外部网页来源（{c9}）");

        var get = await http.GetAsync(url);
        Check((int)get.StatusCode == 405, "MCP GET 返回 405（不开 SSE）");

        bool active = (bool)tServer.GetMethod("IsActive").Invoke(server, new object[] { 120 });
        Check(active, "收到请求后判定 MCP 客户端在玩（用于冻结时间）");

        stop.Cancel();
        await pump;
        ((IDisposable)server).Dispose();
    }
}
