using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class PaperTrader : ITrader
{
    public Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default)
    {
        var market = signal.Market;
        var price = signal.ExecutionPrice;
        var sizeUsd = signal.PositionSizeUsd;
        var shares = price > 0 ? sizeUsd / price : 0.0;
        var tokenId = signal.Side == Side.YES ? market.TokenIdYes : market.TokenIdNo;

        var position = new Position
        {
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            TokenId = tokenId,
            EntryPrice = price,
            SizeUsd = sizeUsd,
            Shares = shares,
            CurrentPrice = price,
            UnrealizedPnl = 0.0,
            Category = market.Category,
            FairEstimateAtEntry = signal.Estimate.FairProbability,
        };
        portfolio.OpenPosition(position);

        var trade = new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            Action = TradeAction.BUY,
            Price = price,
            SizeUsd = sizeUsd,
            Shares = shares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            IsPaper = true,
            Rationale = signal.Estimate.ReasoningSummary,
            EdgeAtEntry = signal.Edge,
            KellyAtEntry = signal.KellyFraction,
        };

        return Task.FromResult<Trade?>(trade);
    }

    public Task<Trade?> ExecuteSellAsync(ExitSignal exitSignal, Portfolio portfolio, CancellationToken ct = default)
    {
        var pos = exitSignal.Position;
        var pnl = portfolio.ClosePosition(pos.ConditionId, exitSignal.CurrentPrice);

        var trade = new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = pos.ConditionId,
            Question = pos.Question,
            Side = pos.Side,
            Action = TradeAction.SELL,
            Price = exitSignal.CurrentPrice,
            SizeUsd = pos.SizeUsd,
            Shares = pos.Shares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            IsPaper = true,
            Rationale = $"Exit: {exitSignal.ExitReason}",
            ExitReason = exitSignal.ExitReason,
        };

        return Task.FromResult<Trade?>(trade);
    }

    public Task<Trade?> ExecuteTopupAndSellAsync(TopupCandidate candidate, Portfolio portfolio, CancellationToken ct = default)
    {
        var pos = candidate.Position;
        var price = pos.CurrentPrice;

        // Step 1: simulate BUY 5 tokens
        portfolio.AddToPosition(pos.ConditionId, candidate.TokensToBuy, candidate.TopupCost);

        // Step 2: simulate SELL all tokens
        var exitSignal = new ExitSignal
        {
            Position = pos,
            ExitReason = candidate.ExitReason,
            CurrentPrice = price,
            UnrealizedPnl = pos.Shares * (price - pos.EntryPrice),
            PnlPct = pos.EntryPrice > 0 ? (price - pos.EntryPrice) / pos.EntryPrice : 0.0,
        };
        return ExecuteSellAsync(exitSignal, portfolio, ct);
    }
}
