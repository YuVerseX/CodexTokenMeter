using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Metrics;
using CodexTokenMeter.Core.Pricing;

// 开发期验证工具：核对增量解析与全量解析的一致性，
// 并测量大文件下的内存与耗时。不参与发布产物。
//
// 用法：
//   dotnet run --project tools/CodexTokenMeter.Probe                  # 大文件一致性 + 性能
//   dotnet run --project tools/CodexTokenMeter.Probe <sessions 路径>  # 真实会话数据

if (args.Length > 0 && args[0] == "--ipc")
{
    return await VerifyIpcAsync(args.Length > 1 ? args[1] : null);
}

if (args.Length > 0 && args[0] == "fork")
{
    return RunForkProbe();
}

if (args.Length > 0 && args[0] == "stability")
{
    return RunStabilityProbe(args.Length > 1 && int.TryParse(args[1], out var rounds) ? rounds : 6);
}

if (args.Length > 0)
{
    return VerifyRealSessions(args[0], args.Length > 1 ? double.Parse(args[1]) : 1.0);
}

return await VerifySyntheticLargeFile();

/// <summary>端到端验证：IPC 跟随当前会话，并把它解析为完整指标。</summary>
static async Task<int> VerifyIpcAsync(string? sessionsRoot)
{
    sessionsRoot ??= Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "sessions");

    using var monitor = new CodexIpcMonitor();
    monitor.StatusChanged += (_, _) =>
    {
        var status = monitor.GetStatus();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 状态变化  " +
                          $"连接={status.IsConnected}  会话={status.ActiveConversationId ?? "(无)"}  " +
                          $"窗口={status.ActiveWindowCount}  帧={status.FramesReceived}");
    };

    Console.WriteLine("监听 Codex IPC，最长 25 秒...");
    Console.WriteLine();

    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
    string? resolvedThread = null;

    while (DateTime.UtcNow < deadline)
    {
        var status = monitor.GetStatus();
        if (status.ActiveConversationId is { } id)
        {
            resolvedThread = id;
            break;
        }

        await Task.Delay(200);
    }

    var final = monitor.GetStatus();
    Console.WriteLine();
    Console.WriteLine($"最终状态：连接={final.IsConnected}  会话={final.ActiveConversationId ?? "(无)"}  帧={final.FramesReceived}");

    if (final.LastError is { } error)
    {
        Console.WriteLine($"最后错误：{error}");
    }

    if (resolvedThread is null)
    {
        Console.WriteLine("未获取到活动会话，可能 Codex 未运行或未选中任何任务。");
        return 2;
    }

    // 把 IPC 跟随到的会话接到日志解析与计费，验证完整链路。
    var log = Directory.EnumerateFiles(sessionsRoot, $"*{resolvedThread}.jsonl", SearchOption.AllDirectories)
        .FirstOrDefault();

    if (log is null)
    {
        Console.WriteLine($"未找到该会话的日志文件：{resolvedThread}");
        return 3;
    }

    Console.WriteLine();
    Console.WriteLine("=== 完整链路：IPC → 日志 → 指标 → 费用 ===");

    var snapshot = SessionLogReader.Read(log);
    var metrics = SessionMetrics.Create(snapshot);
    var cost = SessionCostCalculator.Calculate(snapshot, PricingCatalog.CreateDefault());

    Console.WriteLine($"  日志      {Path.GetFileName(log)}");
    Console.WriteLine($"  模型      {snapshot.PrimaryModel ?? "(无)"}");
    Console.WriteLine($"  上下文    {metrics.ContextUsedTokens:N0} / {metrics.ContextWindowTokens:N0} " +
                      $"({metrics.ContextPercent:F1}%)");
    Console.WriteLine($"  本轮      {metrics.CurrentTurn.Total:N0} tokens   ${cost.CurrentTurnCost:F6}");
    Console.WriteLine($"  累计      {metrics.Cumulative.Total:N0} tokens   ${cost.TotalCost:F6}");
    Console.WriteLine($"  缓存命中率 {metrics.Cumulative.CacheHitRate:F1}%");
    Console.WriteLine($"  消息      用户 {snapshot.UserMessageCount}  助手 {snapshot.AssistantMessageCount}  " +
                      $"工具调用 {snapshot.ToolCallCount}  结果 {snapshot.ToolResultCount}");
    var duration = metrics.ActiveDuration;
    Console.WriteLine($"  活跃时长  {(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}   " +
                      $"压缩 {snapshot.CompactionCount} 次");

    return 0;
}

