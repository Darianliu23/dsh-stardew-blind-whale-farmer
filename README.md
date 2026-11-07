# 大肥鱼种白饭 Blind Whale Farmer

**让 DeepSeek 不靠视觉玩星露谷**：闭着眼睛也能种田（种白饭）的大肥鱼 🐋🍚

`dsh-stardew-blind-whale-farmer`：一个 SMAPI 星露谷模组，加一个 DeepSeek Harness（DSH）插件。

一个 SMAPI 模组。它把游戏画面"翻译"成纯文字（状态、背包、文字地图、物体坐标、作物、出口、对话框/商店内容），交给 DeepSeek；DeepSeek 通过函数调用（tool calling）发出高层指令，模组负责寻路、走过去、用工具、播种、浇水、对话、购物、睡觉。整个过程不需要截图，也不需要视觉模型。

```
游戏 ──(主线程读取)──> 文字观察 ──> DeepSeek API ──> 工具调用 ──> 模组在游戏里执行（寻路+游戏原生操作）
  ^                                                                         |
  └──────────────────────────── 结果写进行动记录，进入下一轮 ────────────────┘
```

## 安装（第一次）

1. **安装 SMAPI**（星露谷的模组加载器）
   - 下载：<https://smapi.io>（开发时用的是 SMAPI 4.5.2 + 游戏 1.6.15）
   - 解压后运行安装程序（Windows 是 `install on Windows.bat`），按提示选择游戏目录
   - Steam 里给星露谷设置启动选项：`"<游戏目录>\StardewModdingAPI.exe" %command%`
2. **安装本模组**：从 GitHub Releases 下载压缩包，把里面的 `BlindWhaleFarmer` 文件夹放进游戏目录的 `Mods` 里。
   从源码安装的话，在仓库根目录运行（会自动在 Steam / GOG 的安装位置找游戏，包括装在其他盘的 Steam 库）：
   ```bash
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```
   ```bash
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```
3. **填 API Key**：打开 `游戏目录\Mods\BlindWhaleFarmer\config.json`，把 `ApiKey` 填成你的 DeepSeek Key（<https://platform.deepseek.com>）。也可以不写在文件里，改设系统环境变量 `DEEPSEEK_API_KEY`。

## 使用

1. 通过 SMAPI 启动游戏，读取存档（**建议先备份存档，或者新开一个存档给 AI 玩**，AI 睡觉时会自动保存）。
2. 按 **F8** 启动 / 停止 AI。屏幕左上角会显示 AI 的想法和正在执行的动作。
3. 游戏窗口失去焦点时星露谷默认会暂停，想让它在后台跑：游戏设置里关闭"窗口不活动时暂停"。

SMAPI 控制台命令（调试用）：

| 命令 | 作用 |
|---|---|
| `dsai_start` / `dsai_stop` | 启动 / 停止 AI |
| `dsai_state` | 打印当前发给 AI 的文字状态（不调用 API，可以先看看它"看到"了什么） |
| `dsai_do <工具> <JSON>` | 手动执行一个工具，例如 `dsai_do travel_to {"location":"SeedShop"}`、`dsai_do water_crops {}` |
| `dsai_notes [内容]` | 查看 / 改写 AI 的长期笔记 |
| `dsai_tokens` | 本次运行消耗的 token 数 |

每次运行的完整行动记录在 `Mods\BlindWhaleFarmer\logs\transcript-*.log`。AI 的笔记按存档保存在 `Mods\BlindWhaleFarmer\data\`。

## 用 WorkBuddy（或其他 MCP 客户端）来玩：不需要 API Key

模组启动时会在本机开一个 MCP 服务器：`http://127.0.0.1:18080/mcp`（只监听本机，别的电脑连不上）。WorkBuddy、Claude Code、Cursor 这类支持 MCP 的客户端连上来后，**由客户端自己的 AI 思考、调用模组的工具玩游戏，消耗的是客户端的额度**，不需要 DeepSeek API Key。

