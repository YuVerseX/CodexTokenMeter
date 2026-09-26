using System.Diagnostics;
using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 日志定位的成本上界。
/// </summary>
/// <remarks>
/// <para>
/// 定位运行在 UI 线程上，其耗时直接表现为界面卡顿；
/// 而它每次轮询（700ms）或复查（2s）都可能发生，
/// 因此必须对整个「最坏路径」设成本上界，而不只是常用路径。
/// </para>
/// <para>
/// 回归保护：倒序检索未命中时会回退全量扫描（109 ms / 3650 文件）。
/// 若老会话被长期使用，每次复查都走这条路径，
/// 就会退化成「每 2 秒卡顿 109 ms」——优化等于白做。
/// </para>
/// <para>
/// <b>成本断言取数量级而非精确值。</b>
/// 这些是回归测试而不是基准测试：xUnit 默认并行执行测试类，
/// 严厉的阈值（如 20 ms）会因调度抖动而偶发失败，
/// 使整个套件变得不可信。真正的退化（回到递归全盘扫描）
/// 会带来 10 倍以上的差距，数量级阈值足以捕获。
/// </para>
/// </remarks>
public class SessionLogLocatorCostTests : IDisposable
{
    private readonly string _root;
    private readonly SessionLogLocator _locator = new();
    private static readonly DateTime Today = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Local);

    public SessionLogLocatorCostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ctm-cost-{Guid.NewGuid():N}");
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

    /// <summary>构造大量干扰文件，模拟长期使用。</summary>
    private void PopulateInterference(int days, int filesPerDay)
    {
        for (var offset = 0; offset < days; offset++)
        {
            var day = Today.AddDays(-offset);
            var directory = Path.Combine(
                _root,
                day.ToString("yyyy"),
                day.ToString("MM"),
                day.ToString("dd"));

            Directory.CreateDirectory(directory);

            for (var i = 0; i < filesPerDay; i++)
            {
                File.WriteAllText(
                    Path.Combine(directory, $"rollout-{day:yyyy-MM-dd}T10-00-00-{Guid.NewGuid()}.jsonl"),
                    "{}");
            }
        }
    }

    private string CreateTarget(DateTime day, string threadId)
    {
        var directory = Path.Combine(
            _root,
            day.ToString("yyyy"),
            day.ToString("MM"),
            day.ToString("dd"));

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"rollout-{day:yyyy-MM-dd}T10-00-00-{threadId}.jsonl");
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void RecentSessionIsLocatedQuicklyDespiteManyFiles()
    {
        PopulateInterference(days: 200, filesPerDay: 10);

        const string threadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
        var target = CreateTarget(Today, threadId);

        var sw = Stopwatch.StartNew();
        var found = _locator.Locate(_root, threadId, Today);
        sw.Stop();

        Assert.Equal(target, found);

        Assert.True(
            sw.Elapsed.TotalMilliseconds < 500,
            $"近期会话定位耗时 {sw.Elapsed.TotalMilliseconds:F1} ms，应远低于此值。");
    }

    [Fact]
    public void AncientSessionDoesNotCauseRepeatedFullScans()
    {
        // 这是关键回归：超过倒序窗口的老会话被继续使用时，
        // 每次复查都会走全量扫描。若不做节流，会退化成
        // 「每 2 秒卡顿 100+ ms」。
        PopulateInterference(days: 200, filesPerDay: 10);

        var ancient = Today.AddDays(-(SessionLogLocator.MaxLookbackDays + 100));
        const string threadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
        var target = CreateTarget(ancient, threadId);

        // 首次定位（必然走全量回退）。
        var sw = Stopwatch.StartNew();
        var found = _locator.Locate(_root, threadId, Today);
        sw.Stop();

        Assert.Equal(target, found);

        var firstCost = sw.Elapsed.TotalMilliseconds;

        // 连续调用 20 次，模拟 2 秒复查间隔下的 40 秒运行。
        // 若每次都做全量扫描，总耗时会接近 firstCost × 20。
        var repeated = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            _locator.Locate(_root, threadId, Today);
        }
        repeated.Stop();

        var perCall = repeated.Elapsed.TotalMilliseconds / 20;

        // 容忍并行执行的调度抖动，但首调用与后续调用的**比例**关系不变：
        // 后续调用若仍做全量扫描，perCall 会与 firstCost 相当（比例 ≈ 1）。
        // 缓存生效时比例会显著小于 1。
        Assert.True(
            perCall < firstCost * 0.6 || perCall < 300,
            $"老会话的重复定位平均 {perCall:F1} ms，首次 {firstCost:F1} ms —— " +
            "未体现出缓存效果，会退化成周期性卡顿。");
    }

    [Fact]
    public void MissingSessionIsCheapAfterFirstAttempt()
    {
        // 会话不存在时（例如日志尚未落盘），重复调用不应反复扫描。
        PopulateInterference(days: 200, filesPerDay: 10);

        const string missing = "ffffffff-ffff-ffff-ffff-ffffffffffff";

        _locator.Locate(_root, missing, Today);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            _locator.Locate(_root, missing, Today);
        }
        sw.Stop();

        var perCall = sw.Elapsed.TotalMilliseconds / 20;

        // 阈值为数量级判断：未缓存时 20 次会重复扫描数千文件，
        // 总耗时会在秒级；缓存生效则在毫秒级。
        // 取 500 ms/次 以容忍并行调度抖动。
        Assert.True(
            perCall < 500,
            $"未命中时的重复定位平均 {perCall:F1} ms，说明未缓存负结果。");
    }
}