static int VerifyRealSessions(string root, double rateMultiplier)
{
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"目录不存在：{root}");
        return 1;
    }

    var logs = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
        .OrderBy(File.GetLastWriteTimeUtc)
        .ToArray();

    var catalog = PricingCatalog.CreateDefault();

    Console.WriteLine($"目录：{root}");
    Console.WriteLine($"日志：{logs.Length} 个    价格表：{catalog.Count} 个模型    分组倍率：{rateMultiplier}");
    Console.WriteLine();

    var allConsistent = true;

    foreach (var log in logs)
    {
        var size = new FileInfo(log).Length;
        var full = SessionLogReader.Read(log);
        var fullTotal = SessionMetrics.Create(full).Cumulative.Total;

        var monitor = new SessionLogMonitor();
        var incremental = monitor.Poll(log);
        var incrementalTotal = SessionMetrics.Create(incremental).Cumulative.Total;

        var consistent = full.UsageRecords.Count == incremental.UsageRecords.Count
            && fullTotal == incrementalTotal
            && incremental.UnreadableLineCount == 0
            && incremental.IsCaughtUp;

        allConsistent &= consistent;

        // service_tier 取自日志；未写明的会话按标准档处理。
        var serviceTier = ReadServiceTier(log);
        var cost = SessionCostCalculator.Calculate(full, catalog, serviceTier, rateMultiplier);

        Console.WriteLine(Path.GetFileName(log));
        Console.WriteLine($"  {size / 1024.0 / 1024.0:F2} MB   模型 {full.PrimaryModel ?? "(无)"}   " +
                          $"压缩 {full.CompactionCount}   档位 {serviceTier ?? "(未记录)"}   不可读 {full.UnreadableLineCount}");
        Console.WriteLine($"  记录 {full.UsageRecords.Count}   累计 {fullTotal:N0}   " +
                          $"增量为 {incrementalTotal:N0}   {(consistent ? "一致" : "不一致 <--")}");
        Console.WriteLine($"  费用 本轮 ${cost.CurrentTurnCost:F6}   累计 ${cost.TotalCost:F6}   " +
                          $"已计价 {cost.PricedCount}/{cost.TotalCount}" +
                          $"{(cost.LongContextSeen ? "   含长上下文" : string.Empty)}" +
                          $"{(cost.IsComplete ? string.Empty : "   <-- 不完整")}");

        if (cost.UnknownModels.Count > 0)
        {
            Console.WriteLine($"  未定价模型：{string.Join(", ", cost.UnknownModels)}");
        }

        Console.WriteLine();
    }

    Console.WriteLine(allConsistent ? "全部会话解析一致。" : "存在解析不一致。");
    return allConsistent ? 0 : 1;
}

static string? ReadServiceTier(string logPath)
{
    // service_tier 出现在 thread_settings_applied 事件里，取最后一次设置。
    string? tier = null;

    using var stream = new FileStream(
        logPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: true);

    while (reader.ReadLine() is { } line)
    {
        var index = line.IndexOf("\"service_tier\"", StringComparison.Ordinal);
        if (index < 0)
        {
            continue;
        }

        var start = line.IndexOf('"', index + 14);
        if (start < 0)
        {
            continue;
        }

        var end = line.IndexOf('"', start + 1);
        if (end > start)
        {
            tier = line[(start + 1)..end];
        }
    }

    return tier;
}

