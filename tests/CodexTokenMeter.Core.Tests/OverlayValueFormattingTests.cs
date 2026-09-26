using System.Globalization;
using CodexTokenMeter.Core.Formatting;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 浮层上直接显示的百分比与时长，在非默认区域下必须是 ASCII。
/// </summary>
/// <remarks>
/// <para>
/// 这些值与 <see cref="NumberFormatter"/> 分属不同层：
/// 它们在 UI 代码里就地插值（如 <c>$"{percent:F1}%"</c>），
/// 因此不受 <see cref="NumberFormatter"/> 的不变文化约束。
/// </para>
/// <para>
/// 实测确认的症状：阿拉伯语（ar-SA）区域下
/// <c>$"{1.2:F1}"</c> 输出 <c>١.٢</c>（本地数字），
/// 而 <c>$"{1.2:0.#}"</c> 输出 <c>1٫2</c>（U+066B 小数点）。
/// 两者都不是浮层期望的 ASCII 表现。
/// </para>
/// <para>
/// 本测试只验证格式化契约本身，不依赖 WPF；
/// 对应的修复是把这四处插值改为走 <see cref="NumberFormatter"/>。
/// </para>
/// </remarks>
public class OverlayValueFormattingTests
{
    private static readonly CultureInfo[] LocalizedCultures =
    [
        new("ar-SA"),
        new("fa-IR"),
        new("hi-IN"),
        new("th-TH"),
        new("de-DE"),
    ];

    /// <summary>
    /// 浮层上要显示的百分比文本。
    /// </summary>
    /// <remarks>
    /// 与 CapsulePresenter / OverlayWindow 中使用的规则一致，
    /// 修复后由这里统一提供。
    /// </remarks>
    private static string Percent(double value, int decimals) =>
        NumberFormatter.Percent(value, decimals);

    /// <summary>
    /// 浮层上要显示的时长文本。
    /// </summary>
    private static string Duration(TimeSpan value) => NumberFormatter.DurationLong(value);

    [Fact]
    public void PercentIsAsciiUnderAllLocalizedCultures()
    {
        foreach (var culture in LocalizedCultures)
        {
            var original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = culture;

                foreach (var decimals in new[] { 0, 1, 2 })
                {
                    var text = Percent(12.3456, decimals);

                    Assert.True(
                        text.All(ch => ch < 128),
                        $"{culture.Name} 下 Percent({decimals}) = \"{text}\"，含非 ASCII 字符。");

                    // 小数点必须是点号，不是区域特定的分隔符。
                    Assert.DoesNotContain('٫', text);
                    Assert.DoesNotContain('،', text);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }
    }

    [Fact]
    public void PercentUsesDotAsDecimalSeparator()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            // 德语用逗号作小数分隔符。
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("12.3%", Percent(12.3456, 1));
            Assert.Equal("0.0%", Percent(0.0, 1));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void DurationLongIsAsciiAndZeroPadded()
    {
        foreach (var culture in LocalizedCultures)
        {
            var original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = culture;

                Assert.Equal("03:07:05", Duration(new TimeSpan(3, 7, 5)));
                Assert.Equal("00:00:00", Duration(TimeSpan.Zero));
                Assert.Equal("13:42:59", Duration(new TimeSpan(13, 42, 59)));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }
    }

    [Fact]
    public void FixedKeepsRequestedPrecision()
    {
        // 通用格式化器不应像百分比那样限制位数：
        // 诊断输出里的费用需要 6 位小数才看得出变化。
        Assert.Equal("0.123457", NumberFormatter.Fixed(0.1234567, 6));
        Assert.Equal("0.12346", NumberFormatter.Fixed(0.1234567, 5));
        Assert.Equal("12", NumberFormatter.Fixed(12.4, 0));
    }

    [Fact]
    public void PercentLimitsPrecisionToTwoDecimals()
    {
        // 百分比超过两位小数没有展示价值，且会让胶囊宽度不稳定。
        Assert.Equal("12.35%", NumberFormatter.Percent(12.3456, 2));
        Assert.Equal("12.35%", NumberFormatter.Percent(12.3456, 6));
    }

    [Fact]
    public void FixedTreatsNonFiniteValuesAsZero()
    {
        // 诊断输出不应出现 NaN 或 ∞：那会打断日志的数值比对。
        Assert.Equal("0.00", NumberFormatter.Fixed(double.NaN, 2));
        Assert.Equal("0.00", NumberFormatter.Fixed(double.PositiveInfinity, 2));
        Assert.Equal("0.00", NumberFormatter.Fixed(double.NegativeInfinity, 2));
    }

    [Fact]
    public void NegativeDurationIsRenderedAsZero()
    {
        // 活跃时长为负不应出现在正常数据里，
        // 但格式化必须给出可读结果而不是负数或异常。
        Assert.Equal("00:00:00", Duration(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void PercentClampsOutOfRangeValues()
    {
        // 数据异常时百分比可能越界，显示层需要收敛到 0–100，
        // 避免出现 "无 12.3%" 这类无法解释的文本。
        Assert.Equal("100.0%", Percent(150, 1));
        Assert.Equal("0.0%", Percent(-20, 1));
    }
}
