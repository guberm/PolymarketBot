using Microsoft.Extensions.Logging;
using PolymarketBot;
using PolymarketBot.Models;
using PolymarketBot.Services;
using System.Net;
using System.Reflection;
using System.Text.Json;

static void Near(double expected, double actual, double tolerance = 1e-9)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new Exception($"Expected {expected}, got {actual}");
}

var buy = ExecutionPricing.CalculateBuy(
    [new BookLevel(0.50, 10), new BookLevel(0.40, 10)], 6.0);
if (!buy.Complete) throw new Exception("Expected complete BUY quote");
Near(14.0, buy.FilledQuantity);
Near(6.0 / 14.0, buy.Vwap);
Near(0.50, buy.WorstPrice);

var smtpPlanMethod = typeof(Notifier).GetMethod("ConnectionAttempts", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new Exception("SMTP fallback connection plan is missing");
var smtpPlan = (Array)(smtpPlanMethod.Invoke(null, [587, true])
    ?? throw new Exception("SMTP fallback connection plan returned null"));
if (smtpPlan.Length != 2) throw new Exception("SMTP 587 plan should include one fallback");
var smtpFallback = smtpPlan.GetValue(1)!;
if ((int)smtpFallback.GetType().GetField("Item1")!.GetValue(smtpFallback)! != 465 ||
    !(bool)smtpFallback.GetType().GetField("Item2")!.GetValue(smtpFallback)!)
    throw new Exception("SMTP fallback should use port 465 with implicit TLS");

var thin = ExecutionPricing.CalculateBuy([new BookLevel(0.40, 10)], 10.0);
if (thin.Complete) throw new Exception("Expected insufficient BUY depth");
Near(4.0, thin.FilledValue);

var sell = ExecutionPricing.CalculateSell(
    [new BookLevel(0.50, 10), new BookLevel(0.60, 10)], 15.0);
if (!sell.Complete) throw new Exception("Expected complete SELL quote");
Near(8.5, sell.FilledValue);
Near(8.5 / 15.0, sell.Vwap);
Near(0.50, sell.WorstPrice);

var buyShares = ExecutionPricing.CalculateBuyShares(
    [new BookLevel(0.50, 3), new BookLevel(0.60, 4)], 5);
if (!buyShares.Complete) throw new Exception("Expected complete token-denominated BUY quote");
Near(2.7, buyShares.FilledValue);
Near(0.54, buyShares.Vwap);
Near(0.60, buyShares.WorstPrice);

using var loggerFactory = LoggerFactory.Create(_ => { });
var config = new BotConfig
{
    InitialBankroll = 100.0,
    DailyStopLossPct = 0.20,
    MaxDrawdownPct = 0.90,
};
var portfolio = new Portfolio(config, loggerFactory.CreateLogger<Portfolio>());
portfolio.SyncBalance(50.0);
portfolio.Positions.Add(new Position
{
    ConditionId = "market-1",
    Question = "Test",
    Side = Side.YES,
    TokenId = "token-1",
    EntryPrice = 0.50,
    SizeUsd = 50.0,
    Shares = 100.0,
    CurrentPrice = 0.20,
    UnrealizedPnl = -30.0,
    Category = "test",
});
Near(20.0, portfolio.LiquidationValue());
Near(70.0, portfolio.Equity());
if (portfolio.CheckPortfolioRisk())
    throw new Exception("Expected daily stop-loss to use liquidation equity");

if (KalshiShadow.MatchScore(
        "Will Bitcoin exceed $150,000 in 2026?", "Bitcoin above $150,000 in 2026") <= 0.5)
    throw new Exception("Expected matching Kalshi market");
Near(0, KalshiShadow.MatchScore(
    "Will Bitcoin exceed $150,000 in 2026?", "Bitcoin above $100,000 in 2026"));

using (var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden_execution.json"))))
foreach (var vector in golden.RootElement.EnumerateArray())
{
    var levels = vector.GetProperty("levels").EnumerateArray()
        .Select(level => new BookLevel(level[0].GetDouble(), level[1].GetDouble())).ToList();
    var quote = vector.GetProperty("kind").GetString() == "buy_usd"
        ? ExecutionPricing.CalculateBuy(levels, vector.GetProperty("requested").GetDouble())
        : ExecutionPricing.CalculateSell(levels, vector.GetProperty("requested").GetDouble());
    if (quote.Complete != vector.GetProperty("complete").GetBoolean()) throw new Exception("Golden completeness mismatch");
    Near(vector.GetProperty("filled_quantity").GetDouble(), quote.FilledQuantity);
    Near(vector.GetProperty("filled_value").GetDouble(), quote.FilledValue);
    Near(vector.GetProperty("vwap").GetDouble(), quote.Vwap);
    Near(vector.GetProperty("worst_price").GetDouble(), quote.WorstPrice);
}

var partial = OrderReconciliation.Parse("""{"status":"live","size_matched":"4","price":"0.55"}""", "BUY", 0.6);
Near(4, partial.Shares);
Near(2.2, partial.Value);
Near(7000, RetryPolicy.DelayMilliseconds("7", 0));
Near(4000, RetryPolicy.DelayMilliseconds(null, 2));
Near(60000, RetryPolicy.DelayMilliseconds("999", 0));
Near(5, ApiPricing.Calculate("a=1/2,b=3/4", "b", 1_000_000, 500_000));
var calibrationWeights = CalibrationWeights.Calculate(new Dictionary<string, ProviderCalibrationStats>
{
    ["good"] = new(40, 2), ["bad"] = new(40, 12),
}, ["good", "bad"], 40, .25, .65);
if (calibrationWeights["good"] <= calibrationWeights["bad"] || calibrationWeights["good"] > .65)
    throw new Exception("Calibration weighting gate/shrink/cap failed");
Near(1, calibrationWeights.Values.Sum());
if (CalibrationWeights.Calculate(new Dictionary<string, ProviderCalibrationStats>
    { ["good"] = new(40, 2) }, ["good", "missing"], 40, .25, .65).Count != 0)
    throw new Exception("Calibration weighting should stay gated without enough samples");

var gracePortfolio = new Portfolio(new BotConfig { QuoteFailureGraceCycles = 3, StaleQuoteHaircutPct = .25 },
    loggerFactory.CreateLogger<Portfolio>());
gracePortfolio.Positions.Add(new Position { ConditionId = "g", Question = "g", Side = Side.YES,
    TokenId = "g", EntryPrice = .5, SizeUsd = 5, Shares = 10, CurrentPrice = .4, Category = "x" });
gracePortfolio.UpdatePositionQuotes(new Dictionary<string, ExecutionQuote>());
Near(.3, gracePortfolio.Positions[0].CurrentPrice);
gracePortfolio.UpdatePositionQuotes(new Dictionary<string, ExecutionQuote>());
gracePortfolio.UpdatePositionQuotes(new Dictionary<string, ExecutionQuote>());
Near(0, gracePortfolio.Positions[0].CurrentPrice);

var partialPortfolio = new Portfolio(new BotConfig { InitialBankroll = 10 }, loggerFactory.CreateLogger<Portfolio>());
partialPortfolio.Positions.Add(new Position { ConditionId = "p", Question = "p", Side = Side.YES,
    TokenId = "p", EntryPrice = .5, SizeUsd = 5, Shares = 10, CurrentPrice = .6, Category = "x" });
Near(.4, partialPortfolio.ReducePosition("p", 4, .6));
Near(12.4, partialPortfolio.Bankroll);
Near(6, partialPortfolio.Positions[0].Shares);
Near(3, partialPortfolio.Positions[0].SizeUsd);

var lockDir = Path.Combine(Path.GetTempPath(), $"polymarket-selftest-{Guid.NewGuid()}");
using (var first = new InstanceLock(lockDir))
using (var second = new InstanceLock(lockDir))
{
    if (!first.Acquire() || second.Acquire()) throw new Exception("Instance lock failed");
}
Directory.Delete(lockDir, true);

var watchDir = Path.Combine(Path.GetTempPath(), $"polymarket-watch-{Guid.NewGuid()}");
var watchMarket = new MarketInfo { ConditionId = "watch", Question = "watch", Slug = "",
    TokenIdYes = "y", TokenIdNo = "n", OutcomeYesPrice = .5, OutcomeNoPrice = .5,
    EndDate = "2020-01-01T00:00:00Z" };
var watchEstimate = new Estimate { MarketConditionId = "watch", Question = "watch",
    FairProbability = .5, RawEstimates = [.5] };
PersistenceService.AppendEstimateEvaluation(watchMarket, watchEstimate, null, "test", "skip", "test", watchDir);
if (!PersistenceService.GetResolutionCandidates(watchDir, 10).Contains("watch"))
    throw new Exception("Resolution watchlist failed");
PersistenceService.AppendEstimateResolution("watch", 1, watchDir);
if (PersistenceService.GetResolutionCandidates(watchDir, 10).Count != 0)
    throw new Exception("Resolution watchlist removal failed");
var secondWatchMarket = new MarketInfo { ConditionId = "watch-2", Question = "watch-2", Slug = "",
    TokenIdYes = "y", TokenIdNo = "n", OutcomeYesPrice = .5, OutcomeNoPrice = .5,
    EndDate = "2020-01-01T00:00:00Z" };
PersistenceService.TrackResolutions([watchMarket, secondWatchMarket], watchDir);
PersistenceService.UpdateResolutionWatchlist(["watch"], ["watch-2"], watchDir, 1);
if (PersistenceService.GetResolutionCandidates(watchDir, 10).Count != 0)
    throw new Exception("Batched resolution watchlist update failed");
Directory.Delete(watchDir, true);

var journalDir = Path.Combine(Path.GetTempPath(), $"polymarket-journal-{Guid.NewGuid()}");
var journal = new OrderJournal(journalDir);
var intentId = journal.Begin(new PendingOrderRecord { Kind = "BUY", ConditionId = "c", Side = "YES" });
journal.Submitted(intentId, "order-1");
journal.Filled(intentId, new OrderFill("MATCHED", 2, 1, .5));
var pendingOrders = new OrderJournal(journalDir).Pending();
if (pendingOrders.Count != 1 || pendingOrders[0].OrderId != "order-1")
    throw new Exception("Pending order journal failed");
Near(2, pendingOrders[0].FillShares);
var appliedPortfolio = new Portfolio(new BotConfig { InitialBankroll = 10 }, loggerFactory.CreateLogger<Portfolio>());
appliedPortfolio.MarkOrderApplied("order-1");
PersistenceService.SaveSnapshot(appliedPortfolio.Snapshot(), journalDir);
var resumedPortfolio = new Portfolio(new BotConfig { InitialBankroll = 10 }, loggerFactory.CreateLogger<Portfolio>(),
    PersistenceService.LoadSnapshot(journalDir));
if (!resumedPortfolio.HasAppliedOrder("order-1")) throw new Exception("Applied order id was not persisted");
journal.Complete(intentId);
if (journal.Pending().Count != 0) throw new Exception("Pending order completion failed");
Directory.Delete(journalDir, true);

using (var geoblockHttp = new HttpClient(new StaticResponseHandler(
    """{"blocked":true,"country":"US","region":"NY"}""")))
{
    var geoblock = await TradingSafety.CheckGeoblockAsync(geoblockHttp);
    if (!geoblock.Blocked || geoblock.Country != "US") throw new Exception("Geoblock startup check failed");
}
var rejectedJournalDir = Path.Combine(Path.GetTempPath(), $"polymarket-rejected-{Guid.NewGuid()}");
var rejectedJournal = new OrderJournal(rejectedJournalDir);
var rejectedIntent = rejectedJournal.Begin(new PendingOrderRecord { Kind = "BUY", ConditionId = "c", Side = "YES" });
try
{
    TradingSafety.HandleDefinitiveRejection(rejectedJournal, rejectedIntent,
        new ClobOrderRejectedException(System.Net.HttpStatusCode.Forbidden, "Trading restricted in your region"));
    throw new Exception("Forbidden order rejection should stop trading");
}
catch (TradingBlockedException) { }
if (rejectedJournal.Pending().Count != 0) throw new Exception("Rejected order intent was not removed");
var badRequestIntent = rejectedJournal.Begin(new PendingOrderRecord { Kind = "BUY", ConditionId = "c", Side = "YES" });
TradingSafety.HandleDefinitiveRejection(rejectedJournal, badRequestIntent,
    new ClobOrderRejectedException(System.Net.HttpStatusCode.BadRequest, "invalid order"));
if (rejectedJournal.Pending().Count != 0) throw new Exception("Definitive 400 intent was not removed");
Directory.Delete(rejectedJournalDir, true);

var clobCapture = new ClobV2CaptureHandler();
using (var clobHttp = new HttpClient(clobCapture))
{
    var clob = new ClobApiClient(new BotConfig
    {
        ClobHost = "https://clob.test",
        PolymarketPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80",
        PolymarketChainId = 137,
        PolymarketSignatureType = 1,
        PolymarketApiKey = "owner",
        PolymarketApiSecret = "dGVzdA==",
        PolymarketApiPassphrase = "pass",
        ExchangeAddress = "0xE111180000d2663C0091e4f400237545B87B996B",
        NegRiskExchangeAddress = "0xe2222d279d744050d28e00520010520000310F59",
    }, clobHttp, loggerFactory.CreateLogger<ClobApiClient>());
    if (await clob.PostMarketBuyOrderAsync("1234", 3, .5, CancellationToken.None) is null)
        throw new Exception("V2 order request was not posted");
}
using (var body = JsonDocument.Parse(clobCapture.OrderBody ?? throw new Exception("Order body was not captured")))
{
    var order = body.RootElement.GetProperty("order");
    foreach (var field in new[] { "timestamp", "metadata", "builder" })
        if (!order.TryGetProperty(field, out _)) throw new Exception($"V2 order field missing: {field}");
    foreach (var legacy in new[] { "taker", "nonce", "feeRateBps" })
        if (order.TryGetProperty(legacy, out _)) throw new Exception($"Legacy V1 order field remains: {legacy}");
    if (!body.RootElement.TryGetProperty("postOnly", out var postOnly) || postOnly.GetBoolean())
        throw new Exception("V2 postOnly=false missing");
    if (!body.RootElement.TryGetProperty("deferExec", out var deferExec) || deferExec.GetBoolean())
        throw new Exception("V2 deferExec=false missing");
    var signature = order.GetProperty("signature").GetString() ?? "";
    if (signature.Length != 132)
        throw new Exception("V2 POLY_PROXY signature is not a 65-byte EIP-712 signature");
}

var invalidJsonConfig = new BotConfig
{
    AiProvider = "openai",
    OpenAiApiKey = "test",
    EnsembleSize = 1,
    ApiPricing = "openai=1/2",
};
using var invalidJsonHttp = new HttpClient(new StaticResponseHandler(
    """{"choices":[{"message":{"content":"not json"}}],"usage":{"prompt_tokens":1000000,"completion_tokens":500000}}"""));
var invalidJsonEstimator = new Estimator(invalidJsonConfig, invalidJsonHttp,
    loggerFactory.CreateLogger<Estimator>());
if (await invalidJsonEstimator.EstimateAsync(watchMarket) is not null)
    throw new Exception("Invalid model JSON should not produce an estimate");
Near(2, invalidJsonEstimator.LastApiCostUsd);
var untrackedEstimator = new Estimator(new BotConfig
{
    AiProvider = "openai", OpenAiApiKey = "test", EnsembleSize = 1,
    ApiPricing = "openai=1/2", LlmCostTrackingEnabled = false,
}, new HttpClient(new StaticResponseHandler(
    """{"choices":[{"message":{"content":"not json"}}],"usage":{"prompt_tokens":1000000,"completion_tokens":500000}}""")),
    loggerFactory.CreateLogger<Estimator>());
if (await untrackedEstimator.EstimateAsync(watchMarket) is not null || untrackedEstimator.LastApiCostUsd != 0)
    throw new Exception("Disabled LLM cost tracking still recorded spend");

var concurrentHandler = new ConcurrentResponseHandler();
using var concurrentHttp = new HttpClient(concurrentHandler);
var concurrentEstimator = new Estimator(new BotConfig
{
    MultiProvider = true,
    EnsembleSize = 2,
    OpenAiApiKey = "test",
    GeminiApiKey = "test",
}, concurrentHttp, loggerFactory.CreateLogger<Estimator>());
if (await concurrentEstimator.EstimateAsync(watchMarket) is null)
    throw new Exception("Concurrent estimator returned no result");
if (concurrentHandler.MaxActive < 2)
    throw new Exception("Provider requests did not overlap");

var bookHandler = new BookResponseHandler();
using var bookHttp = new HttpClient(bookHandler);
var quoteScanner = new MarketScanner(new BotConfig { ClobHost = "https://clob.test" }, bookHttp,
    loggerFactory.CreateLogger<MarketScanner>());
var quotePositions = Enumerable.Range(0, 3).Select(i => new Position
{
    ConditionId = $"q{i}", Question = "q", Side = Side.YES, TokenId = $"t{i}", EntryPrice = .5,
    SizeUsd = 5, Shares = 10, CurrentPrice = .4, Category = "x",
}).ToList();
if ((await quoteScanner.GetSellQuotesAsync(quotePositions)).Count != 3 || bookHandler.MaxActive < 2)
    throw new Exception("Position quote requests did not overlap");
if ((await quoteScanner.CheckMarketResolutionsAsync(["a", "b", "c"])).Count != 3 || bookHandler.MaxActive < 2)
    throw new Exception("Resolution requests did not overlap");

var eventPortfolio = new Portfolio(new BotConfig
{
    InitialBankroll = 100, MaxEventExposurePct = .30,
    MaxCategoryExposurePct = 1, MaxTotalExposurePct = 1,
}, loggerFactory.CreateLogger<Portfolio>());
eventPortfolio.OpenPosition(new Position
{
    ConditionId = "held", Question = "held", Side = Side.YES, TokenId = "held",
    EntryPrice = .5, SizeUsd = 20, Shares = 40, CurrentPrice = .5,
    Category = "politics", EventTitle = "Election 2028",
});
var eventMarket = new MarketInfo
{
    ConditionId = "new", Question = "new", Slug = "", TokenIdYes = "yes", TokenIdNo = "no",
    OutcomeYesPrice = .5, OutcomeNoPrice = .5, Category = "politics", EventTitle = " election 2028 ",
};
var eventSignal = new Signal
{
    Market = eventMarket, Estimate = watchEstimate, Side = Side.YES, Edge = .2,
    MarketPrice = .5, ExecutionPrice = .5, KellyFraction = .1, PositionSizeUsd = 15, ExpectedValue = 3,
};
if (eventPortfolio.CheckRisk(eventSignal)) throw new Exception("Correlated event exposure was not blocked");
var unrelatedSignal = new Signal
{
    Market = new MarketInfo
    {
        ConditionId = "other", Question = "other", Slug = "", TokenIdYes = "yes", TokenIdNo = "no",
        OutcomeYesPrice = .5, OutcomeNoPrice = .5, Category = "politics", EventTitle = "Different event",
    },
    Estimate = watchEstimate, Side = Side.YES, Edge = .2, MarketPrice = .5,
    ExecutionPrice = .5, KellyFraction = .1, PositionSizeUsd = 15, ExpectedValue = 3,
};
if (!eventPortfolio.CheckRisk(unrelatedSignal)) throw new Exception("Different event was incorrectly blocked");

Console.WriteLine("Self-tests passed");

sealed class StaticResponseHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
}

sealed class ClobV2CaptureHandler : HttpMessageHandler
{
    public string? OrderBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/tick-size") return Json("{\"minimum_tick_size\":\"0.01\"}");
        if (path == "/neg-risk") return Json("{\"neg_risk\":false}");
        if (path == "/order")
        {
            OrderBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("{\"orderID\":\"order-v2\",\"status\":\"live\"}");
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

sealed class ConcurrentResponseHandler : HttpMessageHandler
{
    private int _active;
    public int MaxActive { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        MaxActive = Math.Max(MaxActive, active);
        await Task.Delay(40, cancellationToken);
        Interlocked.Decrement(ref _active);
        var body = request.RequestUri!.AbsoluteUri.Contains("generateContent")
            ? """{"candidates":[{"content":{"parts":[{"text":"{\"probability\":0.5}"}]}}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}"""
            : """{"choices":[{"message":{"content":"{\"probability\":0.5}"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}

sealed class BookResponseHandler : HttpMessageHandler
{
    private int _active;
    public int MaxActive { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        MaxActive = Math.Max(MaxActive, active);
        await Task.Delay(40, cancellationToken);
        Interlocked.Decrement(ref _active);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"bids":[{"price":"0.5","size":"100"}],"asks":[],"timestamp":"0"}"""),
        };
    }
}
