using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 浮层的拖动偏移量。
/// </summary>
/// <remarks>
/// 核心不变量：<see cref="OverlayPlacementCalculator.OffsetFromPosition"/>
/// 与 <see cref="OverlayPlacementCalculator.Calculate"/> 必须严格互逆。
/// 否则用户拖到某处松手后，浮层会弹到另一个位置——
/// 表现为「拖不动」或「拖完又跳回去」，而且极难排查。
/// </remarks>
public class OverlayPlacementOffsetTests
{
    /// <summary>不触边界的宿主窗口，避免夹取逻辑干扰互逆性验证。</summary>
    private static IntRect Host => new(0, 0, 1920, 1080);

    private const int OverlayWidth = 400;
    private const int OverlayHeight = 60;

    private static IEnumerable<OverlayAnchor> AllAnchors =>
        Enum.GetValues<OverlayAnchor>();

    [Theory]
    [MemberData(nameof(AnchorsAndOffsets))]
    public void OffsetRoundTripsThroughCalculate(OverlayAnchor anchor, int offsetX, int offsetY)
    {
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            OverlayWidth,
            OverlayHeight,
            anchor,
            margin: 12,
            topInset: 48,
            offsetX: offsetX,
            offsetY: offsetY);

        Assert.True(placement.IsVisible);

        var (recoveredX, recoveredY) = OverlayPlacementCalculator.OffsetFromPosition(
            Host,
            OverlayWidth,
            OverlayHeight,
            placement.Bounds.Left,
            placement.Bounds.Top,
            anchor,
            margin: 12,
            topInset: 48);

        // 夹取会把越界的偏移量收敛到边界，因此不能断言“恰好相等”。
        // 要验证的是**幂等**：用收敛后的偏移量再算一次，位置不再变化。
        // 这正是用户拖动后松手时的实际路径——
        // 若此处不幂等，浮层会在拖动后回弹。
        var reapplied = OverlayPlacementCalculator.Calculate(
            Host,
            OverlayWidth,
            OverlayHeight,
            anchor,
            margin: 12,
            topInset: 48,
            offsetX: recoveredX,
            offsetY: recoveredY);

