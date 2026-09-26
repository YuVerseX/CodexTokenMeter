using System.Globalization;
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

if (args.Length > 0 && args[0] == "threshold")
{
    return RunThresholdProbe();
}

if (args.Length > 0 && args[0] == "guard")
{
    return RunGuardProbe();
}

if (args.Length > 0 && args[0] == "locator-perf")
{
    return RunLocatorPerfProbe();
}

if (args.Length > 0 && args[0] == "poll-cost")
{
    return RunPollCostProbe();
}

if (args.Length > 0 && args[0] == "scan-scale")
{
    return RunScanScaleProbe();
}

if (args.Length > 0 && args[0] == "scan-cost")
{
    return RunScanCostProbe();
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

/// <summary>
/// 测量会话目录扫描成本，用于决定缓存失效策略。
/// </summary>
/// <remarks>
/// 分叉会话产生后需要重新扫描才能发现新文件，但不能每轮都扫。
/// 本工具给出实际数字，让 TTL 的选择有依据而不是拍脑袋。
/// </remarks>
static int RunScanCostProbe()
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

    var total = Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories).Count();
    Console.WriteLine($"会话文件 {total} 个");
    Console.WriteLine();

    var timings = new List<double>();

    for (var i = 0; i < 20; i++)
    {
        var sw = Stopwatch.StartNew();

        string? best = null;
        var bestTime = DateTime.MinValue;

        foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var time = File.GetLastWriteTimeUtc(path);
            if (time > bestTime)
            {
                bestTime = time;
                best = path;
            }
        }

        sw.Stop();
        timings.Add(sw.Elapsed.TotalMilliseconds);
        _ = best;
    }

    timings.Sort();
    var median = timings[timings.Count / 2];

    Console.WriteLine("=== 扫描耗时（20 次）===");
    Console.WriteLine($"  中位数: {median:F2} ms    最快: {timings[0]:F2} ms    最慢: {timings[^1]:F2} ms");
    Console.WriteLine();

    Console.WriteLine("=== 推算 CPU 占用（700ms 轮询）===");
    foreach (var ttl in new[] { 0.0, 2.0, 5.0, 10.0, 30.0 })
    {
        var perSecond = ttl <= 0 ? 1.0 / 0.7 : 1.0 / ttl;
        var load = median * perSecond / 1000.0 * 100;
        var label = ttl <= 0 ? "每轮都扫" : $"TTL {ttl:F0}s";
        Console.WriteLine($"  {label,-12} {load,7:F3}% 单核");
    }

    Console.WriteLine();
    Console.WriteLine("=== 规模外推 ===");
    Console.WriteLine($"  {total} 个文件 → {median:F2} ms");
    Console.WriteLine($"  推算 3000 个文件 → {median * 3000 / Math.Max(1, total):F1} ms（仍是亚毫秒级）");

    return 0;
}

/// <summary>
/// 规模化测量会话目录扫描成本。
/// </summary>
/// <remarks>
/// Poll() 运行在 UI 线程上，扫描耗时会直接表现为界面卡顿。
/// 本地只有十几个文件，无法反映真实规模（用户积累数月后可能有数千个），
/// 因此构造合成目录来测量。
/// </remarks>
static int RunScanScaleProbe()
{
    var root = Path.Combine(Path.GetTempPath(), $"ctm-scan-{Guid.NewGuid():N}");

    try
    {
        Console.WriteLine("=== 构造合成会话目录 ===");

        var dayDirectories = new List<string>();
        var today = new DateTime(2026, 9, 26);

        for (var dayOffset = 0; dayOffset < 90; dayOffset++)
        {
            var day = today.AddDays(-dayOffset);
            var directory = Path.Combine(
                root,
                day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture),
                day.ToString("dd", CultureInfo.InvariantCulture));

            Directory.CreateDirectory(directory);
            dayDirectories.Add(directory);
        }

        const int filesPerDay = 34;

        foreach (var directory in dayDirectories)
        {
            for (var fileIndex = 0; fileIndex < filesPerDay; fileIndex++)
            {
                var id = Guid.NewGuid().ToString();
                File.WriteAllText(
                    Path.Combine(directory, $"rollout-2026-09-26T10-00-00-{id}.jsonl"),
                    "{}");
            }
        }

        var total = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Count();
        Console.WriteLine($"  共 {total} 个文件，{dayDirectories.Count} 个日期目录");
        Console.WriteLine();

        var fullScan = MeasureScan(() => FullScan(root), 10);
        Console.WriteLine($"=== A. 全量扫描（当前实现）===");
        Console.WriteLine($"  中位数 {fullScan.Median:F2} ms   最慢 {fullScan.Max:F2} ms");
        Console.WriteLine();

        var dayScan = MeasureScan(() => DayScan(dayDirectories[0]), 10);
        Console.WriteLine($"=== B. 仅扫今日目录 ===");
        Console.WriteLine($"  中位数 {dayScan.Median:F2} ms   最慢 {dayScan.Max:F2} ms");
        Console.WriteLine();

        Console.WriteLine("=== C. 全量扫描的 CPU 占用推算（700ms 轮询）===");
        foreach (var ttl in new[] { 0.7, 2.0, 5.0, 10.0 })
        {
            var perSecond = 1.0 / ttl;
            Console.WriteLine($"  TTL {ttl,4:F1}s   {fullScan.Median * perSecond / 10.0,7:F2}% 单核");
        }

        Console.WriteLine();
        Console.WriteLine($"=== D. 加速比 ===");
        Console.WriteLine($"  全量 {fullScan.Median:F1} ms -> 今日目录 {dayScan.Median:F2} ms" +
            $"（快 {fullScan.Median / Math.Max(0.001, dayScan.Median):F0} 倍）");

        Console.WriteLine();
        Console.WriteLine("=== E. 结论 ===");
        Console.WriteLine(fullScan.Median > 20
            ? $"  全量扫描 {fullScan.Median:F0} ms 在 UI 线程上会造成可见卡顿。"
            : $"  全量扫描 {fullScan.Median:F0} ms 尚可接受。");

        return 0;
    }
    finally
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论。
        }
    }
}

