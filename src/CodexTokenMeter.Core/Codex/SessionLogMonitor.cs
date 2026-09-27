using System.Text;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 对单个会话日志做增量解析，并维护累计状态。
/// </summary>
/// <remarks>
/// 不能只读文件尾部窗口：尾部窗口会漏掉早期调用记录，
/// 导致累计费用被低算（实测 6.9 MB 会话被 4 MB 窗口截断后少算约 36%）。
/// 因此首次读取整个文件，之后只读取新增部分。
/// 本类是线程安全的；并发调用会被串行化。
/// </remarks>
public sealed class SessionLogMonitor : IDisposable
{
    /// <summary>默认单次读取块大小。</summary>
    private const int DefaultChunkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 单次 <see cref="Poll"/> 最多消费的字节数。
    /// 用于在文件持续高速增长时避免无限循环；正常情况不会触达。
    /// </summary>
    private const long MaxBytesPerPoll = 256L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly SessionLogAccumulator _accumulator;
    private readonly int _chunkBytes;
    private readonly MemoryStream _pending = new();

    private string? _logPath;

    /// <summary>
    /// 已从文件读入的字节偏移。
    /// 已读入但尚未构成完整行的字节保存在 <see cref="_pending"/> 中。
    /// </summary>
    private long _offset;

    /// <summary>是否已在文件开头检查并跳过 UTF-8 BOM。</summary>
    private bool _bomChecked;

    /// <summary>
    /// 缓存的快照。仅在本次 <see cref="Poll"/> 消费了新数据时才重建。
    /// </summary>
    /// <remarks>
    /// 轮询频率远高于日志写入频率，大多数调用没有新内容。
    /// 不缓存会在每次轮询时重新复制全部记录（实测 40 万条记录下
    /// 每次重建约 78 MB），造成无谓的分配与 GC 压力。
    /// </remarks>
    private SessionSnapshot? _cachedSnapshot;

    /// <param name="logPath">初始日志路径，可稍后由 <see cref="Poll"/> 指定。</param>
    /// <param name="chunkBytes">
    /// 单次读取块大小。测试用小值以覆盖块边界行为。
    /// </param>
    public SessionLogMonitor(string? logPath = null, int chunkBytes = DefaultChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);

