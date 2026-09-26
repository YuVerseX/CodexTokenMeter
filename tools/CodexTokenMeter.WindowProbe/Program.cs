using System.Diagnostics;
using CodexTokenMeter.Core.Windowing;

// 诊断：逐个检查 Codex 窗口为何通不过主窗口候选规则。
// 不参与发布产物。

var codexProcessIds = new HashSet<int>();

foreach (var process in Process.GetProcesses())
{
    try
    {
        if (CodexProcessIdentifier.IsCodexDesktop(process.MainModule?.FileName))
        {
            codexProcessIds.Add(process.Id);
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException
        or System.ComponentModel.Win32Exception
        or NotSupportedException)
    {
    }
    finally
    {
        process.Dispose();
    }
}

Console.WriteLine($"Codex 进程：{codexProcessIds.Count} 个");
Console.WriteLine();

var all = NativeWindow.EnumerateTopLevelWindows(codexProcessIds.Contains);
var codexWindows = all.Where(item => item.IsCodexProcess).ToArray();

Console.WriteLine($"Codex 顶层窗口：{codexWindows.Length} 个");
Console.WriteLine();

foreach (var item in codexWindows.OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height))
{
    var reasons = new List<string>();

    if (!item.IsVisible)
    {
        reasons.Add("不可见");
    }

    if (item.IsMinimized)
    {
        reasons.Add("已最小化");
    }

    if (item.OwnerHandle != 0)
    {
        reasons.Add($"有 owner({item.OwnerHandle})");
    }

    if (!string.Equals(item.ClassName, CodexWindowSelector.ChromiumTopLevelClass, StringComparison.Ordinal))
    {
        reasons.Add($"类名不符({item.ClassName})");
    }

    if (item.Bounds.Width < CodexWindowSelector.MinimumHostWidth)
    {
        reasons.Add($"宽度不足({item.Bounds.Width})");
    }

    if (item.Bounds.Height < CodexWindowSelector.MinimumHostHeight)
    {
        reasons.Add($"高度不足({item.Bounds.Height})");
    }

    if ((item.ExtendedStyle & CodexWindowSelector.WsExToolWindow) != 0)
    {
        reasons.Add("toolwindow");
    }

    if ((item.ExtendedStyle & CodexWindowSelector.WsExLayered) != 0)
    {
        reasons.Add("layered");
    }

    var ok = reasons.Count == 0;

    Console.WriteLine($"句柄 {item.Handle,10}  {(ok ? "合格" : "不合格")}  {item.Bounds}");
    Console.WriteLine($"  类名={item.ClassName}  exStyle=0x{item.ExtendedStyle:X8}  进程={item.ProcessId}");
    if (!ok)
    {
        Console.WriteLine($"  原因：{string.Join(", ", reasons)}");
    }

    Console.WriteLine();
}

var host = SelfCheckWrapper.FindHost(codexWindows);
Console.WriteLine(host is null ? "最终结果：无主窗口" : $"最终结果：主窗口 {host.Handle} {host.Bounds}");

return 0;

/// <summary>复用产品规则，避免诊断与实际判定分叉。</summary>
internal static class SelfCheckWrapper
{
    public static WindowCandidate? FindHost(IReadOnlyList<WindowCandidate> windows) =>
        windows
            .Where(item => item.IsVisible && CodexWindowSelector.IsHostCandidate(item))
            .OrderByDescending(item => (long)item.Bounds.Width * item.Bounds.Height)
            .FirstOrDefault();
}
