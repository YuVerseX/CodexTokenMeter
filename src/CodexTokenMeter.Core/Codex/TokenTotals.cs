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

    /// <summary>
    /// 逐项相加，负数归零且溢出时饱和。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 会话日志来自外部文件，可能被截断、损坏或构造。
    /// 未经防护的加法在两个方向上都不可靠：
    /// 负数会传播（使累计值变小甚至为负），
    /// 而超过 <see cref="long.MaxValue"/> 会回绕为负数。
    /// </para>
    /// <para>
    /// 负的累计值会让费用算出负数，用户会认为程序算错了。
    /// 饱和后值偏大，但偏大的含义是“统计异常”，而负值没有含义。
    /// 调用方可用 <see cref="IsSaturated"/> 判断并提示。
    /// </para>
    /// </remarks>
    public static TokenTotals AddSaturating(TokenTotals left, TokenTotals right) => new()
    {
        Input = Saturate(left.Input, right.Input),
        CachedInput = Saturate(left.CachedInput, right.CachedInput),
        CacheWriteInput = Saturate(left.CacheWriteInput, right.CacheWriteInput),
        Output = Saturate(left.Output, right.Output),
        ReasoningOutput = Saturate(left.ReasoningOutput, right.ReasoningOutput),
        Total = Saturate(left.Total, right.Total),
    };

    /// <summary>
    /// 任一字段已达饱和值。
    /// </summary>
    /// <remarks>
    /// 用于把「统计异常」与「用量真的很大」区分开：
    /// <see cref="long.MaxValue"/> 作为 token 数是不可能的。
    /// </remarks>
    public bool IsSaturated =>
        Input == long.MaxValue
        || CachedInput == long.MaxValue
        || CacheWriteInput == long.MaxValue
        || Output == long.MaxValue
        || ReasoningOutput == long.MaxValue
        || Total == long.MaxValue;

    /// <summary>
    /// 相加两个计数：负数归零，溢出饱和。
    /// </summary>
    private static long Saturate(long left, long right)
    {
        var a = Math.Max(0, left);
        var b = Math.Max(0, right);

        // 用减法判断是否溢出，避免 checked 块的异常开销：
        // 本方法在逐条累加的热路径上被调用。
        return a > long.MaxValue - b ? long.MaxValue : a + b;
    }

    /// <summary>
    /// 未命中缓存的输入部分。
    /// </summary>
    /// <remarks>
    /// 下限取 0：异常数据可能出现缓存量大于输入量的情况，
    /// 此时归零而不是产生负值。
    /// </remarks>
    public long UncachedInput => Math.Max(0, Input) - Math.Clamp(CachedInput, 0, Math.Max(0, Input));

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
