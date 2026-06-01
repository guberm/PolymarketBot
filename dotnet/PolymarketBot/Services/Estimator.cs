using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class Estimator
{
    private const string SystemPrompt =
        "You are a calibrated probability estimator for prediction markets.\n" +
        "Given a market question, estimate the TRUE probability that the outcome resolves YES.\n" +
        "\n" +
        "Rules:\n" +
        "- Output ONLY valid JSON: {\"probability\": 0.XX, \"reasoning\": \"one sentence\"}\n" +
        "- probability must be between 0.02 and 0.98\n" +
        "- Be well-calibrated: events you rate at 70% should happen ~70% of the time\n" +
        "- Use base rates, current knowledge, and logical reasoning\n" +
        "- The current market price reflects real-money consensus from many informed traders — treat it as a Bayesian prior. Only deviate significantly if you have strong specific reasoning.\n" +
        "- If deeply uncertain, stay close to the market price\n" +
        "- Keep reasoning under 50 words";

    private readonly BotConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<Estimator> _log;

    // Providers that hit 429 this cycle — skip them until ResetCycle()
    private readonly HashSet<string> _rateLimitedThisCycle = new();

    public void ResetCycle() => _rateLimitedThisCycle.Clear();

    public Estimator(BotConfig config, HttpClient http, ILogger<Estimator> log)
    {
        _config = config;
        _http = http;
        _log = log;
    }

    public async Task<Estimate?> EstimateAsync(MarketInfo market, CancellationToken ct = default)
    {
        return _config.MultiProvider
            ? await EstimateMultiAsync(market, ct)
            : await EstimateSingleAsync(market, ct);
    }

    // ── Single-provider estimation ─────────────────────────────────────────

    private async Task<Estimate?> EstimateSingleAsync(MarketInfo market, CancellationToken ct)
    {
        var rawEstimates = new List<double>();
        var totalInput = 0;
        var totalOutput = 0;
        var firstReasoning = "";

        for (var i = 0; i < _config.EnsembleSize; i++)
        {
            var result = await SingleCallAsync(market, _config.AiProvider, ct);
            if (result is null) continue;
            rawEstimates.Add(result.Value.Probability);
            if (string.IsNullOrEmpty(firstReasoning)) firstReasoning = result.Value.Reasoning;
            totalInput += result.Value.InputTokens;
            totalOutput += result.Value.OutputTokens;
        }

        return BuildEstimate(market, rawEstimates, totalInput, totalOutput, firstReasoning);
    }

    // ── Multi-provider estimation ──────────────────────────────────────────

    private async Task<Estimate?> EstimateMultiAsync(MarketInfo market, CancellationToken ct)
    {
        var configured = GetConfiguredProviders();
        if (configured.Count == 0)
        {
            _log.LogWarning("multi_provider=true but no providers configured — falling back to single");
            return await EstimateSingleAsync(market, ct);
        }

        var callsPer = Math.Max(1, (int)Math.Ceiling((double)_config.EnsembleSize / configured.Count));

        // Per-provider results: (provider, probs, inputTokens, outputTokens, reasoning)
        var providerResults = new List<(string Provider, List<double> Probs, int Input, int Output, string Reasoning)>();
        var totalInput = 0;
        var totalOutput = 0;
        var firstReasoning = "";

        foreach (var provider in configured)
        {
            if (_rateLimitedThisCycle.Contains(provider))
            {
                _log.LogDebug("{Provider} skipped — rate-limited this cycle", provider);
                continue;
            }

            var probs = new List<double>();
            var pInput = 0; var pOutput = 0; var pReasoning = "";

            for (var i = 0; i < callsPer; i++)
            {
                var result = await SingleCallAsync(market, provider, ct);
                if (result is null) continue;
                probs.Add(result.Value.Probability);
                pInput += result.Value.InputTokens;
                pOutput += result.Value.OutputTokens;
                if (string.IsNullOrEmpty(pReasoning)) pReasoning = result.Value.Reasoning;
            }

            if (probs.Count == 0) { _log.LogWarning("  {Provider}: no valid estimates — skipped", provider); continue; }
            providerResults.Add((provider, probs, pInput, pOutput, pReasoning));
            totalInput += pInput; totalOutput += pOutput;
            if (string.IsNullOrEmpty(firstReasoning)) firstReasoning = pReasoning;
        }

        if (providerResults.Count == 0) return null;

        // Score provider reliability. Low variance is good, but strong market deviation is not
        // automatically evidence of skill unless another layer confirms it.
        var marketPrice = (market.OutcomeYesPrice + (1 - market.OutcomeNoPrice)) / 2;
        var scored = providerResults
            .Select(r =>
            {
                var mean = r.Probs.Average();
                var std = r.Probs.Count > 1 ? StdDev(r.Probs) : 0.0;
                var confidence = 1.0 / (std + 0.01);
                var marketDeviation = Math.Abs(mean - marketPrice);
                var score = confidence / (1.0 + 8.0 * marketDeviation);
                return (r.Provider, Mean: mean, Std: std, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var winner = scored[0].Provider;

        // Log breakdown
        var parts = scored.Select(s =>
            $"{(s.Provider == winner ? "⭐" : "  ")}{s.Provider}={s.Mean:P0}(±{s.Std:F2},s={s.Score:F3})");
        _log.LogInformation("Multi-provider [{Question}] | {Breakdown}",
            Truncate(market.Question, 35), string.Join(" | ", parts));

        // Final estimate: trimmed mean of per-provider means (each provider counts equally)
        var providerMeans = scored.Select(s => s.Mean).ToList();
        return BuildEstimate(market, providerMeans, totalInput, totalOutput, firstReasoning,
            note: $"multi({scored.Count} providers, ⭐{winner})");
    }

    private List<string> GetConfiguredProviders()
    {
        var out_ = new List<string>();
        if (_config.AnthropicEnabled && !string.IsNullOrEmpty(_config.AnthropicApiKey)) out_.Add("anthropic");
        if (_config.OpenAiEnabled    && !string.IsNullOrEmpty(_config.OpenAiApiKey))    out_.Add("openai");
        if (_config.GeminiEnabled    && !string.IsNullOrEmpty(_config.GeminiApiKey))    out_.Add("gemini");
        if (_config.OpenRouterEnabled && !string.IsNullOrEmpty(_config.OpenRouterApiKey)) out_.Add("openrouter");
        if (_config.AzureOpenAiEnabled &&
            !string.IsNullOrEmpty(_config.AzureOpenAiApiKey) &&
            !string.IsNullOrEmpty(_config.AzureOpenAiEndpoint) &&
            !string.IsNullOrEmpty(_config.AzureOpenAiDeployment)) out_.Add("azure_openai");
        return out_;
    }

    private string GetModelForProvider(string provider) => provider switch
    {
        "anthropic"    => !string.IsNullOrEmpty(_config.AnthropicModel)  ? _config.AnthropicModel  : "claude-sonnet-4-6",
        "openai"       => !string.IsNullOrEmpty(_config.OpenAiModel)     ? _config.OpenAiModel     : "gpt-4o",
        "gemini"       => !string.IsNullOrEmpty(_config.GeminiModel)     ? _config.GeminiModel     : "gemini-2.0-flash",
        "openrouter"   => _config.OpenRouterModel,
        "azure_openai" => _config.AzureOpenAiDeployment,
        _              => _config.AnthropicModel,
    };

    private Estimate? BuildEstimate(MarketInfo market, List<double> rawEstimates,
        int totalInput, int totalOutput, string firstReasoning, string note = "")
    {
        if (rawEstimates.Count == 0) return null;
        if (rawEstimates.Count < 2)
            _log.LogWarning("Only {Count} valid estimates for: {Question}", rawEstimates.Count, Truncate(market.Question, 60));

        List<double> trimmed = rawEstimates.Count >= 4
            ? rawEstimates.OrderBy(x => x).Skip(1).Take(rawEstimates.Count - 2).ToList()
            : rawEstimates;

        var fairProb = trimmed.Average();
        var confidence = rawEstimates.Count > 1 ? StdDev(rawEstimates) : 1.0;

        if (rawEstimates.Count >= 2 && confidence > _config.MaxEstimateStd)
        {
            _log.LogInformation("SKIP (low confidence): {Question} std={Std:F3} > max={Max:F3}",
                Truncate(market.Question, 50), confidence, _config.MaxEstimateStd);
            return null;
        }

        var label = string.IsNullOrEmpty(note) ? "" : $"[{note}] ";
        _log.LogInformation("Estimate: {Label}{Question} -> {Prob:P2} (n={Count}, std={Std:F3})",
            label, Truncate(market.Question, 50), fairProb, rawEstimates.Count, confidence);

        return new Estimate
        {
            MarketConditionId = market.ConditionId,
            Question = market.Question,
            FairProbability = fairProb,
            RawEstimates = rawEstimates,
            Confidence = confidence,
            ReasoningSummary = firstReasoning,
            InputTokensUsed = totalInput,
            OutputTokensUsed = totalOutput,
        };
    }

    private async Task<CallResult?> SingleCallAsync(MarketInfo market, string provider, CancellationToken ct)
    {
        // Retry delays for transient overload (429 / 529): 10s, 20s, 40s
        int[] backoffMs = { 10_000, 20_000, 40_000 };

        for (var attempt = 0; attempt <= backoffMs.Length; attempt++)
        {
            try
            {
                var (status, body) = await MakeProviderRequestAsync(market, provider, ct);

                if (status == 429 || status == 529)
                {
                    if (attempt < backoffMs.Length)
                    {
                        var delay = backoffMs[attempt];
                        _log.LogWarning("{Provider} {Status} (attempt {A}/{Max}) — retrying in {Sec}s",
                            provider, status, attempt + 1, backoffMs.Length, delay / 1000);
                        await Task.Delay(delay, ct);
                        continue;
                    }
                    _log.LogError("{Provider} {Status}: giving up after {Max} retries for {Question} — skipping for rest of cycle",
                        provider, status, backoffMs.Length, Truncate(market.Question, 40));
                    _rateLimitedThisCycle.Add(provider);
                    return null;
                }

                if (status < 200 || status >= 300)
                {
                    _log.LogError("{Provider} HTTP {Status}: {Body}",
                        provider, status, body[..Math.Min(body.Length, 200)]);
                    return null;
                }

                return ParseProviderResponse(provider, body);
            }
            catch (JsonException ex)
            {
                _log.LogDebug("{Provider} parse failed: {Error}", provider, ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug("{Provider} call failed: {Error}", provider, ex.Message);
                return null;
            }
        }

        return null;
    }

    // ActiveModel is the model for the currently configured single provider (used as fallback)
    private string ActiveModel => GetModelForProvider(_config.AiProvider.ToLowerInvariant());

    private async Task<(int status, string body)> MakeProviderRequestAsync(MarketInfo market, string provider, CancellationToken ct)
    {
        var userPrompt = BuildUserPrompt(market);
        return provider.ToLowerInvariant() switch
        {
            "gemini"       => await MakeGeminiRequestAsync(userPrompt, ct),
            "openai"       => await MakeOpenAiCompatRequestAsync("openai", userPrompt, ct),
            "openrouter"   => await MakeOpenAiCompatRequestAsync("openrouter", userPrompt, ct),
            "azure_openai" => await MakeOpenAiCompatRequestAsync("azure_openai", userPrompt, ct),
            _              => await MakeAnthropicRequestAsync(userPrompt, ct),
        };
    }

    // ── Anthropic ──────────────────────────────────────────────────────────

    private async Task<(int, string)> MakeAnthropicRequestAsync(string userPrompt, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = GetModelForProvider("anthropic"),
            max_tokens = _config.MaxEstimateTokens,
            temperature = _config.EnsembleTemperature,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        });
        var host = string.IsNullOrEmpty(_config.AnthropicApiHost) ? "https://api.anthropic.com" : _config.AnthropicApiHost;
        var req = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _config.AnthropicApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static CallResult? ParseAnthropicResponse(string body)
    {
        var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()?.Trim() ?? "";
        var usage = doc.RootElement.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        var (prob, reasoning) = ParseProbabilityJson(text);
        return prob < 0 ? null : new CallResult(prob, reasoning, inputTokens, outputTokens);
    }

    // ── OpenAI-compatible (OpenAI, OpenRouter, Azure OpenAI) ──────────────

    private async Task<(int, string)> MakeOpenAiCompatRequestAsync(string provider, string userPrompt, CancellationToken ct)
    {
        string url;
        var model = GetModelForProvider(provider);
        var req = new HttpRequestMessage(HttpMethod.Post, "");

        if (provider == "openai")
        {
            var host = string.IsNullOrEmpty(_config.OpenAiApiHost) ? "https://api.openai.com" : _config.OpenAiApiHost.TrimEnd('/');
            url = $"{host}/v1/chat/completions";
            req.Headers.Add("Authorization", $"Bearer {_config.OpenAiApiKey}");
        }
        else if (provider == "openrouter")
        {
            var orHost = string.IsNullOrEmpty(_config.OpenRouterApiHost) ? "https://openrouter.ai" : _config.OpenRouterApiHost.TrimEnd('/');
            url = $"{orHost}/api/v1/chat/completions";
            req.Headers.Add("Authorization", $"Bearer {_config.OpenRouterApiKey}");
        }
        else  // azure_openai
        {
            var endpoint = _config.AzureOpenAiEndpoint.TrimEnd('/');
            var deployment = _config.AzureOpenAiDeployment;
            var version = string.IsNullOrEmpty(_config.AzureOpenAiApiVersion) ? "2024-02-01" : _config.AzureOpenAiApiVersion;
            url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version}";
            req.Headers.Add("api-key", _config.AzureOpenAiApiKey);
            model = deployment;  // Azure uses deployment name
        }

        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = userPrompt }
            },
            temperature = _config.EnsembleTemperature,
            max_tokens = _config.MaxEstimateTokens,
        });
        req.RequestUri = new Uri(url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static CallResult? ParseOpenAiCompatResponse(string body)
    {
        var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim() ?? "";
        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : (JsonElement?)null;
        var inputTokens  = usage?.TryGetProperty("prompt_tokens",     out var pt) == true ? pt.GetInt32() : 0;
        var outputTokens = usage?.TryGetProperty("completion_tokens", out var ct2) == true ? ct2.GetInt32() : 0;
        var (prob, reasoning) = ParseProbabilityJson(text);
        return prob < 0 ? null : new CallResult(prob, reasoning, inputTokens, outputTokens);
    }

    // ── Google Gemini ──────────────────────────────────────────────────────

    private async Task<(int, string)> MakeGeminiRequestAsync(string userPrompt, CancellationToken ct)
    {
        var model = GetModelForProvider("gemini");
        var gemHost = string.IsNullOrEmpty(_config.GeminiApiHost) ? "https://generativelanguage.googleapis.com" : _config.GeminiApiHost.TrimEnd('/');
        var url = $"{gemHost}/v1beta/models/{model}:generateContent?key={_config.GeminiApiKey}";
        var body = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
            generationConfig = new
            {
                temperature = _config.EnsembleTemperature,
                maxOutputTokens = _config.MaxEstimateTokens,
            }
        });
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static CallResult? ParseGeminiResponse(string body)
    {
        var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString()?.Trim() ?? "";
        var meta = doc.RootElement.TryGetProperty("usageMetadata", out var m) ? m : (JsonElement?)null;
        var inputTokens  = meta?.TryGetProperty("promptTokenCount",     out var pt) == true ? pt.GetInt32() : 0;
        var outputTokens = meta?.TryGetProperty("candidatesTokenCount", out var ct2) == true ? ct2.GetInt32() : 0;
        var (prob, reasoning) = ParseProbabilityJson(text);
        return prob < 0 ? null : new CallResult(prob, reasoning, inputTokens, outputTokens);
    }

    // ── Response parsing (shared) ──────────────────────────────────────────

    private static CallResult? ParseProviderResponse(string provider, string body)
    {
        return provider.ToLowerInvariant() switch
        {
            "gemini"       => ParseGeminiResponse(body),
            "openai"       => ParseOpenAiCompatResponse(body),
            "openrouter"   => ParseOpenAiCompatResponse(body),
            "azure_openai" => ParseOpenAiCompatResponse(body),
            _              => ParseAnthropicResponse(body),
        };
    }

    /// <summary>
    /// Parse {"probability": 0.XX, "reasoning": "..."} from model response text.
    /// Returns (prob, reasoning) where prob = -1 signals parse failure.
    /// </summary>
    private static (double prob, string reasoning) ParseProbabilityJson(string text)
    {
        try
        {
            if (text.StartsWith("```"))
            {
                var lines = text.Split('\n').Where(l => !l.TrimStart().StartsWith("```")).ToArray();
                text = string.Join('\n', lines);
            }
            var doc = JsonDocument.Parse(text);
            var prob = doc.RootElement.GetProperty("probability").GetDouble();
            var reasoning = doc.RootElement.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
            return (Math.Clamp(prob, 0.02, 0.98), reasoning);
        }
        catch
        {
            return (-1, "");
        }
    }

    private static string BuildUserPrompt(MarketInfo market)
    {
        var desc = market.Description.Length > 500
            ? market.Description[..500]
            : market.Description;
        if (string.IsNullOrEmpty(desc)) desc = "N/A";

        return $"Market: {market.Question}\n" +
            $"Event: {market.EventTitle}\n" +
            $"Description: {desc}\n" +
            $"Category: {market.Category}\n" +
            $"Resolution date: {(string.IsNullOrEmpty(market.EndDate) ? "Unknown" : market.EndDate)}\n" +
            $"Current market price: YES at {market.OutcomeYesPrice:P0} / NO at {market.OutcomeNoPrice:P0}\n" +
            "\n" +
            "Estimate the probability this resolves YES. Output JSON only.";
    }

    private static double StdDev(List<double> values)
    {
        var mean = values.Average();
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    /// <summary>
    /// Makes a minimal test call to validate the configured provider's API key.
    /// Returns false on auth failure (HTTP 401/403). Network/rate-limit errors return true (don't block startup).
    /// </summary>
    public async Task<bool> ValidateApiKeyAsync(CancellationToken ct = default)
    {
        if (_config.MultiProvider)
        {
            var configured = GetConfiguredProviders();
            if (configured.Count == 0) { _log.LogError("multi_provider=true but no API keys configured"); return false; }
            var results = new Dictionary<string, bool>();
            foreach (var p in configured)
            {
                var ok = await ValidateProviderAsync(p, ct);
                results[p] = ok;
                _log.LogInformation("  {Status} {Provider}", ok ? "✓" : "✗", p);
            }
            if (!results.Values.Any(v => v)) { _log.LogError("All configured providers failed validation"); return false; }
            var failed = results.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
            if (failed.Count > 0) _log.LogWarning("Some providers failed: {Failed} — continuing", string.Join(", ", failed));
            return true;
        }
        return await ValidateProviderAsync(_config.AiProvider.ToLowerInvariant(), ct);
    }

    private async Task<bool> ValidateProviderAsync(string provider, CancellationToken ct)
    {
        try
        {
            HttpRequestMessage req;

            if (provider == "gemini")
            {
                var model = GetModelForProvider("gemini");
                var gemHost = string.IsNullOrEmpty(_config.GeminiApiHost) ? "https://generativelanguage.googleapis.com" : _config.GeminiApiHost.TrimEnd('/');
                var url = $"{gemHost}/v1beta/models/{model}:generateContent?key={_config.GeminiApiKey}";
                var body = JsonSerializer.Serialize(new
                {
                    contents = new[] { new { parts = new[] { new { text = "hi" } } } },
                    generationConfig = new { maxOutputTokens = 1 }
                });
                req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
            else if (provider is "openai" or "openrouter" or "azure_openai")
            {
                var (url, authHeader, authValue) = provider switch
                {
                    "openai" => ($"{(_config.OpenAiApiHost.TrimEnd('/'))}/v1/chat/completions", "Authorization", $"Bearer {_config.OpenAiApiKey}"),
                    "openrouter" => ($"{(string.IsNullOrEmpty(_config.OpenRouterApiHost) ? "https://openrouter.ai" : _config.OpenRouterApiHost.TrimEnd('/'))}/api/v1/chat/completions", "Authorization", $"Bearer {_config.OpenRouterApiKey}"),
                    _ => ($"{_config.AzureOpenAiEndpoint.TrimEnd('/')}/openai/deployments/{_config.AzureOpenAiDeployment}/chat/completions?api-version={_config.AzureOpenAiApiVersion}", "api-key", _config.AzureOpenAiApiKey),
                };
                var body = JsonSerializer.Serialize(new
                {
                    model = GetModelForProvider(provider),
                    messages = new[] { new { role = "user", content = "hi" } },
                    max_tokens = 1
                });
                req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add(authHeader, authValue);
            }
            else  // anthropic
            {
                var host = string.IsNullOrEmpty(_config.AnthropicApiHost) ? "https://api.anthropic.com" : _config.AnthropicApiHost;
                var body = JsonSerializer.Serialize(new
                {
                    model = GetModelForProvider("anthropic"),
                    max_tokens = 1,
                    messages = new[] { new { role = "user", content = "hi" } }
                });
                req = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1/messages")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-api-key", _config.AnthropicApiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
            }

            var resp = await _http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;

            if (status == 401 || status == 403)
            {
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogError("{Provider} API key invalid (HTTP {Status}): {Body}",
                    provider, status, respBody[..Math.Min(respBody.Length, 200)]);
                return false;
            }
            // Gemini returns 400 for invalid keys — check response body
            if (provider == "gemini" && status == 400)
            {
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                if (respBody.Contains("API key", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError("Gemini API key invalid: {Body}", respBody[..Math.Min(respBody.Length, 200)]);
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("API key validation network error: {Error} — continuing", ex.Message);
            return true;
        }
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";

    private readonly record struct CallResult(double Probability, string Reasoning, int InputTokens, int OutputTokens);
}
