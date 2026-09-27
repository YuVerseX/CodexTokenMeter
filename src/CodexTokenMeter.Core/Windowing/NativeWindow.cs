using System.Runtime.InteropServices;
using System.Text;

namespace CodexTokenMeter.Core.Windowing;

/// <summary>
/// Win32 窗口查询与浮层窗口样式设置。
/// </summary>
/// <remarks>
/// 窗口识别与定位使用的 Win32 入口集中在此；鼠标钩子与诊断自检
/// 分别持有自己的原生入口。
///
/// DLL 搜索路径限制在程序集级别声明，见 <c>AssemblyInfo.cs</c>。
/// </remarks>
public static class NativeWindow
{
    private const int GwlExStyle = -20;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaExtendedFrameBounds = 9;

    /// <summary>WS_EX_TOOLWINDOW：不显示在任务栏与 Alt+Tab 列表。</summary>
    private const long WsExToolWindow = 0x00000080L;

    /// <summary>WS_EX_NOACTIVATE：点击不夺取焦点。</summary>
    private const long WsExNoActivate = 0x08000000L;

    /// <summary>WS_EX_TRANSPARENT：点击穿透到下层窗口。</summary>
    private const long WsExTransparent = 0x00000020L;

    private const uint GwOwner = 4;

    /// <summary>
    /// 枚举所有顶层窗口，构造候选列表。
    /// </summary>
    /// <param name="isCodexProcess">
    /// 判定进程是否属于 Codex。
    /// </param>
    /// <remarks>
    /// <para>
    /// 只对属于 Codex 的窗口做昂贵查询。
    /// </para>
    /// <para>
    /// 实机踩到：系统顶层窗口约 570 个，逐一做 8 次 Win32 查询
    /// （含跨进程的 <c>DwmGetWindowAttribute</c>）会把单核跑到饱和。
    /// 而其中属于 Codex 的窗口只有十几个，其余查询全是浪费。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<WindowCandidate> EnumerateTopLevelWindows(
        Func<int, bool> isCodexProcess)
    {
        ArgumentNullException.ThrowIfNull(isCodexProcess);

        var results = new List<WindowCandidate>();

        // 进程归属可能随 PID 复用变化，但单次枚举内只需查一次。
        var processCache = new Dictionary<int, bool>();

        // 必须把委托存成局部变量再由它构造委托实例。
        // 直接传 lambda 会让编译器生成临时委托对象，
        // 它在 EnumWindows 执行期间可能被 GC 回收，
        // 而系统仍持有该函数指针，回调时就会崩溃。
        EnumWindowsProc callback = (handle, _) =>
        {
            var processId = GetWindowProcessId(handle);
            if (processId == 0)
            {
                return true;
            }

            if (!processCache.TryGetValue(processId, out var isCodex))
            {
                isCodex = isCodexProcess(processId);
                processCache[processId] = isCodex;
            }

            // 非 Codex 窗口不构造候选：避免每轮上千次无效调用。
            if (!isCodex)
            {
                return true;
            }

            results.Add(new WindowCandidate
            {
                Handle = handle,
                ProcessId = processId,
                IsCodexProcess = true,
                IsVisible = IsWindowVisible(handle),
                IsMinimized = IsIconic(handle),
                OwnerHandle = GetWindow(handle, GwOwner),
                ExtendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64(),
                Bounds = GetWindowBounds(handle),
                ClassName = GetClassNameText(handle),
            });

            return true;
        };

        EnumWindows(callback, IntPtr.Zero);

        return results;
    }

    /// <summary>取得前台窗口句柄。</summary>
    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>
    /// 取得鼠标的屏幕位置（物理像素）。
    /// </summary>
    /// <remarks>
    /// 用 Win32 而不是 WPF 的鼠标事件：拖动时指针很快会移出胶囊之外，
    /// 此时 WPF 的相对坐标会失真，而屏幕绝对坐标始终可靠。
    /// 返回物理像素，调用方需自行换算为 DIP。
    /// </remarks>
    public static (int X, int Y) GetCursorPosition() =>
        GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    /// <summary>
    /// 以屏幕物理像素取得窗口的可视矩形。
    /// </summary>
    /// <remarks>
    /// 优先使用 <c>DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)</c>。
    /// <c>GetWindowRect</c> 在 Windows 10 之后包含不可见的阴影边框，
    /// 用它定位会让浮层与视觉边界产生数像素偏差。
    /// </remarks>
    public static IntRect GetWindowBounds(nint handle)
    {
        if (DwmGetWindowAttribute(
                handle,
                DwmwaExtendedFrameBounds,
                out var bounds,
                Marshal.SizeOf<NativeRect>()) == 0)
        {
            return new IntRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }

        return GetWindowRect(handle, out var fallback)
            ? new IntRect(fallback.Left, fallback.Top, fallback.Right, fallback.Bottom)
            : IntRect.Empty;
    }

    /// <summary>
    /// 把浮层窗口设置为无焦点、不占任务栏，并按需点击穿透。
    /// </summary>
    /// <param name="handle">窗口句柄。</param>
    /// <param name="clickThrough">是否整体点击穿透。</param>
    public static void ApplyOverlayStyles(nint handle, bool clickThrough = false)
    {
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExNoActivate;

        if (clickThrough)
        {
            style |= WsExTransparent;
        }
        else
        {
            style &= ~WsExTransparent;
        }

        SetWindowLongPtr(handle, GwlExStyle, new nint(style));
    }

    /// <summary>按系统主题设置窗口的明暗外观。</summary>
    public static void ApplyDarkMode(nint handle, bool isDark)
    {
        var value = isDark ? 1 : 0;

        // 返回值有意忽略：
        // DWMWA_USE_IMMERSIVE_DARK_MODE 在 Windows 10 1809 之前不存在，
        // 旧系统上必然失败（E_INVALIDARG），而这只是外观偏好，
        // 失败时保留系统默认外观即可，不应影响浮层功能。
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static int GetWindowProcessId(nint handle)
    {
        // GetWindowThreadProcessId 返回线程 id，进程 id 通过 out 参数给出。
        // 返回值有意忽略：句柄无效时它返回 0，同时把 processId 置 0，
        // 调用方已用 processId == 0 判无效，无需再看返回值。
        _ = GetWindowThreadProcessId(handle, out var processId);
        return (int)processId;
    }

    /// <summary>
    /// 读取窗口类名。
    /// </summary>
    /// <remarks>
    /// 用固定大小的字符缓冲区而非 <see cref="StringBuilder"/>：
    /// 后者需要在原生堆上分配并两次跨越托管边界，
    /// 而类名上限仅 256 字符，栈上数组更快且无分配。
    /// </remarks>
    private static string GetClassNameText(nint handle)
    {
        Span<char> buffer = stackalloc char[256];
        var length = GetClassNameW(handle, ref MemoryMarshal.GetReference(buffer), buffer.Length);

        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameW(nint handle, ref char className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint handle,
        int attribute,
        out NativeRect value,
        int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint handle,
        int attribute,
        ref int value,
        int size);
}
