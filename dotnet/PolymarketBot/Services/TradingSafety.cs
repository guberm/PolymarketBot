using System.Net;
using System.Text.Json;

namespace PolymarketBot.Services;

public sealed record GeoblockStatus(bool Blocked, string Country, string Region);

public sealed class ClobOrderRejectedException(HttpStatusCode statusCode, string responseBody)
    : Exception($"CLOB rejected order with HTTP {(int)statusCode} ({statusCode}): {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class TradingBlockedException(string message, Exception? inner = null)
    : Exception(message, inner);

public static class TradingSafety
{
    public const string GeoblockUrl = "https://polymarket.com/api/geoblock";

    public static async Task<GeoblockStatus> CheckGeoblockAsync(
        HttpClient http, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(GeoblockUrl, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("blocked", out var blocked) ||
            blocked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid Polymarket geoblock response");
        return new GeoblockStatus(
            blocked.GetBoolean(),
            json.RootElement.TryGetProperty("country", out var country) ? country.GetString() ?? "" : "",
            json.RootElement.TryGetProperty("region", out var region) ? region.GetString() ?? "" : "");
    }

    public static void HandleDefinitiveRejection(
        OrderJournal journal, string intentId, ClobOrderRejectedException rejection)
    {
        journal.Complete(intentId);
        if (rejection.StatusCode == HttpStatusCode.Forbidden)
            throw new TradingBlockedException(
                "Emergency stop after definitive CLOB HTTP 403", rejection);
    }
}
