namespace CodexTokenMeter.Core.Windowing;

/// <summary>
/// 判定某个进程是否属于 Codex Desktop。
/// </summary>
/// <remarks>
/// <para>
/// 不能按进程名判断。实测 Codex Desktop 的进程名是 <c>ChatGPT</c>，
/// 可执行文件位于 MSIX 包
/// <c>C:\Program Files\WindowsApps\OpenAI.Codex_&lt;版本&gt;_x64__&lt;标识&gt;\app\ChatGPT.exe</c>。
/// 而 <c>codex.exe</c> 是命令行工具，<c>codex-*</c> 是其辅助进程，
/// 都不创建主窗口。
/// </para>
/// <para>
/// 因此判定依据是**可执行文件路径**：位于 OpenAI 的 Codex MSIX 包目录下。
/// 单纯匹配进程名 <c>ChatGPT</c> 不可靠——同名应用可能来自其它厂商。
/// </para>
/// </remarks>
public static class CodexProcessIdentifier
{
    /// <summary>MSIX 安装目录中包名前缀。</summary>
    public const string PackagePrefix = "OpenAI.Codex_";

    /// <summary>Windows 应用包安装根目录。</summary>
    public const string WindowsAppsSegment = "\\WindowsApps\\";

    /// <summary>桌面包内的可执行文件名。</summary>
    public const string DesktopExecutable = "ChatGPT.exe";

    /// <summary>开发部署时可执行文件所在目录。</summary>
    public const string DevelopmentDirectory = "\\AppData\\Local\\OpenAI\\Codex\\";

    /// <summary>
    /// 按可执行文件路径判定。
    /// </summary>
    /// <param name="executablePath">进程的映像路径。无法取得时为 null 或空。</param>
    public static bool IsCodexDesktop(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        // 两类安装位置：
        // 1. MSIX 包目录，路径形如 ...\WindowsApps\OpenAI.Codex_<版本>_x64__<标识>\app\ChatGPT.exe
        // 2. 开发/本地部署目录 ...\AppData\Local\OpenAI\Codex\...
        var isPackaged = executablePath.Contains(WindowsAppsSegment, StringComparison.OrdinalIgnoreCase)
            && executablePath.Contains(PackagePrefix, StringComparison.OrdinalIgnoreCase);

        var isDevelopment = executablePath.Contains(DevelopmentDirectory, StringComparison.OrdinalIgnoreCase);

        if (!isPackaged && !isDevelopment)
        {
            return false;
        }

        // 必须是桌面包的可执行文件：同目录下的 codex*.exe 是命令行子进程。
        return Path.GetFileName(executablePath)
            .Equals(DesktopExecutable, StringComparison.OrdinalIgnoreCase);
    }
}
