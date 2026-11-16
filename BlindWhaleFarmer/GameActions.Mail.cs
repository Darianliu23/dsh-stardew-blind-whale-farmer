using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 读信 ─────────────────────────

        /// <summary>
        /// 走到信箱把未读的信一封封读完：每封的内容、附带的物品/钱/配方/任务都记下来，附带的东西收进背包（装不下的掉在脚边）。
        /// 交互信箱一次只开一封，AI 常常读完第一封就去干别的，后面的信（任务、剧情）一直没看。
        /// </summary>
        public IEnumerable<object> ReadMail()
        {
            if (Player.mailbox.Count == 0)
            {
                yield return ActionResult.Success("信箱里没有未读的信");
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var o = new Outcome();
            if (Loc is not Farm)
            {
                foreach (var s in TravelInner("Farm", o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("回农场失败：" + o.Message);
                    yield break;
                }
            }
            var box = Player.getMailboxPosition();
            foreach (var s in GoAdjacent(box, o))
                yield return s;
            if (!o.Ok)
            {
                yield return ActionResult.Fail($"走不到信箱 ({box.X},{box.Y})：{o.Message}");
                yield break;
            }

            var letters = new List<string>();
            var dropped = new List<string>();
            int guard = 0;
            while (Player.mailbox.Count > 0 && guard++ < 30)
            {
                Face(box);
                Loc.mailbox();
                LetterViewerMenu letter = null;
                for (int t = 0; t < 60 && letter == null; t++)
                {
                    letter = Game1.activeClickableMenu as LetterViewerMenu;
                    if (letter == null)
                        yield return null;
                }
                if (letter == null)
                    continue; // 只是个标记、没有正文的“信”，游戏不开信纸
                string text = (observer.DescribeMenu() ?? "").Replace("^", " ");
                text = string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith("（读完用 close_menu")));
                if (text.Length > 600)
                    text = text.Substring(0, 600) + "…";
                letters.Add(text);
                // 关信时游戏会把附带的东西收进背包、把任务加进日志；背包满了会弹“拿走物品”的界面
                letter.exitThisMenu(playSound: false);
                for (int t = 0; t < 60 && Game1.activeClickableMenu == letter; t++)
                    yield return null;
                if (Game1.activeClickableMenu is ItemGrabMenu grab)
                    dropped.AddRange(GrabAll(grab));
                foreach (var s in WaitTicks(10))
                    yield return s;
            }

            if (letters.Count == 0)
            {
                yield return ActionResult.Success("信箱里的都是没有正文的通知，已经收掉了");
                yield break;
            }
            string msg = $"读了 {letters.Count} 封信：\n" + string.Join("\n", letters.Select((l, i) => $"{i + 1}. {l}"));
            if (dropped.Count > 0)
                msg += $"\n背包满了，{string.Join("、", dropped)} 掉在脚边（空出位置走过去能捡回来）";
            if (Player.mailbox.Count > 0)
                msg += $"\n还有 {Player.mailbox.Count} 封没读完，再 read_mail 一次";
            yield return ActionResult.Success(msg);
        }
    }
}
