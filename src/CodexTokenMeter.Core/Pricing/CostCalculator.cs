using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Pricing;

/// <summary>一次调用中四个互斥计费桶的 token 数量。</summary>
/// <remarks>
/// 语义与 Sub2API 的 <c>UsageTokens</c> 一致：<see cref="InputTokens"/> 是
/// **净输入**（总量减去缓存读与缓存写），四个桶互不重叠。
/// </remarks>
public readonly record struct BillingBuckets
{
    /// <summary>按普通输入价计费的 token 数。</summary>
    public long InputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    public long OutputTokens { get; init; }

    /// <summary>输入侧总量，用于长上下文判定。等于最初的总输入。</summary>
    public long TotalInputTokens
    {
        get
        {
            var input = Math.Max(0, InputTokens);
            var read = Math.Max(0, CacheReadTokens);
            var write = Math.Max(0, CacheWriteTokens);
            return input > long.MaxValue - read || input + read > long.MaxValue - write
                ? long.MaxValue
                : input + read + write;
        }
    }

    /// <summary>
    /// 把总量口径的用量拆成互斥桶。
    /// </summary>
    /// <remarks>
    /// 这是**唯一一次**减法。日志中的输入总量包含缓存部分，若不拆分，
    /// 缓存 token 会同时按普通输入价和缓存价重复计费。
    /// </remarks>
    public static BillingBuckets From(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var input = Math.Max(0, usage.InputTokens);
        var cacheRead = Math.Clamp(usage.CachedInputTokens, 0, input);
        var cacheWrite = Math.Clamp(usage.CacheWriteInputTokens, 0, input - cacheRead);
        var netInput = input - cacheRead - cacheWrite;

        return new BillingBuckets
        {
            InputTokens = netInput,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            OutputTokens = Math.Max(0, usage.OutputTokens),
        };
    }
}

/// <summary>各分项费用与合计，单位 USD。</summary>
public sealed record CostBreakdown
{
    public required double InputCost { get; init; }
    public required double OutputCost { get; init; }
    public required double CacheReadCost { get; init; }
    public required double CacheWriteCost { get; init; }

    public required double TotalCost { get; init; }

    /// <summary>是否触发了长上下文加价。</summary>
    public bool LongContextApplied { get; init; }

    /// <summary>未应用分组倍率时的费用，用于对照。</summary>
    public double TotalCostBeforeMultiplier { get; init; }

    public static CostBreakdown Zero { get; } = new()
    {
        InputCost = 0,
        OutputCost = 0,
        CacheReadCost = 0,
        CacheWriteCost = 0,
        TotalCost = 0,
    };
}

/// <summary>
/// 按 Sub2API 的算法计算单次调用费用。
/// </summary>
/// <remarks>
/// 实现严格对齐 Sub2API 的 <c>computeTokenBreakdown</c> 与
/// <c>calculateTokenCost</c>。关键语义：
/// <list type="number">
/// <item>输入先拆成互斥三桶，只做一次减法。</item>
/// <item>长上下文触发后**整次请求**改用高价，倍率乘在单价上。</item>
/// <item>长上下文输入倍率同时作用于缓存读与缓存写。</item>
/// <item>显式 priority 价与 tier 倍率互斥，不叠加。</item>
/// <item>全程不做舍入，只在最终结果上量化。</item>
/// </list>
/// </remarks>
public static class CostCalculator
{
    /// <summary>
    /// 金额的规范小数位数，对齐 Sub2API 的 <c>UsageBillingMonetaryScale</c>，
    /// 对应数据库中的 NUMERIC(20,8)。
    /// </summary>
    public const int MonetaryScale = 8;