static async Task<int> VerifySyntheticLargeFile()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "CodexTokenMeterProbe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var logPath = Path.Combine(
            directory,
            "rollout-2026-09-25T10-00-00-01a0d656-cf19-7532-af4a-f089fc3163ed.jsonl");

        Console.WriteLine("生成测试日志...");
        const int recordCount = 400_000;
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        await using (var writer = new StreamWriter(logPath, append: false, utf8NoBom))
        {
            for (var i = 0; i < recordCount; i++)
            {
                await writer.WriteLineAsync(UsageLine($"turn-{i}", 100 + (i % 50)));
            }
        }

        var size = new FileInfo(logPath).Length;
        Console.WriteLine($"日志大小：{size / 1024.0 / 1024.0:F1} MB，记录数：{recordCount}");

        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        Console.WriteLine();
        Console.WriteLine("=== 全量解析 ===");
        var sw = Stopwatch.StartNew();
        var full = SessionLogReader.Read(logPath);
        sw.Stop();
        var fullTotal = full.UsageRecords.Sum(r => r.Usage.TotalTokens);
        Console.WriteLine($"耗时 {sw.ElapsedMilliseconds} ms   记录 {full.UsageRecords.Count}   " +
                          $"累计 {fullTotal:N0}   不可读 {full.UnreadableLineCount}");
        Console.WriteLine($"内存增量 {(GC.GetTotalMemory(false) - baseline) / 1024.0 / 1024.0:F1} MB");

        Console.WriteLine();
        Console.WriteLine("=== 增量解析（单次 Poll）===");
        var monitor = new SessionLogMonitor();
        sw.Restart();
        var incremental = monitor.Poll(logPath);
        sw.Stop();
        var incrementalTotal = incremental.UsageRecords.Sum(r => r.Usage.TotalTokens);
        Console.WriteLine($"耗时 {sw.ElapsedMilliseconds} ms   记录 {incremental.UsageRecords.Count}   " +
                          $"累计 {incrementalTotal:N0}   不可读 {incremental.UnreadableLineCount}");
        Console.WriteLine($"内存增量 {(GC.GetTotalMemory(false) - baseline) / 1024.0 / 1024.0:F1} MB   " +
                          $"追平 {incremental.IsCaughtUp}   偏移 {monitor.Offset} / {size}");

        var consistent = full.UsageRecords.Count == incremental.UsageRecords.Count
            && fullTotal == incrementalTotal;

        Console.WriteLine();
        Console.WriteLine($"两条路径一致：{consistent}");

        Console.WriteLine();
        Console.WriteLine("=== 极小步长的一致性（小样本）===");

        // 极小步长用于精确覆盖块边界，但在 96 MB 文件上迭代次数过多，
        // 因此单独用一个小文件验证。
        var smallPath = Path.Combine(
            directory,
            "rollout-2026-09-25T11-00-00-01a0d999-0000-0000-0000-000000000000.jsonl");
        const int smallCount = 2_000;
        await using (var smallWriter = new StreamWriter(smallPath, append: false, utf8NoBom))
        {
            for (var i = 0; i < smallCount; i++)
            {
                await smallWriter.WriteLineAsync(UsageLine($"small-{i}", 10 + i));
            }
        }

        var smallFull = SessionLogReader.Read(smallPath);
        var smallTotal = smallFull.UsageRecords.Sum(r => r.Usage.TotalTokens);

        var allChunksConsistent = true;
        foreach (var chunkBytes in new[] { 1, 2, 3, 7, 13, 64, 4096 })
        {
            var probe = new SessionLogMonitor(chunkBytes: chunkBytes);
            var result = probe.Poll(smallPath);
            var total = result.UsageRecords.Sum(r => r.Usage.TotalTokens);
            var ok = result.UsageRecords.Count == smallCount
                && total == smallTotal
                && result.UnreadableLineCount == 0
                && result.IsCaughtUp;
            allChunksConsistent &= ok;
            Console.WriteLine($"  块={chunkBytes,6}  记录 {result.UsageRecords.Count,5}  " +
                              $"不可读 {result.UnreadableLineCount}   {(ok ? "一致" : "不一致 <--")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 大文件在常规块大小下的一致性 ===");
        foreach (var chunkBytes in new[] { 65536, 1_048_576, 4_194_304 })
        {
            var probe = new SessionLogMonitor(chunkBytes: chunkBytes);
            sw.Restart();
            var result = probe.Poll(logPath);
            sw.Stop();
            var total = result.UsageRecords.Sum(r => r.Usage.TotalTokens);
            var ok = result.UsageRecords.Count == recordCount
                && total == fullTotal
                && result.UnreadableLineCount == 0;
            allChunksConsistent &= ok;
            Console.WriteLine($"  块={chunkBytes / 1024,6} KB  记录 {result.UsageRecords.Count,6}  " +
                              $"耗时 {sw.ElapsedMilliseconds,5} ms   {(ok ? "一致" : "不一致 <--")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 稳态轮询开销（无新数据）==="
        );

        // 浮层每秒轮询，绝大多数调用没有新内容。
        // 这里测量稳态下的分配量，验证缓存确实生效。
        const int idlePolls = 1_000;
        var beforeIdle = GC.GetTotalAllocatedBytes(precise: true);
        sw.Restart();
        for (var i = 0; i < idlePolls; i++)
        {
            monitor.Poll(logPath);
        }
        sw.Stop();
        var idleAllocated = GC.GetTotalAllocatedBytes(precise: true) - beforeIdle;

        Console.WriteLine($"{idlePolls} 次无变化轮询：");
        Console.WriteLine($"  总耗时 {sw.ElapsedMilliseconds} ms" +
                          $"（{sw.Elapsed.TotalMilliseconds / idlePolls:F3} ms/次）");
        Console.WriteLine($"  总分配 {idleAllocated / 1024.0:F1} KB" +
                          $"（{idleAllocated / (double)idlePolls:F0} 字节/次）");

        // 单次轮询分配量应远小于一次完整快照（约百 MB 量级），
        // 否则说明缓存未生效。
        var idleAllocationOk = idleAllocated / idlePolls < 10_000;

        Console.WriteLine();
        Console.WriteLine("=== 追加后增量跟进 ===");
        await File.AppendAllTextAsync(logPath, UsageLine("appended", 7) + "\n", utf8NoBom);
        sw.Restart();
        var afterAppend = monitor.Poll(logPath);
        sw.Stop();
        var appendOk = afterAppend.UsageRecords.Count == recordCount + 1;
        Console.WriteLine($"记录 {afterAppend.UsageRecords.Count}（应为 {recordCount + 1}）   " +
                          $"耗时 {sw.ElapsedMilliseconds} ms   {(appendOk ? "正确" : "错误 <--")}");

        var success = consistent && allChunksConsistent && appendOk && idleAllocationOk;
        Console.WriteLine();
        Console.WriteLine($"稳态轮询分配量可接受：{idleAllocationOk}");
        Console.WriteLine(success ? "全部检查通过。" : "存在问题。");
        return success ? 0 : 1;
    }
    finally
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略清理失败。
        }
    }
}

