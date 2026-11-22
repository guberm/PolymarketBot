using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed record NewsItem(string Title, string Source, string Published);
public sealed record ResearchVerdict(string Verdict, double Confidence, string Reason, int NewsCount);

/// <summary>
/// News-based research judge: before a BUY goes out, fetch recent news for the
/// market question and let an LLM judge whether the news supports the trade.
/// DENY blocks the order; UNCLEAR/CONFIRM let it through (fail-open).
/// </summary>
public sealed class ResearchJudge
{
    private readonly HttpClient _http;
    private readonly Estimator _estimator;
    private readonly ILogger<ResearchJudge> _log;
    private readonly BotConfig _config;

    public ResearchJudge(BotConfig config, HttpClient http, Estimator estimator, ILogger<ResearchJudge> log)
    {
        _config = config;
        _http = http;
        _estimator = estimator;
        _log = log;
    }

    public async Task<ResearchVerdict?> EvaluateAsync(
        MarketInfo market, double marketPrice, double fairEstimate, string side, CancellationToken ct = default)
    {
        if (!_config.ResearchJudgeEnabled) return null;
        try
        {
            var news = await FetchNewsAsync(market.Question, ct);
            if (news.Count == 0)
                return new ResearchVerdict("UNCLEAR", 0, "no recent news found for the market question", 0);

            var system = "You are a strict research judge for prediction-market trades. " +
                         "Answer ONLY with a single JSON object, no prose around it.";
            var user = BuildJudgePrompt(market, marketPrice, fairEstimate, side, news);

            // One-shot call; must never hang the cycle (same discipline as estimates).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var answer = await _estimator.CompleteOnceAsync(system, user, timeout.Token);
            if (string.IsNullOrWhiteSpace(answer))
                return new ResearchVerdict("UNCLEAR", 0, "research judge unavailable (provider error) — failing open", news.Count);

            var verdict = ParseVerdict(answer, news.Count);
            _log.LogInformation("Research judge: {Verdict} ({Confidence:P0}) — {Reason}", verdict.Verdict, verdict.Confidence, verdict.Reason);
            return verdict;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ResearchVerdict("UNCLEAR", 0, "research judge timed out — failing open", 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Research judge error: {Err} — failing open", ex.Message);
            return new ResearchVerdict("UNCLEAR", 0, $"research judge error: {ex.Message}", 0);
        }
    }

    private async Task<List<NewsItem>> FetchNewsAsync(string question, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(question.Length > 120 ? question[..120] : question);
        var url = $"https://news.google.com/rss/search?q={q}&hl=en-US&gl=US&ceid=US:en";
        var max = Math.Clamp(_config.ResearchMaxNews, 1, 15);
        var items = new List<NewsItem>();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Google News RSS returned {Status}", (int)resp.StatusCode);
                return items;
            }
            var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var item in xml.Descendants("item").Take(max))
            {
                var title = item.Element("title")?.Value ?? "";
                var pub = item.Element("pubDate")?.Value ?? "";
                var source = item.Element("source")?.Value ?? "";
                if (title.Length > 0) items.Add(new NewsItem(title, source, pub));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("News fetch failed: {Err}", ex.Message);
        }
        return items;
    }

    private static string BuildJudgePrompt(
        MarketInfo market, double marketPrice, double fairEstimate, string side, List<NewsItem> news)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Market question: {market.Question}");
        sb.AppendLine($"Our proposed trade: BUY {side} at market price {marketPrice:P1} (our ensemble fair estimate: {fairEstimate:P1})");
        sb.AppendLine($"Today (UTC): {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine("Recent news headlines:");
        for (var i = 0; i < news.Count; i++)
            sb.AppendLine($"{i + 1}. [{news[i].Source} | {news[i].Published}] {news[i].Title}");
        sb.AppendLine();
        sb.AppendLine("Instructions:");
        sb.AppendLine("- Base the verdict ONLY on the news items above. No outside knowledge.");
        sb.AppendLine("- If the news shows the event already happened or the market is clearly stale, verdict = DENY (buying into it is a loss).");
        sb.AppendLine("- If the news supports our trade direction, verdict = CONFIRM.");
        sb.AppendLine("- If the news is silent or ambiguous on the question, verdict = UNCLEAR.");
        sb.AppendLine("- \"already held\" and duplicate coverage count as silence.");
        sb.AppendLine("Return strict JSON: {\"verdict\":\"CONFIRM|DENY|UNCLEAR\",\"confidence\":0.0,\"reason\":\"one sentence\"}");
        return sb.ToString();
    }

    public static ResearchVerdict ParseVerdict(string answer, int newsCount)
    {
        try
        {
            var start = answer.IndexOf('{');
            var end = answer.LastIndexOf('}');
            if (start < 0 || end <= start) return new ResearchVerdict("UNCLEAR", 0, "judge returned no JSON", newsCount);
            using var doc = JsonDocument.Parse(answer[start..(end + 1)]);
            var verdict = (doc.RootElement.TryGetProperty("verdict", out var v) ? v.GetString() : null)?.ToUpperInvariant() ?? "UNCLEAR";
            if (verdict is not ("CONFIRM" or "DENY" or "UNCLEAR")) verdict = "UNCLEAR";
            var confidence = doc.RootElement.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cd) ? cd : 0.5;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new ResearchVerdict(verdict, Math.Clamp(confidence, 0, 1), reason, newsCount);
        }
        catch (Exception)
        {
            return new ResearchVerdict("UNCLEAR", 0, "judge JSON unparseable", newsCount);
        }
    }
}