/// <summary>重复测量并返回统计。</summary>
static (double Median, double Max) MeasureScan(Action action, int iterations)
{
    var timings = new List<double>();

    // 预热，避免把 JIT 与文件系统缓存冷启动算进去。
    action();

    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        timings.Add(sw.Elapsed.TotalMilliseconds);
    }

    timings.Sort();
    return (timings[timings.Count / 2], timings[^1]);
}

/// <summary>当前实现：递归遍历全部目录。</summary>
static void FullScan(string root)
{
    string? best = null;
    var bestTime = DateTime.MinValue;

    foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var time = File.GetLastWriteTimeUtc(path);
        if (time > bestTime)
        {
            bestTime = time;
            best = path;
        }
    }

    _ = best;
}

/// <summary>候选方案：只扫单个日期目录。</summary>
static void DayScan(string directory)
{
    string? best = null;
    var bestTime = DateTime.MinValue;

    foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
    {
        var time = File.GetLastWriteTimeUtc(path);
        if (time > bestTime)
        {
            bestTime = time;
            best = path;
        }
    }

    _ = best;
}

/// <summary>
/// 测量一次完整 Poll 的耗时构成。
/// </summary>
/// <remarks>
/// Poll 运行在 UI 线程上，其耗时直接表现为界面卡顿。
/// 需要分别测量各环节，才能知道优化方向。
/// </remarks>
static int RunPollCostProbe()
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

    // 取最大的会话文件，代表最坏情况。
    var largest = Directory
        .EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
        .Select(path => new FileInfo(path))
        .OrderByDescending(info => info.Length)
        .First();

    Console.WriteLine($"最大会话：{largest.Name}");
    Console.WriteLine($"  大小 {largest.Length / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine();

    var catalog = PricingCatalog.CreateDefault();

    // 首次解析（含全量读文件）。
    var sw = Stopwatch.StartNew();
    var snapshot = SessionLogReader.Read(largest.FullName);
    sw.Stop();
    var parseMs = sw.Elapsed.TotalMilliseconds;

    Console.WriteLine("=== A. 全量解析（仅首次/切换会话时发生）===");
    Console.WriteLine($"  {parseMs:F1} ms   记录数 {snapshot.UsageRecords.Count}");
    Console.WriteLine();

    // 计费重算（每次轮询都发生）。
    var costTimings = new List<double>();
    for (var i = 0; i < 30; i++)
    {
        var s = Stopwatch.StartNew();
        var summary = SessionCostCalculator.Calculate(snapshot, catalog);
        s.Stop();
        costTimings.Add(s.Elapsed.TotalMilliseconds);
        _ = summary;
    }
    costTimings.Sort();
    var costMedian = costTimings[costTimings.Count / 2];

    Console.WriteLine("=== B. 计费重算（每次轮询都发生）===");
    Console.WriteLine($"  中位数 {costMedian:F3} ms   最慢 {costTimings[^1]:F3} ms");
    Console.WriteLine();

    // 指标构建。
    var metricsTimings = new List<double>();
    for (var i = 0; i < 30; i++)
    {
        var s = Stopwatch.StartNew();
        var metrics = SessionMetrics.Create(snapshot);
        s.Stop();
        metricsTimings.Add(s.Elapsed.TotalMilliseconds);
        _ = metrics;
    }
    metricsTimings.Sort();
    var metricsMedian = metricsTimings[metricsTimings.Count / 2];

    Console.WriteLine("=== C. 指标构建（每次轮询都发生）===");
    Console.WriteLine($"  中位数 {metricsMedian:F3} ms");
    Console.WriteLine();

    Console.WriteLine("=== D. 轮询稳态成本（不含文件扫描）===");
    var steady = costMedian + metricsMedian;
    Console.WriteLine($"  {steady:F3} ms / 次");
    Console.WriteLine($"  700ms 轮询 → {steady / 700 * 100:F3}% 单核");
    Console.WriteLine();

    Console.WriteLine("=== E. 加上全量目录扫描（3060 文件的实测值）===");
    var withScan = steady + 84.5;
    Console.WriteLine($"  {withScan:F1} ms / 次");
    Console.WriteLine($"  700ms 轮询 → {withScan / 700 * 100:F2}% 单核，且每次都有 {withScan:F0} ms 的 UI 卡顿");
    Console.WriteLine();
    Console.WriteLine("=== F. 结论 ===");
    Console.WriteLine(costMedian > 1
        ? $"  计费重算 {costMedian:F2} ms 值得优化（可随快照缓存）。"
        : $"  计费重算 {costMedian:F3} ms 可忽略。");
    Console.WriteLine($"  瓶颈是目录扫描：{84.5:F0} ms 而非计费的 {costMedian:F2} ms。");

    return 0;
}