static string UsageLine(string turnId, long input) => JsonSerializer.Serialize(new
{
    timestamp = "2026-09-25T02:00:00.000Z",
    type = "token_usage_record",
    payload = new
    {
        turn_id = turnId,
        usage = new
        {
            input_tokens = input,
            cached_input_tokens = 0,
            cache_write_input_tokens = 0,
            output_tokens = 10,
            reasoning_output_tokens = 2,
            total_tokens = input + 10,
        },
    },
});

/// <summary>
/// 诊断探针：反复检查所有真实会话文件，找出「无压缩却出现 usage record 与
/// token_count 不一致」的文件，并观察该状态是否稳定。
/// </summary>
static int RunStabilityProbe(int rounds)
{
    var sessionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "sessions");

    if (!Directory.Exists(sessionsRoot))
    {
        Console.Error.WriteLine($"会话目录不存在：{sessionsRoot}");
        return 1;
    }

    var logs = Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories).ToArray();

    Console.WriteLine($"会话文件 {logs.Length} 个，检查 {rounds} 轮");
    Console.WriteLine();

    var suspicious = new Dictionary<string, string>();

    for (var round = 1; round <= rounds; round++)
    {
        var inconsistent = 0;

        foreach (var log in logs)
        {
            var name = Path.GetFileName(log);
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(log);

            SessionSnapshot snapshot;
            try
            {
                snapshot = SessionLogReader.Read(log);
            }
            catch (IOException)
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

            if (lastRecord == lastCount || snapshot.CompactionCount > 0)
            {
                continue;
            }

            inconsistent++;
            var detail = $"usage={lastRecord} tokenCount={lastCount} 压缩={snapshot.CompactionCount} "
                + $"写入于 {age.TotalSeconds:F1} 秒前";

            suspicious[name] = detail;
            Console.WriteLine($"  [{round}] {name}");
            Console.WriteLine($"        {detail}");
        }

        Console.WriteLine($"第 {round} 轮：{inconsistent} 个不一致");
    }

    Console.WriteLine();
    Console.WriteLine(suspicious.Count == 0
        ? "结论：未发现稳定不一致。失败是写入竞态。"
        : $"结论：{suspicious.Count} 个文件曾出现不一致，见上。");

    return 0;
}

