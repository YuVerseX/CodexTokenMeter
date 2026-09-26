using System.Globalization;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 按日期定位会话日志。
/// </summary>
/// <remarks>
/// <para>
/// Codex 把会话日志按 <c>sessions/YYYY/MM/DD/</c> 分层存放。
/// 直接递归遍历全部目录去查找一个会话，代价随使用时长线性增长——
/// 实测 3650 个文件时单次扫描 109 ms，而它运行在 UI 线程上会造成可见卡顿；
/// 按日期定位只要 0.125 ms。
/// </para>
/// <para>
/// 策略：从今天起按日期倒序逐个检查目录，**找到即停**。
/// 正确性依据：同一 thread id 的文件若分布在多个日期目录，
/// 说明它跨天延续（或产生了分叉），而更新的那份必然在更新的目录里。
/// </para>
/// <para>
/// 本类持有定位缓存，因此**应当长期复用同一个实例**（而不是每次调用新建）。
/// 缓存刻意做成实例状态而非静态：静态可变状态会让多个使用方
/// （包括单元测试）相互干扰，且无法在多会话场景下隔离。
/// </para>
/// </remarks>
public sealed class SessionLogLocator
{
    /// <summary>
    /// 倒序检索的最大天数。
    /// </summary>
    /// <remarks>
    /// 正常的活动会话不会超过这个跨度：Codex 会按天归档，
    /// 而浮层只关心「当前正在用的」会话。
    /// 超出时退回全量扫描以保证正确性，而不是返回「未找到」。
    /// </remarks>
    public const int MaxLookbackDays = 60;

    /// <summary>
    /// 普通命中结果的缓存时长（毫秒）。
    /// </summary>
    /// <remarks>
    /// 取较小值：分叉会话可能在运行期间产生，缓存太久会延迟发现它。
    /// 定位本身只要 0.125 ms，因此频繁复查不构成负担。
    /// </remarks>
    private const long HitCacheMilliseconds = 1000;

    /// <summary>
    /// 全量回退结果的缓存时长（毫秒）。
    /// </summary>
    /// <remarks>
    /// 全量扫描实测 109 ms / 3650 个文件，而它运行在 UI 线程上。
    /// 若老会话被长期使用，每次复查都走全量回退，
    /// 就会退化成「每 2 秒卡顿 109 ms」——优化等于白做。
    /// 因此全量回退的结果缓存久一些。
    /// </remarks>
    private const long FullScanCacheMilliseconds = 30_000;

    /// <summary>
    /// 未命中结果的缓存时长（毫秒）。
    /// </summary>
    /// <remarks>
    /// 不缓存负结果会让「日志尚未落盘」的窗口期里
    /// 每轮轮询都触发一次检索。
    /// </remarks>
    private const long MissCacheMilliseconds = 3000;

    /// <summary>缓存的最大条目数。</summary>
    private const int MaxCacheEntries = 64;

    private readonly Dictionary<(string Root, string ThreadId), CacheEntry> _cache =
        new(new KeyComparer());

    private readonly Lock _gate = new();

    /// <summary>
    /// 定位会话日志，返回最近写入的一份。
    /// </summary>
    /// <param name="sessionsRoot">会话根目录。</param>
    /// <param name="threadId">会话标识。</param>
    /// <param name="now">当前时间，便于测试注入。</param>
    /// <returns>日志路径；未找到时为 null。</returns>
    public string? Locate(string sessionsRoot, string threadId, DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsRoot);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        if (!Directory.Exists(sessionsRoot))
        {
            return null;
        }

