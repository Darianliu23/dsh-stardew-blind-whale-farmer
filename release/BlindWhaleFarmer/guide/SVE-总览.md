# Stardew Valley Expanded（SVE）专题攻略

> 本篇仅在检测到已安装 **Stardew Valley Expanded（SVE）** 时出现。原版基础机制仍以主攻略与其余专题为准；钓鱼分布、村民送礼喜好、收集包和博物馆缺口插件都会直接读取当前游戏实时数据（用 `fish_info`、`gift_info`、`bundle_info`、`museum_info` 查，不需要背表）。

## 一、在 SVE 里玩的重要提醒
- **新增数据全部实时读取**：SVE 新增了数十名村民、上百张地图、新鱼、新商店和 7 件博物馆藏品（博物馆总数不再是 95，以 `museum_info` 为准）。不要靠记忆猜鱼点或喜好，直接调工具查。
- **部分村民只在特定日期/时段出场**：如 Claire（克莱尔）、Martin（马丁）、Lance（兰斯）、Scarlett（斯嘉丽）等，不在镇上时处于 SVE 后台等待房间，无法直接去找；用 `gift_info` 或 `interact` 查看日程或换天再找。
- **马龙与冈瑟在 SVE 中可社交**：直接用 `interact npc=Marlon` / `give_gift npc=Marlon`（或 `MarlonFay`）、`Gunther`（或 `GuntherSilvian`）即可，插件会自动避开隐藏在地图外的原版影子 NPC。
- **大地图与长路线**：SVE 地图普遍更大（西煤矿森林、高地、赤红荒原等均为 156×156 甚至 240×156），往返耗时比原版长，晚上务必提前留足回家时间或随身带农场传送图腾。
- **野外高危战斗区**：`Custom_Highlands`（高地）、`Custom_HighlandsCavern`（高地洞窟）、`Custom_CrimsonBadlands`（赤红荒原）、`Custom_ForbiddenMaze`（禁林迷宫）虽不是矿洞，但怪物密集且伤害极高，进入前必须按矿洞/骷髅洞穴标准带齐强力武器、大量回血食物、炸弹与回城手段。

## 二、SVE 常用地点内部名速查（直接给 `travel_to` 用）
### 前期即可到达
- `Custom_BlueMoonVineyard`：蓝月葡萄园（从 `Town` 南部或 `Beach` 西侧进入；内有 Sophia 的房子与另一个下水道入口）
- `Custom_SophiaHouse`：Sophia 的家（室内账本可 `open_shop` 买蓝月酒与洒水器；完成 Sophia 特别订单后可买铱制洒水器与加压喷头）
- `Custom_JenkinsHouse`：Olivia 与 Victor 母子的宅邸（在 `Town` 内）
- `Custom_AndyHouse`：Andy 的农舍（从 `Forest` 南部进入）
- `Custom_SusanHouse`：Susan 的翡翠农场（在 `Railroad` 铁路区域，夏季 3 日地震清理山体滑坡后可达）
- `Custom_ShearwaterBridge`：剪水桥（`Town` 东侧，风景与钓鱼/采集点）
- `Custom_AdventurerSummit`：冒险家山顶（`Mountain` 东侧，SVE 将冒险家公会移至此附近）
- `Custom_ForestWest`：西煤矿森林（`Forest` 西侧超大地图，通往极光葡萄园、精灵泉和熊的商店）
- `Custom_AuroraVineyard`：极光葡萄园（位于 `Custom_ForestWest`，中后期翻新支线核心地点）
- `Custom_GrandpasShedOutside` / `Custom_GrandpasShedRuins`：爷爷小屋外部与废墟（装了 Grandpa's Farm / Frontier Farm 或原版标准农场外侧；修好后变为 `Custom_GrandpasShed` 和楼上四季温室 `Custom_GrandpasShedGreenhouse`）

### 中后期解锁地点
- `Custom_SpriteSpring2`：精灵泉（西煤矿森林深处，有回血温泉与稀有鱼/采集）
- `Custom_EnchantedGrove`：魔法森林传送枢纽（连接农场、法师塔、精灵泉、冒险家公会、祝尼魔森林、赤红荒原哨站等）
- `Custom_JunimoWoods`：祝尼魔森林（内有 6 位彩色祝尼魔摊位，可用 `open_shop` 买特殊种子与物品）
- `Custom_Highlands` / `Custom_HighlandsCavern` / `Custom_HighlandsOutpost`：高地、高地洞窟与哨站（完成马龙小船任务后乘船前往）
- `Custom_CrimsonBadlands` / `Custom_CastleVillageOutpost`：赤红荒原与城堡村哨站（顶级战斗刷宝区）
- `Custom_JojaEmporium`：Joja 百货（走 Joja 路线后期解锁，内含多个专属摊位与 Joja 专属种子）

## 三、SVE 实用特色商店
- **Sophia 账本**（`Custom_SophiaHouse`）：直接 `open_shop`，前期能买普通/优质洒水器（600g / 3500g），完成她的特别订单后还能买铱制洒水器与加压喷头，极其省心。
- **熊的商店**（`Custom_ForestWest`，完成原版秘密纸条 23 给熊送枫糖浆后解锁）：卖蜂蜜、各类树液（枫糖浆/松焦油/橡树树脂/冷杉蜡/白桦汁）、红木种子及 SVE 料理配方。
- **祝尼魔森林商店**（`Custom_JunimoWoods`）：6 只彩色祝尼魔各开一个摊位，出售多种 SVE 专属作物种子、怪物掉落与稀有道具，进图后用 `open_shop` 查看可选 `shop_id`。
- **高地矮人商店**（`Custom_HighlandsCavern` 监狱解救矮人后解锁）：出售矿石节点装饰与实用道具。
- **Joja 超市与 Joja 百货**：SVE 里 JojaMart 营业时间很长且有双柜台（`open_shop` 自动找有人看店的柜台）；若走 Joja 路线，后期解锁的 `Custom_JojaEmporium` 有多个独占商品和种子。

## 四、分阶段路线建议
1. **第一年春～夏**：按原版节奏开局（种田、升级背包/水壶/斧镐、下矿），顺路认识新村民（Andy、Sophia、Olivia、Victor、Morris、Marlon），早点去 `Custom_BlueMoonVineyard` 逛逛；需要洒水器又缺矿时可直接去 `Custom_SophiaHouse` 账本买。
2. **第一年秋～冬**：解锁刘易斯家门口特别订单板后，留意 SVE 特别订单（特别是 Sophia、Andy、Susan、Olivia、Marlon 的订单），同时提前囤 **硬木 150、石头 600、铁锭 50、电池组 20**，用于尽快修好**爷爷的小屋**拿第二个四季温室。
3. **路线选择提醒（社区中心 vs Joja）**：
   - 社区中心路线：解锁“社区日（每月 22 日）”、祝尼魔 Apples 室友线与极光葡萄园祝尼魔翻新，后期走魔法/祝尼魔内容最完整。
   - Joja 路线：Morris 戏份大幅扩展，可解锁 Joja 日、Joja 百货（`Custom_JojaEmporium`）与直接买下极光葡萄园，赚钱买项目节奏更快。
4. **第二年及以后**：推进极光葡萄园、魔法森林传送枢纽、姜岛与马龙小船，再装备毕业武器与大量补给挑战高地与赤红荒原。详细主线解锁条件和材料表请读 `get_guide topic=SVE-事件链`。
