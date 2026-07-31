namespace PolymarketBot.Models;

public sealed class Position
{
    public required string ConditionId { get; init; }
    public required string Question { get; init; }
    public Side Side { get; init; }
    public required string TokenId { get; init; }
    public double EntryPrice { get; init; }
    public double SizeUsd { get; set; }
    public double Shares { get; set; }
    public double CurrentPrice { get; set; }
    public double UnrealizedPnl { get; set; }
    public string Category { get; init; } = "other";
    public string EventTitle { get; init; } = "";
    public double OpenedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public string? OrderId { get; init; }
    public double FairEstimateAtEntry { get; set; }  // Latest fair estimate for exit checks (0 = unknown/legacy)
    public double LiquidationLimitPrice { get; set; }
    public bool BookDepthComplete { get; set; } = true;
    public double QuoteAgeSeconds { get; set; }
    public double LastFreshPrice { get; set; }
    public int QuoteFailures { get; set; }
}
