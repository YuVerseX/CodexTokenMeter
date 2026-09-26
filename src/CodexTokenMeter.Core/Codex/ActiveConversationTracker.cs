using System.Collections.Concurrent;

namespace CodexTokenMeter.Core.Codex;

/// <summary>
/// 追踪 Codex Desktop 当前跟随的会话。
/// </summary>
/// <remarks>
/// <para>
/// 协议来自对 <c>\\.\pipe\codex-ipc</c> 的实测观测：
/// 帧格式为 4 字节小端长度前缀 + UTF-8 JSON；
/// 客户端发送 <c>initialize</c> 后即可收到广播。
/// </para>
/// <para>
/// 切换任务时 Codex 会连续发出两条广播——旧会话 <c>following=false</c>
/// 与新会话 <c>following=true</c>，实测间隔约 4ms。
/// 因此不能在收到 false 时立即清空状态，否则浮层会短暂闪烁。
/// 正确做法是维护「每个窗口跟随哪个会话」的映射，
/// 只在映射本身变化时更新对外状态。
/// </para>
/// <para>
/// 窗口标识由 <c>sourceClientId</c> 与 <c>hostId</c> 组成：
/// 同一个 Codex 进程可以开多个窗口，各自跟随不同会话。
/// 多窗口时取**最后开始跟随**的会话。
/// </para>
/// </remarks>
internal sealed class ActiveConversationTracker
{
    /// <summary>窗口键的分隔符。使用不会出现在 id 中的控制字符。</summary>
    private const char KeySeparator = '\u001F';

    private readonly Dictionary<string, WindowState> _byWindow = new(StringComparer.Ordinal);

    /// <summary>递增的跟随序号，用于判定哪个窗口最近开始跟随。</summary>
    private long _sequence;

    /// <summary>当前活动会话标识，无会话时为 null。</summary>
    public string? ActiveConversationId { get; private set; }

    /// <summary>状态版本号，每次活动会话或连接状态变化时递增。</summary>
    public long Version { get; private set; }

    /// <summary>正在跟随会话的窗口数。</summary>
    public int ActiveWindowCount => _byWindow.Count;

    /// <summary>清空全部状态。连接断开时调用。</summary>
    /// <returns>状态是否发生了变化。</returns>
    public bool Reset()
    {
        if (_byWindow.Count == 0 && ActiveConversationId is null)
        {
            return false;
        }

        _byWindow.Clear();
        ActiveConversationId = null;
        Version++;
        return true;
    }

    /// <summary>
    /// 处理一条 <c>thread-stream-following-changed</c> 广播。
    /// </summary>
    /// <param name="conversationId">广播中的会话标识。</param>
    /// <param name="following">true 表示开始跟随，false 表示停止。</param>
    /// <param name="hostId">窗口所在主机标识。</param>
    /// <param name="sourceClientId">发出广播的客户端标识，即窗口归属。</param>
    /// <returns>活动会话是否发生变化。</returns>
    public bool ApplyFollowingChanged(
        string conversationId,
        bool following,
        string hostId,
        string sourceClientId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)
            || string.IsNullOrWhiteSpace(hostId)
            || string.IsNullOrWhiteSpace(sourceClientId))
        {
            return false;
        }

        var key = sourceClientId + KeySeparator + hostId;

        if (following)
        {
            _byWindow[key] = new WindowState(conversationId, ++_sequence);
        }
        else
        {
            if (!_byWindow.TryGetValue(key, out var existing))
            {
                return false;
            }

            // 仅当该窗口当前跟随的正是这个会话时才移除。
            // 过期的 false 广播不应影响已经切到别的会话的窗口。
            if (!string.Equals(existing.ConversationId, conversationId, StringComparison.Ordinal))
            {
                return false;
            }

            _byWindow.Remove(key);
        }

        return Recompute();
    }

    /// <summary>
    /// 处理客户端断开广播，移除该客户端的所有窗口。
    /// </summary>
    /// <returns>活动会话是否发生变化。</returns>
    public bool ApplyClientDisconnected(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        var prefix = clientId + KeySeparator;
        var stale = _byWindow.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        if (stale.Length == 0)
        {
            return false;
        }

        foreach (var key in stale)
        {
            _byWindow.Remove(key);
        }

        return Recompute();
    }

    /// <summary>
    /// 重新计算活动会话：取最后开始跟随的窗口。
    /// </summary>
    /// <remarks>
    /// 按显式序号而不是字典枚举顺序选取——字典顺序不保证稳定，
    /// 依赖它会得到随机的活动会话。
    /// </remarks>
    private bool Recompute()
    {
        string? next = null;
        var highest = -1L;

        foreach (var state in _byWindow.Values)
        {
            if (state.Sequence > highest)
            {
                highest = state.Sequence;
                next = state.ConversationId;
            }
        }

        if (string.Equals(next, ActiveConversationId, StringComparison.Ordinal))
        {
            return false;
        }

        ActiveConversationId = next;
        Version++;
        return true;
    }

    private readonly record struct WindowState(string ConversationId, long Sequence);
}

/// <summary>IPC 连接状态。</summary>
public sealed record IpcConnectionStatus
{
    public required bool IsConnected { get; init; }

    /// <summary>当前活动会话标识。未连接或无会话时为 null。</summary>
    public string? ActiveConversationId { get; init; }

    /// <summary>状态版本号，变化表示活动会话或连接状态有更新。</summary>
    public required long Version { get; init; }

    /// <summary>正在跟随会话的窗口数。</summary>
    public required int ActiveWindowCount { get; init; }

    /// <summary>最近一次错误信息，无错误时为 null。</summary>
    public string? LastError { get; init; }

    /// <summary>已接收的帧数，用于诊断。</summary>
    public required long FramesReceived { get; init; }
}
