using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexTokenMeter.App;

/// <summary>
/// 全局鼠标按键监听。
/// </summary>
/// <remarks>
/// <para>
/// 浮层刻意不夺取焦点（<c>WS_EX_NOACTIVATE</c>），因此无法用「失去激活」
/// 判断用户点到别处。这里安装低层鼠标钩子，在按键发生时回调调用方，
/// 由调用方判断是否落在浮层之外。
/// </para>
/// <para>
/// 钩子回调在安装钩子的线程上被调用，需要该线程持续泵消息——
/// 在 WPF 里即 UI 线程已满足此条件。
/// </para>
/// <para>
/// 回调必须尽快返回：系统对低层钩子有超时限制（约 300ms），
/// 超时会被静默移除。因此回调中只做坐标判断，不执行耗时操作。
/// </para>
/// </remarks>
internal sealed class MouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;

    private readonly LowLevelMouseProc _callback;
    private nint _hookHandle;
    private bool _disposed;

    /// <summary>鼠标按键按下时触发，参数为屏幕坐标。</summary>
    internal event EventHandler<(double X, double Y)>? ButtonDown;

    internal MouseHook()
    {
        // 保存委托实例：若只传匿名方法，它可能被回收，
        // 导致系统调用已释放的回调而崩溃。
        _callback = OnMouseEvent;
    }

    /// <summary>安装钩子。</summary>
    /// <returns>安装是否成功。</returns>
    internal bool Install()
    {
        lock (_gate)
        {
            if (_hookHandle != 0)
            {
                return true;
            }

            if (_disposed)
            {
                return false;
            }

            using var currentProcess = Process.GetCurrentProcess();
            using var currentModule = currentProcess.MainModule;

            var moduleHandle = GetModuleHandle(currentModule?.ModuleName);

            _hookHandle = SetWindowsHookEx(WhMouseLl, _callback, moduleHandle, 0);

            return _hookHandle != 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_hookHandle != 0)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = 0;
            }
        }
    }

    /// <summary>保护安装/卸载与句柄字段。</summary>
    private readonly Lock _gate = new();

    private nint OnMouseEvent(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;

            if (message is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown)
            {
                var data = Marshal.PtrToStructure<MouseHookData>(lParam);

                try
                {
                    ButtonDown?.Invoke(this, (data.PointX, data.PointY));
                }
                catch (Exception exception)
                {
                    // 吞掉**所有**异常，不只是预期的那两种。
                    //
                    // 本回调在系统输入链上运行：异常会穿过原生帧向上传播，
                    // 结果是鼠标输入被丢弃或系统将钩子静默移除。
                    // 订阅者的缺陷不应影响用户的鼠标。
                    //
                    // 不重抛也不记录：钩子回调有 300ms 超时限制，
                    // 写日志会拖长回调耗时。
                    _ = exception;
                }
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int PointX;
        public int PointY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
