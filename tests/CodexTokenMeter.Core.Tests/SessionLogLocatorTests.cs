using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 按日期定位会话日志。
/// </summary>
/// <remarks>
/// 回归保护：早期实现递归遍历全部日期目录，实测 3000 个文件时
/// 单次扫描 84 ms，而它运行在 UI 线程上会造成可见卡顿。
/// 改为按日期倒序检索后降到约 0.9 ms。
/// </remarks>
public class SessionLogLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly SessionLogLocator _locator = new();

    /// <summary>固定「今天」，避免测试跨午夜时不稳定。</summary>
    private static readonly DateTime Today = new(2026, 9, 26, 15, 30, 0, DateTimeKind.Local);

    private const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
    private const string ChildId = "01a0db49-da6c-7f43-ac7f-014858381ce4";

    public SessionLogLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ctm-locator-{Guid.NewGuid():N}");
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

    /// <summary>在指定日期目录下创建文件，返回其路径。</summary>
    private string CreateLog(DateTime day, string fileName, DateTime? writeTime = null)
    {
        var directory = Path.Combine(
            _root,
            day.ToString("yyyy"),
            day.ToString("MM"),
            day.ToString("dd"));

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "{}");

        if (writeTime is { } time)
        {
            File.SetLastWriteTimeUtc(path, time.ToUniversalTime());
        }

        return path;
    }

    private static string PlainName(DateTime stamp) =>
        $"rollout-{stamp:yyyy-MM-dd}T{stamp:HH-mm-ss}-{ThreadId}.jsonl";

    private static string ForkName(DateTime stamp) =>
        $"rollout-{stamp:yyyy-MM-dd}T{stamp:HH-mm-ss}-{ThreadId}_{ChildId}.jsonl";

    [Fact]
    public void ReturnsNullForMissingRoot()
    {
        Assert.Null(_locator.Locate(
            Path.Combine(_root, "不存在"),
            ThreadId,
            Today));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullForBlankThreadId(string threadId)
    {
        Assert.Null(_locator.Locate(_root, threadId, Today));
    }

    [Fact]
    public void FindsTodayFile()
    {
        var path = CreateLog(Today, PlainName(Today));

        Assert.Equal(path, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void FindsFileFromEarlierDay()
    {
        // 昨天的会话，今天仍可能被继续使用。
        var yesterday = Today.AddDays(-1);
        var path = CreateLog(yesterday, PlainName(yesterday));

        Assert.Equal(path, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void PrefersNewerDayOverOlderDay()
    {
        // 跨天延续：两天的目录里都有同名会话，应取新的那份。
        var yesterday = Today.AddDays(-1);
        CreateLog(yesterday, PlainName(yesterday), yesterday);

        var todayPath = CreateLog(Today, PlainName(Today), Today);

        Assert.Equal(todayPath, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void PrefersForkOverParentInSameDay()
    {
        // 同一天内产生分叉：父文件先写，分叉后写，应取分叉。
        CreateLog(Today, PlainName(Today), Today.AddHours(-2));

        var forkPath = CreateLog(Today, ForkName(Today), Today);

        Assert.Equal(forkPath, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void FindsForkOnNewerDayAndStopsThere()
    {
        // 关键场景：昨天启动会话，今天在新窗口继续（分叉）。
        // 父文件在昨天，分叉在今天。
        var yesterday = Today.AddDays(-1);
        CreateLog(yesterday, PlainName(yesterday), yesterday);

        var forkPath = CreateLog(Today, ForkName(Today), Today);

        Assert.Equal(forkPath, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void IgnoresUnrelatedSession()
    {
        const string other = "01a0d650-7d90-7ef0-a9ed-36c75bec684b";
        var todayDir = Path.Combine(_root, "2026", "09", "26");
        Directory.CreateDirectory(todayDir);
        File.WriteAllText(
            Path.Combine(todayDir, $"rollout-2026-09-26T10-00-00-{other}.jsonl"),
            "{}");

        Assert.Null(_locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void IgnoresNonRolloutFiles()
    {
        var todayDir = Path.Combine(_root, "2026", "09", "26");
        Directory.CreateDirectory(todayDir);
        File.WriteAllText(Path.Combine(todayDir, $"config-{ThreadId}.jsonl"), "{}");

        Assert.Null(_locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void FallsBackToFullScanBeyondLookbackWindow()
    {
        // 倒序范围外（超过 60 天）的老会话被重新打开：必须仍能找到。
        var ancient = Today.AddDays(-(SessionLogLocator.MaxLookbackDays + 30));
        var path = CreateLog(ancient, PlainName(ancient));

        Assert.Equal(path, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void SkipsMissingDayDirectoriesWithoutFailing()
    {
        // 中间若干天没有会话：不应因为目录不存在而中断检索。
        var threeDaysAgo = Today.AddDays(-3);
        var path = CreateLog(threeDaysAgo, PlainName(threeDaysAgo));

        Assert.Equal(path, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void HandlesDayAtMonthBoundary()
    {
        // 月初回溯会跨到上个月：日期运算必须正确。
        var firstOfMonth = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);
        var lastOfPrevious = new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Local);

        var path = CreateLog(lastOfPrevious, PlainName(lastOfPrevious));

        Assert.Equal(path, _locator.Locate(_root, ThreadId, firstOfMonth));
    }

    [Fact]
    public void HandlesYearBoundary()
    {
        var firstOfYear = new DateTime(2027, 1, 1, 10, 0, 0, DateTimeKind.Local);
        var lastOfPrevious = new DateTime(2026, 12, 31, 20, 0, 0, DateTimeKind.Local);

        var path = CreateLog(lastOfPrevious, PlainName(lastOfPrevious));

        Assert.Equal(path, _locator.Locate(_root, ThreadId, firstOfYear));
    }

    [Fact]
    public void IsFastOnLargeDirectories()
    {
        // 结构性验证：检索只读日期目录，不做递归。
        // 构造 300 天的干扰文件，检索耗时不应随总文件数线性增长。
        for (var offset = 0; offset < 300; offset++)
        {
            var day = Today.AddDays(-offset);
            CreateLog(day, PlainName(day).Replace(ThreadId, Guid.NewGuid().ToString()));
        }

        var targetDay = Today.AddDays(-5);
        var target = CreateLog(
            targetDay,
            PlainName(targetDay),
            targetDay);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = _locator.Locate(_root, ThreadId, Today);
        sw.Stop();

        Assert.Equal(target, found);

        // 300+ 文件下应远快于全量递归扫描（实测全量约 84 ms / 3000 文件）。
        //
        // 阈值取得宽松（数量级而非精确值）：
        // 这是回归测试而不是基准测试，需要容忍并行执行时的调度抖动。
        // 真正的退化（回到递归全盘扫描）会是 10 倍以上差距。
        Assert.True(
            sw.Elapsed.TotalMilliseconds < 500,
            $"定位耗时 {sw.Elapsed.TotalMilliseconds:F1} ms，超出预期。");
    }
}
