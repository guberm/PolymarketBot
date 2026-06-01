using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PolymarketBot;
using PolymarketBot.Models;
using PolymarketBot.Services;

// ── Enable ANSI colors on Windows ──────────────────────────────
if (OperatingSystem.IsWindows())
{
    EnableAnsiColors();
}

static void EnableAnsiColors()
{
    const int STD_OUTPUT_HANDLE = -11;
    const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    var handle = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetConsoleMode(handle, out uint mode))
        SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
}

[DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
[DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

// ── Parse args ──────────────────────────────────────────────────

var verbose = args.Contains("--verbose") || args.Contains("-v");
var console_ = args.Contains("--console") || args.Contains("-c");

// ── Config ──────────────────────────────────────────────────────

var config = BotConfig.FromEnv();

// CLI args override env vars
static double? ParseDoubleArg(string[] a, string name)
{
    var idx = Array.IndexOf(a, name);
    return idx >= 0 && idx + 1 < a.Length && double.TryParse(a[idx + 1], out var v) ? v : null;
}

static int? ParseIntArg(string[] a, string name)
{
    var idx = Array.IndexOf(a, name);
    return idx >= 0 && idx + 1 < a.Length && int.TryParse(a[idx + 1], out var v) ? v : null;
}

if (ParseDoubleArg(args, "--max-position-pct") is { } maxPosPct)
    config.MaxPositionPct = maxPosPct;
if (ParseDoubleArg(args, "--max-total-exposure-pct") is { } maxExpPct)
    config.MaxTotalExposurePct = maxExpPct;
if (ParseDoubleArg(args, "--max-category-exposure-pct") is { } maxCatPct)
    config.MaxCategoryExposurePct = maxCatPct;
if (ParseDoubleArg(args, "--daily-stop-loss-pct") is { } dailySl)
    config.DailyStopLossPct = dailySl;
if (ParseDoubleArg(args, "--max-drawdown-pct") is { } maxDd)
    config.MaxDrawdownPct = maxDd;
if (ParseIntArg(args, "--max-concurrent-positions") is { } maxPos)
    config.MaxConcurrentPositions = maxPos;

static string Ts() => DateTime.Now.ToString("HH:mm:ss");

// ANSI color codes for console output
const string GREEN = "\x1b[1;32m";
const string RED = "\x1b[1;31m";
const string YELLOW = "\x1b[1;33m";
const string RESET = "\x1b[0m";

// Helper: Console.Write only if --console flag is set
void Con(string msg) { if (console_) Console.WriteLine($"[{Ts()}] {msg}"); }

// ── Logging ─────────────────────────────────────────────────────

Directory.CreateDirectory(config.DataDir);

// JSON file logger (matches Python's JsonFormatter → data/bot.log)
using var fileLogStream = new StreamWriter(
    new FileStream(
        Path.Combine(config.DataDir, "bot.log"),
        FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
    System.Text.Encoding.UTF8) { AutoFlush = true };

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    builder.AddSimpleConsole(opts =>
    {
        opts.TimestampFormat = "[HH:mm:ss] ";
        opts.SingleLine = true;
    });
    builder.AddProvider(new JsonFileLoggerProvider(fileLogStream));
});

var log = loggerFactory.CreateLogger("bot.main");

var mode = config.LiveTrading ? "LIVE" : "PAPER";
log.LogInformation(new string('=', 60));
log.LogInformation("Polymarket Bot (.NET)");
log.LogInformation("Mode: {Mode} | Bankroll: ${Bankroll:F2}", mode, config.InitialBankroll);
log.LogInformation("Min edge: {MinEdge:P0} | Max position: {MaxPos:P0}", config.MinEdge, config.MaxPositionPct);
log.LogInformation("Scan interval: {Interval} min | Markets/cycle: {Markets}",
    config.ScanIntervalMinutes, config.MarketsPerCycle);
var _modeLabel = config.MultiProvider ? "multi" : config.AiProvider;
log.LogInformation("Ensemble: {Size}x [{Mode}]", config.EnsembleSize, _modeLabel);
log.LogInformation(new string('=', 60));

// ── Detailed config dump ─────────────────────────────────────────────────
{
    var enabledProviders = new System.Text.StringBuilder();
    if (config.AnthropicEnabled  && !string.IsNullOrEmpty(config.AnthropicApiKey))  enabledProviders.Append("anthropic ");
    if (config.OpenAiEnabled     && !string.IsNullOrEmpty(config.OpenAiApiKey))     enabledProviders.Append("openai ");
    if (config.GeminiEnabled     && !string.IsNullOrEmpty(config.GeminiApiKey))     enabledProviders.Append("gemini ");
    if (config.OpenRouterEnabled && !string.IsNullOrEmpty(config.OpenRouterApiKey)) enabledProviders.Append("openrouter ");
    if (config.AzureOpenAiEnabled && !string.IsNullOrEmpty(config.AzureOpenAiApiKey)
        && !string.IsNullOrEmpty(config.AzureOpenAiEndpoint)
        && !string.IsNullOrEmpty(config.AzureOpenAiDeployment))                     enabledProviders.Append("azure_openai ");
    var provStr = config.MultiProvider
        ? $"multi ({enabledProviders.ToString().Trim()})"
        : config.AiProvider;

    log.LogInformation("── AI ──────────────────────────────────────────────────────");
    log.LogInformation("  Provider:       {P}",  provStr);
    log.LogInformation("  Ensemble:       {N}x  temp={T}  max_std={S:P0}",
        config.EnsembleSize, config.EnsembleTemperature, config.MaxEstimateStd);
    log.LogInformation("  API budget:     cycle=${Cycle:F2}  daily=${Daily:F2}",
        config.MaxCycleApiCostUsd, config.MaxDailyApiCostUsd);
    log.LogInformation("  Min edge:       {E:P0}",  config.MinEdge);
    log.LogInformation("── RISK ─────────────────────────────────────────────────────");
    var effectiveKelly = EffectiveKellyFraction(config);
    var effectiveMaxPosition = EffectiveMaxPositionPct(config);
    var effectiveMaxExposure = EffectiveMaxExposurePct(config);
    var effectiveDailyStop = EffectiveDailyStopLossPct(config);
    var effectiveMaxDrawdown = EffectiveMaxDrawdownPct(config);
    log.LogInformation("  Max position:   {P:P0}  kelly={K:F2}",  effectiveMaxPosition, effectiveKelly);
    log.LogInformation("  Max exposure:   {E:P0}  max_positions={N}",  effectiveMaxExposure, config.MaxConcurrentPositions);
    log.LogInformation("  Category cap:   {C:P0}",  config.MaxCategoryExposurePct);
    log.LogInformation("  Daily SL:       {D:P0}  max_drawdown={M:P0}",  effectiveDailyStop, effectiveMaxDrawdown);
    if (config.LiveTrading && !config.AllowUnsafeRisk &&
        (effectiveKelly != config.KellyFraction ||
         effectiveMaxPosition != config.MaxPositionPct ||
         effectiveMaxExposure != config.MaxTotalExposurePct ||
         effectiveDailyStop != config.DailyStopLossPct ||
         effectiveMaxDrawdown != config.MaxDrawdownPct))
    {
        log.LogInformation(
            "  Guardrails:     active (configured kelly={K:F2}, pos={P:P0}, exp={E:P0}, daily={D:P0}, dd={M:P0})",
            config.KellyFraction, config.MaxPositionPct, config.MaxTotalExposurePct,
            config.DailyStopLossPct, config.MaxDrawdownPct);
    }
    log.LogInformation("── SCAN ─────────────────────────────────────────────────────");
    log.LogInformation("  Interval:       {I} min  markets/cycle={M}",  config.ScanIntervalMinutes, config.MarketsPerCycle);
    log.LogInformation("  Min liquidity:  ${L:N0}  min_volume_24h=${V:N0}",  config.MinLiquidity, config.MinVolume24Hr);
    log.LogInformation("  Min price:      {P:P0}  max_spread={S:P0}",  config.MinMarketPrice, config.MaxSpread);
    log.LogInformation("  Min TTR:        {H}h",  config.MinTimeToResolutionHours);
    log.LogInformation("── EXITS ────────────────────────────────────────────────────");
    log.LogInformation("  Stop-loss:      {SL:P0}  take-profit={TP:P0}  edge_buf={EB:P0}",
        config.PositionStopLossPct, config.TakeProfitPrice, config.ExitEdgeBuffer);
    log.LogInformation("  Re-estimate:    threshold={T:P0}  size={N}",
        config.ReviewReestimateThresholdPct, config.ReviewEnsembleSize);
    log.LogInformation(new string('=', 60));
}

if (console_)
{
    Console.WriteLine($"\n{new string('=', 60)}");
    Console.WriteLine($"  POLYMARKET BOT (.NET) — {mode} MODE");
    Console.WriteLine($"  Bankroll: ${config.InitialBankroll:F2} | Min edge: {config.MinEdge:P0}");
    Console.WriteLine($"  Risk: {EffectiveMaxPositionPct(config):P0}/pos, {EffectiveMaxExposurePct(config):P0}/total, {EffectiveDailyStopLossPct(config):P0}/daily-SL");
    Console.WriteLine($"  Scan: every {config.ScanIntervalMinutes}min, {config.MarketsPerCycle} markets/cycle");
    Console.WriteLine($"{new string('=', 60)}\n");
}

var _providerKey = config.AiProvider.ToLowerInvariant() switch
{
    "openai"       => config.OpenAiApiKey,
    "gemini"       => config.GeminiApiKey,
    "openrouter"   => config.OpenRouterApiKey,
    "azure_openai" => config.AzureOpenAiApiKey,
    _              => config.AnthropicApiKey,
};
if (string.IsNullOrEmpty(_providerKey))
{
    log.LogError("API key for provider '{Provider}' is not set in config", config.AiProvider);
    return 1;
}

// ── Load state ──────────────────────────────────────────────────

var snapshot = PersistenceService.LoadSnapshot(config.DataDir);
var portfolio = new Portfolio(config, loggerFactory.CreateLogger<Portfolio>(), snapshot);

if (snapshot is not null)
{
    // Clear a stale IsHalted flag if portfolio value is still healthy.
    // The bankroll-depleted halt is transient (positions will return USDC),
    // so don't carry it across restarts when portfolio_value > $1.
    if (portfolio.IsHalted && portfolio.Bankroll + portfolio.TotalExposure() >= 1.0)
    {
        portfolio.IsHalted = false;
        log.LogInformation("Cleared stale IsHalted flag (portfolio value ${Pv:F2} is healthy)",
            portfolio.Bankroll + portfolio.TotalExposure());
    }
    log.LogInformation("Resumed from saved state: ${Bankroll:F2} bankroll, {Positions} positions",
        portfolio.Bankroll, portfolio.Positions.Count);
    Con($"RESUME: ${portfolio.Bankroll:F2} bankroll, {portfolio.Positions.Count} positions, ${portfolio.TotalExposure():F2} exposure");
}
else
{
    log.LogInformation("Starting fresh");
    Con($"START: fresh portfolio, ${portfolio.Bankroll:F2} bankroll");
    // Persist initial state immediately so portfolio.json exists from the start
    PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
}

// ── Services ────────────────────────────────────────────────────

using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

var scanner = new MarketScanner(config, httpClient, loggerFactory.CreateLogger<MarketScanner>());
var estimator = new Estimator(config, httpClient, loggerFactory.CreateLogger<Estimator>());
var notifier = new Notifier(config, loggerFactory.CreateLogger<Notifier>());

// ── Validate Anthropic API key ───────────────────────────────────

if (config.MultiProvider)
    log.LogInformation("Validating all configured providers...");
else
    log.LogInformation("Validating {Provider} API key...", config.AiProvider);

if (!await estimator.ValidateApiKeyAsync())
{
    if (config.MultiProvider)
    {
        log.LogError("All configured AI providers failed — no AI available. Exiting.");
        if (console_) Console.WriteLine($"[{Ts()}] {RED}ERROR: All AI providers failed. Check config.{RESET}");
    }
    else
    {
        log.LogError("{Provider} API key is invalid or unauthorized. Exiting.", config.AiProvider);
        if (console_) Console.WriteLine($"[{Ts()}] {RED}ERROR: {config.AiProvider} API key invalid. Check config.{RESET}");
    }
    return 1;
}

if (config.MultiProvider)
    log.LogInformation("Provider validation complete — at least one provider available.");
else
    log.LogInformation("{Provider} API key validated.", config.AiProvider);

// ── Graceful shutdown ───────────────────────────────────────────

using var cts = new CancellationTokenSource();

ITrader trader;
ClobApiClient? clobClient = null;
if (config.LiveTrading)
{
    if (string.IsNullOrEmpty(config.PolymarketPrivateKey) && string.IsNullOrEmpty(config.PolymarketApiKey))
    {
        log.LogError("POLYMARKET_PRIVATE_KEY or POLYMARKET_API_KEY required for live trading");
        return 1;
    }
    clobClient = new ClobApiClient(config, httpClient, loggerFactory.CreateLogger<ClobApiClient>());
    await clobClient.InitializeAsync(cts.Token);
    Con("CLOB API credentials initialized");

    // Ensure CTF conditional token approvals for exchange contracts (required for SELL orders)
    await clobClient.EnsureConditionalTokenApprovalsAsync(cts.Token);
    Con("CTF token approvals verified");
    var liveTrader = new LiveTrader(clobClient, loggerFactory.CreateLogger<LiveTrader>());
    trader = liveTrader;

    // Sync bankroll from actual on-chain balance
    var initBal = await clobClient.GetBalanceAsync(cts.Token);
    if (initBal is not null)
    {
        portfolio.SyncBalance(initBal.Value);
        log.LogInformation("Initial USDC balance: ${Balance:F2}", initBal.Value);
        Con($"BALANCE: ${initBal.Value:F2} (on-chain)");
    }
}
else
{
    trader = new PaperTrader();
}

notifier.NotifyStarted(mode, portfolio.Bankroll, portfolio.Positions.Count);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    log.LogInformation("Shutdown requested...");
    Con("SHUTDOWN requested (Ctrl+C)");
    cts.Cancel();
};

