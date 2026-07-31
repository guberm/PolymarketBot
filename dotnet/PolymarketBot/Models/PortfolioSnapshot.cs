namespace PolymarketBot.Models;

public sealed class PortfolioSnapshot
{
    public double Bankroll { get; init; }
    public double InitialBankroll { get; init; }
    public required List<Position> Positions { get; init; }
    public double HighWaterMark { get; init; }
    public double DailyStartValue { get; init; }
    public double TotalRealizedPnl { get; init; }
    public int TotalTrades { get; init; }
    public bool IsHalted { get; init; }
    public double LastUpdated { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public double TotalApiCost { get; init; }
    public double DailyApiCost { get; init; }
    public string DailyTrackingDate { get; init; } = "";
    public List<string> AppliedOrderIds { get; init; } = [];
}
