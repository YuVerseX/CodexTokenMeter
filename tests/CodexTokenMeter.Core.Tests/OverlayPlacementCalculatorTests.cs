using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Tests;

public class OverlayPlacementCalculatorTests
{
    /// <summary>宿主窗口：屏幕坐标 (100,100)-(1100,700)。</summary>
    private static IntRect Host => new(100, 100, 1100, 700);

    [Fact]
    public void TopRightRespectsTopInsetToAvoidHostTitleBar()
    {
        // Codex 是自绘标题栏的全屏窗口，默认顶部内缩用于避开其按钮区域。
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.TopRight);

        Assert.Equal(Host.Top + OverlayPlacementCalculator.DefaultTopInset, placement.Bounds.Top);
    }

    [Fact]
    public void TopRightHonoursExplicitTopInset()
    {
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.TopRight,
            topInset: 64);

        Assert.Equal(Host.Top + 64, placement.Bounds.Top);
    }

    [Fact]
    public void TopRightWithZeroInsetSticksToHostTop()
    {
        // 0 是有效值（显式要求贴顶），不能被当作“未指定”。
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.TopRight,
            topInset: 0);

        Assert.Equal(Host.Top, placement.Bounds.Top);
    }

    [Fact]
    public void TopRightPlacesOverlayInsideUpperRightCorner()
    {
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.TopRight);

        Assert.Equal(Host.Right - 200 - OverlayPlacementCalculator.DefaultMargin, placement.Bounds.Left);
        Assert.Equal(200, placement.Bounds.Width);
        Assert.Equal(40, placement.Bounds.Height);
    }

    [Fact]
    public void BottomRightPlacesOverlayInsideLowerRightCorner()
    {
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.BottomRight);

        Assert.Equal(Host.Right - 200 - OverlayPlacementCalculator.DefaultMargin, placement.Bounds.Left);
        Assert.Equal(Host.Bottom - 40 - OverlayPlacementCalculator.DefaultMargin, placement.Bounds.Top);
    }

    [Fact]
    public void OverlayStaysInsideHostBounds()
    {
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.TopRight);

        Assert.True(placement.Bounds.Left >= Host.Left);
        Assert.True(placement.Bounds.Top >= Host.Top);
        Assert.True(placement.Bounds.Right <= Host.Right);
        Assert.True(placement.Bounds.Bottom <= Host.Bottom);
    }

    [Fact]
    public void NarrowHostClampsOverlayToHostLeftEdge()
    {
        // 宿主比浮层窄时，浮层贴左而不是越出窗口左侧。
        var narrow = new IntRect(100, 100, 250, 700);

        var placement = OverlayPlacementCalculator.Calculate(
            narrow,
            overlayWidth: 300,
            overlayHeight: 40);

        Assert.Equal(narrow.Left, placement.Bounds.Left);
        Assert.Equal(300, placement.Bounds.Width);
    }

    [Fact]
    public void TallOverlayIsAnchoredToHostTopWhenItCannotFit()
    {
        // 浮层比宿主还高时无法完整放入。此时贴顶，不产生负偏移；
        // 断言“完全在宿主内”在物理上不可能，因此只要求它从顶部开始。
        var shortHost = new IntRect(100, 100, 1100, 130);

        var placement = OverlayPlacementCalculator.Calculate(
            shortHost,
            overlayWidth: 200,
            overlayHeight: 40);

        Assert.Equal(shortHost.Top, placement.Bounds.Top);
    }

    [Fact]
    public void OverlayNarrowerThanHostIsClampedInsideShortHost()
    {
        // 宿主比浮层高但余量不足时，浮层向内收敛而不越出底边。
        var shortHost = new IntRect(100, 100, 1100, 200);

        var placement = OverlayPlacementCalculator.Calculate(
            shortHost,
            overlayWidth: 200,
            overlayHeight: 60);

        Assert.True(placement.Bounds.Bottom <= shortHost.Bottom);
        Assert.True(placement.Bounds.Top >= shortHost.Top);
    }

    [Fact]
    public void EmptyHostYieldsInvisiblePlacement()
    {
        var placement = OverlayPlacementCalculator.Calculate(
            IntRect.Empty,
            overlayWidth: 200,
            overlayHeight: 40);

        Assert.False(placement.IsVisible);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(200, 0)]
    [InlineData(-10, 40)]
    [InlineData(200, -10)]
    public void NonPositiveOverlaySizeYieldsInvisiblePlacement(int width, int height)
    {
        var placement = OverlayPlacementCalculator.Calculate(Host, width, height);

        Assert.False(placement.IsVisible);
    }

    [Fact]
    public void PlacementCarriesRequestedAnchor()
    {
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.BottomRight);

        Assert.Equal(OverlayAnchor.BottomRight, placement.Anchor);
    }

    [Fact]
    public void NegativeMarginFallsBackToDefault()
    {
        var placement = OverlayPlacementCalculator.Calculate(
            Host,
            overlayWidth: 200,
            overlayHeight: 40,
            OverlayAnchor.TopRight,
            margin: -5);

        Assert.Equal(Host.Right - 200 - OverlayPlacementCalculator.DefaultMargin, placement.Bounds.Left);
    }

    [Fact]
    public void NeedsRepositionIsTrueWithoutPreviousHost()
    {
        Assert.True(OverlayPlacementCalculator.NeedsReposition(Host, lastHost: null));
    }

    [Fact]
    public void NeedsRepositionIsFalseWhenHostUnchanged()
    {
        // 宿主未移动时不应重复设置窗口位置，否则会造成无谓的重绘。
        Assert.False(OverlayPlacementCalculator.NeedsReposition(Host, Host));
    }

    [Fact]
    public void NeedsRepositionIsTrueWhenHostMoved()
    {
        var moved = Host with { Left = Host.Left + 10, Right = Host.Right + 10 };

        Assert.True(OverlayPlacementCalculator.NeedsReposition(moved, Host));
    }

    [Fact]
    public void NeedsRepositionIsTrueWhenHostResized()
    {
        var resized = Host with { Right = Host.Right + 100 };

        Assert.True(OverlayPlacementCalculator.NeedsReposition(resized, Host));
    }

    [Fact]
    public void IntRectReportsDimensionsAndEmptiness()
    {
        Assert.Equal(1000, Host.Width);
        Assert.Equal(600, Host.Height);
        Assert.False(Host.IsEmpty);
        Assert.True(IntRect.Empty.IsEmpty);
        Assert.True(new IntRect(10, 10, 10, 50).IsEmpty);
    }
}
