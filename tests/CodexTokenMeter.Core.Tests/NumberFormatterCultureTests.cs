using System.Globalization;
using CodexTokenMeter.Core.Formatting;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 数值格式化在非默认区域下的行为。
/// </summary>
/// <remarks>
/// <para>
/// 浮层上的数字必须始终是可读的 ASCII 数字。
/// 阿拉伯语、印地语等区域使用本地数字字形
/// （<c>١٢٣</c> / <c>१२३</c>），不带 <see cref="CultureInfo"/> 的格式化
/// 会输出这些字形——在浮层的等宽字体下显示为无法识别的方块或异体字。
/// </para>
/// <para>
/// 回归保护：<see cref="NumberFormatter.Duration"/> 曾使用
/// <c>$"{(int)duration.TotalHours}h{duration.Minutes:D2}m"</c>，
/// 插值中的数字会按当前区域格式化。
/// </para>
/// </remarks>
public class NumberFormatterCultureTests
{
    /// <summary>使用本地数字字形的区域。</summary>
    private static readonly CultureInfo ArabicCulture = new("ar-SA");

    /// <summary>使用天城文数字的区域。</summary>
    private static readonly CultureInfo HindiCulture = new("hi-IN");

    private static void UnderCulture(CultureInfo culture, Action action)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = culture;
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void DurationIsAsciiUnderArabicCulture()
    {
        var duration = new TimeSpan(hours: 3, minutes: 7, seconds: 5);

        UnderCulture(ArabicCulture, () =>
        {
            var text = NumberFormatter.Duration(duration);

            Assert.Equal("3h07m", text);
            Assert.All(text, ch => Assert.True(
                ch < 128,
                $"输出包含非 ASCII 字符：'{ch}'（U+{(int)ch:X4}），完整输出 \"{text}\""));
        });
    }

    [Fact]
    public void DurationLongIsAsciiUnderArabicCulture()
    {
        var duration = new TimeSpan(hours: 13, minutes: 42, seconds: 59);

        UnderCulture(ArabicCulture, () =>
        {
            var text = NumberFormatter.DurationLong(duration);

            Assert.Equal("13:42:59", text);
            Assert.All(text, ch => Assert.True(ch < 128, $"非 ASCII 字符：'{ch}'"));
        });
    }

    [Fact]
    public void DurationIsAsciiUnderHindiCulture()
    {
        var duration = new TimeSpan(hours: 2, minutes: 9, seconds: 8);

        UnderCulture(HindiCulture, () =>
        {
            var text = NumberFormatter.Duration(duration);

            Assert.Equal("2h09m", text);
            Assert.All(text, ch => Assert.True(ch < 128, $"非 ASCII 字符：'{ch}'"));
        });
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("hi-IN")]
    [InlineData("fa-IR")]
    [InlineData("th-TH")]
    public void AllFormattersAreAsciiUnderLocalizedCultures(string cultureName)
    {
        var culture = new CultureInfo(cultureName);

        UnderCulture(culture, () =>
        {
            var outputs = new Dictionary<string, string>
            {
                ["Compact"] = NumberFormatter.Compact(1_234_567),
                ["Full"] = NumberFormatter.Full(1_234_567),
                ["Count(int)"] = NumberFormatter.Count(42),
                ["Count(long)"] = NumberFormatter.Count(42L),
                ["Cost(0.5)"] = NumberFormatter.Cost(0.5),
                ["Cost(1.5)"] = NumberFormatter.Cost(1.5),
                ["Cost(150)"] = NumberFormatter.Cost(150),
                ["ContextWindow"] = NumberFormatter.ContextWindow(258_400),
                ["Duration"] = NumberFormatter.Duration(TimeSpan.FromMinutes(7)),
                ["DurationLong"] = NumberFormatter.DurationLong(TimeSpan.FromMinutes(7)),
            };

            foreach (var (name, text) in outputs)
            {
                Assert.True(
                    text.All(ch => ch < 128),
                    $"{cultureName} 下 {name} 输出 \"{text}\"，含非 ASCII 字符。");
            }
        });
    }

    [Fact]
    public void CostUsesDotAsDecimalSeparatorUnderCommaCulture()
    {
        // 德语等区域用逗号作小数分隔符。
        // 费用显示成 "1,500" 会被读成一千五百，而不是 1.5。
        var culture = new CultureInfo("de-DE");

        UnderCulture(culture, () =>
        {
            Assert.Equal("1.500", NumberFormatter.Cost(1.5));
            Assert.Equal("0.5000", NumberFormatter.Cost(0.5));
        });
    }

    [Fact]
    public void FullUsesCommaAsThousandsSeparatorRegardlessOfCulture()
    {
        // 千分位始终用逗号：这是浮层的既定风格，
        // 不应随用户区域而变成点号或空格。
        UnderCulture(new CultureInfo("de-DE"), () =>
        {
            Assert.Equal("1,234,567", NumberFormatter.Full(1_234_567));
        });
    }
}
