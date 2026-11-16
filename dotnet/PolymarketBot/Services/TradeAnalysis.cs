using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

/// <summary>
/// Post-hoc audit of WHY the ensemble disagrees with the market, logged at BUY
/// time. Classifies each provider estimate against the market price:
///   ECHO       — within the band of the market price (carries no information,
///                it repeats the price its prompt anchored it to);
///   INDEPENDENT — differs from the market (a real opinion);
///   UNSTABLE   — provider's own recent estimates for this market swing widely,
///                so a trimmed mean can be an artifact of one volatile draw.
/// When the edge is carried by independent voices while echoes confirm a deep
/// market, the likeliest explanation is a knowledge gap (recent events the
/// models do not know) — not skill. The verdict lines say so explicitly.
/// </summary>
public static class TradeAnalysis
{
    public const double EchoBand = 0.03;       // |est − market| ≤ 3pp ⇒ echo
    public const double UnstableStd = 0.08;    // per-provider std across recent cycles
    public const double ThinMarketVolume = 10_000;

    public sealed record ProviderAnalysis(string Provider, double Estimate, double DeltaPp, string Classification);

    /// <summary>Pure classification — unit-testable, no I/O.</summary>
    public static (List<ProviderAnalysis> Providers, List<string> Verdicts) Analyze(
        double marketYesPrice, Dictionary<string, double> providerEstimates,
        Dictionary<string, List<double>>? recentHistory, double volume, double liquidity)
    {
        var providers = new List<ProviderAnalysis>();
        var verdicts = new List<string>();
        var independents = new List<string>();
        var echoes = new List<string>();
        var unstable = new List<string>();

        foreach (var (provider, est) in providerEstimates.OrderBy(kv => kv.Key))
        {
            var delta = (est - marketYesPrice) * 100;
            string cls;
            if (recentHistory is not null
                && recentHistory.TryGetValue(provider, out var hist)
                && hist.Count >= 2)
            {
                var mean = hist.Average();
                var std = Math.Sqrt(hist.Sum(x => (x - mean) * (x - mean)) / (hist.Count - 1));
                if (std > UnstableStd)
                {
                    cls = $"UNSTABLE(std={std:F2})";
                    unstable.Add(provider);
                }
                else cls = Math.Abs(delta) <= EchoBand * 100 ? "ECHO" : "INDEPENDENT";
            }
            else cls = Math.Abs(delta) <= EchoBand * 100 ? "ECHO" : "INDEPENDENT";

            if (cls == "ECHO") echoes.Add(provider);
            else if (cls == "INDEPENDENT") independents.Add(provider);
            providers.Add(new ProviderAnalysis(provider, est, delta, cls));
        }

        if (independents.Count >= 2 && echoes.Count > 0)
            verdicts.Add(
                $"PRIOR_VS_MARKET: {independents.Count} independent voices ({string.Join(", ", independents)}) " +
                $"disagree while {echoes.Count} echo(es) confirm the market — likeliest a knowledge gap " +
                "(recent events the models don't know), not skill. Weight this edge skeptically.");
        if (independents.Count >= 2 && echoes.Count == 0)
            verdicts.Add(
                $"BROAD_DISAGREEMENT: all {independents.Count} providers disagree with the market — " +
                "candidate mispricing, not anchoring.");
        if (unstable.Count > 0)
            verdicts.Add(
                $"UNSTABLE: {string.Join(", ", unstable)} — the ensemble edge may be a trim artifact of one volatile draw.");
        if (independents.Count == 1)
            verdicts.Add(
                $"SINGLE_VOICE: only {independents[0]} is independent — the ensemble edge leans on one " +
                "opinion against N echoes of the market price.");
        if (volume < ThinMarketVolume)
            verdicts.Add($"THIN_MARKET: volume ${volume:N0}, liquidity ${liquidity:N0} — the market price itself is unreliable.");

        return (providers, verdicts);
    }

    /// <summary>
    /// Consensus filter: drop providers whose recent estimates for THIS market
    /// swing more than <paramref name="threshold"/> (std) — their draws turn the
    /// trimmed mean into an artifact of one volatile cycle (Clancy case).
    /// Degenerate case (everyone unstable) falls back to keeping everyone.
    /// </summary>
    public static (List<(string Provider, double Mean)> Consensus, List<string> Excluded) StableConsensus(
        IReadOnlyList<(string Provider, double Mean)> scored,
        IReadOnlyDictionary<string, List<double>>? history,
        double threshold = UnstableStd)
    {
        var consensus = new List<(string Provider, double Mean)>();
        var excluded = new List<string>();
        foreach (var (provider, mean) in scored)
        {
            if (history is not null && history.TryGetValue(provider, out var hist) && hist.Count >= 2)
            {
                var m = hist.Average();
                var std = Math.Sqrt(hist.Sum(x => (x - m) * (x - m)) / (hist.Count - 1));
                if (std > threshold)
                {
                    excluded.Add($"{provider}(std={std:F2})");
                    continue;
                }
            }
            consensus.Add((provider, mean));
        }
        if (consensus.Count == 0 && scored.Count > 0)
            return (scored.ToList(), new List<string>());
        return (consensus, excluded);
    }

    /// <summary>Load the last <paramref name="take"/> estimates per provider for this market from the journal.</summary>
    public static Dictionary<string, List<double>> RecentHistory(string dataDir, string conditionId, int take = 5)
    {
        var result = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataDir, "estimates.jsonl");
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains(conditionId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains("\"provider_estimates\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("record_type", out var rt) || rt.GetString() != "evaluation") continue;
                    if (!root.TryGetProperty("provider_estimates", out var pe)) continue;
                    foreach (var p in pe.EnumerateObject())
                    {
                        if (!result.TryGetValue(p.Name, out var list)) result[p.Name] = list = new List<double>();
                        list.Add(p.Value.GetDouble());
                    }
                }
                catch (JsonException) { /* malformed line — skip */ }
            }
        }
        catch (IOException) { /* journal unreadable — skip instability check */ }

        foreach (var k in result.Keys.ToList())
        {
            var list = result[k];
            result[k] = list.Skip(Math.Max(0, list.Count - take)).ToList();
        }
        return result;
    }

    public static void LogAnalysis(ILogger log, MarketInfo market, Estimate estimate, string dataDir)
    {
        var history = RecentHistory(dataDir, market.ConditionId);
        var (providers, verdicts) = Analyze(market.OutcomeYesPrice, estimate.ProviderEstimates, history, market.Volume, market.Liquidity);

        log.LogInformation("TRADE ANALYSIS [{Question}]", market.Question[..Math.Min(market.Question.Length, 60)]);
        foreach (var p in providers)
            log.LogInformation("  {Provider}={Est:P0} Δ{Delta:+0.0;-0.0}pp vs market {Market:P0} → {Classification}",
                p.Provider, p.Estimate, p.DeltaPp, market.OutcomeYesPrice, p.Classification);
        foreach (var v in verdicts)
            log.LogWarning("  VERDICT: {Verdict}", v);
        if (verdicts.Count == 0)
            log.LogInformation("  VERDICT: all estimates within echo band — no independent disagreement to audit");
    }
}
