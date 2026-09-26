using System.Text.Json;
using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 覆盖审查中发现的边界情况：超长行、缺失时间戳、快照独立性、内存上界。
/// </summary>
public class SessionLogAccumulatorTests
{
    private const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";

    private static string LogPath => $"rollout-2026-09-25T10-13-28-{ThreadId}.jsonl";

    [Fact]
    public void ConsumeLine_CountsOversizedLineAsUnreadableInsteadOfSilentlyDropping()
    {
        // 超长行不应被无声丢弃，否则累计值偏低而调用方无从察觉。
        var accumulator = new SessionLogAccumulator(LogPath);
        var oversized = new string('x', SessionLogAccumulator.MaxLineBytes + 1);

        var accepted = accumulator.ConsumeLine(oversized);

        Assert.False(accepted);
        Assert.Equal(1, accumulator.UnreadableLineCount);

        var snapshot = accumulator.BuildSnapshot(LogPath);
        Assert.Equal(1, snapshot.UnreadableLineCount);
    }

    [Fact]
    public void ConsumeLine_CountsMalformedLineAsUnreadable()
    {
        var accumulator = new SessionLogAccumulator(LogPath);

        accumulator.ConsumeLine("这不是 JSON");

        Assert.Equal(1, accumulator.UnreadableLineCount);
    }

    [Fact]
    public void ConsumeLine_CountsNonObjectJsonAsUnreadable()
    {
        var accumulator = new SessionLogAccumulator(LogPath);

        accumulator.ConsumeLine("[1,2,3]");

        Assert.Equal(1, accumulator.UnreadableLineCount);
    }

    [Fact]
    public void ConsumeLine_TreatsRecoverableLineWithoutTimestampAsReadable()
    {
        var accumulator = new SessionLogAccumulator(LogPath);

        var accepted = accumulator.ConsumeLine(
            """{"type":"token_usage_record","payload":{"turn_id":"t1","usage":{"total_tokens":5}}}""");

        Assert.True(accepted);
        Assert.Equal(0, accumulator.UnreadableLineCount);
    }

    [Fact]
    public void ConsumeLine_LeavesTimestampNullWhenAbsent()
    {
        // 缺失时间戳必须保持 null，不能用 0001-01-01 兜底：
        // 否则会污染排序、活跃时长与「最近活动」判断。
        var accumulator = new SessionLogAccumulator(LogPath);

        accumulator.ConsumeLine(
            """{"type":"token_usage_record","payload":{"turn_id":"t1","usage":{"total_tokens":5}}}""");

        var snapshot = accumulator.BuildSnapshot(LogPath);
        Assert.Null(snapshot.UsageRecords[0].Timestamp);
        Assert.Null(snapshot.StartedAt);
        Assert.Null(snapshot.LastActivityAt);
    }

    [Fact]
    public void ConsumeLine_RecordsUnknownTypesWithoutCountingErrors()
    {
        // 未知事件类型是正常情况（Codex 会新增事件），不应算作解析失败。
        var accumulator = new SessionLogAccumulator(LogPath);

        accumulator.ConsumeLine("""{"type":"future_event_type","payload":{}}""");

        Assert.Equal(0, accumulator.UnreadableLineCount);
        Assert.Empty(accumulator.BuildSnapshot(LogPath).UsageRecords);
    }

    [Fact]
    public void BuildSnapshot_ReturnsIndependentCollectionsAcrossCalls()
    {
        // 快照必须是不可变副本：调用方持有旧快照时，
        // 后续解析不应让它发生内容变化。
        var accumulator = new SessionLogAccumulator(LogPath);
        Accumulate(accumulator, "t1", 100);

        var first = accumulator.BuildSnapshot(LogPath);
        Accumulate(accumulator, "t2", 200);
        var second = accumulator.BuildSnapshot(LogPath);

        Assert.Single(first.UsageRecords);
        Assert.Equal(2, second.UsageRecords.Count);
        Assert.Equal(100, first.UsageRecords[0].Usage.TotalTokens);
    }

    [Fact]
    public void BuildSnapshot_TracksCumulativeAndCurrentTurnIncrementally()
    {
        var accumulator = new SessionLogAccumulator(LogPath);
        Accumulate(accumulator, "turn-1", 100);
        Accumulate(accumulator, "turn-1", 200);

        var midTurn = accumulator.BuildSnapshot(LogPath);
        Assert.Equal(300, midTurn.Cumulative.Total);
        Assert.Equal(300, midTurn.CurrentTurn.Total);
        Assert.Equal("turn-1", midTurn.CurrentTurnId);

        // 切到新 turn 时，本轮用量重置而累计值继续增长。
        Accumulate(accumulator, "turn-2", 50);

        var newTurn = accumulator.BuildSnapshot(LogPath);
        Assert.Equal(350, newTurn.Cumulative.Total);
        Assert.Equal(50, newTurn.CurrentTurn.Total);
        Assert.Equal("turn-2", newTurn.CurrentTurnId);
    }

