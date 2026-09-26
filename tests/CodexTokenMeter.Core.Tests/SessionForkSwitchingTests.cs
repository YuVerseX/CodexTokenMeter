using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Metrics;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 分叉会话被创建后，监视器切换到新文件。
/// </summary>
/// <remarks>
/// <para>
/// 这是端到端的行为验证：构造父会话与分叉会话的真实文件，
/// 让监视器先跟随父文件，再让定位器发现分叉文件，验证数据正确切换。
/// </para>
/// <para>
/// 回归保护：早期实现只在「thread id 变化」时重置监视器，
/// 而分叉场景下 thread id 不变（IPC 广播的仍是父 id），
/// 只有文件路径变化。若不重置，会把父文件的偏移与分叉文件的累积混在一起。
/// </para>
/// </remarks>
public class SessionForkSwitchingTests : IDisposable
{
    private readonly string _root;
    private static readonly DateTime Today = new(2026, 9, 26, 15, 30, 0, DateTimeKind.Local);

    private const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
    private const string ChildId = "01a0db49-da6c-7f43-ac7f-014858381ce4";

    public SessionForkSwitchingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ctm-fork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>构造一行 token_usage_record。</summary>
    private static string UsageLine(string turnId, long input, long output)
    {
        var total = input + output;

        // 用拼接而不是原始插值字符串：JSON 里的连续右花括号
        // 会与插值的界定符冲突。
        return "{\"timestamp\":\"2026-09-26T10:00:00Z\",\"type\":\"token_usage_record\","
            + "\"payload\":{\"turn_id\":\"" + turnId + "\",\"usage\":{"
            + "\"input_tokens\":" + input + ","
            + "\"cached_input_tokens\":0,"
            + "\"cache_write_input_tokens\":0,"
            + "\"output_tokens\":" + output + ","
            + "\"reasoning_output_tokens\":0,"
            + "\"total_tokens\":" + total
            + "}}}";
    }

    /// <summary>构造一行 token_count。</summary>
    private static string TokenCountLine(long lastTotal, long cumulativeTotal)
    {
        return "{\"timestamp\":\"2026-09-26T10:00:01Z\",\"type\":\"event_msg\","
            + "\"payload\":{\"type\":\"token_count\",\"info\":{"
            + "\"last_token_usage\":{\"input_tokens\":" + lastTotal
            + ",\"total_tokens\":" + lastTotal + "},"
            + "\"total_token_usage\":{\"input_tokens\":" + cumulativeTotal
            + ",\"total_tokens\":" + cumulativeTotal + "},"
            + "\"model_context_window\":258400}}}";
    }

    private string WriteLog(DateTime day, string fileName, params string[] lines)
    {
        var directory = Path.Combine(
            _root,
            day.ToString("yyyy"),
            day.ToString("MM"),
            day.ToString("dd"));

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, day.ToUniversalTime());

