using System.Text.Json;
using System.Text.Json.Serialization;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public static class PersistenceService
{
    private const string PortfolioFile = "portfolio.json";
    private const string TradesFile = "trades.jsonl";
    private const string EstimatesFile = "estimates.jsonl";
    private const string ResolutionWatchlistFile = "resolution-watchlist.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly JsonSerializerOptions JsonLineOpts = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void SaveSnapshot(PortfolioSnapshot snapshot, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, PortfolioFile);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static PortfolioSnapshot? LoadSnapshot(string dataDir)
    {
        var path = Path.Combine(dataDir, PortfolioFile);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PortfolioSnapshot>(json, JsonOpts);
    }

    public static void AppendTrade(Trade trade, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, TradesFile);
        var line = JsonSerializer.Serialize(trade, JsonLineOpts);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    public static void AppendEstimateEvaluation(
        MarketInfo market,
        Estimate estimate,
        Signal? signal,
        string provider,
        string decision,
        string reason,
        string dataDir,
        object? kalshiReference = null,
        bool trackWatch = true)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, EstimatesFile);
        var record = new
        {
            RecordType = "evaluation",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ConditionId = market.ConditionId,
            market.Question,
            market.Category,
            market.EventTitle,
            Provider = provider,
            FairProbability = estimate.FairProbability,
            RawEstimates = estimate.RawEstimates,
            estimate.Confidence,
            estimate.ApiCostUsd,
            estimate.DurationSeconds,
            estimate.ProviderEstimates,
            MarketYesPrice = market.OutcomeYesPrice,
            MarketNoPrice = market.OutcomeNoPrice,
            Side = signal?.Side.ToString() ?? "",
            ExecutionVwap = signal?.ExecutionPrice ?? 0,
            LimitPrice = signal?.LimitPrice ?? 0,
            QuoteAgeSeconds = signal?.QuoteAgeSeconds ?? 0,
            Edge = signal?.Edge ?? 0,
            PositionSizeUsd = signal?.PositionSizeUsd ?? 0,
            Decision = decision,
            Reason = reason,
            Kalshi = kalshiReference,
        };
        File.AppendAllText(path, JsonSerializer.Serialize(record, JsonLineOpts) + Environment.NewLine);
        if (trackWatch) TrackResolution(market, dataDir);
    }

    public static void AppendEstimateResolution(
        string conditionId, double actualOutcome, string dataDir, bool removeWatch = true)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, EstimatesFile);
        var record = new
        {
            RecordType = "resolution",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ConditionId = conditionId,
            ActualOutcome = actualOutcome,
        };
        File.AppendAllText(path, JsonSerializer.Serialize(record, JsonLineOpts) + Environment.NewLine);
        if (removeWatch) RemoveResolutionWatch(conditionId, dataDir);
    }

    public static List<string> GetResolutionCandidates(string dataDir, int limit)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return LoadResolutionWatchlist(dataDir).Values
            .Where(item => item.NextCheckAt <= now)
            .OrderBy(item => item.NextCheckAt)
            .Take(Math.Max(0, limit))
            .Select(item => item.ConditionId)
            .ToList();
    }

    public static void DeferResolutionCheck(string conditionId, string dataDir, double hours)
        => UpdateResolutionWatchlist([conditionId], [], dataDir, hours);

    public static void UpdateResolutionWatchlist(
        IEnumerable<string> deferIds, IEnumerable<string> removeIds, string dataDir, double hours)
    {
        var watch = LoadResolutionWatchlist(dataDir);
        var changed = false;
        var nextCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + Math.Max(.1, hours) * 3600;
        foreach (var conditionId in deferIds)
        {
            if (!watch.TryGetValue(conditionId, out var item)) continue;
            item.NextCheckAt = nextCheck;
            changed = true;
        }
        foreach (var conditionId in removeIds)
            changed = watch.Remove(conditionId) || changed;
        if (changed) SaveResolutionWatchlist(watch, dataDir);
    }

    private static void TrackResolution(MarketInfo market, string dataDir)
        => TrackResolutions([market], dataDir);

    public static void TrackResolutions(IEnumerable<MarketInfo> markets, string dataDir)
    {
        var watch = LoadResolutionWatchlist(dataDir);
        var changed = false;
        foreach (var market in markets)
        {
            if (watch.ContainsKey(market.ConditionId)) continue;
            var nextCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (DateTimeOffset.TryParse(market.EndDate, out var end))
                nextCheck = Math.Max(nextCheck, end.ToUnixTimeMilliseconds() / 1000.0);
            watch[market.ConditionId] = new ResolutionWatchItem
            {
                ConditionId = market.ConditionId,
                Question = market.Question,
                EndDate = market.EndDate,
                NextCheckAt = nextCheck,
            };
            changed = true;
        }
        if (changed) SaveResolutionWatchlist(watch, dataDir);
    }

    private static void RemoveResolutionWatch(string conditionId, string dataDir)
        => UpdateResolutionWatchlist([], [conditionId], dataDir, 0);

    private static Dictionary<string, ResolutionWatchItem> LoadResolutionWatchlist(string dataDir)
    {
        var path = Path.Combine(dataDir, ResolutionWatchlistFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, ResolutionWatchItem>>(File.ReadAllText(path), JsonOpts) ?? []
                : [];
        }
        catch (Exception) { return []; }
    }

    private static void SaveResolutionWatchlist(Dictionary<string, ResolutionWatchItem> watch, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, ResolutionWatchlistFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(watch, JsonOpts));
        File.Move(tmp, path, true);
    }

    private sealed class ResolutionWatchItem
    {
        public string ConditionId { get; init; } = "";
        public string Question { get; init; } = "";
        public string EndDate { get; init; } = "";
        public double NextCheckAt { get; set; }
    }
}
