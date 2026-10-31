using System;
using System.IO;

namespace BlindWhaleFarmer
{
    /// <summary>
    /// 攻略文件（guide/*.md）：游戏常识和打法建议，和代码里的操作规则（Agent.Rules）分开放，
    /// 玩家可以直接改文本调整 AI 的打法、或者换成其他语言，不用重新编译。
    /// </summary>
    internal static class Guide
    {
        public const string DefaultFile = "guide/zh-CN.md";

        /// <summary>当前攻略全文；没有攻略文件时为空字符串（只用操作规则也能玩）。</summary>
        public static string Text { get; private set; } = "";

        /// <summary>从模组目录读取攻略，返回一行给日志看的说明。file 可以是相对模组目录的路径或绝对路径。</summary>
        public static string Load(string modDir, string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                file = DefaultFile;
            string path = Path.IsPathRooted(file) ? file : Path.Combine(modDir, file);
            try
            {
                if (!File.Exists(path))
                {
                    Text = "";
                    return $"没有找到攻略文件 {path}，AI 只使用基本操作规则";
                }
                Text = File.ReadAllText(path).Trim();
                return $"已加载攻略 {file}（{Text.Length} 字）";
            }
            catch (Exception ex)
            {
                Text = "";
                return $"读取攻略文件 {path} 失败：{ex.Message}";
            }
        }
    }
}
