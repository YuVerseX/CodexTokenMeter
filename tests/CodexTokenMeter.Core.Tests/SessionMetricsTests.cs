using System.Text.Json;
using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Metrics;

namespace CodexTokenMeter.Core.Tests;

public class SessionMetricsTests
{
    private static string LogPath => "rollout-2026-09-25T10-00-00-01a0d656-cf19-7532-af4a-f089fc3163ed.jsonl";

    [Fact]
    public void Cumulative_SumsAllUsageRecords()
    {
        var snapshot = Build(("turn-1", 1000, 800, 50), ("turn-1", 2000, 100, 100));

        var metrics = SessionMetrics.Create(snapshot);

        Assert.Equal(3000, metrics.Cumulative.Input);
        Assert.Equal(900, metrics.Cumulative.CachedInput);
        Assert.Equal(150, metrics.Cumulative.Output);
        Assert.Equal(3150, metrics.Cumulative.Total);
    }

    [Fact]
    public void Cumulative_ComesFromSnapshotNotFromRecomputation()
    {
        // 累计值由解析阶段的累积器维护，指标层只是投影。
        // 若指标层重新遍历记录，这里构造的不一致快照会暴露出来。
        var snapshot = new SessionSnapshot
        {
            ThreadId = "thread-1",
            LogPath = LogPath,
            Cumulative = new TokenTotals { Total = 999 },
            UsageRecords = [],
        };

        Assert.Equal(999, SessionMetrics.Create(snapshot).Cumulative.Total);
    }

    [Fact]
    public void Cumulative_IsBasedOnRecordsNotTokenCountTotals()
    {
        // 计费依据必须是逐次调用记录。token_count 的累计字段在压缩时会漏记账，
        // 因此即使它的数值更大也不能参与计算。
        var snapshot = Build(
            usages: [("turn-1", 1000, 0, 100)],
            tokenCountCumulativeTotal: 999_999);

        Assert.Equal(1100, SessionMetrics.Create(snapshot).Cumulative.Total);
    }

    [Fact]
    public void CurrentTurn_SumsOnlyTheLatestTurn()
    {
        // 一个 turn 可能包含多次模型调用，必须全部计入本轮。
        var snapshot = Build(
            ("turn-1", 100, 0, 10),
            ("turn-2", 200, 0, 20),
            ("turn-2", 300, 0, 30));

        var metrics = SessionMetrics.Create(snapshot);

        Assert.Equal("turn-2", metrics.CurrentTurnId);
        Assert.Equal(500, metrics.CurrentTurn.Input);
        Assert.Equal(550, metrics.CurrentTurn.Total);
        Assert.Equal(660, metrics.Cumulative.Total);
    }

    [Fact]
    public void CurrentTurn_IsEmptyWhenNoRecords()
    {
        var metrics = SessionMetrics.Create(Build());

        Assert.Null(metrics.CurrentTurnId);
        Assert.Equal(0, metrics.CurrentTurn.Total);
    }

    [Fact]
    public void CacheHitRate_UsesInputAsDenominator()
    {
        // 与 pi-web 一致：命中率 = cached / input，
        // 而 input 已包含 cached 与 cacheWrite 部分。
        var totals = new TokenTotals
        {
            Input = 1000,
            CachedInput = 900,
            CacheWriteInput = 0,
            Output = 100,
            Total = 1100,
        };

        Assert.Equal(90d, totals.CacheHitRate);
    }

    [Fact]
    public void CacheHitRate_IsZeroWhenNoInput()
    {
        Assert.Equal(0d, default(TokenTotals).CacheHitRate);
    }

    [Fact]
    public void UncachedInput_SubtractsCachedPortion()
    {
        var totals = new TokenTotals { Input = 1000, CachedInput = 900 };
        Assert.Equal(100, totals.UncachedInput);
    }

    [Fact]
    public void UncachedInput_NeverNegative()
    {
        // 防御异常数据：缓存量大于输入量时归零而不是产生负值。
        var totals = new TokenTotals { Input = 100, CachedInput = 500 };
        Assert.Equal(0, totals.UncachedInput);
    }

    [Fact]
    public void TokenTotals_AddSumsEachFieldIndependently()
    {
        var a = new TokenTotals
        {
            Input = 1,
            CachedInput = 2,
            CacheWriteInput = 3,
            Output = 4,
            ReasoningOutput = 5,
            Total = 6,
        };
        var b = new TokenTotals
        {
            Input = 10,
            CachedInput = 20,
            CacheWriteInput = 30,
            Output = 40,
            ReasoningOutput = 50,
            Total = 60,
        };

        var sum = a.Add(b);

        Assert.Equal(11, sum.Input);
        Assert.Equal(22, sum.CachedInput);
        Assert.Equal(33, sum.CacheWriteInput);
        Assert.Equal(44, sum.Output);
        Assert.Equal(55, sum.ReasoningOutput);
        Assert.Equal(66, sum.Total);
    }

