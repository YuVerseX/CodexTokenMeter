using System.IO;
using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Metrics;
using CodexTokenMeter.Core.Pricing;

namespace CodexTokenMeter.App;

/// <summary>浮层要展示的完整数据。</summary>
public sealed record OverlayData
{
    /// <summary>当前活动会话标识。无会话时为 null。</summary>
    public string? ThreadId { get; init; }

    /// <summary>会话日志路径。</summary>
    public string? LogPath { get; init; }

    /// <summary>模型标识。未知时为 null。</summary>
    public string? Model { get; init; }

    /// <summary>
    /// 会话的工作目录，取自 turn_context。
    /// </summary>
    /// <remarks>
    /// 用于展示项目名。不要从日志所在目录推导：
    /// 会话按年/月/日分层存放，目录名是日期而非项目。
    /// </remarks>
    public string? WorkingDirectory { get; init; }

    public TokenTotals Cumulative { get; init; }

    public TokenTotals CurrentTurn { get; init; }

    /// <summary>
    /// 本轮包含的模型调用次数。
    /// </summary>
    /// <remarks>
    /// 一个 turn 可能包含几十次调用（工具循环），
    /// 因此仅展示“本轮”容易被误读为“最近一次调用”。
    /// </remarks>
    public int CurrentTurnCalls { get; init; }

    public long ContextUsedTokens { get; init; }

    public long ContextWindowTokens { get; init; }

    /// <summary>会话累计费用。未定价时为 null。</summary>
    public double? TotalCost { get; init; }

    /// <summary>最近一轮费用。未定价时为 null。</summary>
    public double? CurrentTurnCost { get; init; }

    public int UserMessageCount { get; init; }
    public int AssistantMessageCount { get; init; }
    public int ToolCallCount { get; init; }
    public int ToolResultCount { get; init; }

    public TimeSpan ActiveDuration { get; init; }

    public int CompactionCount { get; init; }

    /// <summary>数据是否不完整（日志未追平或存在无法解析的行）。</summary>
    public bool IsPartial { get; init; }

    /// <summary>模型未收录价格时为 true，此时费用不可用。</summary>
    public bool IsUnpriced { get; init; }

    /// <summary>
    /// token 计数达到饱和值。
    /// </summary>
    /// <remarks>
    /// 会话日志里的数值异常巨大时会饱和到 <see cref="long.MaxValue"/>。
    /// 上层据此提示「统计异常」，而不是把一个大得离谱的数字
    /// 当作真实用量展示。
    /// </remarks>
    public bool IsSaturated { get; init; }

    /// <summary>IPC 是否已连接。</summary>
    public bool IsConnected { get; init; }

    /// <summary>
    /// 价格表问题提示。为 null 表示价格表正常。
    /// </summary>
    /// <remarks>
    /// 拼错的价格文件会让所有费用静默地回落到内置价，
    /// 用户会以为自己的自定义价格生效了。必须明确告知。
    /// </remarks>
    public string? PricingWarning { get; init; }

    /// <summary>无数据可展示。</summary>
    public bool IsEmpty => ThreadId is null;

    public double ContextPercent => ContextWindowTokens <= 0
        ? 0d
        : Math.Clamp((double)ContextUsedTokens * 100d / ContextWindowTokens, 0d, 100d);

    public double CacheHitRate => Cumulative.CacheHitRate;

    public static OverlayData Empty { get; } = new();
}

/// <summary>
/// 把 IPC 活动会话、会话日志、指标与费用汇总为浮层数据。
/// </summary>
/// <remarks>
/// <para>
/// 订阅 <see cref="CodexIpcMonitor"/> 得知当前会话，再用
/// <see cref="SessionLogMonitor"/> 增量读取该会话的日志。
/// 切换会话时只重建日志监视器，不重新解析全部内容。
/// </para>
/// <para>
/// <see cref="Poll"/> 由 UI 定时驱动。它刻意保持廉价：
/// 无新数据时只做一次文件长度比较。
/// </para>
/// </remarks>
public sealed class OverlayDataProvider : IDisposable
{
    private readonly CodexIpcMonitor _ipc;
    private readonly SessionLogMonitor _logMonitor = new();
    private readonly SessionLogLocator _locator = new();
    private readonly PricingCatalog _catalog;
    private readonly string _sessionsRoot;
    private readonly double _rateMultiplier;
    private readonly string? _pricingWarning;
    private readonly object _sync = new();

    private string? _activeThreadId;
    private string? _activeLogPath;
    private SessionSnapshot? _pricedSnapshot;
    private SessionCostSummary? _pricedCost;
    private OverlayData _current = OverlayData.Empty;
    private bool _disposed;

