using CodexTokenMeter.Core.Windowing;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 物理像素与设备无关单位（DIP）之间的换算。
/// </summary>
/// <remarks>
/// 实机踩到：150% 缩放下把物理像素坐标当作 DIP 使用，
/// 浮层被画到物理 x=5106，而屏幕只有 3840 宽，用户完全看不到。
/// 这里锁定换算规则，防止回归。
/// </remarks>
public class DevicePixelConversionTests
{
    [Theory]
    [InlineData(3404, 1.5, 2269.33)]
    [InlineData(3840, 1.5, 2560)]
    [InlineData(1920, 1.0, 1920)]
    [InlineData(1920, 1.25, 1536)]
    [InlineData(1920, 2.0, 960)]
    public void PhysicalToDipDividesByScale(double physical, double scale, double expected)
    {
        Assert.Equal(expected, physical / scale, 1);
    }

    /// <summary>
    /// 完整的定位流程：物理宿主矩形 -> DIP -> 计算位置。
    /// </summary>
    [Fact]
    public void PlacementInDipKeepsOverlayInsideHostAt150Percent()
    {
        // 实机的 150% 缩放场景。
        const double scale = 1.5;

        // 宿主的物理矩形。
        var hostPhysical = new IntRect(0, 0, 3840, 2100);

        // 换算为 DIP。
        var hostDip = new IntRect(
            (int)Math.Floor(hostPhysical.Left / scale),
            (int)Math.Floor(hostPhysical.Top / scale),
            (int)Math.Ceiling(hostPhysical.Right / scale),
            (int)Math.Ceiling(hostPhysical.Bottom / scale));

        Assert.Equal(2560, hostDip.Right);
        Assert.Equal(1400, hostDip.Bottom);

        // 浮层的 DIP 尺寸（胶囊态实测值）。
        var placement = OverlayPlacementCalculator.Calculate(hostDip, 424, 69);

        Assert.True(placement.IsVisible);

        // 位置在 DIP 空间内应贴住宿主右边缘。
        Assert.Equal(hostDip.Right - 424 - OverlayPlacementCalculator.DefaultMargin, placement.Bounds.Left);
        Assert.True(placement.Bounds.Right <= hostDip.Right);

        // 换算回物理像素后必须落在屏幕内。
        var physicalLeft = placement.Bounds.Left * scale;
        var physicalRight = placement.Bounds.Right * scale;

        Assert.True(physicalLeft >= 0, $"物理左边界 {physicalLeft} 超出屏幕");
        Assert.True(physicalRight <= 3840, $"物理右边界 {physicalRight} 超出 3840 宽的屏幕");
    }

    /// <summary>
    /// 不换算会越界——这正是修复前的缺陷。
    /// </summary>
    [Fact]
    public void PlacementInPhysicalUnitsWouldGoOffScreen()
    {
        // 修复前的错误做法：把物理宿主矩形直接当 DIP 用。
        var hostPhysical = new IntRect(0, 0, 3840, 2100);
        var wrong = OverlayPlacementCalculator.Calculate(hostPhysical, 424, 69);

        // DIP 位置。
        var wrongDipLeft = wrong.Bounds.Left;

        // 按 150% 缩放后为物理坐标。
        var wrongPhysicalLeft = wrongDipLeft * 1.5;

        // 这个值远超屏幕宽度，说明确实会画到屏幕之外。
        Assert.True(
            wrongPhysicalLeft > 3840,
            $"预期越界，实际物理左边界 {wrongPhysicalLeft}");
    }

    [Fact]
    public void RoundTripConversionIsStable()
    {
        // 物理 -> DIP -> 物理 的往返不应产生明显漂移。
        const double scale = 1.5;
        var physical = new IntRect(100, 200, 3400, 2100);

        var dip = new IntRect(
            (int)Math.Floor(physical.Left / scale),
            (int)Math.Floor(physical.Top / scale),
            (int)Math.Ceiling(physical.Right / scale),
            (int)Math.Ceiling(physical.Bottom / scale));

        var backLeft = dip.Left * scale;
        var backRight = dip.Right * scale;

        // 因取整导致的偏差不应超过一个缩放单位。
        Assert.True(Math.Abs(backLeft - physical.Left) <= scale);
        Assert.True(Math.Abs(backRight - physical.Right) <= scale);
    }
}
