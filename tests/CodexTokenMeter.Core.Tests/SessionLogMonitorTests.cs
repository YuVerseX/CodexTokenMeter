using System.Text;
using System.Text.Json;
using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 增量解析的行为验证。重点覆盖尾部窗口会漏数据这一缺陷。
/// </summary>
public class SessionLogMonitorTests : IDisposable
{
    private readonly string _directory;

    public SessionLogMonitorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "CodexTokenMeterTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }

    private string LogPath => Path.Combine(
        _directory,
        "rollout-2026-09-25T10-00-00-01a0d656-cf19-7532-af4a-f089fc3163ed.jsonl");

    [Fact]
    public void Poll_AccumulatesRecordsAcrossAppendsLikeCodexDoes()
    {
        // 模拟 Codex 的行为：先写一批，再持续追加。
        File.WriteAllText(LogPath, Line("token_usage_record", UsagePayload("turn-1", 1000, 100)));
        var monitor = new SessionLogMonitor();

        var first = monitor.Poll(LogPath);
        Assert.Single(first.UsageRecords);
        Assert.Equal(1000, first.UsageRecords[0].Usage.InputTokens);

        File.AppendAllText(LogPath, Line("token_usage_record", UsagePayload("turn-2", 2000, 200)));
        var second = monitor.Poll(LogPath);

        Assert.Equal(2, second.UsageRecords.Count);
        Assert.Equal(3000, second.UsageRecords.Sum(r => r.Usage.InputTokens));
    }

    [Fact]
    public void Poll_DoesNotReprocessAlreadyConsumedBytes()
    {
        File.WriteAllText(LogPath, Line("token_usage_record", UsagePayload("turn-1", 500, 50)));
        var monitor = new SessionLogMonitor();
        monitor.Poll(LogPath);

        // 再次轮询且文件未变化：记录数不应增长。
        var snapshot = monitor.Poll(LogPath);

        Assert.Single(snapshot.UsageRecords);
    }

    [Fact]
    public void Poll_HoldsBackPartialTrailingLineUntilComplete()
    {
        var complete = Line("token_usage_record", UsagePayload("turn-1", 100, 10));
        File.WriteAllText(LogPath, complete);
        var monitor = new SessionLogMonitor();
        monitor.Poll(LogPath);

        // 追加一个尚未写完的行（无结尾换行）。
        var partial = Line("token_usage_record", UsagePayload("turn-2", 999, 99));
        File.AppendAllText(LogPath, partial[..(partial.Length / 2)]);

        var duringWrite = monitor.Poll(LogPath);
        Assert.Single(duringWrite.UsageRecords);
        Assert.Equal(0, duringWrite.UnreadableLineCount);

        // 补全该行后，应被正常解析。
        File.AppendAllText(LogPath, partial[(partial.Length / 2)..] + "\n");

        var afterComplete = monitor.Poll(LogPath);
        Assert.Equal(2, afterComplete.UsageRecords.Count);
        Assert.Equal("turn-2", afterComplete.UsageRecords[1].TurnId);
        Assert.Equal(0, afterComplete.UnreadableLineCount);
    }

    [Fact]
    public void Poll_ResetsWhenFileIsTruncated()
    {
        File.WriteAllText(LogPath,
            Line("token_usage_record", UsagePayload("turn-1", 100, 10)) + "\n" +
            Line("token_usage_record", UsagePayload("turn-2", 200, 20)) + "\n");

        var monitor = new SessionLogMonitor();
        Assert.Equal(2, monitor.Poll(LogPath).UsageRecords.Count);

        // 文件被替换成更短的内容（例如会话被重置）。
        File.WriteAllText(LogPath, Line("token_usage_record", UsagePayload("turn-9", 7, 1)) + "\n");

        var afterReset = monitor.Poll(LogPath);

        Assert.Single(afterReset.UsageRecords);
        Assert.Equal("turn-9", afterReset.UsageRecords[0].TurnId);
    }

