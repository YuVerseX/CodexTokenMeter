using CodexTokenMeter.Core.Settings;

namespace CodexTokenMeter.Core.Tests;

/// <summary>胶囊指标的分组与分隔线。</summary>
public class CapsuleMetricGrouperTests
{
    [Fact]
    public void EmptyInputProducesNoGroups()
    {
        Assert.Empty(CapsuleMetricGrouper.Group([]));
    }

    [Fact]
    public void SingleMetricHasNoDivider()
    {
        // 只有一项时分隔线没有意义，反而让胶囊看起来像是缺了内容。
        var groups = CapsuleMetricGrouper.Group([CapsuleMetric.TurnCost]);

        Assert.Single(groups);
        Assert.False(groups[0].PrecededByDivider);
        Assert.Equal([CapsuleMetric.TurnCost], groups[0].Items);
    }

    [Fact]
    public void MetricsInSameGroupStayTogether()
    {
        var groups = CapsuleMetricGrouper.Group(
            [CapsuleMetric.TurnInput, CapsuleMetric.TurnOutput, CapsuleMetric.TurnTotal]);

        Assert.Single(groups);
        Assert.Equal(
            [CapsuleMetric.TurnInput, CapsuleMetric.TurnOutput, CapsuleMetric.TurnTotal],
            groups[0].Items);
    }

    [Fact]
    public void DifferentGroupsGetDividersBetweenThem()
    {
        var groups = CapsuleMetricGrouper.Group(
            [CapsuleMetric.TurnInput, CapsuleMetric.TurnCost]);

        Assert.Equal(2, groups.Count);
        Assert.False(groups[0].PrecededByDivider);
        Assert.True(groups[1].PrecededByDivider);
    }

    [Fact]
    public void DefaultMetricsProduceFourGroups()
    {
        // 默认集合跨「本轮 / 累计 / 上下文 / 费用」四组。
        var groups = CapsuleMetricGrouper.Group([.. CapsuleMetricCatalog.Default]);

        Assert.Equal(4, groups.Count);

        // 首组之前不插分隔线，其余每组都插。
        Assert.False(groups[0].PrecededByDivider);
        Assert.All(groups.Skip(1), group => Assert.True(group.PrecededByDivider));
    }

    [Fact]
    public void DividerCountEqualsGroupCountMinusOne()
    {
        var groups = CapsuleMetricGrouper.Group([.. CapsuleMetricCatalog.Ordered]);

        var dividers = groups.Count(group => group.PrecededByDivider);

        Assert.Equal(groups.Count - 1, dividers);
    }

    [Fact]
    public void EveryMetricIsPresentExactlyOnce()
    {
        var groups = CapsuleMetricGrouper.Group([.. CapsuleMetricCatalog.Ordered]);
        var flattened = groups.SelectMany(group => group.Items).ToList();

        Assert.Equal(CapsuleMetricCatalog.Ordered, flattened);
    }

    [Fact]
    public void AdjacentGroupsAlwaysHaveDifferentGroupKinds()
    {
        // 两个相邻的分组若属于同一类，说明分组逻辑漏掉了合并。
        var groups = CapsuleMetricGrouper.Group([.. CapsuleMetricCatalog.Ordered]);

        var kinds = groups
            .Select(group => CapsuleMetricCatalog.GetGroup(group.Items[0]))
            .ToList();

        for (var index = 1; index < kinds.Count; index++)
        {
            Assert.NotEqual(kinds[index - 1], kinds[index]);
        }
    }

    [Fact]
    public void ProjectModelAndDurationShareOneGroup()
    {
        // 三者都是「会话标识」类，应落在同一组里。
        var groups = CapsuleMetricGrouper.Group(
            [CapsuleMetric.Project, CapsuleMetric.Model, CapsuleMetric.Duration]);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Items.Count);
    }

    [Fact]
    public void CostMetricsShareOneGroup()
    {
        var groups = CapsuleMetricGrouper.Group(
            [CapsuleMetric.TurnCost, CapsuleMetric.TotalCost]);

        Assert.Single(groups);
    }

    [Fact]
    public void AlternatingGroupsProduceMultipleRuns()
    {
        // 用户手改设置文件可能写出交错顺序（如 A组-B组-A组），
        // 此时必须产生三组而不是按去重后的两类合并。
        var groups = CapsuleMetricGrouper.Group(
        [
            CapsuleMetric.TurnCost,
            CapsuleMetric.ContextPercent,
            CapsuleMetric.TotalCost,
        ]);

        Assert.Equal(3, groups.Count);
        Assert.Equal([CapsuleMetric.TurnCost], groups[0].Items);
        Assert.Equal([CapsuleMetric.ContextPercent], groups[1].Items);
        Assert.Equal([CapsuleMetric.TotalCost], groups[2].Items);
    }
}
