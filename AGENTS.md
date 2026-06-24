# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project Overview

Polymarket trading bot that estimates fair market probabilities via an AI ensemble (Anthropic, Gemini, OpenAI, OpenRouter, or Azure OpenAI), finds mispricing, and executes trades on Polymarket with Kelly criterion sizing. The agent pays for its own inference from its bankroll.

Two implementations: **Python** (`python/`) and **.NET 8** (`dotnet/PolymarketBot/`). Both share the same logic, config, and data formats.

## Running

### Config file (primary)

All settings live in **`polymarket_bot_config.json`** at the project root (gitignored — contains secrets). See `polymarket_bot_config.json.example` for the full annotated template.

Minimum for paper trading:

```json
{
  "anthropic_api_key": "sk-ant-...",
  "anthropic_api_host": "https://api.anthropic.com",
  "anthropic_model": "Codex-sonnet-4-6",
  "gamma_api_host": "https://gamma-api.polymarket.com",
  "clob_host": "https://clob.polymarket.com"
}
```

Config priority (highest wins): **CLI arg → env var → polymarket_bot_config.json → code default**

### Python

```bash
cd python
pip install -r requirements.txt
python main.py           # paper trading
python main.py --verbose # debug logging
python main.py --console # human-readable CLI prints
```

### .NET

```bash
cd dotnet/PolymarketBot
dotnet run               # paper trading
dotnet run -- --verbose  # debug logging
dotnet run -- --console  # human-readable CLI prints
```

### Windows

Double-click `run-bot.bat` — reads `polymarket_bot_config.json` automatically.

### CLI risk overrides

```bash
python main.py --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
dotnet run -- --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
```

Available: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`, `--verbose`, `--console`.

No test suite or linter configured.

## Architecture

### Python (`python/`)

```text
python/
  main.py            – Orchestration loop
  config.py          – BotConfig — per-provider fields, backward compat for claude_model/ai_model
  estimator.py       – Multi-provider AI ensemble: Anthropic/OpenAI/Gemini/OpenRouter/Azure
  notifier.py        – HTML email notifications
  models.py          – Domain dataclasses
  market_scanner.py  – Gamma API pagination, market filtering, CLOB price quotes
  portfolio.py       – Kelly sizing, risk limits, cooldown, ghost removal, position review
  trader.py          – PaperTrader + LiveTrader + ghost detection
  persistence.py     – Atomic JSON portfolio + JSONL trade log
  logger_setup.py    – Colored console + JSON lines file logger
  requirements.txt   – Python dependencies (requests, anthropic, py-clob-client)
```

### .NET (`dotnet/PolymarketBot/`)

```text
dotnet/PolymarketBot/
  Program.cs               – Async orchestration loop
  BotConfig.cs             – Config — per-provider fields, backward compat
  Models/                  – Enums, domain models
  Services/
    Estimator.cs           – Multi-provider AI ensemble (EstimateAsync, EstimateMultiAsync, ValidateApiKeyAsync)
    MarketScanner.cs       – Gamma API + spread filter
    Portfolio.cs           – Kelly sizing, risk checks, cooldown, ghost removal
    Notifier.cs            – HTML email notifications
    ClobApiClient.cs       – EIP-712 + HMAC CLOB auth, orders, auto-claim
    ITrader.cs / LiveTrader.cs / PaperTrader.cs
    PersistenceService.cs  – Atomic JSON + JSONL
    JsonFileLoggerProvider.cs
