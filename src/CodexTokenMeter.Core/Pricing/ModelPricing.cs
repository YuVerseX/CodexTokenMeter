namespace CodexTokenMeter.Core.Pricing;

/// <summary>
/// 单个模型的价格表，单位 USD / 1M tokens。
/// </summary>
/// <remarks>
/// 字段语义对齐 Sub2API 的 <c>ModelPricing</c>。基础价用于标准上下文档
/// 与 standard service tier。
/// </remarks>
public sealed record ModelPricing
{
    /// <summary>未命中缓存的输入单价。</summary>
    public required double Input { get; init; }

    public required double Output { get; init; }

    /// <summary>命中缓存的输入单价。通常远低于 <see cref="Input"/>。</summary>
    public required double CacheRead { get; init; }

    /// <summary>写入缓存的输入单价。OpenAI 语义下为输入的 1.25 倍。</summary>
    public required double CacheWrite { get; init; }

    /// <summary>
    /// Fast / priority 档的显式单价。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FastMultiplier"/> 互斥：Sub2API 在任一子项大于 0
    /// 且未配置 <see cref="FastMultiplier"/> 时使用显式价，此时不再叠加倍率。
    /// 某个子项缺失（为 0）时，该子项独立回落到基础价。
    /// </remarks>
    public PriorityPricing? Priority { get; init; }

    /// <summary>
    /// 渠道级 Fast 档倍率。
    /// </summary>
    /// <remarks>
    /// 一旦配置，即使存在显式 priority 价也改走倍率路径。
    /// 上游部署通常不设置，默认 null 表示按内建规则判断。
    /// </remarks>
    public double? FastMultiplier { get; init; }

    /// <summary>渠道级 flex 档倍率。默认 null 时按 0.5 处理。</summary>
    public double? FlexMultiplier { get; init; }

    /// <summary>
    /// 长上下文定价。为 null 表示该模型无长上下文加价。
    /// </summary>
    public LongContextPricing? LongContext { get; init; }
}

/// <summary>Fast / priority 档的显式单价，单位 USD / 1M tokens。</summary>
/// <remarks>
/// 与 <see cref="ModelPricing.FastMultiplier"/> 互斥。
/// 各子项为 0 表示该项未配置，此时该项回落基础价。
/// </remarks>
public sealed record PriorityPricing
{
    public double Input { get; init; }
    public double Output { get; init; }
    public double CacheRead { get; init; }
    public double CacheWrite { get; init; }
}

/// <summary>
/// 长上下文加价规则。
/// </summary>
/// <remarks>
/// Sub2API 把上游的「超过 N tokens 后的绝对价」统一折算成「阈值 + 倍率」，
/// 本类型沿用同一表达，以便与之逐行对照。
/// </remarks>
public sealed record LongContextPricing
{
    /// <summary>触发加价的输入总量阈值。</summary>
    public required long InputTokensAbove { get; init; }

    /// <summary>
    /// 输入侧倍率，作用于输入、缓存读、缓存写三项单价。
    /// </summary>
    /// <remarks>
    /// 取值 ≤1 视为未配置，按 1.0 处理。Sub2API 对 ≤0 显式归一到 1，
    /// 因为直接相乘会把对应分项算成免费。
    /// </remarks>
    public required double InputMultiplier { get; init; }

    /// <summary>输出侧倍率。取值 ≤1 视为未配置，按 1.0 处理。</summary>
    public required double OutputMultiplier { get; init; }

    /// <summary>
    /// 判定是否使用「达到即触发」。
    /// </summary>
    /// <remarks>
    /// 默认 false，即严格大于阈值才触发。Sub2API 仅对 xAI 系模型置为 true。
    /// </remarks>
    public bool ThresholdInclusive { get; init; }
}
