using System.Globalization;
using System.Text.Json;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 会话日志的解析与状态累积。
/// </summary>
/// <remarks>
/// 这是唯一的解析实现：<see cref="SessionLogReader"/> 与
/// <see cref="SessionLogMonitor"/> 都通过它消费行，
/// 避免两套实现各自演化而在修复时遗漏其中一处。
/// 本类不是线程安全的，调用方负责串行化。
/// </remarks>
internal sealed class SessionLogAccumulator
{
    /// <summary>
    /// 单行长度上限。真实会话中最长的行约 2 MB（含大段工具输出），
    /// 32 MB 留出充足余量；超过此值的行按损坏处理并计数。
    /// </summary>
    internal const int MaxLineBytes = 32 * 1024 * 1024;

    private readonly List<UsageRecord> _usageRecords = [];
    private readonly List<TokenCountSnapshot> _tokenCounts = [];
    private readonly Dictionary<string, TurnContext> _turns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _modelCounts = new(StringComparer.Ordinal);

    private string _threadId;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastActivityAt;
    private string? _workingDirectory;
    private string? _reasoningEffort;
    private int _userMessages;
    private int _assistantMessages;
    private int _toolCalls;
    private int _toolResults;
    private int _compactions;
    private int _unreadableLines;

    /// <summary>
    /// 全部调用记录的逐项累计。
    /// </summary>
    /// <remarks>
    /// 增量维护而不是每次重新遍历记录：轮询频率远高于写入频率，
    /// 每次重建会在长会话上产生可观的 CPU 与分配开销。
    /// 累计值刻意基于调用记录而非 token_count 的累计字段，
    /// 因为上下文压缩会使后者偏小（见 SessionLogMonitor 的说明）。
    /// </remarks>
    private TokenTotals _cumulative;

    /// <summary>最近一个 turn 的累计。turn 变化时会重置。</summary>
    private TokenTotals _currentTurn;

    private string? _currentTurnId;

    /// <summary>当前 turn 已计入的调用次数。</summary>
    private int _currentTurnCallCount;

    public SessionLogAccumulator(string? logPath)
    {
        _threadId = logPath is null ? string.Empty : SessionLogReader.ExtractThreadId(logPath);
    }

    /// <summary>累计的调用记录数量。</summary>
    public int UsageRecordCount => _usageRecords.Count;

    /// <summary>因超长或格式错误而未能解析的行数。</summary>
    public int UnreadableLineCount => _unreadableLines;

    /// <summary>全部调用记录的累计用量。</summary>
    public TokenTotals Cumulative => _cumulative;

    /// <summary>最近一个 turn 的累计用量。</summary>
    public TokenTotals CurrentTurn => _currentTurn;

    /// <summary>最近一个 turn 的标识。</summary>
    public string? CurrentTurnId => _currentTurnId;

    /// <summary>
    /// 将一个超出长度上限、必然无法解析的行计入统计。
    /// </summary>
    /// <remarks>
    /// 用于增量读取时丢弃已经无法挽回的超长行：它永远不会成为合法 JSON，
    /// 继续保留只会让缓冲无限增长。
    /// </remarks>
    public void RecordDiscardedLine() => _unreadableLines++;

    public void Reset(string? logPath)
    {
        _usageRecords.Clear();
        _tokenCounts.Clear();
        _turns.Clear();
        _modelCounts.Clear();
        _threadId = logPath is null ? string.Empty : SessionLogReader.ExtractThreadId(logPath);
        _startedAt = null;
        _lastActivityAt = null;
        _workingDirectory = null;
        _reasoningEffort = null;
        _userMessages = 0;
        _assistantMessages = 0;
        _toolCalls = 0;
        _toolResults = 0;
        _compactions = 0;
        _unreadableLines = 0;
        _cumulative = default;
        _currentTurn = default;
        _currentTurnId = null;
        _currentTurnCallCount = 0;
    }

    /// <summary>
    /// 解析单行并更新状态。
    /// </summary>
    /// <returns>该行是否被识别为合法 JSON 对象。</returns>
    public bool ConsumeLine(string line)
    {
        if (line.Length > MaxLineBytes)
        {
            // 不静默丢弃：计入统计供调用方判断数据完整性。
            _unreadableLines++;
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // 畸形或未知格式的行：跳过，不影响其余解析。
            _unreadableLines++;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _unreadableLines++;
                return false;
            }

            var timestamp = ReadTimestamp(root);
            if (timestamp is { } ts)
            {
                _startedAt ??= ts;
                _lastActivityAt = ts;
            }

            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return true;
            }

            switch (typeElement.GetString())
            {
                case "session_meta":
                    ConsumeSessionMeta(root);
                    break;
                case "token_usage_record":
                    ConsumeUsageRecord(root, timestamp);
                    break;
                case "turn_context":
                    ConsumeTurnContext(root, timestamp);
                    break;
                case "event_msg":
                    ConsumeEventMessage(root, timestamp);
                    break;
                case "response_item":
                    ConsumeResponseItem(root);
                    break;
                default:
                    break;
            }