var lastDailyReset = DateTimeOffset.UtcNow.Date;
var cycle = 0;

// ── Main loop ───────────────────────────────────────────────────

while (!cts.Token.IsCancellationRequested)
{
    cycle++;

    if (portfolio.IsHalted)
    {
        log.LogWarning("Portfolio halted — stopping");
        Con($"{RED}HALTED: portfolio risk limit reached, stopping bot{RESET}");
        notifier.NotifyHalted("Risk limit reached", portfolio);
        break;
    }

    // Daily reset
    var today = DateTimeOffset.UtcNow.Date;
    if (today != lastDailyReset)
    {
        portfolio.ResetDaily();
        lastDailyReset = today;
        log.LogInformation("New day — daily start value reset to ${Bankroll:F2}", portfolio.Bankroll);
        Con($"NEW DAY: daily PnL reset, start=${portfolio.Bankroll:F2}");
        notifier.NotifyDailyReset(portfolio);
    }

    log.LogInformation("--- Cycle {Cycle} ---", cycle);
    estimator.ResetCycle();
    var cycleApiCostStart = portfolio.TotalApiCost;

    // Sync on-chain USDC balance at start of each cycle (live trading only)
    if (trader is LiveTrader ltSync)
    {
        var cycleBal = await ltSync.GetBalanceAsync(cts.Token);
        if (cycleBal is not null)
            portfolio.SyncBalance(cycleBal.Value);
    }

    {
        var pvLog = portfolio.Bankroll + portfolio.TotalExposure();
        log.LogInformation(
            "Portfolio: ${Value:F2} (bankroll=${Bankroll:F2} + exposure=${Exposure:F2}) | {Positions} positions",
            pvLog, portfolio.Bankroll, portfolio.TotalExposure(), portfolio.Positions.Count);
    }

    if (console_)
    {
        var pv = portfolio.Bankroll + portfolio.TotalExposure();
        Console.WriteLine($"\n{new string('\u2500', 60)}");
        Console.WriteLine($"[{Ts()}] CYCLE {cycle}");
        Console.WriteLine($"  Portfolio: ${pv:F2} (bankroll=${portfolio.Bankroll:F2} + exposure=${portfolio.TotalExposure():F2})");
        Console.WriteLine($"  Positions: {portfolio.Positions.Count} | API cost: ${portfolio.TotalApiCost:F4}");
        Console.WriteLine(new string('\u2500', 60));
    }

    // ── Position review phase ─────────────────────────────────
    if (config.EnablePositionReview && portfolio.Positions.Count > 0)
    {
        log.LogInformation("Reviewing {Count} open positions...", portfolio.Positions.Count);
        Con($"REVIEW: checking {portfolio.Positions.Count} positions...");

        var tokenIds = portfolio.Positions.Select(p => p.TokenId).ToList();
        var prices = await scanner.GetMarketPricesAsync(tokenIds, cts.Token);
        portfolio.UpdatePositionPrices(prices);

        // Ghost check: verify actual on-chain balances (live trading only)
        if (clobClient is not null && portfolio.Positions.Count > 0)
        {
            log.LogInformation("  Ghost check: verifying {Count} position balances...", portfolio.Positions.Count);
            var positionsToCheck = portfolio.Positions.ToList();
            foreach (var ghostPos in positionsToCheck)
            {
                var onChainBal = await clobClient.GetActualConditionalBalanceAsync(ghostPos.TokenId, cts.Token);
                if (onChainBal is not null && onChainBal.Value < 0.1)
                {
                    log.LogWarning("  GHOST: {Question} (tracked={Tracked:F2} tokens, on-chain={OnChain:F2})",
                        Truncate(ghostPos.Question, 50), ghostPos.Shares, onChainBal.Value);
                    if (console_) Con($"  {YELLOW}GHOST: {Truncate(ghostPos.Question, 50)}... (no tokens, ${ghostPos.SizeUsd:F2} lost){RESET}");

                    var ghostLoss = ghostPos.SizeUsd;
                    var ghostPnl = portfolio.RemoveGhostPosition(ghostPos.ConditionId);

                    var ghostTrade = new PolymarketBot.Models.Trade
                    {
                        TradeId = Guid.NewGuid().ToString(),
                        ConditionId = ghostPos.ConditionId,
                        Question = ghostPos.Question,
                        Side = ghostPos.Side,
                        Action = PolymarketBot.Models.TradeAction.SELL,
                        Price = 0.0,
                        SizeUsd = ghostLoss,
                        Shares = ghostPos.Shares,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                        IsPaper = false,
                        Rationale = "Ghost position: no on-chain tokens found",
                        ExitReason = "ghost",
                    };
                    PersistenceService.AppendTrade(ghostTrade, config.DataDir);
                    PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                    notifier.NotifyGhostRemoved(ghostPos, ghostLoss, portfolio);
                }
            }
        }

        // Tier 0: check for resolved markets
        // Include both unpriced tokens AND penny positions (CLOB often returns
        // residual sub-cent prices for resolved markets)
        var maybeResolved = portfolio.Positions
            .Where(p => !prices.ContainsKey(p.TokenId) ||
                        (prices.TryGetValue(p.TokenId, out var pr) && pr < 0.01))
            .ToList();
        var resolvedCount = 0;
        foreach (var pos in maybeResolved)
        {
            var resolution = await scanner.CheckMarketResolutionAsync(pos.ConditionId, cts.Token);
            if (resolution is null) continue;

            var won = pos.Side.ToString() == resolution["winning_side"];

            // Auto-claim winning positions on-chain.
            // Never blocks resolution accounting — if the tx fails, balance sync
            // on the next cycle will still pick up the USDC once claimed manually.
            if (won && clobClient is not null && config.AutoClaim)
            {
                Con($"  CLAIM: submitting on-chain redemption for {Truncate(pos.Question, 45)}...");
                var txHash = await clobClient.RedeemWinningPositionAsync(
                    pos.ConditionId, pos.Side.ToString(), cts.Token);
                if (txHash is not null)
                {
                    log.LogInformation("  Auto-claim tx submitted: {Hash}", txHash);
                    Con($"  CLAIM tx: {txHash[..Math.Min(txHash.Length, 22)]}...");
                }
                else
                {
                    Con($"  {YELLOW}CLAIM FAILED — claim manually at polymarket.com{RESET}");
                }
            }

            var pnl = portfolio.ResolvePosition(pos.ConditionId, won);
            var result = won ? "WON" : "LOST";
            var payoutAmt = won ? pos.Shares : 0.0;
            resolvedCount++;

            log.LogInformation("  RESOLVED ({Result}): {Question} payout=${Payout:F2}, PnL=${Pnl:+0.00;-0.00}",
                result, Truncate(pos.Question, 50), payoutAmt, pnl);
            if (console_)
            {
                var color = won ? GREEN : RED;
                Con($"  {color}RESOLVED ({result}): {Truncate(pos.Question, 50)}... PnL=${pnl:+0.00;-0.00}{RESET}");
            }

            var resolveTrade = new PolymarketBot.Models.Trade
            {
                TradeId = Guid.NewGuid().ToString(),
                ConditionId = pos.ConditionId,
                Question = pos.Question,
                Side = pos.Side,
                Action = PolymarketBot.Models.TradeAction.SELL,
                Price = won ? 1.0 : 0.0,
                SizeUsd = pos.SizeUsd,
                Shares = pos.Shares,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                IsPaper = !config.LiveTrading,
                Rationale = $"Market resolved: {result}",
                ExitReason = $"resolved_{result.ToLowerInvariant()}",
            };
            PersistenceService.AppendTrade(resolveTrade, config.DataDir);
            PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
            notifier.NotifyResolved(pos, won, pnl, portfolio);
        }

        if (resolvedCount > 0)
        {
            log.LogInformation("  {Count} market(s) resolved", resolvedCount);
            Con($"  {resolvedCount} market(s) resolved, bankroll now ${portfolio.Bankroll:F2}");
        }

        var pennyCount = portfolio.Positions.Count(p => p.CurrentPrice < 0.01);
        var tinyCount = portfolio.Positions.Count(p => p.CurrentPrice >= 0.01 && p.Shares < 5.0);
        var exitSignals = portfolio.GenerateExitSignals();
        var exitsThisCycle = 0;

        {
            var skipParts = new List<string>();
            if (pennyCount > 0) skipParts.Add($"{pennyCount} penny (price<$0.01)");
            if (tinyCount > 0) skipParts.Add($"{tinyCount} tiny (<5 tokens)");
            if (skipParts.Count > 0)
            {
                var skipMsg = string.Join(", ", skipParts);
                log.LogInformation("  Skipping unsellable: {Msg}", skipMsg);
                Con($"  {YELLOW}SKIP unsellable: {skipMsg}{RESET}");
            }
        }

        if (exitSignals.Count > 0)
        {
            log.LogInformation("  Found {Count} exit signals", exitSignals.Count);
            Con($"  Found {exitSignals.Count} exit signal(s)");
        }
        else
        {
            log.LogInformation("  No exit signals — all positions OK");
            Con($"  {GREEN}All positions OK, no exits needed{RESET}");
        }

        foreach (var es in exitSignals)
        {
            if (cts.Token.IsCancellationRequested || portfolio.IsHalted)
                break;

            if (es.ExitReason == "stop_loss" && config.StopLossRequiresNegativeEdge)
            {
                var reviewMarket = BuildReviewMarket(es.Position);
                log.LogInformation("  STOP-LOSS CHECK: re-estimating {Question} before selling",
                    Truncate(es.Position.Question, 50));
                var stopEstimate = await estimator.EstimateAsync(reviewMarket, cts.Token);
                if (stopEstimate is not null)
                {
                    portfolio.RecordApiCost(stopEstimate.InputTokensUsed, stopEstimate.OutputTokensUsed);
                    es.Position.FairEstimateAtEntry = stopEstimate.FairProbability;
                    var fairForSide = es.Position.Side == Side.YES
                        ? stopEstimate.FairProbability
                        : 1.0 - stopEstimate.FairProbability;
                    var remainingEdge = fairForSide - es.CurrentPrice;
                    if (remainingEdge > config.ExitEdgeBuffer)
                    {
                        log.LogInformation(
                            "  HOLD stop-loss: {Question} still has edge {Edge:P1} after re-estimate",
                            Truncate(es.Position.Question, 50), remainingEdge);
                        Con($"  HOLD stop-loss: {Truncate(es.Position.Question, 45)} edge still {remainingEdge:P1}");
                        PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                        continue;
                    }
                }
                else
                {
                    log.LogWarning("  STOP-LOSS CHECK failed: selling with rule-based stop-loss fallback");
                }
            }

            log.LogInformation(
                "  EXIT {Reason}: {Question} entry={Entry:F4} -> {Current:F4} (PnL={Pnl:+0.0%;-0.0%})",
                es.ExitReason, Truncate(es.Position.Question, 50), es.Position.EntryPrice, es.CurrentPrice, es.PnlPct);
            if (console_)
            {
                Con($"  EXIT ({es.ExitReason}): {Truncate(es.Position.Question, 50)}...");
                Con($"    {es.Position.EntryPrice:F4} -> {es.CurrentPrice:F4} PnL={es.PnlPct:+0.0%;-0.0%}");
            }

            var sellTrade = await trader.ExecuteSellAsync(es, portfolio, cts.Token);
            if (sellTrade is not null)
            {
                // Re-sync on-chain USDC balance after sell to correct any partial-fill
                // accounting drift (portfolio may have credited more than was actually received).
                if (trader is LiveTrader ltSell)
                {
                    var sellBal = await ltSell.GetBalanceAsync(cts.Token);
                    if (sellBal is not null)
                    {
                        portfolio.SyncBalance(sellBal.Value);
                        log.LogInformation("On-chain USDC after sell: ${Balance:F2}", sellBal.Value);
                        Con($"    USDC after sell: ${sellBal.Value:F2}");
                    }
                }

                PersistenceService.AppendTrade(sellTrade, config.DataDir);
                PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                exitsThisCycle++;
                notifier.NotifySell(sellTrade, es.ExitReason, es.PnlPct, portfolio);
                Con($"    {GREEN}SOLD OK{RESET}");
            }
            else
            {
                notifier.NotifySellFail(es.Position, es.ExitReason, "below CLOB minimum or order not filled");
                Con($"    {RED}SELL FAILED (min 5 tokens or order not filled){RESET}");
            }
        }

        // Tier 1.5: top-up-and-sell for tiny positions with exit signals
        var topupCandidates = portfolio.GenerateTopupCandidates();
        if (topupCandidates.Count > 0)
        {
            log.LogInformation("  Found {Count} topup candidate(s) (tiny positions with exit signals)", topupCandidates.Count);
            Con($"  Found {topupCandidates.Count} topup candidate(s) (buy 5 tokens -> sell all)");
        }

        foreach (var tc in topupCandidates)
        {
            if (cts.Token.IsCancellationRequested || portfolio.IsHalted)
                break;

            if (tc.TopupCost > portfolio.Bankroll)
            {
                log.LogInformation(
                    "  SKIP topup: {Question} cost=${Cost:F2} > bankroll=${Bankroll:F2}",
                    Truncate(tc.Position.Question, 40), tc.TopupCost, portfolio.Bankroll);
                Con($"  {YELLOW}SKIP topup: can't afford ${tc.TopupCost:F2} (bankroll=${portfolio.Bankroll:F2}){RESET}");
                continue;
            }

            log.LogInformation(
                "  TOPUP+SELL ({Reason}): {Question} {Shares:F2} tokens, buy 5 more @ {Price:F4} (cost=${Cost:F2}, recover=${Recovery:F2})",
                tc.ExitReason, Truncate(tc.Position.Question, 40), tc.Position.Shares,
                tc.Position.CurrentPrice, tc.TopupCost, tc.RecoveryValue);
            if (console_)
            {
                Con($"  TOPUP ({tc.ExitReason}): {Truncate(tc.Position.Question, 40)}...");
                Con($"    {tc.Position.Shares:F2} tokens + buy 5 @ {tc.Position.CurrentPrice:F4} (cost=${tc.TopupCost:F2})");
            }

            var topupTrade = await trader.ExecuteTopupAndSellAsync(tc, portfolio, cts.Token);
            if (topupTrade is not null)
            {
                if (trader is LiveTrader ltTopup)
                {
                    var topupBal = await ltTopup.GetBalanceAsync(cts.Token);
                    if (topupBal is not null)
                    {
                        portfolio.SyncBalance(topupBal.Value);
                        log.LogInformation("On-chain USDC after topup+sell: ${Balance:F2}", topupBal.Value);
                    }
                }

                PersistenceService.AppendTrade(topupTrade, config.DataDir);
                PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                exitsThisCycle++;
                notifier.NotifyTopupSell(topupTrade, tc, portfolio);
                Con($"    {GREEN}TOPUP+SELL OK (freed ${tc.RecoveryValue:F2}){RESET}");
            }
            else
            {
                notifier.NotifyTopupSellFail(tc, "order not filled");
                Con($"    {RED}TOPUP+SELL FAILED{RESET}");
            }
        }

        Con($"REVIEW: {exitsThisCycle} exits, bankroll=${portfolio.Bankroll:F2}, {portfolio.Positions.Count} positions remaining");
    }

    try
    {
        // Skip market scan entirely if bankroll can't fund the smallest possible
        // trade. Saves the ~15s Gamma API call when no trade is possible.
        var minPosPre = config.MaxPositionPct * portfolio.Bankroll;
        var minRequired = Math.Max(minPosPre, config.MinTradeUsd);
        var tradesThisCycle = 0;

        if (portfolio.Bankroll < minRequired)
        {
            log.LogInformation(
                "Bankroll ${Bankroll:F2} too low to trade (min ~${Min:F2}) — skipping scan",
                portfolio.Bankroll, minRequired);
            Con($"SCAN SKIP: bankroll ${portfolio.Bankroll:F2} < min ${minRequired:F2}");
        }
        else
        {

        log.LogInformation("Scanning markets...");
        Con("SCAN: fetching markets...");
        var markets = await scanner.ScanAsync(cts.Token);
        var eligible = markets.Take(config.MarketsPerCycle).ToList();

        Con($"SCAN: {markets.Count} total, evaluating top {eligible.Count}");

        // Pre-check: skip estimation entirely if exposure is at the limit
        // Use bankroll (free cash) as base, not portfolio value, to avoid false blocks
        // when most capital is locked in positions (matches documented behaviour).
        var pv = portfolio.Bankroll + portfolio.TotalExposure();
        var exposureRoom = config.MaxTotalExposurePct * pv - portfolio.TotalExposure();
        var minRealisticPosition = Math.Max(config.MinTradeUsd, config.MaxPositionPct * portfolio.Bankroll);
        // Also can't trade more than available cash
        exposureRoom = Math.Min(exposureRoom, portfolio.Bankroll);
        var atCapacity = exposureRoom < minRealisticPosition;
        if (atCapacity)
        {
            log.LogInformation(
                "Exposure near limit: room=${Room:F2} < min realistic position=${MinPos:F2} — skipping estimation to save API costs",
                exposureRoom, minRealisticPosition);
            Con($"EXPOSURE FULL: room=${exposureRoom:F2} < ${minRealisticPosition:F2}, skipping evaluations");
        }

        for (var i = 0; i < eligible.Count; i++)
        {
            var market = eligible[i];
            var idx = $"[{i + 1,2}/{eligible.Count}]";

            if (cts.Token.IsCancellationRequested || portfolio.IsHalted)
                break;

            if (portfolio.HasPosition(market.ConditionId))
            {
                log.LogInformation("  {Idx} SKIP (already held): {Question}", idx, Truncate(market.Question, 60));
                Con($"  {idx} SKIP (held): {Truncate(market.Question, 55)}");
                continue;
            }

            // Skip estimation entirely if at exposure limit (saves API costs)
            if (atCapacity)
                continue;

            var cycleApiCost = portfolio.TotalApiCost - cycleApiCostStart;
            var cycleApiBudget = config.MaxCycleApiCostUsd;
            var dailyApiBudget = config.MaxDailyApiCostUsd;
            if (cycleApiCost >= cycleApiBudget)
            {
                log.LogInformation(
                    "  API cycle budget reached (${Cost:F4} >= ${Budget:F4}) — skipping remaining evaluations",
                    cycleApiCost, cycleApiBudget);
                Con($"  API BUDGET: cycle ${cycleApiCost:F4}/${cycleApiBudget:F4}, skipping remaining evaluations");
                break;
            }
            if (portfolio.TotalApiCost >= dailyApiBudget)
            {
                log.LogInformation(
                    "  API daily budget reached (${Cost:F4} >= ${Budget:F4}) — skipping remaining evaluations",
                    portfolio.TotalApiCost, dailyApiBudget);
                Con($"  API BUDGET: daily ${portfolio.TotalApiCost:F4}/${dailyApiBudget:F4}, skipping remaining evaluations");
                break;
            }

            // Skip estimation if we can't afford the CLOB minimum for either side.
            // CLOB enforces: 5 tokens minimum. Use aggressive price (+ 0.02 for 2-tick BUY aggression)
            // so we don't call Claude only to fail at order execution.
            var bestPrice = Math.Min(market.OutcomeYesPrice, market.OutcomeNoPrice);
            var minClobCost = Math.Max(5.0 * Math.Min(bestPrice + config.EntryPriceBuffer, 0.99), 1.0);
            if (portfolio.Bankroll < minClobCost)
            {
                log.LogInformation(
                    "  {Idx} SKIP (can't afford CLOB min ${Min:F2}): {Question}",
                    idx, minClobCost, Truncate(market.Question, 50));
                Con($"  {idx} SKIP (need ${minClobCost:F2} for 5 tokens, have ${portfolio.Bankroll:F2})");
                continue;
            }

            // Estimate fair value
            log.LogInformation("  {Idx} Evaluating: {Question}...", idx, Truncate(market.Question, 60));
            Con($"  {idx} EVAL: {Truncate(market.Question, 55)}...");
            var estimate = await estimator.EstimateAsync(market, cts.Token);
            if (estimate is null)
            {
                log.LogInformation("  {Idx} SKIP (estimation failed)", idx);
                Con($"  {idx} -> {RED}FAILED{RESET}");
                continue;
            }

            // Agent pays for inference
            portfolio.RecordApiCost(estimate.InputTokensUsed, estimate.OutputTokensUsed);

            // Only halt if total portfolio value (not just free USDC) is depleted
            if (portfolio.Bankroll + portfolio.TotalExposure() < 1.0)
            {
                log.LogWarning("Portfolio value < $1 — agent is dead");
                Con($"{RED}DEAD: portfolio value depleted{RESET}");
                portfolio.IsHalted = true;
                notifier.NotifyHalted("Portfolio value < $1 — agent is dead", portfolio);
                break;
            }

            // Generate signal
            var signal = portfolio.GenerateSignal(market, estimate);
            if (signal is null)
            {
                var (yesEdge, noEdge) = CalculateNetEdges(market, estimate.FairProbability, config.EntryPriceBuffer);
                var bestEdge = Math.Max(yesEdge, noEdge);

                if (bestEdge > config.MinEdge)
                {
                    // Edge exists but Kelly size is below 5-token CLOB minimum or MinTradeUsd
                    var tokenPrice = yesEdge >= noEdge
                        ? Math.Min(market.OutcomeYesPrice + config.EntryPriceBuffer, 0.99)
                        : Math.Min(market.OutcomeNoPrice + config.EntryPriceBuffer, 0.99);
                    var clobMin = Math.Round(5.0 * tokenPrice, 2);
                    var kellySide = yesEdge >= noEdge ? "YES" : "NO";
                    log.LogInformation(
                        "  {Idx} SKIP (Kelly size too small for {Side} edge={Edge:+0.0%}): need ${ClobMin:F2} min, bankroll=${Bankroll:F2}",
                        idx, kellySide, bestEdge, clobMin, portfolio.Bankroll);
                    Con($"  {idx} -> {estimate.FairProbability:P0} (edge={bestEdge:+0.0%}) {YELLOW}TOO SMALL: need ${clobMin:F2} min, have ${portfolio.Bankroll:F2}{RESET}");
                }
                else
                {
                    log.LogInformation(
                        "  {Idx} SKIP (no net edge): fair={Fair:P1} vs market={Market:P1} (net_edge={Edge:+0.0%;-0.0%}, need>{Min:P0})",
                        idx, estimate.FairProbability, market.OutcomeYesPrice, bestEdge, config.MinEdge);
                    Con($"  {idx} -> {estimate.FairProbability:P0} (edge={bestEdge:+0.0%;-0.0%}) SKIP");
                }
                continue;
            }

            // Risk check
            if (!portfolio.CheckRisk(signal))
            {
                log.LogInformation(
                    "  {Idx} SKIP (risk limit): {Side} {Question} ${Size:F2}",
                    idx, signal.Side, Truncate(market.Question, 40), signal.PositionSizeUsd);
                Con($"  {idx} -> {estimate.FairProbability:P0} {YELLOW}RISK BLOCKED{RESET}");
                continue;
            }

            // Execute
            log.LogInformation(
                "  {Idx} >>> BUYING {Side} {Question} ${Size:F2} @ {Price:F3} (exec~{Exec:F3})",
                idx, signal.Side, Truncate(market.Question, 50), signal.PositionSizeUsd,
                signal.MarketPrice, signal.ExecutionPrice);
            if (console_)
            {
                Con($"  {idx} -> {estimate.FairProbability:P0} edge={signal.Edge:P1}");
                Con($"  {idx} >>> BUY {signal.Side} ${signal.PositionSizeUsd:F2} @ ~{signal.ExecutionPrice:F3}...");
            }

            var trade = await trader.ExecuteAsync(signal, portfolio, cts.Token);
            if (trade is not null)
            {
                // After a BUY, only sync DOWN if CLOB shows less than expected.
                // Don't sync UP — CLOB balance lags behind unsettled trades and
                // would undo the portfolio's correct internal deduction, causing overspend.
                if (trader is LiveTrader lt)
                {
                    var bal = await lt.GetBalanceAsync(cts.Token);
                    if (bal is not null)
                    {
                        Con($"  USDC balance: ${bal.Value:F2}");
                        if (bal.Value < portfolio.Bankroll - 0.001)
                        {
                            portfolio.SyncBalance(bal.Value);
                            log.LogInformation("On-chain USDC after trade: ${Balance:F2} (synced down)", bal.Value);
                        }
                        else
                        {
                            log.LogDebug("On-chain USDC after trade: ${Balance:F2} (skipping upward sync — CLOB lag)", bal.Value);
                        }
                    }
                }

                PersistenceService.AppendTrade(trade, config.DataDir);
                PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                tradesThisCycle++;
                notifier.NotifyTrade(trade, signal, portfolio);

                log.LogInformation(
                    "  {Idx} TRADE OK: {Side} {Question} ${Size:F2} @ {Price:F3} (edge={Edge:P1}, EV=${EV:F2})",
                    idx, trade.Side, Truncate(market.Question, 50), trade.SizeUsd, trade.Price,
                    signal.Edge, signal.ExpectedValue);

                Con($"  {idx} {GREEN}TRADE OK{RESET} (EV=${signal.ExpectedValue:F2})");
            }
            else
            {
                log.LogWarning("  {Idx} TRADE FAILED: order execution error", idx);
                notifier.NotifyBuyFail(market, signal, "order execution error");
                Con($"  {idx} {RED}TRADE FAILED{RESET}");
            }
        }

        } // end else (scan block)

        // Cycle summary
        log.LogInformation(
            "Cycle {Cycle}: {Trades} trades | Bankroll: ${Bankroll:F2} | Positions: {Positions} | " +
            "Exposure: ${Exposure:F2} | API cost: ${ApiCost:F4} | Realized PnL: ${PnL:+0.00;-0.00}",
            cycle, tradesThisCycle, portfolio.Bankroll, portfolio.Positions.Count,
            portfolio.TotalExposure(), portfolio.TotalApiCost, portfolio.TotalRealizedPnl);

        if (console_)
        {
            var pvSummary = portfolio.Bankroll + portfolio.TotalExposure();
            Console.WriteLine($"\n[{Ts()}] SUMMARY: {tradesThisCycle} trades this cycle");
            Console.WriteLine($"  Portfolio: ${pvSummary:F2} | Bankroll: ${portfolio.Bankroll:F2} | Exposure: ${portfolio.TotalExposure():F2}");
            Console.WriteLine($"  Positions: {portfolio.Positions.Count} | API cost: ${portfolio.TotalApiCost:F4} | PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}");
        }

        PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
    }
    catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
    {
        break;
    }
    catch (OperationCanceledException oce)
    {
        log.LogWarning(oce, "Cycle {Cycle} cancelled (network timeout?) — continuing", cycle);
        Con($"{RED}TIMEOUT: {oce.Message} — retrying next cycle{RESET}");
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Cycle {Cycle} error", cycle);
        Con($"{RED}ERROR: {ex.Message}{RESET}");
        notifier.NotifyError(cycle, ex);
    }

    // Sleep in 1-second ticks for responsive shutdown
    if (!cts.Token.IsCancellationRequested)
    {
        log.LogInformation("Next scan in {Interval} min", config.ScanIntervalMinutes);
        Con($"WAIT: sleeping {config.ScanIntervalMinutes} min...");
        for (var i = 0; i < config.ScanIntervalMinutes * 60; i++)
        {
            if (cts.Token.IsCancellationRequested) break;
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
        }
    }
}