    [Fact]
    public void Poll_ResetsWhenSwitchingSessions()
    {
        var otherPath = Path.Combine(
            _directory,
            "rollout-2026-09-25T11-00-00-01a0d999-0000-0000-0000-000000000000.jsonl");

        File.WriteAllText(LogPath, Line("token_usage_record", UsagePayload("turn-1", 100, 10)));
        File.WriteAllText(otherPath, Line("token_usage_record", UsagePayload("turn-2", 200, 20)));

        var monitor = new SessionLogMonitor();
        Assert.Equal(100, monitor.Poll(LogPath).UsageRecords[0].Usage.InputTokens);

        var switched = monitor.Poll(otherPath);

        Assert.Single(switched.UsageRecords);
        Assert.Equal(200, switched.UsageRecords[0].Usage.InputTokens);
        Assert.Equal("01a0d999-0000-0000-0000-000000000000", switched.ThreadId);
    }

    [Fact]
    public void Poll_IsCorrectWhenChunkBoundaryFallsInsideALine()
    {
        // 固定块大小的实现曾在每个块边界误报一个「不可读行」并丢记录：
        // 块尾的半行被当成完整行解析。这里用 1 字节块把每个字节
        // 都变成块边界，从而确定性地覆盖这种情况。
        var builder = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            builder.AppendLine(Line("token_usage_record", UsagePayload($"turn-{i}", 100, 10)));
        }

        File.WriteAllText(LogPath, builder.ToString());

        var monitor = new SessionLogMonitor(chunkBytes: 1);
        var snapshot = monitor.Poll(LogPath);

