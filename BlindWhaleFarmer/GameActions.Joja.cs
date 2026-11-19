using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── Joja 路线 ─────────────────────────
        // 在 JojaMart 柜台找 Morris：先花 5000g 办会员（社区中心收集包从此作废），
        // 第二天起用“社区发展表”花钱买项目，每个项目对应社区中心的一个房间，一天只能下一单、第二天生效。
        // 五个都买完后看完仓库剧情，还能花 500000g 买电影院。效果和在表上点一样（扣钱、寄第二天生效的信）。

        private static readonly (string mail, int price, string name, string effect)[] JojaProjects =
        {
            ("ccVault", 40000, "巴士", "修好去沙漠的巴士"),
            ("ccBoilerRoom", 15000, "矿车", "修好矿车"),
            ("ccCraftsRoom", 25000, "桥", "修好山上采石场的桥"),
            ("ccPantry", 35000, "温室", "修好农场温室"),
            ("ccFishTank", 20000, "清理大石头", "清走矿洞口河边的闪光大石头（之后能淘盘）"),
        };

        private static readonly string[] JojaMailKeys = { "jojaVault", "jojaBoilerRoom", "jojaCraftsRoom", "jojaPantry", "jojaFishTank" };

        private static string JojaStatusText()
        {
            var p = Game1.player;
            if (!Game1.MasterPlayer.mailReceived.Contains("JojaMember"))
            {
                if (Game1.player.mailForTomorrow.Contains("JojaMember%&NL&%"))
                    return "会员今天刚办，明天起才能用社区发展表买项目";
                if (Game1.MasterPlayer.mailReceived.Contains("ccIsComplete"))
                    return "社区中心已经修好了，Joja 路线没法再走";
                return $"还不是 Joja 会员（会员 5000g，办了以后社区中心收集包作废，改成花钱买项目；你现在有 {p.Money}g）。"
                    + (p.eventsSeen.Contains("611439") ? "" : "要先去看过社区中心的开场剧情（镇长带你进去那段），Morris 才肯办");
            }
            var lines = JojaProjects.Select((x, i) =>
            {
                string state = Utility.doesAnyFarmerHaveOrWillReceiveMail(x.mail)
                    ? (p.mailForTomorrow.Contains(JojaMailKeys[i] + "%&NL&%") ? "已下单，明天生效" : "已完成")
                    : $"{x.price}g";
                return $"{i + 1}. {x.name}（{x.effect}）：{state}";
            });
            string text = "Joja 会员。社区发展项目：\n" + string.Join("\n", lines);
            if (p.eventsSeen.Contains("502261"))
                text += p.hasOrWillReceiveMail("ccMovieTheater") ? "\n电影院：已买" : "\n电影院：500000g，可以买（project=theater）";
            return text;
        }

        /// <summary>Joja 路线：status 看进度、join 办会员、project 买社区发展项目（编号或名字，theater 是电影院）。</summary>
        public IEnumerable<object> Joja(string action, string project)
        {
            action = (action ?? "status").Trim().ToLowerInvariant();
            if (action is "status" or "" || (action != "join" && action != "buy"))
            {
                yield return ActionResult.Success(JojaStatusText());
                yield break;
            }
            if (Blocked(out string why))
            {
                yield return ActionResult.Fail(why);
                yield break;
            }
            var p = Game1.player;
            if (!Game1.IsMasterGame)
            {
                yield return ActionResult.Fail("联机时只有房主能办 Joja 会员、买项目");
                yield break;
            }

            // 先判断能不能办，免得白跑
            int price;
            int index = -1;
            if (action == "join")
            {
                if (p.mailReceived.Contains("JojaMember") || p.mailForTomorrow.Contains("JojaMember%&NL&%"))
                {
                    yield return ActionResult.Fail("已经是 Joja 会员了。" + JojaStatusText());
                    yield break;
                }
                if (p.mailReceived.Contains("ccIsComplete"))
                {
                    yield return ActionResult.Fail("社区中心已经修好了，Morris 不再卖会员");
                    yield break;
                }
                if (!p.eventsSeen.Contains("611439"))
                {
                    yield return ActionResult.Fail("还没看过社区中心的开场剧情（春天晴天 8 点后去镇上社区中心门口附近触发），Morris 现在还不卖会员");
                    yield break;
                }
                price = 5000;
            }
            else
            {
                if (!p.mailReceived.Contains("JojaMember"))
                {
                    yield return ActionResult.Fail(JojaStatusText());
                    yield break;
                }
                string q = (project ?? "").Trim();
                if (q.Equals("theater", System.StringComparison.OrdinalIgnoreCase) || q.Contains("电影"))
                {
                    if (!p.eventsSeen.Contains("502261"))
                    {
                        yield return ActionResult.Fail("要先买完五个社区发展项目、看完社区中心变成仓库的剧情，才能买电影院");
                        yield break;
                    }
                    if (p.hasOrWillReceiveMail("ccMovieTheater"))
                    {
                        yield return ActionResult.Fail("电影院已经买过了");
                        yield break;
                    }
                    price = 500000;
                    index = 5;
                }
                else
                {
                    if (int.TryParse(q, out int n) && n >= 1 && n <= 5)
                        index = n - 1;
                    else if (q != "")
                        index = System.Array.FindIndex(JojaProjects, x => x.name.Contains(q) || q.Contains(x.name) || x.mail.Contains(q, System.StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                    {
                        yield return ActionResult.Fail("要买哪个项目？project 传编号 1-5 或名字，或者 theater。\n" + JojaStatusText());
                        yield break;
                    }
                    if (Utility.doesAnyFarmerHaveOrWillReceiveMail(JojaProjects[index].mail))
                    {
                        yield return ActionResult.Fail($"“{JojaProjects[index].name}”已经完成或已经下单了。\n" + JojaStatusText());
                        yield break;
                    }
                    if (JojaMailKeys.Any(k => p.mailForTomorrow.Contains(k + "%&NL&%")))
                    {
                        yield return ActionResult.Fail("今天已经下过一单了，Morris 说还在处理，明天再来买下一个");
                        yield break;
                    }
                    price = JojaProjects[index].price;
                }
            }
            if (p.Money < price)
            {
                yield return ActionResult.Fail($"钱不够：要 {price}g，现在只有 {p.Money}g");
                yield break;
            }

            // 走到 JojaMart 的 Morris 柜台
            var o = new Outcome();
            if (Loc.NameOrUniqueName != "JojaMart")
            {
                foreach (var s in TravelInner("JojaMart", o))
                    yield return s;
                if (Loc.NameOrUniqueName != "JojaMart")
                {
                    yield return ActionResult.Fail("去 JojaMart 失败：" + o.Message);
                    yield break;
                }
            }
            if (FindTileAction(Loc, "JoinJoja") is { } counter)
            {
                foreach (var s in GoAdjacent(counter, o))
                    yield return s;
                if (!o.Ok)
                {
                    yield return ActionResult.Fail("走不到 Morris 的柜台旁边：" + o.Message);
                    yield break;
                }
            }
            if (p.Money < price)
            {
                yield return ActionResult.Fail($"钱不够：要 {price}g，现在只有 {p.Money}g");
                yield break;
            }

            p.Money -= price;
            Game1.playSound("reward");
            if (action == "join")
            {
                Game1.addMailForTomorrow("JojaMember", noLetter: true, sendToEveryone: true);
                p.removeQuest("26");
                yield return ActionResult.Success($"在 Morris 那里办了 Joja 会员，花了 5000g（还剩 {p.Money}g）。社区中心的收集包从此作废；明天起用 joja action=buy 买社区发展项目：\n"
                    + string.Join("\n", JojaProjects.Select((x, i) => $"{i + 1}. {x.name}（{x.effect}）{x.price}g")));
            }
            else if (index == 5)
            {
                Game1.addMailForTomorrow("ccMovieTheater", noLetter: true, sendToEveryone: true);
                Game1.addMailForTomorrow("ccMovieTheaterJoja", noLetter: true, sendToEveryone: true);
                if (p.team.theaterBuildDate.Value < 0)
                    p.team.theaterBuildDate.Set(Game1.Date.TotalDays + 1);
                yield return ActionResult.Success($"买下了电影院，花了 500000g（还剩 {p.Money}g），过几天就建好");
            }
            else
            {
                var x = JojaProjects[index];
                Game1.addMailForTomorrow(JojaMailKeys[index], noLetter: true, sendToEveryone: true);
                Game1.addMailForTomorrow(x.mail, noLetter: true, sendToEveryone: true);
                p.activeDialogueEvents.TryAdd("joja_Begin", 7);
                int left = JojaProjects.Count(y => !Utility.doesAnyFarmerHaveOrWillReceiveMail(y.mail));
                yield return ActionResult.Success($"在社区发展表上买了“{x.name}”，花了 {x.price}g（还剩 {p.Money}g），明天{x.effect}。"
                    + (left > 0 ? $"还剩 {left} 个项目，一天只能下一单" : "五个项目都买完了，之后会有社区中心变成仓库的剧情，看完能买电影院（500000g）"));
            }
        }
    }
}