**WorkBuddy 设置：**
1. 先通过 SMAPI 启动游戏、读取存档（SMAPI 控制台会显示 `MCP 服务器已启动：http://127.0.0.1:18080/mcp`）。
2. WorkBuddy → 设置 / 连接器 → 自定义连接器 → Configure MCP（会打开 `~/.workbuddy/mcp.json`），加上：
   ```json
   {
     "mcpServers": {
       "stardew": {
         "url": "http://127.0.0.1:18080/mcp"
       }
     }
   }
   ```
   保存后在 My MCP 列表里打开它的开关。不同版本菜单名可能不一样，以你的客户端界面为准；如果它要求选传输方式，选 Streamable HTTP。
3. 对 WorkBuddy 说："用 stardew 工具玩星露谷：先 get_state 看看状态，然后今天把作物浇完、出货，晚上睡觉。"

**说明：**
- 工具：`get_state`（读取文字状态）、`get_guide`（读攻略），加上内置 AI 用的全部工具；连接时还会把操作规则和攻略（和内置 AI 的提示词一样）发给客户端。
- 客户端思考（两次调用之间）时游戏时间自动冻结；超过 `McpIdleSeconds`（默认 120 秒）没有调用就把整个游戏暂停（按任意键或 AI 再次调用时继续，`PauseWhenAiStops` 可关）。屏幕左上角会显示 `[MCP 客户端思考中，时间已冻结]`。
- 按 F8 启动的内置 AI 和 MCP 客户端不能同时操作：内置 AI 运行时，MCP 的工具调用会返回提示让你先按 F8 停掉它。
- AI 卡住或者你想自己玩一会儿时，在游戏里按 **F7** 接管：AI 正在执行的动作立刻中止，之后它的游戏动作都会被拒绝（它会收到“玩家接管了”的提示并停下汇报）。再按一次 F7 交还，然后在 DSH 里让它继续。
- 长动作（下矿、钓鱼、wait）最长要 15 分钟；接受 SSE 的客户端（比如 DSH）会每 15 秒收到一次心跳，不会因为等太久断开。个别客户端的工具调用超时比这短，遇到超时就让它把动作拆小。
- 客户端的一次任务通常只能连续玩一段时间，需要你隔一会儿让它"继续"。
- 端口冲突时改 `config.json` 的 `McpPort`；不想开就设 `EnableMcp: false`。

**用 DeepSeek Harness（DSH）玩：** 装上本仓库的 [DSH 插件](docs/dsh-plugin.md)（`dsh-stardew-blind-whale-farmer`）。它会：
- 提供一个 **“大肥鱼种白饭”会话预设**（默认关闭，在插件的配置页里打开）：新建会话时选它，那个会话才连上游戏、拿到玩游戏的工具；其他会话里完全没有这些工具和说明，不占上下文；已经对话过的会话不能再换预设（DSH 的规定），要玩请新建会话；AI 的长期笔记会出现在 get_state 最前面，新会话能接着玩。
- 也可以在所有会话（包括老会话）里提供一个很小的 **stardew 入口工具**（默认关闭）：不用换预设，常驻上下文只多一个工具的简短说明，AI 要玩时先 `action="help"` 再按需调用各个动作。
- 在 DSH 左侧边栏「插件」→ dsh-stardew-blind-whale-farmer → 点组件名旁边的「配置 ›」 里提供设置：**每次玩几个游戏日 / 多少分钟**、思考时冻结时间、给 AI 的额外要求、攻略文件等，改了自动推送到游戏，不用重启。

桌面版和网页版用不同的 profile，各自都要装。不要再在 profile 里全局挂 `@deepseek-ai/dsh-mcp-client` 连星露谷，否则每个会话都会带上这些工具。

**更新模组后：** 关掉游戏，装好新版，打开游戏（到标题画面就行），再重启 DSH，回到原来的会话说一句“继续”就行，不用新开会话，聊天记录也还在。只重开游戏不够：这样虽然模组改动会生效，但会话里还是旧的工具列表和规则。

## 联机：和 AI 一起玩

你当主机，AI 当农场工人，在同一台电脑上开两个游戏窗口。

