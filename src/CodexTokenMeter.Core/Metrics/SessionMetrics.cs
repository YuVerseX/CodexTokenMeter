using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Metrics;

/// <summary>
/// 会话的派生指标，供 UI 直接绑定。
/// </summary>
/// <remarks>
/// 本类型是快照的纯投影：所有累计值都由 <see cref="SessionSnapshot"/>
/// 携带，不在此处重新遍历记录。轮询频率远高于日志写入频率，
/// 每次重建会在长会话上产生可观的 CPU 与分配开销。
/// </remarks>
public sealed record SessionMetrics
{
    public required SessionSnapshot Snapshot { get; init; }

    /// <summary>会话累计用量，由逐次调用记录求得。</summary>
    public TokenTotals Cumulative => Snapshot.Cumulative;

    /// <summary>最近一个 turn 的用量。</summary>
    public TokenTotals CurrentTurn => Snapshot.CurrentTurn;

    public string? CurrentTurnId => Snapshot.CurrentTurnId;

    /// <summary>当前上下文占用，取自最后一条 token_count 的 last 用量。</summary>
    public long ContextUsedTokens => Snapshot.LatestTokenCount?.LastUsage.TotalTokens ?? 0;

    public long ContextWindowTokens => Snapshot.LatestTokenCount?.ModelContextWindow ?? 0;

    /// <summary>
    /// 上下文占用百分比，取值 0–100。
    /// </summary>
    /// <remarks>
    /// 使用 token_count 的 last 用量而不是累计值：压缩会让上下文回缩，
    /// 而累计值只增不减，两者不可混用。
    /// </remarks>
    public double ContextPercent => ContextWindowTokens <= 0
        ? 0d
        : Math.Clamp((double)ContextUsedTokens * 100d / ContextWindowTokens, 0d, 100d);

    /// <summary>会话活跃时长，从首个到最后一个带时间戳的事件。</summary>
    public TimeSpan ActiveDuration =>
        Snapshot.StartedAt is { } start && Snapshot.LastActivityAt is { } end && end > start
            ? end - start
            : TimeSpan.Zero;

    /// <summary>用户与助手消息合计。工具调用不计入。</summary>
    public int TotalMessageCount =>
        Snapshot.UserMessageCount + Snapshot.AssistantMessageCount;

    /// <summary>日志是否仍有未解析的内容，为 true 时数值可能不完整。</summary>
    public bool IsPartial => !Snapshot.IsCaughtUp || Snapshot.UnreadableLineCount > 0;

    /// <summary>由会话快照构建指标。</summary>
    public static SessionMetrics Create(SessionSnapshot snapshot) => new()
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot)),
    };
}
