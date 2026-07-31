namespace PolymarketBot.Models;

public sealed class Trade
{
    public required string TradeId { get; init; }
    public required string ConditionId { get; init; }
    public required string Question { get; init; }
    public Side Side { get; init; }
    public TradeAction Action { get; init; }
    public double Price { get; init; }
    public double SizeUsd { get; init; }
    public double Shares { get; init; }
    public double Timestamp { get; init; }
    public string? OrderId { get; init; }
    public bool IsPaper { get; init; } = true;
    public string Rationale { get; init; } = "";
    public double EdgeAtEntry { get; init; }
    public double KellyAtEntry { get; init; }
    public string ExitReason { get; init; } = "";
    public double QuotedVwap { get; init; }
    public double SlippageBps { get; init; }
    public string FillStatus { get; init; } = "";
}