    /// <param name="sessionsRoot">会话日志根目录。</param>
    /// <param name="catalog">价格表。为 null 时使用内置表。</param>
    /// <param name="rateMultiplier">上游分组倍率，默认 1.0。</param>
    /// <param name="pricingWarning">价格表加载时的问题，展示在面板上。</param>
    public OverlayDataProvider(
        string sessionsRoot,
        PricingCatalog? catalog = null,
        double rateMultiplier = 1.0,
        string? pricingWarning = null)
    {
        _sessionsRoot = sessionsRoot;
        _catalog = catalog ?? PricingCatalog.CreateDefault();
        _rateMultiplier = rateMultiplier;
        _pricingWarning = pricingWarning;
        _ipc = new CodexIpcMonitor();
        _ipc.StatusChanged += OnIpcStatusChanged;
    }

    /// <summary>
    /// 数据或连接状态变化时触发。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>可能在后台线程上触发。</b>订阅者如需访问 UI，
    /// 必须自行调度到 UI 线程（<c>Dispatcher.Invoke</c>）。
    /// </para>
    /// <para>
    /// 当前实现（App 层的 700ms 轮询）不使用本事件，
    /// 而是直接读 <see cref="Current"/>：轮询已经足够及时，
    /// 且避免了跨线程调度的复杂度。保留本事件是为了让
    /// <see cref="OverlayDataProvider"/> 可被其它宿主以事件驱动方式使用。
    /// </para>
    /// </remarks>
    public event EventHandler? DataChanged;

    /// <summary>当前数据快照。</summary>
    public OverlayData Current
    {
        get { lock (_sync) { return _current; } }
    }

    /// <summary>
    /// 刷新一次。由 UI 定时调用。
    /// </summary>
    public void Poll()
    {
        var changed = false;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var data = BuildData();
            if (_current != data)
            {
                _current = data;
                changed = true;
            }
        }

        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private OverlayData BuildData()
    {
        var status = _ipc.GetStatus();
        var threadId = status.ActiveConversationId;

        if (threadId is null)
        {
            return new OverlayData
            {
                IsConnected = status.IsConnected,
                PricingWarning = _pricingWarning,
            };
        }

        var logPath = ResolveLogPath(threadId);
        if (logPath is null)
        {
            // 会话日志尚未落盘，或该会话不是根会话。
            return new OverlayData
            {
                ThreadId = threadId,
                IsConnected = status.IsConnected,
                PricingWarning = _pricingWarning,
            };
        }

        var snapshot = _logMonitor.Poll(logPath);
        var metrics = SessionMetrics.Create(snapshot);
        if (!ReferenceEquals(snapshot, _pricedSnapshot))
        {
            _pricedCost = SessionCostCalculator.Calculate(snapshot, _catalog, rateMultiplier: _rateMultiplier);
            _pricedSnapshot = snapshot;
        }

        var cost = _pricedCost!;
        var hasCompleteCost = cost.TotalCount > 0 && cost.IsComplete;

        return new OverlayData
        {
            ThreadId = threadId,
            LogPath = logPath,
            Model = snapshot.PrimaryModel,
            WorkingDirectory = snapshot.WorkingDirectory,
            Cumulative = metrics.Cumulative,
            CurrentTurn = metrics.CurrentTurn,
            CurrentTurnCalls = snapshot.CurrentTurnCallCount,
            ContextUsedTokens = metrics.ContextUsedTokens,
            ContextWindowTokens = metrics.ContextWindowTokens,
            TotalCost = hasCompleteCost ? cost.TotalCost : null,
            CurrentTurnCost = hasCompleteCost ? cost.CurrentTurnCost : null,
            UserMessageCount = snapshot.UserMessageCount,
            AssistantMessageCount = snapshot.AssistantMessageCount,
            ToolCallCount = snapshot.ToolCallCount,
            ToolResultCount = snapshot.ToolResultCount,
            ActiveDuration = metrics.ActiveDuration,
            CompactionCount = snapshot.CompactionCount,
            IsPartial = metrics.IsPartial,
            IsUnpriced = cost.TotalCount > 0 && !cost.IsComplete,
            IsSaturated = metrics.Cumulative.IsSaturated || metrics.CurrentTurn.IsSaturated,
            IsConnected = status.IsConnected,
            PricingWarning = _pricingWarning,
        };
    }

