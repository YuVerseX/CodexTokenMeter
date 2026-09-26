using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 定位器的缓存与分叉发现延迟。
/// </summary>
/// <remarks>
/// <para>
/// 定位器内部有缓存（避免老会话反复触发全量扫描），
/// 而 Provider 也按固定间隔复查以发现分叉。
/// 两层缓存叠加会决定「分叉产生后多久被跟随」，必须验证这个延迟可接受。
/// </para>
/// <para>
/// 回归保护：若内层缓存把普通命中结果缓存过久，
/// 分叉产生后浮层会继续显示旧会话的数据。
/// </para>
/// </remarks>
public class SessionLogLocatorCacheTests : IDisposable
{
    private readonly string _root;
    private readonly SessionLogLocator _locator = new();
    private static readonly DateTime Today = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Local);

    private const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
    private const string ChildId = "01a0db49-da6c-7f43-ac7f-014858381ce4";

    public SessionLogLocatorCacheTests()
    {
        // 每个测试用独立根目录，避免静态缓存互相干扰。
        _root = Path.Combine(Path.GetTempPath(), $"ctm-cache-{Guid.NewGuid():N}");
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
    public void ParentIsFoundWhenNoForkExists()
    {
        var parent = CreateLog(Today, $"rollout-2026-09-26T10-00-00-{ThreadId}.jsonl");

        Assert.Equal(parent, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void ForkIsDiscoveredAfterCacheExpires()
    {
        var parent = CreateLog(Today, $"rollout-2026-09-26T10-00-00-{ThreadId}.jsonl");

        // 第一次定位命中父文件，结果进入缓存。
        Assert.Equal(parent, _locator.Locate(_root, ThreadId, Today));

        // 分叉出现。
        var fork = CreateLog(Today, $"rollout-2026-09-26T16-00-00-{ThreadId}_{ChildId}.jsonl");

        // 缓存期内仍返回父文件（这是预期行为，缓存就是为了避免频繁扫描）。
        Assert.Equal(parent, _locator.Locate(_root, ThreadId, Today));

        // 等缓存过期后必须发现分叉。
        Thread.Sleep(1200);

        Assert.Equal(fork, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void DeletedFileInvalidatesCacheImmediately()
    {
        // 缓存里的路径被删除时必须立即重新定位，而不是返回失效路径。
        var parent = CreateLog(Today, $"rollout-2026-09-26T10-00-00-{ThreadId}.jsonl");

        Assert.Equal(parent, _locator.Locate(_root, ThreadId, Today));

        File.Delete(parent);

        var fork = CreateLog(Today, $"rollout-2026-09-26T16-00-00-{ThreadId}_{ChildId}.jsonl");

        // 不需要等缓存过期：文件不存在会立即作废缓存。
        Assert.Equal(fork, _locator.Locate(_root, ThreadId, Today));
    }

    [Fact]
    public void MissingSessionIsCachedBriefly()
    {
        // 会话不存在时，短时间内重复调用不应反复扫描。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            Assert.Null(_locator.Locate(_root, "ffffffff-ffff-ffff-ffff-ffffffffffff", Today));
        }
        sw.Stop();

        Assert.True(
            sw.Elapsed.TotalMilliseconds < 100,
            $"50 次未命中查询耗时 {sw.Elapsed.TotalMilliseconds:F1} ms，说明负结果未被缓存。");
    }

    [Fact]
    public void DifferentRootsDoNotShareCacheEntries()
    {
        // 静态缓存以 (根目录, threadId) 为键，不同根目录不应互相命中。
        var otherRoot = Path.Combine(Path.GetTempPath(), $"ctm-cache2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherRoot);

        try
        {
            var here = CreateLog(Today, $"rollout-2026-09-26T10-00-00-{ThreadId}.jsonl");

            var otherDirectory = Path.Combine(
                otherRoot,
                "2026",
                "09",
                "26");
            Directory.CreateDirectory(otherDirectory);
            var there = Path.Combine(
                otherDirectory,
                $"rollout-2026-09-26T11-00-00-{ThreadId}.jsonl");
            File.WriteAllText(there, "{}");

            Assert.Equal(here, _locator.Locate(_root, ThreadId, Today));
            Assert.Equal(there, _locator.Locate(otherRoot, ThreadId, Today));

            // 再查一次，确认两个根目录各自返回自己的结果。
            Assert.Equal(here, _locator.Locate(_root, ThreadId, Today));
            Assert.Equal(there, _locator.Locate(otherRoot, ThreadId, Today));
        }
        finally
        {
            try
            {
                Directory.Delete(otherRoot, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }

    [Fact]
    public void ThreadIdMatchingIsCaseInsensitiveAcrossCache()
    {
        var parent = CreateLog(Today, $"rollout-2026-09-26T10-00-00-{ThreadId}.jsonl");

        Assert.Equal(parent, _locator.Locate(_root, ThreadId, Today));
        Assert.Equal(parent, _locator.Locate(_root, ThreadId.ToUpperInvariant(), Today));
    }
}
