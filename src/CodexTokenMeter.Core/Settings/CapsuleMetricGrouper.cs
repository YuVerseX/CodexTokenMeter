namespace CodexTokenMeter.Core.Settings;

/// <summary>
/// 胶囊指标的分组结果。
/// </summary>
/// <param name="Items">该组包含的指标。</param>
/// <param name="PrecededByDivider">该组之前是否应插入分隔线。</param>
public sealed record CapsuleMetricGrouping(
    IReadOnlyList<CapsuleMetric> Items,
    bool PrecededByDivider);

/// <summary>
/// 按语义分组排列胶囊指标。
/// </summary>
/// <remarks>
/// <para>
/// 胶囊在分组之间插入一道细分隔线，让「本轮 / 累计 / 上下文 / 费用」
/// 这几类指标无需文字说明即可区分。
/// </para>
/// <para>
/// 用户的勾选组合是任意的，因此分隔线位置无法写死——
/// 由相邻指标的分组是否相同推导。若用户只勾了一项，
/// 或勾选的都在同一组内，则整条胶囊没有分隔线。
/// </para>
/// </remarks>
public static class CapsuleMetricGrouper
{
    /// <summary>
    /// 把指标列表整理为分组序列。
    /// </summary>
    /// <param name="metrics">指标列表，应已按规范顺序排列。</param>
    /// <returns>分组序列，按输入顺序。</returns>
    public static IReadOnlyList<CapsuleMetricGrouping> Group(IReadOnlyList<CapsuleMetric> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var groups = new List<CapsuleMetricGrouping>();

        if (metrics.Count == 0)
        {
            return groups;
        }

        var current = new List<CapsuleMetric>();
        CapsuleMetricGroup? currentGroup = null;

        foreach (var metric in metrics)
        {
            var group = CapsuleMetricCatalog.GetGroup(metric);

            // 分组发生变化即开启新的一组。第一组之前不插分隔线。
            if (currentGroup is { } previous && group != previous)
            {
                groups.Add(new CapsuleMetricGrouping(current, groups.Count > 0));
                current = [];
            }

            current.Add(metric);
            currentGroup = group;
        }

        groups.Add(new CapsuleMetricGrouping(current, groups.Count > 0));
        return groups;
    }
}