    [Fact]
    public void BuildSnapshot_InterleavedTurnsOnlyCountLatestRun()
    {
        // 本轮用量跟踪的是「最后的连续 turn」，而不是「每个 turn 的分组」。
        // 这符合计费需求：用户关心的是当前这一轮花了多少。
        var accumulator = new SessionLogAccumulator(LogPath);
        Accumulate(accumulator, "turn-1", 100);
        Accumulate(accumulator, "turn-2", 200);
        Accumulate(accumulator, "turn-1", 400);

        var snapshot = accumulator.BuildSnapshot(LogPath);

        Assert.Equal(700, snapshot.Cumulative.Total);
        Assert.Equal(400, snapshot.CurrentTurn.Total);
        Assert.Equal("turn-1", snapshot.CurrentTurnId);
    }

    [Fact]
    public void BuildSnapshot_TracksCurrentTurnCallCount()
    {
        // 一个 turn 可能包含几十次模型调用，只展示“本轮”容易被误读。
        var accumulator = new SessionLogAccumulator(LogPath);

        Accumulate(accumulator, "turn-1", 100);
        Accumulate(accumulator, "turn-1", 200);
        Accumulate(accumulator, "turn-1", 300);

        Assert.Equal(3, accumulator.BuildSnapshot(LogPath).CurrentTurnCallCount);

        // 切到新 turn 时计数重置。
        Accumulate(accumulator, "turn-2", 50);
        Assert.Equal(1, accumulator.BuildSnapshot(LogPath).CurrentTurnCallCount);
    }

    [Fact]
    public void BuildSnapshot_CurrentTurnCallCountIsZeroWithoutRecords()
    {
        var accumulator = new SessionLogAccumulator(LogPath);

        Assert.Equal(0, accumulator.BuildSnapshot(LogPath).CurrentTurnCallCount);
    }

    [Fact]
    public void Reset_ClearsCurrentTurnCallCount()
    {
        var accumulator = new SessionLogAccumulator(LogPath);
        Accumulate(accumulator, "turn-1", 100);

        Assert.Equal(1, accumulator.BuildSnapshot(LogPath).CurrentTurnCallCount);

        accumulator.Reset(LogPath);

        Assert.Equal(0, accumulator.BuildSnapshot(LogPath).CurrentTurnCallCount);
    }

    [Fact]
    public void Reset_ClearsAllAccumulatedState()
    {
        var accumulator = new SessionLogAccumulator(LogPath);
        Accumulate(accumulator, "t1", 100);
        accumulator.ConsumeLine("bad json");

        accumulator.Reset(LogPath);
        var snapshot = accumulator.BuildSnapshot(LogPath);

        Assert.Empty(snapshot.UsageRecords);
        Assert.Empty(snapshot.TokenCounts);
        Assert.Equal(0, snapshot.UnreadableLineCount);
        Assert.Equal(0, accumulator.UsageRecordCount);
        Assert.Equal(0, snapshot.Cumulative.Total);
        Assert.Equal(0, snapshot.CurrentTurn.Total);
        Assert.Null(snapshot.CurrentTurnId);
        Assert.Equal(0, snapshot.CurrentTurnCallCount);
    }

    [Fact]
    public void ReadTokenUsage_TreatsMissingFieldsAsZero()
    {
        using var document = JsonDocument.Parse("""{"input_tokens":10}""");

        var usage = SessionLogAccumulator.ReadTokenUsage(document.RootElement);

        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.CachedInputTokens);
    }

    [Fact]
    public void ReadTokenUsage_IgnoresNonNumericValues()
    {
        // 字段类型异常时按 0 处理，不应抛异常中断整体解析。
        using var document = JsonDocument.Parse(
            """{"input_tokens":"many","output_tokens":null,"total_tokens":5.5}""");

        var usage = SessionLogAccumulator.ReadTokenUsage(document.RootElement);

        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.TotalTokens);
    }

    [Fact]
    public void ReadTimestamp_NormalizesToUtc()
    {
        using var document = JsonDocument.Parse(
            """{"timestamp":"2026-09-25T10:13:28.000+08:00"}""");

        var timestamp = SessionLogAccumulator.ReadTimestamp(document.RootElement);

        Assert.NotNull(timestamp);
        Assert.Equal(TimeSpan.Zero, timestamp!.Value.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T02:13:28Z"), timestamp.Value);
    }

    [Fact]
    public void ReadTimestamp_ReturnsNullForUnparseableValue()
    {
        using var document = JsonDocument.Parse("""{"timestamp":"not a timestamp"}""");

        Assert.Null(SessionLogAccumulator.ReadTimestamp(document.RootElement));
    }

    private static void Accumulate(SessionLogAccumulator accumulator, string turnId, long total)
    {
        accumulator.ConsumeLine(JsonSerializer.Serialize(new
        {
            timestamp = "2026-09-25T02:00:00.000Z",
            type = "token_usage_record",
            payload = new
            {
                turn_id = turnId,
                usage = new { input_tokens = total, output_tokens = 0, total_tokens = total },
            },
        }));
    }
}
