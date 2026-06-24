# Polymarket Trading Bot

Autonomous trading agent for [Polymarket](https://polymarket.com) prediction markets. Scans hundreds of binary markets, estimates fair probabilities using an AI ensemble (Anthropic, Gemini, OpenAI, OpenRouter, or Azure OpenAI), finds mispricing, and executes trades with Kelly criterion sizing.

Available in **Python** and **.NET 8** — both implementations share the same logic, config, and data formats.

API costs are tracked against independent USD budgets and are not deducted from trading bankroll. If the total trading portfolio value (bankroll + open positions) drops below $1, the agent halts.

## How It Works

```text
Every N minutes (default 10):
  1. Balance sync — fetch actual on-chain USDC, correct bankroll drift
  2. Ghost check — verify tracked positions still have on-chain tokens; write off strays
  3. Review open positions — fetch current prices, check exit rules:
     - Stop-loss: if price dropped >25%, re-estimate first; sell only when edge is gone
     - Take-profit: sell if price reached 0.95+
     - Edge-gone: sell if market moved past original fair estimate
     - Re-estimate: if price moved >10%, re-run AI ensemble to refresh fair value
     - Cooldown: block re-entering a market for 2 cycles after closing a position in it
     - Skip penny positions (price < $0.01, unsellable on CLOB)
     - Top-up tiny positions (<5 tokens) that need exit: buy 5 more, then sell all
  4. Filter new markets by liquidity, volume, spread, and time to resolution
  5. Estimate fair probability (N AI calls → trimmed mean)
     - Skip markets where ensemble std dev > 10% (low confidence)
     - In multi-provider mode: query all configured providers, score stable providers without rewarding outliers
  6. Find net mispricing > 10% after expected entry slippage/aggression
  7. Size position using fractional Kelly criterion at expected executable price
  8. Check risk limits (per-position, per-category, total exposure, daily stop-loss, drawdown)
  9. Execute trade (paper or live via CLOB GTC limit orders, +2 ticks aggression for immediate fills)
  10. Deduct API costs from bankroll, save state, repeat
```

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

## Live Trading

> **Warning:** Live trading uses real money. Start with paper trading to validate signals.

```json
{
  "live_trading": true,
  "polymarket_private_key": "0x...",
  "polymarket_funder_address": "0x...",
  "exchange_address": "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E",
  "neg_risk_exchange_address": "0xC5d563A36AE78145C45a50134d48A1215220f80a"
}
```

For Gnosis Safe wallets: `"polymarket_signature_type": 1`.

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

Available: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`, `--verbose`, `--console`.

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

### Estimation

| Key | Default | Description |
|-----|---------|-------------|
| `ensemble_size` | `3` | AI calls per market (total; distributed across providers in multi mode) |
| `ensemble_temperature` | `0.7` | Temperature for diversity |
| `max_estimate_tokens` | `1024` | Max output tokens per call |
| `max_estimate_std` | `0.10` | Skip if ensemble std dev exceeds this |
| `max_cycle_api_cost_usd` | `1.00` | Stop new evaluations once cycle API spend reaches this USD budget |
| `max_daily_api_cost_usd` | `10.00` | Stop new evaluations once UTC-day API spend reaches this USD budget |

### Sizing

| Key | Default | Description |
|-----|---------|-------------|
| `min_edge` | `0.10` | Minimum mispricing to trade |
| `kelly_fraction` | `0.20` | Fractional Kelly multiplier |
| `min_trade_usd` | `0.5` | Minimum position size |
| `entry_price_buffer` | `0.02` | Expected BUY slippage/aggression included in net-edge sizing |
| `max_live_order_bankroll_pct` | `0.25` | Live guardrail: skip if CLOB minimum consumes too much free cash |
| `allow_unsafe_risk` | `false` | Disable live guardrail clamps for explicit experiments |

### Risk Limits

| Key | Default | Description |
|-----|---------|-------------|
| `max_position_pct` | `0.15` | Max 15% of portfolio per position |
| `max_total_exposure_pct` | `1.00` | Max 100% in open positions |
| `max_category_exposure_pct` | `0.80` | Max 80% per category |
| `daily_stop_loss_pct` | `0.20` | Halt if daily loss > 20% |
| `max_drawdown_pct` | `0.50` | Halt if drawdown > 50% |
| `max_concurrent_positions` | `10` | Max open positions |

### Exit Rules

| Key | Default | Description |
|-----|---------|-------------|
| `enable_position_review` | `true` | Review positions each cycle |
| `position_stop_loss_pct` | `0.25` | Sell if dropped > 25% |
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
| `email_smtp_port` | `587` | SMTP port |
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
execution_price = market_price + entry_price_buffer
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
- **Top-up-and-sell** — tiny positions (< 5 tokens) buy 5 more then sell all

## Risk Management

Five layers:
1. Per-position cap (15%)
2. Per-category cap (80%)
3. Total exposure cap (100%)
4. Daily stop-loss (20%)
5. Max drawdown (50%)

Plus **cooldown** (6th layer): blocks re-entry for 2 cycles after any close.

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

- API spend is tracked and limited by independent USD budgets; it does not reduce trading bankroll
- Estimation stops for the cycle/UTC-day when API spend reaches `max_cycle_api_cost_usd` or `max_daily_api_cost_usd`; the daily API counter is persisted across restarts
- Scan skipped when `bankroll < max(min_trade_usd, max_position_pct × bankroll)`
- Agent halts when `bankroll + exposure < $1`
- Stale `is_halted` flag auto-clears on restart if portfolio is healthy

## Project Structure

```text
polymarket_bot_config.json         ← Your config (gitignored — contains secrets)
polymarket_bot_config.json.example ← Fully annotated template with all fields

python/                            ← Python implementation
  main.py                            Orchestration loop
  config.py                          BotConfig — per-provider fields, no legacy claude_model/ai_model
  estimator.py                       AI ensemble — dispatches to anthropic/openai/gemini/openrouter/azure
  market_scanner.py                  Gamma API + spread filter
  portfolio.py                       Kelly sizing, risk, cooldown, ghost removal
  trader.py                          PaperTrader + LiveTrader + ghost detection
  notifier.py                        HTML email notifications (8 event types)
  persistence.py                     Atomic JSON portfolio + JSONL trades
  models.py                          Domain dataclasses
  logger_setup.py                    Colored console + JSON file logging

dotnet/PolymarketBot/              ← .NET 8 implementation (mirrors Python)
  Program.cs                         Async orchestration loop
  BotConfig.cs                       Config with per-provider fields
  Services/
    Estimator.cs                     Multi-provider AI ensemble + ValidateApiKeyAsync
    MarketScanner.cs                 Gamma API + spread filter
    Portfolio.cs                     Kelly sizing, risk, cooldown
    LiveTrader.cs                    CLOB GTC orders + ghost detection
    PaperTrader.cs                   Simulated execution
    ClobApiClient.cs                 EIP-712 + HMAC auth, orders, auto-claim
    Notifier.cs                      HTML email notifications
    PersistenceService.cs            Atomic JSON + JSONL
    JsonFileLoggerProvider.cs        JSON line logger

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
