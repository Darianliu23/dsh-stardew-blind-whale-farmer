using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// 扩展模组（当前重点支持 Stardew Valley Expanded / SVE）的运行时检测与语义辅助。
    /// 没装扩展模组时所有判定都返回原版默认行为，不给原版玩家增加任何额外文本。
    /// </summary>
    internal static class ModSupport
    {
        public const string SveContentPackId = "FlashShifter.StardewValleyExpandedCP";
        public const string SveCodeId = "FlashShifter.SVECode";
        public const string GrandpasFarmId = "FlashShifter.GrandpasFarm";
        public const string ImmersiveFarm2Id = "flashshifter.immersivefarm2remastered";
        public const string FrontierFarmId = "flashshifter.FrontierFarm";
        public const string GrampletonFieldsId = "flashshifter.GrampletonFields";

        /// <summary>SVE 里的危险战斗地图（普通 GameLocation，不是 MineShaft/VolcanoDungeon，但遍地高伤怪物）。</summary>
        private static readonly HashSet<string> CustomCombatMaps = new(StringComparer.OrdinalIgnoreCase)
        {
            "Custom_CrimsonBadlands",
            "Custom_Highlands",
            "Custom_HighlandsCavern",
            "Custom_HighlandsCavernPrison",
            "Custom_ForbiddenMaze",
            "Custom_VoidDen",
            "BugLand",
        };

        /// <summary>是否加载了 Stardew Valley Expanded。</summary>
        public static bool HasSve { get; internal set; }

        /// <summary>装了哪个 SVE 官方农场替换模组（用于区分标准农场槽位被 Grandpa's Farm / IF2R / Frontier Farm 替换的情况）。</summary>
        public static bool HasGrandpasFarm { get; internal set; }
        public static bool HasImmersiveFarm2 { get; internal set; }
        public static bool HasFrontierFarm { get; internal set; }
        public static bool HasGrampletonFields { get; internal set; }

        public static void Detect(IModRegistry registry)
        {
            if (registry == null)
                return;
            HasSve = registry.IsLoaded(SveContentPackId) || registry.IsLoaded(SveCodeId);
            HasGrandpasFarm = registry.IsLoaded(GrandpasFarmId);
            HasImmersiveFarm2 = registry.IsLoaded(ImmersiveFarm2Id);
            HasFrontierFarm = registry.IsLoaded(FrontierFarmId);
            HasGrampletonFields = registry.IsLoaded(GrampletonFieldsId);
        }

        /// <summary>供测试显式重置或模拟已加载模组。</summary>
        internal static void ResetForTest(bool hasSve = false, bool grandpasFarm = false, bool if2r = false, bool frontierFarm = false)
        {
            HasSve = hasSve;
            HasGrandpasFarm = grandpasFarm;
            HasImmersiveFarm2 = if2r;
            HasFrontierFarm = frontierFarm;
            HasGrampletonFields = false;
        }

        /// <summary>get_state 首次输出一次的扩展模组提示；原版环境返回 null（零额外 token）。</summary>
        public static string InstalledModsNote()
        {
            if (!HasSve)
                return null;
            var extras = new List<string>();
            if (HasGrandpasFarm) extras.Add("Grandpa's Farm");
            if (HasImmersiveFarm2) extras.Add("Immersive Farm 2 Remastered");
            if (HasFrontierFarm) extras.Add("Frontier Farm");
            if (HasGrampletonFields) extras.Add("Grampleton Fields");
            string sub = extras.Count > 0 ? $"（附加：{string.Join("、", extras)}）" : "";
            return $"扩展模组：已加载 Stardew Valley Expanded（SVE）{sub}。新地点/村民/商店/鱼类自动生效；SVE 专属路线、事件链与危险区见 get_guide topic=SVE-总览";
        }

        /// <summary>
        /// SVE 用来暂存未出场 NPC 或过场演出的后台地图（如 Custom_Claire_WarpRoom、Custom_Void）。
        /// 玩家进不去，寻路和村民位置表里都应该忽略。
        /// </summary>
        public static bool IsWarpRoom(string locationName)
        {
            if (string.IsNullOrWhiteSpace(locationName))
                return false;
            return locationName.EndsWith("_WarpRoom", StringComparison.OrdinalIgnoreCase)
                || locationName.Equals("Custom_Void", StringComparison.OrdinalIgnoreCase)
                || locationName.StartsWith("Custom_FakeEnchantedGrove", StringComparison.OrdinalIgnoreCase)
                || locationName.EndsWith("PictureMap", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按地点名判断是不是已知的 SVE/扩展野外战斗区（离线测试可直接测名字）。</summary>
        public static bool IsCustomCombatMap(string locationName) =>
            !string.IsNullOrWhiteSpace(locationName) && CustomCombatMaps.Contains(locationName);

        /// <summary>矿洞、火山或 SVE 战斗区（赤红荒原、高地、高地洞窟、禁林迷宫等）。</summary>
        public static bool IsCombatZone(GameLocation loc)
        {
            if (loc == null)
                return false;
            if (loc is MineShaft or VolcanoDungeon)
                return true;
            if (IsCustomCombatMap(loc.NameOrUniqueName) || IsCustomCombatMap(loc.Name))
                return true;
            return false;
        }

        /// <summary>当前地图是否处于危险战斗环境（战斗区，或非农场地图上有活着的怪物）。</summary>
        public static bool HasActiveDanger(GameLocation loc) =>
            loc != null && (IsCombatZone(loc) || loc.characters.OfType<Monster>().Any());

        /// <summary>标准农场槽位（whichFarm == 0）是否被 SVE 官方大农场替换；没有替换返回 null。</summary>
        public static string SveStandardFarmOverrideName()
        {
            if (HasImmersiveFarm2)
                return "沉浸式农场2重制版（Immersive Farm 2 Remastered，SVE 超大农场）";
            if (HasGrandpasFarm)
                return "爷爷的农场（Grandpa's Farm，SVE 农场，带爷爷小屋与矿车/温室扩展）";
            if (HasFrontierFarm)
                return "边境农场（Frontier Farm，SVE 农场）";
            return null;
        }
    }
}
