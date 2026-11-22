using System.Collections.Generic;
using System.Linq;

namespace BlindWhaleFarmer
{
    /// <summary>只用普通数据记录背包数量，按物品 ID 和品质分开计数。</summary>
    internal static class InventoryDiff
    {
        private const int MaxShown = 6;

        public static (string id, string name, int quality, int quantity)[] Snapshot(
            IEnumerable<(string id, string name, int quality, int quantity)> items) =>
            Sum(items.Where(item => item.quantity > 0));

        public static (string id, string name, int quality, int quantity)[] Difference(
            IEnumerable<(string id, string name, int quality, int quantity)> before,
            IEnumerable<(string id, string name, int quality, int quantity)> after) =>
            Sum(after.Concat(before.Select(item => (item.id, item.name, item.quality, -item.quantity))));

        // 保留减少的数量，几个动作间隙合起来时才能相互抵消。最后输出时只显示净增加。
        public static (string id, string name, int quality, int quantity)[] Sum(
            IEnumerable<(string id, string name, int quality, int quantity)> items) =>
            items.GroupBy(item => (item.id, item.quality))
                .Select(group => (group.Key.id, group.First().name, group.Key.quality, quantity: group.Sum(item => item.quantity)))
                .Where(item => item.quantity != 0)
                .ToArray();

        public static string FormatGains(IEnumerable<(string id, string name, int quality, int quantity)> changes)
        {
            var gains = changes.Where(item => item.quantity > 0).ToArray();
            if (gains.Length == 0)
                return null;

            string text = string.Join("、", gains.Take(MaxShown).Select(item =>
            {
                string name = string.IsNullOrWhiteSpace(item.name) ? item.id : item.name;
                string quality = item.quality switch { 1 => "（银星）", 2 or 3 => "（金星）", 4 => "（铱星）", _ => "" };
                return $"{name}{quality}x{item.quantity}";
            }));
            return gains.Length > MaxShown ? $"{text} 等 {gains.Length} 样" : text;
        }
    }
}