        var key = (sessionsRoot, threadId);
        var ticks = Environment.TickCount64;

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && ticks < cached.ExpiresAt)
            {
                // 缓存的路径可能已被归档或删除，此时作废并重新定位。
                if (cached.Path is null || File.Exists(cached.Path))
                {
                    return cached.Path;
                }

                _cache.Remove(key);
            }
        }

        var (found, usedFullScan) = LocateCore(sessionsRoot, threadId, now ?? DateTime.Now);

        var ttl = found is null
            ? MissCacheMilliseconds
            : usedFullScan
                ? FullScanCacheMilliseconds
                : HitCacheMilliseconds;

        lock (_gate)
        {
            TrimIfNeeded(ticks);
            _cache[key] = new CacheEntry(found, ticks + ttl);
        }

        return found;
    }

    /// <summary>
    /// 丢弃全部缓存。
    /// </summary>
    /// <remarks>
    /// 会话被外部删除或迁移后可调用，强制下次重新检索。
    /// </remarks>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    /// <summary>清理过期条目；仍然过多时整体清空。</summary>
    private void TrimIfNeeded(long ticks)
    {
        if (_cache.Count < MaxCacheEntries)
        {
            return;
        }

        foreach (var stale in _cache
            .Where(pair => ticks >= pair.Value.ExpiresAt)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _cache.Remove(stale);
        }

        // 全部条目都未过期（极不寻常）：整体清空而不是放任增长。
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// 实际执行定位。
    /// </summary>
    /// <returns>日志路径，以及是否使用了全量回退。</returns>
    private static (string? Path, bool UsedFullScan) LocateCore(
        string sessionsRoot,
        string threadId,
        DateTime now)
    {
        var today = now.Date;

        for (var offset = 0; offset < MaxLookbackDays; offset++)
        {
            var day = today.AddDays(-offset);
            var directory = Path.Combine(
                sessionsRoot,
                day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture),
                day.ToString("dd", CultureInfo.InvariantCulture));

            if (!Directory.Exists(directory))
            {
                continue;
            }

            var found = FindNewestIn(directory, threadId);
            if (found is not null)
            {
                // 更新的目录里已经找到：更早的目录不可能有更新的一份。
                return (found, false);
            }
        }

        // 倒序范围外仍有可能是老会话被重新打开（例如隔月继续）。
        // 此时退回全量扫描，用一次代价换正确性。
        return (LocateByFullScan(sessionsRoot, threadId), true);
    }

    /// <summary>
    /// 在单个目录里查找给定会话的最新文件。
    /// </summary>
    /// <remarks>
    /// 不递归：会话日志直接放在日期目录下，不存在更深的层级。
    /// </remarks>
    private static string? FindNewestIn(string directory, string threadId)
    {
        string? best = null;
        var bestTime = DateTime.MinValue;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                if (!SessionPathResolver.MatchesThreadId(path, threadId))
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
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or DirectoryNotFoundException)
        {
            // 目录在枚举过程中被删除、或权限不足：当作该日无匹配。
            return null;
        }

        return best;
    }

    /// <summary>
    /// 全量扫描作为兜底。
    /// </summary>
    /// <remarks>
    /// 只在倒序检索失败后调用，属于罕见路径。
    /// 不递归会把分布在未知层级的文件漏掉，因此这里必须递归。
    /// </remarks>
    private static string? LocateByFullScan(string sessionsRoot, string threadId)
    {
        string? best = null;
        var bestTime = DateTime.MinValue;

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                if (!SessionPathResolver.MatchesThreadId(path, threadId))
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
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or DirectoryNotFoundException)
        {
            // 遍历中断：返回已找到的最优结果（可能为 null）。
            // 已找到的结果仍然有效，丢掉反而会让浮层无数据可显。
            return best;
        }

        return best;
    }

    private sealed record CacheEntry(string? Path, long ExpiresAt);

    /// <summary>路径与标识的比较不区分大小写（Windows 语义）。</summary>
    private sealed class KeyComparer : IEqualityComparer<(string Root, string ThreadId)>
    {
        public bool Equals((string Root, string ThreadId) x, (string Root, string ThreadId) y) =>
            string.Equals(x.Root, y.Root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ThreadId, y.ThreadId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Root, string ThreadId) obj) =>
            HashCode.Combine(
                obj.Root.ToUpperInvariant(),
                obj.ThreadId.ToUpperInvariant());
    }
}
