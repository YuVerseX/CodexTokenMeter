using System.Text;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 一次性解析 Codex Desktop 的 rollout JSONL 会话文件。
/// </summary>
/// <remarks>
/// 本类始终读取文件全量内容。不要改用尾部窗口优化：
/// 尾部窗口会漏掉早期调用记录，导致累计费用被低算
/// （实测 6.9 MB 会话被 4 MB 窗口截断后少算约 36%）。
/// 需要增量跟随时改用 <see cref="SessionLogMonitor"/>。
/// </remarks>
public static class SessionLogReader
{
    /// <summary>读取整个日志文件并解析。</summary>
    /// <exception cref="FileNotFoundException">日志文件不存在。</exception>
    public static SessionSnapshot Read(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);

        if (!File.Exists(logPath))
        {
            throw new FileNotFoundException("会话日志不存在。", logPath);
        }

        var accumulator = new SessionLogAccumulator(logPath);

        using (var stream = OpenRead(logPath))
        {
            if (stream.Length > 0)
            {
                // 按行流式读取，避免把整个文件（可达数十 MB）读成一个大字符串。
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 64 * 1024,
                    leaveOpen: true);

                while (reader.ReadLine() is { } line)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        accumulator.ConsumeLine(trimmed);
                    }
                }
            }
        }

        return accumulator.BuildSnapshot(logPath);
    }

    /// <summary>
    /// 以共享读方式打开文件。Codex 同时持有写入句柄，
    /// 不共享读写会直接失败。
    /// </summary>
    internal static FileStream OpenRead(string logPath) => new(
        logPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// 从文件名提取 thread id。Codex 的 rollout 文件名以 UUID 结尾。
    /// </summary>
    internal static string ExtractThreadId(string logPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(logPath);
        const int uuidLength = 36;
        return fileName.Length >= uuidLength ? fileName[^uuidLength..] : fileName;
    }
}
