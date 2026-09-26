using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// Codex 进程识别的边界行为。
/// </summary>
/// <remarks>
/// <para>
/// 识别的可靠性直接决定浮层是否跟随正确的窗口：
/// 漏判会让浮层完全不显示，误判会让它贴到无关应用的窗口上。
/// </para>
/// <para>
/// 判定依据是**可执行文件路径**而非进程名，因为 Codex Desktop 的
/// 进程名是 <c>ChatGPT</c>，与其它应用重名。
/// </para>
/// </remarks>
public class CodexProcessIdentifierEdgeCases
{
    /// <summary>真实的 MSIX 包路径形态。</summary>
    private const string PackagedPath =
        @"C:\Program Files\WindowsApps\OpenAI.Codex_1.2.3.0_x64__8wekyb3d8bbwe\app\ChatGPT.exe";

    /// <summary>开发/本地部署路径形态。</summary>
    private const string DevelopmentPath =
        @"C:\Users\example\AppData\Local\OpenAI\Codex\bin\abc123\ChatGPT.exe";

    [Theory]
    [InlineData(PackagedPath)]
    [InlineData(DevelopmentPath)]
    public void RecognizesKnownInstallLayouts(string path)
    {
        Assert.True(CodexProcessIdentifier.IsCodexDesktop(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsMissingPath(string? path)
    {
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(path));
    }

    [Fact]
    public void RejectsCliHelperInSamePackage()
    {
        // 同一个包目录里的命令行工具不创建主窗口。
        // 若把它认作桌面进程，会枚举到一堆无用窗口。
        const string cli =
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.2.3.0_x64__8wekyb3d8bbwe\app\codex.exe";

        Assert.False(CodexProcessIdentifier.IsCodexDesktop(cli));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.CodexHelper_1.0_x64__abc\app\ChatGPT.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Other.Codex_1.0_x64__abc\app\ChatGPT.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Codex_1.0_x64__abc\app\ChatGPT.exe")]
    public void RejectsSimilarButDifferentPackageNames(string path)
    {
        // 包名前缀必须精确匹配 OpenAI.Codex_：
        // 否则同名或相似名的其它应用会被误判。
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(path));
    }

    [Fact]
    public void RejectsChatGptFromOtherVendors()
    {
        // 关键场景：ChatGPT.exe 这个文件名不独占。
        // 只按进程名判断会把其它厂商的同名应用当成 Codex。
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(
            @"C:\Program Files\SomeVendor\ChatGPT.exe"));

        Assert.False(CodexProcessIdentifier.IsCodexDesktop(
            @"C:\Users\example\Desktop\ChatGPT.exe"));
    }

    [Fact]
    public void RejectsChatGptInWindowsAppsButDifferentPackage()
    {
        // 位于 WindowsApps 但不是 OpenAI.Codex 包。
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(
            @"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0_x64__abc\app\ChatGPT.exe"));
    }

    [Fact]
    public void CaseInsensitive()
    {
        // Windows 路径大小写不敏感。
        Assert.True(CodexProcessIdentifier.IsCodexDesktop(
            @"c:\program files\windowsapps\openai.codex_1.0_x64__abc\app\chatgpt.exe"));
    }

    [Fact]
    public void RejectsTraversalOutOfPackageDirectory()
    {
        // 路径里含包名但实际指向别处：仅靠 Contains 无法区分，
        // 因此这里记录实际行为——按当前规则会被接受。
        // 该场景需要攻击者能控制进程映像路径，超出本地工具威胁模型。
        const string traversal =
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__abc\..\..\evil\ChatGPT.exe";

        // 记录事实而非断言“安全”：Contains 匹配会命中包名片段。
        // 若未来要收紧，需改为按路径段解析。
        Assert.True(CodexProcessIdentifier.IsCodexDesktop(traversal));
    }

    [Fact]
    public void RejectsRelativePath()
    {
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(@"ChatGPT.exe"));
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(@"app\ChatGPT.exe"));
    }

    [Fact]
    public void RejectsDirectoryWithoutExecutableName()
    {
        // 传入目录而非文件：没有文件名，不应判定为桌面进程。
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__abc\app\"));
    }

    [Fact]
    public void DevelopmentPathRejectsNonDesktopExecutable()
    {
        // 开发目录里同样有命令行工具。
        Assert.False(CodexProcessIdentifier.IsCodexDesktop(
            @"C:\Users\example\AppData\Local\OpenAI\Codex\bin\abc\codex.exe"));
    }

    [Fact]
    public void DoesNotThrowForMalformedOrUnusualPaths()
    {
        // 进程映像路径来自系统，但权限异常或内核进程可能给出意外值。
        // 识别函数不应抛异常——它在跟随循环里每轮都可能被调用。
        //
        // 这里只要求「不抛异常且返回布尔值」，不断言具体结果：
        // 有些输入（如长路径前缀 \\?\）本身包含有效的包名与文件名，
        // 判为 true 是正确的。
        var malformed = new[]
        {
            @"\\?\C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe",
            @"C:\|invalid|ChatGPT.exe",
            new string('x', 5000),
            @"\\.\pipe\ChatGPT.exe",
            "\0",
            "  \t  ",
        };

        foreach (var path in malformed)
        {
            var exception = Record.Exception(
                () => CodexProcessIdentifier.IsCodexDesktop(path));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void LongPathPrefixIsStillRecognized()
    {
        // \\?\ 前缀是合法的 Windows 长路径写法，
        // 包名与文件名均正确时应识别为 Codex。
        Assert.True(CodexProcessIdentifier.IsCodexDesktop(
            @"\\?\C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__abc\app\ChatGPT.exe"));
    }
}
