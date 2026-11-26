using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;

// SVE 适配单元测试：动态连通分量分区、LoadMap/MagicWarp/EnterSewer 解析、OpenShop 不被特殊商店遮蔽、
// 重名影子 NPC 排序、SVE 特别订单板过滤、战斗区与 WarpRoom 识别、以及条件 SVE 攻略显隐。
partial class Program
{
    static void TestSveCompat(Assembly asm)
    {
        var tExits = asm.GetType("BlindWhaleFarmer.Exits");
        var tPath = asm.GetType("BlindWhaleFarmer.Pathfinder");
        var tActions = asm.GetType("BlindWhaleFarmer.GameActions");
        var tMod = asm.GetType("BlindWhaleFarmer.ModSupport");
        var tGuide = asm.GetType("BlindWhaleFarmer.Guide");
        var tObserver = asm.GetType("BlindWhaleFarmer.Observer");

        // ── 1. 动态连通分量（Exits.ComputeComponents + Exits.AreaFromGrid）──
        var mComp = tExits.GetMethod("ComputeComponents", Any);
        var mAreaGrid = tExits.GetMethod("AreaFromGrid", Any);
        // 10x8 地图，中间 y=4 是一整道悬崖墙，把南北完全隔开（类似 SVE Backwoods / Custom_ForestWest）
        Func<int, int, bool> twoRegion = (x, y) => y != 4;
        var grid = (int[,])mComp.Invoke(null, new object[] { 10, 8, twoRegion });
        int northA = (int)mAreaGrid.Invoke(null, new object[] { grid, 10, 8, new Point(1, 1) });
        int northB = (int)mAreaGrid.Invoke(null, new object[] { grid, 10, 8, new Point(9, 3) });
        int southA = (int)mAreaGrid.Invoke(null, new object[] { grid, 10, 8, new Point(2, 6) });
        int southEdgeWarp = (int)mAreaGrid.Invoke(null, new object[] { grid, 10, 8, new Point(10, 6) }); // 地图外边缘传送点
        int doorOnWall = (int)mAreaGrid.Invoke(null, new object[] { grid, 10, 8, new Point(5, 4) });    // 墙上的门优先吸附南侧
        Check(northA > 0 && northA == northB, "SVE 动态分区：同一连通区域内编号相同");
        Check(southA > 0 && southA != northA, "SVE 动态分区：被悬崖完全隔开的两块区域编号不同");
        Check(southEdgeWarp == southA, "SVE 动态分区：地图外边缘传送点正确夹回所属连通区域");
        Check(doorOnWall == southA, "SVE 动态分区：不可站门格优先吸附到南侧可站区域");

        // ── 2. TouchAction LoadMap / MagicWarp / Warp 与 Pathfinder.IsTouchWarpAction ──
        var mIsTouch = tPath.GetMethod("IsTouchWarpAction", Any);
        Check((bool)mIsTouch.Invoke(null, new object[] { "LoadMap Custom_BlueMoonVineyard 55 48 3" }), "Pathfinder 识别 SVE TouchAction LoadMap 为传送格");
        Check((bool)mIsTouch.Invoke(null, new object[] { "MagicWarp Custom_EnchantedGrove 30 14" }), "Pathfinder 识别 MagicWarp 为传送格");
        Check((bool)mIsTouch.Invoke(null, new object[] { "Warp Mountain 85 40" }), "Pathfinder 识别 Warp 为传送格");
        Check(!(bool)mIsTouch.Invoke(null, new object[] { "SVE_PlaySound slosh" }), "Pathfinder 不把 SVE_PlaySound 误判成传送格");

        var mParseTouch = tExits.GetMethod("ParseTouchWarp", Any);
        Func<string, bool> alwaysExists = _ => true;
        var loadMapExit = mParseTouch.Invoke(null, new object[] { "LoadMap Custom_BlueMoonVineyard 55 48 3", 49, 115, alwaysExists });
        Check(loadMapExit != null
              && (string)loadMapExit.GetType().GetField("Target").GetValue(loadMapExit) == "Custom_BlueMoonVineyard"
              && (Point)loadMapExit.GetType().GetField("TargetTile").GetValue(loadMapExit) == new Point(55, 48)
              && (bool)loadMapExit.GetType().GetField("IsTouch").GetValue(loadMapExit),
            "Exits.ParseTouchWarp 解析 SVE LoadMap 含朝向参数");
        var warpRoomTouch = mParseTouch.Invoke(null, new object[] { "LoadMap Custom_DayEnd_WarpRoom 3 3", 22, 8, alwaysExists });
        Check(warpRoomTouch == null, "Exits.ParseTouchWarp 过滤掉 SVE 后台 WarpRoom 传送");

        // ── 3. Door Action: EnterSewer / MagicWarp / WarpRoom 过滤 ──
        var mParseDoor = tExits.GetMethod("ParseDoorAction", Any);
        var sewerExit = mParseDoor.Invoke(null, new object[] { "EnterSewer", 55, 10, alwaysExists });
        Check(sewerExit != null && (string)sewerExit.GetType().GetField("Target").GetValue(sewerExit) == "Sewer",
            "Exits.ParseDoorAction 解析 EnterSewer（支持城镇与 SVE 蓝月葡萄园下水道入口）");
        var magicDoor = mParseDoor.Invoke(null, new object[] { "MagicWarp Custom_WizardBasement 8 18", 10, 10, alwaysExists });
        Check(magicDoor != null && (string)magicDoor.GetType().GetField("Target").GetValue(magicDoor) == "Custom_WizardBasement",
            "Exits.ParseDoorAction 解析 Buildings 层 Action MagicWarp");
        var warpRoomDoor = mParseDoor.Invoke(null, new object[] { "Warp 3 3 Custom_Claire_WarpRoom", 1, 1, alwaysExists });
        Check(warpRoomDoor == null, "Exits.ParseDoorAction 过滤掉通向 WarpRoom 的门");

        // ── 4. OpenShop 优先级：旅行货车未开时不遮蔽同图其他 OpenShop 摊位 ──
        var mShopChoice = tActions.GetMethod("ResolveSpecialShopChoice", Any);
        ValueTuple<string, string>? cartClosed = (null, "旅行货车今天没来");
        ValueTuple<string, string>? cartOpen = ("Traveler", null);

        var c1 = (ValueTuple<string, string, bool>)mShopChoice.Invoke(null, new object[] { "", cartClosed, Array.Empty<string>() });
        Check(c1.Item2 == "旅行货车今天没来" && !c1.Item3, "OpenShop：森林无其他商店且货车没来时照常提示货车没来");

        var c2 = (ValueTuple<string, string, bool>)mShopChoice.Invoke(null, new object[] { "", cartClosed, new[] { "ModForestShop" } });
        Check(c2.Item2 == null && c2.Item3, "OpenShop：货车没来但同图有其他 OpenShop 摊位时放行不遮蔽");

        var c3 = (ValueTuple<string, string, bool>)mShopChoice.Invoke(null, new object[] { "Traveler", cartClosed, new[] { "ModForestShop" } });
        Check(c3.Item2 == "旅行货车今天没来" && !c3.Item3, "OpenShop：明确点名 Traveler 且没来时仍如实报错");

        var c4 = (ValueTuple<string, string, bool>)mShopChoice.Invoke(null, new object[] { "ModForestShop", cartOpen, new[] { "ModForestShop" } });
        Check(c4.Item2 == null && c4.Item3, "OpenShop：货车开着但点名同图另一家 OpenShop 时直接放行");

        var c5 = (ValueTuple<string, string, bool>)mShopChoice.Invoke(null, new object[] { "", cartOpen, new[] { "ModForestShop" } });
        Check(c5.Item2 != null && c5.Item2.Contains("Traveler") && c5.Item2.Contains("ModForestShop"),
            "OpenShop：货车和同图另一家店同时开放且未传 shop_id 时列出两家供选");

        // ── 5. 同名 NPC 匹配排序（SVE MarlonFay vs 地图外影子 Marlon）──
        var mNpcRank = tActions.GetMethod("NpcMatchRank", Any);
        int shadowMarlon = (int)mNpcRank.Invoke(null, new object[] { true, false, false, false, false });
        int sveMarlonFay = (int)mNpcRank.Invoke(null, new object[] { false, true, false, true, false });
        int warpRoomNpc = (int)mNpcRank.Invoke(null, new object[] { true, true, false, true, true });
        Check(sveMarlonFay > shadowMarlon, "NPC 匹配排序：SVE 在图可社交 MarlonFay 压过地图外原版影子 Marlon");
        Check(sveMarlonFay > warpRoomNpc, "NPC 匹配排序：正常地图 NPC 压过 WarpRoom 后台 NPC");

        // ── 6. SVE 特别订单板过滤 ──
        var mOrderBoard = tActions.GetMethod("MatchesOrderBoard", Any);
        Check((bool)mOrderBoard.Invoke(null, new object[] { "", "" }), "特别订单板：镇上视图包含原版订单");
        Check((bool)mOrderBoard.Invoke(null, new object[] { "StardewValleyExpanded", "" }), "特别订单板：镇上视图包含 SVE 剧情特别订单（如 Robin3/Apples/MarlonFay2）");
        Check(!(bool)mOrderBoard.Invoke(null, new object[] { "Qi", "" }), "特别订单板：镇上视图排除 Qi 订单");
        Check(!(bool)mOrderBoard.Invoke(null, new object[] { "DesertFestivalMarlon", "" }), "特别订单板：镇上视图排除沙漠节订单");
        Check((bool)mOrderBoard.Invoke(null, new object[] { "Qi", "Qi" }), "特别订单板：Qi 视图包含 Qi 订单");
        Check(!(bool)mOrderBoard.Invoke(null, new object[] { "StardewValleyExpanded", "Qi" }), "特别订单板：Qi 视图排除 SVE 镇上订单");

        // ── 7. ModSupport：WarpRoom / 战斗区 / SVE 农场名 / 条件攻略显隐 ──
        var mWarpRoom = tMod.GetMethod("IsWarpRoom", Any);
        Check((bool)mWarpRoom.Invoke(null, new object[] { "Custom_Claire_WarpRoom" }), "ModSupport 识别 Custom_Claire_WarpRoom 为后台等待区");
        Check((bool)mWarpRoom.Invoke(null, new object[] { "Custom_Void" }), "ModSupport 识别 Custom_Void 为后台等待区");
        Check(!(bool)mWarpRoom.Invoke(null, new object[] { "Custom_BlueMoonVineyard" }), "ModSupport 不误伤正常 SVE 地图");

        var mCombat = tMod.GetMethod("IsCustomCombatMap", Any);
        Check((bool)mCombat.Invoke(null, new object[] { "Custom_CrimsonBadlands" }), "ModSupport 识别赤红荒原为战斗区");
        Check((bool)mCombat.Invoke(null, new object[] { "Custom_Highlands" }), "ModSupport 识别高地为战斗区");
        Check((bool)mCombat.Invoke(null, new object[] { "Custom_HighlandsCavern" }), "ModSupport 识别高地洞窟为战斗区");
        Check(!(bool)mCombat.Invoke(null, new object[] { "Custom_SophiaHouse" }), "ModSupport 不把安全室内误判为战斗区");

        var mFormatFarm = tObserver.GetMethod("FormatModFarmType", Any);
        Check(((string)mFormatFarm.Invoke(null, new object[] { "FrontierFarm" })).Contains("边境农场"), "Observer 识别 FrontierFarm");
        Check(((string)mFormatFarm.Invoke(null, new object[] { "MeadowlandsFarm" })).Contains("草原农场"), "Observer 保留草原农场识别");

        var mResetMod = tMod.GetMethod("ResetForTest", Any);
        var mModsNote = tMod.GetMethod("InstalledModsNote", Any);
        var mFarmOverride = tMod.GetMethod("SveStandardFarmOverrideName", Any);
        var mGuideLoad = tGuide.GetMethod("Load", Any);
        var mGuideGet = tGuide.GetMethod("Get", Any);
        var pTopics = tGuide.GetProperty("Topics", Any);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, "BlindWhaleFarmer", "guide")))
            repo = repo.Parent;
        string modDir = Path.Combine(repo?.FullName ?? "", "BlindWhaleFarmer");

        try
        {
            mResetMod.Invoke(null, new object[] { false, false, false, false });
            mGuideLoad.Invoke(null, new object[] { modDir, null });
            var vanillaTopics = (IReadOnlyList<string>)pTopics.GetValue(null);
            Check(mModsNote.Invoke(null, null) == null, "原版环境：InstalledModsNote 返回 null（零额外 token）");
            Check(vanillaTopics.Count >= 9 && !vanillaTopics.Any(t => t.StartsWith("SVE", StringComparison.OrdinalIgnoreCase)),
                "原版环境：Guide.Topics 自动隐藏 SVE 专题：" + string.Join("、", vanillaTopics));
            Check(mGuideGet.Invoke(null, new object[] { "SVE-总览" }) == null && mGuideGet.Invoke(null, new object[] { "SVE" }) == null,
                "原版环境：get_guide topic=SVE / SVE-总览 均返回 null");

            mResetMod.Invoke(null, new object[] { true, true, false, false });
            var sveTopics = (IReadOnlyList<string>)pTopics.GetValue(null);
            string sveNote = (string)mModsNote.Invoke(null, null);
            string sveGuide = (string)mGuideGet.Invoke(null, new object[] { "SVE-总览" });
            string sveAlias = (string)mGuideGet.Invoke(null, new object[] { "SVE" });
            string sveEvents = (string)mGuideGet.Invoke(null, new object[] { "SVE-事件链" });
            Check(sveNote != null && sveNote.Contains("SVE") && sveNote.Contains("Grandpa's Farm"), "SVE 环境：InstalledModsNote 包含 SVE 与附加农场");
            Check(((string)mFarmOverride.Invoke(null, null)).Contains("爷爷的农场"), "SVE 环境：标准农场槽位识别爷爷的农场");
            Check(sveTopics.Contains("SVE-总览") && sveTopics.Contains("SVE-事件链"), "SVE 环境：Guide.Topics 暴露 SVE-总览 与 SVE-事件链专题");
            Check(!string.IsNullOrWhiteSpace(sveGuide) && sveGuide.Contains("Custom_BlueMoonVineyard") && sveAlias == sveGuide, "SVE-总览（及别名 SVE）内容完整");
            Check(!string.IsNullOrWhiteSpace(sveEvents) && sveEvents.Contains("硬木 ×150") && !sveEvents.Contains("908071/e"),
                "SVE-事件链专题采用自然中文且无原始事件条件代码");
        }
        finally
        {
            // 还原原版状态，确保后续 MCP instructions 字节上限测试在原版基线下运行
            mResetMod.Invoke(null, new object[] { false, false, false, false });
        }
    }
}
