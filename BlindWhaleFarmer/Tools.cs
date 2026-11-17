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
        private sealed record Param(string Name, string Type, string Desc, bool Required);

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

        /// <summary>有人说话也不从外面打断的工具：本身就在等人或者很短；钓鱼半路断了鱼竿还在水里，它自己在两竿之间停。</summary>
        private static readonly HashSet<string> NoChatInterrupt = new() { "say", "set_mode", "wait_chat", "follow", "propose", "sleep", "festival", "fish", "get_state", "get_guide" };

        /// <summary>会往背包里攒东西的工具：做完在外面背包快满时提醒一句“回农场会自动收纳”。</summary>
        private static readonly HashSet<string> LootTools = new() { "fish", "forage", "explore_mine", "fight", "move_to", "travel_to", "interact", "check_crab_pots", "clear_area", "use_tool" };

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

            Add("travel_to", "自动走到另一个地点（会自动经过多个地图、开门）。地点用英文内部名，如 Farm、FarmHouse、Town、SeedShop(皮埃尔杂货店)、Blacksmith(铁匠)、ScienceHouse(木匠)、Saloon、Beach、FishShop、Mountain、Forest、BusStop、Mine、JojaMart、ManorHouse(镇长家)、CommunityCenter、Desert(沙漠)、SkullCave(骷髅洞穴)。去沙漠会自动在巴士站买票坐巴士（500g，巴士修好后、帕姆在站台时才行），从沙漠回来也会自动坐巴士。", 300,
                j => () => a.TravelTo(Str(j, "location"), OptBool(j, "detour") == true),
                P("location", "string", "目标地点内部名"),
                P("detour", "boolean", "近路被挡住时绕路走。默认不绕，会先告诉你挡在哪、绕路怎么走，让你选开路还是绕路", false));

            Add("use_tool", "走到目标格旁边并对它使用工具。工具可写：axe/hoe/pickaxe/watering_can/scythe/weapon，或背包序号。可以砍树、敲石头、锄地、浇水、给水壶灌水（对水面使用水壶）、打怪。目标被清除后会自动停止。", 120,
                j => () => a.UseTool(Str(j, "tool"), Int(j, "x"), Int(j, "y"), OptInt(j, "times") ?? 1),
                P("tool", "string", "工具"), P("x", "integer", "目标 x"), P("y", "integer", "目标 y"),
                P("times", "integer", "最多使用几次（砍大树约需10次），默认1", false));

            Add("interact", "走到目标旁边并按“动作键”：和村民聊天、开门、捡采集品、收获作物、收取机器产物、读邮箱、看告示板。传 npc 名字可以自动追着村民走。传 item 则先拿着该物品再交互（用于送礼、往机器里放材料）。", 120,
                j => () => a.Interact(OptInt(j, "x"), OptInt(j, "y"), OptStr(j, "npc"), OptStr(j, "item")),
                P("x", "integer", "目标 x", false), P("y", "integer", "目标 y", false),
                P("npc", "string", "村民名字（英文或中文），与 x,y 二选一", false),
                P("item", "string", "交互时手里拿的物品（名称或背包序号），用于送礼/放材料", false));

            Add("place_item", "把背包里的物品放到某一格（种单颗种子、施肥、放置箱子/稻草人/机器等）。农场上的机器、箱子要集中放在一片（挨着已有的机器），不能放在田里、堵在建筑门口；洒水器、稻草人放田里。洒水器要放在没种作物的格子上（空耕地也行），普通款浇上下左右 4 格、优质 3x3、铱制 5x5；位置浇不到耕地或者有明显更好的位置会被拒绝，并告诉你最好的坐标，照着放就行。", 60,
                j => () => a.PlaceItem(Str(j, "item"), Int(j, "x"), Int(j, "y"), OptBool(j, "force") ?? false),
                P("item", "string", "物品名称或背包序号"), P("x", "integer", "x"), P("y", "integer", "y"),
                P("force", "boolean", "不符合摆放规则也照样放（一般不要用）", false));

            Add("till_area", "用锄头开垦一个矩形区域里所有能开垦的空地（最多80格）；不给坐标只给 count 就挨着最大的田自动扩。只能在农场、温室这类种得了庄稼的地方用。区域里的杂草、石头、树枝、树桩、小树苗会先自己清掉再锄；树和大树桩大石头不动，没开出来的格子结果里会列出来。"
                + "不同作物可以分开开几块田，每块田挑方便的地方开（状态“作物”一节的“农田”行有每块田的位置、种了什么和扩田建议）。", 300,
                j => () => a.TillArea(OptInt(j, "x1"), OptInt(j, "y1"), OptInt(j, "x2"), OptInt(j, "y2"), OptInt(j, "count"), OptBool(j, "force") ?? false),
                P("x1", "integer", "左上 x", false), P("y1", "integer", "左上 y", false), P("x2", "integer", "右下 x", false), P("y2", "integer", "右下 y", false),
                P("count", "integer", "不给坐标时用：挨着最大的田自动往空地最多的一边扩大约这么多格（默认 20，最多 80）", false),
                P("force", "boolean", "在种不了庄稼的地方（农场、温室以外）也照样锄（一般不要用）", false));

            Add("plant_seeds", "在空耕地上批量播种（或施肥）。不给区域时按田来种：先种已经种着同样作物的田，再种空着的田（新作物自己占一块），一行一行种，零散的一两格耕地不种；想种在哪块田就传那块田的区域。", 300,
                j => () => a.PlantSeeds(Str(j, "seed"), OptInt(j, "x1"), OptInt(j, "y1"), OptInt(j, "x2"), OptInt(j, "y2"), OptInt(j, "max") ?? 200),
                P("seed", "string", "种子名称或背包序号"),
                P("x1", "integer", "区域左上 x", false), P("y1", "integer", "区域左上 y", false),
                P("x2", "integer", "区域右下 x", false), P("y2", "integer", "区域右下 y", false),
                P("max", "integer", "最多种几格", false));

            Add("water_crops", "给本地点所有需要浇水的作物浇水（水壶空了会自动去最近的水源灌水）。", 400,
                j => () => a.WaterCrops(OptInt(j, "max") ?? 300),
                P("max", "integer", "最多浇几株", false));

            Add("harvest_crops", "收获本地点所有成熟的作物（需要镰刀的会自动用镰刀）。", 300,
                j => () => a.HarvestCrops(OptInt(j, "max") ?? 300),
                P("max", "integer", "最多收几株", false));

            Add("clear_area", "清理一个矩形区域里的杂物：石头用镐、杂草和草用镰刀、树枝/树苗/树桩用斧头、远古斑点用锄头。默认不砍大树。一次最多60处。不给坐标就清你周围（左右上下各 radius 格，默认 4）。", 400,
                j => () => a.ClearArea(OptInt(j, "x1"), OptInt(j, "y1"), OptInt(j, "x2"), OptInt(j, "y2"), OptInt(j, "radius"), OptBool(j, "include_trees") ?? false),
                P("x1", "integer", "左上 x（四个坐标都不给就清周围）", false), P("y1", "integer", "左上 y", false), P("x2", "integer", "右下 x", false), P("y2", "integer", "右下 y", false),
                P("radius", "integer", "不给坐标时清周围几格（1~8），默认 4", false),
                P("include_trees", "boolean", "是否也砍大树（很耗体力）", false));

            Add("forage", "采集：把当前地图上的野生采集物（地图上的 *，季节野菜、贝壳、蘑菇、海草……）从最近的开始挨个走过去采了。不传 radius 就采整张地图，传了只采身边几格。背包装不下的跳过。", 300,
                j => () => a.Forage(OptInt(j, "radius"), OptInt(j, "max") ?? 30),
                P("radius", "integer", "只采身边几格以内的（可省略=整张地图）", false),
                P("max", "integer", "最多采几样（1~60），默认 30", false));

            Add("ship_items","把物品放进农场的出货箱（每晚自动结算成钱）。不在农场会自动走回农场。任务、特别订单、收集包要交的东西会拒绝（背包里标了【留着】）。", 180,
                j => () => a.ShipItems(Str(j, "item"), OptInt(j, "count"), OptBool(j, "force") ?? false),
                P("item", "string", "物品名称或背包序号"), P("count", "integer", "数量，默认全部", false),
                P("force", "boolean", "要交的东西也照样出货（一般不要用）", false));

            Add("store_items", "把背包里的东西存进箱子，腾出背包（工具和武器只存写了全名的，比如“训练用鱼竿”；整理背包更推荐 tidy_backpack）。不传坐标就自动挑箱子：同样的东西放进已经有它的箱子（叠在一起），没有就放进装着同类东西的箱子，再没有才放最近的。", 120,
                j => () => a.StoreItems(OptStr(j, "items"), OptInt(j, "x"), OptInt(j, "y")),
                P("items", "string", "要存的物品名，多个用逗号分隔；不传或 all 表示除工具外全部存", false),
                P("x", "integer", "箱子 x", false), P("y", "integer", "箱子 y", false));

            Add("festival", "参加节日：enter 走去节日地点并入场（节日开放时间内）；greet 和在场的村民挨个打招呼（节日上说话也加好感）；"
                + "start 自动玩这个节日的小游戏（找彩蛋、夏威夷宴会放汤、花舞节邀舞伴、冬日星盛宴送神秘礼物、万灵节迷宫宝箱、冰钓比赛、展览会展示柜评比），再找刘易斯开始主活动并等它结束（结束后游戏把你送回农场）；leave 中途离开。节日当天先 get_guide 读“节日”专题。联机时只有主机能开始主活动：你是农场工人时 start 会先做好准备、说一声，等主机开始后自动参加（已经开始了就直接加入）；展览会的共用展示柜要问过主机再用 grange 摆。", 620,
                j => () => a.Festival(Str(j, "action")),
                P("action", "string", "enter / greet / start / leave / grange", true));

            Add("tidy_backpack", "整理背包：每类工具只留最好的一把（低级鱼竿、多余的武器等存起来），身上留吃的（料理；不够两样时再留回血最多的普通食物）、种子、肥料、鱼饵钓具、炸弹、任务物品、要摆放的机器，"
                + "其余（矿石、石头木材、宝石、鱼、作物、怪物掉落……）存进农场的箱子，一个满了换下一个。不在农场会先回农场。出门下矿/钓鱼前、背包快满时用。"
                + "传 purpose 时只留这次出门用得上的：mine 下矿（武器、镐子、炸弹、最多 5 样回血食物）、fish 钓鱼（鱼竿、鱼饵钓具、料理）、farm 种地（农具、种子肥料、洒水器机器）、town 去镇上（料理、花束）；还会提醒这次该带却在箱子里的东西。", 300,
                j => () => a.TidyBackpack(OptStr(j, "keep"), OptStr(j, "purpose")),
                P("keep", "string", "额外要留在身上的物品名，多个用逗号分隔（比如今天要送的礼物、要交的收集包材料）", false),
                P("purpose", "string", "出门干什么：mine / fish / farm / town，可省略", false));

            Add("list_chests", "列出所有箱子（农场、农舍、冰箱……）在哪、里面有什么；传 item 只找这样东西放在哪个箱子。不花游戏时间。找东西、出门前想想要带什么时用，别凭记忆。", 10,
                j => () => a.ListChests(OptStr(j, "item")),
                P("item", "string", "要找的物品名，多个用逗号分隔，可省略", false));

            Add("take_items", "从箱子里取东西回背包。结果会列出箱子里还有什么。", 120,
                j => () => a.TakeItems(OptStr(j, "items"), OptInt(j, "x"), OptInt(j, "y")),
                P("items", "string", "要取的物品名，多个用逗号分隔；all 表示全部", false),
                P("x", "integer", "箱子 x", false), P("y", "integer", "箱子 y", false));

            Add("trash_item", "把背包里的东西扔进垃圾桶，扔了就没了。只在背包满了、又不方便回去存箱子时用（比如在矿洞里），先扔最不值钱、最好补的（泥土、石头、杂物）；工具和任务物品扔不了。", 20,
                j => () => a.TrashItem(Str(j, "item"), OptInt(j, "count"), OptBool(j, "force") ?? false),
                P("item", "string", "物品名称或背包序号"), P("count", "integer", "扔几个，不传扔掉这一格全部", false),
                P("force", "boolean", "要交的东西也照样扔（一般不要用）", false));

            Add("eat", "吃掉背包里的食物恢复体力和生命。任务、特别订单、收集包要交的东西（背包里标了【留着】）会拒绝，挑别的吃。", 30,
                j => () => a.Eat(Str(j, "item"), OptBool(j, "force") ?? false),
                P("item", "string", "食物名称或背包序号"),
                P("force", "boolean", "要交的东西也照样吃（一般不要用）", false));

            Add("use_item", "拿起背包里的东西使用（和手持右键一样，用掉一个）：读书（技能书加经验，能力书第一次读有永久加成）、读秘密纸条（直接返回纸条内容）、用图腾（传送图腾回农场/山上/海边/沙漠/姜岛、雨图腾让明天下雨、宝藏图腾）、吹马笛。吃东西用 eat；晶球、神秘盒找克林特用 crack_geodes。", 60,
                j => () => a.UseItem(Str(j, "item")),
                P("item", "string", "物品名称或背包序号"));

            Add("open_shop", "在商店所在地点打开商店界面（店主要在场、在营业时间）。不传 shop_id 就自动判断：SeedShop→杂货店，Blacksmith→铁匠，ScienceHouse→木匠，FishShop→鱼店，Saloon→酒吧。", 20,
                j => () => a.OpenShop(OptStr(j, "shop_id")),
                P("shop_id", "string", "商店ID，可省略", false));

            Add("buy", "在已打开的商店里购买。", 20,
                j => () => a.Buy(Str(j, "item"), OptInt(j, "count") ?? 1),
                P("item", "string", "商品名称或商店列表序号"), P("count", "integer", "数量", false));

            Add("sell", "在已打开的商店里出售背包物品（立即得钱，价格和出货箱一样）。", 20,
                j => () => a.Sell(Str(j, "item"), OptInt(j, "count")),
                P("item", "string", "物品名称或背包序号"), P("count", "integer", "数量，默认全部", false));

            Add("craft", "用已学会的配方制作物品（如 Chest 箱子、Scarecrow 稻草人、Basic Fertilizer 肥料）。", 20,
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

            Add("skip_event", "跳过当前可以跳过的剧情事件。", 20,
                j => () => a.SkipEvent());

            Add("leave_festival", "离开正在参加的节日（复活节、花舞节等）。节日里时间停止、普通的移动/干活工具都不能用；离开后游戏会把你送回去（时间按游戏规则推进）。", 320,
                j => () => a.LeaveFestival());

            Add("fish", "钓鱼：自动在附近找岸边、抛竿、等咬钩、提竿并完成钓鱼小游戏，连钓多次。需要背包里有鱼竿。每次抛竿约消耗 8 体力、几十分钟游戏时间。也可以指定站立坐标和朝向。", 900,
                j => () => a.Fish(OptInt(j, "casts") ?? 5, OptInt(j, "x"), OptInt(j, "y"), OptStr(j, "direction")),
                P("casts", "integer", "抛竿次数 1~20，默认 5", false),
                P("x", "integer", "站立的 x（可省略，自动找）", false), P("y", "integer", "站立的 y", false),
                P("direction", "string", "朝向 up/down/left/right（可省略）", false));

            Add("care_animals", "照顾所有动物：自动走进每个鸡舍/畜棚，捡蛋和羊毛等地上的产物、清空自动采集器、给食槽放干草（用筒仓里的干草）、摸每只动物、用挤奶桶/剪刀收奶和毛，最后处理农场上在外面的动物。每天做一次（早上最好）。door=open 会打开动物门让它们出去吃草（晴天、非冬季），door=close 关门（下雨/晚上）。", 600,
                j => () => a.CareAnimals(OptStr(j, "door")),
                P("door", "string", "open/close，可省略（不动门）", false));

            Add("gift_info","查询村民的送礼喜好（最爱/喜欢/讨厌）、生日、好感和本周送礼次数，以及背包里哪些东西他喜欢。不花游戏时间。", 10,
                j => () => a.GiftInfo(Str(j, "npc")),
                P("npc", "string", "村民名字（英文或中文）"));

            Add("give_gift", "给村民送礼：村民不在当前地点会自动找过去。每人每天 1 次、每周 2 次（生日不限）；生日送最爱的礼物效果 8 倍。送之前最好用 gift_info 确认他喜欢。送花束开始恋爱、送美人鱼吊坠求婚也用它，这两样不受次数限制。", 240,
                j => () => a.GiveGift(Str(j, "npc"), Str(j, "item")),
                P("npc", "string", "村民名字"), P("item", "string", "礼物（物品名称或背包序号）"));

            Add("donate_museum", "去博物馆（ArchaeologyHouse，8:00-18:00）把背包里所有还没捐过的矿物/古物捐掉，并领取捐赠奖励（种子、配方、家具等）。", 240,
                j => () => a.DonateMuseum());

            Add("bundle_info", "查看社区中心收集包：每个包还缺哪些材料、背包里有哪些能交。all=true 时也列出还没解锁的区域。", 10,
                j => () => a.BundleInfo(OptBool(j, "all") ?? false),
                P("all", "boolean", "是否包含未解锁区域", false));

            Add("donate_bundles", "去社区中心把背包里能交的材料全部交进收集包，完成的收集包会自动领奖励，整个房间完成时会播放修复过场。", 600,
                j => () => a.DonateBundles());

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

            Add("explore_mine", "自动下矿洞：打附近的怪、用镐敲石头找楼梯、下楼，下够 levels 层或体力/生命/时间不够时停下。在矿洞外会先去矿洞入口；start_level 可用电梯直达（5 的倍数且到过）。出发前带上武器、镐子和回血食物。where=skull 去沙漠的骷髅洞穴（要有骷髅钥匙，会自己坐巴士过去；没有电梯，每次从第 1 层开始；遇到竖井、血量充足就跳，一次连降几层）。背包里有炸弹时，碰到一片值得炸的石头（罩住 2 个以上矿石/宝石，或 8 块以上石头；体力不到三成时 3 块就炸）会自己炸，炸完把掉的东西捡回来；骷髅洞穴里留 2 颗对付木乃伊。", 900,
                j => () => a.ExploreMine(OptInt(j, "levels") ?? 3, OptInt(j, "start_level"), OptStr(j, "where"), OptInt(j, "bombs")),
                P("levels", "integer", "这次往下走几层（1~20），默认 3", false),
                P("start_level", "integer", "从哪层开始（电梯层，可省略；骷髅洞穴没有电梯）", false),
                P("where", "string", "mine=普通矿洞（默认），skull=骷髅洞穴，volcano=姜岛火山（往上走 0~9 层到火山口：自己敲石头、用水壶浇冷挡路的岩浆、踩机关开关开门；levels 最多 10；第 0 层入口和第 5 层能给水壶灌水）。人已经在沙漠/骷髅洞穴入口时默认 skull，在火山里或岛北时默认 volcano", false),
                P("bombs", "integer", "这次最多用几颗炸弹炸石头（默认 5，0=不用炸弹；炸木乃伊不算在内）", false));

            Add("use_bomb", "放炸弹：走到那格旁边放下，马上跑出爆炸范围，等它炸完再返回。不传坐标就自动挑：附近有倒地的木乃伊就炸它们（木乃伊倒地时只有炸弹能炸死），没有就找身边矿石、宝石节点最多（其次石头最多）的地方，炸完把掉出来的矿石宝石捡回来。爆炸范围是以炸弹为中心的正方形：樱桃炸弹半径 3、炸弹 5、超级炸弹 7；来不及跑出范围的位置不会放。fight 和 explore_mine 只剩倒地的木乃伊时也会自己用；explore_mine 碰到一片值得炸的石头也会自己炸。", 60,
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

            Add("feed_parrot", "姜岛：给鹦鹉交金核桃解锁东西（第一只鹦鹉 1、乌龟让路 10、农舍 20、信箱 5、方尖碑 20、鹦鹉快递 10、挖掘场的桥 10、岛上商人 10、度假村 20、火山桥/捷径各 5）。不传 which 列出现在能交的；传名字就走过去交。", 300,
                j => () => a.FeedParrot(OptStr(j, "which")),
                P("which", "string", "交给哪只：中文名或英文（Hut、Turtle、House、House_Mailbox、Obelisk、ParrotPlatforms、Bridge、Trader、Resort、VolcanoBridge、VolcanoShortcutOut），可省略=只列出来", false));

            Add("collect_walnuts", "姜岛：在当前地图（或 where 指定的岛上地点）摇结着金核桃的灌木、用锄头挖地下埋着的金核桃，捡起来。解谜得的核桃（青蛙美食家、化石办公室、音乐石等）不在这里，见攻略“姜岛”。", 300,
                j => () => a.CollectWalnuts(OptStr(j, "where")),
                P("where", "string", "岛上地点：IslandSouth、IslandEast、IslandWest、IslandNorth、IslandSouthEast 等，可省略=当前地图", false));

            Add("repair_boat", "去威利鱼店后屋的船坞修去姜岛的船（社区中心修好或 Joja 路线走完后威利来信邀请才进得去）：把背包里够数的材料交上——硬木 200、铱锭 5、电池组 5，可以分几次交；全交齐的第二天船就能开，之后 travel_to IslandSouth 坐船去姜岛（船票 1000g，回来免费）。", 180,
                j => () => a.RepairBoat());

            Add("buy_animal", "去 Marnie 的牧场（AnimalShop）买动物，自动放进有空位的鸡舍/畜棚并起名。不传 type 会列出能买的动物和价格。", 240,
                j => () => a.BuyAnimal(OptStr(j, "type"), OptStr(j, "name"), OptInt(j, "count") ?? 1),
                P("type", "string", "动物种类，如 Chicken、Cow、Goat、Duck、Pig（中英文都可以）", false),
                P("name", "string", "名字（可省略，自动起名）", false), P("count", "integer", "买几只，默认 1", false));

            Add("build", "找木匠 Robin（ScienceHouse）在农场建造或升级建筑（鸡舍 Coop、畜棚 Barn、筒仓 Silo 等；Big Coop 等是升级）。不传 building 会列出能建的和价格。"
                + "推荐不传 x,y：会自动规划位置（同类建筑排成一排、四周留一格路、门口下方空两格、不贴着田）。自己给坐标时也按这些规则检查，不合适会拒绝并给出建议位置。", 300,
                j => () => a.Build(OptStr(j, "building"), OptInt(j, "x"), OptInt(j, "y"), OptBool(j, "force") ?? false),
                P("building", "string", "建筑名（中英文都可以），可省略", false),
                P("x", "integer", "建筑左上角 x（可省略）", false), P("y", "integer", "建筑左上角 y（可省略）", false),
                P("force", "boolean", "坐标不符合规划规则也照样建（一般不要用）", false));

            Add("upgrade_house", "找 Robin 升级农舍：第一次 10000g+450木材，升级后有厨房（可以 cook）和冰箱。", 240,
                j => () => a.UpgradeHouse());

            Add("sleep", "回家上床睡觉，结束这一天（会自动走回农舍）。体力恢复，作物生长。必须在凌晨2点前睡觉，否则会晕倒并损失金钱。会等到第二天早上（结算界面确认完）才返回。", 420,
                j => () => a.Sleep());

            Add("wait", "原地等待若干游戏分钟（10~120，一次最多两小时，要等更久就分几次），比如等商店开门。", 300,
                j => () => a.Wait(OptInt(j, "minutes") ?? 60),
                P("minutes", "integer", "游戏内分钟数"));

            Add("say", "联机时在游戏聊天里说一句话，其他玩家能看到。只有联机时能用。", 10,
                j => () => a.Say(Str(j, "text")),
                P("text", "string", "要说的话（300 字以内）"));

            Add("set_name", "给孩子或者刚出生的动物起名（弹出起名界面时用）。", 20,
                j => () => a.SetName(Str(j, "name")),
                P("name", "string", "名字（1~12 个字）"));

            Add("set_mode", "切换联机模式：别的玩家让你换模式时用，会在聊天里说一声。solo 自己玩、command 主要听指令、hybrid 指令做完接着自己玩。别人没要求就不要自己换。只有联机时能用。", 10,
                j => () => a.SetMode(Str(j, "mode")),
                P("mode", "string", "solo、command 或 hybrid"));

            Add("wait_chat", "联机时等别人说话：有人说话就立刻返回，最多等 seconds 秒（5~300）。闲着等吩咐时用。", 320,
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
        }

        private static Param P(string name, string type, string desc, bool required = true) => new(name, type, desc, required);

        private void Add(string name, string desc, int maxSeconds, Func<JsonObject, Func<IEnumerable<object>>> make, params Param[] ps)
        {
            specs[name] = new Spec { Name = name, Desc = desc, Params = ps, MaxSeconds = maxSeconds, Make = make };
        }

        private JsonArray BuildSchema()
        {
            var arr = new JsonArray();
            foreach (var s in specs.Values)
                arr.Add(Fn(s.Name, s.Desc, s.Params));
            return arr;
        }

        private static JsonObject Fn(string name, string desc, Param[] ps)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var p in ps)
            {
                props[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Desc };
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

        public bool IsKnown(string name) => name is "update_notes" or "cancel_action" || specs.ContainsKey(name);

        /// <summary>只查信息、马上出结果的工具：内置 AI 在跑的时候 MCP 也能用（不占动作队列，不会打断它）。</summary>
        public static readonly HashSet<string> ReadOnlyTools = new() { "gift_info", "bundle_info", "list_chests" };

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
            try
            {
                if (StardewModdingAPI.Context.IsWorldReady)
                    locBefore = await main.Invoke(() => StardewValley.Game1.currentLocation?.NameOrUniqueName).ConfigureAwait(false);
            }
            catch
            {
                // 拿不到就不提醒
            }
            bool interruptOnDamage = !CombatTools.Contains(name) && !InterfaceTools.Contains(name);
            // 本来就在剧情里用的（对话、跳过、关界面）、本来就要进剧情的（节日、睡觉时的夜间事件和婚礼）以外，途中冒出剧情就停下
            bool stopOnScene = !InterfaceTools.Contains(name) && name is not ("festival" or "sleep");
            string result = await main.RunAction(factory, spec.MaxSeconds, InterfaceTools.Contains(name), interruptOnDamage, name, !InterfaceTools.Contains(name) && !NoChatInterrupt.Contains(name), stopOnScene).ConfigureAwait(false);
            if (name == "sleep")
            {
                // 睡前顺手收纳了战利品就说一声
                string slept = null;
                try { slept = StardewModdingAPI.Context.IsWorldReady ? await main.Invoke(GameActions.TakeStoredLootNote).ConfigureAwait(false) : null; } catch { }
                return slept == null ? result : result + "\n" + slept;
            }
            if (result.Contains("背包满了") && name != "store_items" && name != "take_items")
                result += "\n" + BagFullAdvice;
            // 拿到更好的鞋子、戒指就自动换上（宝箱、商店、任务奖励都可能给）；
            // 身边有怪、深夜、体力见底的提醒也接在动作结果后面：MCP 客户端不一定每步都看 get_state
            string warning = null;
            try
            {
                // 世界没就绪时主线程可能不再 Tick，排队的 Invoke 会一直等，所以先判断
                if (StardewModdingAPI.Context.IsWorldReady)
                    warning = await main.Invoke(() =>
                    {
                        var equipped = GameActions.AutoEquip();
                        // 动作把人带到了别的地图（比如在海边 ship_items，会走回农场的出货箱）：明说现在在哪，免得 AI 以为还在原地
                        string locNow = StardewValley.Game1.currentLocation?.NameOrUniqueName;
                        string moved = locBefore != null && locNow != null && locNow != locBefore && name != "travel_to"
                            && StardewValley.Game1.currentLocation is not StardewValley.Locations.MineShaft
                            ? $"📍 这个动作把你从 {locBefore} 带到了 {locNow}，你现在在 {locNow}。接下来的动作都在这里进行；要回 {locBefore} 接着干就用 travel_to"
                            : null;
                        // 做完的任务，金币奖励要在日志里点一下才到手；AI 没有翻日志的工具，这里替它领
                        var claimed = GameActions.ClaimQuestRewards();
                        // 这次结果里有新的话就先不催，等下一步
                        string chat = GameActions.TakeChat();
                        string replyReminder = chat == null && name != "get_guide" ? GameActions.TakeReplyReminder() : null;
                        var parts = new[] { moved, equipped != null ? "🎽 " + equipped : null, claimed != null ? "💰 " + claimed : null, chat, replyReminder, GameActions.TakePickedOnTheWay(), GameActions.TakeStoredLootNote(), LootTools.Contains(name) ? GameActions.LootReminder() : null, GameActions.ThreatWarning(), GameActions.BedtimeWarning(urgentOnly: true) }.Where(w => w != null).ToList();
                        return parts.Count > 0 ? string.Join("\n", parts) : null;
                    }).ConfigureAwait(false);
            }
            catch
            {
                // 世界未就绪等情况下不提醒
            }
            if (jsonFixed != null)
                result += "\n" + jsonFixed;
            return warning == null ? result : result + "\n" + warning;
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

        /// <summary>
        /// 参数名对不上时不能静默忽略（比如 startLevel 被当成没传，人就从第 1 层开始爬）：
        /// 只是大小写、驼峰、下划线、连字符不同的，改成正确的名字；真不认识的报错，并列出这个工具的参数。
        /// </summary>
        private static string NormalizeArgs(Spec spec, JsonObject args)
        {
            var known = spec.Params.Select(p => p.Name).ToHashSet();
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
                var match = spec.Params.FirstOrDefault(p => CanonKey(p.Name) == CanonKey(key));
                if (match != null && !args.ContainsKey(match.Name))
                {
                    var value = args[key];
                    args.Remove(key);
                    args[match.Name] = value;
                    continue;
                }
                unknown.Add(key);
            }
            if (unknown.Count == 0)
                return null;
            string list = spec.Params.Length == 0 ? "这个工具不用传参数"
                : "这个工具的参数有：" + string.Join("、", spec.Params.Select(p => p.Name + (p.Required ? "（必填）" : "")));
            return $"{spec.Name} 不认识参数 {string.Join("、", unknown)}，所以什么都没做。{list}。改好参数名再调用一次";
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

        private static bool? OptBool(JsonObject j, string key)
        {
            var n = j[key];
            if (n is JsonValue v)
            {
                if (v.TryGetValue(out bool b))
                    return b;
                if (v.TryGetValue(out string s))
                    return s.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "是";
            }
            return null;
        }
    }
}