// ── Final save ──────────────────────────────────────────────────

PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
notifier.NotifyStopped(portfolio);
log.LogInformation(
    "Bot stopped | Final bankroll: ${Bankroll:F2} | Total trades: {Trades} | " +
    "Total API cost: ${ApiCost:F4} | Realized PnL: ${PnL:+0.00;-0.00}",
    portfolio.Bankroll, portfolio.TotalTrades, portfolio.TotalApiCost, portfolio.TotalRealizedPnl);

if (console_)
{
    var pv = portfolio.Bankroll + portfolio.TotalExposure();
    Console.WriteLine($"\n{new string('=', 60)}");
    Console.WriteLine($"[{Ts()}] BOT STOPPED");
    Console.WriteLine($"  Final portfolio: ${pv:F2} | Bankroll: ${portfolio.Bankroll:F2}");
    Console.WriteLine($"  Total trades: {portfolio.TotalTrades} | API cost: ${portfolio.TotalApiCost:F4}");
    Console.WriteLine($"  Realized PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}");
    Console.WriteLine(new string('=', 60));
}

return 0;

static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";

static double EffectiveKellyFraction(BotConfig config) =>
    config.LiveTrading && !config.AllowUnsafeRisk ? Math.Min(config.KellyFraction, 0.50) : config.KellyFraction;

