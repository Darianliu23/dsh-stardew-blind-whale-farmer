using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StardewValley;

namespace BlindWhaleFarmer
{
    /// <summary>保存最近显示过的几条文字，读取时只给新条目。</summary>
    internal sealed class ScreenHistory
    {
        private readonly (string line, object key)[] entries = new (string, object)[8];
        private long written;
        private long read;

        public static bool HudChanged(bool known, (string text, int number) previous, (string text, int number) current) =>
            !known || previous != current;

        public void Add(string stamp, string kind, string text, object key = null)
        {
            // 同一条 HUD 在两次读取之间叠加时，只留最新的总数。
            if (key != null)
                for (long i = Math.Max(read, written - entries.Length); i < written; i++)
                    if (ReferenceEquals(entries[i % entries.Length].key, key))
                    {
                        for (long j = i; j < written - 1; j++)
                            entries[j % entries.Length] = entries[(j + 1) % entries.Length];
                        written--;
                        break;
                    }
            entries[written++ % entries.Length] = ($"- {stamp} {kind}：{Clip(text, 160)}", key);
        }

        public List<string> TakeRecent()
        {
            var result = new List<string>();
            for (long i = Math.Max(read, written - 5); i < written; i++)
                result.Add(entries[i % entries.Length].line);
            read = written;
            return result;
        }

        public void Clear()
        {
            Array.Clear(entries, 0, entries.Length);
            written = read = 0;
        }

        public static string Clip(string text, int max)
        {
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length <= max)
                return text;
            int end = max - 1;
            if (char.IsHighSurrogate(text[end - 1]))
                end--;
            return text.Substring(0, end) + "…";
        }

        public static List<string> ExtractEventLines(string[] commands, int start)
        {
            var lines = new List<string>();
            if (commands == null)
                return lines;
            for (int i = Math.Max(0, start); i < commands.Length && lines.Count < 8; i++)
            {
                if (string.IsNullOrWhiteSpace(commands[i]))
                    continue;
                var args = ArgUtility.SplitBySpaceQuoteAware(commands[i]);
                string command = args.FirstOrDefault()?.ToLowerInvariant();
                if (command == "end")
                    break;
                bool speak = command == "speak";
                int textIndex = speak ? 2 : 1;
                if ((!speak && command != "message") || args.Length <= textIndex)
                    continue;
                string text = args[textIndex];
                // 只处理字面文字。情绪标记只换头像，可以去掉。带替换符、翻译键或对白分支的段落略过。
                if (speak)
                    text = Regex.Replace(text, @"\$(?:neutral|[hsula])(?=$|[\s#])", "");
                if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(new[] { '@', '%', '$', '[', ']', '{', '}', '^', '\\', '¦' }) >= 0
                    || Regex.IsMatch(text, @"^[^\s:：]+:[^\s]+$"))
                    continue;
                foreach (string part in text.Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    lines.Add(Clip(speak ? $"{args[1]}：{part}" : part, 160));
                    if (lines.Count == 8)
                        break;
                }
            }
            return lines;
        }
    }
}
