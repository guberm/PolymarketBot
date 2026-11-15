# Polymarket bot repository guidance

The bot has aligned Python and .NET implementations. `README.md` documents usage and architecture; `polymarket_bot_config.json.example` documents current configuration.

## Safety and scope

- Never expose API keys, wallet material, cookies, or private configuration.
- Paper mode is the default. Do not place live orders, redeem positions, or spend external funds unless the user explicitly requests that action.
- Treat estimates as uncertain analysis, not guaranteed outcomes or financial advice.

## Core invariants

- Keep shared trading, risk, configuration, and persistence behavior aligned across Python and .NET unless the difference is explicitly platform-specific.
- In multi-provider mode, continue when at least one configured provider returns a valid estimate.
- Parse responses according to the provider actually called.
- Apply uncertainty before edge thresholding and Kelly sizing.
- Preserve layered exposure limits, exit cooldowns, ghost-position handling, external-close accounting (unresolved intent + vanished tokens must not write a realized loss), and atomic persistence.
- Block new orders and paid AI estimates while a live POST outcome is unknown; startup recovery reconciles the pending intent against the on-chain token balance.
- Keep the transport circuit breaker aligned across implementations: two consecutive unknown order outcomes open a 15-minute cooldown persisted in `data/transport-breaker.json`, startup recovery of a lost-response intent carries the streak, and any submitted order resets it.
- Handle Gamma fields that may be JSON strings or arrays and CLOB tick sizes that may be strings or numbers.
- Keep dashboard configuration fields and runtime configuration in sync when either changes.

## Working guidance

- Trace the relevant cycle path and both implementations before changing shared behavior.
- Prefer a focused deterministic self-test or existing test for non-trivial logic.
- Fix failures caused by the change and rerun affected local checks without asking between iterations.
- Use live APIs only when the task requires them and authorization permits it.
- Read current commands, defaults, and model names from code and config examples rather than recording them here.
