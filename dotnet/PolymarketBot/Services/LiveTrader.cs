using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

/// <summary>
/// Live execution via Polymarket CLOB API with proper EIP-712 + HMAC auth.
/// </summary>
public sealed class LiveTrader : ITrader
{
    private readonly ClobApiClient _clob;
    private readonly ILogger<LiveTrader> _log;
    private readonly OrderJournal _journal;

    public LiveTrader(ClobApiClient clob, ILogger<LiveTrader> log, string dataDir)
    {
        _clob = clob;
        _log = log;
        _journal = new OrderJournal(dataDir);
        _log.LogInformation("Live CLOB trader initialized");
    }

    public void ConfirmAppliedOrders(Portfolio portfolio)
    {
        foreach (var record in _journal.Pending())
            if (portfolio.HasAppliedOrder(record.OrderId))
                _journal.Complete(record.IntentId);
    }

    public async Task<bool> RecoverPendingOrdersAsync(
        Portfolio portfolio, string dataDir, CancellationToken ct = default)
    {
        foreach (var record in _journal.Pending())
        {
            if (!string.IsNullOrEmpty(record.OrderId) && portfolio.HasAppliedOrder(record.OrderId))
            {
                _journal.Complete(record.IntentId);
                continue;
            }
            if (string.IsNullOrEmpty(record.OrderId))
            {
                _log.LogCritical(
                    "Uncertain live order intent {Intent}: POST outcome unknown; refusing live trading until pending-orders.json is reconciled manually",
                    record.IntentId);
                return false;
            }

            OrderFill? fill = record.FillShares > 0
                ? new(record.FillStatus, record.FillShares, record.FillValue, record.FillPrice)
                : null;
            if (fill is null)
            {
                var orderSide = record.Kind.Contains("BUY", StringComparison.Ordinal) ? "BUY" : "SELL";
                var current = await _clob.GetOrderFillAsync(record.OrderId, orderSide, record.LimitPrice, ct);
                if (current is null)
                {
                    _log.LogError("Cannot reconcile pending order {OrderId}", record.OrderId);
                    return false;
                }
                if (current.Value.Status is not ("MATCHED" or "CANCELLED"))
                {
                    await _clob.CancelOrderAsync(record.OrderId, ct);
                    var final = await _clob.GetOrderFillAsync(record.OrderId, orderSide, record.LimitPrice, ct);
                    if (final is not null && final.Value.Shares >= current.Value.Shares) current = final;
                }
                if (current.Value.Shares <= 1e-9)
                {
                    _journal.Complete(record.IntentId);
                    continue;
                }
                fill = current.Value.Status == "MATCHED" ? current : current.Value with { Status = "PARTIAL" };
                _journal.Filled(record.IntentId, fill.Value);
            }

            var trade = ApplyRecoveredOrder(record, fill.Value, portfolio);
            if (trade is null) return false;
            PersistenceService.SaveSnapshot(portfolio.Snapshot(), dataDir);
            PersistenceService.AppendTrade(trade, dataDir);
            _journal.Complete(record.IntentId);
            _log.LogWarning("Recovered pending {Kind} order {OrderId} ({Shares:F4} shares)",
                record.Kind, record.OrderId, fill.Value.Shares);
        }
        return true;
    }

