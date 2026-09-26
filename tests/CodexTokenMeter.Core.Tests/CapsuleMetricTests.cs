using CodexTokenMeter.Core.Settings;

namespace CodexTokenMeter.Core.Tests;

/// <summary>胶囊指标目录。</summary>
public class CapsuleMetricTests
{
    [Fact]
    public void EveryMetricHasUniqueKey()
    {
        var keys = CapsuleMetricCatalog.Ordered.Select(CapsuleMetricCatalog.ToKey).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryMetricHasDisplayName()
    {
        foreach (var metric in CapsuleMetricCatalog.Ordered)
        {
            Assert.False(string.IsNullOrWhiteSpace(CapsuleMetricCatalog.GetDisplayName(metric)));
        }
    }

    [Fact]
    public void ToKeyRoundTripsForEveryMetric()
    {
        foreach (var metric in CapsuleMetricCatalog.Ordered)
        {
            Assert.True(CapsuleMetricCatalog.TryParse(CapsuleMetricCatalog.ToKey(metric), out var parsed));
            Assert.Equal(metric, parsed);
        }
    }

    [Fact]
    public void ParsingIsCaseInsensitive()
    {
        // 用户手工编辑设置文件时不该因大小写而静默失效。
        Assert.True(CapsuleMetricCatalog.TryParse("TURNCOST", out var upper));
        Assert.Equal(CapsuleMetric.TurnCost, upper);

        Assert.True(CapsuleMetricCatalog.TryParse("turncost", out var lower));
        Assert.Equal(CapsuleMetric.TurnCost, lower);
    }

    [Fact]
    public void ParsingTrimsWhitespace()
    {
        Assert.True(CapsuleMetricCatalog.TryParse("  turnCost  ", out var parsed));
        Assert.Equal(CapsuleMetric.TurnCost, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("不存在的指标")]
    [InlineData("turnCost2")]
    public void UnknownKeysAreRejected(string? key)
    {
        Assert.False(CapsuleMetricCatalog.TryParse(key, out _));
    }

    [Fact]
    public void DefaultIncludesTheFiveCommonMetrics()
    {
        var defaults = CapsuleMetricCatalog.Default;

        Assert.Contains(CapsuleMetric.TurnInput, defaults);
        Assert.Contains(CapsuleMetric.TurnOutput, defaults);
        Assert.Contains(CapsuleMetric.CachedInput, defaults);
        Assert.Contains(CapsuleMetric.TurnCost, defaults);
        Assert.Contains(CapsuleMetric.ContextPercent, defaults);
    }

    [Fact]
    public void SanitizeReturnsDefaultForNull()
    {
        Assert.Equal(CapsuleMetricCatalog.Default, CapsuleMetricCatalog.Sanitize(null));
    }

    [Fact]
    public void DefaultIsAlreadyInCanonicalOrder()
    {
        // 不变量：Sanitize 应保持默认集合不变。
        // 否则首次运行（未配置）与保存后（已配置）会得到两种排列，
        // 表现为胶囊自己重排一次。
        Assert.Equal(
            CapsuleMetricCatalog.Default,
            CapsuleMetricCatalog.Sanitize(CapsuleMetricCatalog.DefaultKeys));
    }

    [Fact]
    public void SanitizeFallsBackToDefaultWhenAllKeysUnknown()
    {
        // 配置文件来自更新的版本时可以含未知项；
        // 但如果一项都认不出来，空胶囊毫无意义，应回退默认。
        var result = CapsuleMetricCatalog.Sanitize(["完全未知", "也不认识"]);

        Assert.Equal(CapsuleMetricCatalog.Default, result);
    }

    [Fact]
    public void SanitizeIgnoresUnknownKeysButKeepsKnownOnes()
    {
        var result = CapsuleMetricCatalog.Sanitize(["turnCost", "未来指标", "totalCost"]);

        Assert.Equal(
            [CapsuleMetric.TurnCost, CapsuleMetric.TotalCost],
            result);
    }

    [Fact]
    public void SanitizeRemovesDuplicates()
    {
        var result = CapsuleMetricCatalog.Sanitize(["turnCost", "turnCost", "TURNCOST"]);

        Assert.Equal([CapsuleMetric.TurnCost], result);
    }

    [Fact]
    public void SanitizeReordersToCanonicalOrder()
    {
        // 规范顺序让胶囊的视觉节奏稳定，不随用户的勾选顺序变化。
        var result = CapsuleMetricCatalog.Sanitize(["totalCost", "turnInput", "contextPercent"]);

        Assert.Equal(
            [CapsuleMetric.TurnInput, CapsuleMetric.ContextPercent, CapsuleMetric.TotalCost],
            result);
    }

    [Fact]
    public void SanitizeFallsBackToDefaultForEmptyList()
    {
        Assert.Equal(CapsuleMetricCatalog.Default, CapsuleMetricCatalog.Sanitize([]));
    }

    [Fact]
    public void ToKeysRoundTripsThroughSanitize()
    {
        IReadOnlyList<CapsuleMetric> original =
            [CapsuleMetric.Model, CapsuleMetric.TurnCost, CapsuleMetric.ContextTokens];

        var restored = CapsuleMetricCatalog.Sanitize(CapsuleMetricCatalog.ToKeys(original));

        // 规范顺序按分组排列：上下文在费用之前，模型在最后。
        Assert.Equal(
            [CapsuleMetric.ContextTokens, CapsuleMetric.TurnCost, CapsuleMetric.Model],
            restored);
    }

    [Fact]
    public void GroupingIsConsistentWithOrdering()
    {
        // 分组决定分隔线位置。同一分组的指标在规范顺序中必须连续，
        // 否则会出现「A组-B组-A组」这样需要更多分隔线的排布。
        var seen = new HashSet<CapsuleMetricGroup>();
        CapsuleMetricGroup? previous = null;

        foreach (var metric in CapsuleMetricCatalog.Ordered)
        {
            var group = CapsuleMetricCatalog.GetGroup(metric);

            if (previous is { } prior && group != prior)
            {
                seen.Add(prior);
            }

            previous = group;
        }

        // 每个分组最多出现一次连续区间。
        var groups = CapsuleMetricCatalog.Ordered
            .Select(CapsuleMetricCatalog.GetGroup)
            .Distinct()
            .ToList();

        Assert.Equal(groups.Count, CapsuleMetricCatalog.Ordered
            .Select(CapsuleMetricCatalog.GetGroup)
            .Aggregate(
                seed: (Count: 0, Previous: (CapsuleMetricGroup?)null),
                func: (accumulator, current) =>
                {
                    var isNewRun = accumulator.Previous != current;
                    return (
                        accumulator.Count + (isNewRun ? 1 : 0),
                        current);
                })
            .Count);
    }

    [Fact]
    public void RestoringDefaultByIdTogglingEachWouldBeWrong()
    {
        // 回归保护：早期版本把「恢复默认」实现为逐个切换默认指标，
        // 而切换是“已选则取消”，因此已选中的项会被取消掉。
        //
        // 例：当前 [输入, 上下文]，默认含这两项，
        // 逐个切换后得到 [输出, 缓存读, 费用] —— 完全不是默认集合。
        var current = new List<CapsuleMetric>
        {
            CapsuleMetric.TurnInput,
            CapsuleMetric.ContextPercent,
        };

        foreach (var metric in CapsuleMetricCatalog.Default)
        {
            if (!current.Remove(metric))
            {
                current.Add(metric);
            }
        }

        Assert.NotEqual(
            CapsuleMetricCatalog.Default.ToHashSet(),
            current.ToHashSet());

        // 正确做法：直接替换为默认集合。
        var correct = CapsuleMetricCatalog.Sanitize(CapsuleMetricCatalog.DefaultKeys);

        Assert.Equal(CapsuleMetricCatalog.Default, correct);
    }

    [Fact]
    public void DefaultKeysResolveBackToDefault()
    {
        // App 层的「恢复默认」直接写 DefaultKeys，它必须能解析回默认集合。
        Assert.Equal(
            CapsuleMetricCatalog.Default,
            CapsuleMetricCatalog.Sanitize(CapsuleMetricCatalog.DefaultKeys));
    }

    [Fact]
    public void OrderCountMatchesEnumMemberCount()
    {
        // 新增枚举成员但忘记加入 Ordered 会让它在菜单里消失。
        var enumMembers = Enum.GetValues<CapsuleMetric>();

        Assert.Equal(enumMembers.Length, CapsuleMetricCatalog.Ordered.Count);
        Assert.Equal(enumMembers.ToHashSet(), CapsuleMetricCatalog.Ordered.ToHashSet());
    }
}

/// <summary>设置中的胶囊指标持久化。</summary>
public class CapsuleMetricSettingsTests
{
    [Fact]
    public void MissingListResolvesToDefault()
    {
        var settings = new OverlaySettings();

        Assert.Null(settings.CapsuleMetrics);
        Assert.Equal(CapsuleMetricCatalog.Default, settings.ResolvedCapsuleMetrics);
    }

    [Fact]
    public void NormalizeKeepsNullAsNull()
    {
        // null 表示“未配置”，与“用户主动清空”是不同的语义。
        var normalized = new OverlaySettings { CapsuleMetrics = null }.Normalize();

        Assert.Null(normalized.CapsuleMetrics);
    }

    [Fact]
    public void NormalizeRewritesKeysToCanonicalForm()
    {
        var normalized = new OverlaySettings
        {
            CapsuleMetrics = ["TURNCOST", "unknown", "turncost"],
        }.Normalize();

        Assert.Equal(["turnCost"], normalized.CapsuleMetrics);
    }

    [Fact]
    public void NormalizeMovesKeysIntoCanonicalOrder()
    {
        var normalized = new OverlaySettings
        {
            CapsuleMetrics = ["totalCost", "turnInput"],
        }.Normalize();

        Assert.Equal(["turnInput", "totalCost"], normalized.CapsuleMetrics);
    }

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctm-metrics-{Guid.NewGuid():N}.json");

        try
        {
            var original = new OverlaySettings
            {
                CapsuleMetrics = ["contextPercent", "totalCost", "cacheHitRate"],
            };

            Assert.True(SettingsStore.Save(original, path));

            // 保存时已重排为规范顺序（缓存 → 上下文 → 费用），
            // 因此读回的也应是这个顺序。
            var loaded = SettingsStore.Load(path);

            Assert.Equal(
                ["cacheHitRate", "contextPercent", "totalCost"],
                loaded.CapsuleMetrics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingsWithoutMetricsFieldUseDefault()
    {
        // 兼容旧版设置文件：没有该字段时应使用默认指标而不是空胶囊。
        var path = Path.Combine(Path.GetTempPath(), $"ctm-old-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "Anchor": 0, "Margin": 12 }""");

            var loaded = SettingsStore.Load(path);

            Assert.Null(loaded.CapsuleMetrics);
            Assert.Equal(CapsuleMetricCatalog.Default, loaded.ResolvedCapsuleMetrics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptedMetricsFieldFallsBackToDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctm-bad-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "CapsuleMetrics": ["不认识", "也不认识"] }""");

            var loaded = SettingsStore.Load(path);

            Assert.Equal(CapsuleMetricCatalog.Default, loaded.ResolvedCapsuleMetrics);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
