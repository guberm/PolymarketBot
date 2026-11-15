# Bot Memory

Running notes between Claude Code sessions. Not a changelog — just current state, known issues, and context useful to restore quickly.

---

## Current State (as of 2026-11-15)

- **Mode:** LIVE on Polygon (chain ID 137), bot v1.7.0+ (master)
- **Network:** bridge proxy — the Windows bot runs natively and sends ALL HTTP
  (gamma, CLOB, geoblock) through a tinyproxy on a Hetzner Helsinki VPS
  (allowed region, geoblock FI). Commercial VPN exit pools (Surfshark IL/CY)
  get order POSTs tarpitted by Polymarket risk systems (~60 s hangs + Gamma 406s) —
  that is why the bridge exists.
- **Proxy config:** `network_mode: "proxy"` + structured `proxy_*` fields in
  `polymarket_bot_config.json`; the dashboard composes `HTTP_PROXY`/`HTTPS_PROXY`
  env for the bot process. Credentials live in the local config (gitignored) and
  in the access email sent 2026-11-15.
- **Reliability systems:** startup recovery reconciles lost-response intents
  against on-chain state; transport circuit breaker (2 consecutive unknown
  order outcomes → 15-min pause, persisted in `data/transport-breaker.json`);
  external-close accounting; gamma 406 diagnostics (`gzip_required` was the
  historical cause — automatic decompression is enabled).
- **Verified live:** take-profit SELL executed through the bridge
  (9.58 NO @ 0.973, +$4.82), balance sync confirmed on-chain.
- **Wallet:** Gnosis Safe (`polymarket_signature_type: 1`), funder
  `0x6e1e4f95258f110d2498e3a2a0c9fed3375bd2d9` (public on-chain data).
- **Known noise:** `wallet_flow_shadow` endpoint returns 410 Gone (disabled in
  config); npm audit reports 2 high (Electron/undici) — accepted.

## AI Provider System

### Config structure (no legacy fields)

Each provider has exactly three config fields — key, host, model:

```json
"ai_provider": "anthropic",
"multi_provider": true,

"anthropic_api_key": "sk-ant-...",
"anthropic_api_host": "https://api.anthropic.com",
"anthropic_model": "claude-sonnet-4-6",

"gemini_api_key": "AIza...",
"gemini_api_host": "https://generativelanguage.googleapis.com",
"gemini_model": "gemini-2.0-flash",

"openrouter_api_key": "sk-or-v1-...",
"openrouter_api_host": "https://openrouter.ai",
"openrouter_model": "anthropic/claude-sonnet-4-5",

"openai_api_key": "",
"openai_api_host": "https://api.openai.com",
"openai_model": "gpt-4o",

"azure_openai_api_key": "...",
"azure_openai_endpoint": "https://...",
"azure_openai_deployment": "",
"azure_openai_api_version": "2024-02-01"
```

Removed: `claude_model`, `ai_model` (backward compat still reads them → populate `anthropic_model`).

### Multi-provider scoring

```
conviction  = |provider_mean - market_price|   (disagreement with market)
confidence  = 1 / (std_dev + 0.01)             (consistency of own calls)
score       = conviction × confidence
```

Winner `⭐` is logged; final estimate = trimmed mean of per-provider means (equal weight). Bot only stops if ALL providers fail validation.

### Validation at startup

- Single mode: validates the one provider, exits on 401/403
- Multi mode: validates all configured providers. Logs `✓`/`✗` per provider. Continues if at least one passes.

---

## New Features (recent additions)

- **Per-provider enable/disable flags** — `anthropic_enabled`, `gemini_enabled`, `openai_enabled`, `openrouter_enabled`, `azure_openai_enabled` in config (all default `true`). A provider is active only if enabled AND api_key is non-empty.
- **Provider rate-limit cooldown (.NET)** — `_rateLimitedThisCycle: HashSet<string>`. Providers that give up on 429 this cycle are skipped instantly. `ResetCycle()` called at start of each cycle from `Program.cs`.
- **Config dump at startup** — Program.cs logs full config after banner in 4 sections: `── AI ──`, `── RISK ──`, `── SCAN ──`, `── EXITS ──`
- **Expanded startup email** — `NotifyStarted` builds 4-section email: Portfolio / AI / Risk limits / Scan
- **Log copy button** — clipboard copy in dashboard log panel, 1.5s `✓` feedback
- **Dashboard settings persistence** — `read-settings`/`write-settings` IPC; stores lang, theme, bot-mode, bot-verbose, bot-console, panel sizes in `dashboard-settings.json`
- **Panel size persistence** — `dragResize` `onDone` callback saves panel dimensions to settings
- **Dashboard icon** — run `node setup-icon.js` once from `dashboard/` to generate `icon.png`

