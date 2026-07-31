namespace PolymarketBot.Models;

public sealed class Signal
{
    public required MarketInfo Market { get; init; }
    public required Estimate Estimate { get; init; }
    public Side Side { get; init; }
    public double Edge { get; init; }
    public double MarketPrice { get; init; }
    public double ExecutionPrice { get; init; }
    public double KellyFraction { get; init; }
    public double PositionSizeUsd { get; init; }
    public double ExpectedValue { get; init; }
    public double LimitPrice { get; init; }
    public double QuoteAgeSeconds { get; init; }
}