1. **准备存档**：联机存档要有空的小屋（新建时选，或者找 Robin 建）。建议先备份。
2. **打开两个窗口**：在仓库目录运行 `.\coop.ps1`。AI 的窗口用单独的 `Mods-AI` 文件夹和 18081 端口。
3. **你的窗口**（标题写 4 mods 或者你的模组数量）：合作 → 主持，读取存档。
4. **AI 的窗口**（2 mods）：点一下让它在前台，合作 → 加入，输入 `127.0.0.1`，选空的农场工人。
5. **DSH**：插件配置里把游戏端口改成 18081，重启 DSH。之后单人玩要改回 18080。

**聊天**：按 **F6** 弹出中文输入框，回车发送（游戏自带的聊天框看不到输入法候选词）。AI 用 `say` 回你，也看得懂没有声调的拼音。

**三种模式**（DSH 插件配置“联机时怎么安排自己”，也可以直接在聊天里跟她说）：

| 模式 | 你说话时 | 没指令时 |
|---|---|---|
| 指令做完接着自己玩（默认） | 手上的动作先停下，听你的 | 接着自己的计划 |
| 主要听指令 | 手上的动作先停下，听你的 | 跟着你或等你发话 |
| 自己玩 | 不打断，回一句 | 接着自己的计划 |

聊天里说“听我的”“你自己玩吧”这类固定口令立刻切换；用自己的话说，AI 听懂后用 `set_mode` 切换。重启游戏后，DSH 会把模式改回配置卡片上的值。

**只有主机能做的事**：社区中心开门的剧情（第 5 天起的晴天 8:00-13:00，从巴士站走进小镇）、玛妮送宠物、德米特里厄斯选山洞、节日开始主活动（冬日星盛宴、万灵节除外）、拆建筑、升级农舍。亲手捐收集包的玩家要各自去法师塔看完剧情才读得懂卷轴。

**节日**：所有玩家都到场才开始。进去后等 AI 说“我准备好了”，你再找刘易斯开始，不然她赶不上邀舞伴、放汤。夏威夷宴会每个玩家都要放汤料。

**结婚**：AI 可以娶村民，也可以和你结婚：你拿结婚戒指对她按动作键，或者她用 `propose` 向你求婚，婚礼在 3 天后，双方都要把对白点完。

**已知限制**：
- 联机时游戏时钟不能暂停，AI 思考时时间照走。
- 有些游戏数据（比如她不在的地点）在农场工人这边可能不是最新的。
- 只实际测试过复活节和跟玩家结婚的完整流程；其他节日、娶村民、婚后的孩子是对照游戏代码写的，还没实测。

## AI 能看到什么（纯文字）

- 时间、天气（含明天天气预报）、金钱、体力、生命、技能等级、未读邮件
- 当前界面：对话内容和选项、商店商品和价格、信件内容、升级职业描述、其他菜单的可点击元素
- 背包每一格
- 本地点的所有出口（含营业时间）、村民位置和好感度、生日、今天是否聊过天
- 作物：需要浇水/可收获/枯死的坐标，生长中作物还剩几天
- 附近物体：石头、杂草、树、树枝、采集品、机器（加工状态）、箱子、建筑、可交互的地图格，带坐标和建议工具
- 以玩家为中心的字符地图（29×19 格，带图例和坐标）
- 动物待办汇总（没摸的、可挤奶/剪毛的、地上的蛋、食槽缺的干草、筒仓存量），以及当前地点每只动物的好感和状态
- 附近村民对你背包里物品的喜好（最爱/喜欢/讨厌）
- 背包里能捐给博物馆的东西、能交给社区中心收集包的材料
- 附近能不能钓鱼；矿洞层数、下楼梯/竖井/电梯/出口的坐标

## AI 能做什么（52 个工具）

