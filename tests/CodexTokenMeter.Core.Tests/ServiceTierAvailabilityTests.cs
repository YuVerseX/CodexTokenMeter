using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Pricing;

namespace CodexTokenMeter.Core.Tests;

/// <summary>
/// service tier 在日志中的位置与可关联性。
/// </summary>
/// <remarks>
/// <para>
/// 计费层完整支持 Fast 档（显式价与倍率互斥），但数据层目前不提供档位，
/// 即假设 standard。这不是遗漏，而是**没有可靠的数据来源**：
/// </para>
/// <list type="bullet">
/// <item>档位出现在 <c>event_msg/thread_settings_applied</c> 的
/// <c>thread_settings</c> 中，该事件不带 <c>turn_id</c>。</item>
/// <item>带 <c>turn_id</c> 的 <c>turn_context</c> 不含 <c>service_tier</c>。</item>
/// <item>实测事件顺序为 <c>turn_context</c> → <c>token_usage_record</c> →
/// … → <c>thread_settings_applied</c>，设置事件在部分调用之后才写出。</item>
/// </list>
/// <para>
/// 因此不能把「最近一次出现的值」当作调用所属的档位——
/// 那会给已发生的调用套用后续设置，而档位差异最大可达 2 倍价。
/// 这些测试把这个结论固化下来，避免后人“顺手”把它接上。
/// </para>
/// </remarks>
public class ServiceTierAvailabilityTests
{
    /// <summary>构造一行 thread_settings_applied（不带 turn_id）。</summary>
    private static string SettingsLine(string tier) =>
        "{\"timestamp\":\"2026-09-26T10:00:00Z\",\"ordinal\":10,\"type\":\"event_msg\","
        + "\"payload\":{\"type\":\"thread_settings_applied\",\"thread_id\":\"t\","
        + "\"thread_settings\":{\"model\":\"gpt-6-sol\",\"service_tier\":\"" + tier + "\"}}}";

    private static string TurnContextLine(string turnId) =>
        "{\"timestamp\":\"2026-09-26T09:59:59Z\",\"ordinal\":1,\"type\":\"turn_context\","
        + "\"payload\":{\"turn_id\":\"" + turnId + "\",\"model\":\"gpt-6-sol\",\"cwd\":\"C:\\\\w\"}}";

    private static string UsageLine(string turnId, long input, long output) =>
        "{\"timestamp\":\"2026-09-26T10:00:01Z\",\"ordinal\":20,\"type\":\"token_usage_record\","
        + "\"payload\":{\"turn_id\":\"" + turnId + "\",\"usage\":{"
        + "\"input_tokens\":" + input + ",\"cached_input_tokens\":0,"
        + "\"cache_write_input_tokens\":0,\"output_tokens\":" + output + ","
        + "\"reasoning_output_tokens\":0,\"total_tokens\":" + (input + output) + "}}}";