```

**Data flow per cycle:**

1. **Balance sync** — fetch on-chain USDC, sync bankroll
2. **Ghost check** — verify on-chain token balances; write off positions with < 0.1 tokens
3. **Position review** — fetch prices, run exits (stop-loss/take-profit/edge-gone), optionally re-estimate, topup-and-sell tiny positions
4. `MarketScanner.Scan()` → filtered `MarketInfo` list (liquidity, volume, spread, price, time)
5. `Estimator.Estimate()` → `Estimate` (single or multi-provider ensemble, trimmed mean, confidence filter)
6. `Portfolio.GenerateSignal()` → `Signal` when edge > `min_edge`
7. `Portfolio.CheckRisk()` → 5-layer risk + cooldown
8. `PaperTrader/LiveTrader.Execute()` → `Trade` + `Position`
9. `Persistence` → save snapshot + append trade

**External APIs:**

- Gamma API (`gamma-api.polymarket.com/events`) — market discovery
- CLOB API (`clob.polymarket.com`) — price quotes + live orders
- Anthropic / OpenAI / Gemini / OpenRouter / Azure API — AI estimation

## Key Design Decisions

- **Multi-provider AI estimation** — `multi_provider: true` queries ALL configured providers simultaneously. Each provider gets `ceil(ensemble_size / num_providers)` calls. Scored by `conviction × confidence` (conviction = |estimate - market_price|, confidence = 1/(std_dev + 0.01)). Final estimate = trimmed mean of per-provider means. Bot stops only if ALL providers fail validation.
- **Per-provider model fields** — `anthropic_model`, `openai_model`, `gemini_model`, `openrouter_model` are fully independent. No fallback between providers. Defaults: Anthropic=`Codex-sonnet-4-6`, OpenAI=`gpt-4o`, Gemini=`gemini-2.0-flash`.
- **Per-provider `*_enabled` flags** — each provider has `anthropic_enabled`, `gemini_enabled`, `openai_enabled`, `openrouter_enabled`, `azure_openai_enabled` (default true). A provider is only included if BOTH `*_enabled: true` AND its API key is set. Checked in `_get_configured_providers()` (Python) and `GetConfiguredProviders()` (.NET).
- **No legacy `claude_model`/`ai_model` fields** — removed from codebase. JSON values are still read for backward compat (populate `anthropic_model`), but don't create new configs with them.
- **API key validation at startup** — both implementations make a minimal 1-token call per configured provider. Multi mode logs `✓`/`✗` per provider; continues if at least one passes.
- **Provider rate-limit cooldown** — in multi-provider mode (.NET), if a provider exhausts all 429 retries for a market, it's added to `_rateLimitedThisCycle` (HashSet) and skipped instantly for all remaining markets that cycle. `ResetCycle()` clears it at the start of each new cycle. Prevents one rate-limited provider from adding 70+ seconds of retry delays per cycle.
- **Bug fix: ParseProviderResponse** — was always using `_config.AiProvider` to decide parse format (always parsed as anthropic in multi-mode). Now takes `provider` string parameter. This caused azure_openai responses to be parsed as Anthropic format → KeyNotFoundException.
- **Config dump at startup** — after the banner, logs 4 sections: `── AI ──`, `── RISK ──`, `── SCAN ──`, `── EXITS ──` with all key parameters. Helps verify which settings are active.
- **Startup email expanded** — `NotifyStarted`/`notify_started` now shows 4 sections: Portfolio (mode/bankroll/positions), AI (provider/ensemble/min_edge), Risk limits (all 6), Scan (interval/markets/liquidity/volume/spread).
- **Binary markets only** — filters out non-binary outcomes
- **Estimator system prompt** shows current market price as a Bayesian prior — Codex is told to treat market consensus as an anchor
- **Anthropic TextBlock safety** — `response.content[0]` can be ThinkingBlock/ToolUseBlock etc. Always use `next(b for b in response.content if hasattr(b, "text"), None)` not `.content[0].text`
- **Ghost position detection** — each cycle (live only), actual on-chain conditional token balance checked. < 0.1 tokens = ghost: written off immediately with `exit_reason="ghost"`, email notification
- **Position cooldown** — after any close (stop-loss/take-profit/edge-gone/resolved/ghost), blocks re-entry for 2 scan cycles. In-memory, resets on restart. Prevents flip-flopping.
- **Re-estimation during review** — if price moved > `review_reestimate_threshold_pct` (10%), re-run AI with `review_ensemble_size` calls to refresh `fair_estimate_at_entry`
- **CLOB minimum pre-check** uses `price + 0.02` (aggressive price after 2-tick BUY adjustment), not raw market price. Prevents calling AI only to fail at order execution.
- **Tick size** — CLOB `/tick-size` API may return `Number` or `String` JSON. Always handle both value kinds.
- **Confidence filter** — if ensemble std dev > `max_estimate_std` (10%), skip market: `SKIP (low confidence)`
- **Spread filter** — `max_spread = 0.04`: skip markets with wide bid-ask spreads
- **Gamma API JSON quirk** — `outcomes`, `outcomePrices`, `clobTokenIds` can be JSON-encoded strings or actual arrays
- **Risk is layered** — 5 layers: per-position (15%), per-category (80%), total exposure (100%), daily stop-loss (20%), max drawdown (50%). Plus cooldown (6th layer).
- **Config file** `polymarket_bot_config.json` at project root. `CONFIG_FILE` env var overrides path. Priority: CLI arg → env var → config file → code default
- **HTML email notifications** — all events use color-coded HTML templates. Events: started, trade, sell, topup+sell, ghost_removed, resolved, halted, daily_reset, error, stopped
- **CLI args** override env vars/config for risk params
- **Agent pays for inference** — API token costs deducted each cycle
- **Atomic persistence** — portfolio.json written via tmp+rename
- **Polygon chain** (chain ID 137) for Polymarket settlement
- **Live trading** uses GTC limit orders. BUY = midpoint + 2 ticks (taker aggression). SELL = midpoint − 2 ticks. Poll 5×3s for MATCHED status, cancel if unfilled.
- **Top-up-and-sell** for tiny positions (< 5 tokens): buy 5 tokens, then sell all
- **Agent survival** — estimation stops at `bankroll < $0.30`; scan skips when bankroll too low for minimum position; truly halts at `bankroll + total_exposure < $1`. `IsHalted` auto-clears on restart if portfolio healthy.
- **Scan skip threshold** = `max(MinTradeUsd, MaxPositionPct × bankroll)` — free cash only
- **.NET Estimator** uses raw HttpClient to provider REST APIs (no SDK for non-Anthropic providers). Python uses `anthropic` SDK for Anthropic, `requests` for others.
- **.NET CLOB auth** implements EIP-712 signing + HMAC-SHA256 using Nethereum.Signer
- **Auto-claim** (.NET only) — WON position detected → `ClobApiClient.RedeemWinningPositionAsync()` submits raw EIP-155 tx to Polygon
- **Azure OpenAI config note** — `azure_openai_deployment` must match the deployment name exactly (e.g. `gpt-4o-mini`). Without it, azure_openai is excluded from `GetConfiguredProviders()`.

## Dashboard (`dashboard/`)

Electron desktop app for real-time bot monitoring.

### Dashboard Running

```bash
# Windows: double-click run-dashboard.bat (detaches electron, closes CMD immediately)
# Or:
cd dashboard && npm install && npm start
```

### Files

```text
dashboard/
  main.js            Main process: IPC handlers, file watchers, bot process management, fetch-ai-models handler
  preload.js         Context bridge (exposes api.* including fetchAiModels)
  renderer.js        All UI logic: stats, tables, charts, log, per-provider config sections
  index.html         UI shell
  styles.css         Dark/light theme
  package.json       electron ^33.0.0 devDependency
  setup-icon.js      Icon generator — run once: `node setup-icon.js`. Generates icon.png (256×256, Polymarket blue #1652F0, white "P", rounded corners) using pure Node.js (zlib + manual PNG encoding).
  [runtime]          dashboard-settings.json — created at runtime in bot root (next to polymarket_bot_config.json). Stores persistent settings (lang, theme, panel sizes, bot options).
```

### Config Editor — Provider Sections

The config form is organized into per-provider sections: AI PROVIDER, ANTHROPIC, OPENAI, GEMINI, OPENROUTER, AZURE OPENAI. Each provider section has its own API Key, API Host, and Model field.

Model fields use `type: 'model-select'` with a **↺ Load** button. The `loadFrom` property (not `providers`) tells the button which provider API to call for model loading. The `providers` property on AI PROVIDER section fields is for show/hide logic only.

The `fetch-ai-models` IPC handler in `main.js` calls each provider's live model API using Node `fetch()`.

### Key Patterns

- **Bot spawn**: `shell: false` for direct `.exe` path. `shell: true` for `python`/`dotnet run`.
- **Log isolation**: `logClearedAt = Date.now()` on load hides pre-existing entries.
- **Log rotation**: `bot.log` → `bot-TIMESTAMP.log` before each new bot start.
- **Log copy button**: `⎘ copy` button in log controls (next to export). Copies current visible log lines to clipboard. Shows `✓` for 1.5s as confirmation. No new IPC channel needed (clipboard API).
- **Timestamp normalization**: `parseTs(ts)` handles .NET's 7-decimal `ToString("o")`.
- **Charts**: `animation: false` init; `chart.update('none')` — no flicker.
- **FileShare (.NET)**: `new FileStream(..., FileShare.ReadWrite)` for concurrent dashboard + bot access.
- **Stale exe**: after .NET changes, `dotnet build -c Debug` from `dotnet/PolymarketBot/`.
- **File watcher**: 300ms debounce + `name === null` fallback.
- **`t` variable shadowing**: `refresh()` must use `[p, tr, l]` not `[p, t, l]`.
- **i18n**: `TRANS = { ru:{}, en:{} }` + `t(key,...args)`. Text-node update in `applyLang()`.
- **Tooltips**: single `position:fixed` div in `<body>` — avoids `overflow:hidden` clipping.
- **Settings persistence**: `dashboard-settings.json` in bot root, read/written via IPC `read-settings`/`write-settings`. Replaces localStorage. Loaded async at boot before `initTheme`/`initLang`/`setupResize`. Persists: `lang`, `theme`, `bot-mode`, `bot-verbose`, `bot-console`, `panel-left-w`, `panel-upper-h`.
- **Panel sizes persist**: `dragResize` has `onDone` callback. Raw `newW`/`newH` values saved via `setSetting` on mouseup. Restored in `setupResize` before wiring drag handlers.
- **No-terminal launch**: `run-dashboard.bat` uses `start "" "electron.exe" .` to detach electron as a separate process and immediately close CMD. Alternative: `run-dashboard.vbs` for a truly hidden launch.
- **Icon**: `dashboard/setup-icon.js` — run once to generate `icon.png`. Referenced in `BrowserWindow` `icon` option.
- **Rate-limit cooldown** (.NET): `_rateLimitedThisCycle` (HashSet) tracks providers that exhausted 429 retries for a market. Skipped instantly for remaining markets. Cleared by `ResetCycle()` at start of each cycle.

### IPC Channels

`read-portfolio`, `read-trades`, `read-logs`, `read-config`, `write-config`, `get-data-dir`, `set-data-dir`, `browse-data-dir`, `bot-status`, `start-bot`, `stop-bot`, `save-file`, `open-logs-dir`, `fetch-ai-models`, `read-settings`, `write-settings`

Push events (main → renderer): `file-changed`, `bot-output`, `bot-stopped`