            return true;
        }
    }

    public SessionSnapshot BuildSnapshot(string logPath)
    {
        var primaryModel = _modelCounts.Count > 0
            ? _modelCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .First().Key
            : null;

        return new SessionSnapshot
        {
            ThreadId = _threadId,
            LogPath = logPath,
            StartedAt = _startedAt,
            LastActivityAt = _lastActivityAt,
            WorkingDirectory = _workingDirectory,
            PrimaryModel = primaryModel,
            ReasoningEffort = _reasoningEffort,
            UsageRecords = [.. _usageRecords],
            TokenCounts = [.. _tokenCounts],
            Turns = new Dictionary<string, TurnContext>(_turns, StringComparer.Ordinal),
            UserMessageCount = _userMessages,
            AssistantMessageCount = _assistantMessages,
            ToolCallCount = _toolCalls,
            ToolResultCount = _toolResults,
            CompactionCount = _compactions,
            UnreadableLineCount = _unreadableLines,
            Cumulative = _cumulative,
            CurrentTurn = _currentTurn,
            CurrentTurnId = _currentTurnId,
            CurrentTurnCallCount = _currentTurnCallCount,
        };
    }

    private void ConsumeSessionMeta(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var id = ReadString(payload, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            _threadId = id!;
        }
    }

    private void ConsumeUsageRecord(JsonElement root, DateTimeOffset? timestamp)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("usage", out var usageElement)
            || usageElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var turnId = ReadString(payload, "turn_id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        var usage = ReadTokenUsage(usageElement);

        _usageRecords.Add(new UsageRecord
        {
            // 缺少时间戳的记录保留为 null 而不是 0001-01-01，避免污染排序与时长计算。
            Timestamp = timestamp,
            TurnId = turnId!,
            ResponseId = ReadString(payload, "response_id"),
            Usage = usage,
        });

        var totals = TokenTotals.From(usage);

        // 用饱和累加而非普通加法。
        //
        // 数值来自外部日志文件，可能被截断或损坏。
        // 普通加法会回绕为负数，而负的累计值会让费用也算成负数。
        _cumulative = TokenTotals.AddSaturating(_cumulative, totals);

        if (!string.Equals(_currentTurnId, turnId, StringComparison.Ordinal))
        {
            _currentTurnId = turnId;
            _currentTurn = totals;
            _currentTurnCallCount = 1;
        }
        else
        {
            _currentTurn = TokenTotals.AddSaturating(_currentTurn, totals);
            _currentTurnCallCount++;
        }
    }

    private void ConsumeTurnContext(JsonElement root, DateTimeOffset? timestamp)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var turnId = ReadString(payload, "turn_id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        var turn = new TurnContext
        {
            TurnId = turnId!,
            Model = ReadString(payload, "model"),
            ReasoningEffort = ReadString(payload, "effort"),
            WorkingDirectory = ReadString(payload, "cwd"),
            Timestamp = timestamp,
        };

        _turns[turn.TurnId] = turn;
        _workingDirectory ??= turn.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(turn.Model))
        {
            var model = turn.Model!;
            _modelCounts[model] = _modelCounts.GetValueOrDefault(model) + 1;
        }

        if (!string.IsNullOrWhiteSpace(turn.ReasoningEffort))
        {
            _reasoningEffort = turn.ReasoningEffort;
        }
    }

    private void ConsumeEventMessage(JsonElement root, DateTimeOffset? timestamp)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("type", out var payloadType)
            || payloadType.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (payloadType.GetString())
        {
            case "token_count":
                if (TryReadTokenCount(payload, timestamp, out var snapshot))
                {
                    _tokenCounts.Add(snapshot);
                }
                break;

            case "item_completed":
                // 计数只走 item_completed：response_item 描述同一批条目，
                // 两者都算会翻倍。
                ConsumeCompletedItem(payload);
                break;

            default:
                break;
        }
    }

    private void ConsumeCompletedItem(JsonElement payload)
    {
        if (!payload.TryGetProperty("item", out var item)
            || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (ReadString(item, "type"))
        {
            case "UserMessage":
                _userMessages++;
                break;
            case "AgentMessage":
                _assistantMessages++;
                break;
            case "CommandExecution":
            case "McpToolCall":
            case "WebSearch":
                _toolCalls++;
                break;
            case "ContextCompaction":
                _compactions++;
                break;
            default:
                break;
        }
    }

    private void ConsumeResponseItem(JsonElement root)
    {
        // 工具结果没有对应的 item_completed 条目，只能从这里计数。
        if (root.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object
            && ReadString(payload, "type") == "function_call_output")
        {
            _toolResults++;
        }
    }

    private static bool TryReadTokenCount(
        JsonElement payload,
        DateTimeOffset? timestamp,
        out TokenCountSnapshot snapshot)
    {
        snapshot = null!;

        if (!payload.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("last_token_usage", out var last)
            || last.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("total_token_usage", out var total)
            || total.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        snapshot = new TokenCountSnapshot
        {
            Timestamp = timestamp,
            LastUsage = ReadTokenUsage(last),
            TotalUsage = ReadTokenUsage(total),
            ModelContextWindow = ReadLong(info, "model_context_window"),
        };
        return true;
    }

    internal static TokenUsage ReadTokenUsage(JsonElement element) => new()
    {
        InputTokens = ReadLong(element, "input_tokens"),
        CachedInputTokens = ReadLong(element, "cached_input_tokens"),
        CacheWriteInputTokens = ReadLong(element, "cache_write_input_tokens"),
        OutputTokens = ReadLong(element, "output_tokens"),
        ReasoningOutputTokens = ReadLong(element, "reasoning_output_tokens"),
        TotalTokens = ReadLong(element, "total_tokens"),
    };

    internal static long ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
            ? result
            : 0;

    internal static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        var raw = ReadString(root, "timestamp");
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