    [Fact]
    public void ContextPercent_UsesLatestTokenCountLastUsage()
    {
        // 上下文占用取 token_count 的 last 值：压缩后它会正确回缩，
        // 而累计值继续增长，两者不可混用。
        var snapshot = Build(
            usages: [("turn-1", 1000, 0, 100)],
            tokenCountLastTotal: 25_840,
            window: 258_400);

        var metrics = SessionMetrics.Create(snapshot);

        Assert.Equal(25_840, metrics.ContextUsedTokens);
        Assert.Equal(258_400, metrics.ContextWindowTokens);
        Assert.Equal(10d, metrics.ContextPercent);
    }

    [Fact]
    public void ContextPercent_IsZeroWithoutWindow()
    {
        var snapshot = Build(usages: [("turn-1", 1000, 0, 100)]);
        Assert.Equal(0d, SessionMetrics.Create(snapshot).ContextPercent);
    }

    [Fact]
    public void ActiveDuration_SpansFirstToLastEvent()
    {
        var snapshot = Build(usages: [("turn-1", 1, 0, 1)]) with
        {
            StartedAt = DateTimeOffset.Parse("2026-09-25T02:00:00Z"),
            LastActivityAt = DateTimeOffset.Parse("2026-09-25T02:01:34Z"),
        };

        Assert.Equal(TimeSpan.FromSeconds(94), SessionMetrics.Create(snapshot).ActiveDuration);
    }

    [Fact]
    public void ActiveDuration_IsZeroWhenTimestampsMissing()
    {
        Assert.Equal(TimeSpan.Zero, SessionMetrics.Create(Build(("turn-1", 1, 0, 1))).ActiveDuration);
    }

    [Fact]
    public void TotalMessageCount_ExcludesToolCalls()
    {
        var snapshot = Build(usages: [("turn-1", 1, 0, 1)]) with
        {
            UserMessageCount = 3,
            AssistantMessageCount = 5,
            ToolCallCount = 99,
        };

        Assert.Equal(8, SessionMetrics.Create(snapshot).TotalMessageCount);
    }

    [Fact]
    public void IsPartial_IsTrueWhenNotCaughtUp()
    {
        var snapshot = Build(usages: [("turn-1", 1, 0, 1)]) with { IsCaughtUp = false };

        Assert.True(SessionMetrics.Create(snapshot).IsPartial);
    }

    [Fact]
    public void IsPartial_IsTrueWhenLinesWereUnreadable()
    {
        var snapshot = Build(usages: [("turn-1", 1, 0, 1)]) with { UnreadableLineCount = 2 };

        Assert.True(SessionMetrics.Create(snapshot).IsPartial);
    }

    [Fact]
    public void IsPartial_IsFalseForCompleteSnapshot()
    {
        Assert.False(SessionMetrics.Create(Build(("turn-1", 1, 0, 1))).IsPartial);
    }

    /// <summary>
    /// 经由累积器构造快照，与产品代码路径一致。
    /// 直接 new 一个快照会绕过累计值维护，测不出真实行为。
    /// </summary>
    private static SessionSnapshot Build(
        params (string TurnId, long Input, long Cached, long Output)[] usages) =>
        Build(usages, tokenCountLastTotal: 0, tokenCountCumulativeTotal: 0, window: 0);

    private static SessionSnapshot Build(
        (string TurnId, long Input, long Cached, long Output)[] usages,
        long tokenCountLastTotal = 0,
        long tokenCountCumulativeTotal = 0,
        long window = 0)
    {
        var accumulator = new SessionLogAccumulator(LogPath);

        foreach (var (turnId, input, cached, output) in usages)
        {
            accumulator.ConsumeLine(JsonSerializer.Serialize(new
            {
                timestamp = "2026-09-25T02:00:00.000Z",
                type = "token_usage_record",
                payload = new
                {
                    turn_id = turnId,
                    usage = new
                    {
                        input_tokens = input,
                        cached_input_tokens = cached,
                        cache_write_input_tokens = 0,
                        output_tokens = output,
                        reasoning_output_tokens = 0,
                        total_tokens = input + output,
                    },
                },
            }));
        }

        if (tokenCountLastTotal > 0 || tokenCountCumulativeTotal > 0 || window > 0)
        {
            accumulator.ConsumeLine(JsonSerializer.Serialize(new
            {
                timestamp = "2026-09-25T02:00:00.000Z",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new
                        {
                            input_tokens = tokenCountLastTotal,
                            output_tokens = 0,
                            total_tokens = tokenCountLastTotal,
                        },
                        total_token_usage = new
                        {
                            input_tokens = tokenCountCumulativeTotal,
                            output_tokens = 0,
                            total_tokens = tokenCountCumulativeTotal,
                        },
                        model_context_window = window,
                    },
                },
            }));
        }

        return accumulator.BuildSnapshot(LogPath);
    }
}
