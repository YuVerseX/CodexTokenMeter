using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Pricing;

namespace CodexTokenMeter.Core.Tests;

public class CostCalculatorTests
{
    /// <summary>gpt-6-sol 的标准档价，与 Sub2API 内置价卡一致。</summary>
    private static ModelPricing Gpt6Sol => new()
    {
        Input = 2,
        Output = 10,
        CacheRead = 0.2,
        CacheWrite = 2.5,
        Priority = new PriorityPricing { Input = 4, Output = 20, CacheRead = 0.4, CacheWrite = 5 },
        LongContext = new LongContextPricing
        {
            InputTokensAbove = 272_000,
            InputMultiplier = 2,
            OutputMultiplier = 1.5,
        },
    };

    private static TokenUsage Usage(long input, long cached, long cacheWrite, long output) => new()
    {
        InputTokens = input,
        CachedInputTokens = cached,
        CacheWriteInputTokens = cacheWrite,
        OutputTokens = output,
        TotalTokens = input + output,
    };

    // ── 净输入减法：最容易搞错的一环 ──

    [Fact]
    public void Buckets_SubtractCacheFromTotalInput()
    {
        // 日志中的输入总量包含缓存部分。不拆分会导致缓存 token
        // 同时按普通输入价与缓存价重复计费。
        var buckets = BillingBuckets.From(Usage(input: 1000, cached: 800, cacheWrite: 0, output: 0));

        Assert.Equal(200, buckets.InputTokens);
        Assert.Equal(800, buckets.CacheReadTokens);
        Assert.Equal(0, buckets.CacheWriteTokens);
    }

    [Fact]
    public void Buckets_SubtractBothCacheReadAndCacheWrite()
    {
        var buckets = BillingBuckets.From(Usage(input: 1000, cached: 300, cacheWrite: 200, output: 0));

        Assert.Equal(500, buckets.InputTokens);
        Assert.Equal(300, buckets.CacheReadTokens);
        Assert.Equal(200, buckets.CacheWriteTokens);
    }

    [Fact]
    public void Buckets_ClampNetInputToZeroWhenCacheExceedsTotal()
    {
        // 异常数据：缓存量大于总量时净输入归零，而不是产生负数。
        var buckets = BillingBuckets.From(Usage(input: 100, cached: 500, cacheWrite: 0, output: 0));

        Assert.Equal(0, buckets.InputTokens);
        Assert.Equal(500, buckets.CacheReadTokens);
    }

    [Fact]
    public void Buckets_TotalInputTokensReconstructsOriginalTotal()
    {
        // 三桶相加必须还原出原始输入总量，这是减法正确的必要条件。
        var buckets = BillingBuckets.From(Usage(input: 1000, cached: 300, cacheWrite: 200, output: 0));

        Assert.Equal(1000, buckets.TotalInputTokens);
    }

    [Fact]
    public void Buckets_TreatNegativeCacheValuesAsZero()
    {
        var buckets = BillingBuckets.From(Usage(input: 1000, cached: -5, cacheWrite: -3, output: 0));

        Assert.Equal(1000, buckets.InputTokens);
        Assert.Equal(0, buckets.CacheReadTokens);
        Assert.Equal(0, buckets.CacheWriteTokens);
    }

    // ── 算例对照（子代理依据源码推导，已用上游官方公式交叉验证）──

