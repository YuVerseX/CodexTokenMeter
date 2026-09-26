using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexTokenMeter.Core.Windowing;

// 诊断：检查浮层的跟随预检是否真的在运行。
// 不参与发布产物。

NativeMethods.SetProcessDpiAwarenessContext(-4);

var overlayProcess = Process.GetProcesses()
    .FirstOrDefault(p => p.ProcessName.Equals("CodexTokenMeter", StringComparison.OrdinalIgnoreCase));

if (overlayProcess is null)
{
    Console.Error.WriteLine("浮层未运行。");
    return 1;
}

var overlayHandle = FindOverlay(overlayProcess.Id);

if (overlayHandle == 0)
{
    Console.Error.WriteLine("未找到浮层窗口。");
    return 2;
}

Console.WriteLine("=== 观察浮层位置 8 秒（不干预）===");

for (var i = 0; i < 8; i++)
{
    var bounds = NativeWindow.GetWindowBounds(overlayHandle);
    Console.WriteLine($"  [{i}s] {bounds}");
    Thread.Sleep(1000);
}

Console.WriteLine();
Console.WriteLine("=== 用 SetWindowPos 移动到错误位置 ===");

var original = NativeWindow.GetWindowBounds(overlayHandle);
var wrongX = original.Left - 700;
var wrongY = original.Top + 200;

NativeMethods.SetWindowPos(
    overlayHandle,
    nint.Zero,
    wrongX,
    wrongY,
    0,
    0,
    0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE

Console.WriteLine($"  目标 ({wrongX},{wrongY})");

for (var i = 0; i < 8; i++)
{
    Thread.Sleep(1000);
    var bounds = NativeWindow.GetWindowBounds(overlayHandle);
    Console.WriteLine($"  [{i + 1}s] {bounds}");
}

var final = NativeWindow.GetWindowBounds(overlayHandle);
var pulledBack = Math.Abs(final.Left - original.Left) <= 20 && Math.Abs(final.Top - original.Top) <= 20;

Console.WriteLine();
Console.WriteLine($"原始位置：{original}");
Console.WriteLine($"最终位置：{final}");
Console.WriteLine($"被纠正：  {pulledBack}");

return pulledBack ? 0 : 3;

static nint FindOverlay(int processId)
{
    nint result = 0;

    NativeMethods.EnumWindows(
        (handle, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);

            if ((int)pid != processId || !NativeMethods.IsWindowVisible(handle))
            {
                return true;
            }

            if (NativeWindow.GetWindowBounds(handle).Width < 100)
            {
                return true;
            }

            result = handle;
            return false;
        },
        nint.Zero);

    return result;
}

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint handle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