| 类别 | 工具 |
|---|---|
| 移动 | `move_to`（A* 寻路）、`travel_to`（跨地图自动规划路线，会自动开门） |
| 单个操作 | `use_tool`、`interact`（聊天/开门/捡东西/收机器/送礼）、`place_item`、`eat` |
| 批量农活 | `till_area`、`plant_seeds`、`water_crops`（水壶空了自动去灌水）、`harvest_crops`、`clear_area` |
| 钓鱼 | `fish`（自动找岸边、按钓鱼等级算抛竿力度、等咬钩、提竿、自动玩钓鱼小游戏、收宝箱，一次最多连钓 20 竿） |
| 动物 | `care_animals`（自动进每个鸡舍/畜棚：捡蛋和羊毛、清空自动采集器、食槽放干草、摸动物、挤奶/剪毛；可开关动物门） |
| 经营 | `build`（找 Robin 建造/升级建筑，自动找空地）、`upgrade_house`、`buy_animal`（找 Marnie 买动物，自动入住、起名）、`crack_geodes`（找 Clint 砸晶球）、`upgrade_tool`（找 Clint 升级/领取工具）、`quest_board`（告示板委托） |
| 冒险 | `fight`（自动靠近打怪，血少自动吃回血食物）、`explore_mine`（自动打怪、敲石头找楼梯、往下走，可坐电梯直达） |
| 生活 | `cook`（厨房做菜，冰箱材料也能用）、`check_crab_pots`（收蟹笼、补鱼饵） |
| 联机 | `say`（在游戏聊天里说话）、`set_mode`（换联机模式）、`wait_chat`（等别人说话）、`follow`（跟着别的玩家走）、`propose`（拿结婚戒指向别的玩家求婚） |
| 起名 | `set_name`（给孩子、刚出生的动物起名） |
| 社交 | `gift_info`（查喜好/生日/本周送礼次数）、`give_gift`（自动找到村民送礼，先检查次数限制） |
| 收集 | `bundle_info` / `donate_bundles`（社区中心收集包：看缺什么、自动去交，领奖励、触发修复过场）、`donate_museum`（捐矿物古物并领奖励） |
| 经济 | `ship_items`（出货箱）、`open_shop`、`buy`、`sell`、`craft`、`store_items` / `take_items`（箱子存取）、`tidy_backpack`（一键整理背包：每类工具留最好的，其余存进农场箱子） |
| 界面 | `dialogue`、`menu_click`、`close_menu`、`choose_profession`、`skip_event`、`festival`（参加节日：入场、打招呼、开始主活动、离开）、`leave_festival` |
| 时间 | `sleep`（自动回家上床）、`wait` |
| 记忆 | `update_notes` |

工具执行都走游戏自己的逻辑（`BeginUsingTool`、`checkAction`、`tryToPlaceItem`、出货箱 `shipItem` 等），体力消耗、动画、掉落都和手动操作一致；走路是逐格移动并播放走路动画。

## 配置项（config.json）

| 字段 | 默认 | 说明 |
|---|---|---|
| `ApiKey` | 空 | DeepSeek API Key |
| `BaseUrl` | `https://api.deepseek.com` | 任何 OpenAI 兼容接口都可以 |
| `Model` | `deepseek-v4-flash` | 需要支持 function calling 的模型；想要更聪明可改成 `deepseek-v4-pro`（更贵更慢）。旧名 `deepseek-chat` 已停用 |
| `MaxTokens` | 4096 | 每次回复最多输出多少 token（只按实际输出计费；模型如果开了思考模式，思考内容也算在里面） |
| `ToggleKey` | `F8` | 开关快捷键 |
| `TakeOverKey` | `F7` | 接管键：AI 卡住或乱跑时按一下，立刻中止它正在做的动作、由你自己操作；再按一次交还给 AI |
| `FreezeTimeWhileThinking` | `true` | 等模型回复时冻结游戏时钟，思考不占游戏时间 |
| `MaxActionsPerStep` | 6 | 模型一次回复最多执行几个工具调用 |
| `HistoryLines` | 25 | 提示词里保留多少条最近行动记录 |
| `MapRadiusX` / `MapRadiusY` | 14 / 9 | 文字地图大小 |
| `ScanRadius` | 16 | 附近物体扫描半径 |
| `AutoConfirmNightMenus` | `true` | 自动确认出货结算、普通升级界面 |
| `ForageRadius` | `1` | 走路时顺手采集野菜、贝壳这类野生采集物的范围（格）。`0` 不采，`1` 只采紧挨着路的，更大会离开路线几步去采再回来（最大 10）。DSH 插件的配置页里也能改 |
| `ExtraInstructions` | 空 | 写给 AI 的额外目标，例如 `"第一年专心种地赚钱，每天和 Abigail 聊天"` |
| `GuideFile` | `guide/攻略.md` | 攻略文件（游戏常识和打法路线，见下方“攻略”）。想换打法就复制一份改名，再把这里指过去 |
| `LogPrompts` | `false` | 把完整提示词写进 transcript，调试用 |
| `EnableMcp` | `true` | 开启本机 MCP 服务器（给 WorkBuddy 等客户端用） |
| `McpPort` | 18080 | MCP 端口，地址 `http://127.0.0.1:端口/mcp` |
| `McpFreezeTime` | `true` | MCP 客户端思考时冻结游戏时间 |
| `McpIdleSeconds` | 120 | 多久没有 MCP 调用就视为客户端不玩了，恢复走时 |
| `SessionDays` | 0 | 每次游玩最多玩几个游戏日（睡一觉算一天），到了 AI 停下汇报。0 = 不限，每次读档重新计 |
| `SessionMinutes` | 0 | 每次游玩最多玩多少现实分钟，到了先回家睡觉存档再停下。0 = 不限 |

