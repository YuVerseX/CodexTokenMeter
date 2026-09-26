using System.Globalization;

namespace CodexTokenMeter.Core.Formatting;

/// <summary>
/// 数值格式化。
/// </summary>
/// <remarks>
/// 集中在一处以便统一风格：同一个数值在胶囊与展开面板里
/// 应当用同一种规则呈现，散落各处必然出现不一致。
/// </remarks>
public static class NumberFormatter
{
    /// <summary>紧凑格式，用于胶囊：40.3k、1.2M。</summary>
    public static string Compact(long value)
    {
        if (value <= 0)
        {
            return "0";
        }

        return value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
            >= 1_000 => $"{value / 1_000d:0.#}k",
            _ => value.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>千分位完整格式，用于展开面板。</summary>
    public static string Full(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 无分隔符的整数。
    /// </summary>
    /// <remarks>
    /// 必须显式指定不变文化。不带 <see cref="CultureInfo"/> 的
    /// <c>int.ToString()</c> 会在阿拉伯语、印地语等区域输出本地数字
    /// （如 <c>١٢٣</c>），浮层上会显示为无法识别的字符。
    /// </remarks>
    public static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>无分隔符的整数。</summary>
    public static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 费用格式。
    /// </summary>
    /// <remarks>
    /// 小数位随量级变化：单次调用常低于 0.01，
    /// 固定两位小数会把它显示成 0.00。
    /// </remarks>
    public static string Cost(double value) => value switch
    {
        >= 100 => value.ToString("0.00", CultureInfo.InvariantCulture),
        >= 1 => value.ToString("0.000", CultureInfo.InvariantCulture),
        >= 0.01 => value.ToString("0.0000", CultureInfo.InvariantCulture),
        _ => value.ToString("0.000000", CultureInfo.InvariantCulture),
    };

    /// <summary>上下文窗口容量：258k、1.0M。</summary>
    public static string ContextWindow(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
        >= 1_000 => $"{value / 1_000d:0}k",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>时长：紧凑形式，用于胶囊。</summary>
    public static string Duration(TimeSpan duration) => duration <= TimeSpan.Zero
        ? "—"
        : duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h{duration.Minutes:D2}m"
            : $"{duration.Minutes}m{duration.Seconds:D2}s";

    /// <summary>时长：完整形式，用于展开面板与提示。</summary>
    public static string DurationLong(TimeSpan duration) => duration <= TimeSpan.Zero
        ? "00:00:00"
        : $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
}
