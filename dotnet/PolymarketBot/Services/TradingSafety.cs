using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Cross-check the geoblock verdict with curl.exe (a different TLS stack).
    /// Polymarket's geo service serves a canned "blocked CA/ON" response to
    /// .NET TLS fingerprints while curl from the same IP/proxy sees the real
    /// region (observed live 2026-11-16; CLOB orders kept working throughout,
    /// proving the "block" was bot-flagging, not geolocation).
    /// Returns true/false when curl produced a verdict, null when unavailable.
    /// </summary>
    public static bool? CrossCheckGeoblockWithCurl(ILogger log)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("curl.exe", $"-s -m 15 \"{GeoblockUrl}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(20000);
            using var json = JsonDocument.Parse(stdout);
            if (!json.RootElement.TryGetProperty("blocked", out var blocked)
                || blocked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                log.LogWarning("Geoblock curl cross-check returned no verdict");
                return null;
            }
            return blocked.GetBoolean();
        }
        catch (Exception ex)
        {
            log.LogWarning("Geoblock curl cross-check unavailable: {Error}", ex.Message);
            return null;
        }
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
