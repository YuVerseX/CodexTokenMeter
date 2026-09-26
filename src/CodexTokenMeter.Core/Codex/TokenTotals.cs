namespace CodexTokenMeter.Core.Codex;

/// <summary>一组 token 计数，便于在解析、指标与计费之间传递。</summary>
/// <remarks>
/// 放在 Codex 命名空间而非 Metrics：会话快照需要携带它，
/// 而 Metrics 依赖 Codex，反向引用会形成循环依赖。
/// </remarks>
public readonly record struct TokenTotals
{
    /// <summary>输入总量，含缓存命中与缓存写入部分。</summary>
    public long Input { get; init; }

    /// <summary>输入中命中缓存的部分，是 <see cref="Input"/> 的子集。</summary>
    public long CachedInput { get; init; }

    /// <summary>输入中写入缓存的部分，是 <see cref="Input"/> 的子集。</summary>
    public long CacheWriteInput { get; init; }

    public long Output { get; init; }

    /// <summary>输出中属于推理过程的部分，是 <see cref="Output"/> 的子集。</summary>
    public long ReasoningOutput { get; init; }

    /// <summary>该次调用的输入与输出之和。</summary>
    public long Total { get; init; }

    /// <summary>由单次用量转换。</summary>
    public static TokenTotals From(TokenUsage usage) => new()
    {
        Input = usage.InputTokens,
        CachedInput = usage.CachedInputTokens,
        CacheWriteInput = usage.CacheWriteInputTokens,
        Output = usage.OutputTokens,
        ReasoningOutput = usage.ReasoningOutputTokens,
        Total = usage.TotalTokens,
    };

    /// <summary>逐项相加。用于增量累加，避免每次都重新遍历全部记录。</summary>
    public TokenTotals Add(TokenTotals other) => new()
    {
        Input = Input + other.Input,
        CachedInput = CachedInput + other.CachedInput,
        CacheWriteInput = CacheWriteInput + other.CacheWriteInput,
        Output = Output + other.Output,
        ReasoningOutput = ReasoningOutput + other.ReasoningOutput,
        Total = Total + other.Total,
    };

    /// <summary>
    /// 未命中缓存的输入部分。
    /// </summary>
    /// <remarks>
    /// 下限取 0：异常数据可能出现缓存量大于输入量的情况，
    /// 此时归零而不是产生负值。
    /// </remarks>
    public long UncachedInput => Math.Max(0, Input - CachedInput);

    /// <summary>
    /// 缓存命中率，取值 0–100。
    /// </summary>
    /// <remarks>
    /// 分母用 <see cref="Input"/>，即含缓存读与缓存写的总输入，
    /// 与 pi-web 的口径一致。Codex 日志中缓存写入恒为 0，
    /// 因此当前等价于 cached / input。
    /// </remarks>
    public double CacheHitRate =>
        Input <= 0 ? 0d : Math.Clamp((double)CachedInput * 100d / Input, 0d, 100d);
}
