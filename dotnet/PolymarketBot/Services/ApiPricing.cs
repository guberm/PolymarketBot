namespace PolymarketBot.Services;

public static class ApiPricing
{
    public static double Calculate(string spec, string provider, int inputTokens, int outputTokens)
    {
        var rates = Parse(spec);
        if (!rates.TryGetValue(provider.ToLowerInvariant(), out var rate)) return 0;
        return (inputTokens * rate.Input + outputTokens * rate.Output) / 1_000_000.0;
    }

    public static Dictionary<string, (double Input, double Output)> Parse(string spec)
    {
        var result = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) continue;
            var rates = pair[1].Split('/', 2, StringSplitOptions.TrimEntries);
            if (rates.Length == 2 &&
                double.TryParse(rates[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var input) &&
                double.TryParse(rates[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var output))
                result[pair[0]] = (input, output);
        }
        return result;
    }
}
