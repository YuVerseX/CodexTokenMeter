using System.Diagnostics;
using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 分叉被发现的最坏延迟。
/// </summary>
/// <remarks>
/// <para>
/// 用户「在新窗口继续此会话」后，浮层需要多久才跟随到新会话？
/// 这由两层缓存叠加决定：
/// <list type="number">
/// <item>Provider 的复查间隔（2 秒）</item>
/// <item>定位器内部的普通结果缓存（1 秒）</item>
/// </list>
/// 最坏情况是两者都刚好错过，因此上界约 3 秒。
/// </para>
/// <para>
/// 这个延迟是设计取舍的结果而非缺陷：不缓存会让老会话场景
/// 每 2 秒卡顿 109 ms。但必须验证它确实有界，且量级可接受。
/// </para>
/// </remarks>
public class ForkDetectionLatencyTests : IDisposable
{
    private readonly string _root;
    private readonly SessionLogLocator _locator = new();
    private static readonly DateTime Today = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Local);

    private const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
    private const string ChildId = "01a0db49-da6c-7f43-ac7f-014858381ce4";

    public ForkDetectionLatencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ctm-latency-{Guid.NewGuid():N}");
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

    private string CreateLog(DateTime day, string fileName)
    {
        var directory = Path.Combine(
            _root,
            day.ToString("yyyy"),
            day.ToString("MM"),
            day.ToString("dd"));

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void ForkIsDetectedWithinBoundedTime()
    {
        // 父会话：昨天。
        var yesterday = Today.AddDays(-1);
        var parent = CreateLog(yesterday, $"rollout-{yesterday:yyyy-MM-dd}T10-00-00-{ThreadId}.jsonl");

        Assert.Equal(parent, _locator.Locate(_root, ThreadId, Today));

        // 分叉产生。记录时刻，然后按 Provider 的复查节奏轮询。
        var fork = CreateLog(Today, $"rollout-{Today:yyyy-MM-dd}T16-00-00-{ThreadId}_{ChildId}.jsonl");
        var start = Stopwatch.StartNew();

        string? detected = null;

        // 模拟 Provider：每 2 秒调用一次，最多等 10 秒。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(2000);

            var found = _locator.Locate(_root, ThreadId, Today);
            if (string.Equals(found, fork, StringComparison.OrdinalIgnoreCase))
            {
                detected = found;
                break;
            }
        }

        start.Stop();

        Assert.Equal(fork, detected);
        Assert.True(
            start.Elapsed.TotalSeconds <= 5,
            $"分叉发现耗时 {start.Elapsed.TotalSeconds:F1} 秒，超出可接受上界。");
    }

    [Fact]
    public void RecentSessionIsNotDelayedByCache()
    {
        // 会话切换（不同 threadId）不应受上一个会话的缓存影响。
        const string firstThread = "01a0d656-cf19-7532-af4a-f089fc3163ed";
        const string secondThread = "02b1e767-da2b-8643-bf5b-f19a5274d204";

        CreateLog(Today, $"rollout-2026-09-26T10-00-00-{firstThread}.jsonl");
        var second = CreateLog(Today, $"rollout-2026-09-26T11-00-00-{secondThread}.jsonl");

        _locator.Locate(_root, firstThread, Today);

        // 立即查另一个会话，不应因第一个的缓存而变慢或出错。
        var sw = Stopwatch.StartNew();
        var found = _locator.Locate(_root, secondThread, Today);
        sw.Stop();

        Assert.Equal(second, found);
        Assert.True(
            sw.Elapsed.TotalMilliseconds < 50,
            $"切换会话后首次定位耗时 {sw.Elapsed.TotalMilliseconds:F1} ms。");
    }
}
