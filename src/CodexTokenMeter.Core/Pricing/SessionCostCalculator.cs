using CodexTokenMeter.Core.Codex;

namespace CodexTokenMeter.Core.Pricing;

/// <summary>单次调用连同其模型与费用的记录。</summary>
public sealed record PricedUsage
{
    public required UsageRecord Record { get; init; }

    /// <summary>该次调用所属 turn 使用的模型。无法确定时为 null。</summary>
    public string? Model { get; init; }

    /// <summary>该模型的价格表。未收录时为 null。</summary>
    public ModelPricing? Pricing { get; init; }

    /// <summary>费用明细。未定价时为 null。</summary>
    public CostBreakdown? Cost { get; init; }

    /// <summary>该次调用是否已成功计价。</summary>
    public bool IsPriced => Cost is not null;
}

/// <summary>会话费用汇总。</summary>
public sealed record SessionCostSummary
{
    /// <summary>会话累计费用。</summary>
    public required double TotalCost { get; init; }

    /// <summary>最近一个 turn 的费用。</summary>
    public required double CurrentTurnCost { get; init; }

    /// <summary>已成功计价的调用数与总调用数。</summary>
    public required int PricedCount { get; init; }
    public required int TotalCount { get; init; }

    /// <summary>涉及但未收录价格的模型。</summary>
    public required IReadOnlyList<string> UnknownModels { get; init; }

    /// <summary>会话中是否出现过触发长上下文加价的调用。</summary>
    public bool LongContextSeen { get; init; }

    /// <summary>使用的分组倍率。</summary>
    public double RateMultiplier { get; init; } = 1.0;

    /// <summary>是否全部调用都已计价。false 表示合计值有遗漏。</summary>
    public bool IsComplete => PricedCount == TotalCount && UnknownModels.Count == 0;

    /// <summary>合计值是否为不完整估算。</summary>
    public bool IsPartial => !IsComplete;
}

/// <summary>
/// 把会话中的调用记录逐条计价。
/// </summary>
/// <remarks>
/// 模型按 **turn** 关联而不是按会话：同一个会话可能在不同 turn 使用不同模型
/// （例如评审轮次换用更便宜的模型），用会话主导模型会把费用算错。
/// </remarks>
public static class SessionCostCalculator
{
    /// <summary>计算会话费用。</summary>
    /// <param name="snapshot">会话快照。</param>
    /// <param name="catalog">价格表。</param>
    /// <param name="serviceTier">服务档位，取自日志。</param>
    /// <param name="rateMultiplier">分组倍率，默认 1.0。</param>
    public static SessionCostSummary Calculate(
        SessionSnapshot snapshot,
        PricingCatalog catalog,
        string? serviceTier = null,
        double rateMultiplier = 1.0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);

        var total = 0d;
        var currentTurnCost = 0d;
        var pricedCount = 0;
        var unknownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longContextSeen = false;

        var currentTurnId = snapshot.CurrentTurnId;

        foreach (var record in snapshot.UsageRecords)
        {
            var model = ResolveModel(snapshot, record);
            var pricing = catalog.Find(model);

            if (pricing is null)
            {
                if (!string.IsNullOrWhiteSpace(model))
                {
                    unknownModels.Add(model);
                }

                continue;
            }

            var cost = CostCalculator.Calculate(pricing, record.Usage, serviceTier, rateMultiplier);

            pricedCount++;
            total += cost.TotalCost;
            longContextSeen |= cost.LongContextApplied;

            if (currentTurnId is not null
                && string.Equals(record.TurnId, currentTurnId, StringComparison.Ordinal))
            {
                currentTurnCost += cost.TotalCost;
            }
        }

        return new SessionCostSummary
        {
            TotalCost = total,
            CurrentTurnCost = currentTurnCost,
            PricedCount = pricedCount,
            TotalCount = snapshot.UsageRecords.Count,
            UnknownModels = [.. unknownModels.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)],
            LongContextSeen = longContextSeen,
            RateMultiplier = rateMultiplier,
        };
    }

    /// <summary>
    /// 取该次调用所属 turn 的模型。
    /// </summary>
    /// <remarks>
    /// turn_context 先于该 turn 内的调用记录写出，因此按 turn id 查找即可。
    /// 若该 turn 没有上下文记录，回落到会话主导模型。
    /// </remarks>
    public static string? ResolveModel(SessionSnapshot snapshot, UsageRecord record)
    {
        if (snapshot.Turns.TryGetValue(record.TurnId, out var turn)
            && !string.IsNullOrWhiteSpace(turn.Model))
        {
            return turn.Model;
        }

        return snapshot.PrimaryModel;
    }
}
