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
git clone https://github.com/guberm/PolymarketBot.git
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

## Bot-only VPN and proxy (Windows)

The Dashboard can route **only the launched bot** through a VPN or HTTP/HTTPS proxy. It does not change the Windows system connection, browser traffic, or other applications. `direct` remains the default.

| Mode | What you provide | Isolation |
|------|------------------|-----------|
| `direct` | Nothing | Uses the normal Windows connection |
| `proxy` | HTTP/HTTPS host, port, and optional credentials | Proxy variables are passed only to the bot process |
| `wireguard` | `.conf` file and, when needed, matching private/public keys | Runs inside a dedicated WSL network namespace |
| `openvpn` | `.ovpn` file and Surfshark service username/password | Runs inside a dedicated WSL network namespace |

### Configure through the Dashboard

1. Open **⚙ Config → NETWORK**.
2. Choose **Bot Network**: Direct, HTTP/HTTPS proxy, WireGuard, or OpenVPN.
3. For a VPN, select the local `.conf`/`.ovpn` file through **VPN Config File**.
4. Enter the WireGuard keys or OpenVPN service credentials when required.
5. Save the config and start the bot from the Dashboard.

WireGuard example:

```json
{
  "network_mode": "wireguard",
  "vpn_config_path": "C:\\VPN\\il-tlv.conf",
  "vpn_wsl_distro": "Ubuntu",
  "wireguard_private_key": "<private key>",
  "wireguard_public_key": "<matching public key>"
}
```

OpenVPN example:

```json
{
  "network_mode": "openvpn",
  "vpn_config_path": "C:\\VPN\\il-tlv.prod.surfshark.com_udp.ovpn",
  "vpn_wsl_distro": "Ubuntu",
  "openvpn_username": "<Surfshark service username>",
  "openvpn_password": "<Surfshark service password>"
}
```

Proxy example:

```json
{
  "network_mode": "proxy",
  "proxy_type": "http",
  "proxy_host": "proxy.example.com",
  "proxy_port": 8080,
  "proxy_username": "",
  "proxy_password": "",
  "proxy_bypass": "localhost,127.0.0.1"
}
```

### Proxy bridge to your own VPS (production-proven for restricted regions)

If the bot host sits in a Polymarket-restricted region (e.g. Canada/Ontario) **and**
VPN-provider exit pools get their order POSTs tarpitted by risk systems (observed:
~60 s hangs on `POST /order` followed by Gamma 406s across several Surfshark pools),
the reliable setup is a **bridge proxy on your own VPS in an allowed region**:

1. Rent any cheap VPS in an allowed region (Hetzner Helsinki was verified: geoblock
   `FI`, `POST /order` answered in 0.06 s). Check the IP before configuring anything:
   ```bash
   curl -m 15 -s -o /dev/null -w 'HTTP %{http_code} in %{time_total}s\n' \
     -X POST https://clob.polymarket.com/order -H 'Content-Type: application/json' -d '{}'
   ```
   A fast 4xx (401/403) means the IP is clean; a hang means pick another provider/region.
2. Install a proxy on the VPS and protect it with BasicAuth:
   ```bash
   apt install -y tinyproxy
   printf 'Port 8888\nBasicAuth bridge <password>\nAllow 0.0.0.0/0\nAllow ::/0\n' >> /etc/tinyproxy/tinyproxy.conf
   systemctl restart tinyproxy
   ```
3. Point the bot at it (Dashboard ⚙ Config → NETWORK → mode `proxy`, or the config key
   `polymarket_proxy_url` below). The dashboard passes proxy env vars to the bot process;
   the bot-level `polymarket_proxy_url` key takes precedence when set.
4. Verify from the bot host through the proxy — geoblock must report the VPS region and
   the startup log must show `All bot HTTP traffic via bridge proxy`.

Notes:
- The bot-level key is optional: set `polymarket_proxy_url` to override the structured
  `proxy_*` fields, e.g. `"polymarket_proxy_url": "http://user:pass@203.0.113.10:8888"`.
- gamma-api serves `/events` compressed and answers `406 {"error":"gzip_required"}` to
  clients without `Accept-Encoding: gzip`; the .NET client enables automatic
  decompression, and 406 responses now log a `406 diagnostics:` line (server + body)
  so the real reason is visible in `bot.log`.

### VPN runtime and safety

- Requires Windows, WSL2, and the configured Linux distribution (default: `Ubuntu`).
- On first start, missing Ubuntu networking packages are installed automatically.
- The runner creates a dedicated network namespace and starts only the selected Python or .NET bot inside it.
- A kill switch blocks direct bot traffic; only the VPN endpoint is reachable outside the tunnel.
- WireGuard tries resolved endpoint IPs until one completes a handshake, then verifies the external VPN IP.
- If the tunnel has no Internet access, startup fails closed and the bot is not started.
- Stopping the bot removes the namespace, temporary credentials, routes, and firewall rules.