        Assert.Equal(200, snapshot.UsageRecords.Count);
        Assert.Equal(0, snapshot.UnreadableLineCount);
        Assert.True(snapshot.IsCaughtUp);
    }

    [Fact]
    public void Poll_MatchesFullReadAcrossManyChunkSizes()
    {
        // 分块大小不得影响结果。逐个验证多个非对齐尺寸。
        var builder = new StringBuilder();
        for (var i = 0; i < 300; i++)
        {
            builder.AppendLine(Line("token_usage_record", UsagePayload($"turn-{i}", 7 * i + 1, 3)));
        }

        File.WriteAllText(LogPath, builder.ToString());

        var expected = SessionLogReader.Read(LogPath);
        var expectedTotal = expected.UsageRecords.Sum(r => r.Usage.TotalTokens);

        foreach (var chunkBytes in new[] { 1, 2, 3, 7, 13, 64, 127, 128, 129, 1000, 4096 })
        {
            var monitor = new SessionLogMonitor(chunkBytes: chunkBytes);
            var actual = monitor.Poll(LogPath);

            Assert.Equal(expected.UsageRecords.Count, actual.UsageRecords.Count);
            Assert.Equal(expectedTotal, actual.UsageRecords.Sum(r => r.Usage.TotalTokens));
            Assert.Equal(0, actual.UnreadableLineCount);
        }
    }

    [Fact]
    public void Poll_IsCorrectWhenChunkBoundarySplitsMultiByteCharacter()
    {
        // 块边界切在 UTF-8 多字节字符中间时，偏移推进必须仍然正确。
        var builder = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            builder.AppendLine(Line("turn_context", new
            {
                turn_id = $"turn-{i}",
                model = "gpt-6-sol",
                cwd = @"E:\代码\三维重构\测试数据",
            }));
            builder.AppendLine(Line("token_usage_record", UsagePayload($"turn-{i}", 100, 10)));
        }

        File.WriteAllText(LogPath, builder.ToString());

        var expected = SessionLogReader.Read(LogPath);

        foreach (var chunkBytes in new[] { 1, 2, 3, 5, 17, 63 })
        {
            var monitor = new SessionLogMonitor(chunkBytes: chunkBytes);
            var actual = monitor.Poll(LogPath);

            Assert.Equal(expected.UsageRecords.Count, actual.UsageRecords.Count);
            Assert.Equal(expected.Turns.Count, actual.Turns.Count);
            Assert.Equal(0, actual.UnreadableLineCount);
        }
    }

    [Fact]
    public void Poll_SplitsPartialLineAcrossChunksIntoOneRecord()
    {
        // 一个行的字节跨越多个块时，必须拼完整后只产生一条记录。
        var line = Line("token_usage_record", UsagePayload("turn-1", 123456, 789));
        File.WriteAllText(LogPath, line + "\n");

        var blockSize = line.Length / 4;
        var monitor = new SessionLogMonitor(chunkBytes: blockSize);
        var snapshot = monitor.Poll(LogPath);

        var record = Assert.Single(snapshot.UsageRecords);
        Assert.Equal(123456, record.Usage.InputTokens);
        Assert.Equal(0, snapshot.UnreadableLineCount);
    }

    [Fact]
    public void Poll_ReturnsSameSnapshotInstanceWhenNothingChanged()
    {
        // 轮询频率远高于日志写入频率。若无缓存，每次轮询都会重建并复制
        // 全部记录，在长会话上造成持续的分配压力。
        File.WriteAllText(LogPath, Line("token_usage_record", UsagePayload("turn-1", 100, 10)) + "\n");
        var monitor = new SessionLogMonitor();

        var first = monitor.Poll(LogPath);
        var second = monitor.Poll(LogPath);
        var third = monitor.Poll(LogPath);

        Assert.Same(first, second);
        Assert.Same(second, third);
    }

    [Fact]
    public void Poll_RebuildsSnapshotAfterNewDataArrives()
    {
        File.WriteAllText(LogPath, Line("token_usage_record", UsagePayload("turn-1", 100, 10)) + "\n");
        var monitor = new SessionLogMonitor();

        var first = monitor.Poll(LogPath);
        File.AppendAllText(LogPath, Line("token_usage_record", UsagePayload("turn-2", 200, 20)) + "\n");
        var second = monitor.Poll(LogPath);

        Assert.NotSame(first, second);
        Assert.Single(first.UsageRecords);
        Assert.Equal(2, second.UsageRecords.Count);
    }

    [Fact]
    public void Poll_CachedSnapshotIsNotMutatedByLaterParsing()
    {
        // 缓存不得破坏快照的不变性：旧的快照在后续解析后内容必须完全不变。
        File.WriteAllText(LogPath, Line("token_usage_record", UsagePayload("turn-1", 111, 11)) + "\n");
        var monitor = new SessionLogMonitor();

        var cached = monitor.Poll(LogPath);
        File.AppendAllText(LogPath, Line("token_usage_record", UsagePayload("turn-2", 222, 22)) + "\n");
        monitor.Poll(LogPath);

        Assert.Single(cached.UsageRecords);
        Assert.Equal(111, cached.UsageRecords[0].Usage.InputTokens);
    }

    [Fact]
    public void Poll_SkipsUtf8BomLikeFullReadDoes()
    {
        // 全量路径用 StreamReader，会自动跳过 BOM；
        // 增量路径按字节读取，不处理就会把 BOM 当内容，
        // 导致首行 JSON 解析失败而丢失第一条记录。
        // 两条路径对同一文件必须得出相同结果。
        var content = Line("token_usage_record", UsagePayload("turn-1", 111, 11)) + "\n"
            + Line("token_usage_record", UsagePayload("turn-2", 222, 22)) + "\n";

        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(LogPath, [.. bom, .. Encoding.UTF8.GetBytes(content)]);

        var expected = SessionLogReader.Read(LogPath);
        Assert.Equal(2, expected.UsageRecords.Count);
        Assert.Equal(0, expected.UnreadableLineCount);

        foreach (var chunkBytes in new[] { 1, 2, 3, 4, 64 })
        {
            var monitor = new SessionLogMonitor(chunkBytes: chunkBytes);
            var actual = monitor.Poll(LogPath);

            Assert.Equal(2, actual.UsageRecords.Count);
            Assert.Equal(0, actual.UnreadableLineCount);
            Assert.True(actual.IsCaughtUp);
        }
    }

    [Fact]
    public void Poll_ReadsRecordsBeyondFourMegabytes()
    {
        // 这是修复前的真实缺陷：尾部 4 MB 窗口会漏掉早期记录。
        // 构造一个超过 4 MB 的日志，验证累计值仍覆盖全部记录。
        var builder = new StringBuilder();
        const int recordCount = 60_000;
        for (var i = 0; i < recordCount; i++)
        {
            builder.AppendLine(Line("token_usage_record", UsagePayload($"turn-{i}", 100, 10)));
        }

        var content = builder.ToString();
        Assert.True(
            Encoding.UTF8.GetByteCount(content) > 4 * 1024 * 1024,
            "测试数据必须超过 4 MB 才能覆盖该缺陷。");

        File.WriteAllText(LogPath, content);

        var monitor = new SessionLogMonitor();
        var snapshot = monitor.Poll(LogPath);

        Assert.Equal(recordCount, snapshot.UsageRecords.Count);
        Assert.Equal((long)recordCount * 100, snapshot.UsageRecords.Sum(r => r.Usage.InputTokens));
        Assert.True(snapshot.IsCaughtUp);
    }

    [Fact]
    public void Poll_ReadsRecordsFarLargerThanSingleChunkInOneCall()
    {
        // 单次 Poll 必须自己循环至追平，而不是靠调用方反复调用。
        // 否则首次轮询只能看到文件的最后一部分，累计值偏低。
        var builder = new StringBuilder();
        const int recordCount = 200_000;
        for (var i = 0; i < recordCount; i++)
        {
            builder.AppendLine(Line("token_usage_record", UsagePayload($"turn-{i}", 100, 10)));
        }

        File.WriteAllText(LogPath, builder.ToString());
        var size = new FileInfo(LogPath).Length;

        var monitor = new SessionLogMonitor();
        var snapshot = monitor.Poll(LogPath);

        Assert.True(size > 16 * 1024 * 1024, $"测试数据应远超单块上限，实际 {size} 字节。");
        Assert.Equal(recordCount, snapshot.UsageRecords.Count);
        Assert.True(snapshot.IsCaughtUp);
        Assert.Equal(size, monitor.Offset);
    }

    [Fact]
    public void Poll_ReportsNotCaughtUpWhenTrailingLineIsIncomplete()
    {
        // 末尾是写了一半的行时，尚未追平，调用方应知道数据可能不完整。
        var complete = Line("token_usage_record", UsagePayload("turn-1", 100, 10));
        File.WriteAllText(LogPath, complete + "\n" + complete[..(complete.Length / 2)]);

        var monitor = new SessionLogMonitor();
        var snapshot = monitor.Poll(LogPath);

        Assert.False(snapshot.IsCaughtUp);
        Assert.Single(snapshot.UsageRecords);
    }

    [Fact]
    public void Poll_DoesNotLoseRecordsWhenLogGrowsRepeatedlyInSmallSteps()
    {
        // 模拟 Codex 边写边刷的真实节奏，验证多次小步增长不会丢记录。
        File.WriteAllText(LogPath, string.Empty);
        var monitor = new SessionLogMonitor();

        for (var i = 0; i < 50; i++)
        {
            File.AppendAllText(LogPath, Line("token_usage_record", UsagePayload($"turn-{i}", 10, 1)) + "\n");
            var snapshot = monitor.Poll(LogPath);
            Assert.Equal(i + 1, snapshot.UsageRecords.Count);
        }
    }

    [Fact]
    public void Poll_HandlesMultiByteCharactersAtConsumptionBoundary()
    {
        // 偏移量按字节推进，但换行扫描按字符进行；中文内容必须正确对账，
        // 否则偏移会错位，导致重复解析或漏读。
        var payload = new
        {
            type = "turn_context",
            turn_id = "turn-1",
            model = "gpt-6-sol",
            cwd = @"E:\代码\三维重构\项目",
        };

        File.WriteAllText(LogPath,
            JsonSerializer.Serialize(new { timestamp = "2026-09-25T02:00:00.000Z", type = "turn_context", payload }) + "\n");

        var monitor = new SessionLogMonitor();
        monitor.Poll(LogPath);

        // 再追加一条记录，验证偏移未错位。
        File.AppendAllText(LogPath, Line("token_usage_record", UsagePayload("turn-2", 42, 4)) + "\n");

        var snapshot = monitor.Poll(LogPath);

        var record = Assert.Single(snapshot.UsageRecords);
        Assert.Equal("turn-2", record.TurnId);
    }

    private static string Line(string type, object payload) =>
        JsonSerializer.Serialize(new
        {
            timestamp = "2026-09-25T02:00:00.000Z",
            type,
            payload,
        });

    private static object UsagePayload(string turnId, long input, long output) => new
    {
        turn_id = turnId,
        usage = new
        {
            input_tokens = input,
            cached_input_tokens = 0,
            cache_write_input_tokens = 0,
            output_tokens = output,
            reasoning_output_tokens = 0,
            total_tokens = input + output,
        },
    };
}
