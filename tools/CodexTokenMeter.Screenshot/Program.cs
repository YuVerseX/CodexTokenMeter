using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

// 开发期工具：截取浮层窗口，可指定是否先展开面板。
// 不参与发布产物。
//
// 用法：
//   dotnet run --project tools/CodexTokenMeter.Screenshot -- <输出路径> [--expanded]

const int PerMonitorAwareV2 = -4;
SetProcessDpiAwarenessContext(PerMonitorAwareV2);

var outputPath = args.Length > 0
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "codex-token-meter.png");

var target = Process.GetProcesses().FirstOrDefault(IsMeterProcess);

if (target is null)
{
    Console.Error.WriteLine("未找到运行中的 CodexTokenMeter 进程。");
    return 1;
}

var handle = WindowCapture.FindOverlayWindow(target.Id);

if (handle == 0)
{
    Console.Error.WriteLine("未找到浮层窗口。");
    return 2;
}

if (!WindowCapture.GetWindowRect(handle, out var rect))
{
    Console.Error.WriteLine("无法取得窗口矩形。");
    return 3;
}

var width = rect.Right - rect.Left;
var height = rect.Bottom - rect.Top;

Console.WriteLine($"窗口物理矩形：({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) [{width}×{height}]");

if (width <= 0 || height <= 0)
{
    Console.Error.WriteLine("窗口尺寸无效，可能已隐藏。");
    return 4;
}

using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

// 用屏幕抓取而不是 PrintWindow。
//
// PrintWindow 对分层窗口（WS_EX_LAYERED + AllowsTransparency）返回的是
// 未与桌面合成的中间画面：实测会混入不属于本窗口的内容，
// 导致误判为「内容被裁」。屏幕抓取得到的是用户实际看到的画面。
WindowCapture.CaptureFromScreen(bitmap, rect);

bitmap.Save(outputPath, ImageFormat.Png);

Console.WriteLine($"已保存：{outputPath}  {new FileInfo(outputPath).Length / 1024.0:F1} KB");

return 0;

static bool IsMeterProcess(Process process)
{
    try
    {
        return process.ProcessName.Equals("CodexTokenMeter", StringComparison.OrdinalIgnoreCase);
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

/// <summary>声明本进程的 DPI 感知级别。</summary>
[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(int context);

/// <summary>窗口查找与截取。</summary>
internal static class WindowCapture
{
    internal static nint FindOverlayWindow(int processId)
    {
        nint result = 0;

        EnumWindows(
            (handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var windowProcessId);

                if ((int)windowProcessId != processId || !IsWindowVisible(handle))
                {
                    return true;
                }

                var title = new StringBuilder(256);
                GetWindowText(handle, title, title.Capacity);

                if (!title.ToString().Equals("Codex Token Meter", StringComparison.Ordinal))
                {
                    return true;
                }

                result = handle;
                return false;
            },
            nint.Zero);

        return result;
    }

    internal static void CaptureFromScreen(Bitmap bitmap, Rect rect)
    {
        using var graphics = Graphics.FromImage(bitmap);

        graphics.CopyFromScreen(
            rect.Left,
            rect.Top,
            0,
            0,
            new Size(rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
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
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint handle, out Rect rect);
}