    /// <summary>
    /// 丢弃缓存状态，下次 <see cref="Poll"/> 重新解析。
    /// </summary>
    /// <remarks>
    /// 当前代码不调用它：会话切换已由 <see cref="Poll"/> 内部检测并重置，
    /// 无需外部干预。留给宿主主动失效使用（例如外部删除了日志文件后
    /// 要求立即重新搜索）。
    /// </remarks>
    public void Invalidate()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _activeThreadId = null;
            _activeLogPath = null;
            _pricedSnapshot = null;
            _pricedCost = null;
            _logMonitor.Reset();
            _locator.Invalidate();
        }
    }

    public void Dispose()
    {
        // Poll 整体持锁；释放监视器也必须在同一锁内完成。
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _logMonitor.Dispose();
            _pricedSnapshot = null;
            _pricedCost = null;
        }

        _ipc.StatusChanged -= OnIpcStatusChanged;
        _ipc.Dispose();
    }

    private void OnIpcStatusChanged(object? sender, EventArgs e)
    {
        // 会话可能已切换；下一轮 Poll 会解析新路径。
        //
        // 本方法在 IPC 的后台线程上被调用（见 CodexIpcMonitor 的说明），
        // 因此这里只发通知，不做任何读取或 UI 操作。
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按 thread id 定位会话日志。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先复用已缓存的路径，避免每轮都做目录遍历。
    /// 缓存失效（文件被归档或删除）时才重新搜索。
    /// </para>
    /// <para>
    /// 缓存的正结果设了复查间隔：**分叉会话可能在运行期间产生**，
    /// 此时旧路径仍然存在，只靠 <c>File.Exists</c> 判断会一直命中
    /// 早已结束的父文件而永不切换。
    /// </para>
    /// <para>
    /// 搜索失败的负结果也会被缓存一小段时间。若不缓存，
    /// 「会话已切换但日志尚未落盘」的窗口期会让每轮轮询
    /// （700ms）都做一次目录遍历。
    /// </para>
    /// </remarks>
    private string? ResolveLogPath(string threadId)
    {
        var now = Environment.TickCount64;

        lock (_sync)
        {
            var sameThread =
                string.Equals(_activeThreadId, threadId, StringComparison.OrdinalIgnoreCase);

            // 已缓存且文件仍在，且未到复查时间：直接返回。
            if (sameThread
                && _activeLogPath is not null
                && now < _nextResolveAllowedAt
                && File.Exists(_activeLogPath))
            {
                return _activeLogPath;
            }

            // 上次搜索没找到，且间隔未到：不要重复搜索。
            if (sameThread
                && _activeLogPath is null
                && now < _nextSearchAllowedAt)
            {
                return null;
            }
        }

        if (!Directory.Exists(_sessionsRoot))
        {
            return null;
        }

        // 按日期倒序定位，避免全盘遍历。
        // 实测 3650 个文件全量扫描 109 ms（UI 线程上会卡顿），
        // 按日期定位只要 0.125 ms。
        var best = _locator.Locate(_sessionsRoot, threadId);
        var pathChanged = false;

        lock (_sync)
        {
            // 文件换了（包括首次找到、分叉产生、或旧文件被归档）：
            // 必须重置解析状态，否则会把上一份文件的偏移与累计混进来。
            pathChanged = !string.Equals(_activeLogPath, best, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_activeThreadId, threadId, StringComparison.OrdinalIgnoreCase);

            if (pathChanged)
            {
                _logMonitor.Reset(best);
            }

            _activeThreadId = threadId;
            _activeLogPath = best;

            // 未找到时节流：新会话的日志可能几秒后才落盘。
            _nextSearchAllowedAt = best is null
                ? now + SearchRetryIntervalMilliseconds
                : 0;

            // 找到后也要定期复查，以便发现运行期间产生的分叉会话。
            _nextResolveAllowedAt = best is null
                ? 0
                : now + ResolveRecheckIntervalMilliseconds;
        }

        return best;
    }

    /// <summary>未找到日志时，两次搜索的最小间隔。</summary>
    private const long SearchRetryIntervalMilliseconds = 3000;

    /// <summary>
    /// 已找到日志时，两次复查的最小间隔。
    /// </summary>
    /// <remarks>
    /// 复查用于发现运行期间产生的分叉会话。
    /// 按日期定位的代价实测约 0.9 ms，2 秒一次即 0.045% 单核，
    /// 相比“永不发现分叉”而言这个代价是值得的。
    /// </remarks>
    private const long ResolveRecheckIntervalMilliseconds = 2000;

    /// <summary>下一次允许搜索的时间点（未找到时）。</summary>
    private long _nextSearchAllowedAt;

    /// <summary>下一次允许复查的时间点（已找到后）。</summary>
    private long _nextResolveAllowedAt;
}