    private Trade? ApplyRecoveredOrder(PendingOrderRecord record, OrderFill fill, Portfolio portfolio)
    {
        if (!Enum.TryParse<Side>(record.Side, true, out var side)) side = Side.YES;
        if (record.Kind == "BUY")
        {
            if (portfolio.HasPosition(record.ConditionId))
            {
                _log.LogCritical("Pending BUY {OrderId} conflicts with an existing position", record.OrderId);
                return null;
            }
            portfolio.OpenPosition(new Position
            {
                ConditionId = record.ConditionId, Question = string.IsNullOrEmpty(record.Question) ? "Recovered order" : record.Question,
                Side = side, TokenId = record.TokenId, EntryPrice = fill.Price, SizeUsd = fill.Value,
                Shares = fill.Shares, CurrentPrice = fill.Price, UnrealizedPnl = 0, Category = record.Category,
                EventTitle = record.EventTitle,
                OrderId = record.OrderId, FairEstimateAtEntry = record.FairEstimate,
                LiquidationLimitPrice = record.LimitPrice,
            });
        }
        else if (record.Kind == "TOPUP_BUY")
        {
            if (!portfolio.HasPosition(record.ConditionId))
            {
                _log.LogCritical("Pending TOPUP BUY {OrderId} has no position to update", record.OrderId);
                return null;
            }
            portfolio.AddToPosition(record.ConditionId, fill.Shares, fill.Value);
        }
        else if (record.Kind is "SELL" or "TOPUP_SELL")
        {
            if (!portfolio.HasPosition(record.ConditionId))
            {
                _log.LogCritical("Pending SELL {OrderId} has no position to reduce", record.OrderId);
                return null;
            }
            portfolio.ReducePosition(record.ConditionId, fill.Shares, fill.Price);
        }
        else
        {
            _log.LogCritical("Unknown pending order kind: {Kind}", record.Kind);
            return null;
        }

        portfolio.MarkOrderApplied(record.OrderId);
        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(), ConditionId = record.ConditionId,
            Question = string.IsNullOrEmpty(record.Question) ? "Recovered order" : record.Question,
            Side = side, Action = record.Kind.Contains("BUY", StringComparison.Ordinal) ? TradeAction.BUY : TradeAction.SELL,
            Price = fill.Price, SizeUsd = fill.Value, Shares = fill.Shares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = record.OrderId, IsPaper = false, Rationale = $"Recovered after restart: {record.Kind}",
            ExitReason = record.ExitReason, QuotedVwap = record.QuotedVwap, FillStatus = fill.Status,
        };
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _clob.InitializeAsync(ct);
    }

    /// <summary>
    /// Fetch actual USDC balance from CLOB API.
    /// </summary>
    public async Task<double?> GetBalanceAsync(CancellationToken ct = default)
    {
        return await _clob.GetBalanceAsync(ct);
    }

    private async Task<OrderFill?> ReconcileAsync(
        ClobApiClient.OrderResult initial, string side, double limitPrice,
        int attempts, int delayMs, CancellationToken ct)
    {
        var best = initial.IsMatched
            ? new OrderFill("MATCHED", initial.ActualShares, initial.ActualCostUsd,
                initial.ActualShares > 0 ? initial.ActualCostUsd / initial.ActualShares : limitPrice)
            : new OrderFill("", 0, 0, limitPrice);
        var matched = initial.IsMatched;
        for (var attempt = 0; !matched && attempt < attempts; attempt++)
        {
            await Task.Delay(delayMs, ct);
            var fill = await _clob.GetOrderFillAsync(initial.OrderId, side, limitPrice, ct);
            if (fill is null) break;
            if (fill.Value.Shares >= best.Shares) best = fill.Value;
            _log.LogInformation("{Side} order poll {Attempt}: status={Status}, filled={Shares:F4}",
                side, attempt + 1, fill.Value.Status, fill.Value.Shares);
            matched = fill.Value.Status == "MATCHED";
            if (fill.Value.Status is "CANCELLED" or "DELAYED") break;
        }

        if (!matched)
        {
            await _clob.CancelOrderAsync(initial.OrderId, ct);
            var final = await _clob.GetOrderFillAsync(initial.OrderId, side, limitPrice, ct);
            if (final is not null && final.Value.Shares >= best.Shares) best = final.Value;
        }
        if (best.Shares <= 1e-9) return null;
        return best.Status == "MATCHED" ? best : best with { Status = "PARTIAL" };
    }

    public async Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default)
    {
        var market = signal.Market;
        var price = signal.LimitPrice > 0 ? signal.LimitPrice : signal.ExecutionPrice;
        var sizeUsd = signal.PositionSizeUsd;
        var tokenId = signal.Side == Side.YES ? market.TokenIdYes : market.TokenIdNo;
        var intentId = _journal.Begin(new PendingOrderRecord
        {
            Kind = "BUY", ConditionId = market.ConditionId, Question = market.Question,
            Side = signal.Side.ToString(), TokenId = tokenId, Requested = sizeUsd,
            LimitPrice = price, Category = market.Category, EventTitle = market.EventTitle,
            FairEstimate = signal.Estimate.FairProbability,
            Edge = signal.Edge, Kelly = signal.KellyFraction, QuotedVwap = signal.ExecutionPrice,
        });

        ClobApiClient.OrderResult? result;
        try
        {
            result = await _clob.PostMarketBuyOrderAsync(tokenId, sizeUsd, price, ct);
            if (result is null)
            {
                _journal.Complete(intentId);
                _log.LogWarning("CLOB order returned null (see errors above)");
                return null;
            }
            _journal.Submitted(intentId, result.OrderId);
            _log.LogInformation("CLOB GTC order submitted: {OrderId}", result.OrderId);
        }
        catch (ClobOrderRejectedException ex)
        {
            TradingSafety.HandleDefinitiveRejection(_journal, intentId, ex);
            _log.LogError("CLOB rejected BUY: {Error}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError("CLOB BUY outcome unknown; intent retained for recovery: {Error}", ex.Message);
            return null;
        }

        var fill = await ReconcileAsync(result, "BUY", price, 5, 3000, ct);
        if (fill is null)
        {
            _journal.Complete(intentId);
            return null;
        }
        _journal.Filled(intentId, fill.Value);

        var actualCost = fill.Value.Value;
        var actualShares = fill.Value.Shares;
        var actualPrice = fill.Value.Price;

        _log.LogInformation("Fill: requested ${Req:F2}, actual ${Act:F2} ({Shares:F2} shares @ {Price:F4})",
            sizeUsd, actualCost, actualShares, actualPrice);

        var position = new Position
        {
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            TokenId = tokenId,
            EntryPrice = actualPrice,
            SizeUsd = actualCost,
            Shares = actualShares,
            CurrentPrice = actualPrice,
            UnrealizedPnl = 0.0,
            Category = market.Category,
            EventTitle = market.EventTitle,
            OrderId = result.OrderId,
            FairEstimateAtEntry = signal.Estimate.FairProbability,
            LiquidationLimitPrice = signal.LimitPrice,
            QuoteAgeSeconds = signal.QuoteAgeSeconds,
        };
        portfolio.OpenPosition(position);
        portfolio.MarkOrderApplied(result.OrderId);

        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            Action = TradeAction.BUY,
            Price = actualPrice,
            SizeUsd = actualCost,
            Shares = actualShares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = result.OrderId,
            IsPaper = false,
            Rationale = signal.Estimate.ReasoningSummary,
            EdgeAtEntry = signal.Edge,
            KellyAtEntry = signal.KellyFraction,
            QuotedVwap = signal.ExecutionPrice,
            SlippageBps = signal.ExecutionPrice > 0
                ? (actualPrice - signal.ExecutionPrice) / signal.ExecutionPrice * 10_000 : 0,
            FillStatus = fill.Value.Status,
        };
    }

    public async Task<Trade?> ExecuteSellAsync(ExitSignal exitSignal, Portfolio portfolio, CancellationToken ct = default)
    {
        var pos = exitSignal.Position;
        if (!pos.BookDepthComplete || pos.LiquidationLimitPrice <= 0)
        {
            _log.LogWarning("SKIP SELL (insufficient bid depth): {Question}", pos.Question);
            return null;
        }
        var price = pos.LiquidationLimitPrice;

        if (pos.Shares < 5.0)
        {
            _log.LogWarning("SKIP SELL (below CLOB minimum 5 tokens): {Question} {Shares:F2} shares",
                pos.Question[..Math.Min(pos.Question.Length, 40)], pos.Shares);
            return null;
        }

        var intentId = _journal.Begin(new PendingOrderRecord
        {
            Kind = "SELL", ConditionId = pos.ConditionId, Question = pos.Question,
            Side = pos.Side.ToString(), TokenId = pos.TokenId, Requested = pos.Shares,
            LimitPrice = price, Category = pos.Category, QuotedVwap = exitSignal.CurrentPrice,
            ExitReason = exitSignal.ExitReason,
        });

        ClobApiClient.OrderResult? result;
        try
        {
            result = await _clob.PostMarketSellOrderAsync(pos.TokenId, pos.Shares, price, ct);
            if (result is null)
            {
                _journal.Complete(intentId);
                _log.LogWarning("CLOB SELL order returned null");
                return null;
            }
            _journal.Submitted(intentId, result.OrderId);
            _log.LogInformation("CLOB SELL GTC order submitted: {OrderId}", result.OrderId);
        }
        catch (ClobOrderRejectedException ex)
        {
            TradingSafety.HandleDefinitiveRejection(_journal, intentId, ex);
            _log.LogError("CLOB rejected SELL: {Error}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError("CLOB SELL outcome unknown; intent retained for recovery: {Error}", ex.Message);
            return null;
        }

        var fill = await ReconcileAsync(result, "SELL", price, 5, 3000, ct);
        if (fill is null)
        {
            _journal.Complete(intentId);
            return null;
        }
        _journal.Filled(intentId, fill.Value);

        var actualFillPrice = fill.Value.Price;
        var pnl = portfolio.ReducePosition(pos.ConditionId, fill.Value.Shares, actualFillPrice);
        portfolio.MarkOrderApplied(result.OrderId);

        _log.LogInformation("SOLD: {Question} fill={FillPrice:F4} (limit={Limit:F4}) PnL=${Pnl:+0.00;-0.00} ({Reason})",
            pos.Question[..Math.Min(pos.Question.Length, 40)], actualFillPrice, price, pnl, exitSignal.ExitReason);

        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = pos.ConditionId,
            Question = pos.Question,
            Side = pos.Side,
            Action = TradeAction.SELL,
            Price = actualFillPrice,
            SizeUsd = fill.Value.Value,
            Shares = fill.Value.Shares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = result.OrderId,
            IsPaper = false,
            Rationale = $"Exit: {exitSignal.ExitReason}",
            ExitReason = exitSignal.ExitReason,
            QuotedVwap = exitSignal.CurrentPrice,
            SlippageBps = exitSignal.CurrentPrice > 0
                ? (exitSignal.CurrentPrice - actualFillPrice) / exitSignal.CurrentPrice * 10_000 : 0,
            FillStatus = fill.Value.Status,
        };
    }

    public async Task<Trade?> ExecuteTopupAndSellAsync(TopupCandidate candidate, Portfolio portfolio, CancellationToken ct = default)
    {
        var pos = candidate.Position;
        var buyPrice = candidate.BuyLimitPrice;
        var sellPrice = candidate.SellLimitPrice;
        var buyUsd = candidate.TopupCost;
        var buyIntentId = _journal.Begin(new PendingOrderRecord
        {
            Kind = "TOPUP_BUY", ConditionId = pos.ConditionId, Question = pos.Question,
            Side = pos.Side.ToString(), TokenId = pos.TokenId, Requested = buyUsd,
            LimitPrice = buyPrice, Category = pos.Category, QuotedVwap = candidate.BuyVwap,
        });

        // Step 1: BUY 5 tokens to top up position
        _log.LogInformation("TOPUP BUY: {Question} 5 tokens @ {Price:F4} (${Cost:F2})",
            pos.Question[..Math.Min(pos.Question.Length, 40)], buyPrice, buyUsd);

        ClobApiClient.OrderResult? buyResult;
        try
        {
            buyResult = await _clob.PostMarketBuyOrderAsync(pos.TokenId, buyUsd, buyPrice, ct);
            if (buyResult is null)
            {
                _journal.Complete(buyIntentId);
                _log.LogWarning("TOPUP BUY order returned null");
                return null;
            }
            _journal.Submitted(buyIntentId, buyResult.OrderId);
            _log.LogInformation("TOPUP BUY GTC order submitted: {OrderId}", buyResult.OrderId);
        }
        catch (ClobOrderRejectedException ex)
        {
            TradingSafety.HandleDefinitiveRejection(_journal, buyIntentId, ex);
            _log.LogError("CLOB rejected TOPUP BUY: {Error}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError("TOPUP BUY outcome unknown; intent retained for recovery: {Error}", ex.Message);
            return null;
        }

        var buyFill = await ReconcileAsync(buyResult, "BUY", buyPrice, 3, 2000, ct);
        if (buyFill is null)
        {
            _journal.Complete(buyIntentId);
            return null;
        }
        _journal.Filled(buyIntentId, buyFill.Value);

        // BUY filled — update position in portfolio
        portfolio.AddToPosition(pos.ConditionId, buyFill.Value.Shares, buyFill.Value.Value);
        portfolio.MarkOrderApplied(buyResult.OrderId);

        // Step 2: SELL all tokens (now >= 5)
        var totalShares = pos.Shares;  // already updated by AddToPosition
        if (totalShares < 5.0)
        {
            _log.LogWarning("TOPUP partial fill left only {Shares:F2} tokens; cannot SELL yet", totalShares);
            return null;
        }
        _log.LogInformation("TOPUP SELL: {Shares:F2} tokens @ {Price:F4}", totalShares, sellPrice);
        var sellIntentId = _journal.Begin(new PendingOrderRecord
        {
            Kind = "TOPUP_SELL", ConditionId = pos.ConditionId, Question = pos.Question,
            Side = pos.Side.ToString(), TokenId = pos.TokenId, Requested = totalShares,
            LimitPrice = sellPrice, Category = pos.Category, QuotedVwap = candidate.SellVwap,
            ExitReason = candidate.ExitReason,
        });

        ClobApiClient.OrderResult? sellResult;
        try
        {
            sellResult = await _clob.PostMarketSellOrderAsync(pos.TokenId, totalShares, sellPrice, ct);
            if (sellResult is null)
            {
                _journal.Complete(sellIntentId);
                _log.LogWarning("TOPUP SELL order returned null (position now has {Shares:F2} tokens)", totalShares);
                return null;
            }
            _journal.Submitted(sellIntentId, sellResult.OrderId);
            _log.LogInformation("TOPUP SELL GTC order submitted: {OrderId}", sellResult.OrderId);
        }
        catch (ClobOrderRejectedException ex)
        {
            TradingSafety.HandleDefinitiveRejection(_journal, sellIntentId, ex);
            _log.LogError("CLOB rejected TOPUP SELL: {Error}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError("TOPUP SELL outcome unknown; intent retained for recovery: {Error}", ex.Message);
            return null;
        }

        var sellFill = await ReconcileAsync(sellResult, "SELL", sellPrice, 5, 3000, ct);
        if (sellFill is null)
        {
            _journal.Complete(sellIntentId);
            return null;
        }
        _journal.Filled(sellIntentId, sellFill.Value);

        var pnl = portfolio.ReducePosition(pos.ConditionId, sellFill.Value.Shares, sellFill.Value.Price);
        portfolio.MarkOrderApplied(sellResult.OrderId);
        _log.LogInformation("TOPUP+SELL complete: {Question} PnL=${Pnl:+0.00;-0.00} ({Reason})",
            pos.Question[..Math.Min(pos.Question.Length, 40)], pnl, candidate.ExitReason);

        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = pos.ConditionId,
            Question = pos.Question,
            Side = pos.Side,
            Action = TradeAction.SELL,
            Price = sellFill.Value.Price,
            SizeUsd = sellFill.Value.Value,
            Shares = sellFill.Value.Shares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = sellResult.OrderId,
            IsPaper = false,
            Rationale = $"Topup+Exit: {candidate.ExitReason}",
            ExitReason = candidate.ExitReason,
            QuotedVwap = candidate.SellVwap,
            SlippageBps = candidate.SellVwap > 0
                ? (candidate.SellVwap - sellFill.Value.Price) / candidate.SellVwap * 10_000 : 0,
            FillStatus = sellFill.Value.Status,
        };
    }
}
