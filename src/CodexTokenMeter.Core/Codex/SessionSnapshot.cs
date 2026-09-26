using CodexTokenMeter.Core.Metrics;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 一次会话快照的完整内容。由 <see cref="SessionLogReader"/> 从单个 JSONL 文件解析得到。
/// </summary>
public sealed record SessionSnapshot
{
    public required string ThreadId { get; init; }
    public required string LogPath { get; init; }

    /// <summary>会话首次记录的落盘时间。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>最后一条带时间戳事件的时刻。</summary>
    public DateTimeOffset? LastActivityAt { get; init; }

    /// <summary>会话工作目录，取自 turn_context。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// 出现次数最多的模型。Codex 不记录「当前模型」，因此以主导模型作为会话模型。
    /// 若 turn_context 缺失则为 null。
    /// </summary>
    public string? PrimaryModel { get; init; }

    /// <summary>最近的推理档位。</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>逐次模型调用记录，按时间顺序。这是计费的权威数据源。</summary>
    public IReadOnlyList<UsageRecord> UsageRecords { get; init; } = [];

    /// <summary>上下文快照序列，按时间顺序。</summary>
    public IReadOnlyList<TokenCountSnapshot> TokenCounts { get; init; } = [];

    /// <summary>turn 元信息，按 turnId 索引。</summary>
    public IReadOnlyDictionary<string, TurnContext> Turns { get; init; } =
        new Dictionary<string, TurnContext>();

    public int UserMessageCount { get; init; }
    public int AssistantMessageCount { get; init; }
    public int ToolCallCount { get; init; }
    public int ToolResultCount { get; init; }

    /// <summary>发生上下文压缩的次数。</summary>
    public int CompactionCount { get; init; }

    /// <summary>
    /// 无法解析的行数，包括超长行与格式错误行。
    /// 非零表示数据不完整，UI 应提示而不是默默展示可能偏低的数值。
    /// </summary>
    public int UnreadableLineCount { get; init; }

    /// <summary>
    /// 日志中的全部数据是否都已解析并反映到本快照。
    /// </summary>
    /// <remarks>
    /// false 表示仍有未消费的内容（例如正在写入的半行），
    /// 此时快照可能不完整，UI 不应把它当作最终值。
    /// 全量读取始终为 true。
    /// </remarks>
    public bool IsCaughtUp { get; init; } = true;

    /// <summary>
    /// 全部调用记录的逐项累计用量。
    /// </summary>
    /// <remarks>
    /// 这是计费的权威口径。它基于逐次调用记录，而非 token_count 的
    /// 累计字段——上下文压缩会使后者偏小。
    /// </remarks>
    public TokenTotals Cumulative { get; init; }

    /// <summary>最近一个 turn 的累计用量。一个 turn 可能包含多次模型调用。</summary>
    public TokenTotals CurrentTurn { get; init; }

    /// <summary>最近一个 turn 的标识，无记录时为 null。</summary>
    public string? CurrentTurnId { get; init; }

    /// <summary>
    /// 最近一个 turn 包含的模型调用次数。
    /// </summary>
    /// <remarks>
    /// 由解析阶段增量维护，避免 UI 每次刷新都重新遍历记录。
    /// 一个 turn 可能包含几十次调用（工具循环），
    /// 仅展示“本轮”容易被误读为“最近一次调用”。
    /// </remarks>
    public int CurrentTurnCallCount { get; init; }

    /// <summary>最后一条 token_count 快照，可能为 null。</summary>
    public TokenCountSnapshot? LatestTokenCount =>
        TokenCounts.Count > 0 ? TokenCounts[^1] : null;

    /// <summary>是否成功取到可用的会话数据。</summary>
    public bool HasData => UsageRecords.Count > 0 || TokenCounts.Count > 0;
}
