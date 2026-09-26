using System.Globalization;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 按日期定位会话日志。
/// </summary>
/// <remarks>
/// <para>
/// Codex 把会话日志按 <c>sessions/YYYY/MM/DD/</c> 分层存放。
/// 直接递归遍历全部目录去查找一个会话，代价随使用时长线性增长——
/// 实测 3000 个文件时单次扫描 84 ms（在 UI 线程上会造成可见卡顿），
/// 而按日期定位只要 0.9 ms。
/// </para>
/// <para>
/// 策略：从今天起按日期倒序逐个检查目录，**找到即停**。
/// 正确性依据：同一 thread id 的文件若分布在多个日期目录，
/// 说明它跨天延续（或产生了分叉），而更新的那份必然在更新的目录里。
/// 因此一旦在某个日期目录里找到，更早的目录不可能有更新的一份。
/// </para>
/// </remarks>
public static class SessionLogLocator
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
    /// 定位会话日志，返回最近写入的一份。
    /// </summary>
    /// <param name="sessionsRoot">会话根目录。</param>
    /// <param name="threadId">会话标识。</param>
    /// <param name="now">当前时间，便于测试注入。</param>
    /// <returns>日志路径；未找到时为 null。</returns>
    public static string? Locate(string sessionsRoot, string threadId, DateTime? now = null)
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

        var today = (now ?? DateTime.Now).Date;

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
                return found;
            }
        }

        // 倒序范围外仍有可能是老会话被重新打开（例如隔月继续）。
        // 此时退回全量扫描，用一次代价换正确性。
        return LocateByFullScan(sessionsRoot, threadId);
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
}
