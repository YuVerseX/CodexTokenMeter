using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 主窗口识别规则的验证。
/// 每条规则都对应一种真实存在的干扰窗口。
/// </summary>
public class CodexWindowSelectorTests
{
    private const int CodexProcess = 1000;
    private const int OtherProcess = 2000;

    [Fact]
    public void MissingForegroundWindowYieldsNoSelection()
    {
        var candidates = new[] { Host(handle: 1, processId: CodexProcess) };

        Assert.Null(CodexWindowSelector.Select(candidates, foregroundHandle: 99));
    }

    [Fact]
    public void ForegroundFromOtherProcessYieldsNoSelection()
    {
        // 前台不是 Codex 时浮层应隐藏，而不是贴到别人窗口上。
        var candidates = new[] { Host(handle: 1, processId: OtherProcess, isCodexProcess: false) };

        Assert.Null(CodexWindowSelector.Select(candidates, foregroundHandle: 1));
    }

    [Fact]
    public void HiddenForegroundYieldsNoSelection()
    {
        var candidates = new[] { Host(handle: 1, processId: CodexProcess, isVisible: false) };

        Assert.Null(CodexWindowSelector.Select(candidates, foregroundHandle: 1));
    }

    [Fact]
    public void SelectsLargestEligibleWindowInSameProcess()
    {
        var candidates = new[]
        {
            Host(handle: 1, processId: CodexProcess, width: 800, height: 600),
            Host(handle: 2, processId: CodexProcess, width: 1600, height: 1000),
            Host(handle: 3, processId: CodexProcess, width: 900, height: 700),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void IgnoresWindowsFromOtherProcesses()
    {
        // 多开的 Codex 实例不应互相影响。
        var candidates = new[]
        {
            Host(handle: 1, processId: CodexProcess, width: 800, height: 600),
            Host(handle: 2, processId: OtherProcess, width: 4000, height: 3000, isCodexProcess: false),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(1, selected!.Handle);
    }

    [Fact]
    public void IgnoresMinimizedWindows()
    {
        var candidates = new[]
        {
            Host(handle: 1, processId: CodexProcess, width: 1600, height: 1000, isMinimized: true),
            Host(handle: 2, processId: CodexProcess, width: 800, height: 600),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void IgnoresOwnedWindows()
    {
        // 带 owner 的窗口是对话框或附属窗口，不是主窗口。
        var candidates = new[]
        {
            Host(handle: 1, processId: CodexProcess, width: 1600, height: 1000, ownerHandle: 42),
            Host(handle: 2, processId: CodexProcess, width: 800, height: 600),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void IgnoresNonChromiumWindowClasses()
    {
        var candidates = new[]
        {
            Host(handle: 1, processId: CodexProcess, width: 1600, height: 1000, className: "SomeOtherClass"),
            Host(handle: 2, processId: CodexProcess, width: 800, height: 600),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void IgnoresToolWindows()
    {
        // WS_EX_TOOLWINDOW 窗口如托盘提示、浮动工具条。
        var candidates = new[]
        {
            Host(
                handle: 1,
                processId: CodexProcess,
                width: 1600,
                height: 1000,
                extendedStyle: CodexWindowSelector.WsExToolWindow),
            Host(handle: 2, processId: CodexProcess, width: 800, height: 600),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void IgnoresLayeredWindows()
    {
        // Chromium 用分层窗口做阴影与合成，它们不是主窗口。
        var candidates = new[]
        {
            Host(
                handle: 1,
                processId: CodexProcess,
                width: 1600,
                height: 1000,
                extendedStyle: CodexWindowSelector.WsExLayered),
            Host(handle: 2, processId: CodexProcess, width: 800, height: 600),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(2, selected!.Handle);
    }

    [Fact]
    public void MinimizedWindowIsNeverSelectedEvenWhenItIsTheOnlyOne()
    {
        // 实机踩到：Codex 主窗口最小化后坐标为 -32000，
        // 若把它当作宿主，浮层会被定位到屏幕外。
        var candidates = new[]
        {
            Host(
                handle: 1,
                processId: CodexProcess,
                isMinimized: true,
                left: -32000,
                top: -32000,
                width: 1200,
                height: 800),
        };

        Assert.Null(CodexWindowSelector.Select(candidates, foregroundHandle: 1));
    }

    [Fact]
    public void IgnoresWindowsSmallerThanMinimum()
    {
        var candidates = new[]
        {
            Host(handle: 1, processId: CodexProcess, width: 499, height: 1000),
            Host(handle: 2, processId: CodexProcess, width: 1000, height: 399),
            Host(handle: 3, processId: CodexProcess, width: 800, height: 600),
        };

        var selected = CodexWindowSelector.Select(candidates, foregroundHandle: 1);

        Assert.Equal(3, selected!.Handle);
    }

    [Fact]
    public void ReturnsNullWhenNoEligibleWindowExists()
    {
        // 只有隐藏窗口时应返回 null，让浮层隐藏而不是错贴。
        var candidates = new[]
        {
            Host(handle: 1, processId: CodexProcess, isVisible: false),
            Host(handle: 2, processId: CodexProcess, width: 100, height: 100),
        };

        Assert.Null(CodexWindowSelector.Select(candidates, foregroundHandle: 1));
    }

    [Fact]
    public void SelectionIsStableForEqualAreaWindows()
    {
        // 面积相同时用句柄作次键，避免同一批候选在不同次调用选出不同窗口。
        var candidates = new[]
        {
            Host(handle: 7, processId: CodexProcess, width: 800, height: 600),
            Host(handle: 3, processId: CodexProcess, width: 800, height: 600),
            Host(handle: 5, processId: CodexProcess, width: 800, height: 600),
        };

        var first = CodexWindowSelector.Select(candidates, foregroundHandle: 7);
        var second = CodexWindowSelector.Select(candidates, foregroundHandle: 5);

        Assert.Equal(first!.Handle, second!.Handle);
        Assert.Equal(3, first.Handle);
    }

    [Fact]
    public void IsHostCandidateAcceptsWellFormedHost()
    {
        Assert.True(CodexWindowSelector.IsHostCandidate(Host(handle: 1, processId: CodexProcess)));
    }

    private static WindowCandidate Host(
        nint handle,
        int processId,
        bool isCodexProcess = true,
        bool isVisible = true,
        bool isMinimized = false,
        nint ownerHandle = 0,
        long extendedStyle = 0,
        int width = 1200,
        int height = 800,
        int left = 100,
        int top = 100,
        string className = CodexWindowSelector.ChromiumTopLevelClass) => new()
        {
            Handle = handle,
            ProcessId = processId,
            IsCodexProcess = isCodexProcess,
            IsVisible = isVisible,
            IsMinimized = isMinimized,
            OwnerHandle = ownerHandle,
            ExtendedStyle = extendedStyle,
            Bounds = new IntRect(left, top, left + width, top + height),
            ClassName = className,
        };
}
