namespace CodexTokenMeter.Core.Windowing;

/// <summary>
/// 从一批候选窗口中选出 Codex Desktop 的主窗口。
/// </summary>
/// <remarks>
/// <para>
/// Codex Desktop 基于 Chromium，单个进程会创建多个顶层窗口：
/// 主窗口、隐藏的辅助窗口、工具窗口、以及带阴影的合成窗口。
/// 浮层必须贴在**主窗口**上，而不是面积最大的任意窗口。
/// </para>
/// <para>
/// 判定分两步：
/// </para>
/// <list type="number">
/// <item>
/// 前台窗口必须属于 Codex 且可见。否则整个选择返回 null，
/// 浮层据此隐藏自己。
/// </item>
/// <item>
/// 在与前台窗口**同一进程**的候选中选出主窗口。
/// 限定同进程是必要的：多开的 Codex 实例不应互相干扰。
/// </item>
/// </list>
/// </remarks>
public static class CodexWindowSelector
{
    /// <summary>Chromium 顶层窗口的窗口类名。</summary>
    public const string ChromiumTopLevelClass = "Chrome_WidgetWin_1";

    /// <summary>WS_EX_TOOLWINDOW：工具窗口，不出现在任务栏。</summary>
    public const long WsExToolWindow = 0x00000080L;

    /// <summary>WS_EX_LAYERED：分层窗口，Chromium 用于阴影与合成层。</summary>
    public const long WsExLayered = 0x00080000L;

    /// <summary>主窗口的最小宽度。小于此值的一定不是主窗口。</summary>
    public const int MinimumHostWidth = 500;

    /// <summary>主窗口的最小高度。</summary>
    public const int MinimumHostHeight = 400;

    /// <summary>
    /// 选出主窗口。
    /// </summary>
    /// <param name="candidates">所有候选顶层窗口。</param>
    /// <param name="foregroundHandle">当前前台窗口句柄。</param>
    /// <returns>主窗口。前台不是 Codex 或找不到主窗口时返回 null。</returns>
    public static WindowCandidate? Select(
        IReadOnlyList<WindowCandidate> candidates,
        nint foregroundHandle)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var foreground = candidates.FirstOrDefault(item => item.Handle == foregroundHandle);

        // 前台窗口不属于 Codex 或不可见时，浮层应隐藏。
        if (foreground is null || !foreground.IsCodexProcess || !foreground.IsVisible)
        {
            return null;
        }

        var sameProcess = candidates
            .Where(item => item.ProcessId == foreground.ProcessId && item.IsCodexProcess)
            .ToArray();

        // 同进程内取面积最大的合格窗口。
        // 次键用句柄保证结果稳定，避免同一批候选在不同次调用下选出不同窗口。
        return sameProcess
            .Where(IsHostCandidate)
            .OrderByDescending(Area)
            .ThenBy(item => item.Handle)
            .FirstOrDefault();
    }

    /// <summary>
    /// 判定是否为可能的主窗口。
    /// </summary>
    /// <remarks>
    /// 每一项都对应一种真实存在的干扰窗口：隐藏窗口、最小化窗口、
    /// 带 owner 的对话框、工具窗口、分层合成窗口，以及过小的辅助窗口。
    /// </remarks>
    public static bool IsHostCandidate(WindowCandidate item) =>
        item.IsVisible
        && !item.IsMinimized
        && item.OwnerHandle == 0
        && string.Equals(item.ClassName, ChromiumTopLevelClass, StringComparison.Ordinal)
        && item.Bounds.Width >= MinimumHostWidth
        && item.Bounds.Height >= MinimumHostHeight
        && (item.ExtendedStyle & (WsExToolWindow | WsExLayered)) == 0;

    private static long Area(WindowCandidate item) =>
        (long)item.Bounds.Width * item.Bounds.Height;
}
