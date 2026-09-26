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

    [Fact]
    public void SelectLargestDoesNotSpanProcesses()
    {
        // 用户可能开着多个 Codex 实例。
        // 跨进程直接取最大会跟随到另一个实例的窗口，必须避免。
        var candidates = new[]
        {
            // 进程 100：主窗口较小。
            Host(handle: 1, processId: 100, width: 800, height: 600),

            // 进程 200：另一个实例，窗口更大。
            Host(handle: 2, processId: 200, width: 1600, height: 1200),
            Host(handle: 3, processId: 200, width: 1500, height: 1100),
        };

        var selected = CodexWindowSelector.SelectLargest(candidates);

        Assert.NotNull(selected);
        Assert.Equal(200, selected.ProcessId);
        Assert.Equal(2, selected.Handle);
    }

    [Fact]
    public void SelectLargestIsStableForSameSizedWindows()
    {
        // 两个同尺寸窗口时，结果必须稳定：
        // 否则不同轮次可能选中不同窗口，浮层会无规律地跳换宿主。
        var forward = new[]
        {
            Host(handle: 10, processId: 100, width: 1000, height: 800),
            Host(handle: 20, processId: 100, width: 1000, height: 800),
        };

        var reversed = new[]
        {
            Host(handle: 20, processId: 100, width: 1000, height: 800),
            Host(handle: 10, processId: 100, width: 1000, height: 800),
        };

        var first = CodexWindowSelector.SelectLargest(forward);
        var second = CodexWindowSelector.SelectLargest(reversed);

        Assert.NotNull(first);
        Assert.NotNull(second);

        // 输入顺序不同，结果必须相同（按句柄升序取第一个）。
        Assert.Equal(first.Handle, second.Handle);
        Assert.Equal(10, first.Handle);
    }

    [Fact]
    public void SelectLargestIgnoresNonHostCandidates()
    {
        // 面积很大的工具窗口或分层合成窗口不应被选中。
        var tool = Host(
            handle: 1,
            processId: 100,
            extendedStyle: CodexWindowSelector.WsExToolWindow,
            width: 3000,
            height: 2000);

        var valid = Host(handle: 2, processId: 100, width: 900, height: 700);

        var selected = CodexWindowSelector.SelectLargest([tool, valid]);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Handle);
    }

    [Fact]
    public void SelectLargestReturnsNullWhenNoCandidateQualifies()
    {
        var tiny = Host(handle: 1, processId: 100, width: 100, height: 80);

        Assert.Null(CodexWindowSelector.SelectLargest([tiny]));
        Assert.Null(CodexWindowSelector.SelectLargest([]));
    }

    [Fact]
    public void SelectLargestSkipsMinimizedAndHiddenWindows()
    {
        var minimized = Host(
            handle: 1,
            processId: 100,
            isMinimized: true,
            width: 2000,
            height: 1500);

        var hidden = Host(
            handle: 2,
            processId: 100,
            isVisible: false,
            width: 2000,
            height: 1500);

        var valid = Host(handle: 3, processId: 100, width: 900, height: 700);

        var selected = CodexWindowSelector.SelectLargest([minimized, hidden, valid]);

        Assert.NotNull(selected);
        Assert.Equal(3, selected.Handle);
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