        _accumulator = new SessionLogAccumulator(logPath);
        _logPath = logPath;
        _chunkBytes = chunkBytes;
    }

    /// <summary>当前监视的日志路径。</summary>
    public string? LogPath
    {
        get { lock (_sync) { return _logPath; } }
    }

    /// <summary>已从文件读入的字节偏移。</summary>
    public long Offset
    {
        get { lock (_sync) { return _offset; } }
    }

    /// <summary>已读入但尚未构成完整行的待处理字节数。</summary>
    public int PendingByteCount
    {
        get { lock (_sync) { return (int)_pending.Length; } }
    }

    /// <summary>
    /// 读取自上次调用以来新增的内容，返回当前累计快照。
    /// </summary>
    /// <remarks>
    /// 内部循环读取直到追平文件末尾，因此返回的快照总是完整的。
    /// 内存占用有界：读缓冲固定，跨块的行由待处理缓冲区承载，
    /// 其大小受单行上限约束。
    /// </remarks>
    public SessionSnapshot Poll(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);

        lock (_sync)
        {
            // 释放检查必须在锁内。
            //
            // 在锁外检查会留下窗口：检查通过后、取得锁之前，
            // 另一个线程可能完成 Dispose（它在锁内置 _disposed 并释放缓冲），
            // 随后本次调用就会对已释放的 MemoryStream 调用 SetLength。
            ThrowIfDisposed();

            if (!string.Equals(_logPath, logPath, StringComparison.OrdinalIgnoreCase))
            {
                ResetCore(logPath);
            }

            var caughtUp = ReadUntilCaughtUp();
            return EnsureSnapshot(_logPath ?? logPath, caughtUp);
        }
    }

    /// <summary>
    /// 返回当前快照，只在内容或追平状态变化时重建。
    /// </summary>
    /// <remarks>
    /// 缓存必须把 <see cref="SessionSnapshot.IsCaughtUp"/> 一起纳入，
    /// 否则每次轮询仍会因 with 表达式产生一次新对象分配。
    /// </remarks>
    private SessionSnapshot EnsureSnapshot(string logPath, bool caughtUp)
    {
        if (_cachedSnapshot is not null
            && _cachedSnapshot.IsCaughtUp == caughtUp
            && string.Equals(_cachedSnapshot.LogPath, logPath, StringComparison.Ordinal))
        {
            return _cachedSnapshot;
        }

        // 只有在解析结果本身变化时才重建：缓冲消费会清空 _cachedSnapshot，
        // 此时必须先重新构建。
        _cachedSnapshot = _cachedSnapshot is null
            ? _accumulator.BuildSnapshot(logPath)
            : _cachedSnapshot;

        if (_cachedSnapshot.IsCaughtUp != caughtUp)
        {
            _cachedSnapshot = _cachedSnapshot with { IsCaughtUp = caughtUp };
        }

        return _cachedSnapshot;
    }

    /// <summary>
    /// 丢弃已累积状态，并可切换到另一份日志。
    /// </summary>
    /// <remarks>
    /// 切换会话时复用同一实例，而不是新建：
    /// 每次切换都新建会让 <see cref="MemoryStream"/> 的缓冲被反复分配。
    /// </remarks>
    public void Reset(string? logPath = null)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ResetCore(logPath);
        }
    }

    private void ResetCore(string? logPath)
    {
        _logPath = logPath;
        _offset = 0;
        _pending.SetLength(0);
        _bomChecked = false;
        _cachedSnapshot = null;
        _accumulator.Reset(logPath);
    }

    /// <summary>
    /// 循环读取直到追平文件末尾，或达到单次调用预算。
    /// </summary>
    /// <returns>是否已追平文件末尾。</returns>
    /// <remarks>
    /// 文件句柄在整个循环中复用。早期实现在每个块重新打开文件，
    /// 小步长下会产生大量系统调用。
    /// </remarks>
    private bool ReadUntilCaughtUp()
    {
        if (_logPath is null || !File.Exists(_logPath))
        {
            return true;
        }

        using var stream = SessionLogReader.OpenRead(_logPath);

        long readThisPoll = 0;

        while (true)
        {
            var (caughtUp, read) = ReadNextChunk(stream);
            if (caughtUp)
            {
                return true;
            }

            if (read <= 0)
            {
                // 文件长度未增长且仍有余量未读，说明遇到了异常情况；
                // 保留待处理内容，等文件继续增长。
                return false;
            }

            readThisPoll += read;
            if (readThisPoll >= MaxBytesPerPoll)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 从已打开的流读入一个数据块并消费其中完整的行。
    /// </summary>
    /// <returns>是否已追平文件末尾，以及本次从文件读入的字节数。</returns>
    private (bool CaughtUp, long Read) ReadNextChunk(FileStream stream)
    {
        if (_logPath is null)
        {
            return (true, 0);
        }

        var length = stream.Length;

        if (length < _offset)
        {
            // 文件被截断或替换：重新从头解析。
            ResetCore(_logPath);
        }

        if (length == _offset)
        {
            // 已读到文件末尾。只有待处理内容也为空时才算真正追平：
            // 残留的尾部片段意味着还有一个行未反映到快照中。
            return (_pending.Length == 0, 0);
        }

        stream.Seek(_offset, SeekOrigin.Begin);

        var remaining = length - _offset;
        var toRead = (int)Math.Min(remaining, _chunkBytes);

        var buffer = new byte[toRead];
        var read = stream.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);
        if (read <= 0)
        {
            return (true, 0);
        }

        if (read > 0)
        {
            // 所有读入的字节都进入待处理缓冲。这样即使一个行跨越多个块
            // （甚至在 1 字节块下），也能拼齐后再解析。
            _pending.Write(buffer, 0, read);
        }

        _offset += read;
        // 文件头的 UTF-8 BOM 必须跳过。全量路径的 StreamReader 会自动剥离它，
        // 增量路径按字节读取则不会，不处理就会使首行解析失败而丢掉第一条记录。
        // 检测必须在待处理缓冲上进行：BOM 可能跨多个块，
        // 只看当前块会在块小于 3 字节时漏判。
        if (!_bomChecked && _pending.Length >= 3)
        {
            _bomChecked = true;
            var head = _pending.GetBuffer();
            if (head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                RemovePendingPrefix(3);
            }
        }

        ConsumeCompleteLines(atEndOfStream: _offset >= length);

        // 缓冲区内容已变化，缓存的快照不再有效。
        _cachedSnapshot = null;

        return (_offset >= length && _pending.Length == 0, read);
    }

    /// <summary>移除待处理缓冲区的开头若干字节。</summary>
    private void RemovePendingPrefix(int count)
    {
        var length = (int)_pending.Length;
        if (count >= length)
        {
            _pending.SetLength(0);
            return;
        }

        var buffer = _pending.GetBuffer();
        Buffer.BlockCopy(buffer, count, buffer, 0, length - count);
        _pending.SetLength(length - count);
    }

    /// <summary>
    /// 消费待处理缓冲区中的完整行；末尾不完整的部分保留。
    /// </summary>
    /// <param name="atEndOfStream">
    /// 是否已到达文件末尾。只有在此为 true 时，末尾缺换行的内容
    /// 才被当作完整行解析。
    /// </param>
    private void ConsumeCompleteLines(bool atEndOfStream)
    {
        if (_pending.Length == 0)
        {
            return;
        }

        var bytes = _pending.GetBuffer().AsSpan(0, (int)_pending.Length);
        var lastNewline = bytes.LastIndexOf((byte)'\n');

        if (lastNewline < 0)
        {
            // 整个缓冲都没有完整行。已到文件末尾时，它本身就是一行（仅缺结尾换行）；
            // 否则等待后续数据拼齐。
            if (atEndOfStream)
            {
                ConsumeTrailingTail();
                return;
            }
            // 若整段已超过单行上限，说明该行必然损坏，
            // 丢弃以免缓冲无限增长。
            if (_pending.Length > SessionLogAccumulator.MaxLineBytes)
            {
                _accumulator.RecordDiscardedLine();
                _pending.SetLength(0);
            }

            return;
        }

        var consumableLength = lastNewline + 1;
        var text = Encoding.UTF8.GetString(bytes[..consumableLength]);

        ConsumeLines(text);

        // 保留剩余未消费字节。此处必须复制：GetBuffer 返回的内部数组
        // 会在后续写入时被覆盖。
        var leftover = bytes[consumableLength..];
        var leftoverCopy = leftover.ToArray();
        _pending.SetLength(0);
        _pending.Write(leftoverCopy, 0, leftoverCopy.Length);

        if (atEndOfStream && _pending.Length > 0)
        {
            ConsumeTrailingTail();
        }
    }

    /// <summary>
    /// 文件末尾无换行的尾段：尝试作为完整行解析，
    /// 成功则消费，失败则留待文件继续增长。
    /// </summary>
    private void ConsumeTrailingTail()
    {
        var bytes = _pending.GetBuffer().AsSpan(0, (int)_pending.Length);
        var text = Encoding.UTF8.GetString(bytes).Trim();

        if (text.Length == 0 || _accumulator.ConsumeLine(text, countUnreadable: false))
        {
            _pending.SetLength(0);
        }
    }

    /// <summary>
    /// 释放待处理缓冲区。
    /// </summary>
    /// <remarks>
    /// 该类实例长期存活（跟随会话），但 <see cref="MemoryStream"/>
    /// 会随日志增长保留一块可达 4 MB 的内部数组。
    /// 应用退出或彻底放弃该实例时应调用此方法。
    /// 调用后实例不可再使用。
    /// </remarks>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _pending.Dispose();
            _cachedSnapshot = null;
        }

        // 不写终结器：本类不含非托管资源，
        // 缓冲区由 GC 负责回收，因此无需 SuppressFinalize。
    }

    /// <summary>
    /// 已释放标志。
    /// </summary>
    /// <remarks>
    /// 读写都在 <c>_sync</c> 锁内，因此不需要 <c>volatile</c>。
    /// </remarks>
    private bool _disposed;

    /// <summary>抛出异常，阻止已释放实例继续使用。</summary>
    /// <remarks>调用方必须已持有 <c>_sync</c>。</remarks>
    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>逐行解析文本。</summary>
    private void ConsumeLines(string text)
    {
        var start = 0;

        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                break;
            }

            var length = newline - start;
            if (length > 0)
            {
                var line = text.AsSpan(start, length).Trim();
                if (!line.IsEmpty)
                {
                    _accumulator.ConsumeLine(line.ToString());
                }
            }

            start = newline + 1;
        }
    }
}
