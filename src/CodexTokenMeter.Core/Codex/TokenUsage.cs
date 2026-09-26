namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 单次模型调用的 token 用量。
/// 这是计费的权威依据：每次调用都真实产生费用，与上下文是否被压缩无关。
/// </summary>
public sealed record TokenUsage
{
    /// <summary>输入 token 总数，含缓存命中和缓存写入部分。</summary>
    public long InputTokens { get; init; }

    /// <summary>输入中命中缓存的部分，单价远低于普通输入。</summary>
    public long CachedInputTokens { get; init; }

    /// <summary>写入缓存的部分。Codex 目前恒为 0，保留以兼容后续变化。</summary>
    public long CacheWriteInputTokens { get; init; }

    public long OutputTokens { get; init; }

    /// <summary>输出中属于推理过程的部分，是 OutputTokens 的子集。</summary>
    public long ReasoningOutputTokens { get; init; }

    /// <summary>该次调用的输入与输出之和。</summary>
    public long TotalTokens { get; init; }

    public static TokenUsage Empty { get; } = new();
}

/// <summary>
/// 一条 <c>token_usage_record</c>，对应一次具体的模型调用。
/// </summary>
public sealed record UsageRecord
{
    /// <summary>事件时间戳。日志缺字段时为 null，而不是 0001-01-01。</summary>
    public DateTimeOffset? Timestamp { get; init; }

    public required string TurnId { get; init; }
    public string? ResponseId { get; init; }
    public required TokenUsage Usage { get; init; }

    /// <summary>该 turn 从开始到本次调用的累计用量。用于在不重算前缀的情况下求本轮花费。</summary>
    public TokenUsage? TurnTokenUsage { get; init; }
}

/// <summary>
/// 一条 <c>token_count</c> 事件。
/// 与 <see cref="UsageRecord"/> 的关键区别：上下文压缩会重置此处的 last 用量，
/// 因此它反映的是「当前上下文占用」，而不是「本次调用消耗」。
/// </summary>
public sealed record TokenCountSnapshot
{
    /// <summary>事件时间戳。日志缺字段时为 null，而不是 0001-01-01。</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>压缩后的当前上下文占用。压缩发生时该值会骤降。</summary>
    public required TokenUsage LastUsage { get; init; }

    /// <summary>会话累计值。注意：压缩会使其小于 UsageRecord 的真实求和。</summary>
    public required TokenUsage TotalUsage { get; init; }

    /// <summary>模型上下文窗口容量，用于计算占用百分比。</summary>
    public long ModelContextWindow { get; init; }
}

/// <summary>
/// 单个 turn 的元信息，来自 <c>turn_context</c>。
/// </summary>
public sealed record TurnContext
{
    public required string TurnId { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? WorkingDirectory { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}
