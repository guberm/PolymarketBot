using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed partial class KalshiShadow
{
    private readonly BotConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<KalshiShadow> _log;
    private readonly List<KalshiMarket> _markets = [];

    public KalshiShadow(BotConfig config, HttpClient http, ILogger<KalshiShadow> log)
    {
        _config = config;
        _http = http;
        _log = log;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _markets.Clear();
        try
        {
            var host = _config.KalshiApiHost.TrimEnd('/');
            using var response = await _http.GetAsync(
                $"{host}/markets?status=open&mve_filter=exclude&limit={Math.Clamp(_config.KalshiMarketsLimit, 1, 1000)}", ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in document.RootElement.GetProperty("markets").EnumerateArray())
            {
                var title = Text(item, "title");
                if (title.Length == 0 || Text(item, "mve_collection_ticker").Length > 0) continue;
                _markets.Add(new KalshiMarket(
                    Text(item, "ticker"), title, Text(item, "close_time"),
                    Text(item, "rules_primary"), Text(item, "rules_secondary"),
                    Number(item, "yes_bid_dollars"), Number(item, "yes_ask_dollars"),
                    Number(item, "liquidity_dollars")));
            }
            _log.LogInformation("Kalshi shadow: loaded {Count} open reference markets", _markets.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Kalshi shadow refresh failed");
        }
    }

    public async Task<KalshiLookupResult> FindReferenceAsync(
        MarketInfo market, Estimator estimator, CancellationToken ct = default)
    {
        KalshiMarket? best = null;
        var bestScore = 0.0;
        foreach (var candidate in _markets)
        {
            var score = MatchScore(market.Question, candidate.Title);
            if (score <= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        if (best is null || bestScore < _config.KalshiMinMatchScore)
            return new(null, 0);

        var verification = await estimator.VerifyMarketEquivalenceAsync(market, best, ct);
        var verifiedProbability = verification?.Probability;
        return new(new KalshiReference(
            best.Ticker, best.Title, best.CloseTime, best.YesBid, best.YesAsk, best.Liquidity,
            bestScore, verifiedProbability,
            verifiedProbability is double probability && probability >= _config.KalshiLlmSameThreshold,
            verifiedProbability is not null), verification?.ApiCostUsd ?? 0);
    }

    public static double MatchScore(string left, string right)
    {
        var (leftTokens, leftNumbers) = Tokens(left);
        var (rightTokens, rightNumbers) = Tokens(right);
        if (!leftNumbers.SetEquals(rightNumbers)) return 0;
        var union = new HashSet<string>(leftTokens);
        union.UnionWith(rightTokens);
        if (union.Count == 0) return 0;
        var intersection = new HashSet<string>(leftTokens);
        intersection.IntersectWith(rightTokens);
        return (double)intersection.Count / union.Count;
    }

    private static (HashSet<string> Words, HashSet<string> Numbers) Tokens(string value)
    {
        var numbers = NumberRegex().Matches(value.ToLowerInvariant())
            .Select(match => match.Value.Replace(",", ""))
            .ToHashSet();
        var withoutNumbers = NumberRegex().Replace(value.ToLowerInvariant(), " ");
        var words = WordRegex().Matches(withoutNumbers).Select(match => match.Value).ToHashSet();
        words.UnionWith(numbers);
        return (words, numbers);
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static double Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number : 0;
    }

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordRegex();
}

public sealed record KalshiMarket(
    string Ticker, string Title, string CloseTime, string RulesPrimary, string RulesSecondary,
    double YesBid, double YesAsk, double Liquidity);

public sealed record KalshiReference(
    string Ticker, string Title, string CloseTime, double YesBid, double YesAsk, double Liquidity,
    double MatchScore, double? LlmProbability, bool SameMarket, bool Verified);

public readonly record struct KalshiLookupResult(object? Reference, double ApiCostUsd);
