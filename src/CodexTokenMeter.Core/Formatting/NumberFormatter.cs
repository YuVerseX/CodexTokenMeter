using System.Globalization;

namespace CodexTokenMeter.Core.Formatting;

/// <summary>
/// 数值格式化。
/// </summary>
/// <remarks>
/// <para>
/// 集中在一处以便统一风格：同一个数值在胶囊与展开面板里
/// 应当用同一种规则呈现，散落各处必然出现不一致。
/// </para>
/// <para>
/// <b>所有输出强制使用不变文化。</b>
/// 浮层是给读数字用的，数字必须是 ASCII：
/// 阿拉伯语（ar-SA）与波斯语（fa-IR）区域把小数点渲染成
/// <c>٫</c>（U+066B），印地语等在部分格式下用本地数字字形。
/// 这类字符在浮层的等宽字体下会显示为异体字或方块，
/// 且 "1٫2M" 这种写法也难以快速辨识。
/// </para>
/// <para>
/// 插值字符串中的数字同样受当前区域影响，
/// 因此带格式说明符的插值一律显式传入 <see cref="CultureInfo.InvariantCulture"/>，
/// 而不是依赖默认格式化。
/// </para>
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
            >= 1_000_000_000 => Format(value / 1_000_000_000d, "0.#") + "B",
            >= 1_000_000 => Format(value / 1_000_000d, "0.#") + "M",
            >= 1_000 => Format(value / 1_000d, "0.#") + "k",
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
        >= 100 => Format(value, "0.00"),
        >= 1 => Format(value, "0.000"),
        >= 0.01 => Format(value, "0.0000"),
        _ => Format(value, "0.000000"),
    };

    /// <summary>上下文窗口容量：258k、1.0M。</summary>
    public static string ContextWindow(long value) => value switch
    {
        >= 1_000_000 => Format(value / 1_000_000d, "0.0") + "M",
        >= 1_000 => Format(value / 1_000d, "0") + "k",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>时长：紧凑形式，用于胶囊。</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "—";
        }

        // 每一段都显式指定不变文化：
        // 数字间的分隔符与补零不应随区域变化。
        var minutes = duration.Minutes.ToString("D2", CultureInfo.InvariantCulture);
        var seconds = duration.Seconds.ToString("D2", CultureInfo.InvariantCulture);

        return duration.TotalHours >= 1
            ? $"{Count((int)duration.TotalHours)}h{minutes}m"
            : $"{Count(duration.Minutes)}m{seconds}s";
    }

    /// <summary>
    /// 按不变文化格式化小数，位数可指定。
    /// </summary>
    /// <remarks>
    /// 通用工具，供不需要百分号或千分位的场景使用
    /// （如诊断输出里的坐标、缩放比、费用）。
    /// 非有限值归零，避免输出 <c>NaN</c> / <c>∞</c>。
    /// </remarks>
    public static string Fixed(double value, int decimals)
    {
        var digits = Math.Clamp(decimals, 0, 8);
        var format = "F" + digits.ToString(CultureInfo.InvariantCulture);
        var safe = double.IsFinite(value) ? value : 0d;

        return Format(safe, format);
    }

    /// <summary>
    /// 按不变文化格式化数值，不带百分号。
    /// </summary>
    /// <remarks>
    /// 供需要自行拼接场景的调用方使用（例如诊断输出里的
    /// 「命中率=12.3%」需要把数值与单位分开处理）。
    /// 与 <see cref="Percent"/> 不同，本方法不做 0–100 收敛。
    /// </remarks>
    public static string PercentRaw(double value, int decimals = 0) =>
        Fixed(value, decimals);

    /// <summary>
    /// 百分比，用于浮层上的命中率与上下文占用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 收敛到 0–100：数据异常时可能出现越界值，
    /// 而「无 150.0%」这类文本会让人无法判断到底发生了什么。
    /// </para>
    /// <para>
    /// 小数位限制在 0–2：百分比超过两位小数没有展示价值，
    /// 而且会让胶囊宽度不稳定。需要更多位数时用
    /// <see cref="Fixed"/>。
    /// </para>
    /// </remarks>
    public static string Percent(double value, int decimals)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0d;
        var digits = Math.Clamp(decimals, 0, 2);

        return Fixed(clamped, digits) + "%";
    }

    /// <summary>
    /// 时长：完整形式，用于展开面板与提示。
    /// </summary>
    public static string DurationLong(TimeSpan duration) => duration <= TimeSpan.Zero
        ? "00:00:00"
        : $"{Count((int)duration.TotalHours).PadLeft(2, '0')}"
            + $":{duration.Minutes.ToString("D2", CultureInfo.InvariantCulture)}"
            + $":{duration.Seconds.ToString("D2", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 按不变文化格式化一个数值。
    /// </summary>
    /// <remarks>
    /// 插值字符串的格式说明符会使用当前区域，
    /// 因此这里统一走显式指定文化的方法，而不是内联插值。
    /// </remarks>
    private static string Format(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
