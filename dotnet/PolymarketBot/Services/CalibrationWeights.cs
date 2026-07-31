using System.Text.Json;

namespace PolymarketBot.Services;

public readonly record struct ProviderCalibrationStats(int Count, double SquaredError);

public static class CalibrationWeights
{
    public static Dictionary<string, ProviderCalibrationStats> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var outcomes = new Dictionary<string, double>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Text(root, "record_type") == "resolution" &&
                    root.TryGetProperty("actual_outcome", out var actual))
                    outcomes[Text(root, "condition_id")] = actual.GetDouble();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        }

        var stats = new Dictionary<string, ProviderCalibrationStats>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Text(root, "record_type", "evaluation") != "evaluation" ||
                    !outcomes.TryGetValue(Text(root, "condition_id"), out var outcome) ||
                    !root.TryGetProperty("provider_estimates", out var providers) ||
                    providers.ValueKind != JsonValueKind.Object) continue;
                foreach (var provider in providers.EnumerateObject())
                {
                    var probability = provider.Value.GetDouble();
                    stats.TryGetValue(provider.Name, out var current);
                    stats[provider.Name] = new ProviderCalibrationStats(
                        current.Count + 1, current.SquaredError + Math.Pow(probability - outcome, 2));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        }
        return stats;
    }

    public static Dictionary<string, double> Calculate(
        IReadOnlyDictionary<string, ProviderCalibrationStats> stats,
        IReadOnlyList<string> providers, int minSamples, double shrinkage, double maxWeight)
    {
        if (providers.Count == 0 || providers.Any(p => !stats.TryGetValue(p, out var s) || s.Count < minSamples))
            return [];
        if (providers.Count == 1) return new() { [providers[0]] = 1 };

        var inverse = providers.ToDictionary(p => p,
            p => 1 / Math.Max(stats[p].SquaredError / stats[p].Count, .01));
        var inverseTotal = inverse.Values.Sum();
        var equal = 1.0 / providers.Count;
        shrinkage = Math.Clamp(shrinkage, 0, 1);
        var desired = providers.ToDictionary(p => p,
            p => shrinkage * equal + (1 - shrinkage) * inverse[p] / inverseTotal);
        var cap = Math.Max(equal, Math.Clamp(maxWeight, 0, 1));
        var fixedProviders = new HashSet<string>();
        while (true)
        {
            var remaining = providers.Where(p => !fixedProviders.Contains(p)).ToList();
            var mass = 1 - cap * fixedProviders.Count;
            var scale = mass / remaining.Sum(p => desired[p]);
            var newlyFixed = remaining.Where(p => desired[p] * scale > cap).ToList();
            if (newlyFixed.Count == 0)
                return providers.ToDictionary(p => p,
                    p => fixedProviders.Contains(p) ? cap : desired[p] * scale);
            foreach (var provider in newlyFixed) fixedProviders.Add(provider);
        }
    }

    private static string Text(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
}