    [Fact]
    public void Calculate_MatchesReferenceCaseForGpt6Sol()
    {
        // input=1000, cached=800, cacheWrite=0, output=500, 标准档
        // net=200 -> 200×2e-6 = 0.0004
        // output  -> 500×1e-5 = 0.005
        // cacheRead -> 800×2e-7 = 0.00016
        // 合计 0.00556
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 1000, cached: 800, cacheWrite: 0, output: 500));

        Assert.Equal(0.0004, cost.InputCost, 12);
        Assert.Equal(0.005, cost.OutputCost, 12);
        Assert.Equal(0.00016, cost.CacheReadCost, 12);
        Assert.Equal(0, cost.CacheWriteCost, 12);
        Assert.Equal(0.00556, cost.TotalCost, 12);
        Assert.False(cost.LongContextApplied);
    }

    [Fact]
    public void Calculate_PriorityTierUsesExplicitPricesWithoutStackingMultiplier()
    {
        // priority 档应使用显式档位价，且**不再**叠加 2 倍率。
        // 若叠加会得到 4 倍价——这是最易犯的错误。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 1000, cached: 800, cacheWrite: 0, output: 500),
            serviceTier: "priority");

        // net 200×4e-6 + 500×2e-5 + 800×4e-7 = 0.0008 + 0.01 + 0.00032
        Assert.Equal(0.0008, cost.InputCost, 12);
        Assert.Equal(0.01, cost.OutputCost, 12);
        Assert.Equal(0.00032, cost.CacheReadCost, 12);
        Assert.Equal(0.01112, cost.TotalCost, 12);
    }

    [Fact]
    public void Calculate_PriorityTierFallsBackPerFieldWhenPriorityPriceMissing()
    {
        // 逐字段独立回退：缺失的子项保留基础价，
        // 而不是整体按 0 处理（那会把该项算成免费）。
        var pricing = Gpt6Sol with
        {
            Priority = new PriorityPricing { Input = 4, Output = 0, CacheRead = 0, CacheWrite = 0 },
        };

        var cost = CostCalculator.Calculate(
            pricing,
            Usage(input: 1000, cached: 800, cacheWrite: 0, output: 500),
            serviceTier: "fast");

        Assert.Equal(0.0008, cost.InputCost, 12);   // 4e-6，来自 priority
        Assert.Equal(0.005, cost.OutputCost, 12);   // 1e-5，回落到基础价
        Assert.Equal(0.00016, cost.CacheReadCost, 12); // 2e-7，回落到基础价
    }

    // ── 长上下文：整次请求涨价 ──

    [Fact]
    public void Calculate_LongContextRaisesPricesForEntireRequest()
    {
        // 触发后**全部**输入按高价计，不是只有超出阈值的部分。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 300_000, cached: 0, cacheWrite: 0, output: 1000));

        Assert.True(cost.LongContextApplied);
        // 300000 × 4e-6 = 1.2（输入 ×2）
        Assert.Equal(1.2, cost.InputCost, 10);
        // 1000 × 1.5e-5 = 0.015（输出 ×1.5）
        Assert.Equal(0.015, cost.OutputCost, 10);
        Assert.Equal(1.215, cost.TotalCost, 10);
    }

    [Fact]
    public void Calculate_LongContextNotTriggeredAtExactlyThreshold()
    {
        // gpt-6-sol 使用严格大于：恰好等于阈值不涨价。
        var atThreshold = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 272_000, cached: 0, cacheWrite: 0, output: 0));

        Assert.False(atThreshold.LongContextApplied);
        Assert.Equal(272_000 * 2 / 1_000_000d, atThreshold.InputCost, 12);

        var aboveThreshold = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 272_001, cached: 0, cacheWrite: 0, output: 0));

        Assert.True(aboveThreshold.LongContextApplied);
    }

    [Fact]
    public void Calculate_LongContextThresholdInclusiveUsesGreaterOrEqual()
    {
        // xAI 系模型语义为「达到即触发」，需显式开启。
        var pricing = Gpt6Sol with
        {
            LongContext = new LongContextPricing
            {
                InputTokensAbove = 272_000,
                InputMultiplier = 2,
                OutputMultiplier = 1.5,
                ThresholdInclusive = true,
            },
        };

        var cost = CostCalculator.Calculate(
            pricing,
            Usage(input: 272_000, cached: 0, cacheWrite: 0, output: 0));

        Assert.True(cost.LongContextApplied);
    }

    [Fact]
    public void Calculate_LongContextCountsCacheTokensTowardThreshold()
    {
        // 判定用量是输入侧总量（净输入 + 缓存读 + 缓存写）。
        // 只用净输入判定会漏掉缓存部分，导致该涨价时不涨价。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 300_000, cached: 250_000, cacheWrite: 0, output: 0));

        Assert.True(cost.LongContextApplied);
    }

    [Fact]
    public void Calculate_LongContextScalesCacheReadByInputMultiplier()
    {
        // 缓存读跟随输入倍率。若漏乘，缓存命中越多漏计越多。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 300_000, cached: 100_000, cacheWrite: 0, output: 0));

        // 缓存读 100000 × (0.2×2)e-6 = 0.04
        Assert.Equal(0.04, cost.CacheReadCost, 10);
    }

    [Fact]
    public void Calculate_LongContextScalesCacheWriteByInputMultiplier()
    {
        // 缓存写同样跟随输入倍率。它的单价独立取值，容易漏乘。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 300_000, cached: 0, cacheWrite: 50_000, output: 0));

        // 缓存写 50000 × 2.5 × 2e-6 = 0.25
        Assert.Equal(0.25, cost.CacheWriteCost, 10);
    }

    [Fact]
    public void Calculate_LongContextMultiplierBelowOneIsTreatedAsUnconfigured()
    {
        // 倍率 ≤1 视为未配置并按 1.0 处理。
        // 若直接相乘，倍率为 0 时会把该分项算成免费。
        var pricing = Gpt6Sol with
        {
            LongContext = new LongContextPricing
            {
                InputTokensAbove = 1000,
                InputMultiplier = 0,
                OutputMultiplier = 3,
            },
        };

        var cost = CostCalculator.Calculate(pricing, Usage(input: 2000, cached: 0, cacheWrite: 0, output: 100));

        Assert.True(cost.LongContextApplied);
        Assert.Equal(2000 * 2 / 1_000_000d, cost.InputCost, 12);   // 倍率 0 -> 1.0
        Assert.Equal(100 * 10 * 3 / 1_000_000d, cost.OutputCost, 12); // 倍率 3 生效
    }

    [Fact]
    public void Calculate_LongContextSkippedWhenBothMultipliersUnconfigured()
    {
        var pricing = Gpt6Sol with
        {
            LongContext = new LongContextPricing
            {
                InputTokensAbove = 1000,
                InputMultiplier = 1,
                OutputMultiplier = 1,
            },
        };

        var cost = CostCalculator.Calculate(pricing, Usage(input: 5000, cached: 0, cacheWrite: 0, output: 0));

        Assert.False(cost.LongContextApplied);
    }

    // ── service tier 其它档位 ──

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("", 1.0)]
    [InlineData("default", 1.0)]
    [InlineData("standard", 1.0)]
    [InlineData("unknown-tier", 1.0)]
    public void ConfiguredTierMultiplier_DefaultAndUnknownTiersAreOne(string? tier, double expected)
    {
        var normalized = CostCalculator.NormalizeServiceTier(tier);
        Assert.Equal(expected, CostCalculator.ConfiguredTierMultiplier(normalized, Gpt6Sol));
    }

    [Fact]
    public void Calculate_FlexTierUsesHalfMultiplierNotExplicitPrices()
    {
        // flex 永远走倍率，不会使用显式 priority 价。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 1000, cached: 0, cacheWrite: 0, output: 0),
            serviceTier: "flex");

        Assert.Equal(1000 * 2 * 0.5 / 1_000_000d, cost.InputCost, 12);
    }

    [Fact]
    public void Calculate_ChannelFastMultiplierOverridesExplicitPriorityPrices()
    {
        // 配置了渠道级 FastMultiplier 时，改走倍率路径。
        var pricing = Gpt6Sol with { FastMultiplier = 3.0 };

        var cost = CostCalculator.Calculate(
            pricing,
            Usage(input: 1000, cached: 0, cacheWrite: 0, output: 0),
            serviceTier: "fast");

        Assert.Equal(1000 * 2 * 3.0 / 1_000_000d, cost.InputCost, 12);
    }

    [Fact]
    public void Calculate_TierMultiplierAppliesOnTopOfLongContext()
    {
        // 长上下文先改单价，tier 倍率再作用于分项费用。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 300_000, cached: 0, cacheWrite: 0, output: 0),
            serviceTier: "flex");

        // 300000 × (2×2) × 0.5 = 0.6
        Assert.Equal(0.6, cost.InputCost, 10);
    }

    [Fact]
    public void Calculate_ServiceTierIsCaseAndWhitespaceInsensitive()
    {
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 1000, cached: 0, cacheWrite: 0, output: 0),
            serviceTier: "  FLEX  ");

        Assert.Equal(1000 * 2 * 0.5 / 1_000_000d, cost.InputCost, 12);
    }

    // ── 分组倍率 ──

    [Fact]
    public void Calculate_AppliesRateMultiplierToTotal()
    {
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 1000, cached: 800, cacheWrite: 0, output: 500),
            rateMultiplier: 1.5);

        Assert.Equal(0.00556 * 1.5, cost.TotalCost, 12);
        Assert.Equal(0.00556, cost.TotalCostBeforeMultiplier, 12);
    }

    // ── 舍入 ──

    [Theory]
    [InlineData(0.00556, 0.00556)]
    [InlineData(0.123456789, 0.12345679)]
    [InlineData(0.123456785, 0.12345679)]
    [InlineData(1.000000005, 1.00000001)]
    [InlineData(0, 0)]
    public void Quantize_RoundsToEightDecimalsAwayFromZero(double input, double expected)
    {
        Assert.Equal(expected, CostCalculator.Quantize(input), 12);
    }

    [Fact]
    public void Quantize_IsSymmetricForNegativeAmounts()
    {
        Assert.Equal(-CostCalculator.Quantize(0.123456789), CostCalculator.Quantize(-0.123456789), 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Quantize_PassesThroughNonFiniteValues(double value)
    {
        var result = CostCalculator.Quantize(value);
        Assert.Equal(value, result);
    }

    [Fact]
    public void Calculate_DoesNotRoundIntermediateTerms()
    {
        // 全程不做中间舍入，避免分项误差累积。
        var cost = CostCalculator.Calculate(
            Gpt6Sol,
            Usage(input: 33_333, cached: 11_111, cacheWrite: 0, output: 777));

        var expected = (33_333 - 11_111) * 2 / 1_000_000d
            + 777 * 10 / 1_000_000d
            + 11_111 * 0.2 / 1_000_000d;

        Assert.Equal(expected, cost.TotalCost, 15);
    }
}
