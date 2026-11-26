using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using StardewValley;
using StardewValley.Tools;

// 通过反射调用模组里的 internal 类型，在不启动游戏的情况下测试：工具 schema、参数解析、DeepSeek 客户端收发。
partial class Program
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
        TestTidyBackpack(tActions);
        TestInventoryDiff(asm);
        TestScreenHistory(asm);
        TestMineNodeScoring(tActions, schema);
        TestPrepareFishing(tActions, schema);
        TestPrepareField(tActions, schema);

        var machineParams = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "list_machines")?["function"]?["parameters"];
        Check(machineParams?["properties"] is JsonObject machineProps && machineProps.Count == 1
            && machineProps["item"]?["type"]?.GetValue<string>() == "string" && machineParams["required"] is JsonArray { Count: 0 },
            "list_machines 的 schema：只有一个可选的 item(string)");
        var readOnly = (HashSet<string>)tTools.GetField("ReadOnlyTools", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        Check(readOnly.Contains("list_machines"), "list_machines 注册为只读工具");
        TestMuseumInfo(asm, schema, readOnly);
        TestQuestLabels(tActions);
        TestVaultBundles(asm, tActions, schema);
        TestHayAndStorage(tActions, schema, readOnly);
        TestGiftInfoReverse(tActions, schema, readOnly);
        TestSveCompat(asm);

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

        // ── 参数别名、必填检查（M1）：参数没问题的会排进动作队列，世界未就绪时报“游戏世界未就绪”；参数有问题的在排队前就被拦下 ──
        async Task<string> Run(string name, string json)
        {
            var t = (Task<string>)exec.Invoke(tools, new object[] { name, json });
            for (int i = 0; i < 3 && !t.IsCompleted; i++)
                tMain.GetMethod("Tick").Invoke(main, new object[] { false });
            return await t;
        }
        bool ArgsAccepted(string r) => !r.Contains("不认识参数") && !r.Contains("缺少必填参数") && !r.Contains("缺少参数");
        string autoInteract = await Run("interact", "{\"item\":\"铜矿石\"}");
        Check(autoInteract.Contains("游戏世界未就绪"), "interact 只传 item 通过参数校验");
        string reverseGiftDefault = await Run("gift_info", "{}");
        Check(reverseGiftDefault.Contains("游戏世界未就绪"), "gift_info 不传 npc 通过参数校验");
        string reverseGiftChests = await Run("gift_info", "{\"chests\":true}");
        Check(reverseGiftChests.Contains("游戏世界未就绪"), "gift_info 只传 chests=true 通过参数校验");
        foreach (var (name, json) in new[]
        {
            ("craft", "{\"item\":\"Chest\"}"),
            ("craft", "{\"name\":\"箱子\",\"count\":2}"),
            ("plant_seeds", "{\"item\":\"防风草种子\"}"),
            ("plant_seeds", "{\"seed_name\":\"防风草种子\"}"),
            ("place_item", "{\"name\":\"Chest\",\"x\":60,\"y\":15}"),
            ("craft", "{\"recipe\":\"Chest\",\"item\":\"Scarecrow\"}"),
            ("till_area", "{\"width\":4,\"height\":3}"),
            ("till_area", "{\"near\":\"60,15\",\"width\":5}"),
            ("till_area", "{\"x1\":60,\"y1\":15,\"Width\":4}"),
        })
        {
            string r = await Run(name, json);
            Check(ArgsAccepted(r), $"别名参数被接受 {name}{json}：{Shorten(r)}");
        }
        string rMissing = await Run("craft", "{}");
        Check(rMissing.StartsWith("失败：craft 缺少必填参数 recipe") && rMissing.Contains("这个工具的参数有"), "不传必填参数时报清楚缺哪个：" + Shorten(rMissing));
        string rNull = await Run("place_item", "{\"name\":\"Chest\",\"x\":60}");
        Check(rNull.StartsWith("失败：place_item 的 x、y 要一起给，你只给了 x"), "place_item 只给 x 时报要成对给：" + Shorten(rNull));
        string rNoItem = await Run("place_item", "{\"x\":60,\"y\":15}");
        Check(rNoItem.StartsWith("失败：place_item 缺少必填参数 item"), "place_item 不给 item 照样报缺必填：" + Shorten(rNoItem));
        string rAutoPlace = await Run("place_item", "{\"item\":\"Chest\"}");
        Check(ArgsAccepted(rAutoPlace) && !rAutoPlace.Contains("要一起给"), "place_item 不给 x,y 时参数被接受（进游戏后自动找位置）：" + Shorten(rAutoPlace));
        TestCoopCartGiftDrop(asm, tActions, schema, Run);
        TestMachinesBooksGiftNaming(asm, tActions, schema, readOnly, Run);
        string rUnknown = await Run("craft", "{\"recipe\":\"Chest\",\"colour\":\"red\"}");
        Check(rUnknown.Contains("不认识参数 colour"), "真不认识的参数照样拒绝：" + Shorten(rUnknown));
        string rOtherTool = await Run("move_to", "{\"x\":1,\"y\":2,\"width\":3}");
        Check(rOtherTool.Contains("不认识参数 width"), "width 只对 till_area 有效：" + Shorten(rOtherTool));

        // till_area 的 width/height/near 在 schema 里，模型才看得到
        var tillProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "till_area")?["function"]["parameters"]["properties"] as JsonObject;
        Check(tillProps != null && tillProps["width"]?["type"]?.GetValue<string>() == "integer" && tillProps["height"]?["type"]?.GetValue<string>() == "integer"
              && tillProps["near"]?["type"]?.GetValue<string>() == "string", "till_area 的 schema 里有 width/height(integer)、near(string)");

        // 类型错误：传了但不是整数，报“要整数，你给的是 …”，不报“缺少参数”
        string rBadX = await Run("move_to", "{\"x\":\"abc\",\"y\":6}");
        Check(rBadX.StartsWith("失败：move_to 的参数 x 要整数，你给的是 abc") && !rBadX.Contains("缺少"), "x:\"abc\" 报类型错误：" + Shorten(rBadX));
        string rBadBool = await Run("use_tool", "{\"tool\":\"axe\",\"x\":true,\"y\":[1]}");
        Check(rBadBool.Contains("x 要整数，你给的是 true") && rBadBool.Contains("y 要整数，你给的是 [1]"), "两个类型错误一起报：" + Shorten(rBadBool));
        string rBadOpt = await Run("till_area", "{\"width\":\"宽一点\"}");
        Check(rBadOpt.Contains("width 要整数，你给的是 宽一点"), "可选参数类型错误也报：" + Shorten(rBadOpt));
        string rStrNum = await Run("move_to", "{\"x\":\"5\",\"y\":6.4}");
        Check(ArgsAccepted(rStrNum) && !rStrNum.Contains("要整数"), "\"5\" 和 6.4 照样当整数用：" + Shorten(rStrNum));

        // 开关参数：认不出的值报“要 true/false”，不静默当 false
        string rMaybe = await Run("travel_to", "{\"location\":\"Town\",\"detour\":\"maybe\"}");
        Check(rMaybe.StartsWith("失败：travel_to 的参数 detour 要 true/false，你给的是 maybe"), "detour:\"maybe\" 报开关类型错误：" + Shorten(rMaybe));
        string rTwo = await Run("travel_to", "{\"location\":\"Town\",\"detour\":2}");
        Check(rTwo.Contains("detour 要 true/false，你给的是 2"), "detour:2 报开关类型错误：" + Shorten(rTwo));
        string rMixed2 = await Run("place_item", "{\"item\":\"Chest\",\"x\":\"左边\",\"y\":1,\"force\":\"也许\"}");
        Check(rMixed2.Contains("x 要整数，你给的是 左边") && rMixed2.Contains("force 要 true/false，你给的是 也许"), "整数和开关类型错误一起报：" + Shorten(rMixed2));
        foreach (var ok in new[] { "true", "false", "\"true\"", "\"no\"", "\"是\"", "\"否\"", "1", "0", "\"on\"" })
        {
            string rb = await Run("travel_to", "{\"location\":\"Town\",\"detour\":" + ok + "}");
            Check(ArgsAccepted(rb) && !rb.Contains("true/false"), $"detour:{ok} 照样认：" + Shorten(rb));
        }
        // travel_to 带坐标（M3）：x、y 成对；location 和坐标至少一样；只给坐标按当前地点算
        string rTravelX = await Run("travel_to", "{\"location\":\"Town\",\"x\":10}");
        Check(rTravelX.StartsWith("失败：travel_to 的 x、y 要一起给，你只给了 x"), "travel_to 只给 x 报要一起给：" + Shorten(rTravelX));
        string rTravelY = await Run("travel_to", "{\"y\":10}");
        Check(rTravelY.StartsWith("失败：travel_to 的 x、y 要一起给，你只给了 y"), "travel_to 只给 y 报要一起给：" + Shorten(rTravelY));
        string rTravelNone = await Run("travel_to", "{}");
        Check(rTravelNone.StartsWith("失败：travel_to 要给 location") && !rTravelNone.Contains("缺少参数"), "travel_to 什么都不给报清楚：" + Shorten(rTravelNone));
        foreach (var json in new[] { "{\"location\":\"Town\",\"x\":10,\"y\":20}", "{\"x\":10,\"y\":20}", "{\"location\":\"Town\"}", "{\"location\":\"Town\",\"x\":\"10\",\"y\":20,\"detour\":true}" })
        {
            string rt = await Run("travel_to", json);
            Check(ArgsAccepted(rt) && !rt.Contains("要一起给") && !rt.Contains("要给 location"), $"travel_to{json} 参数被接受：" + Shorten(rt));
        }
        var travelProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "travel_to")?["function"]["parameters"];
        Check(travelProps?["properties"]?["x"]?["type"]?.GetValue<string>() == "integer" && travelProps?["properties"]?["y"] != null
              && !((JsonArray)travelProps["required"]).Any(r => r.GetValue<string>() == "location"), "travel_to 的 schema 有 x/y，location 不再必填");

        // build 支持 action=move：schema、成对 x/y、建筑坐标解析
        var buildProps = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "build")?["function"]["parameters"]?["properties"] as JsonObject;
        Check(buildProps != null && buildProps["action"]?["type"]?.GetValue<string>() == "string"
              && buildProps["building"]?["type"]?.GetValue<string>() == "string"
              && buildProps["x"]?["type"]?.GetValue<string>() == "integer" && buildProps["y"]?["type"]?.GetValue<string>() == "integer",
            "build 的 schema 有 action/building/x/y");
        string rBuildOnlyX = await Run("build", "{\"action\":\"move\",\"building\":\"Coop\",\"x\":40}");
        Check(rBuildOnlyX.StartsWith("失败：build 的 x、y 要一起给，你只给了 x"), "build 只给 x 报要一起给：" + Shorten(rBuildOnlyX));
        foreach (var json in new[] { "{\"action\":\"move\"}", "{\"action\":\"move\",\"building\":\"Coop\"}", "{\"action\":\"move\",\"building\":\"50,15\",\"x\":40,\"y\":20}" })
        {
            string rb = await Run("build", json);
            Check(ArgsAccepted(rb) && !rb.Contains("要一起给"), $"build{json} 参数被接受：" + Shorten(rb));
        }
        var mParseCoord = tActions.GetMethod("TryParseBuildingCoord", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Check(mParseCoord != null, "GameActions.TryParseBuildingCoord 存在");
        if (mParseCoord != null)
        {
            foreach (var (input, wantOk, wx, wy) in new[]
            {
                ("50,15", true, 50, 15),
                ("(50, 15)", true, 50, 15),
                ("50，15", true, 50, 15),
                ("Coop@50,15", true, 50, 15),
                ("鸡舍(50,15)", true, 50, 15),
                ("Coop", false, 0, 0),
                ("鸡舍", false, 0, 0),
                ("", false, 0, 0),
            })
            {
                var pArgs = new object[] { input, null, null };
                bool ok = (bool)mParseCoord.Invoke(null, pArgs);
                var pt = (Microsoft.Xna.Framework.Point)pArgs[1];
                Check(ok == wantOk && (!wantOk || (pt.X == wx && pt.Y == wy)), $"TryParseBuildingCoord(\"{input}\") => {ok} ({pt.X},{pt.Y})");
            }
        }

        // 结果拼接只砍真正的“成功：”“失败：”前缀（M3 修 Substring(3)）
        var strip = tActions.GetMethod("StripResultPrefix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        string Strip(string s) => (string)strip.Invoke(null, new object[] { s });
        Check(Strip("成功：已放下") == "已放下" && Strip("失败：放不了") == "放不了", "StripResultPrefix 砍掉成功/失败前缀");
        Check(Strip("对话已推进") == "对话已推进" && Strip("错误：崩了") == "错误：崩了" && Strip("") == "" && Strip(null) == "", "StripResultPrefix 不是成功/失败开头的原样返回");

        // say 聊天文字清理：开头的 / 去掉；模型吐坏的工具调用标记从最早出现的位置截断
        var clean = tActions.GetMethod("CleanChatText", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        string Clean(string s) => (string)clean.Invoke(null, new object[] { s });
        Check(Clean("你好呀</text> </｜DSML｜ invoke> </｜DSML｜ calls>") == "你好呀", "CleanChatText 截掉 </text> 和 DSML 标记");
        Check(Clean("/helpme 来啦 <invoke name=\"x\">") == "helpme 来啦" && Clean("好的</invoke>") == "好的" && Clean("a</parameter>b") == "a", "CleanChatText 去掉开头 / 并截掉 invoke/parameter");
        Check(Clean("正常的话") == "正常的话" && Clean("</text>") == "" && Clean(null) == "" && Clean("  <｜x ") == "", "CleanChatText 正常话原样，只有标记返回空");

        // 版本号（M3）：装进游戏要能和上游 v1.1.25 的包区分
        // 从测试程序所在目录往上找仓库根（有 BlindWhaleFarmer/manifest.json 的那一层）
        var repo = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !System.IO.File.Exists(System.IO.Path.Combine(repo.FullName, "BlindWhaleFarmer", "manifest.json")))
            repo = repo.Parent;
        string ReadVersion(string rel, string key) => repo == null ? null
            : JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(repo.FullName, rel)).TrimStart('﻿'))?[key]?.GetValue<string>();
        string manifestVersion = ReadVersion(System.IO.Path.Combine("BlindWhaleFarmer", "manifest.json"), "Version");
        Check(manifestVersion != null && manifestVersion.Length > 0, "manifest.json 有版本号：" + manifestVersion);
        Check(ReadVersion(System.IO.Path.Combine("release", "BlindWhaleFarmer", "manifest.json"), "Version") == manifestVersion, "release/BlindWhaleFarmer/manifest.json 和 manifest.json 版本一致");
        Check(ReadVersion("package.json", "version") == manifestVersion, "package.json 和 manifest.json 版本一致（DSH 插件按版本号决定要不要更新模组）");

        // ── M11：地图认知与卡住诊断（离线拼地图，不进游戏） ──
        await TestMapInfo(asm, schema, Run);

        var parseBool = tTools.GetMethod("ParseBool", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Check((bool?)parseBool.Invoke(null, new object[] { JsonNode.Parse("\"no\"") }) == false && (bool?)parseBool.Invoke(null, new object[] { JsonNode.Parse("\"maybe\"") }) == null,
            "ParseBool：\"no\" 是 false，\"maybe\" 认不出");

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
        TestFruitTreeAndSprinkler(asm, tActions, schema);

        Console.WriteLine(failures == 0 ? "\n全部通过" : $"\n{failures} 项失败");
        return failures;
    }

    static void TestTidyBackpack(Type tActions)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var parse = tActions.GetMethod("TryParsePurposes", flags);
        (bool ok, List<string> purposes, string error) Parse(string spec)
        {
            object[] args = { spec, null, null };
            bool ok = (bool)parse.Invoke(null, args);
            return (ok, (List<string>)args[1], (string)args[2]);
        }

        var mixed = Parse(" Fish，去镇上、下矿 和 种地 fish ");
        Check(mixed.ok && mixed.purposes.SequenceEqual(new[] { "fish", "town", "mine", "farm" }), "整理背包识别逗号、顿号、空格和「和」，重复目的只算一次");
        var fishTown = Parse("fish,town");
        var purposeName = tActions.GetMethod("TripPurposeName", flags);
        Check(fishTown.ok && (string)purposeName.Invoke(null, new object[] { fishTown.purposes }) == "钓鱼+去镇上", "整理背包合并显示出门目的");
        var unspecified = Parse(" ");
        Check(unspecified.ok && unspecified.purposes.Count == 0, "整理背包没写目的时按通用规则");
        var unknown = Parse("fish,看电影,town");
        Check(!unknown.ok && unknown.error.Contains("不认识出门目的“看电影”")
            && new[] { "mine", "fish", "farm", "town" }.All(p => unknown.error.Contains(p)), "整理背包拒绝含未知段的目的并列出可选值");
        Check(!Parse("、,").ok, "整理背包拒绝只有分隔符的目的");

        // 选留哪些东西要建 Hoe、MeleeWeapon 等游戏对象，离线没有物品注册表（ItemId 一设就抛 KeyNotFoundException），只能进游戏测
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
        var defaultCfg = Activator.CreateInstance(tConfig);
        Check((int)tConfig.GetProperty("SessionDays").GetValue(defaultCfg) == 3
              && (int)tConfig.GetProperty("SessionMinutes").GetValue(defaultCfg) == 60,
            "默认游玩上限：3 个游戏日 / 60 分钟");
        var noLimitCfg = Activator.CreateInstance(tConfig);
        tConfig.GetProperty("SessionDays").SetValue(noLimitCfg, 0);
        tConfig.GetProperty("SessionMinutes").SetValue(noLimitCfg, 0);
        var noLimit = Activator.CreateInstance(tSession, all, null, new object[] { noLimitCfg }, null);
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

        // get_state 的 section：中英文都认，写错的名字明说
        var filter = tServer.GetMethod("FilterSections", BindingFlags.Static | BindingFlags.NonPublic);
        string fake = "笔记\n## 基本状态\n时间\n## 当前界面\n无\n## 位置\n农场\n## 背包（序号: 物品）\n0: 锄头\n## 穿戴（背包里有更好的鞋子…）\n鞋\n## 箱子（所有地点）\n箱A\n## 本地出口（…）\n门\n## 任务\n任务A\n";
        string F(string s) => (string)filter.Invoke(null, new object[] { fake, s });
        foreach (var s in new[] { "背包", "inventory", "Inventory", "BAG", "物品栏" })
            Check(F(s).Contains("0: 锄头") && !F(s).Contains("箱A") && !F(s).Contains("鞋"), $"section={s} 只给背包（不带穿戴）");
        Check(F("箱子,quests").Contains("箱A") && F("箱子,quests").Contains("任务A") && !F("箱子,quests").Contains("锄头"), "section 中英文混写：箱子,quests");
        Check(F("背包、任务").Contains("0: 锄头") && F("背包、任务").Contains("任务A"), "section 用顿号分隔");
        Check(F("出口").Contains("门") && F("exits").Contains("门"), "section 出口/exits 都对上“本地出口”");
        string fBad = F("背包,钱包");
        Check(fBad.Contains("0: 锄头") && fBad.Contains("钱包 没有对应的段落"), "section 写错的名字明说：" + Shorten(fBad.Substring(fBad.LastIndexOf('（'))));
        Check(!F("背包").Contains("没有对应的段落"), "section 都对上时不报写错");
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
        // 专题攻略按需读，不在连接说明里
        var (ct, topic) = await Post("{\"jsonrpc\":\"2.0\",\"id\":23,\"method\":\"tools/call\",\"params\":{\"name\":\"get_guide\",\"arguments\":{\"topic\":\"钓鱼\"}}}");
        string topicText = topic?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Check(ct == 200 && topicText.Contains("传说鱼") && !instructions.Contains("各水域能钓到的鱼"), "MCP get_guide topic=钓鱼 读专题，专题不占连接说明");
        // 长动作超过 5 分钟时 Node 的 fetch 会断开：工具调用必须用 SSE 先发响应头、动作期间发心跳
        Check(lastContentType == "text/event-stream" && lastRaw.StartsWith(": start") && lastRaw.Contains("event: message"),
            "MCP 工具调用用 SSE 回复（先发响应头，结果是 message 事件）");
        var (cj, initAgain) = await Post("{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"ping\"}");
        Check(cj == 200 && lastContentType == "application/json", "MCP 非工具调用仍然用普通 JSON 回复");

        // ── /settings：DSH 插件推设置 ──
        var post = new System.Net.Http.StringContent(
            "{\"revision\":\"r1\",\"settings\":{\"sessionDays\":3,\"sessionMinutes\":45,\"freezeTime\":false,\"idleSeconds\":90,\"extraInstructions\":\"专心钓鱼\",\"reservedItems\":\"草莓种子, 远古水果\"}}",
            Encoding.UTF8, "application/json");
        var sp = await http.PostAsync($"http://127.0.0.1:{port}/settings", post);
        var spBody = JsonNode.Parse(await sp.Content.ReadAsStringAsync());
        Check((int)sp.StatusCode == 200 && spBody?["settings"]?["sessionDays"]?.GetValue<int>() == 3
              && spBody?["settings"]?["sessionMinutes"]?.GetValue<int>() == 45
              && spBody?["settings"]?["freezeTime"]?.GetValue<bool>() == false,
            "POST /settings 应用设置：" + Shorten(spBody?["message"]?.GetValue<string>() ?? ""));
        Check((int)tConfig.GetProperty("SessionMinutes").GetValue(config) == 45 && (string)tConfig.GetProperty("ExtraInstructions").GetValue(config) == "专心钓鱼"
              && (string)tConfig.GetProperty("ReservedItems").GetValue(config) == "草莓种子, 远古水果",
            "设置写进了模组配置");
        var sg = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/settings"));
        Check(sg?["revision"]?.GetValue<string>() == "r1" && !string.IsNullOrEmpty(sg?["instance"]?.GetValue<string>()),
            "GET /settings 返回版本号和游戏实例标识（插件靠它判断要不要重推）");
        var badSettings = await http.PostAsync($"http://127.0.0.1:{port}/settings", new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json"));
        Check((int)badSettings.StatusCode == 400, "POST /settings 格式不对返回 400");

        // ── /thought：DSH 插件推 AI 想法到左上角 HUD ──
        var hudField = tMod.GetField("Hud", all).GetValue(mod);
        var thoughtField = hudField.GetType().GetField("thought", BindingFlags.Instance | BindingFlags.NonPublic);
        var tp = await http.PostAsync($"http://127.0.0.1:{port}/thought",
            new System.Net.Http.StringContent("{\"text\":\"第一行\\r\\n第二行\\t\\u0000\\u0007去浇水啦\"}", Encoding.UTF8, "application/json"));
        var tpBody = JsonNode.Parse(await tp.Content.ReadAsStringAsync());
        string hudThought = (string)thoughtField.GetValue(hudField);
        Check((int)tp.StatusCode == 200 && tpBody?["ok"]?.GetValue<bool>() == true && hudThought == "第一行 第二行 去浇水啦",
            "POST /thought 过滤控制字符、合成一行并写入 Hud.SetThought：" + hudThought);

        string longText = new string('鱼', 260);
        var tpLong = await http.PostAsync($"http://127.0.0.1:{port}/thought",
            new System.Net.Http.StringContent(new JsonObject { ["text"] = longText }.ToJsonString(), Encoding.UTF8, "application/json"));
        string hudLong = (string)thoughtField.GetValue(hudField);
        Check((int)tpLong.StatusCode == 200 && hudLong.Length == 200 && hudLong.EndsWith("…"),
            $"POST /thought 超长文字截断到 200 字（实际 {hudLong.Length}）");

        var tpBad = await http.PostAsync($"http://127.0.0.1:{port}/thought",
            new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json"));
        Check((int)tpBad.StatusCode == 400, "POST /thought 缺少 text 返回 400");

        var tgGet = await http.GetAsync($"http://127.0.0.1:{port}/thought");
        Check((int)tgGet.StatusCode == 405, "GET /thought 返回 405");

        var evilThought = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/thought")
        {
            Content = new System.Net.Http.StringContent("{\"text\":\"hi\"}", Encoding.UTF8, "application/json")
        };
        evilThought.Headers.Add("Origin", "http://evil.example.com");
        var tpEvil = await http.SendAsync(evilThought);
        Check((int)tpEvil.StatusCode == 403, $"POST /thought 拒绝外部网页来源（{(int)tpEvil.StatusCode}）");

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
