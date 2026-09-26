using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 释放与并发使用的关系。
/// </summary>
/// <remarks>
/// <para>
/// 释放后继续调用必须抛出 <see cref="ObjectDisposedException"/>，
/// 而不是返回已释放的缓冲内容或崩溃在未定义状态。
/// </para>
/// <para>
/// 这是为了配合调用方做「释放时立刻拒绝新调用」的判断：
/// <c>OverlayDataProvider.Poll</c> 的 <c>_disposed</c> 检查不是原子的，
/// 释放过程中的并发调用会穿过去，此时监视器必须可靠地拒绝，
/// 而不是访问已释放的 <c>MemoryStream</c>。
/// </para>
/// </remarks>
public class SessionLogMonitorDisposalTests
{
    [Fact]
    public void PollAfterDisposeThrows()
    {
        var path = CreateLog();

        try
        {
            var monitor = new SessionLogMonitor();
            monitor.Dispose();

            Assert.Throws<ObjectDisposedException>(() => monitor.Poll(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResetAfterDisposeThrows()
    {
        var monitor = new SessionLogMonitor();
        monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(() => monitor.Reset());
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var monitor = new SessionLogMonitor();

        monitor.Dispose();
        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void ResetBeforeDisposeWorks()
    {
        var path = CreateLog();

        try
        {
            using var monitor = new SessionLogMonitor();

            monitor.Poll(path);
            monitor.Reset();
            monitor.Reset(path);

            // 重置后应能从新文件重新解析。
            var snapshot = monitor.Poll(path);
            Assert.NotNull(snapshot);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConcurrentPollAndDisposeDoesNotCorrupt()
    {
        // 释放过程中的并发调用可能穿过后检（见类型文档），
        // 此时要么正常完成、要么抛 ObjectDisposedException，
        // 不允许出现其它异常（例如访问已释放对象而抛 ObjectDisposedException
        // 以外的类型，或内部状态损坏）。
        var path = CreateLog();

        try
        {
            for (var round = 0; round < 30; round++)
            {
                var monitor = new SessionLogMonitor();
                var failures = new List<Exception>();
                var completed = 0;

                var poller = new Thread(() =>
                {
                    try
                    {
                        for (var i = 0; i < 100; i++)
                        {
                            monitor.Poll(path);
                            Interlocked.Increment(ref completed);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // 预期：释放后拒绝服务。
                    }
                    catch (Exception exception)
                    {
                        lock (failures)
                        {
                            failures.Add(exception);
                        }
                    }
                });

                poller.Start();

                // 把释放插入到轮询循环中间，最大化交错概率。
                if (round % 3 == 0)
                {
                    monitor.Dispose();
                }
                else
                {
                    Thread.Sleep(1);
                    monitor.Dispose();
                }

                poller.Join();

                Assert.True(
                    failures.Count == 0,
                    $"第 {round} 轮出现非预期异常：{failures.FirstOrDefault()?.GetType().Name} " +
                    $"– {failures.FirstOrDefault()?.Message}（已完成 {completed} 次轮询）");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConcurrentResetAndPollStaysConsistent()
    {
        // 重置与轮询并发时，不得出现内部状态错乱。
        // 典型症状是快照里出现两份不同文件的记录混在一起。
        var first = CreateLog();
        var second = CreateLog();

        try
        {
            using var monitor = new SessionLogMonitor();
            var failures = new List<Exception>();

            var poller = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 200; i++)
                    {
                        var snapshot = monitor.Poll(i % 2 == 0 ? first : second);

                        // 每次调用的累计必须恰好等于单个文件的记录量（110）。
                        if (snapshot.Cumulative.Total != 110)
                        {
                            throw new InvalidOperationException(
                                $"累计值异常：{snapshot.Cumulative.Total}，应为 110。");
                        }
                    }
                }
                catch (Exception exception)
                {
                    lock (failures)
                    {
                        failures.Add(exception);
                    }
                }
            });

            poller.Start();
            poller.Join();

            Assert.True(
                failures.Count == 0,
                $"出现 {failures.Count} 次异常，首个：{failures.FirstOrDefault()?.Message}");
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private static string CreateLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctm-dispose-{Guid.NewGuid():N}.jsonl");

        File.WriteAllLines(path,
        [
            """
            {"timestamp":"2026-09-26T10:00:00Z","type":"token_usage_record","payload":{"turn_id":"t1","usage":{"input_tokens":100,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":0,"total_tokens":110}}}
            """,
        ]);

        return path;
    }
}
