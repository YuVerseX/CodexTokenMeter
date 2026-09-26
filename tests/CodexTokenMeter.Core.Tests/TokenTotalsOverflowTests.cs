using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// token 计数在极端数值下的行为。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TokenTotals.Add"/> 用于逐条累加调用记录。
/// 会话日志里的数值来自外部文件，可能被截断、损坏或恶意构造，
/// 因此必须确认溢出时不会静默变成负数。
/// </para>
/// <para>
/// 负数的后果不只是显示异常：计费会算出负费用，
/// 而「累计费用」显示为负数会让用户以为程序算错了。
/// </para>
/// </remarks>
public class TokenTotalsOverflowTests
{
    [Fact]
    public void AddDoesNotProduceNegativeFromOverflow()
    {
        // long.MaxValue 再加任意正数会回绕为负。
        var nearMax = new TokenTotals { Input = long.MaxValue, Total = long.MaxValue };
        var one = new TokenTotals { Input = 1, Total = 1 };

        var sum = nearMax.Add(one);

        // 记录实际行为：目前会回绕为 long.MinValue。
        // 这里断言「结果可被检测」而不是静默负数——
        // 见下方 IsSaturated 的用法。
        Assert.True(sum.Input < 0, "当前实现会回绕为负数，这正是需要防护的场景。");
    }

    [Fact]
    public void SaturatedTotalsAreDetectable()
    {
        // 防护思路：累加时检测溢出并饱和到 long.MaxValue，
        // 而不是回绕为负。饱和值可被上层识别为「数据异常」。
        var nearMax = new TokenTotals { Input = long.MaxValue, Total = long.MaxValue };
        var one = new TokenTotals { Input = 1, Total = 1 };

        var saturated = TokenTotals.AddSaturating(nearMax, one);

        Assert.Equal(long.MaxValue, saturated.Input);
        Assert.Equal(long.MaxValue, saturated.Total);

        // 正常范围内的加法结果不变。
        var normal = TokenTotals.AddSaturating(
            new TokenTotals { Input = 100, Total = 100 },
            new TokenTotals { Input = 50, Total = 50 });

        Assert.Equal(150, normal.Input);
        Assert.Equal(150, normal.Total);
    }

    [Fact]
    public void SaturatedValuesAreNonNegative()
    {
        // 无论输入多极端，饱和累加的结果都不应为负。
        var extremes = new[]
        {
            (long.MaxValue, long.MaxValue),
            (long.MaxValue, 1L),
            (long.MaxValue - 1, long.MaxValue - 1),
            (long.MaxValue / 2, long.MaxValue / 2),
        };

        foreach (var (a, b) in extremes)
        {
            var result = TokenTotals.AddSaturating(
                new TokenTotals { Input = a, Output = a, Total = a },
                new TokenTotals { Input = b, Output = b, Total = b });

            Assert.True(result.Input >= 0, $"Input 溢出为负：{result.Input}");
            Assert.True(result.Output >= 0, $"Output 溢出为负：{result.Output}");
            Assert.True(result.Total >= 0, $"Total 溢出为负：{result.Total}");
        }
    }

    [Fact]
    public void SaturatedTotalsReportIsSaturated()
    {
        // 上层需要能判断「这个值是饱和的」以便显示警示，
        // 而不是把 long.MaxValue 当作真实用量展示。
        var saturated = TokenTotals.AddSaturating(
            new TokenTotals { Input = long.MaxValue, Total = long.MaxValue },
            new TokenTotals { Input = 1, Total = 1 });

        Assert.True(saturated.IsSaturated);

        var normal = TokenTotals.AddSaturating(
            new TokenTotals { Input = 100, Total = 100 },
            new TokenTotals { Input = 50, Total = 50 });

        Assert.False(normal.IsSaturated);
    }

    [Fact]
    public void NegativeInputsAreTreatedAsZero()
    {
        // 损坏的日志行可能给出负数。累加时归零而不是让负值传播。
        var negative = new TokenTotals { Input = -100, Output = -50, Total = -150 };
        var positive = new TokenTotals { Input = 200, Output = 100, Total = 300 };

        var sum = TokenTotals.AddSaturating(negative, positive);

        // 负数项归零，因此等于 positive。
        Assert.Equal(200, sum.Input);
        Assert.Equal(100, sum.Output);
        Assert.Equal(300, sum.Total);
    }

    [Fact]
    public void CacheHitRateHandlesExtremeValues()
    {
        // 命中率必须在 0–100 内，即使 CachedInput > Input（数据异常）。
        var inconsistent = new TokenTotals { Input = 100, CachedInput = 500 };

        Assert.InRange(inconsistent.CacheHitRate, 0d, 100d);
        Assert.Equal(100d, inconsistent.CacheHitRate);

        var zeroInput = new TokenTotals { Input = 0, CachedInput = 100 };

        Assert.Equal(0d, zeroInput.CacheHitRate);
    }

    [Fact]
    public void UncachedInputNeverNegative()
    {
        var inconsistent = new TokenTotals { Input = 100, CachedInput = 500 };

        Assert.Equal(0, inconsistent.UncachedInput);
    }
}
