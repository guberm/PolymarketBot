using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class Portfolio
{
    private readonly BotConfig _config;
    private readonly ILogger<Portfolio> _log;

    public double Bankroll { get; private set; }
    public double InitialBankroll { get; private set; }
    public List<Position> Positions { get; private set; }
    public double HighWaterMark { get; private set; }
    public double DailyStartValue { get; private set; }
    public double TotalRealizedPnl { get; private set; }
    public int TotalTrades { get; private set; }
    public bool IsHalted { get; set; }
    public double TotalApiCost { get; private set; }
    public double DailyApiCost { get; private set; }
    public string DailyTrackingDate { get; private set; }
    private readonly HashSet<string> _appliedOrderIds;

    // condition_id -> DateTimeOffset of close (in-memory cooldown, not persisted)
    private readonly Dictionary<string, DateTimeOffset> _recentlyClosed = new();

    public Portfolio(BotConfig config, ILogger<Portfolio> log, PortfolioSnapshot? snapshot = null)
    {
        _config = config;
        _log = log;

        if (snapshot is not null)
        {
            Bankroll = snapshot.Bankroll;
            InitialBankroll = snapshot.InitialBankroll;
            Positions = new List<Position>(snapshot.Positions);
            HighWaterMark = snapshot.HighWaterMark;
            DailyStartValue = snapshot.DailyStartValue;
            TotalRealizedPnl = snapshot.TotalRealizedPnl;
            TotalTrades = snapshot.TotalTrades;
            IsHalted = snapshot.IsHalted;
            TotalApiCost = snapshot.TotalApiCost;
            DailyApiCost = snapshot.DailyApiCost;
            DailyTrackingDate = string.IsNullOrWhiteSpace(snapshot.DailyTrackingDate)
                ? DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd")
                : snapshot.DailyTrackingDate;
            _appliedOrderIds = snapshot.AppliedOrderIds.ToHashSet();
        }
        else
        {
            Bankroll = config.InitialBankroll;
            InitialBankroll = config.InitialBankroll;
            Positions = [];
            HighWaterMark = config.InitialBankroll;
            DailyStartValue = config.InitialBankroll;
            TotalRealizedPnl = 0;
            TotalTrades = 0;
            IsHalted = false;
            TotalApiCost = 0;
            DailyApiCost = 0;
            DailyTrackingDate = DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd");
            _appliedOrderIds = [];
        }
    }

    public PortfolioSnapshot Snapshot() => new()
    {
        Bankroll = Bankroll,
        InitialBankroll = InitialBankroll,
        Positions = new List<Position>(Positions),
        HighWaterMark = HighWaterMark,
        DailyStartValue = DailyStartValue,
        TotalRealizedPnl = TotalRealizedPnl,
        TotalTrades = TotalTrades,
        IsHalted = IsHalted,
        TotalApiCost = TotalApiCost,
        DailyApiCost = DailyApiCost,
        DailyTrackingDate = DailyTrackingDate,
        AppliedOrderIds = _appliedOrderIds.ToList(),
    };

    public bool HasAppliedOrder(string orderId) => _appliedOrderIds.Contains(orderId);

    public void MarkOrderApplied(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return;
        _appliedOrderIds.Add(orderId);
        if (_appliedOrderIds.Count <= 500) return;
        foreach (var old in _appliedOrderIds.Take(_appliedOrderIds.Count - 500).ToList())
            _appliedOrderIds.Remove(old);
    }

    public double TotalExposure() => Positions.Sum(p => p.SizeUsd);

    public double LiquidationValue() => Positions.Sum(p => Math.Max(0, p.Shares * p.CurrentPrice));

    public double Equity() => Bankroll + LiquidationValue();

    public double CategoryExposure(string category)
        => Positions.Where(p => p.Category == category).Sum(p => p.SizeUsd);

    public double EventExposure(string eventTitle)
        => string.IsNullOrWhiteSpace(eventTitle) ? 0 : Positions
            .Where(p => string.Equals(p.EventTitle.Trim(), eventTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.SizeUsd);

    public bool HasPosition(string conditionId)
        => Positions.Any(p => p.ConditionId == conditionId);

    // -- Signal generation --

    public Signal? GenerateSignal(MarketInfo market, Estimate estimate)
    {
        var fair = estimate.FairProbability;
        var yesExecutionPrice = EstimateBuyExecutionPrice(market.OutcomeYesPrice);
        var noExecutionPrice = EstimateBuyExecutionPrice(market.OutcomeNoPrice);
        var yesEdge = fair - yesExecutionPrice;
        var noEdge = (1.0 - fair) - noExecutionPrice;

        Side side;
        double marketPrice, executionPrice;

        if (yesEdge > noEdge && yesEdge > _config.MinEdge)
        {
            side = Side.YES;
            marketPrice = market.OutcomeYesPrice;
            executionPrice = yesExecutionPrice;
        }
        else if (noEdge > _config.MinEdge)
        {
            side = Side.NO;
            marketPrice = market.OutcomeNoPrice;
            executionPrice = noExecutionPrice;
        }
        else
        {
            return null;
        }

        return BuildSignal(market, estimate, side, marketPrice, executionPrice, executionPrice, 0, null);
    }

    public Signal? RepriceSignal(
        Signal signal, double executionPrice, double limitPrice, double quoteAgeSeconds = 0)
    {
        return BuildSignal(signal.Market, signal.Estimate, signal.Side, signal.MarketPrice,
            executionPrice, limitPrice, quoteAgeSeconds, signal.PositionSizeUsd);
    }

    private Signal? BuildSignal(
        MarketInfo market,
        Estimate estimate,
        Side side,
        double marketPrice,
        double executionPrice,
        double limitPrice,
        double quoteAgeSeconds,
        double? sizeCapUsd)
    {
        var fair = estimate.FairProbability;
        var fairForSide = side == Side.YES ? fair : 1.0 - fair;
        var edge = fairForSide - executionPrice;
        if (edge <= _config.MinEdge || executionPrice <= 0 || executionPrice >= 1) return null;

        // Kelly criterion: f* = (b*p - q) / b
        var b = (1.0 / executionPrice) - 1.0;
        var p = side == Side.YES ? fair : 1.0 - fair;
        var q = 1.0 - p;
        var kellyRaw = b > 0 ? (b * p - q) / b : 0.0;
        kellyRaw = Math.Max(0.0, kellyRaw);

        // Fractional Kelly + position cap (use portfolio value, not just cash)
        var kellyFraction = _config.LiveTrading && !_config.AllowUnsafeRisk
            ? Math.Min(_config.KellyFraction, 0.50)
            : _config.KellyFraction;
        var maxPositionPct = _config.LiveTrading && !_config.AllowUnsafeRisk
            ? Math.Min(_config.MaxPositionPct, 0.15)
            : _config.MaxPositionPct;
        var kelly = kellyRaw * kellyFraction;
        var portfolioVal = Equity();
        var sizeUsd = kelly * portfolioVal;
        sizeUsd = Math.Min(sizeUsd, portfolioVal * maxPositionPct);
        sizeUsd = Math.Min(sizeUsd, Bankroll); // never exceed available cash
        if (sizeCapUsd.HasValue) sizeUsd = Math.Min(sizeUsd, sizeCapUsd.Value);

        if (sizeUsd < _config.MinTradeUsd) return null;

        // CLOB minimum: 5 tokens at the estimated executable BUY price.
        var minClobUsd = Math.Max(5.0 * executionPrice, 1.0);
        if (sizeUsd < minClobUsd)
        {
            _log.LogInformation("Position ${Size:F2} below CLOB minimum ${Min:F2} (5 tokens @ exec {Price:F3})",
                sizeUsd, minClobUsd, executionPrice);
            return null;
        }

        if (_config.LiveTrading && !_config.AllowUnsafeRisk && Bankroll > 0 &&
            minClobUsd > Bankroll * _config.MaxLiveOrderBankrollPct)
        {
            _log.LogInformation(
                "Live risk BLOCK: CLOB minimum ${Min:F2} would use {Pct:P0} of bankroll (limit {Limit:P0})",
                minClobUsd, minClobUsd / Bankroll, _config.MaxLiveOrderBankrollPct);
            return null;
        }

        return new Signal
        {
            Market = market,
            Estimate = estimate,
            Side = side,
            Edge = edge,
            MarketPrice = marketPrice,
            ExecutionPrice = executionPrice,
            KellyFraction = kelly,
            PositionSizeUsd = Math.Round(sizeUsd, 2),
            ExpectedValue = Math.Round(sizeUsd * edge, 4),
            LimitPrice = limitPrice,
            QuoteAgeSeconds = quoteAgeSeconds,
        };
    }

    private double EstimateBuyExecutionPrice(double marketPrice)
    {
        return Math.Min(marketPrice + _config.EntryPriceBuffer, 0.99);
    }

    // -- Risk checks --

    public bool CheckPortfolioRisk()
    {
        // Daily stop loss uses executable liquidation value, not cost basis.
        var portfolioValue = Equity();
        var dailyPnl = portfolioValue - DailyStartValue;
        var dailyStopLossPct = _config.LiveTrading && !_config.AllowUnsafeRisk
            ? Math.Min(_config.DailyStopLossPct, 0.25)
            : _config.DailyStopLossPct;
        if (dailyPnl < 0 && Math.Abs(dailyPnl) > DailyStartValue * dailyStopLossPct)
        {
            _log.LogWarning("HALT: Daily stop loss triggered (PnL=${Pnl:+0.00;-0.00}, limit={Limit:P0})",
                dailyPnl, dailyStopLossPct);
            IsHalted = true;
            return false;
        }

        // Max drawdown from high water mark
        if (HighWaterMark > 0)
        {
            var drawdown = (HighWaterMark - portfolioValue) / HighWaterMark;
            var maxDrawdownPct = _config.LiveTrading && !_config.AllowUnsafeRisk
                ? Math.Min(_config.MaxDrawdownPct, 0.60)
                : _config.MaxDrawdownPct;
            if (drawdown > maxDrawdownPct)
            {
                _log.LogWarning(
                    "HALT: Max drawdown {Drawdown:P1} exceeded (limit={Limit:P0}, configured={Configured:P0}, allow_unsafe_risk={AllowUnsafe})",
                    drawdown, maxDrawdownPct, _config.MaxDrawdownPct, _config.AllowUnsafeRisk);
                IsHalted = true;
                return false;
            }
        }

        // Agent death — only when total portfolio value (free cash + open positions)
        // drops below $1. Negative bankroll from API costs while holding positions
        // is normal: positions will eventually resolve and return USDC.
        if (portfolioValue < 1.0)
        {
            _log.LogWarning("HALT: Portfolio value < $1 — agent is dead");
            IsHalted = true;
            return false;
        }

        return true;
    }

    public bool CheckRisk(Signal signal)
    {
        if (HasPosition(signal.Market.ConditionId))
        {
            _log.LogInformation("Risk BLOCK: already positioned in {Question}", Truncate(signal.Market.Question, 40));
            return false;
        }

        var cooldownSecs = _config.ScanIntervalMinutes * 60 * 2;  // 2-cycle cooldown
        if (_recentlyClosed.TryGetValue(signal.Market.ConditionId, out var closedAt))
        {
            var elapsed = (DateTimeOffset.UtcNow - closedAt).TotalSeconds;
            if (elapsed < cooldownSecs)
            {
                var remainingMin = (cooldownSecs - elapsed) / 60;
                _log.LogInformation("Risk BLOCK: recently closed {Question} ({Remaining:F0}min cooldown remaining)",
                    Truncate(signal.Market.Question, 40), remainingMin);
                return false;
            }
            else
            {
                _recentlyClosed.Remove(signal.Market.ConditionId);
            }
        }

        if (Positions.Count >= _config.MaxConcurrentPositions)
        {
            _log.LogInformation("Risk BLOCK: max positions ({Max}) reached", _config.MaxConcurrentPositions);
            return false;
        }

        var pv = Equity();
        var newExposure = TotalExposure() + signal.PositionSizeUsd;
        var maxTotalExposurePct = _config.LiveTrading && !_config.AllowUnsafeRisk
            ? Math.Min(_config.MaxTotalExposurePct, 0.90)
            : _config.MaxTotalExposurePct;
        var maxAllowed = pv * maxTotalExposurePct;
        if (newExposure > maxAllowed)
        {
            _log.LogInformation("Risk BLOCK: total exposure ${New:F2} > limit ${Limit:F2}", newExposure, maxAllowed);
            return false;
        }

        var catExp = CategoryExposure(signal.Market.Category) + signal.PositionSizeUsd;
        var catLimit = pv * _config.MaxCategoryExposurePct;
        if (catExp > catLimit)
        {
            _log.LogInformation("Risk BLOCK: '{Category}' exposure ${Exp:F2} > limit ${Limit:F2}",
                signal.Market.Category, catExp, catLimit);
            return false;
        }

        var eventExp = EventExposure(signal.Market.EventTitle) + signal.PositionSizeUsd;
        var eventLimit = pv * _config.MaxEventExposurePct;
        if (!string.IsNullOrWhiteSpace(signal.Market.EventTitle) && eventExp > eventLimit)
        {
            _log.LogInformation("Risk BLOCK: event exposure ${Exp:F2} > limit ${Limit:F2} for '{Event}'",
                eventExp, eventLimit, Truncate(signal.Market.EventTitle, 40));
            return false;
        }

        return CheckPortfolioRisk();
    }

    // -- Position management --

    public void OpenPosition(Position position)
    {
        Bankroll -= position.SizeUsd;
        TotalTrades++;
        Positions.Add(position);
        _log.LogInformation("Opened {Side} on {Question} ${Size:F2} @ {Price:F3}",
            position.Side, Truncate(position.Question, 40), position.SizeUsd, position.EntryPrice);
    }

    public double ClosePosition(string conditionId, double exitPrice)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null) return 0.0;

        var pnl = pos.Shares * (exitPrice - pos.EntryPrice);
        Bankroll += pos.SizeUsd + pnl;
        TotalRealizedPnl += pnl;
        Positions = Positions.Where(p => p.ConditionId != conditionId).ToList();
        _recentlyClosed[conditionId] = DateTimeOffset.UtcNow;
        HighWaterMark = Math.Max(HighWaterMark, Equity());

        _log.LogInformation("Closed {Question} PnL: ${Pnl:+0.00;-0.00}", Truncate(pos.Question, 40), pnl);
        return pnl;
    }

    public double ReducePosition(string conditionId, double soldShares, double exitPrice)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null || soldShares <= 0) return 0;
        var sold = Math.Min(soldShares, pos.Shares);
        var costBasis = sold * pos.EntryPrice;
        var proceeds = sold * exitPrice;
        var pnl = proceeds - costBasis;
        Bankroll += proceeds;
        TotalRealizedPnl += pnl;
        TotalTrades++;
        pos.Shares -= sold;
        pos.SizeUsd = Math.Max(0, pos.SizeUsd - costBasis);
        if (pos.Shares < 0.1)
        {
            Positions = Positions.Where(p => p.ConditionId != conditionId).ToList();
            _recentlyClosed[conditionId] = DateTimeOffset.UtcNow;
        }
        else
            pos.UnrealizedPnl = pos.Shares * (pos.CurrentPrice - pos.EntryPrice);
        HighWaterMark = Math.Max(HighWaterMark, Equity());
        return pnl;
    }

    public double ResolvePosition(string conditionId, bool won)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null) return 0.0;

        var payout = won ? pos.Shares : 0.0;
        var pnl = payout - pos.SizeUsd;
        Bankroll += payout;
        TotalRealizedPnl += pnl;
        TotalTrades++;
        Positions = Positions.Where(p => p.ConditionId != conditionId).ToList();
        _recentlyClosed[conditionId] = DateTimeOffset.UtcNow;
        HighWaterMark = Math.Max(HighWaterMark, Equity());

        var result = won ? "WON" : "LOST";
        _log.LogInformation("Resolved ({Result}): {Question} payout=${Payout:F2}, PnL=${Pnl:+0.00;-0.00}",
            result, Truncate(pos.Question, 40), payout, pnl);
        return pnl;
    }

    // -- Position review --

    public void UpdatePositionPrices(Dictionary<string, double> prices)
    {
        foreach (var pos in Positions)
        {
            if (prices.TryGetValue(pos.TokenId, out var price))
            {
                pos.CurrentPrice = price;
                pos.UnrealizedPnl = pos.Shares * (pos.CurrentPrice - pos.EntryPrice);
            }
        }
    }

    public void UpdatePositionQuotes(IReadOnlyDictionary<string, ExecutionQuote> quotes)
    {
        foreach (var pos in Positions)
        {
            if (!quotes.TryGetValue(pos.TokenId, out var quote) || pos.Shares <= 0)
            {
                pos.QuoteFailures++;
                if (pos.LastFreshPrice <= 0) pos.LastFreshPrice = pos.CurrentPrice;
                var fallback = pos.LastFreshPrice;
                if (fallback > 0 && pos.QuoteFailures < Math.Max(1, _config.QuoteFailureGraceCycles))
                {
                    pos.CurrentPrice = fallback * (1 - _config.StaleQuoteHaircutPct);
                    pos.UnrealizedPnl = pos.Shares * (pos.CurrentPrice - pos.EntryPrice);
                    _log.LogWarning("Quote unavailable for {Question}; using {Haircut:P0} haircut ({Failures}/{Grace} grace cycles)",
                        Truncate(pos.Question, 40), _config.StaleQuoteHaircutPct, pos.QuoteFailures,
                        _config.QuoteFailureGraceCycles - 1);
                }
                else
                {
                    pos.CurrentPrice = 0;
                    pos.UnrealizedPnl = -pos.SizeUsd;
                    _log.LogError("Quote grace exhausted for {Question}; liquidation value set to zero",
                        Truncate(pos.Question, 40));
                }
                pos.LiquidationLimitPrice = 0;
                pos.BookDepthComplete = false;
                pos.QuoteAgeSeconds = 0;
                continue;
            }
            pos.CurrentPrice = quote.FilledValue / pos.Shares;
            pos.UnrealizedPnl = pos.Shares * (pos.CurrentPrice - pos.EntryPrice);
            pos.LiquidationLimitPrice = quote.Complete ? quote.WorstPrice : 0;
            pos.BookDepthComplete = quote.Complete;
            pos.QuoteAgeSeconds = quote.AgeSeconds;
            pos.LastFreshPrice = pos.CurrentPrice;
            pos.QuoteFailures = 0;
        }
    }

    public List<ExitSignal> GenerateExitSignals()
    {
        var signals = new List<ExitSignal>();
        foreach (var pos in Positions)
        {
            // Haircut/zero fallback prices protect valuation but are not executable quotes.
            if (!pos.BookDepthComplete || pos.LiquidationLimitPrice <= 0)
            {
                _log.LogDebug("Skip exit review: no executable bid depth for {Question}",
                    Truncate(pos.Question, 40));
                continue;
            }

            // Skip unsellable positions: penny prices or below CLOB minimum (5 tokens)
            if (pos.CurrentPrice < 0.01)
            {
                _log.LogDebug("Skip review: {Question} (price {Price:F4} < $0.01)",
                    Truncate(pos.Question, 40), pos.CurrentPrice);
                continue;
            }
            if (pos.Shares < 5.0)
            {
                _log.LogDebug("Skip review: {Question} ({Shares:F2} tokens < 5 minimum)",
                    Truncate(pos.Question, 40), pos.Shares);
                continue;
            }

            var pnl = pos.Shares * (pos.CurrentPrice - pos.EntryPrice);
            var pnlPct = pos.EntryPrice > 0 ? (pos.CurrentPrice - pos.EntryPrice) / pos.EntryPrice : 0.0;

            // Stop-loss
            if (pnlPct < -_config.PositionStopLossPct)
            {
                signals.Add(new ExitSignal { Position = pos, ExitReason = "stop_loss", CurrentPrice = pos.CurrentPrice, UnrealizedPnl = pnl, PnlPct = pnlPct });
                continue;
            }

            // Take-profit
            if (pos.CurrentPrice >= _config.TakeProfitPrice)
            {
                signals.Add(new ExitSignal { Position = pos, ExitReason = "take_profit", CurrentPrice = pos.CurrentPrice, UnrealizedPnl = pnl, PnlPct = pnlPct });
                continue;
            }

            // Edge-gone
            if (pos.FairEstimateAtEntry > 0)
            {
                var fairForSide = pos.Side == Side.YES ? pos.FairEstimateAtEntry : 1.0 - pos.FairEstimateAtEntry;
                if (pos.CurrentPrice > fairForSide + _config.ExitEdgeBuffer)
                {
                    signals.Add(new ExitSignal { Position = pos, ExitReason = "edge_gone", CurrentPrice = pos.CurrentPrice, UnrealizedPnl = pnl, PnlPct = pnlPct });
                    continue;
                }
            }
        }
        return signals;
    }

    public List<TopupCandidate> GenerateTopupCandidates()
    {
        var candidates = new List<TopupCandidate>();
        foreach (var pos in Positions)
        {
            if (pos.CurrentPrice < 0.01) continue;  // penny = unsellable even with top-up
            if (pos.Shares >= 5.0) continue;         // can sell normally
            if (pos.EntryPrice <= 0) continue;

            var pnlPct = (pos.CurrentPrice - pos.EntryPrice) / pos.EntryPrice;

            // Check same exit conditions as GenerateExitSignals
            string? exitReason = null;
            if (pnlPct < -_config.PositionStopLossPct)
                exitReason = "stop_loss";
            else if (pos.CurrentPrice >= _config.TakeProfitPrice)
                exitReason = "take_profit";
            else if (pos.FairEstimateAtEntry > 0)
            {
                var fairForSide = pos.Side == Side.YES ? pos.FairEstimateAtEntry : 1.0 - pos.FairEstimateAtEntry;
                if (pos.CurrentPrice > fairForSide + _config.ExitEdgeBuffer)
                    exitReason = "edge_gone";
            }

            if (exitReason is null) continue;
            if (exitReason == "stop_loss" && _config.StopLossRequiresNegativeEdge)
            {
                continue;
            }

            var topupCost = 5.0 * pos.CurrentPrice;
            var recoveryValue = pos.Shares * pos.CurrentPrice;

            candidates.Add(new TopupCandidate
            {
                Position = pos,
                ExitReason = exitReason,
                TokensToBuy = 5.0,
                TopupCost = topupCost,
                RecoveryValue = recoveryValue,
            });
        }
        return candidates;
    }

    public void AddToPosition(string conditionId, double additionalShares, double additionalCost)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null) return;
        pos.Shares += additionalShares;
        pos.SizeUsd += additionalCost;
        Bankroll -= additionalCost;
        _log.LogInformation(
            "Top-up: +{Shares:F2} tokens (${Cost:F2}) -> {Question} now {Total:F2} tokens",
            additionalShares, additionalCost, Truncate(pos.Question, 40), pos.Shares);
    }

    public List<Position> GetReviewCandidates()
    {
        var candidates = new List<Position>();
        foreach (var pos in Positions)
        {
            if (pos.EntryPrice <= 0) continue;
            var priceMove = Math.Abs(pos.CurrentPrice - pos.EntryPrice) / pos.EntryPrice;
            if (priceMove >= _config.ReviewReestimateThresholdPct)
                candidates.Add(pos);
        }
        candidates.Sort((a, b) => b.SizeUsd.CompareTo(a.SizeUsd));
        return candidates;
    }

    // -- Balance sync --

    /// <summary>
    /// Sync bankroll from actual on-chain USDC balance.
    /// On-chain USDC is always the free cash (bankroll) — conditional tokens
    /// are held separately in the CLOB and not reflected in USDC balance.
    /// Always syncs both up and down so the bot has an accurate view of
    /// spendable funds (handles resolved-position payouts, deposits, fees, etc.)
    /// </summary>
    public void SyncBalance(double actualUsdcBalance)
    {
        var prevBankroll = Bankroll;
        var diff = actualUsdcBalance - prevBankroll;
        if (Math.Abs(diff) <= 0.001)
            return;

        Bankroll = actualUsdcBalance;
        if (diff > 0)
            _log.LogInformation(
                "Balance sync (upward): ${Old:F2} -> ${New:F2} (+${Diff:F2}, {Count} positions open)",
                prevBankroll, Bankroll, diff, Positions.Count);
        else
            _log.LogWarning(
                "Balance sync (downward): ${Old:F2} -> ${New:F2} (${Diff:F2}, {Count} positions open)",
                prevBankroll, Bankroll, diff, Positions.Count);

        HighWaterMark = Math.Max(HighWaterMark, Equity());
    }

    // -- Cost tracking --

    public void RecordApiCost(int inputTokens, int outputTokens)
    {
        RecordApiCostUsd((inputTokens * 3.0 + outputTokens * 15.0) / 1_000_000.0);
    }

    public void RecordApiCostUsd(double cost)
    {
        TotalApiCost += Math.Max(0, cost);
        DailyApiCost += Math.Max(0, cost);
    }

    /// <summary>
    /// Remove a phantom position that has no actual on-chain tokens.
    /// Does NOT adjust bankroll — balance sync already reflects real USDC.
    /// Records the cost basis as realized loss.
    /// </summary>
    public double RemoveGhostPosition(string conditionId)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null) return 0.0;
        var pnl = -pos.SizeUsd;
        TotalRealizedPnl += pnl;
        _recentlyClosed[conditionId] = DateTimeOffset.UtcNow;
        Positions = Positions.Where(p => p.ConditionId != conditionId).ToList();
        _log.LogWarning("Ghost removed: {Question} (${Loss:F2} written off, PnL={Pnl:+0.00;-0.00})",
            Truncate(pos.Question, 40), pos.SizeUsd, pnl);
        return pnl;
    }

    public void ResetDaily(string? trackingDate = null)
    {
        DailyStartValue = Equity();
        DailyApiCost = 0;
        DailyTrackingDate = string.IsNullOrWhiteSpace(trackingDate)
            ? DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd")
            : trackingDate;
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
