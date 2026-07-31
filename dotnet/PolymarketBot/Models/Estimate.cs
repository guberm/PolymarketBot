namespace PolymarketBot.Models;

public sealed class Estimate
{
    public required string MarketConditionId { get; init; }
    public required string Question { get; init; }
    public double FairProbability { get; init; }
    public required List<double> RawEstimates { get; init; }
    public double Confidence { get; init; }
    public string ReasoningSummary { get; init; } = "";
    public double Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public int InputTokensUsed { get; init; }
    public int OutputTokensUsed { get; init; }
    public double ApiCostUsd { get; set; }
    public double DurationSeconds { get; set; }
    public Dictionary<string, double> ProviderEstimates { get; init; } = [];
}