---

## Key Features (all implemented, both Python + .NET)

### Ghost Position Detection

Each cycle (live only): verify on-chain token balance via CLOB `/balance-allowance`. Balance < 0.1 → ghost: write off, `exit_reason="ghost"`, email notification (purple), cooldown entry.

### Position Cooldown

After closing any position (stop-loss / take-profit / edge-gone / resolved / ghost): block re-entry for `scan_interval_minutes × 2` seconds (2 cycles). In-memory only — resets on restart.

### Re-estimation During Review

If price moved > `review_reestimate_threshold_pct` (10%) since entry: re-run AI with `review_ensemble_size` (3) calls. Updates `fair_estimate_at_entry` before edge-gone logic.

### Confidence Filter

Skip market if ensemble std dev > `max_estimate_std` (10%). Logs `SKIP (low confidence)`.

### Spread Filter

Skip markets with bid-ask spread > `max_spread` (4¢). Thin liquidity, poor fill quality.

### CLOB Minimum Pre-check

Pre-scan check uses `price + 0.02` (aggressive price after 2-tick BUY adjustment) so we don't call AI only to fail at order execution. Previously used raw market price which underestimated cost.

### Tick Size Bug Fix

`GetTickSizeAsync` (.NET) now handles both `String` and `Number` JSON value kinds from CLOB `/tick-size` API.

### HTML Emails

All notifications use HTML templates with color-coded event types. Events: started, trade, sell, topup_sell, ghost_removed, resolved, halted, daily_reset, error, stopped.

---

## Known Issues / Notes

- **Gemini free tier** rate-limits heavily in multi-provider mode — disable with `"gemini_enabled": false` in config
- **Azure OpenAI** requires `azure_openai_deployment` to be set (the deployment name, e.g. `gpt-4o-mini`) — not the model name
- **Config is read at startup only** — both Python and .NET load config once. Restart required for any config changes to take effect.

---

## Architecture Reminders

- Config priority: CLI arg → env var → `polymarket_bot_config.json` → code default
- `polymarket_bot_config.json` is gitignored; `polymarket_bot_config.json.example` is the template
- Both Python and .NET must stay in sync — mirror every logic change
- `IsHalted` auto-clears on restart if `bankroll + TotalExposure() > $1`
- Scan skip threshold = `max(MinTradeUsd, MaxPositionPct × bankroll)` — free cash only
- CLOB minimum = 5 tokens per order; TopupAndSell for tiny positions
- Bankroll can be negative when capital is locked in positions — normal

---

## Config Defaults (code-level)

| Setting | Default |
|---------|---------|
| `ai_provider` | `anthropic` |
| `multi_provider` | `false` |
| `anthropic_model` | `claude-sonnet-4-6` |
| `openai_model` | `gpt-4o` |
| `gemini_model` | `gemini-2.0-flash` |
| `openrouter_model` | (empty) |
| `scan_interval_minutes` | 10 |
| `markets_per_cycle` | 15 |
| `min_liquidity` | 10000 |
| `min_volume_24hr` | 1000 |
| `max_spread` | 0.04 |
| `ensemble_size` | 3 |
| `max_estimate_std` | 0.10 |
| `min_edge` | 0.12 |
| `kelly_fraction` | 0.15 |
| `min_trade_usd` | 0.5 |
| `max_position_pct` | 15% |
| `max_total_exposure_pct` | 100% |
| `max_category_exposure_pct` | 80% |
| `daily_stop_loss_pct` | 20% |
| `max_drawdown_pct` | 50% |
| `max_concurrent_positions` | 8 |
| `position_stop_loss_pct` | 20% |
| `take_profit_price` | 0.95 |
| `review_reestimate_threshold_pct` | 0.10 |
| `review_ensemble_size` | 3 |
| `auto_claim` | true (.NET only) |
| `polygon_rpc_url` | https://polygon-rpc.com |
