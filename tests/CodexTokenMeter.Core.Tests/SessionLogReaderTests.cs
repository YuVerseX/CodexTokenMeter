using System.Text;
using System.Text.Json;
using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 用合成数据覆盖解析器的边界行为。
/// 真实会话数据不适合进版本库（含对话内容），因此这里全部构造。
/// </summary>
public class SessionLogReaderTests
{
    private const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
    private const string Timestamp = "2026-09-25T02:00:00.000Z";

    private static string LogPath => $"rollout-2026-09-25T10-13-28-{ThreadId}.jsonl";

    /// <summary>
    /// 对文本跑一次完整解析。产品代码只暴露文件级入口，
    /// 这里直接驱动累积器以覆盖逐行边界情况。
    /// </summary>
    private static SessionSnapshot Parse(string text)
    {
        var accumulator = new SessionLogAccumulator(LogPath);
        using var reader = new StringReader(text);

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                accumulator.ConsumeLine(trimmed);
            }
        }

        return accumulator.BuildSnapshot(LogPath);
    }

    [Fact]
    public void Parse_ExtractsThreadIdFromFileName()
    {
        var snapshot = Parse(string.Empty);
        Assert.Equal(ThreadId, snapshot.ThreadId);
    }

    [Fact]
    public void Parse_PrefersSessionMetaIdOverFileName()
    {
        var jsonl = Line("session_meta", new { id = "meta-id-wins" });

        var snapshot = Parse(jsonl);

        Assert.Equal("meta-id-wins", snapshot.ThreadId);
    }

    [Fact]
    public void Parse_ReadsUsageRecords()
    {
        var jsonl = Line("token_usage_record", new
        {
            turn_id = "turn-1",
            response_id = "resp-1",
            usage = new
            {
                input_tokens = 1000,
                cached_input_tokens = 800,
                cache_write_input_tokens = 0,
                output_tokens = 50,
                reasoning_output_tokens = 12,
                total_tokens = 1050,
            },
        });

        var snapshot = Parse(jsonl);

        var record = Assert.Single(snapshot.UsageRecords);
        Assert.Equal("turn-1", record.TurnId);
        Assert.Equal("resp-1", record.ResponseId);
        Assert.Equal(1000, record.Usage.InputTokens);
        Assert.Equal(800, record.Usage.CachedInputTokens);
        Assert.Equal(50, record.Usage.OutputTokens);
        Assert.Equal(12, record.Usage.ReasoningOutputTokens);
        Assert.Equal(1050, record.Usage.TotalTokens);
    }

    [Fact]
    public void Parse_SkipsTruncatedTrailingLine()
    {
        // 模拟 Codex 正在追加写入：最后一行是写了一半的 JSON。
        var jsonl = string.Join('\n',
            Line("token_usage_record", UsagePayload("turn-1", 100, 0, 10, 110)),
            """{"timestamp":"2026-09-25T02:00:00.000Z","type":"token_usage_record","payload":{"turn_id":"turn-2","usag""");

        var snapshot = Parse(jsonl);

        var record = Assert.Single(snapshot.UsageRecords);
        Assert.Equal("turn-1", record.TurnId);
    }

    [Fact]
    public void Parse_SkipsMalformedLineWithoutLosingOthers()
    {
        var jsonl = string.Join('\n',
            Line("token_usage_record", UsagePayload("turn-1", 100, 0, 10, 110)),
            "not json at all",
            Line("token_usage_record", UsagePayload("turn-2", 200, 0, 20, 220)));

        var snapshot = Parse(jsonl);

        Assert.Equal(2, snapshot.UsageRecords.Count);
        Assert.Equal(["turn-1", "turn-2"], snapshot.UsageRecords.Select(r => r.TurnId));
    }

    [Fact]
    public void Parse_ReadsTurnContextAndPicksDominantModel()
    {
        var jsonl = string.Join('\n',
            Line("turn_context", TurnContextPayload("turn-1", "gpt-6-sol", "max", @"E:\Code\demo")),
            Line("turn_context", TurnContextPayload("turn-2", "gpt-6-sol", "max", @"E:\Code\demo")),
            Line("turn_context", TurnContextPayload("turn-3", "gpt-6-luna", "low", @"E:\Code\demo")));

        var snapshot = Parse(jsonl);

        // 模型取主导值：会话中途可能用不同模型（如 review_model）跑单轮，
        // 单轮例外不应改变会话模型。
        Assert.Equal("gpt-6-sol", snapshot.PrimaryModel);
        Assert.Equal(@"E:\Code\demo", snapshot.WorkingDirectory);
        Assert.Equal(3, snapshot.Turns.Count);

        // 推理档位取最近一次：它反映当前生效的设置。
        Assert.Equal("low", snapshot.ReasoningEffort);
    }

    [Fact]
    public void Parse_KeepsPerTurnModelForBilling()
    {
        // 不同 turn 可能走不同模型，计费必须逐 turn 取价，不能用会话主导模型。
        var jsonl = string.Join('\n',
            Line("turn_context", TurnContextPayload("turn-1", "gpt-6-sol", "max", @"E:\Code\demo")),
            Line("turn_context", TurnContextPayload("turn-2", "gpt-5.6-luna", "low", @"E:\Code\demo")));

        var snapshot = Parse(jsonl);

        Assert.Equal("gpt-6-sol", snapshot.Turns["turn-1"].Model);
        Assert.Equal("gpt-5.6-luna", snapshot.Turns["turn-2"].Model);
    }

    [Fact]
    public void Parse_RejectsUsageRecordWithoutTurnId()
    {
        var jsonl = Line("token_usage_record", new
        {
            usage = new { input_tokens = 10, output_tokens = 1, total_tokens = 11 },
        });

        var snapshot = Parse(jsonl);

        Assert.Empty(snapshot.UsageRecords);
    }

    [Fact]
    public void Parse_CountsMessagesOnceDespiteDualRepresentation()
    {
        // 同一批条目同时出现在 item_completed 与 response_item 中，
        // 计数必须只走一条路径，否则会翻倍。
        var jsonl = string.Join('\n',
            ItemCompleted("UserMessage"),
            ItemCompleted("AgentMessage"),
            ItemCompleted("CommandExecution"),
            Line("response_item", new { type = "message", role = "user" }),
            Line("response_item", new { type = "message", role = "assistant" }),
            Line("response_item", new { type = "function_call", name = "shell" }));

        var snapshot = Parse(jsonl);

        Assert.Equal(1, snapshot.UserMessageCount);
        Assert.Equal(1, snapshot.AssistantMessageCount);
        Assert.Equal(1, snapshot.ToolCallCount);
    }

    [Fact]
    public void Parse_CountsToolResultsFromResponseItems()
    {
        // item_completed 不为工具结果单独发条目，因此只能从 response_item 计数。
        var jsonl = string.Join('\n',
            Line("response_item", new { type = "function_call_output", call_id = "a" }),
            Line("response_item", new { type = "function_call_output", call_id = "b" }));

        var snapshot = Parse(jsonl);

        Assert.Equal(2, snapshot.ToolResultCount);
        Assert.Equal(0, snapshot.ToolCallCount);
    }

    [Fact]
    public void Parse_CountsCompactionOnceDespiteDualRepresentation()
    {
        // 顶层 compacted 与 item_completed/ContextCompaction 是同一事件。
        var jsonl = string.Join('\n',
            Line("compacted", new { }),
            ItemCompleted("ContextCompaction"));

        var snapshot = Parse(jsonl);

        Assert.Equal(1, snapshot.CompactionCount);
    }

    [Fact]
    public void Parse_ReadsContextWindowFromTokenCount()
    {
        var jsonl = Line("event_msg", new
        {
            type = "token_count",
            info = new
            {
                last_token_usage = new { input_tokens = 164_310, output_tokens = 0, total_tokens = 164_310 },
                total_token_usage = new { input_tokens = 500_000, output_tokens = 0, total_tokens = 500_000 },
                model_context_window = 258_400,
            },
        });

        var snapshot = Parse(jsonl);

        var tokenCount = Assert.Single(snapshot.TokenCounts);
        Assert.Equal(164_310, tokenCount.LastUsage.TotalTokens);
        Assert.Equal(500_000, tokenCount.TotalUsage.TotalTokens);
        Assert.Equal(258_400, tokenCount.ModelContextWindow);
    }

    [Fact]
    public void Parse_TracksActivityWindow()
    {
        var jsonl = string.Join('\n',
            """{"timestamp":"2026-09-25T02:00:00.000Z","type":"token_usage_record","payload":{"turn_id":"t1","usage":{"total_tokens":1}}}""",
            """{"timestamp":"2026-09-25T02:33:36.000Z","type":"token_usage_record","payload":{"turn_id":"t2","usage":{"total_tokens":1}}}""");

        var snapshot = Parse(jsonl);

        Assert.Equal(DateTimeOffset.Parse("2026-09-25T02:00:00Z"), snapshot.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T02:33:36Z"), snapshot.LastActivityAt);
    }

    private static string Line(string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = Timestamp, type, payload });

    private static string ItemCompleted(string itemType) =>
        Line("event_msg", new { type = "item_completed", item = new { type = itemType } });

    private static object UsagePayload(string turnId, long input, long cached, long output, long total) => new
    {
        turn_id = turnId,
        usage = new
        {
            input_tokens = input,
            cached_input_tokens = cached,
            cache_write_input_tokens = 0,
            output_tokens = output,
            reasoning_output_tokens = 0,
            total_tokens = total,
        },
    };

    private static object TurnContextPayload(string turnId, string model, string effort, string cwd) => new
    {
        turn_id = turnId,
        model,
        effort,
        cwd,
    };
}

