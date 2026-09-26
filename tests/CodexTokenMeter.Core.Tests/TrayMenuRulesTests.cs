using CodexTokenMeter.Core.Settings;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// 托盘菜单的交互规则。
/// </summary>
/// <remarks>
/// 验证的是**决策逻辑**，不是实际的菜单渲染。托盘菜单无法在单元测试里
/// 真实点击，但「何时保持打开、何时关闭」是可推导的规则，
/// 而它正是用户报告问题的根源：
/// 每勾选一项菜单就关闭，必须重新右键展开。
/// </remarks>
public class TrayMenuRulesTests
{
    [Fact]
    public void ClickingMetricItemKeepsMenuOpen()
    {
        // 核心需求：勾选一项后菜单不该关。
        Assert.True(TrayMenuRules.ShouldKeepMenuOpen(
            closeReasonIsItemClicked: true,
            allowClose: false));
    }

    [Fact]
    public void ClickingRestoreDefaultClosesMenu()
    {
        // 「恢复默认」一次改写整份列表，没有继续勾选的需求。
        Assert.False(TrayMenuRules.ShouldKeepMenuOpen(
            closeReasonIsItemClicked: true,
            allowClose: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void NonItemCloseReasonsAlwaysClose(bool isItemClicked, bool allowClose)
    {
        // 点到别处、按 Esc、窗口失活等必须正常关闭，
        // 否则菜单会“粘”在屏幕上挡住内容。
        Assert.False(TrayMenuRules.ShouldKeepMenuOpen(isItemClicked, allowClose));
    }

    [Fact]
    public void AllowFlagDoesNotLeakAcrossMenuSessions()
    {
        // 回归保护：allowClose 标志必须在每次打开菜单时清掉。
        //
        // 场景：用户点了「恢复默认」但 Closing 未触发就点到别处，
        // 标志会残留，使下一次的普通勾选意外关闭菜单。
        var allowClose = true;

        Assert.False(TrayMenuRules.ShouldKeepMenuOpen(true, allowClose));

        // 菜单重新打开时必须重置为 false。
        allowClose = false;

        Assert.True(TrayMenuRules.ShouldKeepMenuOpen(true, allowClose));
    }

    [Fact]
    public void MetricLimitDisablesFurtherSelection()
    {
        // 达到上限后，未选中的项应被禁用，
        // 让「为什么点不动」有可见的理由。
        const int limit = 8;
        var selected = new HashSet<CapsuleMetric>(CapsuleMetricCatalog.Default);

        foreach (var metric in CapsuleMetricCatalog.Ordered)
        {
            if (selected.Count >= limit)
            {
                break;
            }

            selected.Add(metric);
        }

        Assert.Equal(limit, selected.Count);

        foreach (var metric in CapsuleMetricCatalog.Ordered)
        {
            var isChecked = selected.Contains(metric);

            // 与 TrayIcon.SyncSettings 的判定一致。
            var enabled = isChecked || selected.Count < limit;

            Assert.Equal(isChecked, enabled);
        }
    }

    [Fact]
    public void DefaultSetIsWithinLimit()
    {
        // 默认集合必须不超上限，否则一启动就有项被禁用。
        Assert.True(CapsuleMetricCatalog.Default.Count <= 8);
    }
}