/// <summary>
/// 诊断探针：调查 Codex 的「分叉会话」（fork）。
/// </summary>
/// <remarks>
/// 现象：会话目录里出现了文件名含下划线的文件：
///   rollout-&lt;时间&gt;-&lt;父会话id&gt;_&lt;子会话id&gt;.jsonl
/// 它的 session_meta.id 与文件名末段不一致，且 token_count 累计值
/// 远大于它自己的 token_usage_record 求和。
/// </remarks>
static int RunForkProbe()
{
    var sessionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "sessions");

    if (!Directory.Exists(sessionsRoot))
    {
        Console.Error.WriteLine($"会话目录不存在：{sessionsRoot}");
        return 1;
    }

    Console.WriteLine($"会话目录：{sessionsRoot}");
    Console.WriteLine();

    var rows = new List<(bool IsFork, string Name, long RecordSum, long CountCumulative, long LastUsage, string ThreadId, string Tail)>();

    foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
    {
        var leaf = Path.GetFileNameWithoutExtension(path);
        var isFork = leaf.Contains('_', StringComparison.Ordinal);
        var snapshot = SessionLogReader.Read(path);
        var tail = leaf.Length >= 36 ? leaf[^36..] : leaf;

        rows.Add((
            isFork,
            Path.GetFileName(path),
            snapshot.Cumulative.Total,
            snapshot.LatestTokenCount?.TotalUsage.TotalTokens ?? 0,
            snapshot.LatestTokenCount?.LastUsage.TotalTokens ?? 0,
            snapshot.ThreadId,
            tail));

        Console.WriteLine($"{(isFork ? "[分叉]" : "[普通]")} {Path.GetFileName(path)}");
        Console.WriteLine($"  记录数: usage={snapshot.UsageRecords.Count} tokenCount={snapshot.TokenCounts.Count} 压缩={snapshot.CompactionCount}");
        Console.WriteLine($"  usage_record 求和:   {snapshot.Cumulative.Total,14:N0}");
        Console.WriteLine($"  token_count 累计:    {snapshot.LatestTokenCount?.TotalUsage.TotalTokens ?? 0,14:N0}");
        Console.WriteLine($"  token_count 的 last: {snapshot.LatestTokenCount?.LastUsage.TotalTokens ?? 0,14:N0}   <- 当前上下文占用");
        Console.WriteLine($"  上下文窗口:          {snapshot.LatestTokenCount?.ModelContextWindow ?? 0,14:N0}");
        Console.WriteLine($"  快照 ThreadId:       {snapshot.ThreadId}");
        Console.WriteLine($"  文件名末段:          {tail}");
        Console.WriteLine($"  文件名末段==ThreadId: {tail == snapshot.ThreadId}");
        Console.WriteLine();

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        if (reader.ReadLine() is { } first)
        {
            using var document = JsonDocument.Parse(first);
            if (document.RootElement.TryGetProperty("payload", out var payload))
            {
                if (payload.TryGetProperty("history_base", out var historyBase))
                {
                    Console.WriteLine($"  history_base: {historyBase.GetRawText()}");
                }

                if (payload.TryGetProperty("thread_source", out var source))
                {
                    Console.WriteLine($"  thread_source: {source.GetString()}");
                }

                Console.WriteLine();
            }
        }
    }

    var forks = rows.Where(r => r.IsFork).ToList();
    Console.WriteLine($"总计 {rows.Count} 个会话，其中分叉 {forks.Count} 个");

    foreach (var fork in forks)
    {
        Console.WriteLine($"  分叉 {fork.Name}");
        Console.WriteLine($"    文件名末段 != ThreadId: {fork.Tail != fork.ThreadId}");
        Console.WriteLine($"    usage 求和 < token_count 累计: {fork.RecordSum < fork.CountCumulative}");
    }

    return 0;
}