VPN/proxy modes are applied by the Dashboard launcher. Direct `python main.py`, `dotnet run`, and `run-bot.bat` commands use the current machine network.

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
- Per-bot network selection — Direct, HTTP/HTTPS proxy, isolated WireGuard, or isolated OpenVPN
- Start / Stop bot, mode/flag preferences persist
- Light/dark theme + English/Russian UI toggle

**Requirements:** Node.js (for first-time `npm install`).

### Tests

Run the deterministic checks from the repository root:

- Python: `cd python && python -m unittest discover -p 'test_*.py' -v`
- .NET: `dotnet run --project dotnet/PolymarketBot.SelfTests/PolymarketBot.SelfTests.csproj --configuration Release` (requires .NET 8)
- Dashboard: `cd dashboard && npm ci && npm test`
- VPN runner integration (Windows + WSL2/Ubuntu): `cd dashboard && npm run test:vpn`

These checks use fixtures or local self-tests; they do not place real Polymarket orders.

## Evaluation journal and calibration

Every completed estimate is appended to `data/estimates.jsonl`, including the original market price, executable VWAP/limit, confidence, decision, and skip reason. Resolution records allow post-hoc Brier score and calibration analysis:

New evaluation rows use journal schema v2 and also record the implementation, run/cycle IDs, reasoning summary, token usage, contributing provider models, and a versioned SHA-256 fingerprint of the exact system + market prompt. Full prompts, raw provider payloads, API keys, and credentials are not written to disk. Older journal rows remain valid for replay and analysis.

`data/resolution-watchlist.json` also tracks evaluated markets that were never bought, avoiding calibration bias toward executed trades only.

```bash
cd python
python analyze_estimates.py --estimates ../data/estimates.jsonl
python replay_estimates.py --estimates ../data/estimates.jsonl --min-edge 0.08 --kelly-fraction 0.10
python historical_analogs.py --estimates ../data/estimates.jsonl
```

The replay is deterministic and offline: it applies sizing and risk overrides to the shared journal schema emitted by both the Python and .NET bots. Calibration-weighted live aggregation is opt-in and stays equal-weighted until every active provider has at least `calibration_min_samples` resolved predictions.

### Optional wallet-flow shadow

The default-off wallet-flow shadow reads Polymarket's public Data API and stores only market-level aggregates: directional volume, imbalance, unique-wallet count, concentration, and large-trade share. Wallet addresses are used transiently for aggregation, discarded when the lookup finishes, and never written to the journal. The telemetry cannot change signals, sizing, risk checks, or execution.

`analyze_estimates.py` compares the earliest flow observation for each resolved market against both the market and AI Brier scores. Its printed live gate stays on `HOLD` until at least 100 independent resolved markets beat both baselines; passing that offline report still does not enable live influence.

```json
{
  "wallet_flow_shadow_enabled": true,
  "wallet_flow_window_minutes": 60,
  "wallet_flow_trades_limit": 500,
  "wallet_flow_large_trade_usd": 1000
}
```

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

All settings live in **`polymarket_bot_config.json`**. See `polymarket_bot_config.json.example` for a fully annotated template. Runtime bot config priority: **CLI arg → env var → config file → code default**. Runtime keys can also be set as uppercase env vars; Dashboard-only network settings are read from the JSON file.

### Network (Dashboard launcher)

| Key | Default | Description |
|-----|---------|-------------|
| `network_mode` | `direct` | `direct`, `proxy`, `wireguard`, or `openvpn` |
| `vpn_config_path` | — | Absolute Windows path to a `.conf` or `.ovpn` file |
| `vpn_wsl_distro` | `Ubuntu` | WSL distribution used for the isolated namespace |
| `wireguard_private_key` | — | Optional replacement for the config's `PrivateKey`; stored only in the gitignored local config |
| `wireguard_public_key` | — | Optional matching local public key used to validate the private key |
| `openvpn_username` | — | VPN provider service username |
| `openvpn_password` | — | VPN provider service password |
| `proxy_type` | `http` | `http` or `https` |
| `proxy_host` / `proxy_port` | — | Proxy endpoint |
| `proxy_username` / `proxy_password` | — | Optional proxy authentication |
| `proxy_bypass` | — | Comma-separated `NO_PROXY` hosts |
| `polymarket_proxy_url` | — | Optional bot-level override; full proxy URL that takes precedence over the `proxy_*` fields |

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
| `wallet_flow_shadow_enabled` | `false` | Record anonymous public wallet-flow aggregates for offline analysis only |
| `wallet_flow_window_minutes` | `60` | Public trade lookback window per evaluated market |
| `wallet_flow_trades_limit` | `500` | Maximum public trades fetched per evaluated market |
| `wallet_flow_large_trade_usd` | `1000` | Notional threshold for aggregate large-trade metrics |

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
| `uncertainty_edge_multiplier` | `1.0` | Subtract this multiple of ensemble std dev from fair probability before edge and Kelly sizing |
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
| `email_enabled` | `false` | Send HTML email notifications |
| `email_smtp_host` | — | e.g. `smtp.gmail.com` |
| `email_smtp_port` | `587` | Preferred port used by `auto` mode |
| `email_security` | `auto` | `auto` tries the preferred mode and falls back between STARTTLS 587 and implicit TLS 465; `starttls` or `ssl` forces one mode |
| `email_use_tls` | `true` | Legacy preference used by `auto`; `false` prefers implicit TLS on 465 |
| `email_user` | — | Sender address |
| `email_password` | — | App password for Gmail |
| `email_to` | — | Recipient address |

