namespace PolymarketBot.Models;

/// <summary>Tiny position (&lt;5 tokens) that wants to exit but needs a top-up BUY first.</summary>
public sealed class TopupCandidate
{
    public required Position Position { get; init; }
    public required string ExitReason { get; init; }
    public double TokensToBuy { get; init; }    // 5.0 (CLOB minimum for BUY order)
    public double TopupCost { get; set; }
    public double RecoveryValue { get; set; }
    public double BuyVwap { get; set; }
    public double BuyLimitPrice { get; set; }
    public double SellVwap { get; set; }
    public double SellLimitPrice { get; set; }
}