    /// <summary>
    /// 计算一次调用的费用。
    /// </summary>
    /// <param name="pricing">该模型的价格表。</param>
    /// <param name="usage">该次调用的用量，输入为总量口径。</param>
    /// <param name="serviceTier">服务档位，取自日志。null 或 default 表示标准档。</param>
    /// <param name="rateMultiplier">
    /// 分组倍率。上游站点在此之上叠加自己的定价倍率，
    /// 该值无法从本地数据推断，需用户提供。
    /// </param>
    public static CostBreakdown Calculate(
        ModelPricing pricing,
        TokenUsage usage,
        string? serviceTier = null,
        double rateMultiplier = 1.0)
    {
        ArgumentNullException.ThrowIfNull(pricing);

        if (!IsValidPricing(pricing))
        {
            throw new ArgumentOutOfRangeException(nameof(pricing), "价格必须是有效的非负有限数。");
        }

        if (!double.IsFinite(rateMultiplier) || rateMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rateMultiplier), "倍率必须是有效的正有限数。");
        }

        var buckets = BillingBuckets.From(usage);
        var tier = NormalizeServiceTier(serviceTier);

        // ── service tier 决定用显式档位价还是倍率，二者互斥 ──
        var tierMultiplier = 1.0;
        var inputPrice = pricing.Input;
        var outputPrice = pricing.Output;
        var cacheReadPrice = pricing.CacheRead;
        var cacheWritePrice = pricing.CacheWrite;

        if (UseExplicitPriorityPricing(tier, pricing))
        {
            var priority = pricing.Priority!;
            // 逐字段独立回退：该子项缺失时保留基础价。
            if (priority.Input > 0)
            {
                inputPrice = priority.Input;
            }

            if (priority.Output > 0)
            {
                outputPrice = priority.Output;
            }

            if (priority.CacheRead > 0)
            {
                cacheReadPrice = priority.CacheRead;
            }

            if (priority.CacheWrite > 0)
            {
                cacheWritePrice = priority.CacheWrite;
            }
        }
        else
        {
            tierMultiplier = ConfiguredTierMultiplier(tier, pricing);
        }

        // ── 长上下文：倍率乘在单价上，作用于整次请求 ──
        var longContextApplied = false;
        var cacheWriteMultiplier = 1.0;

        if (ShouldApplyLongContext(buckets, pricing.LongContext))
        {
            longContextApplied = true;
            var longContext = pricing.LongContext!;
            var inputMultiplier = MultiplierOrOne(longContext.InputMultiplier);
            var outputMultiplier = MultiplierOrOne(longContext.OutputMultiplier);

            inputPrice *= inputMultiplier;
            outputPrice *= outputMultiplier;
            // 缓存读是输入侧的复用，跟随输入倍率；否则缓存命中越多漏计越多。
            cacheReadPrice *= inputMultiplier;
            // 缓存写同样属于输入侧，但它的价格在下面的分项计算中单独取用，
            // 因此通过独立倍率下传，避免漏乘。
            cacheWriteMultiplier = inputMultiplier;
        }

        // ── 四个分项 ──
        var inputCost = buckets.InputTokens * inputPrice / 1_000_000d;
        var outputCost = buckets.OutputTokens * outputPrice / 1_000_000d;
        var cacheReadCost = buckets.CacheReadTokens * cacheReadPrice / 1_000_000d;
        var cacheWriteCost = buckets.CacheWriteTokens * cacheWritePrice * cacheWriteMultiplier / 1_000_000d;

        // ── tier 倍率作用于各分项 ──
        if (tierMultiplier != 1.0)
        {
            inputCost *= tierMultiplier;
            outputCost *= tierMultiplier;
            cacheReadCost *= tierMultiplier;
            cacheWriteCost *= tierMultiplier;
        }

        var total = inputCost + outputCost + cacheReadCost + cacheWriteCost;
        var beforeMultiplier = total;

        if (rateMultiplier != 1.0)
        {
            total *= rateMultiplier;
        }

        if (!double.IsFinite(inputCost) || !double.IsFinite(outputCost)
            || !double.IsFinite(cacheReadCost) || !double.IsFinite(cacheWriteCost)
            || !double.IsFinite(total))
        {
            throw new OverflowException("费用超出可表示范围。");
        }

        return new CostBreakdown
        {
            InputCost = inputCost,
            OutputCost = outputCost,
            CacheReadCost = cacheReadCost,
            CacheWriteCost = cacheWriteCost,
            TotalCost = total,
            LongContextApplied = longContextApplied,
            TotalCostBeforeMultiplier = beforeMultiplier,
        };
    }

    /// <summary>
    /// 判断长上下文加价是否触发。
    /// </summary>
    /// <remarks>
    /// 判定用量为**输入侧总量**，不含输出。
    /// </remarks>
    public static bool ShouldApplyLongContext(BillingBuckets buckets, LongContextPricing? longContext)
    {
        if (longContext is null || longContext.InputTokensAbove <= 0)
        {
            return false;
        }

        // 两侧倍率都不大于 1 表示未配置加价。
        if (longContext.InputMultiplier <= 1 && longContext.OutputMultiplier <= 1)
        {
            return false;
        }

        var total = buckets.TotalInputTokens;

        return longContext.ThresholdInclusive
            ? total >= longContext.InputTokensAbove
            : total > longContext.InputTokensAbove;
    }

    /// <summary>
    /// 把金额量化到 <see cref="MonetaryScale"/> 位小数。
    /// </summary>
    /// <remarks>
    /// 与 Sub2API 的 <c>QuantizeUsageBillingAmount</c> 一致：
    /// 中点远离零，非有限值与零原样返回。
    /// 计算过程不做舍入；对账需要固定 8 位小数时使用本方法。
    /// </remarks>
    public static double Quantize(double amount)
    {
        if (amount == 0 || double.IsNaN(amount) || double.IsInfinity(amount))
        {
            return amount;
        }

        var scale = Math.Pow(10, MonetaryScale);
        var scaled = amount * scale;

        if (!double.IsFinite(scaled))
        {
            return amount;
        }

        // 中点远离零，与 decimal.Round(..., MidpointRounding.AwayFromZero) 等价。
        var rounded = scaled >= 0
            ? Math.Floor(scaled + 0.5)
            : Math.Ceiling(scaled - 0.5);

        return rounded / scale;
    }

    /// <summary>档位名归一化：小写去空白。</summary>
    internal static string NormalizeServiceTier(string? serviceTier) =>
        (serviceTier ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// 是否使用显式 priority 档位价。
    /// </summary>
    /// <remarks>
    /// 仅 priority 与 fast 两个档位可能走显式价；flex 永远走倍率。
    /// 配置了渠道级 <see cref="ModelPricing.FastMultiplier"/> 时也改走倍率。
    /// </remarks>
    internal static bool UseExplicitPriorityPricing(string tier, ModelPricing pricing)
    {
        if (tier is not ("priority" or "fast"))
        {
            return false;
        }

        if (pricing.FastMultiplier is not null)
        {
            return false;
        }

        var priority = pricing.Priority;
        return priority is not null
            && (priority.Input > 0
                || priority.Output > 0
                || priority.CacheRead > 0
                || priority.CacheWrite > 0);
    }

    /// <summary>
    /// 取得 tier 倍率。
    /// </summary>
    /// <remarks>
    /// 与 Sub2API 的 <c>configuredServiceTierMultiplier</c> 一致：
    /// 渠道显式倍率优先，否则按档位内置规则。
    /// </remarks>
    internal static double ConfiguredTierMultiplier(string tier, ModelPricing pricing)
    {
        switch (tier)
        {
            case "priority":
            case "fast":
                return pricing.FastMultiplier ?? 2.0;
            case "flex":
                return pricing.FlexMultiplier ?? 0.5;
            case "ultrafast":
                return 2.0;
            default:
                return 1.0;
        }
    }

    /// <summary>倍率 ≤1 视为未配置，按 1.0 处理。</summary>
    internal static double MultiplierOrOne(double value) => value > 1 ? value : 1.0;

    private static bool IsValidPricing(ModelPricing pricing)
    {
        static bool Price(double value) => double.IsFinite(value) && value >= 0;
        static bool Multiplier(double? value) => value is null || double.IsFinite(value.Value) && value.Value > 0;

        var priority = pricing.Priority;
        var longContext = pricing.LongContext;
        return Price(pricing.Input) && Price(pricing.Output)
            && Price(pricing.CacheRead) && Price(pricing.CacheWrite)
            && (priority is null || Price(priority.Input) && Price(priority.Output)
                && Price(priority.CacheRead) && Price(priority.CacheWrite))
            && Multiplier(pricing.FastMultiplier) && Multiplier(pricing.FlexMultiplier)
            && (longContext is null || longContext.InputTokensAbove > 0
                && Price(longContext.InputMultiplier) && Price(longContext.OutputMultiplier));
    }
}