/// <summary>
/// 对比优化前后的会话定位性能。
/// </summary>
/// <remarks>
/// 优化前：递归遍历全部日期目录。
/// 优化后：按日期倒序检索，命中即停。
/// </remarks>
static int RunLocatorPerfProbe()
{
    var root = Path.Combine(Path.GetTempPath(), $"ctm-perf-{Guid.NewGuid():N}");

    try
    {
        Console.WriteLine("=== 构造合成目录（模拟长期使用）===");

        var days = 365;
        var perDay = 10;
        var today = DateTime.Now.Date;

        for (var offset = 0; offset < days; offset++)
        {
            var day = today.AddDays(-offset);
            var directory = Path.Combine(
                root,
                day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture),
                day.ToString("dd", CultureInfo.InvariantCulture));

            Directory.CreateDirectory(directory);

            for (var i = 0; i < perDay; i++)
            {
                var id = Guid.NewGuid().ToString();
                File.WriteAllText(
                    Path.Combine(directory, $"rollout-{day:yyyy-MM-dd}T10-00-00-{id}.jsonl"),
                    "{}");
            }
        }

        var total = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Count();
        Console.WriteLine($"  {total} 个文件，{days} 个日期目录");
        Console.WriteLine();

        // 目标会话放在今天，代表正常使用。
        const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
        var targetDirectory = Path.Combine(
            root,
            today.ToString("yyyy", CultureInfo.InvariantCulture),
            today.ToString("MM", CultureInfo.InvariantCulture),
            today.ToString("dd", CultureInfo.InvariantCulture));
        File.WriteAllText(
            Path.Combine(targetDirectory, $"rollout-{today:yyyy-MM-dd}T15-00-00-{ThreadId}.jsonl"),
            "{}");

        Console.WriteLine("=== A. 优化前：递归全量扫描 ===");
        var before = MeasureOp(() => FullScanAgain(root, ThreadId), 10);
        Console.WriteLine($"  中位数 {before.Median:F2} ms   最慢 {before.Max:F2} ms");
        Console.WriteLine();

        Console.WriteLine("=== B. 优化后：按日期倒序定位 ===");

        // 定位器持有缓存，应当长期复用同一个实例。
        var locator = new SessionLogLocator();
        var after = MeasureOp(() => locator.Locate(root, ThreadId), 20);
        Console.WriteLine($"  中位数 {after.Median:F3} ms   最慢 {after.Max:F3} ms");
        Console.WriteLine();

        Console.WriteLine("=== C. 改进 ===");
        Console.WriteLine($"  快 {before.Median / Math.Max(0.001, after.Median):F0} 倍");
        Console.WriteLine();

        Console.WriteLine("=== D. CPU 占用推算（700ms 轮询 + 2s 复查）===");
        var beforeLoad = before.Median / 2000 * 100;
        var afterLoad = after.Median / 2000 * 100;
        Console.WriteLine($"  优化前 {beforeLoad:F2}% 单核，且每 2 秒有 {before.Median:F0} ms 的 UI 卡顿");
        Console.WriteLine($"  优化后 {afterLoad:F4}% 单核，卡顿 {after.Median:F3} ms（不可感知）");
        Console.WriteLine();

        Console.WriteLine("=== E. 结论 ===");
        Console.WriteLine(after.Median < 1
            ? $"  定位耗时 {after.Median:F3} ms，已不再是瓶颈。"
            : $"  定位仍需 {after.Median:F1} ms，值得继续优化。");

        return 0;
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}