以上 `SessionDays`、`SessionMinutes`、`McpFreezeTime`、`McpIdleSeconds`、`ExtraInstructions`、`GuideFile` 也可以在 DSH 里用 `dsh-stardew-blind-whale-farmer` 插件改，改完会写回这个文件。

## 攻略（AI 的打法）

AI 收到的提示分两部分：
- **操作规则**（写在 `Agent.cs`）：怎么用工具、先处理界面、失败别重复、按时睡觉……跟着工具代码一起改。
- **攻略**（`guide/攻略.md`，纯文本）：游戏常识、装备、花钱规划、农场类型和第一年的打法路线。启动时读取，不用重新编译；MCP 客户端也能用 `get_guide` 工具随时查。
- **专题**（`guide/` 下其他 `.md`：`钓鱼`、`采矿`、`节日`）：不放进每次连接的说明里，AI 用 `get_guide` 传 `topic` 按需读，省 token。钓鱼专题里各水域的鱼是从游戏数据生成的。
- 在 DSH 插件的配置页里也能直接改这几篇攻略（文本框里默认就是自带的内容），改过的篇目会推给游戏、优先于文件。

想让 AI 换个玩法（比如专心钓鱼、走 Joja 路线），或者翻译成别的语言：把 `guide/攻略.md` 复制成新文件（比如 `guide/my.md`）改内容，再把 `config.json` 的 `GuideFile` 指过去。自带的攻略会随模组更新覆盖，自己的文件不会。

攻略内容参考了社区的新手攻略，具体数字（营业时间、配方、剧情触发条件等）都对照游戏数据核对过，欢迎提 PR 补充改进。

## 费用估计

每一步请求大约 10k～14k 输入 token（系统提示 + 43 个工具定义约 7.5k，固定不变，能命中 DeepSeek 的缓存；游戏状态 3k～6k），输出几百 token。AI 大约每 10～20 秒一步，所以每小时大约一两百万输入 token。具体价格以 DeepSeek 官网为准，可以用 `dsai_tokens` 查看实际消耗。

## 已知限制

- **钓鱼**：小游戏由模组按帧自动操作（模型只决定去哪钓、钓几竿）。用复刻的小游戏物理离线模拟过：难度 70 以下的普通鱼几乎都能钓上，难度 90 以上的（传说鱼等）钓鱼等级低时成功率很低。蟹笼、鱼饵/浮漂的装配没有专门支持。
- **战斗比较朴素**：`fight` / `explore_mine` 的策略是“靠近、挥最好的近战武器、血少就吃”，不会走位躲技能、不用弹弓和炸弹，矿洞深层（40 层以后）和骷髅洞穴会很危险。竖井不会自动跳。
- 动物：喂干草直接调用游戏的 `feedAllAnimals`（从筒仓扣干草放进食槽，结果和手动放一样，但省掉了来回走到饲料槽取干草）。孵蛋没有专门支持。
- 建造时位置由程序自动选（农舍附近、不压耕地和树），也可以指定坐标；拆除、移动、换皮肤没有支持。
- 装修、节日里的小游戏（找彩蛋、冰钓比赛、展览会小游戏、夏威夷宴会放汤）、特别订单（Special Orders）没有专门支持（可以用 `interact`/`menu_click` 硬来，效果看模型发挥）。
- **节日**：`festival enter` 入场 → `greet` 挨个打招呼加好感 → `start` 找刘易斯开始主活动，主活动结束游戏会送你回农场。不参加的话，节日进行中 `travel_to` 不会走进或路过节日地图（一进去时间就停住、普通工具都用不了）。节日专题攻略里有每个节日的玩法。
- **晚上**：19:00 以后状态里会显示“回家上床大约要多少分钟”（按回家路线的距离和走路速度估算）；来不及在 24:00 前躺下或体力快见底时，每个动作结果后面都会附上警告。
- 寻路只在当前地图内做 A*，跨地图靠出口图做 BFS；个别特殊入口（比如需要钥匙的门、沙漠巴士）可能走不通，会返回失败原因。
- 只支持单人模式。

