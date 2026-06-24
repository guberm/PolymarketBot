using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class MarketScanner
{
    private static readonly Dictionary<string, string[]> CategoryKeywords = new()
    {
        ["politics"] = ["president", "election", "congress", "senate", "governor", "vote", "party",
                        "democrat", "republican", "trump", "biden", "political", "inaugur",
                        "legislation", "supreme court", "cabinet", "impeach", "primary"],
        ["geopolitics"] = ["iran", "israel", "strike", "invade", "invasion", "war", "military",
                           "nato", "sanction", "nuclear", "missile", "ceasefire", "peace deal",
                           "china", "taiwan", "russia", "ukraine", "north korea", "tariff"],
        ["sports"] = ["nfl", "nba", "mlb", "nhl", "soccer", "football", "basketball", "baseball",
                      "tennis", "ufc", "fight", "championship", "super bowl", "world series",
                      "premier league", "match", "game", "serie a", "ncaa", "ligue 1",
                      "olympics", "medal", "la liga", "bundesliga", "win on 202", "rio open",
                      "open:", "grand slam"],
        ["crypto"] = ["bitcoin", "btc", "ethereum", "eth", "crypto", "solana", "sol", "token",
                      "defi", "blockchain", "coin", "memecoin", "fdv", "airdrop"],
        ["tech"] = ["ai model", "claude", "gpt", "openai", "anthropic", "google ai", "apple",
                    "microsoft", "tesla", "spacex", "launch", "release", "chip", "semiconductor"],
        ["social_media"] = ["tweet", "post", "elon musk", "follower", "subscriber", "tiktok",
                            "youtube", "instagram", "x.com"],
        ["weather"] = ["weather", "temperature", "hurricane", "storm", "rainfall", "snow", "climate"],
        ["entertainment"] = ["oscar", "grammy", "emmy", "movie", "film", "tv", "show", "album",
                             "music", "celebrity", "award", "box office"],
        ["finance"] = ["fed", "interest rate", "inflation", "gdp", "stock", "market", "s&p",
                       "nasdaq", "dow", "recession", "unemployment", "spx", "treasury"],
    };

    private readonly BotConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<MarketScanner> _log;
    private readonly string _baseUrl;
    private readonly string _clobHost;

    public MarketScanner(BotConfig config, HttpClient http, ILogger<MarketScanner> log)
    {
        _config = config;
        _http = http;
        _log = log;
        _baseUrl = config.GammaApiHost;
        _clobHost = config.ClobHost;
    }

    public async Task<List<MarketInfo>> ScanAsync(CancellationToken ct = default)
    {
        var rawEvents = await FetchAllEventsAsync(ct);
        var markets = new List<MarketInfo>();

        foreach (var evt in rawEvents)
        {
            var eventTitle = evt.GetPropertyOrDefault("title", "");
            var eventSlug = evt.GetPropertyOrDefault("slug", "");
            var description = evt.GetPropertyOrDefault("description", "");
            var category = Categorize(eventTitle, eventSlug);

            if (evt.TryGetProperty("markets", out var marketsEl) && marketsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var mkt in marketsEl.EnumerateArray())
                {
                    var parsed = ParseMarket(mkt, eventTitle, description, category);
                    if (parsed is not null)
                        markets.Add(parsed);
                }
            }
        }

        markets.Sort((a, b) => b.Volume24Hr.CompareTo(a.Volume24Hr));
        _log.LogInformation("Scan complete: {EventCount} events, {MarketCount} eligible markets",
            rawEvents.Count, markets.Count);
        return markets;
    }

    private async Task<List<JsonElement>> FetchAllEventsAsync(CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var offset = 0;
        const int limit = 100;

        while (true)
        {
            var page = await FetchEventsPageAsync(offset, limit, ct);
            if (page.Count == 0) break;
            all.AddRange(page);
            if (page.Count < limit) break;
            offset += limit;
        }

        return all;
    }

    private async Task<List<JsonElement>> FetchEventsPageAsync(int offset, int limit, CancellationToken ct)
    {
        var url = $"{_baseUrl}/events?active=true&closed=false&limit={limit}&offset={offset}";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resp = await _http.GetAsync(url, ct);

                if ((int)resp.StatusCode == 429)
                {
                    var wait = (int)Math.Pow(2, attempt);
                    _log.LogWarning("Rate limited on /events, retrying in {Wait}s", wait);
                    await Task.Delay(wait * 1000, ct);
                    continue;
                }

                if ((int)resp.StatusCode == 422 && offset > 0)
                {
                    _log.LogDebug("Reached end of /events pagination at offset {Offset}", offset);
                    return [];
                }

                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.EnumerateArray().ToList();

                return [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt < 2)
                {
                    var wait = (int)Math.Pow(2, attempt);
                    _log.LogWarning("Error fetching events (attempt {Attempt}): {Error}, retrying in {Wait}s",
                        attempt + 1, ex.Message, wait);
                    await Task.Delay(wait * 1000, ct);
                }
                else
                {
                    _log.LogError("Failed to fetch events after 3 attempts: {Error}", ex.Message);
                    return [];
                }
            }
        }

        return [];
    }

    private MarketInfo? ParseMarket(JsonElement mkt, string eventTitle, string description, string category)
    {
        try
        {
            if (!mkt.GetPropertyOrDefault("active", false) || mkt.GetPropertyOrDefault("closed", false))
                return null;

            var outcomes = ParseJsonStringOrArray(mkt, "outcomes");
            if (outcomes.Count != 2) return null;

            var prices = ParseJsonStringOrArray(mkt, "outcomePrices");
            if (prices.Count != 2) return null;

            var yesPrice = double.Parse(prices[0]);
            var noPrice = double.Parse(prices[1]);

            var tokens = ParseJsonStringOrArray(mkt, "clobTokenIds");
            if (tokens.Count != 2) return null;

            var liquidity = mkt.GetDoubleOrDefault("liquidity");
            var volume = mkt.GetDoubleOrDefault("volume");
            var volume24Hr = mkt.GetDoubleOrDefault("volume24hr");

            if (liquidity < _config.MinLiquidity) return null;
            if (volume24Hr < _config.MinVolume24Hr) return null;

            // Price filter — skip markets where neither side is in the tradeable range
            // Markets at extreme prices (e.g. YES=0.001, NO=0.999) have no FOK liquidity
            var minP = _config.MinMarketPrice;
            var maxP = 1.0 - minP;
            var yesInRange = yesPrice >= minP && yesPrice <= maxP;
            var noInRange = noPrice >= minP && noPrice <= maxP;
            if (!yesInRange && !noInRange)
                return null;

            var endDateStr = mkt.GetPropertyOrDefault("endDate", "");
            if (!string.IsNullOrEmpty(endDateStr))
            {
                if (DateTimeOffset.TryParse(endDateStr, out var endDate))
                {
                    var hoursLeft = (endDate - DateTimeOffset.UtcNow).TotalHours;
                    if (hoursLeft < _config.MinTimeToResolutionHours)
                        return null;
                }
            }

            var bestBid = mkt.GetDoubleOrDefault("bestBid");
            var bestAsk = mkt.GetDoubleOrDefault("bestAsk");
            var spread = bestAsk > bestBid ? bestAsk - bestBid : 0.0;

            // Filter wide spreads — indicates thin liquidity and poor fill quality
            if (bestBid > 0 && bestAsk > 0 && spread > _config.MaxSpread)
                return null;

            return new MarketInfo
            {
                ConditionId = mkt.GetPropertyOrDefault("conditionId", ""),
                Question = mkt.GetPropertyOrDefault("question", eventTitle),
                Slug = mkt.GetPropertyOrDefault("slug", ""),
                OutcomeYesPrice = yesPrice,
                OutcomeNoPrice = noPrice,
                TokenIdYes = tokens[0],
                TokenIdNo = tokens[1],
                Liquidity = liquidity,
                Volume = volume,
                Volume24Hr = volume24Hr,
                BestBid = bestBid,
                BestAsk = bestAsk,
                Spread = spread,
                EndDate = endDateStr,
                Category = category,
                EventTitle = eventTitle,
                Description = mkt.GetPropertyOrDefault("description", description),
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug("Failed to parse market: {Error}", ex.Message);
            return null;
        }
    }

    private static List<string> ParseJsonStringOrArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return [];

        if (el.ValueKind == JsonValueKind.Array)
            return el.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

        if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString() ?? "[]";
            var arr = JsonDocument.Parse(str).RootElement;
            if (arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Select(e => e.GetString() ?? e.GetRawText().Trim('"')).ToList();
        }

        return [];
    }

    private static string Categorize(string title, string slug)
    {
        var text = $"{title} {slug}".ToLowerInvariant();
        foreach (var (category, keywords) in CategoryKeywords)
        {
            if (keywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                return category;
        }
        return "other";
    }

    public async Task<Dictionary<string, string>?> CheckMarketResolutionAsync(
        string conditionId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_clobHost}/markets/{conditionId}";
            var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.GetPropertyOrDefault("closed", false))
                return null;

            // CLOB returns tokens array with winner flag
            if (root.TryGetProperty("tokens", out var tokensEl) && tokensEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var token in tokensEl.EnumerateArray())
                {
                    if (token.GetPropertyOrDefault("winner", false))
                    {
                        var outcome = token.GetPropertyOrDefault("outcome", "").ToUpperInvariant();
                        if (outcome == "YES")
                            return new Dictionary<string, string> { ["winning_side"] = "YES" };
                        if (outcome == "NO")
                            return new Dictionary<string, string> { ["winning_side"] = "NO" };
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug("Resolution check failed for {ConditionId}: {Error}",
                conditionId[..Math.Min(conditionId.Length, 20)], ex.Message);
            return null;
        }
    }

    public async Task<Dictionary<string, double>> GetMarketPricesAsync(
        IEnumerable<string> tokenIds, CancellationToken ct = default)
    {
        var prices = new Dictionary<string, double>();
        foreach (var tid in tokenIds)
        {
            var p = await GetMarketPriceAsync(tid, ct);
            if (p.HasValue && p.Value > 0)
                prices[tid] = p.Value;
        }
        return prices;
    }

    public async Task<double?> GetMarketPriceAsync(string tokenId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_clobHost}/midpoint?token_id={tokenId}";
            var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mid", out var mid))
            {
                if (mid.ValueKind == JsonValueKind.Number)
                    return mid.GetDouble();
                if (mid.ValueKind == JsonValueKind.String && double.TryParse(mid.GetString(), out var val))
                    return val;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

internal static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement el, string name, string defaultValue)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? defaultValue;
        return defaultValue;
    }

    public static bool GetPropertyOrDefault(this JsonElement el, string name, bool defaultValue)
    {
        if (el.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    public static double GetDoubleOrDefault(this JsonElement el, string name, double defaultValue = 0.0)
    {
        if (!el.TryGetProperty(name, out var prop)) return defaultValue;
        if (prop.ValueKind == JsonValueKind.Number) return prop.GetDouble();
        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var val)) return val;
        if (prop.ValueKind == JsonValueKind.Null) return defaultValue;
        return defaultValue;
    }
}