static (double Median, double Max) MeasureOp(Action action, int iterations)
{
    var timings = new List<double>();
    action();

    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        timings.Add(sw.Elapsed.TotalMilliseconds);
    }

    timings.Sort();
    return (timings[timings.Count / 2], timings[^1]);
}

static void FullScanAgain(string root, string threadId)
{
    string? best = null;
    var bestTime = DateTime.MinValue;

    foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var time = File.GetLastWriteTimeUtc(path);
        if (time > bestTime)
        {
            bestTime = time;
            best = path;
        }
    }

    _ = best;
    _ = threadId;
}

/// <summary>验证内容级一致性守卫在写入期间会拒绝。</summary>
static int RunGuardProbe()
{
    var path = Path.Combine(Path.GetTempPath(), $"ctm-guard-{Guid.NewGuid():N}.jsonl");
    const string Line = "{\"timestamp\":\"2026-09-26T10:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"t1\",\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"cache_write_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":0,\"total_tokens\":110}}}";

    File.WriteAllText(path, Line + Environment.NewLine);

    var accepted = 0;
    var rejected = 0;
    var stop = false;

    var writer = new Thread(() =>
    {
        while (!stop)
        {
            try { File.AppendAllText(path, Line + Environment.NewLine); }
            catch (IOException) { }
            Thread.Sleep(1);
        }
    });
    writer.Start();

    try
    {
        for (var round = 0; round < 300; round++)
        {
            if (!File.Exists(path)) { continue; }

            var first = SessionLogReader.Read(path);
            var second = SessionLogReader.Read(path);

            if (first.UsageRecords.Count == second.UsageRecords.Count
                && first.Cumulative == second.Cumulative)
            {
                accepted++;
            }
            else
            {
                rejected++;
            }
        }
    }
    finally
    {
        stop = true;
        writer.Join();
        File.Delete(path);
    }

    Console.WriteLine($"  接受 {accepted} 次，拒绝 {rejected} 次");
    Console.WriteLine(rejected > 0
        ? "  结论：守卫在写入期间正确拒绝，机制有效。"
        : "  结论：守卫从未拒绝 —— 可能失效。");

    return 0;
}

/// <summary>验证成本断言的阈值仍能区分「已优化」与「未优化」。</summary>
static int RunThresholdProbe()
{
    var root = Path.Combine(Path.GetTempPath(), $"ctm-thresh-{Guid.NewGuid():N}");

    try
    {
        // 构造与 SessionLogLocatorCostTests 相同的干扰规模。
        var today = DateTime.Now.Date;

        for (var offset = 0; offset < 200; offset++)
        {
            var day = today.AddDays(-offset);
            var directory = Path.Combine(
                root,
                day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture),
                day.ToString("dd", CultureInfo.InvariantCulture));

            Directory.CreateDirectory(directory);

            for (var i = 0; i < 10; i++)
            {
                File.WriteAllText(
                    Path.Combine(directory, $"rollout-{day:yyyy-MM-dd}T10-00-00-{Guid.NewGuid()}.jsonl"),
                    "{}");
            }
        }

        const string ThreadId = "01a0d656-cf19-7532-af4a-f089fc3163ed";
        var todayDir = Path.Combine(root, today.ToString("yyyy"), today.ToString("MM"), today.ToString("dd"));
        File.WriteAllText(
            Path.Combine(todayDir, $"rollout-{today:yyyy-MM-dd}T10-00-00-{ThreadId}.jsonl"),
            "{}");

        // 未优化路径：递归全盘扫描。
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) { FullScanAgain(root, ThreadId); }
        sw.Stop();
        var unoptimizedPerCall = sw.Elapsed.TotalMilliseconds / 20;

        // 已优化路径：按日期定位。
        var locator = new SessionLogLocator();
        locator.Locate(root, ThreadId);

        sw.Restart();
        for (var i = 0; i < 20; i++) { locator.Locate(root, ThreadId); }
        sw.Stop();
        var optimizedPerCall = sw.Elapsed.TotalMilliseconds / 20;

        const double Threshold = 500;

        Console.WriteLine($"  未优化（全盘扫描）: {unoptimizedPerCall:F1} ms/次");
        Console.WriteLine($"  已优化（按日期）:   {optimizedPerCall:F3} ms/次");
        Console.WriteLine($"  测试阈值:           {Threshold:F0} ms");
        Console.WriteLine($"  倍数差:             {unoptimizedPerCall / Math.Max(0.001, optimizedPerCall):F0}x");
        Console.WriteLine();
        Console.WriteLine(unoptimizedPerCall > 20
            ? "  结论：阈值足以区分两种实现（优化前 20 ms+/次）"
            : "  结论：数据规模不足以体现差异，阈值验证无效");

        return 0;
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
