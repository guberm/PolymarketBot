using System.Text.Json;

namespace PolymarketBot;

/// <summary>
/// Bot configuration.
/// Priority (highest wins):
///   1. Environment variables
///   2. polymarket_bot_config.json  (project root, or path in CONFIG_FILE env var)
///   3. Code defaults
/// </summary>
public sealed class BotConfig
{
    private static string? _configDir;

    // Mode
    public bool LiveTrading { get; init; }

    // Scan
    public int ScanIntervalMinutes { get; init; } = 10;
    public double MinLiquidity { get; init; } = 10000.0;
    public double MinVolume24Hr { get; init; } = 1000.0;
    public double MinTimeToResolutionHours { get; init; } = 48.0;
    public double MinMarketPrice { get; init; } = 0.10;
    public int MarketsPerCycle { get; init; } = 15;
    public double MaxSpread { get; init; } = 0.04;

    // Optional read-only Kalshi comparison
    public bool KalshiShadowEnabled { get; init; } = false;
    public string KalshiApiHost { get; init; } = "https://api.elections.kalshi.com/trade-api/v2";
    public int KalshiMarketsLimit { get; init; } = 200;
    public double KalshiMinMatchScore { get; init; } = 0.55;
    public double KalshiLlmSameThreshold { get; init; } = 0.90;

    // AI provider
    public string AiProvider { get; init; } = "anthropic";   // selected provider for single-provider mode
    public bool MultiProvider { get; init; } = false;        // True = query ALL configured providers and aggregate

    // Per-provider credentials + models (one place per provider, no overlap)
    // Anthropic
    public bool AnthropicEnabled { get; init; } = true;
    public string AnthropicApiKey { get; init; } = "";
    public string AnthropicApiHost { get; init; } = "https://api.anthropic.com";
    public string AnthropicModel { get; init; } = "claude-sonnet-4-6";
    // OpenAI
    public bool OpenAiEnabled { get; init; } = true;
    public string OpenAiApiKey { get; init; } = "";
    public string OpenAiApiHost { get; init; } = "https://api.openai.com";
    public string OpenAiModel { get; init; } = "gpt-4o";
    // Google Gemini
    public bool GeminiEnabled { get; init; } = true;
    public string GeminiApiKey { get; init; } = "";
    public string GeminiApiHost { get; init; } = "https://generativelanguage.googleapis.com";
    public string GeminiModel { get; init; } = "gemini-2.0-flash";
    // OpenRouter
    public bool OpenRouterEnabled { get; init; } = true;
    public string OpenRouterApiKey { get; init; } = "";
    public string OpenRouterApiHost { get; init; } = "https://openrouter.ai";
    public string OpenRouterModel { get; init; } = "";
    // Azure OpenAI
    public bool AzureOpenAiEnabled { get; init; } = true;
    public string AzureOpenAiApiKey { get; init; } = "";
    public string AzureOpenAiEndpoint { get; init; } = "";
    public string AzureOpenAiDeployment { get; init; } = "";
    public string AzureOpenAiApiVersion { get; init; } = "2024-02-01";

    // Estimation
    public int EnsembleSize { get; init; } = 3;
    public double EnsembleTemperature { get; init; } = 0.7;
    public int MaxEstimateTokens { get; init; } = 1024;
    public double MaxEstimateStd { get; init; } = 0.10;
    public bool LlmCostTrackingEnabled { get; init; } = true;
    public double MaxCycleApiCostUsd { get; init; } = 1.00;
    public double MaxDailyApiCostUsd { get; init; } = 10.00;
    public string ApiPricing { get; init; } = "anthropic=3/15,openai=5/15,gemini=0.10/0.40,openrouter=3/15,azure_openai=5/15";
    public bool CalibrationWeightingEnabled { get; init; } = false;
    public int CalibrationMinSamples { get; init; } = 40;
    public double CalibrationShrinkage { get; init; } = 0.50;
    public double CalibrationMaxProviderWeight { get; init; } = 0.60;

    // Sizing
    public double KellyFraction { get; init; } = 0.15;
    public double MinEdge { get; init; } = 0.12;
    public double MinTradeUsd { get; init; } = 0.5;
    public double EntryPriceBuffer { get; init; } = 0.02;
    public double MaxQuoteAgeSeconds { get; init; } = 15.0;
    public int QuoteFailureGraceCycles { get; init; } = 3;
    public double StaleQuoteHaircutPct { get; init; } = 0.25;
    public int ResolutionChecksPerCycle { get; init; } = 20;
    public double ResolutionRetryHours { get; init; } = 6.0;
    public double MaxLiveOrderBankrollPct { get; init; } = 0.25;
    public bool AllowUnsafeRisk { get; init; } = false;