Events: bot started, trade opened/closed, ghost removed, market resolved, halted, error, stopped.

The successful SMTP port is reused for the rest of the bot session. Messages use one HTML MIME body encoded as Base64 to stay within SMTP line-length limits.

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
conservative_probability = fair_probability - uncertainty_edge_multiplier × std_dev
edge = conservative_probability - execution_price
b = (1 - execution_price) / execution_price
f* = (b × conservative_probability - (1 - conservative_probability)) / b
bet = kelly_fraction × f* × portfolio_value
```

Capped by `max_position_pct` and available bankroll. In live mode, unless `allow_unsafe_risk=true`, the bot also clamps aggressive risk settings and skips trades where the 5-token CLOB minimum would consume too much free cash.

## Position Review & Exits

- **Ghost check** — verify on-chain token balance; if < 0.1 tokens, write off as ghost (`exit_reason = "ghost"`)
- **External close** — if the vanished position has an unresolved live-order intent (lost POST response or a manual sell on the website), the position is removed without writing a realized loss; bankroll is corrected by the on-chain sync (`exit_reason = "external_close"`)
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

## Deterministic guardrails around AI (live mode)

- Any live order whose POST outcome is unknown (network error, lost response) marks the session as **uncertain**. While uncertain: no new BUY / SELL / TOPUP orders are sent, and no new AI estimates are paid for — existing positions and exits are still valued. The block clears after startup recovery reconciles the pending intent against the on-chain token balance.
- Recovery: if an uncertain intent's tokens have vanished on-chain and no tracked position remains, the intent is closed as `external_close` and the bot starts normally. Tokens still on-chain keep the bot in manual-reconciliation mode.
- Transport circuit breaker: **two consecutive unknown order outcomes** open a 15-minute cooldown that pauses all live orders (any submitted order resets it). The breaker persists in `data/transport-breaker.json` and startup recovery of a lost-response intent carries the streak — restarting the bot no longer retries a broken transport once per start.
- Gamma 406 responses log a diagnostics line (server header + body fingerprint); the common self-inflicted cause is a missing `Accept-Encoding: gzip` on `/events` (`gzip_required`).

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
  wallet_flow_shadow.py              Anonymous public flow telemetry for offline research
  runtime_safety.py                  Cross-language single-instance lock
  analyze_estimates.py               Brier score and calibration report
  historical_analogs.py              Leak-free historical analog evaluation
  replay_estimates.py                Deterministic offline decision replay
  market_scanner.py                  Gamma API + fresh CLOB books
  portfolio.py                       Kelly sizing, risk, cooldown, ghost removal
  trader.py                          PaperTrader + LiveTrader + ghost detection
  notifier.py                        VPN-compatible plain-text email notifications
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
    WalletFlowShadow.cs              Anonymous public flow telemetry for offline research
    RuntimeSafety.cs                 Cross-language single-instance lock
    MarketScanner.cs                 Gamma API + fresh CLOB books
    Portfolio.cs                     Kelly sizing, risk, cooldown
    LiveTrader.cs                    CLOB GTC orders + ghost detection
    PaperTrader.cs                   Simulated execution
    ClobApiClient.cs                 EIP-712 + HMAC auth, orders, auto-claim
    Notifier.cs                      VPN-compatible plain-text email notifications
    PersistenceService.cs            Atomic JSON + JSONL
    JsonFileLoggerProvider.cs        JSON line logger

dotnet/PolymarketBot.SelfTests/    ← Dependency-free parity and reliability checks
tests/golden_execution.json        ← Shared Python/.NET execution vectors
resolution-watchlist.json          ← Runtime calibration outcomes queue

dashboard/                         ← Electron desktop app
  main.js                            IPC, file watchers, bot spawn, model fetching API
  vpn-runner.sh                      Isolated WSL WireGuard/OpenVPN runner and kill switch
  test-vpn-runner.js                 VPN runner integration self-check
  preload.js                         Context bridge
  renderer.js                        UI + per-provider config sections
  index.html / styles.css            Shell + dark/light themes
```

## Disclaimer

Experimental software. Prediction market trading carries risk. Do not trade with money you cannot afford to lose.

## License

MIT
