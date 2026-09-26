using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.App;

/// <summary>
/// 开发期自检：程序化激活 Codex 窗口，供浮层跟随行为验证使用。
/// </summary>
/// <remarks>
/// 通过 <c>--self-check</c> 参数启用，仅用于开发验证，不影响正常启动路径。
/// </remarks>
internal static class SelfCheck
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint handle);

    /// <summary>
    /// 把 Codex 主窗口带到前台。
    /// </summary>
    /// <returns>找到并激活的主窗口句柄，未找到时为 0。</returns>
    internal static nint ActivateCodexWindow()
    {
        var host = FindOrRestoreCodexHostWindow();

        if (host is null)
        {
            return 0;
        }

        ForceForeground(host.Handle);
        return host.Handle;
    }

    /// <summary>
    /// 查找 Codex 主窗口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须先按进程过滤再按规则筛选：其它 Chromium 应用（Edge、VS Code）
    /// 的窗口同样符合 <c>Chrome_WidgetWin_1</c>，
    /// 不过滤进程会把它们的窗口当成 Codex 主窗口。
    /// </para>
    /// <para>
    /// 最小化窗口的坐标是 <c>-32000</c>，不能作为宿主；
    /// 因此这里排除最小化窗口，而不是先恢复再查找。
    /// </para>
    /// </remarks>
    internal static WindowCandidate? FindCodexHostWindow()
    {
        var codexProcessIds = CollectCodexProcessIds();

        if (codexProcessIds.Count == 0)
        {
            return null;
        }

        return NativeWindow
            .EnumerateTopLevelWindows(codexProcessIds.Contains)
            .Where(item => item.IsCodexProcess && item.IsVisible && CodexWindowSelector.IsHostCandidate(item))
            .OrderByDescending(item => (long)item.Bounds.Width * item.Bounds.Height)
            .FirstOrDefault();
    }

    /// <summary>
    /// 查找 Codex 主窗口，必要时先恢复最小化的窗口。
    /// </summary>
    /// <remarks>
    /// 恢复最小化窗口需要时间（系统动画），因此恢复后会轮询等待位置变为有效值。
    /// </remarks>
    internal static WindowCandidate? FindOrRestoreCodexHostWindow(int timeoutMilliseconds = 1500)
    {
        var direct = FindCodexHostWindow();
        if (direct is not null)
        {
            return direct;
        }

        var codexProcessIds = CollectCodexProcessIds();
        if (codexProcessIds.Count == 0)
        {
            return null;
        }

        // 找出被最小化的 Codex 主窗口并恢复它。
        var minimized = NativeWindow
            .EnumerateTopLevelWindows(codexProcessIds.Contains)
            .Where(item => item.IsCodexProcess
                && item.IsMinimized
                && string.Equals(
                    item.ClassName,
                    CodexWindowSelector.ChromiumTopLevelClass,
                    StringComparison.Ordinal))
            .OrderByDescending(item => (long)item.Bounds.Width * item.Bounds.Height)
            .FirstOrDefault();

        if (minimized is null)
        {
            return null;
        }

        ShowWindow(minimized.Handle, SwRestore);

        // 等待窗口恢复完成：轮询直到它不再是最小化状态。
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            var restored = FindCodexHostWindow();
            if (restored is not null)
            {
                return restored;
            }

            Thread.Sleep(60);
        }

        return FindCodexHostWindow();
    }

    /// <summary>收集所有属于 Codex Desktop 的进程 id。</summary>
    private static HashSet<int> CollectCodexProcessIds()
    {
        var ids = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (CodexProcessIdentifier.IsCodexDesktop(process.MainModule?.FileName))
                {
                    ids.Add(process.Id);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
                // 系统进程或权限不足的进程无法读取模块路径。
                // 它们不可能是 Codex，跳过即可，不应中断整轮枚举。
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids;
    }

    /// <summary>
    /// 强制把窗口设为前台。
    /// </summary>
    /// <remarks>
    /// 直接调用 <c>SetForegroundWindow</c> 会被 Windows 拒绝：
    /// 只有当前前台进程，或刚收到用户输入的进程，才有权设置前台窗口。
    /// 通过 <c>AttachThreadInput</c> 临时把本线程挂到当前前台线程的输入队列，
    /// 即可获得该权限，设置完成后立即解除。
    /// </remarks>
    private static void ForceForeground(nint handle)
    {
        var currentForeground = GetForegroundWindow();
        if (currentForeground == handle)
        {
            return;
        }

        var foregroundThread = currentForeground == 0
            ? 0u
            : GetWindowThreadProcessId(currentForeground, out _);

        var targetThread = GetWindowThreadProcessId(handle, out _);

        var attached = false;

        if (foregroundThread != 0 && foregroundThread != targetThread)
        {
            attached = AttachThreadInput(foregroundThread, targetThread, true);
        }

        try
        {
            SetForegroundWindow(handle);
            BringWindowToTop(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foregroundThread, targetThread, false);
            }
        }
    }
}
