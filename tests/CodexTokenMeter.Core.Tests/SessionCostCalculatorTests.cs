using CodexTokenMeter.Core.Codex;
using CodexTokenMeter.Core.Pricing;

namespace CodexTokenMeter.Core.Tests;

public class SessionCostCalculatorTests
{
    private static SessionSnapshot Snapshot(TokenUsage usage, bool hasTurn) => new()
    {
        ThreadId = "thread",
        LogPath = "rollout.jsonl",
        PrimaryModel = "gpt-6-sol",
        CurrentTurnId = "turn",
        Turns = hasTurn
            ? new Dictionary<string, TurnContext>
            {
                ["turn"] = new TurnContext { TurnId = "turn", Model = "gpt-6-sol" },
            }
            : new Dictionary<string, TurnContext>(),
        UsageRecords =
        [
            new UsageRecord { TurnId = "turn", Usage = usage },
        ],
    };

    [Fact]
    public void MissingTurnModelDoesNotGuessFromSessionMajority()
    {
        var snapshot = Snapshot(new TokenUsage { InputTokens = 100, TotalTokens = 100 }, hasTurn: false);

        var summary = SessionCostCalculator.Calculate(snapshot, PricingCatalog.CreateDefault());

        Assert.False(summary.IsComplete);
        Assert.Equal(0, summary.PricedCount);
        Assert.Equal(0, summary.TotalCost);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(100, 101)]
    [InlineData(long.MaxValue, 0)]
    public void InvalidUsageCannotProduceACompleteCost(long input, long cached)
    {
        var snapshot = Snapshot(new TokenUsage
        {
            InputTokens = input,
            CachedInputTokens = cached,
            TotalTokens = 100,
        }, hasTurn: true);

        var summary = SessionCostCalculator.Calculate(snapshot, PricingCatalog.CreateDefault());

        Assert.False(summary.IsComplete);
        Assert.Equal(1, summary.InvalidUsageCount);
        Assert.Equal(0, summary.PricedCount);
    }

    [Fact]
    public void ReasoningTokensCannotExceedOutputTokens()
    {
        var snapshot = Snapshot(new TokenUsage
        {
            InputTokens = 10,
            OutputTokens = 5,
            ReasoningOutputTokens = 6,
            TotalTokens = 15,
        }, hasTurn: true);

        var summary = SessionCostCalculator.Calculate(snapshot, PricingCatalog.CreateDefault());

        Assert.False(summary.IsComplete);
        Assert.Equal(1, summary.InvalidUsageCount);
    }
}