## 目录结构

```
dsh-stardew-blind-whale-farmer/
├─ package.json / cordis.patch.yml / lib/   DSH 插件（就在仓库根目录，DSH 可以直接从 GitHub 装）
├─ docs/dsh-plugin.md         DSH 插件的说明
├─ BlindWhaleFarmer/          星露谷模组源码（C#，net6.0）
│  ├─ ModEntry.cs             入口、快捷键、HUD、控制台命令、时间冻结
│  ├─ Agent.cs                AI 主循环和操作规则
│  ├─ Guide.cs                读取攻略文件
│  ├─ guide/攻略.md            攻略（游戏常识和第一年路线）
│  ├─ McpServer.cs            本机 MCP 服务器（WorkBuddy 等客户端接入）
│  ├─ DeepSeekClient.cs       OpenAI 兼容接口客户端（重试、超时）
│  ├─ Tools.cs                所有工具的 JSON Schema 和参数解析
│  ├─ GameActions.Storage.cs  箱子存取
│  ├─ GameActions.cs          所有动作的实现（协程，逐帧执行）
│  ├─ GameActions.Fishing.cs  钓鱼动作和钓鱼小游戏自动操作
│  ├─ GameActions.Animals.cs  照顾动物
│  ├─ GameActions.Knowledge.cs 送礼、博物馆捐赠、社区中心收集包
│  ├─ GameActions.Adventure.cs 烹饪、蟹笼、战斗、自动下矿
│  ├─ GameActions.Town.cs     工具升级、告示板、买动物、建造、升级农舍
│  ├─ Knowledge.cs            送礼喜好/收集包/博物馆数据解析
│  ├─ Observer.cs             游戏状态 → 文字
│  ├─ Exits.cs                出口识别和跨地图路线规划
│  ├─ Pathfinder.cs           A* 寻路
│  ├─ MainThread.cs           后台线程 ↔ 游戏主线程调度
│  └─ Util.cs                 物品/工具查找等工具函数
├─ tests/HarnessTest/         模组的离线测试（不需要启动游戏）
├─ tests/DshPlugin/           DSH 插件的测试（node --test tests/DshPlugin/sync.test.mjs）
├─ tools/MapDump/             读本机游戏的地图/数据文件：画碰撞图、模拟寻路和钓点（排查“走不到”时用）
├─ release/BlindWhaleFarmer/   build.ps1 生成的模组，直接复制进 Mods
├─ Directory.Build.props      各项目共用的游戏路径设置
├─ common.ps1                 脚本共用：找 dotnet 和游戏目录
├─ local.ps1.example          本机路径设置示例（复制成 local.ps1，不会提交）
├─ build.ps1                  编译
├─ test.ps1                   编译并跑离线测试
└─ install.ps1                安装到游戏
```

## 从源码编译

需要 .NET 6 或更新的 SDK，以及装好 SMAPI 的星露谷。

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

```bash
powershell -ExecutionPolicy Bypass -File test.ps1
```

脚本会自动在 Steam（包括其他盘的 Steam 库）和 GOG 的默认位置找游戏。找不到、或者 dotnet 不在 PATH 里时，把 `local.ps1.example` 复制成 `local.ps1` 填上路径。直接用 `dotnet build` 也行，设置环境变量 `STARDEW_PATH` 指向游戏目录即可。
