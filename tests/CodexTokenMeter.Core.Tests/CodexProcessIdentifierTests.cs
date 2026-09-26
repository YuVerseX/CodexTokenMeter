using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 进程识别规则的验证。
/// 测试数据取自实机观测：Codex Desktop 的进程名是 ChatGPT，
/// 可执行文件位于 MSIX 包目录下。
/// </summary>
public class CodexProcessIdentifierTests
{
    /// <summary>实机观测到的路径。</summary>
    private const string PackagedPath =
        @"C:\Program Files\WindowsApps\OpenAI.Codex_26.917.9434.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";

    private const string DevelopmentPath =
        @"C:\Users\example\AppData\Local\OpenAI\Codex\bin\13995fba801849b0\codex.exe";

    [Fact]
    public void RecognizesPackagedDesktopExecutable()
    {
        Assert.True(CodexProcessIdentifier.IsCodexDesktop(PackagedPath));
    }

    [Fact]
    public void RecognitionIsCaseInsensitive()
    {
        var lower = PackagedPath.ToLowerInvariant();
        Assert.True(CodexProcessIdentifier.IsCodexDesktop(lower));
    }

    [Fact]
    public void RejectsCommandLineExecutableInSameDirectory()
    {
        // codex.exe 是命令行工具，不创建主窗口。
        var commandLine =
            @"C:\Program Files\WindowsApps\OpenAI.Codex_26.917.9434.0_x64__2p2nqsd0c76g0\app\codex.exe";

        Assert.False(CodexProcessIdentifier.IsCodexDesktop(commandLine));
    }

    [Fact]
    public void RejectsCodexCliInDevelopmentDirectory()
    {
        // 开发目录里的 codex.exe 同样是命令行工具。
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(DevelopmentPath));
    }

    [Fact]
    public void RejectsUnrelatedChatGptInstallation()
    {
        // 不能只看进程名：别的厂商也可能发布同名可执行文件。
        var unrelated = @"C:\Program Files\SomeVendor\ChatGPT\ChatGPT.exe";

        Assert.False(CodexProcessIdentifier.IsCodexDesktop(unrelated));
    }

    [Fact]
    public void RejectsChromiumBrowsers()
    {
        // Edge 与 VS Code 也是 Chrome_WidgetWin_1，必须排除。
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"));
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(
            @"C:\Users\example\AppData\Local\Programs\Microsoft VS Code\Code.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsFalseForMissingPath(string? path)
    {
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(path));
    }

    [Fact]
    public void RejectsWindowsAppsPathFromOtherVendor()
    {
        var otherVendor =
            @"C:\Program Files\WindowsApps\Contoso.App_1.0.0.0_x64__abc123\app\ChatGPT.exe";

        Assert.False(CodexProcessIdentifier.IsCodexDesktop(otherVendor));
    }

    [Fact]
    public void RejectsDevelopmentDirectoryWithoutCodexPackage()
    {
        var stray = @"C:\Users\example\AppData\Local\Other\ChatGPT.exe";

        Assert.False(CodexProcessIdentifier.IsCodexDesktop(stray));
    }
}
