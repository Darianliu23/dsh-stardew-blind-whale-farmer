using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;

partial class Program
{
    static void TestCoopCartGiftDrop(Assembly asm, Type tActions, JsonArray schema, Func<string, string, Task<string>> run)
    {
        const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var tObserver = asm.GetType("BlindWhaleFarmer.Observer");

        // 1. 猪车（旅行货车）观察提示与营业判定
        var fmtCart = tObserver.GetMethod("FormatTravelingCartHint", Any);
        string Cart(bool day, int time, bool inForest, int x = 27, int y = 11) =>
            (string)fmtCart.Invoke(null, new object[] { day, time, inForest, new Point(x, y) });

        Check(Cart(false, 1000, true) == null, "非周五/周日森林里不提示旅行货车");
        Check(Cart(false, 1000, false) == null, "非周五/周日森林外不提示旅行货车");
        string cartOut = Cart(true, 900, false);
        Check(cartOut != null && cartOut.Contains("Forest") && cartOut.Contains("open_shop"), "周五/周日森林外提示猪车在 Forest 营业：" + cartOut);
        string cartIn = Cart(true, 900, true, 27, 11);
        Check(cartIn != null && cartIn.Contains("(27,11)") && cartIn.Contains("open_shop") && cartIn.Contains("interact"), "周五/周日森林内提示猪车柜台坐标和打开方式：" + cartIn);
        string cartClosedIn = Cart(true, 2000, true);
        Check(cartClosedIn != null && cartClosedIn.Contains("20:00"), "20:00 后森林内提示猪车已收摊：" + cartClosedIn);
        Check(Cart(true, 2000, false) == null, "20:00 后森林外不再提示猪车");

        var cartOpen = tActions.GetMethod("TravelingMerchantOpenState", Any);
        (bool open, string why) CartState(bool flag, bool shouldVisit, int time) =>
            ((bool, string))cartOpen.Invoke(null, new object[] { flag, shouldVisit, time });

        Check(!CartState(false, false, 1000).open, "非周五/周日猪车不开");
        Check(CartState(false, true, 1000).open, "联机农场工人 travelingMerchantDay 未同步但今天应来时，20:00 前判定可开");
        Check(!CartState(true, true, 2000).open && CartState(true, true, 2000).why.Contains("20:00"), "20:00 后猪车收摊");

        // 2. 开局礼物盒提示与归属区分（房主 FarmHouse vs 农场工人 Cabin）
        var fmtHomeGift = tObserver.GetMethod("FormatHomeGiftboxHint", Any);
        string HomeGift(bool inHome, string homeName, int x, int y, string items) =>
            (string)fmtHomeGift.Invoke(null, new object[] { inHome, homeName, new Point(x, y), items });

        string insideHint = HomeGift(true, "Cabin_1234", 3, 7, "防风草种子x15");
        Check(insideHint.Contains("(3,7)") && insideHint.Contains("防风草种子x15") && insideHint.Contains("interact x=3 y=7"), "在自己小屋内提示礼物盒坐标与内容：" + insideHint);
        string outsideHint = HomeGift(false, "Cabin_1234", 3, 7, "防风草种子x15");
        Check(outsideHint.Contains("Cabin_1234") && outsideHint.Contains("travel_to") && outsideHint.Contains("(3,7)"), "在屋外时提示自己家里的未拆礼物盒：" + outsideHint);

        var fmtGiftFeat = tObserver.GetMethod("FormatGiftboxFeature", Any);
        (string name, string hint) GiftFeat(bool starter, bool canOpen, string owner, string myHome, string items) =>
            ((string, string))fmtGiftFeat.Invoke(null, new object[] { starter, canOpen, owner, myHome, items });

        var ownFeat = GiftFeat(true, true, "小鱼", "Cabin_1234", "防风草种子x15");
        Check(ownFeat.name.Contains("礼物盒") && ownFeat.name.Contains("防风草种子x15") && ownFeat.hint == "interact 打开", "自己的开局礼物盒标明可 interact 打开");
        var otherFeat = GiftFeat(true, false, "房主", "Cabin_1234", "防风草种子x15");
        Check(otherFeat.name.Contains("房主") && otherFeat.name.Contains("打不开") && otherFeat.name.Contains("Cabin_1234") && otherFeat.hint == "", "别人的开局礼物盒明确提示打不开并指向自己家");

        // 3. drop_item 数量校验与 schema / 别名
        var resolveDrop = tActions.GetMethod("ResolveDropCount", Any);
        (int cnt, string err) DropCnt(int? req, int stack) =>
            ((int, string))resolveDrop.Invoke(null, new object[] { req, stack });

        Check(DropCnt(null, 15) == (15, null), "drop_item 不传数量时默认整格");
        Check(DropCnt(3, 15) == (3, null), "drop_item 指定部分数量");
        Check(DropCnt(99, 15) == (15, null), "drop_item 超过堆叠数时截断到当前堆叠数");
        Check(DropCnt(0, 15).err != null, "drop_item 数量为 0 报错");
        Check(DropCnt(-2, 15).err != null, "drop_item 数量为负报错");
        Check(DropCnt(1, 0).err != null, "drop_item 空堆叠报错");

        var dropSchema = schema.FirstOrDefault(t => t["function"]["name"].GetValue<string>() == "drop_item");
        Check(dropSchema != null, "schema 包含 drop_item 工具");
        var dropProps = dropSchema?["function"]?["parameters"]?["properties"] as JsonObject;
        Check(dropProps != null && dropProps["item"] != null && dropProps["count"] != null && dropProps["to"] != null && dropProps["force"] != null,
            "drop_item 参数包含 item/count/to/force");

        string rDropMissing = run("drop_item", "{}").GetAwaiter().GetResult();
        Check(rDropMissing.StartsWith("失败：drop_item 缺少必填参数 item"), "drop_item 缺少 item 报错：" + Shorten(rDropMissing));
        foreach (string aliasJson in new[]
        {
            "{\"item\":\"防风草种子\"}",
            "{\"name\":\"防风草种子\",\"quantity\":5,\"player\":\"小明\"}",
            "{\"item_name\":\"木材\",\"count\":2,\"target\":\"房主\"}",
        })
        {
            string rr = run("drop_item", aliasJson).GetAwaiter().GetResult();
            Check(rr.Contains("游戏世界未就绪"), $"drop_item 别名通过校验 {aliasJson}：" + Shorten(rr));
        }
    }
}