    // Risk
    public double MaxPositionPct { get; set; } = 0.15;
    public double MaxTotalExposurePct { get; set; } = 1.00;
    public double MaxCategoryExposurePct { get; set; } = 0.80;
    public double MaxEventExposurePct { get; set; } = 0.30;
    public double DailyStopLossPct { get; set; } = 0.20;
    public double MaxDrawdownPct { get; set; } = 0.50;
    public int MaxConcurrentPositions { get; set; } = 8;

    // Position review / exit
    public bool EnablePositionReview { get; init; } = true;
    public double PositionStopLossPct { get; init; } = 0.20;
    public double TakeProfitPrice { get; init; } = 0.95;
    public double ExitEdgeBuffer { get; init; } = 0.05;
    public double ReviewReestimateThresholdPct { get; init; } = 0.10;
    public int ReviewEnsembleSize { get; init; } = 3;
    public bool StopLossRequiresNegativeEdge { get; init; } = true;

    // Capital
    public double InitialBankroll { get; init; } = 10000.0;

    // Polymarket credentials
    public string PolymarketPrivateKey { get; init; } = "";
    public string PolymarketFunderAddress { get; init; } = "";
    public int PolymarketChainId { get; init; } = 137;
    public int PolymarketSignatureType { get; init; } = 0;

    // CLOB API credentials (pre-generated)
    public string PolymarketApiKey { get; init; } = "";
    public string PolymarketApiSecret { get; init; } = "";
    public string PolymarketApiPassphrase { get; init; } = "";

    // Polymarket endpoints / contracts
    public string GammaApiHost { get; init; } = "";
    public string ClobHost { get; init; } = "";
    public string ExchangeAddress { get; init; } = "";
    public string NegRiskExchangeAddress { get; init; } = "";

    // Auto-claim: send CTF.redeemPositions on-chain when a winning position resolves
    public bool AutoClaim { get; init; } = true;
    public string PolygonRpcUrl { get; init; } = "https://polygon-rpc.com";
    public string CtfAddress { get; init; } = "";
    public string UsdcAddress { get; init; } = "";

    // Email notifications
    public bool EmailEnabled { get; init; }
    public string EmailSmtpHost { get; init; } = "";
    public int EmailSmtpPort { get; init; } = 587;
    public bool EmailUseTls { get; init; } = true;
    public string EmailUser { get; init; } = "";
    public string EmailPassword { get; init; } = "";
    public string EmailTo { get; init; } = "";

    // Persistence (shared between Python and .NET)
    public string DataDir { get; init; } = "data";

    public static BotConfig FromEnv()
    {
        var j = LoadJsonConfig();
        var dataDirEnv = Environment.GetEnvironmentVariable("DATA_DIR");
        var dataDirRaw = !string.IsNullOrEmpty(dataDirEnv)
            ? dataDirEnv
            : j.TryGetValue("data_dir", out var jsonDataDir) ? jsonDataDir : "data";

        // Priority: env var > polymarket_bot_config.json > default
        string Cfg(string jsonKey, string envKey, string def)
        {
            var ev = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(ev)) return ev;
            if (j.TryGetValue(jsonKey, out var jv)) return jv;
            return def;
        }

        // Backward compat: claude_model / ai_model → anthropic_model
        var legacyAnthropicModel = Cfg("claude_model", "CLAUDE_MODEL", "") is { Length: > 0 } cm ? cm
            : Cfg("ai_model", "AI_MODEL", "");