static double EffectiveMaxPositionPct(BotConfig config) =>
    config.LiveTrading && !config.AllowUnsafeRisk ? Math.Min(config.MaxPositionPct, 0.15) : config.MaxPositionPct;

static double EffectiveMaxExposurePct(BotConfig config) =>
    config.LiveTrading && !config.AllowUnsafeRisk ? Math.Min(config.MaxTotalExposurePct, 0.90) : config.MaxTotalExposurePct;

static double EffectiveDailyStopLossPct(BotConfig config) =>
    config.LiveTrading && !config.AllowUnsafeRisk ? Math.Min(config.DailyStopLossPct, 0.25) : config.DailyStopLossPct;

static double EffectiveMaxDrawdownPct(BotConfig config) =>
    config.LiveTrading && !config.AllowUnsafeRisk ? Math.Min(config.MaxDrawdownPct, 0.60) : config.MaxDrawdownPct;

static (double YesEdge, double NoEdge) CalculateNetEdges(MarketInfo market, double fairProbability, double entryBuffer)
{
    var yesExecutionPrice = Math.Min(market.OutcomeYesPrice + entryBuffer, 0.99);
    var noExecutionPrice = Math.Min(market.OutcomeNoPrice + entryBuffer, 0.99);
    return (
        fairProbability - yesExecutionPrice,
        (1.0 - fairProbability) - noExecutionPrice
    );
}

static MarketInfo BuildReviewMarket(Position pos)
{
    var yesPrice = pos.Side == Side.YES ? pos.CurrentPrice : 1.0 - pos.CurrentPrice;
    var noPrice = pos.Side == Side.NO ? pos.CurrentPrice : 1.0 - pos.CurrentPrice;
    yesPrice = Math.Clamp(yesPrice, 0.01, 0.99);
    noPrice = Math.Clamp(noPrice, 0.01, 0.99);

    return new MarketInfo
    {
        ConditionId = pos.ConditionId,
        Question = pos.Question,
        Slug = "",
        OutcomeYesPrice = yesPrice,
        OutcomeNoPrice = noPrice,
        TokenIdYes = pos.Side == Side.YES ? pos.TokenId : "",
        TokenIdNo = pos.Side == Side.NO ? pos.TokenId : "",
        Liquidity = 0,
        Volume = 0,
        Volume24Hr = 0,
        BestBid = 0,
        BestAsk = 0,
        Spread = 0,
        Category = pos.Category,
        EventTitle = pos.Question,
        Description = "Position review re-estimate before stop-loss exit.",
    };
}
