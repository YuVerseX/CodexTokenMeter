using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Metrics;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 针对仓库外的真实会话文件的验证。
/// 这些测试只在本地存在会话日志时运行，CI 上会跳过。
/// 它们断言的是数据源之间必须成立的不变量，而不是具体数值。
/// </summary>
/// <remarks>
/// 只验证已稳定的会话文件（见 <c>EnumerateLogs</c>）。
/// 正在写入的日志会短暂地处于不一致状态，对其断言不成立。
/// </remarks>
public class RealSessionInvariantTests
{
    /// <summary>
    /// 真实 Codex 会话目录。可用 CODEX_HOME 覆盖。
    /// </summary>
    private static string SessionsRoot
    {
        get
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                codexHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex");
            }

            return Path.Combine(codexHome, "sessions");
        }
    }

    private static IEnumerable<string> EnumerateLogs() =>
        Directory.Exists(SessionsRoot)
            ? Directory.EnumerateFiles(SessionsRoot, "*.jsonl", SearchOption.AllDirectories)
            : [];

    /// <summary>
    /// 读取一份稳定快照；文件正在写入时返回 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这些测试断言的是完成态会话的不变量，而日志是逐行追加的：
    /// 先写 usage record，后写 token_count。读取恰好落在两者之间时，
    /// 会看到「无压缩却有差异」的假象。
    /// </para>
    /// <para>
    /// 不能用「几秒内无写入」这类时间启发式：那只是猜测。
    /// 也不能只比对文件元数据：写入可能在读取开始前就已落盘且元数据已更新，
    /// 但两份记录尚未配齐（实测该写法仍会偶发失败）。
    /// </para>
    /// <para>
    /// 改为**内容级一致性**：连读两次，两者完全一致才认为文件已静止。
    /// 这直接验证了「没有处于写入中间状态」，而不是去推测。
    /// 代价是读取次数翻倍；这些测试只在本地有会话日志时运行，可以接受。
    /// </para>
    /// </remarks>
    private static SessionSnapshot? ReadStable(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return null;
        }

        var first = SessionLogReader.Read(logPath);
        var second = SessionLogReader.Read(logPath);

        // 两次结果不一致：文件在读取期间发生了变化，无法断定哪一次是完整的。
        return AreEquivalent(first, second) ? second : null;
    }

    /// <summary>
    /// 判断两份快照是否描述同一份内容。
    /// </summary>
    /// <remarks>
    /// 只比对与不变量相关的字段，而不是整个对象：
    /// 时间戳等字段在两次读取间可能因系统时钟精度而不同。
    /// </remarks>
    private static bool AreEquivalent(SessionSnapshot left, SessionSnapshot right) =>
        left.ThreadId == right.ThreadId
        && left.UsageRecords.Count == right.UsageRecords.Count
        && left.TokenCounts.Count == right.TokenCounts.Count
        && left.CompactionCount == right.CompactionCount
        && left.Cumulative == right.Cumulative
        && left.CurrentTurn == right.CurrentTurn
        && left.CurrentTurnCallCount == right.CurrentTurnCallCount
        && left.UnreadableLineCount == right.UnreadableLineCount;

    [Fact]
    public void RealSessions_ParseWithoutError()
    {
        var logs = EnumerateLogs().ToArray();
        if (logs.Length == 0)
        {
            return; // 无会话数据时跳过，不视为失败。
        }

        foreach (var log in logs)
        {
            if (ReadStable(log) is not { } snapshot)
            {
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(snapshot.ThreadId));
            Assert.Equal(Path.GetFileName(log), Path.GetFileName(snapshot.LogPath));
            Assert.True(snapshot.UsageRecords.Count > 0 || snapshot.TokenCounts.Count > 0);
        }
    }

    /// <summary>
    /// 核心不变量：<c>token_usage_record</c> 求和必须大于或等于
    /// <c>token_count</c> 的累计值。
    /// 差额来自上下文压缩：压缩当次调用的真实消耗不会计入累计值
    /// （实测该次记录的累计增量为 0），但该次调用确实发生了费用。
    /// 若此不变量反转，说明计费依据选错了数据源。
    /// </summary>
    /// <remarks>
    /// 不适用于**分叉会话**，见 <see cref="IsForkSession"/>。
    /// </remarks>
    [Fact]
    public void RealSessions_UsageRecordSumIsNeverBelowCumulativeTotal()
    {
        foreach (var log in EnumerateLogs())
        {
            if (IsForkSession(log))
            {
                continue;
            }

            if (ReadStable(log) is not { } snapshot)
            {
                continue;
            }

            var latest = snapshot.LatestTokenCount;
            if (latest is null || snapshot.UsageRecords.Count == 0)
            {
                continue;
            }

            var recordSum = SessionMetrics.Create(snapshot).Cumulative.Total;
            var cumulative = latest.TotalUsage.TotalTokens;

            Assert.True(
                recordSum >= cumulative,
                $"{Path.GetFileName(log)}: 逐次调用求和 {recordSum} 小于 token_count 累计 {cumulative}。");
        }
    }

    /// <summary>
    /// 判断会话文件是否为分叉会话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分叉（fork）由「在新窗口继续此会话」类操作产生，文件名为
    /// <c>rollout-&lt;时间&gt;-&lt;父会话id&gt;_&lt;子会话id&gt;.jsonl</c>。
    /// </para>
    /// <para>
    /// 它的 <c>token_count</c> 累计值**继承自父会话**（实测分叉首条为
    /// 31,327,157，而父会话末条为 31,224,644），而 <c>token_usage_record</c>
    /// 只记分叉后新增的调用。因此「求和 ≥ 累计」在分叉上必然不成立，
    /// 这不是数据异常，而是两个字段的口径不同：
    /// 累计值描述整个对话历史，逐次记录描述本文件。
    /// </para>
    /// <para>
    /// 费用仍必须基于逐次记录：分叉前的费用已在父会话里计过，
    /// 若改用累计值会重复计费。
    /// </para>
    /// </remarks>
    private static bool IsForkSession(string logPath)
    {
        var name = Path.GetFileNameWithoutExtension(logPath);

        if (!name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 前缀形如 rollout-2026-09-26T09-17-25-，其中含多个连字符，
        // 因此不能用「最后一个连字符」定位 id 段。
        // 时间戳固定为 2026-09-26T09-17-25（19 字符），
        // 跳过 "rollout-" + 时间戳 + "-" 后剩下的部分才含 id。
        const int timestampLength = 19;
        var offset = "rollout-".Length + timestampLength;

        return name.Length > offset
            && name[offset] == '-'
            && name.IndexOf('_', offset) > 0;
    }

    /// <summary>
    /// 逐次调用记录的最后一条，必须与最后一条 token_count 的 last 用量一致
    /// （除压缩发生的那一次）。这验证了两个数据源指向同一次调用。
    /// </summary>
    [Fact]
    public void RealSessions_LatestUsageRecordMatchesTokenCountWhenNotCompacted()
    {
        foreach (var log in EnumerateLogs())
        {
            if (ReadStable(log) is not { } snapshot)
            {
                continue;
            }
            var latest = snapshot.LatestTokenCount;
            if (latest is null || snapshot.UsageRecords.Count == 0)
            {
                continue;
            }

            var lastRecord = snapshot.UsageRecords[^1].Usage.TotalTokens;
            var lastCount = latest.LastUsage.TotalTokens;

            if (lastRecord == lastCount)
            {
                continue; // 常规情形。
            }

            // 不一致只允许出现在压缩附近：此时 token_count.last 是压缩后的上下文规模，
            // 而 usage record 是压缩调用的真实消耗，后者必然更大。
            Assert.True(
                snapshot.CompactionCount > 0,
                $"{Path.GetFileName(log)}: 无压缩事件却出现 {lastRecord} vs {lastCount} 的不一致。");
            Assert.True(
                lastRecord > lastCount,
                $"{Path.GetFileName(log)}: 压缩后 usage record ({lastRecord}) 应大于上下文规模 ({lastCount})。");
        }
    }

    /// <summary>
    /// 上下文占用不能超过模型窗口容量。
    /// </summary>
    [Fact]
    public void RealSessions_ContextUsageStaysWithinWindow()
    {
        foreach (var log in EnumerateLogs())
        {
            if (ReadStable(log) is not { } snapshot)
            {
                continue;
            }
            var metrics = SessionMetrics.Create(snapshot);
            if (metrics.ContextWindowTokens <= 0)
            {
                continue;
            }

            Assert.InRange(metrics.ContextPercent, 0d, 100d);
        }
    }

    /// <summary>
    /// 计费语义验证：发生压缩的会话，两个数据源必然出现差额。
    /// 这直接支撑「费用必须基于逐次调用记录」这一架构决策。
    /// </summary>
    /// <remarks>
    /// 不适用于分叉会话：它的累计值继承自父会话，
    /// 与自己的逐次记录不可比。
    /// </remarks>
    [Fact]
    public void RealSessions_CompactionCreatesDivergenceBetweenTheTwoSources()
    {
        foreach (var log in EnumerateLogs())
        {
            // 分叉会话的累计值继承自父会话，差额可能为负，不适用本不变量。
            if (IsForkSession(log))
            {
                continue;
            }

            if (ReadStable(log) is not { } snapshot)
            {
                continue;
            }

            var latest = snapshot.LatestTokenCount;
            if (latest is null || snapshot.CompactionCount == 0)
            {
                continue;
            }

            var recordSum = SessionMetrics.Create(snapshot).Cumulative.Total;
            var cumulative = latest.TotalUsage.TotalTokens;

            Assert.True(
                recordSum > cumulative,
                $"{Path.GetFileName(log)}: 存在压缩但两个数据源无差额，压缩未影响累计记账。");
        }
    }

    /// <summary>
    /// 压缩发生的那个 turn，其 usage record 远大于 token_count 记录的增量。
    /// 这是压缩绕过累计记账的直接证据。
    /// </summary>
    [Fact]
    public void RealSessions_CompactionTurnIsAbsentFromCumulativeTotals()
    {
        foreach (var log in EnumerateLogs())
        {
            if (ReadStable(log) is not { } snapshot)
            {
                continue;
            }
            if (snapshot.CompactionCount == 0 || snapshot.UsageRecords.Count < 2)
            {
                continue;
            }

            var cumulativeDeltas = new List<long>();
            for (var i = 1; i < snapshot.TokenCounts.Count; i++)
            {
                cumulativeDeltas.Add(
                    snapshot.TokenCounts[i].TotalUsage.TotalTokens
                    - snapshot.TokenCounts[i - 1].TotalUsage.TotalTokens);
            }

            // 存在某些调用的消耗未进入累计值。
            var hasUnaccountedCall = cumulativeDeltas.Any(delta => delta == 0);
            Assert.True(
                hasUnaccountedCall,
                $"{Path.GetFileName(log)}: 存在压缩却未发现增量为 0 的累计记录。");
        }
    }
}