        return new BotConfig
        {
            LiveTrading = Cfg("live_trading", "LIVE_TRADING", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            ScanIntervalMinutes = int.Parse(Cfg("scan_interval_minutes", "SCAN_INTERVAL_MINUTES", "10")),
            MinLiquidity = double.Parse(Cfg("min_liquidity", "MIN_LIQUIDITY", "10000")),
            MinVolume24Hr = double.Parse(Cfg("min_volume_24hr", "MIN_VOLUME_24HR", "1000")),
            MinTimeToResolutionHours = double.Parse(Cfg("min_time_to_resolution_hours", "MIN_TIME_TO_RESOLUTION_HOURS", "48")),
            MinMarketPrice = double.Parse(Cfg("min_market_price", "MIN_MARKET_PRICE", "0.10")),
            MarketsPerCycle = int.Parse(Cfg("markets_per_cycle", "MARKETS_PER_CYCLE", "15")),
            MaxSpread = double.Parse(Cfg("max_spread", "MAX_SPREAD", "0.04")),
            KalshiShadowEnabled = Cfg("kalshi_shadow_enabled", "KALSHI_SHADOW_ENABLED", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            KalshiApiHost = Cfg("kalshi_api_host", "KALSHI_API_HOST", "https://api.elections.kalshi.com/trade-api/v2"),
            KalshiMarketsLimit = int.Parse(Cfg("kalshi_markets_limit", "KALSHI_MARKETS_LIMIT", "200")),
            KalshiMinMatchScore = double.Parse(Cfg("kalshi_min_match_score", "KALSHI_MIN_MATCH_SCORE", "0.55")),
            KalshiLlmSameThreshold = double.Parse(Cfg("kalshi_llm_same_threshold", "KALSHI_LLM_SAME_THRESHOLD", "0.90")),
            AiProvider = Cfg("ai_provider", "AI_PROVIDER", "anthropic"),
            MultiProvider = Cfg("multi_provider", "MULTI_PROVIDER", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            AnthropicEnabled = Cfg("anthropic_enabled", "ANTHROPIC_ENABLED", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            AnthropicApiKey = Cfg("anthropic_api_key", "ANTHROPIC_API_KEY", ""),
            AnthropicApiHost = Cfg("anthropic_api_host", "ANTHROPIC_API_HOST", "https://api.anthropic.com"),
            AnthropicModel = Cfg("anthropic_model", "ANTHROPIC_MODEL", legacyAnthropicModel.Length > 0 ? legacyAnthropicModel : "claude-sonnet-4-6"),
            OpenAiEnabled = Cfg("openai_enabled", "OPENAI_ENABLED", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            OpenAiApiKey = Cfg("openai_api_key", "OPENAI_API_KEY", ""),
            OpenAiApiHost = Cfg("openai_api_host", "OPENAI_API_HOST", "https://api.openai.com"),
            OpenAiModel = Cfg("openai_model", "OPENAI_MODEL", "gpt-4o"),
            GeminiEnabled = Cfg("gemini_enabled", "GEMINI_ENABLED", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            GeminiApiKey = Cfg("gemini_api_key", "GEMINI_API_KEY", ""),
            GeminiApiHost = Cfg("gemini_api_host", "GEMINI_API_HOST", "https://generativelanguage.googleapis.com"),
            GeminiModel = Cfg("gemini_model", "GEMINI_MODEL", "gemini-2.0-flash"),
            OpenRouterEnabled = Cfg("openrouter_enabled", "OPENROUTER_ENABLED", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            OpenRouterApiKey = Cfg("openrouter_api_key", "OPENROUTER_API_KEY", ""),
            OpenRouterApiHost = Cfg("openrouter_api_host", "OPENROUTER_API_HOST", "https://openrouter.ai"),
            OpenRouterModel = Cfg("openrouter_model", "OPENROUTER_MODEL", ""),
            AzureOpenAiEnabled = Cfg("azure_openai_enabled", "AZURE_OPENAI_ENABLED", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            AzureOpenAiApiKey = Cfg("azure_openai_api_key", "AZURE_OPENAI_API_KEY", ""),
            AzureOpenAiEndpoint = Cfg("azure_openai_endpoint", "AZURE_OPENAI_ENDPOINT", ""),
            AzureOpenAiDeployment = Cfg("azure_openai_deployment", "AZURE_OPENAI_DEPLOYMENT", ""),
            AzureOpenAiApiVersion = Cfg("azure_openai_api_version", "AZURE_OPENAI_API_VERSION", "2024-02-01"),
            EnsembleSize = int.Parse(Cfg("ensemble_size", "ENSEMBLE_SIZE", "3")),
            EnsembleTemperature = double.Parse(Cfg("ensemble_temperature", "ENSEMBLE_TEMPERATURE", "0.7")),
            MaxEstimateTokens = int.Parse(Cfg("max_estimate_tokens", "MAX_ESTIMATE_TOKENS", "1024")),
            MaxEstimateStd = double.Parse(Cfg("max_estimate_std", "MAX_ESTIMATE_STD", "0.10")),
            LlmCostTrackingEnabled = Cfg("llm_cost_tracking_enabled", "LLM_COST_TRACKING_ENABLED", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            MaxCycleApiCostUsd = double.Parse(Cfg("max_cycle_api_cost_usd", "MAX_CYCLE_API_COST_USD", "1.00")),
            MaxDailyApiCostUsd = double.Parse(Cfg("max_daily_api_cost_usd", "MAX_DAILY_API_COST_USD", "10.00")),
            ApiPricing = Cfg("api_pricing", "API_PRICING", "anthropic=3/15,openai=5/15,gemini=0.10/0.40,openrouter=3/15,azure_openai=5/15"),
            CalibrationWeightingEnabled = Cfg("calibration_weighting_enabled", "CALIBRATION_WEIGHTING_ENABLED", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            CalibrationMinSamples = int.Parse(Cfg("calibration_min_samples", "CALIBRATION_MIN_SAMPLES", "40")),
            CalibrationShrinkage = double.Parse(Cfg("calibration_shrinkage", "CALIBRATION_SHRINKAGE", "0.50")),
            CalibrationMaxProviderWeight = double.Parse(Cfg("calibration_max_provider_weight", "CALIBRATION_MAX_PROVIDER_WEIGHT", "0.60")),
            KellyFraction = double.Parse(Cfg("kelly_fraction", "KELLY_FRACTION", "0.15")),
            MinEdge = double.Parse(Cfg("min_edge", "MIN_EDGE", "0.12")),
            MinTradeUsd = double.Parse(Cfg("min_trade_usd", "MIN_TRADE_USD", "0.5")),
            EntryPriceBuffer = double.Parse(Cfg("entry_price_buffer", "ENTRY_PRICE_BUFFER", "0.02")),
            MaxQuoteAgeSeconds = double.Parse(Cfg("max_quote_age_seconds", "MAX_QUOTE_AGE_SECONDS", "15")),
            QuoteFailureGraceCycles = int.Parse(Cfg("quote_failure_grace_cycles", "QUOTE_FAILURE_GRACE_CYCLES", "3")),
            StaleQuoteHaircutPct = double.Parse(Cfg("stale_quote_haircut_pct", "STALE_QUOTE_HAIRCUT_PCT", "0.25")),
            ResolutionChecksPerCycle = int.Parse(Cfg("resolution_checks_per_cycle", "RESOLUTION_CHECKS_PER_CYCLE", "20")),
            ResolutionRetryHours = double.Parse(Cfg("resolution_retry_hours", "RESOLUTION_RETRY_HOURS", "6")),
            MaxLiveOrderBankrollPct = double.Parse(Cfg("max_live_order_bankroll_pct", "MAX_LIVE_ORDER_BANKROLL_PCT", "0.25")),
            AllowUnsafeRisk = Cfg("allow_unsafe_risk", "ALLOW_UNSAFE_RISK", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            MaxPositionPct = double.Parse(Cfg("max_position_pct", "MAX_POSITION_PCT", "0.15")),
            MaxTotalExposurePct = double.Parse(Cfg("max_total_exposure_pct", "MAX_TOTAL_EXPOSURE_PCT", "1.00")),
            MaxCategoryExposurePct = double.Parse(Cfg("max_category_exposure_pct", "MAX_CATEGORY_EXPOSURE_PCT", "0.80")),
            MaxEventExposurePct = double.Parse(Cfg("max_event_exposure_pct", "MAX_EVENT_EXPOSURE_PCT", "0.30")),
            DailyStopLossPct = double.Parse(Cfg("daily_stop_loss_pct", "DAILY_STOP_LOSS_PCT", "0.20")),
            MaxDrawdownPct = double.Parse(Cfg("max_drawdown_pct", "MAX_DRAWDOWN_PCT", "0.50")),
            MaxConcurrentPositions = int.Parse(Cfg("max_concurrent_positions", "MAX_CONCURRENT_POSITIONS", "8")),
            EnablePositionReview = Cfg("enable_position_review", "ENABLE_POSITION_REVIEW", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            PositionStopLossPct = double.Parse(Cfg("position_stop_loss_pct", "POSITION_STOP_LOSS_PCT", "0.20")),
            TakeProfitPrice = double.Parse(Cfg("take_profit_price", "TAKE_PROFIT_PRICE", "0.95")),
            ExitEdgeBuffer = double.Parse(Cfg("exit_edge_buffer", "EXIT_EDGE_BUFFER", "0.05")),
            ReviewReestimateThresholdPct = double.Parse(Cfg("review_reestimate_threshold_pct", "REVIEW_REESTIMATE_THRESHOLD_PCT", "0.10")),
            ReviewEnsembleSize = int.Parse(Cfg("review_ensemble_size", "REVIEW_ENSEMBLE_SIZE", "3")),
            StopLossRequiresNegativeEdge = Cfg("stop_loss_requires_negative_edge", "STOP_LOSS_REQUIRES_NEGATIVE_EDGE", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            InitialBankroll = double.Parse(Cfg("initial_bankroll", "INITIAL_BANKROLL", "10000")),
            PolymarketPrivateKey = Cfg("polymarket_private_key", "POLYMARKET_PRIVATE_KEY", ""),
            PolymarketFunderAddress = Cfg("polymarket_funder_address", "POLYMARKET_FUNDER_ADDRESS", ""),
            PolymarketChainId = int.Parse(Cfg("polymarket_chain_id", "POLYMARKET_CHAIN_ID", "137")),
            PolymarketSignatureType = int.Parse(Cfg("polymarket_signature_type", "POLYMARKET_SIGNATURE_TYPE", "0")),
            PolymarketApiKey = Cfg("polymarket_api_key", "POLYMARKET_API_KEY", ""),
            PolymarketApiSecret = Cfg("polymarket_api_secret", "POLYMARKET_API_SECRET", ""),
            PolymarketApiPassphrase = Cfg("polymarket_api_passphrase", "POLYMARKET_API_PASSPHRASE", ""),
            GammaApiHost = Cfg("gamma_api_host", "GAMMA_API_HOST", ""),
            ClobHost = Cfg("clob_host", "CLOB_HOST", ""),
            ExchangeAddress = Cfg("exchange_address", "EXCHANGE_ADDRESS", ""),
            NegRiskExchangeAddress = Cfg("neg_risk_exchange_address", "NEG_RISK_EXCHANGE_ADDRESS", ""),
            AutoClaim = Cfg("auto_claim", "AUTO_CLAIM", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            PolygonRpcUrl = Cfg("polygon_rpc_url", "POLYGON_RPC_URL", "https://polygon-rpc.com"),
            CtfAddress = Cfg("ctf_address", "CTF_ADDRESS", ""),
            UsdcAddress = Cfg("usdc_address", "USDC_ADDRESS", ""),
            EmailEnabled = Cfg("email_enabled", "EMAIL_ENABLED", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            EmailSmtpHost = Cfg("email_smtp_host", "EMAIL_SMTP_HOST", ""),
            EmailSmtpPort = int.Parse(Cfg("email_smtp_port", "EMAIL_SMTP_PORT", "587")),
            EmailUseTls = Cfg("email_use_tls", "EMAIL_USE_TLS", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            EmailUser = Cfg("email_user", "EMAIL_USER", ""),
            EmailPassword = Cfg("email_password", "EMAIL_PASSWORD", ""),
            EmailTo = Cfg("email_to", "EMAIL_TO", ""),
            DataDir = ResolveDataDir(dataDirRaw, !string.IsNullOrEmpty(dataDirEnv)),
        };
    }

    /// <summary>
    /// Load polymarket_bot_config.json, returning all values as strings (matching env var behaviour).
    /// Looks for CONFIG_FILE env var first, then walks upward from CWD/AppContext.BaseDirectory.
    /// </summary>
    private static Dictionary<string, string> LoadJsonConfig()
    {
        var configFile = FindConfigFile();

        if (string.IsNullOrEmpty(configFile) || !File.Exists(configFile))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            _configDir = Path.GetDirectoryName(configFile);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(File.ReadAllText(configFile));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Skip comment keys
                if (prop.Name.StartsWith("_")) continue;
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => prop.Value.GetRawText(),
                };
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? FindConfigFile()
    {
        var configFile = Environment.GetEnvironmentVariable("CONFIG_FILE");
        if (!string.IsNullOrEmpty(configFile))
            return Path.GetFullPath(configFile);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "polymarket_bot_config.json");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }

        return null;
    }

    private static string ResolveDataDir(string dataDir, bool fromEnv)
    {
        if (Path.IsPathRooted(dataDir))
            return Path.GetFullPath(dataDir);

        var baseDir = fromEnv ? Directory.GetCurrentDirectory() : (_configDir ?? Directory.GetCurrentDirectory());
        return Path.GetFullPath(Path.Combine(baseDir, dataDir));
    }
}