        Assert.Equal(placement.Bounds.Left, reapplied.Bounds.Left);
        Assert.Equal(placement.Bounds.Top, reapplied.Bounds.Top);
    }

    [Theory]
    [MemberData(nameof(AnchorsAndOffsets))]
    public void OffsetsInsideHostRoundTripExactly(OverlayAnchor anchor, int offsetX, int offsetY)
    {
        // 不越界的偏移量必须精确往返：这是拖动换算式正确性的直接证据。
        var placement = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, anchor,
            margin: 12, topInset: 48, offsetX: offsetX, offsetY: offsetY);

        // 只在未被夹取时断言精确相等。
        var wasClamped = placement.Bounds.Left == Host.Left
            || placement.Bounds.Top == Host.Top
            || placement.Bounds.Right == Host.Right
            || placement.Bounds.Bottom == Host.Bottom;

        if (wasClamped)
        {
            return;
        }

        var (recoveredX, recoveredY) = OverlayPlacementCalculator.OffsetFromPosition(
            Host, OverlayWidth, OverlayHeight,
            placement.Bounds.Left, placement.Bounds.Top, anchor,
            margin: 12, topInset: 48);

        Assert.Equal(offsetX, recoveredX);
        Assert.Equal(offsetY, recoveredY);
    }

    [Theory]
    [MemberData(nameof(PositionsOnHost))]
    public void EveryOnHostPositionYieldsMatchingOffset(OverlayAnchor anchor, int left, int top)
    {
        // 反向路径：任意落点都能算出偏移量，且该偏移量能还原出同一点。
        var (offsetX, offsetY) = OverlayPlacementCalculator.OffsetFromPosition(
            Host,
            OverlayWidth,
            OverlayHeight,
            left,
            top,
            anchor,
            margin: 12,
            topInset: 48);

        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            OverlayWidth,
            OverlayHeight,
            anchor,
            margin: 12,
            topInset: 48,
            offsetX: offsetX,
            offsetY: offsetY);

        Assert.Equal(left, placement.Bounds.Left);
        Assert.Equal(top, placement.Bounds.Top);
    }

    public static TheoryData<OverlayAnchor, int, int> AnchorsAndOffsets()
    {
        var data = new TheoryData<OverlayAnchor, int, int>();

        foreach (var anchor in AllAnchors)
        {
            data.Add(anchor, 0, 0);
            data.Add(anchor, 50, 0);
            data.Add(anchor, 0, 40);
            data.Add(anchor, 120, 90);
            data.Add(anchor, -30, -20);
        }

        return data;
    }

    public static TheoryData<OverlayAnchor, int, int> PositionsOnHost()
    {
        var data = new TheoryData<OverlayAnchor, int, int>();

        // 在宿主内均匀取点，覆盖四个角落与中心附近。
        foreach (var anchor in AllAnchors)
        {
            data.Add(anchor, 0, 0);
            data.Add(anchor, 0, 500);
            data.Add(anchor, 760, 0);
            data.Add(anchor, 760, 500);
            data.Add(anchor, 400, 300);
            data.Add(anchor, 1400, 900);
        }

        return data;
    }

    [Fact]
    public void PositiveOffsetMovesInwardFromRightEdge()
    {
        // 贴右边缘时，正偏移应把浮层推离右边缘（向左）。
        var plain = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.TopRight);

        var shifted = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.TopRight, offsetX: 100);

        Assert.Equal(plain.Bounds.Left - 100, shifted.Bounds.Left);
    }

    [Fact]
    public void PositiveOffsetMovesInwardFromLeftEdge()
    {
        // 贴左边缘时，正偏移把浮层推离左边缘（向右）。
        // 两侧符号相反但语义一致，这是拖动换算能互逆的前提。
        var plain = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.TopLeft);

        var shifted = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.TopLeft, offsetX: 100);

        Assert.Equal(plain.Bounds.Left + 100, shifted.Bounds.Left);
    }

    [Fact]
    public void PositiveOffsetMovesDownwardFromTopEdge()
    {
        var plain = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.TopRight);

        var shifted = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.TopRight, offsetY: 80);

        Assert.Equal(plain.Bounds.Top + 80, shifted.Bounds.Top);
    }

    [Fact]
    public void PositiveOffsetMovesUpwardFromBottomEdge()
    {
        var plain = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.BottomRight);

        var shifted = OverlayPlacementCalculator.Calculate(
            Host, OverlayWidth, OverlayHeight, OverlayAnchor.BottomRight, offsetY: 80);

        Assert.Equal(plain.Bounds.Top - 80, shifted.Bounds.Top);
    }

    [Fact]
    public void AllFourAnchorsPlaceOverlayInsideHost()
    {
        foreach (var anchor in AllAnchors)
        {
            var placement = OverlayPlacementCalculator.Calculate(
                Host, OverlayWidth, OverlayHeight, anchor);

            Assert.True(
                placement.Bounds.Left >= Host.Left && placement.Bounds.Right <= Host.Right,
                $"{anchor}: 水平越界 {placement.Bounds}");
            Assert.True(
                placement.Bounds.Top >= Host.Top && placement.Bounds.Bottom <= Host.Bottom,
                $"{anchor}: 垂直越界 {placement.Bounds}");
        }
    }

    [Fact]
    public void OffsetsAreClampedToReasonableRange()
    {
        // 损坏或手误的设置文件可能写入极大偏移，
        // 那会把浮层放到屏幕之外，用户会以为程序坏了。
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            OverlayWidth,
            OverlayHeight,
            OverlayAnchor.TopLeft,
            offsetX: 999_999,
            offsetY: 999_999);

        // 夹取后仍在宿主内，不会跑到极远处。
        Assert.True(placement.Bounds.Left <= Host.Right);
        Assert.True(placement.Bounds.Top <= Host.Bottom);
    }

    [Fact]
    public void OverlayCannotBeDraggedOutsideHost()
    {
        // 拖出宿主会被收敛回边界内——浮层不能跑到看不见的地方，
        // 否则既没用也无法再抓到。
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            OverlayWidth,
            OverlayHeight,
            OverlayAnchor.TopLeft,
            offsetX: -5000,
            offsetY: -5000);

        Assert.Equal(Host.Left, placement.Bounds.Left);
        Assert.Equal(Host.Top, placement.Bounds.Top);
    }

    [Fact]
    public void OffsetIsStableAcrossHostMovement()
    {
        // 这是「存偏移量而非屏幕坐标」的核心价值：
        // 宿主移动后浮层相对位置不变。
        const int offsetX = 60;
        const int offsetY = 30;

        var before = OverlayPlacementCalculator.Calculate(
            new IntRect(0, 0, 1920, 1080), OverlayWidth, OverlayHeight,
            OverlayAnchor.TopRight, offsetX: offsetX, offsetY: offsetY);

        var after = OverlayPlacementCalculator.Calculate(
            new IntRect(300, 200, 2220, 1280), OverlayWidth, OverlayHeight,
            OverlayAnchor.TopRight, offsetX: offsetX, offsetY: offsetY);

        // 相对宿主左上角的位移应与宿主位移一致。
        Assert.Equal(before.Bounds.Left + 300, after.Bounds.Left);
        Assert.Equal(before.Bounds.Top + 200, after.Bounds.Top);
    }

    [Fact]
    public void OffsetSurvivesHostResize()
    {
        // 用户调整 Codex 窗口大小后，浮层应保持在相对同一角落的位置。
        const int offsetX = 40;

        var small = OverlayPlacementCalculator.Calculate(
            new IntRect(0, 0, 1280, 800), OverlayWidth, OverlayHeight,
            OverlayAnchor.TopRight, offsetX: offsetX);

        var large = OverlayPlacementCalculator.Calculate(
            new IntRect(0, 0, 2560, 1400), OverlayWidth, OverlayHeight,
            OverlayAnchor.TopRight, offsetX: offsetX);

        // 两者右边缘到宿主右边缘的距离应相同。
        Assert.Equal(
            1280 - small.Bounds.Right,
            2560 - large.Bounds.Right);
    }

    [Fact]
    public void OverflowingOverlayIsPinnedToHostOrigin()
    {
        // 宿主比浮层还小（极小化窗口）时贴左上角，
        // 而不是产生负偏移把浮层推到屏幕外。
        var tiny = new IntRect(500, 500, 620, 560);

        var placement = OverlayPlacementCalculator.Calculate(
            tiny,
            overlayWidth: 800,
            overlayHeight: 300,
            OverlayAnchor.BottomRight);

        Assert.Equal(tiny.Left, placement.Bounds.Left);
        Assert.Equal(tiny.Top, placement.Bounds.Top);
    }
}
