# Polymarket Trading Bot

Autonomous trading agent for [Polymarket](https://polymarket.com) prediction markets. Scans hundreds of binary markets, estimates fair probabilities using an AI ensemble (Anthropic, Gemini, OpenAI, OpenRouter, or Azure OpenAI), finds mispricing, and executes trades with Kelly criterion sizing.

Available in **Python** and **.NET 8** — both implementations share the same logic, config, and data formats.

By default, API costs are tracked against independent USD budgets and are not deducted from trading bankroll; tracking can be disabled without disabling LLM calls. If the total trading portfolio value (bankroll + open positions) drops below $1, the agent halts.

## How It Works

```text
Every N minutes (default 10):
  1. Balance sync — fetch actual on-chain USDC, correct bankroll drift
  2. Ghost check — verify tracked positions still have on-chain tokens; write off strays
  3. Review open positions — value each holding from full-size executable bid depth, check exit rules:
     - Stop-loss: if price dropped >25%, re-estimate first; sell only when edge is gone
     - Take-profit: sell if price reached 0.95+
     - Edge-gone: sell if market moved past original fair estimate
     - Re-estimate: if price moved >10%, re-run AI ensemble to refresh fair value
     - Cooldown: block re-entering a market for 2 cycles after closing a position in it
     - Skip penny positions (price < $0.01, unsellable on CLOB)
     - Top-up tiny positions (<5 tokens) that need exit: buy 5 more, then sell all
  4. Filter new markets by liquidity, volume, spread, and time to resolution
  5. Estimate fair probability (N AI calls → equal provider mean, optionally calibration-weighted after the sample gate)
     - Skip markets where ensemble std dev > 10% (low confidence)
     - In multi-provider mode: query all configured providers, score stable providers without rewarding outliers
  6. Find net mispricing > 10% after expected entry slippage/aggression
  7. Fetch a fresh CLOB order book, calculate full-size VWAP, and reject stale/thin books
  8. Recalculate edge and fractional Kelly size at executable VWAP
  9. Check risk limits against liquidation equity (per-position, per-event, per-category, total exposure, daily stop-loss, drawdown)
  10. Execute at the quoted worst book level (paper or live via CLOB GTC limit orders)
  11. Journal the estimate/decision, optionally track API spend separately, save state, repeat
```

Only one process may own a data directory at a time. `bot.lock` prevents Python and .NET from trading the same wallet concurrently and is automatically recovered after a crashed process.

## Quick Start

### 1. Create your config file

```bash
git clone https://github.com/guberm/polymarket-bot.git
cd polymarket-bot
cp polymarket_bot_config.json.example polymarket_bot_config.json
# Edit polymarket_bot_config.json — fill in your provider API key
```

Minimum required for paper trading (Anthropic):

```json
{
  "anthropic_api_key": "sk-ant-...",
  "anthropic_api_host": "https://api.anthropic.com",
  "anthropic_model": "claude-sonnet-4-6",
  "gamma_api_host": "https://gamma-api.polymarket.com",
  "clob_host": "https://clob.polymarket.com"
}
```

### 2. Run

**Python:**

```bash
cd python
pip install -r requirements.txt
python main.py           # paper trading
python main.py --verbose # debug logging
python main.py --console # human-readable console output
```

**.NET:**

```bash
cd dotnet/PolymarketBot
dotnet run               # paper trading
dotnet run -- --verbose  # debug logging
dotnet run -- --console  # human-readable console output
```

**Windows (.bat):**

```text
run-bot.bat   ← double-click, reads polymarket_bot_config.json automatically
```

**Dashboard (Windows):**

```text
run-dashboard.vbs   ← Electron desktop app, hidden/no terminal window
run-dashboard.bat   ← fallback launcher, may briefly show a terminal
```

Or:

```bash
cd dashboard
npm install
npm start
```

## AI Providers

The bot supports five AI providers for market estimation. Set `ai_provider` to choose one, or enable `multi_provider` to query all of them simultaneously.

### Supported providers

| Provider | `ai_provider` value | Model field | Notes |
|----------|---------------------|-------------|-------|
| Anthropic (Claude) | `anthropic` | `anthropic_model` | Default. claude-sonnet-4-6 recommended |
| Google Gemini | `gemini` | `gemini_model` | gemini-2.0-flash recommended |
| OpenRouter | `openrouter` | `openrouter_model` | Proxy for 100+ models |
| OpenAI | `openai` | `openai_model` | gpt-4o recommended |
| Azure OpenAI | `azure_openai` | `azure_openai_deployment` | Enterprise Azure endpoint |

### Single-provider mode

```json
{
  "ai_provider": "gemini",
  "gemini_api_key": "AIza...",
  "gemini_model": "gemini-2.0-flash"
}
```

### Multi-provider mode (recommended)

Query all configured providers simultaneously. Each provider makes `ceil(ensemble_size / num_providers)` calls. Responses are scored by **conviction × confidence** and aggregated via trimmed mean:

- **conviction** = how far the estimate is from market price (strong disagreement with market = confident signal)
- **confidence** = 1 / std_dev (how consistent the provider's own calls were)
- Final estimate = trimmed mean of per-provider means (equal weight per provider)
- The `⭐` winner is logged; bot continues even if some providers fail

```json
{
  "multi_provider": true,
  "anthropic_api_key": "sk-ant-...",
  "anthropic_model": "claude-sonnet-4-6",
  "gemini_api_key": "AIza...",
  "gemini_model": "gemini-2.0-flash",
  "openrouter_api_key": "sk-or-v1-...",
  "openrouter_model": "anthropic/claude-sonnet-4-5"
}
```

Log output:
```
Multi-provider [Will Iran...]: consensus=12% | ⭐anthropic=8%(±0.00,s=8.00) | gemini=15%(±0.03,s=3.33) | openrouter=14%(±0.01,s=4.00)
```

### API key validation at startup

Both implementations validate all configured provider keys before starting the main loop. Only exits if **all** providers fail — a single working provider is enough to continue.

## Dashboard

An Electron desktop app that visualises the bot's state in real time.

**Features:**

- Live portfolio stats — free cash, portfolio value, realized/unrealized P&L, drawdown, win rate
- Open positions table with sortable columns, category color-coding, and per-category filters
- Trade history table with sortable columns
- Cumulative P&L and exposure-by-category charts (flicker-free)
- Risk limit meters
- Exit reason breakdown (stop-loss, take-profit, edge-gone, ghost, resolved)
- Live log — current session only, clears between restarts
- Config editor — per-provider sections (ANTHROPIC, OPENAI, GEMINI, OPENROUTER, AZURE OPENAI) with live model loading (↺ Load button fetches available models from each provider's API)
- Start / Stop bot, mode/flag preferences persist
- Light/dark theme + English/Russian UI toggle

**Requirements:** Node.js (for first-time `npm install`).

## Evaluation journal and calibration

Every completed estimate is appended to `data/estimates.jsonl`, including the original market price, executable VWAP/limit, confidence, decision, and skip reason. Resolution records allow post-hoc Brier score and calibration analysis:

`data/resolution-watchlist.json` also tracks evaluated markets that were never bought, avoiding calibration bias toward executed trades only.

```bash
cd python
python analyze_estimates.py --estimates ../data/estimates.jsonl
python replay_estimates.py --estimates ../data/estimates.jsonl --min-edge 0.08 --kelly-fraction 0.10
python historical_analogs.py --estimates ../data/estimates.jsonl
```

The replay is deterministic and offline: it applies sizing and risk overrides to the shared journal schema emitted by both the Python and .NET bots. Calibration-weighted live aggregation is opt-in and stays equal-weighted until every active provider has at least `calibration_min_samples` resolved predictions.

`historical_analogs.py` is an offline, leak-free nearest-neighbor baseline. It represents each market as one independent lifecycle episode, uses only candidates that had already resolved when the target was observed, and compares its walk-forward Brier score and log loss with both the AI estimate and market price. New journal rows include liquidity, volume, spread, order-book levels, end date, and time to resolution. The report keeps `live_gate.ready=false` until at least 100 predictions beat both baselines across three chronological folds; it never changes live signals or execution.

## Optional Kalshi shadow comparison

Kalshi can be used as a read-only independent reference. Once per scan cycle the bot downloads a bounded public market snapshot, requires matching numeric terms, ranks titles by token overlap, and verifies the best candidate with one AI call. The result is written only to `estimates.jsonl`; it never changes signals, sizing, risk checks, or execution.

```json
{
  "kalshi_shadow_enabled": true,
  "kalshi_markets_limit": 200,
  "kalshi_min_match_score": 0.55,
  "kalshi_llm_same_threshold": 0.90
}
```

## Live Trading

> **Warning:** Live trading uses real money. Start with paper trading to validate signals.

```json
{
  "live_trading": true,
  "polymarket_private_key": "0x...",
  "polymarket_funder_address": "0x...",
  "exchange_address": "0xE111180000d2663C0091e4f400237545B87B996B",
  "neg_risk_exchange_address": "0xe2222d279d744050d28e00520010520000310F59"
}
```

Signature types: `0` = EOA, `1` = Polymarket proxy, `2` = Gnosis Safe, `3` = POLY_1271 deposit wallet.

### Auto-claim (.NET only)

Automatically submits `CTF.redeemPositions` on-chain when a position resolves WON:

```json
{
  "auto_claim": true,
  "ctf_address":    "0x4D97DCd97eC945f40cF65F87097ACe5EA0476045",
  "usdc_address":   "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174",
  "polygon_rpc_url": "https://polygon-rpc.com"
}
```

## CLI Arguments

```bash
python main.py --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
dotnet run -- --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
```

Available: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--max-event-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`, `--verbose`, `--console`.

## Configuration

All settings live in **`polymarket_bot_config.json`**. See `polymarket_bot_config.json.example` for a fully annotated template. Config priority: **CLI arg → env var → config file → code default**. All keys can also be set as uppercase env vars.

### AI Provider

| Key | Default | Description |
|-----|---------|-------------|
| `ai_provider` | `anthropic` | Active provider for single-provider mode |
| `multi_provider` | `false` | Query all configured providers and aggregate |

**Per-provider fields** (each provider has its own key, host, and model):

| Provider | Key field | Host field | Model field | Default model |
|----------|-----------|------------|-------------|---------------|
| Anthropic | `anthropic_api_key` | `anthropic_api_host` | `anthropic_model` | `claude-sonnet-4-6` |
| OpenAI | `openai_api_key` | `openai_api_host` | `openai_model` | `gpt-4o` |
| Gemini | `gemini_api_key` | `gemini_api_host` | `gemini_model` | `gemini-2.0-flash` |
| OpenRouter | `openrouter_api_key` | `openrouter_api_host` | `openrouter_model` | (set manually) |
| Azure OpenAI | `azure_openai_api_key` | `azure_openai_endpoint` | `azure_openai_deployment` | (set manually) |

Azure also requires: `azure_openai_api_version` (default `2024-02-01`).

### Trading Mode

| Key | Default | Description |
|-----|---------|-------------|
| `live_trading` | `false` | Real orders on CLOB |
| `initial_bankroll` | `10000` | Starting capital in USD |

### Market Scanning

| Key | Default | Description |
|-----|---------|-------------|
| `scan_interval_minutes` | `10` | Time between cycles |
| `markets_per_cycle` | `20` | Max markets evaluated per cycle |
| `min_liquidity` | `10000` | Min pool liquidity in USD |
| `min_volume_24hr` | `500` | Min 24h trading volume |
| `min_time_to_resolution_hours` | `48` | Skip markets resolving too soon |
| `min_market_price` | `0.10` | Skip extreme prices |
| `max_spread` | `0.04` | Skip wide bid-ask spreads |
| `max_quote_age_seconds` | `15` | Reject older CLOB order-book snapshots |
| `quote_failure_grace_cycles` | `3` | Keep a haircutted last quote before marking an unavailable position at zero |
| `stale_quote_haircut_pct` | `0.25` | Haircut applied while quote failures are inside the grace window |
| `resolution_checks_per_cycle` | `20` | Bounded resolution checks for evaluated, unbought markets |
| `resolution_retry_hours` | `6` | Delay before retrying an unresolved watched market |

### Estimation

| Key | Default | Description |
|-----|---------|-------------|
| `ensemble_size` | `3` | AI calls per market (total; distributed across providers in multi mode) |
| `ensemble_temperature` | `0.7` | Temperature for diversity |
| `max_estimate_tokens` | `1024` | Max output tokens per call |
| `max_estimate_std` | `0.10` | Skip if ensemble std dev exceeds this |
| `llm_cost_tracking_enabled` | `true` | Track estimated LLM spend and enforce API budgets; `false` leaves LLM calls enabled |
| `max_cycle_api_cost_usd` | `1.00` | Stop new evaluations once cycle API spend reaches this USD budget |
| `max_daily_api_cost_usd` | `10.00` | Stop new evaluations once UTC-day API spend reaches this USD budget |
| `api_pricing` | provider map | Input/output USD per million tokens, e.g. `anthropic=3/15` |
| `calibration_weighting_enabled` | `false` | Use resolved provider Brier scores after the sample gate |
| `calibration_min_samples` | `40` | Required resolved predictions for every active provider |
| `calibration_shrinkage` | `0.50` | Shrink learned weights toward equal weights |
| `calibration_max_provider_weight` | `0.60` | Maximum weight assigned to one provider |

### Sizing

| Key | Default | Description |
|-----|---------|-------------|
| `min_edge` | `0.12` | Minimum mispricing to trade |
| `kelly_fraction` | `0.15` | Fractional Kelly multiplier |
| `min_trade_usd` | `0.5` | Minimum position size |
| `entry_price_buffer` | `0.02` | Conservative pre-book slippage used before the fresh VWAP quote |
| `max_live_order_bankroll_pct` | `0.25` | Live guardrail: skip if CLOB minimum consumes too much free cash |
| `allow_unsafe_risk` | `false` | Disable live guardrail clamps for explicit experiments |

### Risk Limits

| Key | Default | Description |
|-----|---------|-------------|
| `max_position_pct` | `0.15` | Max 15% of portfolio per position |
| `max_total_exposure_pct` | `1.00` | Max 100% in open positions |
| `max_category_exposure_pct` | `0.80` | Max 80% per category |
| `max_event_exposure_pct` | `0.30` | Max 30% across correlated markets in one event |
| `daily_stop_loss_pct` | `0.20` | Halt if daily loss > 20% |
| `max_drawdown_pct` | `0.50` | Halt if drawdown > 50% |
| `max_concurrent_positions` | `8` | Max open positions |

### Exit Rules

| Key | Default | Description |
|-----|---------|-------------|
| `enable_position_review` | `true` | Review positions each cycle |
| `position_stop_loss_pct` | `0.20` | Sell if dropped > 20% |
| `take_profit_price` | `0.95` | Sell if price ≥ 0.95 |
| `exit_edge_buffer` | `0.05` | Buffer before edge-gone exit |
| `review_reestimate_threshold_pct` | `0.10` | Re-run AI if price moved > 10% |
| `review_ensemble_size` | `3` | Ensemble size for re-estimation |
| `stop_loss_requires_negative_edge` | `true` | Confirm stop-loss with fresh estimate before selling |

### Email Notifications

| Key | Default | Description |
|-----|---------|-------------|
| `email_enabled` | `false` | Send HTML emails |
| `email_smtp_host` | — | e.g. `smtp.gmail.com` |
| `email_smtp_port` | `587` | SMTP port; blocked `587` automatically falls back to implicit TLS on `465` |
| `email_use_tls` | `true` | STARTTLS; `false` = SSL on port 465 |
| `email_user` | — | Sender address |
| `email_password` | — | App password for Gmail |
| `email_to` | — | Recipient address |

Events: bot started, trade opened/closed, ghost removed, market resolved, halted, error, stopped.

## How Estimation Works

### Single-provider mode

N independent AI calls → trimmed mean (drop highest + lowest if N ≥ 4) → confidence filter (skip if std dev > `max_estimate_std`). The current market price is shown to the AI as a Bayesian prior.

### Multi-provider mode

Each configured provider gets `ceil(ensemble_size / num_providers)` calls. Scoring:

```text
confidence       = 1 / (std_dev + 0.01)
market_deviation = |provider_mean - market_price|
score            = confidence / (1 + 8 × market_deviation)
```

The highest-scoring provider is marked `⭐` in the log. Final estimate = trimmed mean of per-provider means. Strong disagreement with the market is no longer treated as a virtue by itself; it still affects the final estimate, but the "winner" favors stable, non-extreme providers.

### Kelly Criterion Sizing

```text
provisional_price = market_price + entry_price_buffer
execution_price = full-size ask-book VWAP
edge = fair_probability - execution_price
b = (1 - execution_price) / execution_price
f* = (b × p - q) / b
bet = kelly_fraction × f* × portfolio_value
```

Capped by `max_position_pct` and available bankroll. In live mode, unless `allow_unsafe_risk=true`, the bot also clamps aggressive risk settings and skips trades where the 5-token CLOB minimum would consume too much free cash.

## Position Review & Exits

- **Ghost check** — verify on-chain token balance; if < 0.1 tokens, write off as ghost (`exit_reason = "ghost"`)
- **Stop-loss** — if price dropped > 25% from entry, re-estimate first; hold if the refreshed fair value still leaves positive edge
- **Take-profit** — sell if price ≥ 0.95
- **Edge-gone** — sell if market moved past original fair estimate
- **Re-estimation** — if price moved > 10%, re-run AI with `review_ensemble_size` calls before deciding to exit
- **Cooldown** — 2 cycles before re-entering the same market after closing
- **Top-up-and-sell** — tiny positions run only when one fresh book has enough ask depth for the top-up and enough bid depth for the full exit
- **Partial fills** — after cancellation the bot reads final matched size, persists partial BUYs, and reduces partial SELLs proportionally

## Risk Management

Six layers:
1. Per-position cap (15%)
2. Per-event cap (30%)
3. Per-category cap (80%)
4. Total exposure cap (100%)
5. Daily stop-loss (20%)
6. Max drawdown (50%)

Plus **cooldown**: blocks re-entry for 2 cycles after any close.

All limits use **portfolio value** (bankroll + open positions), not just free cash.

Live trading has additional guardrails by default:
- Full Kelly is capped at half-Kelly
- Per-position exposure is capped at 15%
- Total exposure is capped at 90%
- Daily stop-loss is capped at 25%
- Max drawdown is capped at 60%
- New entries are skipped if the CLOB minimum exceeds `max_live_order_bankroll_pct` of bankroll

Set `allow_unsafe_risk=true` only for deliberate experiments.

## Trade Analysis

Use the local analyzer to inspect realized PnL by exit reason and surface the worst trades:

```bash
cd python
python analyze_trades.py --trades ../data/trades.jsonl
```

## Agent Survival

- When `llm_cost_tracking_enabled` is `true`, API spend is tracked and limited by independent USD budgets; it does not reduce trading bankroll
- Setting `llm_cost_tracking_enabled` to `false` keeps LLM calls running but disables cost accumulation, API-budget enforcement, and dashboard budget warnings; previously saved totals are preserved
- With tracking enabled, estimation stops for the cycle/UTC-day when API spend reaches `max_cycle_api_cost_usd` or `max_daily_api_cost_usd`; the daily API counter is persisted across restarts
- Scan skipped when `bankroll < max(min_trade_usd, max_position_pct × bankroll)`
- Agent halts when liquidation equity (`bankroll + executable bid value`) falls below $1
- Stale `is_halted` flag auto-clears on restart if portfolio is healthy

## Project Structure

```text
polymarket_bot_config.json         ← Your config (gitignored — contains secrets)
polymarket_bot_config.json.example ← Fully annotated template with all fields

python/                            ← Python implementation
  main.py                            Orchestration loop
  config.py                          BotConfig — per-provider fields, no legacy claude_model/ai_model
  estimator.py                       AI ensemble — dispatches to anthropic/openai/gemini/openrouter/azure
  api_pricing.py                     Per-provider token cost calculation
  calibration.py                     Gated provider calibration weights
  execution.py                       CLOB depth walking and VWAP quotes
  kalshi_shadow.py                   Optional read-only cross-market reference
  runtime_safety.py                  Cross-language single-instance lock
  analyze_estimates.py               Brier score and calibration report
  historical_analogs.py              Leak-free historical analog evaluation
  replay_estimates.py                Deterministic offline decision replay
  market_scanner.py                  Gamma API + fresh CLOB books
  portfolio.py                       Kelly sizing, risk, cooldown, ghost removal
  trader.py                          PaperTrader + LiveTrader + ghost detection
  notifier.py                        HTML email notifications (8 event types)
  persistence.py                     Atomic portfolio + JSONL trades/estimates
  models.py                          Domain dataclasses
  logger_setup.py                    Colored console + JSON file logging

dotnet/PolymarketBot/              ← .NET 8 implementation (mirrors Python)
  Program.cs                         Async orchestration loop
  BotConfig.cs                       Config with per-provider fields
  Services/
    Estimator.cs                     Multi-provider AI ensemble + ValidateApiKeyAsync
    ApiPricing.cs                    Per-provider token cost calculation
    ExecutionPricing.cs              CLOB depth walking and VWAP quotes
    KalshiShadow.cs                  Optional read-only cross-market reference
    RuntimeSafety.cs                 Cross-language single-instance lock
    MarketScanner.cs                 Gamma API + fresh CLOB books
    Portfolio.cs                     Kelly sizing, risk, cooldown
    LiveTrader.cs                    CLOB GTC orders + ghost detection
    PaperTrader.cs                   Simulated execution
    ClobApiClient.cs                 EIP-712 + HMAC auth, orders, auto-claim
    Notifier.cs                      HTML email notifications
    PersistenceService.cs            Atomic JSON + JSONL
    JsonFileLoggerProvider.cs        JSON line logger

dotnet/PolymarketBot.SelfTests/    ← Dependency-free parity and reliability checks
tests/golden_execution.json        ← Shared Python/.NET execution vectors
resolution-watchlist.json          ← Runtime calibration outcomes queue

dashboard/                         ← Electron desktop app
  main.js                            IPC, file watchers, bot spawn, model fetching API
  preload.js                         Context bridge
  renderer.js                        UI + per-provider config sections
  index.html / styles.css            Shell + dark/light themes
```

## Disclaimer

Experimental software. Prediction market trading carries risk. Do not trade with money you cannot afford to lose.

## License

MIT