        return path;
    }

    [Fact]
    public void SwitchingToForkReplacesAccumulatedData()
    {
        var yesterday = Today.AddDays(-1);

        // 父会话：昨天，累计 100 万。
        var parentPath = WriteLog(
            yesterday,
            $"rollout-{yesterday:yyyy-MM-dd}T10-00-00-{ThreadId}.jsonl",
            UsageLine("turn-parent", 900_000, 100_000),
            TokenCountLine(1_000_000, 1_000_000));

        // 分叉会话：今天，只记自己新增的 5 万。
        var forkPath = WriteLog(
            Today,
            $"rollout-{Today:yyyy-MM-dd}T09-00-00-{ThreadId}_{ChildId}.jsonl",
            UsageLine("turn-fork", 45_000, 5_000),
            TokenCountLine(50_000, 1_050_000));

        using var monitor = new SessionLogMonitor();

        // 第一步：跟随父文件。
        var parentSnapshot = monitor.Poll(parentPath);
        Assert.Equal(1_000_000, SessionMetrics.Create(parentSnapshot).Cumulative.Total);

        // 第二步：定位到分叉文件（模拟 2 秒复查后发现了新文件）。
        var located = SessionLogLocator.Locate(_root, ThreadId, Today);
        Assert.Equal(forkPath, located);

        // 第三步：切换到分叉文件，累计必须**只**反映分叉自己的记录。
        var forkSnapshot = monitor.Poll(located!);
        var forkMetrics = SessionMetrics.Create(forkSnapshot);

        Assert.Equal(50_000, forkMetrics.Cumulative.Total);
    }

    [Fact]
    public void ForkAccumulationDoesNotLeakFromParent()
    {
        var yesterday = Today.AddDays(-1);

        var parentPath = WriteLog(
            yesterday,
            $"rollout-{yesterday:yyyy-MM-dd}T10-00-00-{ThreadId}.jsonl",
            UsageLine("turn-a", 100_000, 10_000),
            UsageLine("turn-b", 200_000, 20_000));

        var forkPath = WriteLog(
            Today,
            $"rollout-{Today:yyyy-MM-dd}T09-00-00-{ThreadId}_{ChildId}.jsonl",
            UsageLine("turn-c", 7_000, 700));

        using var monitor = new SessionLogMonitor();

        monitor.Poll(parentPath);
        var forkSnapshot = monitor.Poll(forkPath);
        var metrics = SessionMetrics.Create(forkSnapshot);

        // 只有 turn-c 的 7,700，不含父会话的 330,000。
        Assert.Equal(7_700, metrics.Cumulative.Total);
        Assert.Equal(7_700, metrics.CurrentTurn.Total);
        Assert.Equal(1, forkSnapshot.CurrentTurnCallCount);
    }

    [Fact]
    public void SwitchingBackAndForthStaysConsistent()
    {
        var yesterday = Today.AddDays(-1);

        var parentPath = WriteLog(
            yesterday,
            $"rollout-{yesterday:yyyy-MM-dd}T10-00-00-{ThreadId}.jsonl",
            UsageLine("turn-parent", 100_000, 0),
            TokenCountLine(100_000, 100_000));

        var forkPath = WriteLog(
            Today,
            $"rollout-{Today:yyyy-MM-dd}T09-00-00-{ThreadId}_{ChildId}.jsonl",
            UsageLine("turn-fork", 5_000, 0),
            TokenCountLine(5_000, 105_000));

        using var monitor = new SessionLogMonitor();

        // 反复来回切换，每次都必须得到该文件自己的正确值。
        for (var round = 0; round < 3; round++)
        {
            var parent = SessionMetrics.Create(monitor.Poll(parentPath));
            Assert.Equal(100_000, parent.Cumulative.Total);

            var fork = SessionMetrics.Create(monitor.Poll(forkPath));
            Assert.Equal(5_000, fork.Cumulative.Total);
        }
    }

    [Fact]
    public void IncrementalGrowthAfterSwitchIsAccumulated()
    {
        // 切到分叉后文件继续增长，新增的记录必须被累积。
        var forkPath = WriteLog(
            Today,
            $"rollout-{Today:yyyy-MM-dd}T09-00-00-{ThreadId}_{ChildId}.jsonl",
            UsageLine("turn-1", 1_000, 100));

        using var monitor = new SessionLogMonitor();

        var first = SessionMetrics.Create(monitor.Poll(forkPath));
        Assert.Equal(1_100, first.Cumulative.Total);

        // 追加一条记录。
        File.AppendAllLines(forkPath, [UsageLine("turn-1", 2_000, 200)]);

        var second = SessionMetrics.Create(monitor.Poll(forkPath));
        Assert.Equal(3_300, second.Cumulative.Total);

        // 同一个 turn 的两条记录应累加到本轮。
        Assert.Equal(2, monitor.Poll(forkPath).CurrentTurnCallCount);
    }

    [Fact]
    public void ParentStillPreferredWhenForkDoesNotExist()
    {
        // 没有分叉时，定位器仍应返回父文件。
        var yesterday = Today.AddDays(-1);
        var parentPath = WriteLog(
            yesterday,
            $"rollout-{yesterday:yyyy-MM-dd}T10-00-00-{ThreadId}.jsonl",
            UsageLine("turn-a", 1_000, 0));

        Assert.Equal(parentPath, SessionLogLocator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void LocatorFollowsForkCreatedLater()
    {
        // 模拟运行期间产生分叉：先只有父文件，随后出现分叉文件。
        var yesterday = Today.AddDays(-1);
        var parentPath = WriteLog(
            yesterday,
            $"rollout-{yesterday:yyyy-MM-dd}T10-00-00-{ThreadId}.jsonl",
            UsageLine("turn-a", 1_000, 0));

        Assert.Equal(parentPath, SessionLogLocator.Locate(_root, ThreadId, Today));

        // 分叉出现。
        var forkPath = WriteLog(
            Today,
            $"rollout-{Today:yyyy-MM-dd}T16-00-00-{ThreadId}_{ChildId}.jsonl",
            UsageLine("turn-b", 2_000, 0));

        // 复查必须发现它。
        Assert.Equal(forkPath, SessionLogLocator.Locate(_root, ThreadId, Today));
    }
}
