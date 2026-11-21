using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace BlindWhaleFarmer
{
    internal sealed partial class GameActions
    {
        // ───────────────────────── 发表情（头顶气泡 / 动作） ─────────────────────────
        // 走 Farmer.netDoEmote：本机马上播，联机时其他玩家也看得到（和游戏里按 E 的表情菜单是同一条路）。

        private static readonly (string id, string cn)[] EmoteNames =
        {
            ("happy", "开心"), ("sad", "难过"), ("heart", "爱心"), ("exclamation", "感叹号"), ("note", "音符"),
            ("sleep", "睡觉"), ("game", "游戏"), ("question", "疑问"), ("x", "叉"), ("pause", "省略号"),
            ("angry", "生气"), ("yes", "点头"), ("no", "摇头"), ("sick", "不舒服"), ("laugh", "大笑"),
            ("surprised", "惊讶"), ("hi", "打招呼"), ("taunt", "挑衅"), ("uh", "呃"), ("music", "唱歌"),
        };

        public IEnumerable<object> Emote(string which)
        {
            string key = (which ?? "").Trim().ToLowerInvariant();
            if (key == "")
            {
                yield return ActionResult.Success("可用表情：" + string.Join("、", EmoteNames.Select(e => $"{e.id}（{e.cn}）"))
                    + "。传 emote=名字就在头顶冒出气泡；点头/摇头/大笑/打招呼/挑衅/唱歌等带动作，要站着不动才播得出来。");
                yield break;
            }
            var hit = EmoteNames.FirstOrDefault(e => e.id == key || e.cn == key);
            if (hit.id == null)
            {
                yield return ActionResult.Fail($"没有“{which}”这个表情。不传 emote 可以看列表");
                yield break;
            }
            var who = Player;
            if (!who.CanEmote())
            {
                yield return ActionResult.Fail("现在发不了表情（在过场、读档或正在做别的动作），等一下再试");
                yield break;
            }
            who.netDoEmote(hit.id);
            for (int i = 0; i < 90 && (who.isEmoting || who.isEmoteAnimating); i++)
                yield return null;
            yield return ActionResult.Success($"发了“{hit.cn}”表情");
        }
    }
}
