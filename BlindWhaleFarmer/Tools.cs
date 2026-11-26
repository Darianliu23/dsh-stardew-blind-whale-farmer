using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BlindWhaleFarmer
{
    /// <summary>暴露给大模型的工具：JSON Schema 定义 + 调用分发。</summary>
    internal sealed class Tools
    {
        private sealed record Param(string Name, string Type, string Desc, bool Required, string[] Choices, string Default);

        private sealed class Spec
        {
            public string Name;
            public string Desc;
            public Param[] Params;
            public int MaxSeconds;
            public Func<JsonObject, Func<IEnumerable<object>>> Make;
        }

        private readonly MainThread main;
        private readonly Dictionary<string, Spec> specs = new();

        /// <summary>处理界面/剧情的工具：剧情或界面打开时可以打断卡住的长动作（见 MainThread.RunAction）。</summary>
        private static readonly HashSet<string> InterfaceTools = new() { "dialogue", "skip_event", "close_menu", "menu_click", "leave_festival", "choose_profession", "set_name" };

        /// <summary>背包满了时附在结果后面的处理顺序：先存，后出货，别急着卖。</summary>
        private const string BagFullAdvice =
            "背包满了的处理顺序（①②③ 都要回农场，做完人就在 Farm，不在原来的地方了）：① 用 tidy_backpack 一键整理（低级鱼竿、多余武器、矿石、鱼等存进农场箱子），或者 store_items 存指定的东西；"
            + "② 没箱子就 craft Chest（50 木材）、place_item 放在农场再存；③ 今天就要换钱的作物放进出货箱 ship_items；"
            + "④ 不要为了腾地方马上 sell 掉，尤其是收集包材料和等钓鱼 5 级再卖更值钱的鱼；"
            + "在矿洞里回不去箱子时：先 eat 吃掉食物，再用 trash_item 扔最不值钱的（泥土、石头、杂物），别让矿石宝石留在地上";

        /// <summary>
        /// 结果里说了“背包满了”时补上处理顺序，但同一件事只说一遍：
        /// 动作自己已经给了处理办法（钓鱼、矿洞这类会写 tidy_backpack / trash_item）就不再补；存取箱子本身就在处理背包，也不补。
        /// bagFullNoted 为真时，调用方别再追加“🎒 背包只剩 N 格”的提醒（真机上钓鱼一次结果里三段说的是同一件事，600 多字）。
        /// </summary>
        internal static string WithBagFullAdvice(string name, string result, out bool bagFullNoted)
        {
            bagFullNoted = result.Contains("背包满了");
            if (!bagFullNoted || name is "store_items" or "take_items")
                return result;
            bool hasOwnAdvice = result.Contains("tidy_backpack") || result.Contains("trash_item");
            return hasOwnAdvice ? result : result + "\n" + BagFullAdvice;
        }

        /// <summary>有人说话也不从外面打断的工具：本身就在等人或者很短；钓鱼半路断了鱼竿还在水里，它自己在两竿之间停。</summary>
        private static readonly HashSet<string> NoChatInterrupt = new() { "say", "set_mode", "reserve_items", "wait_chat", "follow", "propose", "sleep", "festival", "fish", "get_state", "get_guide", "map_info" };

        /// <summary>会往背包里攒东西的工具：做完在外面背包快满时提醒一句“回农场会自动收纳”。</summary>
        private static readonly HashSet<string> LootTools = new() { "fish", "forage", "explore_mine", "fight", "move_to", "travel_to", "interact", "check_crab_pots", "collect_machines", "clear_area", "use_tool" };

        /// <summary>打怪类工具：本来就会挨打，不因掉血中断。</summary>
        private static readonly HashSet<string> CombatTools = new() { "fight", "explore_mine", "collect_walnuts" };
        private readonly Func<string, string> updateNotes;

        public JsonArray Schema { get; }

        public Tools(MainThread main, GameActions a, Func<string, string> updateNotes)
        {
            this.main = main;
            this.updateNotes = updateNotes;

            Add("move_to", "走到当前地点的某一格（自动寻路）。", 60,
                j => () => a.MoveTo(Int(j, "x"), Int(j, "y")),
                P("x", "integer", "目标 x"), P("y", "integer", "目标 y"));

            Add("travel_to", "自动走到另一个地点（会自动经过多个地图、开门；有马厩、马在附近又路远时会自己骑马，到了下马）。地点用英文内部名，如 Farm、FarmHouse、Town、SeedShop(皮埃尔杂货店)、Blacksmith(铁匠)、ScienceHouse(木匠)、Saloon、Beach、FishShop、Mountain、Forest、BusStop、Mine、JojaMart、ManorHouse(镇长家)、CommunityCenter、Desert(沙漠)、SkullCave(骷髅洞穴)。去沙漠会自动在巴士站买票坐巴士（500g，巴士修好后、帕姆在站台时才行），从沙漠回来也会自动坐巴士。"
                + "给了 x,y（格子坐标，和 get_state 里“你站在 (x,y)”同一套）就到了地点再走到那一格，一步到位；只给 x,y 不给 location 就在当前地点里走过去（等于 move_to）。location 可填“回家”，联机时按 AiHomeLocation 设置选择房子。", 300,
                j => () => a.TravelTo(OptStr(j, "location"), OptInt(j, "x"), OptInt(j, "y"), OptBool(j, "detour") == true),
                P("location", "string", "目标地点内部名（只给 x,y 时可省略=当前地点）", false),
                P("x", "integer", "到了以后再走到哪格 x（和 y 一起给，可省略）", false), P("y", "integer", "到了以后再走到哪格 y", false),
                P("detour", "boolean", "近路被挡住时绕路走。默认不绕，会先告诉你挡在哪、绕路怎么走，让你选开路还是绕路", false));

            Add("use_tool", "走到目标格旁边并对它使用工具。工具可写：axe/hoe/pickaxe/watering_can/scythe/weapon，或背包序号。可以砍树、敲石头、锄地、浇水、给水壶灌水（对水面使用水壶）、打怪。目标被清除后会自动停止。", 120,
                j => () => a.UseTool(Str(j, "tool"), Int(j, "x"), Int(j, "y"), OptInt(j, "times") ?? 1),
                P("tool", "string", "工具"), P("x", "integer", "目标 x"), P("y", "integer", "目标 y"),
                P("times", "integer", "最多使用几次（砍大树约需10次），默认1", false));

            Add("interact", "走到目标旁边并按“动作键”：和村民聊天、开门、捡采集品、收获作物、收取机器产物、调节长笛块/鼓块音高、读邮箱、看告示板。传 npc 名字可以自动追着村民走。传 item 可以送礼或投料；只给 item 会在当前地点和农场找空闲的普通机器投料。", 120,
                j => () => a.Interact(OptInt(j, "x"), OptInt(j, "y"), OptStr(j, "npc"), OptStr(j, "item"), OptInt(j, "times") ?? 1),
                P("x", "integer", "目标 x", false), P("y", "integer", "目标 y", false),
                P("npc", "string", "村民名字（英文或中文），与 x,y 二选一", false),
                P("item", "string", "手里拿的物品（名称或背包序号）；只传 item 自动找机器投料", false),
                P("times", "integer", "连续交互次数（调长笛块 1~25、鼓块 1~7 用，默认 1）", false));

            Add("place_item", "把背包里的物品放到某一格（种单颗种子、施肥、放置箱子/稻草人/机器等）。item 参数就是物品名，中英文都行，如 Chest / 宝箱，也可以写背包序号。x,y 可以不给：不给就在你身边 8 格内找离你最近、符合摆放规矩的一格放下，结果里会说放在了哪。农场上的机器、箱子建议集中放在一片（挨着已有的），不能放在田里、堵在建筑门口；洒水器、稻草人放田里。洒水器要放在没种作物的格子上（空耕地也行），普通款浇上下左右 4 格、优质 3x3、铱制 5x5；位置浇不到耕地或者有明显更好的位置会被拒绝，并告诉你最好的坐标，照着放就行。放箱子时也可以先用 storage_info 查储藏区，然后用 near=\"x,y\" 选定箱子群或新锚点，在它旁边挑一个合规空位。near 和直接放置的 x,y 二选一。拿大箱子/大石箱对着已有普通木箱/石箱的 x,y 放可原地升级（物品保留不用搬），也可另放空位。", 60,
                j => () => a.PlaceItem(Str(j, "item"), OptInt(j, "x"), OptInt(j, "y"), OptBool(j, "force") ?? false, OptStr(j, "near")),
                P("item", "string", "物品名称或背包序号"),
                P("x", "integer", "放在哪格 x（和 y 一起给；都不给就在你身边自动找一格）", false), P("y", "integer", "放在哪格 y", false),
                P("force", "boolean", "不符合摆放规则也照样放（一般不要用）", false),
                P("near", "string", "只放箱子用，写当前地图的箱子坐标或锚点 x,y，自动在它旁边放（始终检查空位和通路）", false));

            Add("till_area", "用锄头开垦矩形区域里所有能开垦的空地（单次最多80格，返回“开了 N 格，还差 M 格”）；不给坐标只给 count 时优先贴合已有洒水器覆盖范围，否则挨着最大的田自动扩。只能在农场、温室这类种得了庄稼的地方用。区域里的杂草、石头、树枝、树桩、小树苗会先清掉再锄；树和大树桩大石头不动，没开出来的会列出原因与下一步。"
                + "不同作物可分开开几块田（状态“农田”行有每块田位置、作物和扩田建议）。", 300,
                j => () =>
                {
                    var (x1, y1, x2, y2) = TillRect(j);
                    return a.TillArea(x1, y1, x2, y2, OptInt(j, "count"), OptBool(j, "force") ?? false, OptBool(j, "shared") ?? false);
                },
                P("x1", "integer", "左上 x", false), P("y1", "integer", "左上 y", false), P("x2", "integer", "右下 x", false), P("y2", "integer", "右下 y", false),
                P("count", "integer", "不给坐标时用：挨着最大的田自动往空地最多的一边扩大约这么多格（默认 20，最多 80）", false),
                P("width", "integer", "不想算右下角时用：区域宽几格（1~40）。和 x1,y1 一起给就从 (x1,y1) 往右展开；坐标都不给就以 near 为中心展开。只给 width 或 height 时是正方形", false),
                P("height", "integer", "区域高几格（1~40），用法同 width；width、height 都不给但给了 near 时是 5x5", false),
                P("near", "string", "以哪一格为中心展开 width x height 的区域，写 \"x,y\"（比如 \"60,15\"）；写 me 或者不写坐标就是你现在站的格子", false),
                P("shared", "boolean", "联机时你是农场工人：不给坐标默认挨着自己小屋门口 10 格内的田扩（门口没田就在门口开第一块），不碰主机的田；传 true 才扩全农场最大的田", false),
                P("force", "boolean", "在种不了庄稼的地方（农场、温室以外）也照样锄（一般不要用）", false));

            Add("prepare_field", "按同一份图清障、从中心向外锄地、安放设备、播种。先 preview 看覆盖和走道，再用同样参数 execute；中断或补齐物资后按 get_state 的编号 resume。选 none 手浇，选等级即使缺洒水器也留位；稻草人也可预留。棚架自动留路，普通作物可要求行间走道。例子见 get_guide topic=种田。", 600,
                j => () => a.PrepareField(OptStr(j,"mode") ?? "preview",OptInt(j,"plan_id"),
                    j["near"] == null ? null : NearPoint(j["near"]),OptInt(j,"x1"),OptInt(j,"y1"),OptInt(j,"x2"),OptInt(j,"y2"),
                    OptInt(j,"count"),OptStr(j,"seed"),OptStr(j,"sprinkler") ?? "none",OptBool(j,"scarecrow") ?? false,OptStr(j,"path") ?? "auto"),
                P("mode","string","preview 只看图，execute 施工，resume 按编号续跑",false,new[] { "preview","execute","resume" },"preview"),
                P("plan_id","integer","resume 用的编号",false), P("near","string","中心 x,y 或 me，和矩形二选一",false),
                P("x1","integer","矩形一角 x",false), P("y1","integer","矩形一角 y",false),
                P("x2","integer","另一角 x",false), P("y2","integer","另一角 y",false),
                P("count","integer","目标种植格 1～80；只给数量则挨已有田选区，农场工人按小屋边界",false),
                P("seed","string","一种作物的种子名或背包序号；不填只整地",false),
                P("sprinkler","string","none、basic、quality、iridium；自行选择留哪种喷头",false,new[] { "none","basic","quality","iridium" },"none"),
                P("scarecrow","boolean","是否为现有稻草人保护不到的作物补位置",false),
                P("path","string","auto 棚架自动留路，rows 普通作物也隔行留路",false,new[] { "auto","rows" },"auto"));

            Add("plant_seeds", "在空耕地上批量播种（或施肥）。seed 是种子物品名或背包序号。不给区域按同作物主田→空田→混种田→零散耕地的优先顺序顺延种（只排序不硬截断）；想指定哪块就传 x1,y1,x2,y2。返回剩余种子数、剩余空耕地和下次可用范围。", 300,
                j => () => a.PlantSeeds(Str(j, "seed"), OptInt(j, "x1"), OptInt(j, "y1"), OptInt(j, "x2"), OptInt(j, "y2"), OptInt(j, "max") ?? 200, OptBool(j, "shared") ?? false),
                P("seed", "string", "种子名称或背包序号"),
                P("x1", "integer", "区域左上 x", false), P("y1", "integer", "区域左上 y", false),
                P("x2", "integer", "区域右下 x", false), P("y2", "integer", "区域右下 y", false),
                P("max", "integer", "最多种几格（默认 200，可传更大）", false),
                P("shared", "boolean", "联机时你是农场工人：不给区域默认只种自己小屋门口 10 格内的田；传 true 才往全农场的田里种", false));

            Add("water_crops", "给本地点所有需要浇水的作物浇水（水壶空了会自动去最近的水源灌水）。", 400,
                j => () => a.WaterCrops(OptInt(j, "max") ?? 300),
                P("max", "integer", "最多浇几株", false));

            Add("harvest_crops", "收获本地点所有成熟的作物和挂果的果树（需要镰刀的作物会自动用镰刀；果树最多挂 3 个果子，当季晚收不烂但会占位置不再长）。", 300,
                j => () => a.HarvestCrops(OptInt(j, "max") ?? 300),
                P("max", "integer", "最多收几株", false));

            Add("clear_area", "清理矩形区域里的杂物：石头用镐、杂草和草用镰刀、树枝/树苗/树桩用斧头、远古斑点用锄头、枯死作物用镰刀（只清枯死不伤活作物）。默认不砍大树。一次最多60处（从最近开始）。不给坐标清周围（radius 默认 4）；all=true 清整张地图。", 400,
                j => () => a.ClearArea(OptInt(j, "x1"), OptInt(j, "y1"), OptInt(j, "x2"), OptInt(j, "y2"), OptInt(j, "radius"), OptBool(j, "include_trees") ?? false, OptBool(j, "all") ?? false),
                P("x1", "integer", "左上 x（四个坐标都不给就清周围）", false), P("y1", "integer", "左上 y", false), P("x2", "integer", "右下 x", false), P("y2", "integer", "右下 y", false),
                P("radius", "integer", "不给坐标时清周围几格（1~8），默认 4", false),
                P("include_trees", "boolean", "是否也砍大树（很耗体力）", false),
                P("all", "boolean", "清整张地图（忽略坐标和 radius），每次从最近的清 60 处", false));

            Add("forage", "采集：把当前地图上的野生采集物（地图上的 *，季节野菜、贝壳、蘑菇、海草……）从最近的开始挨个走过去采了。不传 radius 就采整张地图，传了只采身边几格。背包装不下的跳过。", 300,
                j => () => a.Forage(OptInt(j, "radius"), OptInt(j, "max") ?? 30),
                P("radius", "integer", "只采身边几格以内的（可省略=整张地图）", false),
                P("max", "integer", "最多采几样（1~60），默认 30", false));

            Add("ship_items","把物品放进农场的出货箱（每晚自动结算成钱）。不在农场会自动走回农场。任务、特别订单、收集包要交的东西会拒绝（背包里标了【留着】）。", 180,
                j => () => a.ShipItems(Str(j, "item"), OptInt(j, "count"), OptBool(j, "force") ?? false),
                P("item", "string", "物品名称或背包序号"), P("count", "integer", "数量，默认全部", false),
                P("force", "boolean", "要交的东西也照样出货（一般不要用）", false));

            Add("store_items", "把背包里的东西存进箱子，腾出背包（工具和武器只存写了全名的，比如“训练用鱼竿”；整理背包更推荐 tidy_backpack）。不传坐标就自动挑箱子：同样的东西放进已经有它的箱子（叠在一起），没有就放进装着同类东西的箱子；箱子按大类分工（矿石资源、作物、鱼和钓具、采集品、宝石矿物古物……），新种类先找装同一大类的箱子，再找空箱子，实在没地方才混放。", 120,
                j => () => a.StoreItems(OptStr(j, "items"), OptInt(j, "x"), OptInt(j, "y")),
                P("items", "string", "要存的物品：逗号分隔字符串（如\"铁矿石,铜矿石\"或\"铁矿石x5\"）、数组（[\"铁矿石\",\"铜矿石\"]）或{\"名字\":数量}；不传或\"all\"表示除工具外全部存", false),
                P("x", "integer", "箱子 x", false), P("y", "integer", "箱子 y", false));

            Add("festival", "参加节日：enter 走去节日地点并入场（节日开放时间内）；greet 和在场的村民挨个打招呼（节日上说话也加好感）；"
                + "start 自动玩这个节日的小游戏（找彩蛋、夏威夷宴会放汤、花舞节邀舞伴需传 partner、冬日星盛宴送神秘礼物、万灵节迷宫宝箱、冰钓比赛、展览会展示柜评比），再找刘易斯开始主活动并等它结束（结束后游戏把你送回农场）；leave 中途离开。节日当天先 get_guide 读“节日”专题。联机时只有主机能开始主活动：你是农场工人时 start 会先做好准备、说一声，等主机开始后自动参加（已经开始了就直接加入）；展览会的共用展示柜要问过主机再用 grange 摆。", 620,
                j => () => a.Festival(Str(j, "action"), OptStr(j, "partner")),
                P("action", "string", "enter / greet / start / leave / grange", true),
                P("partner", "string", "花舞节 start 时要邀请的舞伴名字（不传会列出可邀请的人；不邀传 none）", false));

            Add("tidy_backpack", "整理背包：每类工具只留最好的一把，用不上的东西存箱子，一个满了换下一个；这里没箱子会先回农场。出门前先想好今天要干的所有事，一起写进 purpose，只要有一件事用得上就留。今天会用到但规则不带的东西（比如去镇上也想带剑、要送的礼物）写进 keep。还会提醒这次该带却没带的东西。", 300,
                j => () => a.TidyBackpack(OptStr(j, "keep"), OptStr(j, "purpose")),
                P("keep", "string", "额外要留在身上的物品名，多个用逗号分隔（比如今天要送的礼物、要交的收集包材料）", false),
                P("purpose", "string", "今天出门要做的所有事：mine 下矿、fish 钓鱼、farm 种地、town 去镇上。多个用逗号分隔，例如 fish,town。不传就按通用规则整理", false));

            Add("list_chests", "列出所有箱子（农场、农舍、冰箱……）在哪、里面有什么；传 item 只找这样东西放在哪个箱子。不花游戏时间。找东西、出门前想想要带什么时用，别凭记忆。", 10,
                j => () => a.ListChests(OptStr(j, "item")),
                P("item", "string", "要找的物品名，多个用逗号分隔，可省略", false));

            Add("storage_info", "看玩家放的箱子在哪些地图聚成储藏区，每组给范围、箱子数、空格数及上下左右能放箱子的坐标。只读。可给地点筛选，或给 near=\"x,y\" 单查某个箱子群或新锚点。不填 location 时，near 用当前地图坐标；两者都不填时查所有箱子群。你来决定放在哪和往哪边扩。", 20,
                j => () => a.StorageInfo(OptStr(j, "location"), OptStr(j, "near")),
                P("location", "string", "地图内部名（可省略）", false),
                P("near", "string", "箱子坐标或自选锚点，写成 x,y（可省略）", false));

            Add("list_machines", "列出农场及室内机器的位置和状态，传 item 按机器名筛选。不走路，不花游戏时间。", 10,
                j => () => a.ListMachines(OptStr(j, "item")),
                P("item", "string", "机器名（中英文），可省略", false));

            Add("collect_machines", "一次收完指定地点（或当前地点）所有已做好的机器产物（含蘑菇箱、小桶、罐头瓶、熔炉、采集器等），不自动补料。", 240,
                j => () => a.CollectMachines(OptStr(j, "location"), OptStr(j, "item")),
                P("location", "string", "地点内部名（可省略=当前地点）", false),
                P("item", "string", "按机器名或产物名筛选（可省略）", false));

            Add("map_info", "查当前地图的出口（走过去就换地图）、桥（会断/要修的桥，写明修没修好）、门（店门、房门、建筑门），每条写名字、格子坐标、通向哪个地点。"
                + "坐标和 get_state 里“你站在 (x,y)”同一套。按离你远近排，每类最多 " + MapInfo.MaxEntriesPerSection + " 条，要全部传 all=true。"
                + "只读，不移动你、不花游戏时间。move_to 老走不到、想换地图走、找不到出口时用；要去别的地点直接 travel_to 就行，不用先查。", 10,
                j => () => a.MapInfoTool(OptStr(j, "section"), OptBool(j, "all") ?? false),
                P("section", "string", "只看一类：exits（出口）/ bridges（桥）/ doors（门），可以用逗号写几类；不填给全部", false),
                P("all", "boolean", "true 时每类全部列出（默认每类最多 " + MapInfo.MaxEntriesPerSection + " 条，离你近的先）", false));

            Add("take_items", "从箱子里取东西回背包。指定了物品时会在所有地点的箱子里找，箱子在别的地图就自己走过去（不用先 travel_to）；不传坐标优先用这张地图里有这样东西的箱子。结果会列出箱子里还有什么。", 300,
                j => () => a.TakeItems(OptStr(j, "items"), OptInt(j, "x"), OptInt(j, "y"), OptInt(j, "count")),
                P("items", "string", "要取的物品：逗号分隔字符串（如\"铁锭x5,煤炭x10\"）、数组（[\"铁锭\",\"煤炭\"]）或{\"名字\":数量}；不带数量整摞拿，\"all\"表示全部", false),
                P("count", "integer", "只取一样东西时可以用它写数量（items=干草 count=3 等于 items=干草x3）；items 里已经写了数量就以 items 为准", false),
                P("x", "integer", "箱子 x", false), P("y", "integer", "箱子 y", false));

            Add("trash_item", "把背包里的东西扔进垃圾桶，扔了就没了。只在背包满了、又不方便回去存箱子时用（比如在矿洞里），先扔最不值钱、最好补的（泥土、石头、杂物）；工具和任务物品扔不了；宝石、矿物、矿石和锭、古物、料理、工匠品、戒指和一个卖 50g 以上的东西会拒绝，并列出能扔的。格号会变（扔掉一格后新捡的东西会填进去），扔之前先 get_state 看清楚。", 20,
                j => () => a.TrashItem(Str(j, "item"), OptInt(j, "count"), OptBool(j, "force") ?? false),
                P("item", "string", "物品名称或背包序号"), P("count", "integer", "扔几个，不传扔掉这一格全部", false),
                P("force", "boolean", "要交的东西也照样扔（一般不要用）", false));

            Add("drop_item", "把背包里的东西丢到地上（不会销毁，别人走近可以捡起）。联机时传 to=玩家名 会自动走到对方身边丢在脚边交给对方，可附带 message 和 emote 一步送礼说话发表情；不传 to 就在原地丢。工具和任务物品不能丢。", 180,
                j => () => a.DropItem(Str(j, "item"), OptInt(j, "count"), OptStr(j, "to"), OptBool(j, "force") ?? false, OptStr(j, "message"), OptStr(j, "emote")),
                P("item", "string", "物品名称或背包序号"), P("count", "integer", "丢几个，不传丢掉这一格全部", false),
                P("to", "string", "联机时交给哪位在线玩家（玩家名，留空则在原地丢）", false),
                P("force", "boolean", "原地丢弃时保留物品也照样丢（一般不要用）", false),
                P("message", "string", "联机送给玩家后顺便在聊天里说一句话（可省略）", false),
                P("emote", "string", "联机送给玩家后顺便发一个表情，如 heart/happy（可省略）", false));

            Add("eat", "吃掉背包里的食物恢复体力和生命。任务、特别订单、收集包要交的东西（背包里标了【留着】）会拒绝，挑别的吃。", 30,
                j => () => a.Eat(Str(j, "item"), OptBool(j, "force") ?? false),
                P("item", "string", "食物名称或背包序号"),
                P("force", "boolean", "要交的东西也照样吃（一般不要用）", false));

            Add("use_item", "拿起背包里的东西使用（和手持右键一样，用掉一个）：读书（技能书加经验，能力书第一次读有永久加成）、读秘密纸条（直接返回纸条内容）、用图腾（传送图腾回农场/山上/海边/沙漠/姜岛、雨图腾让明天下雨、宝藏图腾）、吹马笛。吃东西用 eat；晶球、神秘盒找克林特用 crack_geodes。", 60,
                j => () => a.UseItem(Str(j, "item"), OptBool(j, "force") ?? false),
                P("item", "string", "物品名称或背包序号"),
                P("force", "boolean", "时间还早、走回农场来得及时，传送图腾：农场默认不让用（图腾留到深夜赶不回去时再用）；确实要现在用才传 true", false));

            Add("open_shop", "在商店所在地点打开商店界面（店主要在场、在营业时间）。不传 shop_id 就自动判断：SeedShop→杂货店，Blacksmith→铁匠，ScienceHouse→木匠，FishShop→鱼店，Saloon→酒吧，"
                + "Forest→旅行货车（周五、周日），Sewer→科罗布斯（卖星之果实等），QiNutRoom→齐钻商店，Mine→矮人，SandyHouse→桑迪，Desert→沙漠商人，Club→赌场，IslandNorth→岛上商人，VolcanoDungeon5→火山矮人。沙漠节时在 DesertFestival 直接用 open_shop 查当前能开的店，村民摊位用 DesertFestival_ 加摊主的英文内部名。", 20,
                j => () => a.OpenShop(OptStr(j, "shop_id")),
                P("shop_id", "string", "商店ID，可省略", false));

            Add("buy", "在已打开的商店里购买。自己会做且材料够的东西、已读过的能力书会先拦下；真要买传 force=true。", 20,
                j => () => a.Buy(Str(j, "item"), OptInt(j, "count") ?? 1, OptBool(j, "force") ?? false),
                P("item", "string", "商品名称或商店列表序号"), P("count", "integer", "数量", false),
                P("force", "boolean", "强制购买（如会做也要买、或买已读能力书送人）", false));

            Add("sell", "在已打开的商店里出售背包物品（立即得钱，价格和出货箱一样）。同名的东西不管分在几格（普通、银星、金星各一格）都会一起卖，便宜的品质先卖；"
                + "写了品质（“沙丁鱼 金星”）或背包序号就只卖那一档或那一格。标了【留着】的格子不会跟着卖掉。", 20,
                j => () => a.Sell(Str(j, "item"), OptBool(j, "all") == true ? null : OptInt(j, "count"), OptBool(j, "force") ?? false),
                P("item", "string", "物品名称或背包序号"), P("count", "integer", "最多卖几个，不写就是全部", false),
                P("all", "boolean", "true = 卖光所有同名的（不写 count 本来就是这样，写了 count 又想卖光时用）", false),
                P("force", "boolean", "收集包、任务要交的东西（标了【留着】）也照样卖（一般不要用）", false));

            Add("craft", "用已学会的配方制作物品（如 Chest 宝箱、Scarecrow 稻草人、Basic Fertilizer 肥料）。recipe 参数就是要做的物品名，中英文都行，如 Chest / 宝箱。制作机器时会提示农场和背包里已有几台。", 20,
                j => () => a.Craft(Str(j, "recipe"), OptInt(j, "count") ?? 1),
                P("recipe", "string", "配方名（中英文都可以）"), P("count", "integer", "数量", false));

            Add("dialogue", "推进当前对话框；如果是选择题，传 choice 选项序号。", 20,
                j => () => a.Dialogue(OptInt(j, "choice")),
                P("choice", "integer", "选项序号（选择题才需要）", false));

            Add("menu_click", "点击当前界面里的某个可点击元素：传序号 index，或者传名字 name（按“当前界面”列出的名字找，包含这个词就算）。", 20,
                j => () => a.MenuClick(OptInt(j, "index"), OptStr(j, "name")),
                P("index", "integer", "元素序号", false),
                P("name", "string", "元素名字（和 index 二选一）", false));

            Add("close_menu", "关闭当前界面/信件/商店，或确认提示。", 20,
                j => () => a.CloseMenu());

            Add("read_mail", "走到信箱把所有未读的信一次读完：返回每封信的内容，附带的物品、钱、配方、任务都自动收下。状态里说有未读信件时用它，不用 interact 信箱一封封开。", 120,
                j => () => a.ReadMail());

            Add("choose_profession", "升级时选择职业。", 20,
                j => () => a.ChooseProfession(Str(j, "side")),
                P("side", "string", "left 或 right"));

            Add("skip_event", "跳过当前可以跳过的剧情事件。结果会列出可确定的剩余脚本台词，可能有分支没走到。", 20,
                j => () => a.SkipEvent());

            Add("leave_festival", "离开正在参加的节日（复活节、花舞节等）。节日里时间停止、普通的移动/干活工具都不能用；离开后游戏会把你送回去（时间按游戏规则推进）。", 320,
                j => () => a.LeaveFestival());

            Add("prepare_fishing", "从背包挑最好的鱼竿、装现有鱼饵和钓具。要采购就走到威利鱼店（FishShop）的柜台，按实际在售商品和预算，先升级鱼竿，再补鱼饵，最后给有槽的竿买一个钓具。"
                + "联机共用钱包时会先在游戏聊天报出金额和商品；AllowSpendSharedMoney 默认关闭，玩家在聊天同意后才传 confirm=true 再调用。游戏聊天发不出去就暂缓采购。", 240,
                j => () => a.PrepareFishing(OptInt(j, "budget"), OptBool(j, "buy_rod") ?? true,
                    OptInt(j, "bait_count") ?? GameActions.DefaultFishingBaitCount, OptBool(j, "confirm") ?? false),
                P("budget", "integer", "最多花多少金币；省略用当前余额，0 只配现有装备", false),
                P("buy_rod", "boolean", "允许购买更好的鱼竿，默认 true", false),
                P("bait_count", "integer", "鱼饵目标总数（算上已有鱼饵），0～999，默认 30", false),
                P("confirm", "boolean", "玩家已在游戏聊天里同意这笔共用钱包采购才传 true，默认 false", false));

            Add("fish", "钓鱼：自动在附近找岸边、抛竿、等咬钩、提竿并完成钓鱼小游戏，连钓多次。需要背包里有鱼竿。每次抛竿约消耗 8 体力、几十分钟游戏时间。也可以指定站立坐标和朝向。装备不齐可以先用 prepare_fishing。", 900,
                j => () => a.Fish(OptInt(j, "casts") ?? 5, OptInt(j, "x"), OptInt(j, "y"), OptStr(j, "direction")),
                P("casts", "integer", "抛竿次数 1~20，默认 5", false),
                P("x", "integer", "站立的 x（可省略，自动找）", false), P("y", "integer", "站立的 y", false),
                P("direction", "string", "朝向 up/down/left/right（可省略）", false));

            Add("care_animals", "照顾所有动物：自动走进每个鸡舍/畜棚，捡蛋和羊毛等地上的产物、清空自动采集器、给食槽放干草（用筒仓里的干草）、摸每只动物、用挤奶桶/剪刀收奶和毛，最后处理农场上在外面的动物。每天做一次（早上最好）。door=open 会打开动物门让它们出去吃草（晴天、非冬季），door=close 关门（下雨/晚上）。干草不够时，有筒仓且农场有草就先 cut_hay，不够再去 Marnie 那里买。", 600,
                j => () => a.CareAnimals(OptStr(j, "door")),
                P("door", "string", "open/close，可省略（不动门）", false));

            Add("cut_hay", "到主农场逐格用镰刀割草并按筒仓实际净增报干草。默认补到筒仓满，每批最多挥刀 60 格，先割近处。可用 x1,y1,x2,y2 圈一块，不给就找主农场的草。留动物门三格内的牧草、约四分之一的草和单独一格的草。没筒仓就不割。", 400,
                j => () => a.CutHay(OptInt(j, "target"), OptInt(j, "max_tiles"), OptInt(j, "x1"), OptInt(j, "y1"), OptInt(j, "x2"), OptInt(j, "y2")),
                P("target", "integer", "想补到的干草总数，默认筒仓总容量", false),
                P("max_tiles", "integer", "本批最多挥刀的草格数，默认 60，上限 300", false),
                P("x1", "integer", "左上 x（四个坐标要一起给）", false), P("y1", "integer", "左上 y", false),
                P("x2", "integer", "右下 x", false), P("y2", "integer", "右下 y", false));

            Add("gift_info", "查送礼：传 npc 查这个人的喜好、生日、好感和本周次数；不传 npc 从背包反查今天还能送谁（只列最爱/喜欢，生日置顶，chests=true 可连箱子一起查）。不花时间。", 10,
                j => () => a.GiftInfo(OptStr(j, "npc"), OptBool(j, "chests") ?? false),
                P("npc", "string", "村民名（中英文）；省略则从背包反查今天送谁", false),
                P("chests", "boolean", "不传 npc 时是否把箱子里的物品也算进去，默认 false", false));

            Add("give_gift", "给村民送礼：村民不在当前地点会自动找过去。每人每天 1 次、每周 2 次（生日不限）；生日送最爱的礼物效果 8 倍。送之前最好用 gift_info 确认他喜欢；他讨厌、不喜欢的东西会拒送并列出背包里能换的（交任务的东西不拦）。送花束开始恋爱、送美人鱼吊坠求婚也用它，这两样不受次数限制。", 240,
                j => () => a.GiveGift(Str(j, "npc"), Str(j, "item"), OptBool(j, "force") ?? false),
                P("npc", "string", "村民名字"), P("item", "string", "礼物（物品名称或背包序号）"),
                P("force", "boolean", "他讨厌/不喜欢的东西也照样送（会掉好感，一般不要用）", false));

            Add("donate_museum", "去博物馆（ArchaeologyHouse，8:00-18:00）把背包里所有还没捐过的矿物/古物捐掉，并领取捐赠奖励（种子、配方、家具等）。", 240,
                j => () => a.DonateMuseum());

            Add("museum_info", "查看博物馆捐赠进度，按矿物、文物列出还缺或已捐的藏品。只读，不用去博物馆。", 10,
                j => () => Knowledge.MuseumInfo(OptStr(j, "section"), OptStr(j, "kind")),
                P("section", "string", "missing=还缺（默认），donated=已捐，all=两份都列", false, new[] { "missing", "donated", "all" }, "missing"),
                P("kind", "string", "minerals=矿物，artifacts=文物，省略则两类都列", false, new[] { "minerals", "artifacts" }));

            Add("bundle_info", "查看社区中心收集包：每个包还缺哪些材料、背包里有哪些能交。all=true 时也列出还没解锁的区域。", 10,
                j => () => a.BundleInfo(OptBool(j, "all") ?? false),
                P("all", "boolean", "是否包含未解锁区域", false));

            Add("donate_bundles", "去社区中心把背包里能交的材料交进收集包，完成的收集包会自动领奖励，整个房间完成时会播放修复过场。"
                + "金库（Vault）交钱收集包传 pay_vault=true（按从便宜到贵交钱，可用 budget 限制最多花多少；联机共用钱包默认需玩家同意后再传 confirm=true）。", 600,
                j => () => a.DonateBundles(OptBool(j, "pay_vault") ?? false, OptInt(j, "budget"), OptBool(j, "confirm") ?? false),
                P("pay_vault", "boolean", "是否给已解锁的金库（Vault）收集包交钱，默认 false", false),
                P("budget", "integer", "金库交钱最多花多少金币（可省略=按余额）", false),
                P("confirm", "boolean", "联机共用钱包时，玩家已同意交钱再传 true，默认 false", false));

            Add("cook", "在厨房做菜（农舍升级后才有厨房；不在家会自动回家）。背包和冰箱里的材料都能用。不传 recipe 或写错时会列出已学会的菜谱和材料、标出现在能做的。料理回体力回血多，也是好礼物。", 120,
                j => () => a.Cook(OptStr(j, "recipe"), OptInt(j, "count") ?? 1),
                P("recipe", "string", "菜谱名（中英文都可以），可省略", false), P("count", "integer", "做几份，默认 1", false));

            Add("check_crab_pots", "收当前地点（或指定地点）所有蟹笼里的东西，并给没鱼饵的蟹笼放鱼饵。蟹笼用 place_item 放在水面上（紧挨岸边）。", 240,
                j => () => a.CheckCrabPots(OptStr(j, "location")),
                P("location", "string", "蟹笼所在地点（可省略=当前地点）", false));

            Add("fight", "打附近的怪物：自动靠近、挥最好的武器，生命低时自动吃回血食物；没有食物会停下让你撤退。最长约 2 分钟。", 150,
                j => () => a.Fight(OptInt(j, "radius") ?? 12, OptInt(j, "min_health_percent") ?? 40),
                P("radius", "integer", "多远以内的怪（格），默认 12", false),
                P("min_health_percent", "integer", "生命低于百分之多少就吃东西，默认 40", false));

            Add("explore_mine", "自动下矿洞：打附近的怪、用镐敲石头找楼梯、下楼，下够 levels 层或体力/生命/时间不够时停下。在矿洞外会先去矿洞入口；start_level 可用电梯直达（5 的倍数且到过）。出发前带上武器、镐子和回血食物。where=skull 去沙漠的骷髅洞穴（要有骷髅钥匙，会自己坐巴士过去；没有电梯，每次从第 1 层开始；遇到竖井、血量充足就跳，一次连降几层）。背包里有炸弹时，碰到一片值得炸的石头（罩住 2 个以上矿石/宝石，或 8 块以上石头；体力不到三成时 3 块就炸）会自己炸，炸完把掉的东西捡回来；骷髅洞穴里留 2 颗对付木乃伊。goal 会调整顺手挖矿和炸点偏好，炸矿门槛按节点总分折算（约等于两个常见矿点）；descent 先找楼梯，按石头数量决定要不要炸。", 900,
                j => () => a.ExploreMine(OptInt(j, "levels") ?? 3, OptInt(j, "start_level"), OptStr(j, "where"), OptInt(j, "bombs"), OptStr(j, "goal")),
                P("levels", "integer", "这次往下走几层（1~20），默认 3", false),
                P("start_level", "integer", "从哪层开始（电梯层，5 的倍数且到过；不传就坐电梯到你到过的最深电梯层，没解锁电梯则从第 1 层；想从头挖传 1；骷髅洞穴没有电梯，总是从第 1 层开始）", false),
                P("where", "string", "mine=普通矿洞（默认），skull=骷髅洞穴（也认 skullcavern、skull cavern、desert、沙漠、骷髅洞），volcano=姜岛火山（往上走 0~9 层到火山口：自己敲石头、用水壶浇冷挡路的岩浆、踩机关开关开门；levels 最多 10；第 0 层入口和第 5 层能给水壶灌水）。人已经在沙漠/骷髅洞穴入口时默认 skull，在火山里或岛北时默认 volcano", false),
                P("bombs", "integer", "这次最多用几颗炸弹炸石头（默认 5，0=不用炸弹；炸木乃伊不算在内）", false),
                P("goal", "string", "balanced=均衡（默认）；descent=优先下楼，只挖顺路的高分矿；ore=找金属矿（铱>金>铁>铜）；coal=找煤矿和容易掉煤的石头；gems=找宝石和晶球", false));

            Add("use_bomb", "放炸弹：走到那格旁边放下，马上跑出爆炸范围，等它炸完再返回。不传坐标就自动挑：附近有倒地的木乃伊就炸它们（木乃伊倒地时只有炸弹能炸死），没有就按均衡模式的矿点评分和石头数量挑身边的炸点，炸完把掉出来的矿石宝石捡回来。爆炸范围是以炸弹为中心的正方形：樱桃炸弹半径 3、炸弹 5、超级炸弹 7；来不及跑出范围的位置不会放。fight 和 explore_mine 只剩倒地的木乃伊时也会自己用；explore_mine 碰到一片值得炸的石头也会自己炸。", 60,
                j => () => a.UseBomb(OptInt(j, "x"), OptInt(j, "y"), OptStr(j, "kind")),
                P("x", "integer", "炸弹放在哪格 x（可省略，自动挑）", false), P("y", "integer", "y", false),
                P("kind", "string", "cherry（樱桃炸弹）/ bomb（炸弹）/ mega（超级炸弹），可省略=自动挑（小炸弹优先，大炸弹罩住的矿多得多时才用）", false));

            Add("upgrade_tool", "找铁匠 Clint（Blacksmith）升级工具（花钱+矿锭，2 天后取，期间工具不能用），或者领取已经升级好的工具（tool 可省略）。建议在下雨天升级水壶。", 240,
                j => () => a.UpgradeTool(OptStr(j, "tool")),
                P("tool", "string", "要升级的工具：axe/pickaxe/hoe/watering_can；领取时可省略", false));

            Add("crack_geodes", "找铁匠 Clint（Blacksmith，9:00-16:00）砸晶球，每个 25g，返回开出了什么。会自己走过去，不用打开界面。", 240,
                j => () => a.CrackGeodes(OptStr(j, "item"), OptInt(j, "count")),
                P("item", "string", "只砸哪种（如 冰封晶球），可省略=全部", false),
                P("count", "integer", "最多砸几个，可省略=全部", false));

            Add("quest_board", "去镇上皮埃尔店门口的告示板查看今天的委托任务；accept=true 接受（2 天内完成，有报酬和好感）。", 180,
                j => () => a.QuestBoard(OptBool(j, "accept") ?? false),
                P("accept", "boolean", "是否接受", false));

            Add("special_orders", "去特别订单板：默认是刘易斯家门口的（第一年秋季 2 日开放），board=qi 是齐先生核桃房里的齐挑战（找到 100 个金核桃后开门，奖励齐钻）。不传 accept 列出左右两个订单的内容、目标、期限和报酬；accept=left 或 right 接下其中一个（每块板一周只能接一个，每周一刷新）。这周接过了就直接报正在做的订单进度。", 300,
                j => () => a.SpecialOrders(OptStr(j, "accept"), OptStr(j, "board")),
                P("accept", "string", "接哪个：left（左边）或 right（右边），也可以传订单名的一部分；不传只看", false),
                P("board", "string", "town（默认，刘易斯家门口）或 qi（齐先生的核桃房）", false));

            Add("special_order_dropbox", "有的特别订单不是交给人，而是把东西放进指定地点的投递箱（订单目标里会写“放进 xx 的投递箱”）。这个工具自己走过去，把背包里订单要的东西放进去并确认，交够数订单就完成。", 300,
                j => () => a.SpecialOrderDropBox(OptStr(j, "order")),
                P("order", "string", "哪个订单（名字的一部分），可省略=第一个背包里有东西能交的", false));

            Add("prize_machine", "奖券：先去订单板左边的小机器领完成特别订单攒下的奖券，再去刘易斯家（ManorHouse）的奖券机一张换一个奖品（奖品按固定顺序排，有种子、神秘盒、星之果茶、家具、技能书等）。look=true 只看接下来的奖品不换。", 300,
                j => () => a.PrizeMachine(OptInt(j, "count"), OptBool(j, "look") ?? false),
                P("count", "integer", "换几张，默认全部", false), P("look", "boolean", "只看接下来的奖品", false));

            Add("emote", "发表情：在大肥鱼头顶冒出气泡（happy 开心、heart 爱心、sad 难过、angry 生气、exclamation 感叹号、question 疑问、note 音符、sleep 睡觉、laugh 大笑、yes 点头、no 摇头、hi 打招呼……）。联机时别的玩家也看得到。不传 emote 列出全部。", 10,
                j => () => a.Emote(OptStr(j, "emote")),
                P("emote", "string", "表情名（英文或中文），可省略=列出全部", false));

            Add("furniture", "查看或整理当前屋里的家具：list（默认）列出当前屋里与背包里的家具、坐标、尺寸和可否搬动；pickup 拿起家具进背包；place 把背包里的家具摆到 (x,y)；move 把家具从 (x,y) 移到 (to_x,to_y)；rotate 旋转家具。严格走游戏自己的放置与权限检查（别人屋里的床只有屋主或配偶能动）。", 90,
                j => () => a.ManageFurniture(OptStr(j, "action"), OptStr(j, "name"), OptInt(j, "x"), OptInt(j, "y"), OptInt(j, "to_x"), OptInt(j, "to_y"), OptInt(j, "times")),
                P("action", "string", "list（默认）/ pickup / place / move / rotate", false),
                P("name", "string", "家具名（place 必填；pickup/move/rotate 可与 x,y 二选一或同传）", false),
                P("x", "integer", "家具所在格 x（place 时为目标摆放左上角 x）", false),
                P("y", "integer", "家具所在格 y（place 时为目标摆放左上角 y）", false),
                P("to_x", "integer", "move 时的目标左上角 x", false),
                P("to_y", "integer", "move 时的目标左上角 y", false),
                P("times", "integer", "旋转次数（1~3，rotate 默认 1，place/move 可选顺便旋转）", false));

            Add("perfection", "查完美度（通关目标）：总百分比和每一项（出货、方尖碑、黄金时钟、怪物讨伐、好感、技能、星之果实、烹饪、制作、钓鱼、金色核桃）还缺什么。不花游戏时间。", 10,
                j => () => a.Perfection(OptStr(j, "detail")),
                P("detail", "string", "想看某一项的完整清单：ship / friends / cooking / crafting / fish", false));

            Add("pan", "用淘盘淘矿：自己找现在哪个地点有淘盘点（河、湖边水面闪光的地方，社区中心鱼缸修好后才有），走过去淘一盘，出铜铁金矿、晶球，有时有宝石。背包里要有淘盘。", 300,
                j => () => a.PanOre());

            Add("monster_goals","查冒险家公会的怪物讨伐目标：每种怪打了多少、要多少、奖励领没领。不花游戏时间。", 10,
                j => () => a.MonsterGoals());

            Add("mastery","精通（五项技能都 10 级后）：继续攒经验涨精通等级，每级去森林的精通洞穴在一个技能的台子上领一份奖励（耕种=铱金镰刀等，每个技能只能领一次）。不传 skill 列出精通进度和各技能奖励；传 skill 就自己走进洞里领。", 300,
                j => () => a.Mastery(OptStr(j, "skill")),
                P("skill", "string", "farming/mining/fishing/foraging/combat（或 耕种/采矿/钓鱼/采集/战斗），可省略=只看", false));

            Add("mermaid_show", "夜市（冬季 15-17 日 17:00-24:30）：自己走进沙滩上的美人鱼船看表演（免费，一分多钟），演完按顺序敲贝壳拿珍珠（每个存档一次，拿过就只看表演）。", 600,
                j => () => a.MermaidShow(OptBool(j, "again") ?? false),
                P("again", "boolean", "珍珠已经拿过时还想再看一遍才传 true", false));

            Add("feed_parrot", "姜岛：给鹦鹉交金核桃解锁东西（第一只鹦鹉 1、乌龟让路 10、农舍 20、信箱 5、方尖碑 20、鹦鹉快递 10、挖掘场的桥 10、岛上商人 10、度假村 20、火山桥/捷径各 5）。不传 which 列出现在能交的；传名字就走过去交。", 300,
                j => () => a.FeedParrot(OptStr(j, "which")),
                P("which", "string", "交给哪只：中文名或英文（Hut、Turtle、House、House_Mailbox、Obelisk、ParrotPlatforms、Bridge、Trader、Resort、VolcanoBridge、VolcanoShortcutOut），可省略=只列出来", false));

            Add("collect_walnuts", "姜岛：在当前地图（或 where 指定的岛上地点）摇结着金核桃的灌木、用锄头挖地下埋着的金核桃，捡起来。晶石洞解谜用 crystal_cave；其他解谜见攻略“姜岛”。", 300,
                j => () => a.CollectWalnuts(OptStr(j, "where")),
                P("where", "string", "岛上地点：IslandSouth、IslandEast、IslandWest、IslandNorth、IslandSouthEast 等，可省略=当前地图", false));

            Add("crystal_cave", "姜岛晶石洞（IslandWestCave1，需先让乌龟让路进岛西）：自动去洞里启动狮子雕像并按每轮晶石闪光顺序完成全部 5 轮解谜，拿 3 颗金核桃。", 300,
                j => () => a.CrystalCave());

            Add("gem_shrine", "姜岛丛林宝石鸟祭坛（IslandShrine）：不传参数查看北(24,25)/南(24,28)/东(27,27)/西(21,27)四个基座放了什么及今天下雨天岛上出现的宝石鸟；传 north/south/east/west 把对应宝石（绿宝石/海蓝宝石/红宝石/紫水晶/黄水晶，或 take 取下）放上基座，放对全部 4 个拿 5 颗金核桃。", 300,
                j => () => a.GemShrine(OptStr(j, "north"), OptStr(j, "south"), OptStr(j, "east"), OptStr(j, "west")),
                P("north", "string", "北基座/岛北（IslandNorth）要放的宝石或 take", false),
                P("south", "string", "南基座/岛南（IslandSouth）要放的宝石或 take", false),
                P("east", "string", "东基座/岛东（IslandEast）要放的宝石或 take", false),
                P("west", "string", "西基座/岛西（IslandWest）要放的宝石或 take", false));

            Add("mermaid_puzzle", "姜岛雨天美人鱼谜题（IslandSouthEast 东南海滩，需先喂 20 核桃解锁度假村且姜岛下雨天、凑齐 5 个长笛块）：自动摆放/复用 5 个长笛块、按 2/11/9/5/7 档（音高 200/1100/900/500/700）调音并从左向右走过吹响，拿 5 颗金核桃。", 300,
                j => () => a.MermaidPuzzle());
            Add("darts_game", "姜岛海盗湾投飞镖小游戏（IslandSouthEastCave，偶数日晚上 20:00 后且姜岛不下雨）：自动去海盗湾找 (30,8) 海盗开局并精准投完 301 分归零，捡走金核桃（共 3 颗，需赢 3 局分别 20/15/10 镖内）；已在投飞镖界面里时直接接管投完。", 180,
                j => () => a.DartsGame());

            Add("repair_boat", "去威利鱼店后屋的船坞修去姜岛的船（社区中心修好或 Joja 路线走完后威利来信邀请才进得去）：把背包里够数的材料交上——硬木 200、铱锭 5、电池组 5，可以分几次交；全交齐的第二天船就能开，之后 travel_to IslandSouth 坐船去姜岛（船票 1000g，回来免费）。", 180,
                j => () => a.RepairBoat());

            Add("joja", "Joja 路线（不修社区中心、改花钱）：action=status 看会员和项目进度；join 去 JojaMart 找 Morris 办会员（5000g，社区中心收集包从此作废，要先看过社区中心开场剧情）；buy 用社区发展表买项目，project 传编号或名字：1 巴士 40000g、2 矿车 15000g、3 桥 25000g、4 温室 35000g、5 清理大石头 20000g，一天只能下一单、第二天生效；五个都买完、看完仓库剧情后 project=theater 买电影院 500000g。", 180,
                j => () => a.Joja(OptStr(j, "action"), OptStr(j, "project")),
                P("action", "string", "status（默认）/ join / buy", false),
                P("project", "string", "buy 时要买的项目：1-5、名字（巴士/矿车/桥/温室/清理大石头）或 theater", false));

            Add("field_office", "姜岛考察站（IslandFieldOffice，要先从北边洞里救出蜗牛教授）：action=donate（默认）把背包里还缺的化石全交给蜗牛教授，拼齐一组（骨架 6 块/蛇 3 块/蝙蝠/青蛙）给金核桃等奖励，会顺手领走；status 看还缺哪些化石；survey 回答墙上的调查（紫花 22、紫海星 18，一次答一个）。全部完成触发完结剧情。", 180,
                j => () => a.FieldOfficeAction(OptStr(j, "action")),
                P("action", "string", "donate（默认）/ status / survey", false));

            Add("grandpa", "爷爷的评价（第三年春 1 日早上在农场神龛点蜡烛）：action=status（默认）看现在能得几分、每项差什么；diamond 往神龛放一颗钻石，第二天早上重新评（没满 4 根时用）；statue 4 根蜡烛都亮后去神龛领完美雕像（放下后每天出铱矿石）。", 120,
                j => () => a.Grandpa(OptStr(j, "action")),
                P("action", "string", "status（默认）/ diamond / statue", false));

            Add("raccoon", "1.6 煤矿森林北边大树桩与浣熊家族任务（53,2 一带）：status（默认）查看大树桩状态或当前要交的 2 样物品（包括熏鱼/果酱/果干等）；fix 交 100 硬木修好树桩；donate 走去找浣熊交付所需物品并领奖；shop 打开浣熊太太商店。", 240,
                j => () => a.RaccoonAction(OptStr(j, "action")),
                P("action", "string", "status（默认）/ fix / donate / shop", false));

            Add("buy_animal", "去 Marnie 的牧场（AnimalShop）买动物，自动放进有空位的鸡舍/畜棚。不传 type 列能买的动物和价格。欢迎自己想可爱的食物名（如饭团、米饭、包子、馒头、大白饭）传 name。", 240,
                j => () => a.BuyAnimal(OptStr(j, "type"), OptStr(j, "name"), OptInt(j, "count") ?? 1),
                P("type", "string", "动物种类，如 Chicken、Cow、Goat、Duck、Pig（中英文都可以）", false),
                P("name", "string", "名字（欢迎自己起名，如食物名；可省略，会从食物名字库随机挑不重名的）", false), P("count", "integer", "买几只，默认 1", false));

            Add("build", "在农场建造、升级或移动建筑：action=build（默认）建造/升级（普通建筑找木匠 Robin，魔法建筑如方尖碑/黄金时钟/祝尼魔小屋找法师；不传 building 列图纸和价格，不传 x,y 自动规划位置）；action=move 找 Robin 免费当场移动已有建筑（不传 building 列农场建筑和坐标，不传 x,y 给几个可放空位供你选）。", 300,
                j => () => a.Build(OptStr(j, "building"), OptInt(j, "x"), OptInt(j, "y"), OptBool(j, "force") ?? false, OptStr(j, "action")),
                P("action", "string", "build（默认：建造/升级）/ move（移动已有建筑）", false),
                P("building", "string", "建筑名（中英文），move 时也可写坐标如 50,15（同类多个用坐标区分），可省略", false),
                P("x", "integer", "目标左上角 x（可省略）", false), P("y", "integer", "目标左上角 y（可省略）", false),
                P("force", "boolean", "建造时忽略规划规则（一般不要用）", false));

            Add("upgrade_house", "找 Robin 升级农舍：第一次 10000g+450木材，升级后有厨房（可以 cook）和冰箱。", 240,
                j => () => a.UpgradeHouse());

            Add("sleep", "回家上床睡觉，结束这一天（会自动走回农舍）。体力恢复，作物生长。必须在凌晨2点前睡觉，否则会晕倒并损失金钱。会等到第二天早上（结算界面确认完）才返回。18:00 前睡会被拦一下，提醒你一天还长（浇完水就睡等于白白浪费一天）；确实要睡传 force=true。联机时可传 with 指定跟某位玩家一起睡（或 random 随机挑在线玩家），也可传 location 指定房子；都不传时按 CoSleepDefault / AiHomeLocation / 自己的家。", 420,
                j => () => a.Sleep(OptBool(j, "force") ?? false, OptStr(j, "with"), OptStr(j, "location")),
                P("force", "boolean", "18:00 前想睡也要传 true（确认今天真的没别的事可做、或者想跳过今天）", false),
                P("with", "string", "联机时指定跟哪位玩家一起睡（玩家名，或 random 随机挑在线玩家）；省略则按默认", false),
                P("location", "string", "指定去哪栋房子睡（如 FarmHouse、IslandFarmHouse 或小屋唯一地图名）；省略则按默认", false));

            Add("wait", "原地等待（一次最多两小时，要等更久就分几次），比如等商店开门、等节日开始。要等到某个钟点就传 until（比如 18:00），按游戏里的真实时间算，已经过了就不等、直接告诉你现在几点；别用记忆里的时间自己算 minutes。节日当天不会把入场时间等过去。", 300,
                j => () => a.Wait(OptInt(j, "minutes"), OptStr(j, "until")),
                P("minutes", "integer", "等多少游戏分钟（10~120），和 until 二选一", false),
                P("until", "string", "等到几点，比如 18:00（凌晨写 25:00）", false));

            Add("say", "联机时在游戏聊天里说一句话，其他玩家能看到。只有联机时能用，而且玩家要在设置里打开“允许 AI 聊天”（默认开，玩家关掉后这个工具会直接拒绝）。", 10,
                j => () => a.Say(Str(j, "text")),
                P("text", "string", "要说的话（300 字以内）"));

            Add("set_name", "给孩子或者刚出生的动物起名（弹出起名界面时用）。", 20,
                j => () => a.SetName(Str(j, "name")),
                P("name", "string", "名字（1~12 个字）"));

            Add("set_mode", "切换联机模式：别的玩家让你换模式时用，会在聊天里说一声。solo 自己玩、command 主要听指令、hybrid 指令做完接着自己玩。别人没要求就不要自己换。只有联机时能用。", 10,
                j => () => a.SetMode(Str(j, "mode")),
                P("mode", "string", "solo、command 或 hybrid"));

            Add("reserve_items", "记下或去掉玩家要留着的东西（按存档保存）。玩家说“草莓种子留着明年春天种”“这个别卖”时加进去，说“可以用了”时去掉；清单里的东西不卖、不出货、不吃、不扔、不种、不送（交任务可以）。玩家说了留到什么时候就填 until 和 purpose：到那天自动放开，get_state 最前面会提醒你去用。add 和 remove 都不传就看现在的清单。玩家在设置里写的那部分你去不掉。", 10,
                j => () => a.ReserveItems(OptStr(j, "add"), OptStr(j, "remove"), OptStr(j, "until"), OptStr(j, "purpose")),
                P("add", "string", "要加的物品名，多个用逗号分隔（名字要和游戏里的一样，比如“草莓种子”）。已经在清单里的会按这次的 until/purpose 更新", false),
                P("remove", "string", "要去掉的物品名，多个用逗号分隔", false),
                P("until", "string", "留到哪天，到了自动放开：“第2年春季”“明年春天”“夏季15日”“10天后”；没写年份就是下一个这样的日子。不传 = 一直留着", false),
                P("purpose", "string", "玩家说留着干什么（比如“种在温室”“送给阿比盖尔”），到期提醒时会带上", false));

            Add("wait_chat","联机时等别人说话：有人说话就立刻返回，最多等 seconds 秒（5~300）。闲着等吩咐时用。", 320,
                j => () => a.WaitChat(OptInt(j, "seconds") ?? 60),
                P("seconds", "integer", "最多等几秒，默认 60", false));

            Add("follow", "联机时跟着别的玩家走，换地图也会跟过去，有人说话、出现剧情或界面就停下。只有联机时能用。", 620,
                j => () => a.Follow(OptInt(j, "seconds") ?? 120),
                P("seconds", "integer", "跟多少秒（10~600），默认 120", false));

            Add("propose", "联机时拿结婚戒指向别的玩家求婚：自动走到对方旁边，对方的窗口里会弹问题，等对方回答（最多 2 分钟）。背包里要有结婚戒指。先用 say 说一声。只有联机时能用。", 180,
                j => () => a.Propose());

            Schema = BuildSchema();
            Schema.Add(Fn("update_notes", "覆盖写入你的长期笔记（会在每一步提示给你）：写下当前目标、计划、重要发现（商店位置、NPC喜好、已开垦区域坐标等）。保持简洁，200字以内。",
                new[] { P("notes", "string", "完整的新笔记内容") }));
            Schema.Add(Fn("cancel_action", "中止正在执行的长动作（钓鱼、下矿、wait、走路……）。动作卡住、或者你改主意了想先做别的时用；被中止的动作可能只做了一半，之后先 get_state 看看情况。",
                Array.Empty<Param>()));
            Schema.Add(Fn("play_for", "玩家要你玩一段时间时先调用它记下时长：“玩半小时”“玩一个小时”传 minutes（现实分钟），“玩 4 个游戏日”“玩一周”“玩到季末”传 days（游戏日，今天算第 1 天）。"
                + "记下后每个动作结果后面会写还剩多少：没玩够就一件接一件地做，一天做完就 sleep、醒来接着玩，不要做完一件就停下来汇报或问玩家；玩够了会提醒你收尾汇报。玩家说“不玩了/先停下”时两项都传 0 取消。",
                new[] { P("minutes", "integer", "现实分钟数：半小时 30，一小时 60；不按现实时间就传 0", false), P("days", "integer", "游戏日数：玩 4 天传 4，一周 7；不按游戏日就传 0", false) }));
        }

        private static Param P(string name, string type, string desc, bool required = true, string[] choices = null, string defaultValue = null) =>
            new(name, type, desc, required, choices, defaultValue);

        private void Add(string name, string desc, int maxSeconds, Func<JsonObject, Func<IEnumerable<object>>> make, params Param[] ps)
        {
            specs[name] = new Spec { Name = name, Desc = desc, Params = ps, MaxSeconds = maxSeconds, Make = make };
        }

        private JsonArray BuildSchema()
        {
            var arr = new JsonArray();
            foreach (var s in specs.Values)
                arr.Add(Fn(s.Name, WithTimeLimit(s.Desc, s.MaxSeconds), s.Params));
            return arr;
        }

        /// <summary>
        /// 长动作在描述末尾写上最长跑多久（现实时间）：客户端常见 60 秒超时，超过 45 秒模组会先回一句“还在执行”，
        /// 做完的结果放进下一次 get_state 最前面。短动作（60 秒以内）不写，省 token。
        /// </summary>
        internal static string WithTimeLimit(string desc, int maxSeconds) =>
            maxSeconds <= 60 ? desc
            : desc + $"（最长约 {(maxSeconds >= 120 ? maxSeconds / 60 + " 分钟" : maxSeconds + " 秒")}现实时间；超过 45 秒会先回“还在执行”，做完的结果在下一次 get_state 最前面）";

        private static JsonObject Fn(string name, string desc, Param[] ps)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var p in ps)
            {
                var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Desc };
                if (p.Choices != null)
                    prop["enum"] = new JsonArray(p.Choices.Select(choice => (JsonNode)JsonValue.Create(choice)).ToArray());
                if (p.Default != null)
                    prop["default"] = p.Default;
                props[p.Name] = prop;
                if (p.Required)
                    required.Add(p.Name);
            }
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = required
                    }
                }
            };
        }

        public bool IsKnown(string name) => name is "update_notes" or "cancel_action" or "play_for" || specs.ContainsKey(name);

        /// <summary>只查信息、马上出结果的工具：内置 AI 在跑的时候 MCP 也能用（不占动作队列，不会打断它）。</summary>
        public static readonly HashSet<string> ReadOnlyTools = new() { "gift_info", "bundle_info", "list_chests", "list_machines", "map_info", "museum_info", "storage_info" };

        /// <summary>在主线程上直接跑一个只读工具，取它的第一个结果；不经过动作队列。</summary>
        public string Peek(string name, string arguments)
        {
            if (!ReadOnlyTools.Contains(name) || !specs.TryGetValue(name, out var spec))
                return $"失败：{name} 不是只读工具";
            try
            {
                var args = ParseArgs(arguments, out _);
                string argProblem = NormalizeArgs(spec, args);
                if (argProblem != null)
                    return "失败：" + argProblem;
                foreach (var step in spec.Make(args)())
                    if (step is ActionResult result)
                        return result.Message;
                    else
                        break;
                return "失败：这个查询现在出不了结果，等内置 AI 停下再试";
            }
            catch (Exception ex)
            {
                return "失败：" + ex.Message;
            }
        }

        /// <summary>本次游玩的时长限制（见 PlaySession）；为 null 时不限制。</summary>
        public PlaySession Session { get; set; }

        public async Task<string> Execute(string name, string arguments)
        {
            string inventoryNote = null;
            string result = await ExecuteCore(name, arguments, note => inventoryNote = note).ConfigureAwait(false);
            // 玩家说了“玩半小时”：每个动作后面写还剩几分钟，免得 AI 做完一件事就停下汇报
            if (Session != null && name is not ("play_for" or "update_notes" or "reserve_items") && Session.GoalReminder() is string goal)
                result += "\n" + goal;
            return inventoryNote == null ? result : result + "\n这次拿到了：" + inventoryNote;
        }

        private async Task<string> ExecuteCore(string name, string arguments, Action<string> reportInventory)
        {
            JsonObject args;
            string jsonFixed = null;
            try
            {
                args = ParseArgs(arguments, out jsonFixed);
            }
            catch (Exception ex)
            {
                return $"失败：参数不是合法 JSON，没能自动修好（{ex.Message}）。参数要写成 {{\"名字\": 值}}，字符串用双引号，数字不加引号，最后一项后面不加逗号";
            }

            // 中止当前动作：不排队（动作通道正被占着），也不受游玩时长限制
            if (name == "cancel_action")
                return await main.CancelForTool().ConfigureAwait(false);

            if (Session != null)
            {
                string denied = Session.Gate(name);
                if (denied != null)
                    return denied;
                Session.Touch();
            }

            if (name == "update_notes")
                return updateNotes(OptStr(args, "notes") ?? "");

            if (name == "play_for")
            {
                if (Session == null)
                    return "失败：这里没有游玩计时";
                int? minutes = OptInt(args, "minutes"), days = OptInt(args, "days");
                if (minutes == null && days == null)
                    return "失败：要传 minutes（现实分钟，半小时就是 30）或 days（游戏日，玩 4 天就是 4）；两项都传 0 是取消";
                return Session.SetGoal(minutes ?? 0, days ?? 0);
            }

            if (name is "list_machines" or "museum_info")
                return await main.Invoke(() => StardewModdingAPI.Context.IsWorldReady
                    ? Peek(name, arguments) : "失败：游戏世界未就绪").ConfigureAwait(false);

            if (!specs.TryGetValue(name, out var spec))
                return $"失败：没有叫 {name} 的工具";

            string argProblem = NormalizeArgs(spec, args);
            if (argProblem != null)
                return "失败：" + argProblem;

            Func<IEnumerable<object>> factory;
            try
            {
                factory = spec.Make(args);
            }
            catch (Exception ex)
            {
                return "失败：参数错误：" + ex.Message;
            }
            // 打怪类动作本来就会挨打；其他动作被怪打了要停下，让 AI 先清怪
            // 记下动作开始时在哪张地图：出货、整理背包、存东西这些动作会自己走去别的地方，做完人就不在原地了
            string locBefore = null;
            // 动作前的游戏时间、日期、体力：做完在结果后面写一句“用了多久、体力多少”（clear_area 从 8 点干到 9 点以前什么都不说）
            (int time, int day, float stamina)? before = null;
            try
            {
                if (StardewModdingAPI.Context.IsWorldReady)
                {
                    locBefore = await main.Invoke(() => StardewValley.Game1.currentLocation?.NameOrUniqueName).ConfigureAwait(false);
                    before = await main.Invoke(() => (StardewValley.Game1.timeOfDay, (int)StardewValley.Game1.stats.DaysPlayed, StardewValley.Game1.player.Stamina)).ConfigureAwait(false);
                }
            }
            catch
            {
                // 拿不到就不提醒
            }
            bool interruptOnDamage = !CombatTools.Contains(name) && !InterfaceTools.Contains(name);
            // 本来就在剧情里用的（对话、跳过、关界面）、本来就要进剧情的（节日、睡觉时的夜间事件和婚礼）以外，途中冒出剧情就停下
            bool stopOnScene = !InterfaceTools.Contains(name) && name is not ("festival" or "sleep");
            // 开田预览只读现场，跳过自动换装、领奖等动作后处理。
            bool fieldPreview = name == "prepare_field" && (OptStr(args, "mode") ?? "preview") == "preview";
            bool trackInventory = !ReadOnlyTools.Contains(name) && !fieldPreview;
            string equipped = null, claimed = null;
            Action afterAction = null;
            if (trackInventory && name != "sleep")
            {
                afterAction = () =>
                {
                    // 自动换装会把旧装备放回背包，所以和动作一起结算。
                    equipped = GameActions.AutoEquip();
                    // 已完成的任务要在日志里点一下才发金币，这里替 AI 领。
                    claimed = GameActions.ClaimQuestRewards();
                };
            }
            var completed = await main.RunAction(factory, spec.MaxSeconds, InterfaceTools.Contains(name), interruptOnDamage, name,
                !InterfaceTools.Contains(name) && !NoChatInterrupt.Contains(name), stopOnScene, trackInventory, afterAction).ConfigureAwait(false);
            reportInventory(completed.InventoryNote);
            string result = completed.Message;
            if (name == "prepare_field" && !fieldPreview && result.StartsWith("失败") && !result.Contains("plan_id=")
                && MainThread.Progress is string fieldStep && fieldStep.StartsWith("plan "))
                result += "\n开田停在：" + fieldStep;
            if (name == "sleep")
            {
                // 睡前顺手收纳了战利品就说一声
                string slept = null;
                try { slept = StardewModdingAPI.Context.IsWorldReady ? await main.Invoke(GameActions.TakeStoredLootNote).ConfigureAwait(false) : null; } catch { }
                return slept == null ? result : result + "\n" + slept;
            }
            result = WithBagFullAdvice(name, result, out bool bagFullNoted);
            // 身边有怪、深夜、体力见底的提醒接在动作结果后面：MCP 客户端不一定每步都看 get_state
            string warning = null;
            try
            {
                // 世界没就绪时主线程可能不再 Tick，排队的 Invoke 会一直等，所以先判断
                if (StardewModdingAPI.Context.IsWorldReady)
                    warning = await main.Invoke(() =>
                    {
                        // 动作把人带到了别的地图（比如在海边 ship_items，会走回农场的出货箱）：明说现在在哪，免得 AI 以为还在原地
                        string locNow = StardewValley.Game1.currentLocation?.NameOrUniqueName;
                        string moved = locBefore != null && locNow != null && locNow != locBefore && name != "travel_to"
                            && StardewValley.Game1.currentLocation is not StardewValley.Locations.MineShaft
                            ? $"📍 这个动作把你从 {locBefore} 带到了 {locNow}，你现在在 {locNow}。接下来的动作都在这里进行；要回 {locBefore} 接着干就用 travel_to"
                            : null;
                        // 这次结果里有新的话就先不催，等下一步
                        string chat = GameActions.TakeChat();
                        string replyReminder = chat == null && name != "get_guide" ? GameActions.TakeReplyReminder() : null;
                        var parts = new[] { moved, equipped != null ? "🎽 " + equipped : null, claimed != null ? "💰 " + claimed : null, chat, replyReminder, GameActions.TakePickedOnTheWay(), GameActions.TakeStoredLootNote(), LootTools.Contains(name) && !bagFullNoted ? GameActions.LootReminder() : null, GameActions.ThreatWarning(), GameActions.BedtimeWarning(urgentOnly: true) }.Where(w => w != null).ToList();
                        return parts.Count > 0 ? string.Join("\n", parts) : null;
                    }).ConfigureAwait(false);
            }
            catch
            {
                // 世界未就绪等情况下不提醒
            }
            if (before is { } b && name != "sleep")
            {
                try
                {
                    var after = StardewModdingAPI.Context.IsWorldReady
                        ? await main.Invoke(() => (StardewValley.Game1.timeOfDay, (int)StardewValley.Game1.stats.DaysPlayed, StardewValley.Game1.player.Stamina)).ConfigureAwait(false)
                        : ((int, int, float)?)null;
                    if (after is { } a && CostNote(b.time, b.day, b.stamina, a.Item1, a.Item2, a.Item3) is string cost)
                        result += "\n" + cost;
                }
                catch
                {
                    // 读不到就不写
                }
            }
            if (jsonFixed != null)
                result += "\n" + jsonFixed;
            return warning == null ? result : result + "\n" + warning;
        }

        /// <summary>
        /// “⏱ 用时 1 小时 10 分（08:10 → 09:20），体力 192 → 126（-66）”。游戏时间没走、体力也没变时返回 null（查询类、界面类不吵）。
        /// 跨天（熬过 24 点、晕倒）只写时间点，不算分钟。时间不走（单机思考冻结、节日里）就只写体力。
        /// </summary>
        internal static string CostNote(int t0, int day0, float s0, int t1, int day1, float s1)
        {
            int ds = (int)s1 - (int)s0;
            bool timeMoved = t1 != t0 || day1 != day0;
            if (!timeMoved && ds == 0)
                return null;
            string time = null;
            if (timeMoved)
            {
                if (day1 == day0 && t1 > t0)
                {
                    int minutes = StardewValley.Utility.CalculateMinutesBetweenTimes(t0, t1);
                    string len = minutes >= 60 ? $"{minutes / 60} 小时" + (minutes % 60 > 0 ? $" {minutes % 60} 分" : "") : $"{minutes} 分钟";
                    time = $"用时 {len}（{Util.TimeString(t0)} → {Util.TimeString(t1)}）";
                }
                else
                    time = $"游戏时间 {Util.TimeString(t0)} → {(day1 != day0 ? "第二天 " : "")}{Util.TimeString(t1)}";
            }
            string stamina = ds != 0 ? $"体力 {(int)s0} → {(int)s1}（{(ds > 0 ? "+" : "")}{ds}）" : null;
            return "⏱ " + string.Join("，", new[] { time, stamina }.Where(x => x != null));
        }

        /// <summary>
        /// 解析工具参数。模型偶尔写出坏 JSON（数字后面多个引号 {"x":50"}、最后一项后面多逗号、单引号、键名没引号、少个右括号、包在 ```json 里），
        /// 直接报错的话它下一步多半原样再发一次。这里按顺序一步步修，每修一步试着解析一次；修好了 fixNote 说明一句，修不好抛出原来的错误。
        /// </summary>
        internal static JsonObject ParseArgs(string arguments, out string fixNote)
        {
            fixNote = null;
            if (string.IsNullOrWhiteSpace(arguments))
                return new JsonObject();
            JsonException original;
            try
            {
                return AsObject(JsonNode.Parse(arguments));
            }
            catch (JsonException ex)
            {
                original = ex;
            }

            string s = arguments.Trim();
            var steps = new List<(string what, Func<string, string> fix)>
            {
                ("去掉了代码块标记", t => System.Text.RegularExpressions.Regex.Replace(t, @"^```(?:json)?\s*|\s*```$", "")),
                ("去掉了数字后面多余的引号", t => System.Text.RegularExpressions.Regex.Replace(t, @"(:\s*-?\d+(?:\.\d+)?)""(?=\s*[,}\]])", "$1")),
                ("去掉了多余的逗号", t => System.Text.RegularExpressions.Regex.Replace(t, @",\s*([}\]])", "$1")),
                ("把单引号换成了双引号", t => t.Contains('"') ? t : t.Replace('\'', '"')),
                ("给键名加了引号", t => System.Text.RegularExpressions.Regex.Replace(t, @"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)\s*:", "$1\"$2\":")),
                ("补上了缺的右括号", t => t + new string('}', Math.Max(0, t.Count(c => c == '{') - t.Count(c => c == '}')))),
            };
            var done = new List<string>();
            foreach (var (what, fix) in steps)
            {
                string next = fix(s);
                if (next == s)
                    continue;
                s = next;
                done.Add(what);
                try
                {
                    var obj = AsObject(JsonNode.Parse(s));
                    fixNote = $"（参数 JSON 写错了，已自动修好：{string.Join("、", done)}。下次注意格式）";
                    return obj;
                }
                catch (JsonException)
                {
                    // 接着修下一处
                }
            }
            throw original;
        }

        /// <summary>参数应该是对象；有的模型会把整个对象再包成一个字符串，拆开它。</summary>
        private static JsonObject AsObject(JsonNode node)
        {
            if (node is JsonObject obj)
                return obj;
            if (node is JsonValue v && v.TryGetValue(out string inner) && inner.TrimStart().StartsWith("{"))
                return JsonNode.Parse(inner) as JsonObject ?? new JsonObject();
            return new JsonObject();
        }

        /// <summary>模型常顺手带上的说明性字段，不算参数，直接去掉。</summary>
        private static readonly HashSet<string> CommentKeys = new(StringComparer.OrdinalIgnoreCase) { "thought", "thoughts", "reason", "reasoning", "why", "note", "comment" };

        private static string CanonKey(string key) => new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static Dictionary<string, string> Aliases(params (string alias, string name)[] pairs) =>
            pairs.ToDictionary(p => CanonKey(p.alias), p => p.name);

        /// <summary>
        /// 模型凭直觉起的参数名 → 正式参数名，按工具分（比较时用 CanonKey，大小写、下划线不同也认）。
        /// 同一次调用里正式名已经传了，就以正式名为准，别名丢掉。
        /// </summary>
        /// <summary>村民名参数的各种写法（正式名是 npc）。</summary>
        private static readonly (string alias, string name)[] NpcPairs =
        {
            ("name", "npc"), ("villager", "npc"), ("character", "npc"), ("target", "npc"), ("person", "npc"), ("who", "npc"), ("npc_name", "npc"),
        };

        private static Dictionary<string, string> NpcAliases() => Aliases(NpcPairs);

        private static readonly Dictionary<string, Dictionary<string, string>> ParamAliases = new()
        {
            ["craft"] = Aliases(("item", "recipe"), ("name", "recipe"), ("item_name", "recipe")),
            ["plant_seeds"] = Aliases(("item", "seed"), ("seed_name", "seed"), ("seeds", "seed"), ("name", "seed")),
            ["place_item"] = Aliases(("name", "item"), ("item_name", "item")),
            ["get_state"] = Aliases(("part", "section"), ("which", "section"), ("sections", "section")),
            // 村民名：模型最常写 name、villager、character、target（WorkBuddy 真机：gift_info 写 name 被整段拒绝）
            ["gift_info"] = Aliases(NpcPairs.Concat(new[] { ("include_chests", "chests"), ("chest", "chests") }).ToArray()),
            ["give_gift"] = Aliases(NpcPairs.Concat(new[] { ("gift", "item"), ("present", "item"), ("item_name", "item") }).ToArray()),
            ["interact"] = Aliases(NpcPairs.Concat(new[] { ("item_name", "item") }).ToArray()),
            ["use_tool"] = Aliases(("tool_name", "tool"), ("times_to_use", "times"), ("count", "times")),
            ["eat"] = Aliases(("food", "item"), ("name", "item"), ("item_name", "item")),
            ["use_item"] = Aliases(("name", "item"), ("item_name", "item")),
            ["trash_item"] = Aliases(("name", "item"), ("item_name", "item"), ("quantity", "count")),
            ["drop_item"] = Aliases(("name", "item"), ("item_name", "item"), ("quantity", "count"), ("player", "to"), ("target", "to"), ("npc", "to")),
            ["ship_items"] = Aliases(("name", "item"), ("items", "item"), ("item_name", "item"), ("quantity", "count")),
            ["sell"] = Aliases(("name", "item"), ("item_name", "item"), ("quantity", "count")),
            ["buy"] = Aliases(("name", "item"), ("item_name", "item"), ("quantity", "count"), ("amount", "count")),
            ["cook"] = Aliases(("name", "recipe"), ("dish", "recipe"), ("item", "recipe"), ("quantity", "count")),
            ["open_shop"] = Aliases(("shop", "shop_id"), ("id", "shop_id"), ("name", "shop_id"), ("shopid", "shop_id")),
            ["store_items"] = Aliases(("item", "items"), ("name", "items")),
            ["take_items"] = Aliases(("item", "items"), ("name", "items"), ("quantity", "count")),
            ["list_chests"] = Aliases(("items", "item"), ("name", "item")),
            ["build"] = Aliases(("name", "building"), ("type", "building"), ("building_type", "building")),
            ["buy_animal"] = Aliases(("animal", "type"), ("kind", "type"), ("species", "type"), ("quantity", "count")),
            ["fish"] = Aliases(("times", "casts"), ("count", "casts"), ("number", "casts"), ("facing", "direction")),
            ["upgrade_tool"] = Aliases(("name", "tool"), ("tool_name", "tool")),
            ["crack_geodes"] = Aliases(("name", "item"), ("geode", "item")),
            ["wait"] = Aliases(("time", "until"), ("duration", "minutes"), ("min", "minutes")),
            ["travel_to"] = Aliases(("place", "location"), ("destination", "location"), ("map", "location"), ("to", "location")),
            ["check_crab_pots"] = Aliases(("place", "location"), ("map", "location")),
            ["collect_machines"] = Aliases(("place", "location"), ("map", "location"), ("machine", "item"), ("name", "item")),
            ["collect_walnuts"] = Aliases(("location", "where"), ("place", "where")),
            ["mastery"] = Aliases(("name", "skill")),
            ["set_name"] = Aliases(("text", "name")),
            ["say"] = Aliases(("message", "text"), ("msg", "text"), ("content", "text")),
            ["dialogue"] = Aliases(("option", "choice"), ("answer", "choice"), ("response", "choice")),
            ["menu_click"] = Aliases(("button", "name"), ("label", "name")),
            ["choose_profession"] = Aliases(("choice", "side"), ("option", "side")),
            ["festival"] = Aliases(("npc", "partner"), ("who", "partner"), ("name", "partner")),
            ["sleep"] = Aliases(("player", "with"), ("who", "with"), ("house", "location"), ("place", "location")),
            ["furniture"] = Aliases(("item", "name"), ("furniture", "name"), ("count", "times"), ("rotate", "times")),
            ["donate_bundles"] = Aliases(("vault", "pay_vault"), ("pay", "pay_vault"), ("max_spend", "budget")),
        };


        /// <summary>把别名换成正式名。known 里的键本来就认识，不动。</summary>
        private static void ApplyAliases(string tool, JsonObject args, HashSet<string> known)
        {
            if (!ParamAliases.TryGetValue(tool, out var table))
                return;
            foreach (var key in args.Select(kv => kv.Key).ToList())
            {
                if (known.Contains(key) || !table.TryGetValue(CanonKey(key), out var name))
                    continue;
                var value = args[key];
                args.Remove(key);
                if (!args.ContainsKey(name))
                    args[name] = value;
            }
        }

        /// <summary>get_state 这类在 McpServer 里直接处理、不在 specs 里的工具：只换别名，不校验。</summary>
        internal static void ApplyInfoAliases(string tool, JsonObject args)
        {
            if (args != null)
                ApplyAliases(tool, args, new HashSet<string>());
        }

        /// <summary>
        /// 参数名对不上时不能静默忽略（比如 startLevel 被当成没传，人就从第 1 层开始爬）：
        /// 先查别名表（craft 的 item → recipe 这类）；只是大小写、驼峰、下划线、连字符不同的，改成正确的名字；
        /// 真不认识的报错，并列出这个工具的参数。名字都对上了再查必填参数有没有传。
        /// </summary>
        private static string NormalizeArgs(Spec spec, JsonObject args)
        {
            var known = spec.Params.Select(p => p.Name).ToHashSet();
            ApplyAliases(spec.Name, args, known);
            var unknown = new List<string>();
            foreach (var key in args.Select(kv => kv.Key).ToList())
            {
                if (known.Contains(key))
                    continue;
                if (CommentKeys.Contains(key))
                {
                    args.Remove(key);
                    continue;
                }
                string match = known.FirstOrDefault(k => CanonKey(k) == CanonKey(key));
                if (match != null && !args.ContainsKey(match))
                {
                    var value = args[key];
                    args.Remove(key);
                    args[match] = value;
                    continue;
                }
                unknown.Add(key);
            }
            string list = spec.Params.Length == 0 ? "这个工具不用传参数"
                : "这个工具的参数有：" + string.Join("、", spec.Params.Select(p => p.Name + (p.Required ? "（必填）" : "")));
            if (unknown.Count > 0)
            {
                // 参数名猜错，其实是用错了工具：dialogue 只推进已经弹出来的对话框，想和村民说话要用 interact（真机 WorkBuddy 就这样）
                string wrongTool = spec.Name is "dialogue" or "menu_click" or "close_menu" or "skip_event" && unknown.Any(k => NpcPairs.Any(p => CanonKey(p.alias) == CanonKey(k)) || CanonKey(k) == "npc")
                    ? "。想和村民说话用 interact（传 npc=名字，会自己走过去、开口），说出对话框以后再用 dialogue 一句句推进"
                    : "";
                return $"{spec.Name} 不认识参数 {string.Join("、", unknown)}，所以什么都没做。{list}。改好参数名再调用一次{wrongTool}";
            }
            // 传了但类型不对（整数传 "abc"、true、数组；开关传 "maybe"、2）：说清楚是类型不对，别让它以为没传、也别静默当成 false
            var badTypes = spec.Params.Where(p => args[p.Name] is not null
                    && ((p.Type == "integer" && OptInt(args, p.Name) == null) || (p.Type == "boolean" && OptBool(args, p.Name) == null)))
                .Select(p => p.Type == "integer"
                    ? $"{p.Name} 要整数，你给的是 {ShowValue(args[p.Name])}"
                    : $"{p.Name} 要 true/false，你给的是 {ShowValue(args[p.Name])}")
                .ToList();
            if (badTypes.Count > 0)
                return $"{spec.Name} 的参数 {string.Join("；", badTypes)}，所以什么都没做。整数直接写数字（比如 5），开关写 true 或 false，都不加引号，改好再调用一次";
            var missing = spec.Params.Where(p => p.Required && args[p.Name] is null).ToList();
            if (missing.Count > 0)
                return $"{spec.Name} 缺少必填参数 {string.Join("、", missing.Select(p => $"{p.Name}（{p.Desc}）"))}，所以什么都没做。{list}。补上再调用一次";
            // 成对的可选坐标：只给一半多半是写漏了，猜不出另一半
            if (PairedKeys.TryGetValue(spec.Name, out var pair) && (args[pair.a] is null) != (args[pair.b] is null))
                return $"{spec.Name} 的 {pair.a}、{pair.b} 要一起给，你只给了 {(args[pair.a] is null ? pair.b : pair.a)}，所以什么都没做。{pair.note}";
            // travel_to：地点和坐标至少给一样
            if (spec.Name == "travel_to" && string.IsNullOrWhiteSpace(OptStr(args, "location")) && args["x"] is null)
                return $"travel_to 要给 location（去哪个地点）或者 x,y（当前地点里的哪一格），两样都没给，所以什么都没做。{list}";
            // map_info 的 section 只有三种：参数阶段就拦下，不用等进游戏
            if (spec.Name == "map_info" && MapInfo.ParseSections(OptStr(args, "section"), out string badSection) == null)
                return $"map_info 不认识 section “{badSection}”。能写 exits（出口）、bridges（桥）、doors（门），可以用逗号写几类，不填给全部";
            // store_items / take_items 的 items 支持字符串、数组、{名字: 数量} 对象
            if (spec.Name is "store_items" or "take_items" && args["items"] is JsonNode itemsNode)
            {
                if (!NormalizeItemsArg(itemsNode, out string normItems, out string itemsErr))
                    return $"{spec.Name} 的 {itemsErr}";
                if (normItems == null)
                    args.Remove("items");
                else
                    args["items"] = normItems;
            }
            return null;
        }

        private static string ItemsFormatHint(JsonNode node) =>
            $"items 格式不对（收到了 {ShowValue(node)}）：请传逗号分隔字符串（如 \"铁矿石, 铜矿石\" 或 \"铁矿石x5\"）、数组（[\"铁矿石\", \"铜矿石\"]）或对象（{{\"铁矿石\": 5, \"铜矿石\": 10}}），不传或 \"all\" 表示全部";

        private static bool TryPositiveInt(JsonNode n, out int value)
        {
            value = 0;
            if (n is not JsonValue v)
                return false;
            if (v.TryGetValue(out int i))
                value = i;
            else if (v.TryGetValue(out double d) && d == Math.Floor(d) && d >= 1 && d <= int.MaxValue)
                value = (int)d;
            else if (v.TryGetValue(out string s) && int.TryParse(s.Trim(), out int si))
                value = si;
            return value > 0;
        }

        private static bool ParseItemsObject(JsonObject obj, JsonNode rootNode, List<string> parts, out string error)
        {
            error = null;
            if (obj.Count == 0)
            {
                error = ItemsFormatHint(rootNode);
                return false;
            }
            // 兼容 {"name"/"item": "铁矿石", "count"/"quantity": 5} 单项对象写法
            var singleNameKey = obj.ContainsKey("item") ? "item" : obj.ContainsKey("name") ? "name" : null;
            if (singleNameKey != null && obj.All(kv => kv.Key is "item" or "name" or "count" or "quantity" or "amount")
                && obj[singleNameKey] is JsonValue nv && nv.TryGetValue(out string itemName) && !string.IsNullOrWhiteSpace(itemName))
            {
                var cntKey = obj.ContainsKey("count") ? "count" : obj.ContainsKey("quantity") ? "quantity" : obj.ContainsKey("amount") ? "amount" : null;
                if (cntKey == null || obj[cntKey] == null)
                {
                    parts.Add(itemName.Trim());
                    return true;
                }
                if (TryPositiveInt(obj[cntKey], out int cnt))
                {
                    parts.Add($"{itemName.Trim()}x{cnt}");
                    return true;
                }
                error = ItemsFormatHint(rootNode);
                return false;
            }
            foreach (var kv in obj)
            {
                string key = kv.Key?.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    error = ItemsFormatHint(rootNode);
                    return false;
                }
                if (kv.Value == null)
                {
                    parts.Add(key);
                    continue;
                }
                if (kv.Value is JsonValue sv && sv.TryGetValue(out string s)
                    && (s.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || s.Trim() is "全部" or ""))
                {
                    parts.Add(key);
                    continue;
                }
                if (TryPositiveInt(kv.Value, out int qty))
                {
                    parts.Add($"{key}x{qty}");
                    continue;
                }
                error = ItemsFormatHint(rootNode);
                return false;
            }
            return true;
        }

        /// <summary>将 store_items / take_items 的 items（字符串、数组、{名字: 数量} 或它们的 JSON 字符串）统一转成逗号分隔规格。</summary>
        internal static bool NormalizeItemsArg(JsonNode node, out string normalized, out string error)
        {
            normalized = null;
            error = null;
            if (node == null)
                return true;
            if (node is JsonValue v)
            {
                if (!v.TryGetValue(out string s))
                {
                    error = ItemsFormatHint(node);
                    return false;
                }
                string t = s.Trim();
                if (t.StartsWith('[') || t.StartsWith('{'))
                {
                    try
                    {
                        var parsed = JsonNode.Parse(t);
                        return NormalizeItemsArg(parsed, out normalized, out error);
                    }
                    catch
                    {
                        error = ItemsFormatHint(node);
                        return false;
                    }
                }
                normalized = s;
                return true;
            }
            var parts = new List<string>();
            if (node is JsonArray arr)
            {
                if (arr.Count == 0)
                {
                    error = ItemsFormatHint(node);
                    return false;
                }
                foreach (var el in arr)
                {
                    if (el is JsonValue ev && ev.TryGetValue(out string es) && !string.IsNullOrWhiteSpace(es))
                    {
                        string et = es.Trim();
                        if ((et.StartsWith('[') || et.StartsWith('{')) && NormalizeItemsArg(ev, out string sub, out error))
                        {
                            if (!string.IsNullOrWhiteSpace(sub))
                                parts.Add(sub);
                            continue;
                        }
                        if (error != null)
                            return false;
                        parts.Add(et);
                    }
                    else if (el is JsonObject eo)
                    {
                        if (!ParseItemsObject(eo, node, parts, out error))
                            return false;
                    }
                    else
                    {
                        error = ItemsFormatHint(node);
                        return false;
                    }
                }
                normalized = string.Join(",", parts);
                return true;
            }
            if (node is JsonObject obj)
            {
                if (!ParseItemsObject(obj, node, parts, out error))
                    return false;
                normalized = string.Join(",", parts);
                return true;
            }
            error = ItemsFormatHint(node);
            return false;
        }

        /// <summary>要么都给、要么都不给的一对参数，和都不给时会怎样。</summary>
        private static readonly Dictionary<string, (string a, string b, string note)> PairedKeys = new()
        {
            ["place_item"] = ("x", "y", "都不给就在你身边 8 格内自动找一格空位放"),
            ["travel_to"] = ("x", "y", "只是去那个地点就都不给；要到地点里的某一格就两个都给"),
            ["build"] = ("x", "y", "都不给时：建造会自动找位置，移动会列出几个可放空位供选择"),
        };

        /// <summary>
        /// till_area 的 width/height/near 换算成 x1,y1,x2,y2。在主线程上调用（要读玩家位置）。
        /// 四个坐标给全了就用坐标；给了 x1,y1 就从那里往右下展开；坐标一个都没给就以 near（默认玩家站的格子）为中心展开；
        /// 只给了一部分坐标的原样交给 TillArea，由它报“坐标要给全”。width/height 只给一个时是正方形，都没给是 5x5。
        /// </summary>
        private static (int? x1, int? y1, int? x2, int? y2) TillRect(JsonObject j)
        {
            int? x1 = OptInt(j, "x1"), y1 = OptInt(j, "y1"), x2 = OptInt(j, "x2"), y2 = OptInt(j, "y2");
            int? w = OptInt(j, "width"), h = OptInt(j, "height");
            bool anyCoord = x1.HasValue || y1.HasValue || x2.HasValue || y2.HasValue;
            if ((w == null && h == null && j["near"] == null) || (x1.HasValue && y1.HasValue && x2.HasValue && y2.HasValue))
                return (x1, y1, x2, y2);
            int width = Math.Clamp(w ?? h ?? 5, 1, 40), height = Math.Clamp(h ?? w ?? 5, 1, 40);
            if (x1.HasValue && y1.HasValue)
                return (x1, y1, x1 + width - 1, y1 + height - 1);
            if (anyCoord)
                return (x1, y1, x2, y2);
            var c = NearPoint(j["near"]);
            int left = c.X - (width - 1) / 2, top = c.Y - (height - 1) / 2;
            return (left, top, left + width - 1, top + height - 1);
        }

        /// <summary>near 写成 "x,y"、[x,y] 或 {"x":..,"y":..} 就用那一格；true、"me"、"player"、没写坐标的都当玩家站的格子。</summary>
        private static Microsoft.Xna.Framework.Point NearPoint(JsonNode near)
        {
            if (near is JsonObject o && OptInt(o, "x") is int ox && OptInt(o, "y") is int oy)
                return new Microsoft.Xna.Framework.Point(ox, oy);
            string s = near is JsonValue v && v.TryGetValue(out string str) ? str : near?.ToJsonString();
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"(-?\d+)\s*[,，\s]\s*(-?\d+)");
            if (m.Success)
                return new Microsoft.Xna.Framework.Point(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            return StardewValley.Game1.player.TilePoint;
        }

        /// <summary>报错时原样给出模型传的值：字符串不带引号，其他按 JSON 写，太长截断。</summary>
        internal static string ShowValue(JsonNode n)
        {
            string s = n is JsonValue v && v.TryGetValue(out string str) ? str : n.ToJsonString();
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }

        // ── 参数解析：模型有时会把数字写成字符串，这里都兼容 ──

        private static int Int(JsonObject j, string key) => OptInt(j, key) ?? throw new ArgumentException($"缺少参数 {key}");

        private static int? OptInt(JsonObject j, string key)
        {
            var n = j[key];
            if (n == null)
                return null;
            if (n is JsonValue v)
            {
                if (v.TryGetValue(out int i))
                    return i;
                if (v.TryGetValue(out double d))
                    return (int)Math.Round(d);
                if (v.TryGetValue(out string s) && int.TryParse(s.Trim(), out int si))
                    return si;
            }
            return null;
        }

        private static string Str(JsonObject j, string key) => OptStr(j, key) ?? throw new ArgumentException($"缺少参数 {key}");

        private static string OptStr(JsonObject j, string key)
        {
            var n = j[key];
            if (n == null)
                return null;
            if (n is JsonValue v && v.TryGetValue(out string s))
                return s;
            return n.ToJsonString().Trim('"');
        }

        private static bool? OptBool(JsonObject j, string key) => ParseBool(j[key]);

        /// <summary>
        /// 开关参数：true/false，数字 1/0，字符串 true/false、1/0、yes/no、on/off、是/否。
        /// 认不出来的（"maybe"、2、数组）返回 null，由调用方报“要 true/false”，不当成 false。
        /// </summary>
        internal static bool? ParseBool(JsonNode n)
        {
            if (n is not JsonValue v)
                return null;
            if (v.TryGetValue(out bool b))
                return b;
            if (v.TryGetValue(out double d))
                return d == 1 ? true : d == 0 ? false : null;
            if (v.TryGetValue(out string s))
            {
                string t = s.Trim().ToLowerInvariant();
                if (t is "true" or "1" or "yes" or "y" or "on" or "是" or "对")
                    return true;
                if (t is "false" or "0" or "no" or "n" or "off" or "否" or "不")
                    return false;
            }
            return null;
        }
    }
}