    private static SessionSnapshot Parse(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctm-tier-{Guid.NewGuid():N}.jsonl");

        try
        {
            File.WriteAllLines(path, lines);
            return SessionLogReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TurnContextDoesNotCarryServiceTier()
    {
        // 带 turn_id 的事件里没有档位字段。
        var snapshot = Parse(TurnContextLine("turn-a"));

        Assert.Single(snapshot.Turns);
        var turn = snapshot.Turns["turn-a"];

        // TurnContext 类型本身没有该属性，这里用它确实能提供的字段作对照，
        // 说明「档位不在其中」这一事实。
        Assert.Equal("gpt-6-sol", turn.Model);
    }

    [Fact]
    public void SettingsEventIsNotAttributedToAnyTurn()
    {
        // thread_settings_applied 不带 turn_id，因此解析器无法把它归属到 turn。
        var snapshot = Parse(
            TurnContextLine("turn-a"),
            SettingsLine("priority"),
            UsageLine("turn-a", 1000, 100));

        // 只有一个 turn（来自 turn_context），设置事件没有新增 turn。
        Assert.Single(snapshot.Turns);
        Assert.True(snapshot.Turns.ContainsKey("turn-a"));
    }

    [Fact]
    public void CostIsComputedAsStandardTierWhenNoTierIsSupplied()
    {
        // 当前行为：不传档位即按 standard 计费。
        //
        // 用量刻意低于长上下文阈值（272,000），否则会叠加 ×2 加价，
        // 掩盖掉档位本身的差异。
        var snapshot = Parse(
            TurnContextLine("turn-a"),
            UsageLine("turn-a", 100_000, 0));

        var catalog = PricingCatalog.CreateDefault();

        var withoutTier = SessionCostCalculator.Calculate(snapshot, catalog);

        // gpt-6-sol 输入价 2 USD / 1M，10 万 token → 0.2。
        Assert.Equal(0.2, withoutTier.TotalCost, precision: 6);

        // 若误把 priority 档套上去，会是显式 priority 价 4 USD / 1M（2 倍）。
        var withPriority = SessionCostCalculator.Calculate(
            snapshot, catalog, serviceTier: "priority");

        Assert.Equal(0.4, withPriority.TotalCost, precision: 6);
        Assert.Equal(2.0, withPriority.TotalCost / withoutTier.TotalCost, precision: 6);
    }

    [Fact]
    public void LongContextMultiplierStacksOnTopOfTier()
    {
        // 档位与长上下文是两个独立维度，会相乘。
        // 这也说明测试数据必须控制用量，否则两种效应会混在一起。
        var snapshot = Parse(
            TurnContextLine("turn-a"),
            UsageLine("turn-a", 300_000, 0));

        var catalog = PricingCatalog.CreateDefault();

        var standard = SessionCostCalculator.Calculate(snapshot, catalog);

        // 基础价 2 × 长上下文 2 = 4 USD / 1M，30 万 → 1.2。
        Assert.Equal(1.2, standard.TotalCost, precision: 6);
        Assert.True(standard.LongContextSeen);
    }

    [Fact]
    public void TierAffectsCacheReadAndWriteToo()
    {
        // 档位差异不止作用于输入与输出，缓存读也按档位价算。
        // 因此猜错档位的影响范围比直觉更大。
        //
        // 用量低于长上下文阈值，避免叠加加价。
        var snapshot = Parse(
            TurnContextLine("turn-a"),
            """
            {"timestamp":"2026-09-26T10:00:01Z","type":"token_usage_record","payload":{"turn_id":"turn-a","usage":{"input_tokens":100000,"cached_input_tokens":100000,"cache_write_input_tokens":0,"output_tokens":0,"reasoning_output_tokens":0,"total_tokens":100000}}}
            """);

        var catalog = PricingCatalog.CreateDefault();

        var standard = SessionCostCalculator.Calculate(snapshot, catalog);
        var priority = SessionCostCalculator.Calculate(snapshot, catalog, serviceTier: "priority");

        // 全部命中缓存：按 cache read 价计费。
        // standard = 0.2 USD/1M × 0.1M = 0.02；priority 显式价 0.4 → 0.04。
        Assert.Equal(0.02, standard.TotalCost, precision: 6);
        Assert.Equal(0.04, priority.TotalCost, precision: 6);
    }

    [Fact]
    public void FlexTierUsesHalfMultiplier()
    {
        // flex 档的行为与 priority 不同：走倍率而不是显式价。
        var snapshot = Parse(
            TurnContextLine("turn-a"),
            UsageLine("turn-a", 100_000, 0));

        var catalog = PricingCatalog.CreateDefault();

        var flex = SessionCostCalculator.Calculate(snapshot, catalog, serviceTier: "flex");

        // 基础价 2 × 0.5 = 1 USD / 1M，10 万 token → 0.1。
        Assert.Equal(0.1, flex.TotalCost, precision: 6);
    }
}
