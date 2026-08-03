"""Bot configuration.

Priority (highest wins):
  1. Environment variables
  2. polymarket_bot_config.json  (project root, or path in CONFIG_FILE env var)
  3. Code defaults
"""

import json
import os
from dataclasses import dataclass
from pathlib import Path

_CONFIG_DIR: Path | None = None


def _find_config_file() -> Path | None:
    env_path = os.environ.get("CONFIG_FILE")
    if env_path:
        return Path(env_path).expanduser().resolve()

    for start in (Path.cwd(), Path(__file__).resolve().parent.parent):
        for base in (start, *start.parents):
            candidate = base / "polymarket_bot_config.json"
            if candidate.exists():
                return candidate

    return None


def _load_json() -> dict:
    """Load config.json. Returns empty dict if not found."""
    global _CONFIG_DIR
    path = _find_config_file()
    if path is None:
        return {}
    try:
        _CONFIG_DIR = path.parent
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return {}
    except Exception as e:
        import logging
        logging.getLogger("bot.config").warning(f"Could not load config.json: {e}")
        return {}


def _resolve_data_dir(data_dir: str, from_env: bool) -> str:
    path = Path(data_dir).expanduser()
    if path.is_absolute():
        return str(path.resolve())
    base = Path.cwd() if from_env else (_CONFIG_DIR or Path.cwd())
    return str((base / path).resolve())


@dataclass
class BotConfig:
    # Mode
    live_trading: bool = False

    # Scan
    scan_interval_minutes: int = 10
    min_liquidity: float = 10000.0
    min_volume_24hr: float = 1000.0
    min_time_to_resolution_hours: float = 48.0
    min_market_price: float = 0.10
    markets_per_cycle: int = 15
    max_spread: float = 0.04

    # Optional read-only Kalshi comparison
    kalshi_shadow_enabled: bool = False
    kalshi_api_host: str = "https://api.elections.kalshi.com/trade-api/v2"
    kalshi_markets_limit: int = 200
    kalshi_min_match_score: float = 0.55
    kalshi_llm_same_threshold: float = 0.90

    # AI provider
    ai_provider: str = "anthropic"   # selected provider for single-provider mode
    multi_provider: bool = False     # True = query ALL configured providers and aggregate

    # Per-provider credentials + models
    # Anthropic
    anthropic_enabled: bool = True
    anthropic_api_key: str = ""
    anthropic_api_host: str = "https://api.anthropic.com"
    anthropic_model: str = "claude-sonnet-4-6"

    # OpenAI
    openai_enabled: bool = True
    openai_api_key: str = ""
    openai_api_host: str = "https://api.openai.com"
    openai_model: str = "gpt-4o"

    # Google Gemini
    gemini_enabled: bool = True
    gemini_api_key: str = ""
    gemini_api_host: str = "https://generativelanguage.googleapis.com"
    gemini_model: str = "gemini-2.0-flash"

    # OpenRouter
    openrouter_enabled: bool = True
    openrouter_api_key: str = ""
    openrouter_api_host: str = "https://openrouter.ai"
    openrouter_model: str = ""

    # Azure OpenAI
    azure_openai_enabled: bool = True
    azure_openai_api_key: str = ""
    azure_openai_endpoint: str = ""
    azure_openai_deployment: str = ""
    azure_openai_api_version: str = "2024-02-01"

    # Estimation
    ensemble_size: int = 3
    ensemble_temperature: float = 0.7
    max_estimate_tokens: int = 1024
    max_estimate_std: float = 0.10
    llm_cost_tracking_enabled: bool = True
    max_cycle_api_cost_usd: float = 1.00
    max_daily_api_cost_usd: float = 10.00
    api_pricing: str = "anthropic=3/15,openai=5/15,gemini=0.10/0.40,openrouter=3/15,azure_openai=5/15"
    calibration_weighting_enabled: bool = False
    calibration_min_samples: int = 40
    calibration_shrinkage: float = 0.50
    calibration_max_provider_weight: float = 0.60

    # Sizing
    kelly_fraction: float = 0.15
    min_edge: float = 0.12
    min_trade_usd: float = 0.5
    entry_price_buffer: float = 0.02
    max_quote_age_seconds: float = 15.0
    quote_failure_grace_cycles: int = 3
    stale_quote_haircut_pct: float = 0.25
    resolution_checks_per_cycle: int = 20
    resolution_retry_hours: float = 6.0
    max_live_order_bankroll_pct: float = 0.25
    allow_unsafe_risk: bool = False

    # Risk
    max_position_pct: float = 0.15
    max_total_exposure_pct: float = 1.00
    max_category_exposure_pct: float = 0.80
    max_event_exposure_pct: float = 0.30
    daily_stop_loss_pct: float = 0.20
    max_drawdown_pct: float = 0.50
    max_concurrent_positions: int = 8

    # Position review / exit
    enable_position_review: bool = True
    position_stop_loss_pct: float = 0.20
    take_profit_price: float = 0.95
    exit_edge_buffer: float = 0.05
    review_reestimate_threshold_pct: float = 0.10
    review_ensemble_size: int = 3
    stop_loss_requires_negative_edge: bool = True

    # Capital
    initial_bankroll: float = 10000.0

    # Polymarket credentials
    polymarket_private_key: str = ""
    polymarket_funder_address: str = ""
    polymarket_chain_id: int = 137
    polymarket_signature_type: int = 0
    polymarket_api_key: str = ""
    polymarket_api_secret: str = ""
    polymarket_api_passphrase: str = ""

    # Polymarket endpoints
    gamma_api_host: str = ""
    clob_host: str = ""
    exchange_address: str = "0xE111180000d2663C0091e4f400237545B87B996B"
    neg_risk_exchange_address: str = "0xe2222d279d744050d28e00520010520000310F59"

    # Email notifications
    email_enabled: bool = False
    email_smtp_host: str = ""
    email_smtp_port: int = 587
    email_security: str = "auto"
    email_use_tls: bool = True
    email_user: str = ""
    email_password: str = ""
    email_to: str = ""

    # Persistence
    data_dir: str = "data"

    @classmethod
    def from_env(cls) -> "BotConfig":
        """Build config: env var > config.json > code default."""
        j = _load_json()
        data_dir_env = os.environ.get("DATA_DIR")
        data_dir_raw = data_dir_env if data_dir_env is not None else j.get("data_dir", "data")

        def get(key: str, default):
            env_val = os.environ.get(key.upper())
            if env_val is not None:
                if isinstance(default, bool):
                    return env_val.lower() == "true"
                if isinstance(default, int):
                    return int(env_val)
                if isinstance(default, float):
                    return float(env_val)
                return env_val
            if key in j:
                return j[key]
            return default

        # Backward compat: claude_model / ai_model → anthropic_model
        _legacy_anthropic = j.get("claude_model") or j.get("ai_model") or ""

        return cls(
            live_trading=get("live_trading", False),
            scan_interval_minutes=get("scan_interval_minutes", 10),
            min_liquidity=get("min_liquidity", 10000.0),
            min_volume_24hr=get("min_volume_24hr", 1000.0),
            min_time_to_resolution_hours=get("min_time_to_resolution_hours", 48.0),
            min_market_price=get("min_market_price", 0.10),
            markets_per_cycle=get("markets_per_cycle", 15),
            max_spread=get("max_spread", 0.04),
            kalshi_shadow_enabled=get("kalshi_shadow_enabled", False),
            kalshi_api_host=get("kalshi_api_host", "https://api.elections.kalshi.com/trade-api/v2"),
            kalshi_markets_limit=get("kalshi_markets_limit", 200),
            kalshi_min_match_score=get("kalshi_min_match_score", 0.55),
            kalshi_llm_same_threshold=get("kalshi_llm_same_threshold", 0.90),
            ai_provider=get("ai_provider", "anthropic"),
            multi_provider=get("multi_provider", False),
            anthropic_enabled=get("anthropic_enabled", True),
            anthropic_api_key=get("anthropic_api_key", ""),
            anthropic_api_host=get("anthropic_api_host", "https://api.anthropic.com"),
            anthropic_model=get("anthropic_model", _legacy_anthropic or "claude-sonnet-4-6"),
            openai_enabled=get("openai_enabled", True),
            openai_api_key=get("openai_api_key", ""),
            openai_api_host=get("openai_api_host", "https://api.openai.com"),
            openai_model=get("openai_model", "gpt-4o"),
            gemini_enabled=get("gemini_enabled", True),
            gemini_api_key=get("gemini_api_key", ""),
            gemini_api_host=get("gemini_api_host", "https://generativelanguage.googleapis.com"),
            gemini_model=get("gemini_model", "gemini-2.0-flash"),
            openrouter_enabled=get("openrouter_enabled", True),
            openrouter_api_key=get("openrouter_api_key", ""),
            openrouter_api_host=get("openrouter_api_host", "https://openrouter.ai"),
            openrouter_model=get("openrouter_model", ""),
            azure_openai_enabled=get("azure_openai_enabled", True),
            azure_openai_api_key=get("azure_openai_api_key", ""),
            azure_openai_endpoint=get("azure_openai_endpoint", ""),
            azure_openai_deployment=get("azure_openai_deployment", ""),
            azure_openai_api_version=get("azure_openai_api_version", "2024-02-01"),
            ensemble_size=get("ensemble_size", 3),
            ensemble_temperature=get("ensemble_temperature", 0.7),
            max_estimate_tokens=get("max_estimate_tokens", 1024),
            max_estimate_std=get("max_estimate_std", 0.10),
            llm_cost_tracking_enabled=get("llm_cost_tracking_enabled", True),
            max_cycle_api_cost_usd=get("max_cycle_api_cost_usd", 1.00),
            max_daily_api_cost_usd=get("max_daily_api_cost_usd", 10.00),
            api_pricing=get("api_pricing", "anthropic=3/15,openai=5/15,gemini=0.10/0.40,openrouter=3/15,azure_openai=5/15"),
            calibration_weighting_enabled=get("calibration_weighting_enabled", False),
            calibration_min_samples=get("calibration_min_samples", 40),
            calibration_shrinkage=get("calibration_shrinkage", 0.50),
            calibration_max_provider_weight=get("calibration_max_provider_weight", 0.60),
            kelly_fraction=get("kelly_fraction", 0.15),
            min_edge=get("min_edge", 0.12),
            min_trade_usd=get("min_trade_usd", 0.5),
            entry_price_buffer=get("entry_price_buffer", 0.02),
            max_quote_age_seconds=get("max_quote_age_seconds", 15.0),
            quote_failure_grace_cycles=get("quote_failure_grace_cycles", 3),
            stale_quote_haircut_pct=get("stale_quote_haircut_pct", 0.25),
            resolution_checks_per_cycle=get("resolution_checks_per_cycle", 20),
            resolution_retry_hours=get("resolution_retry_hours", 6.0),
            max_live_order_bankroll_pct=get("max_live_order_bankroll_pct", 0.25),
            allow_unsafe_risk=get("allow_unsafe_risk", False),
            max_position_pct=get("max_position_pct", 0.15),
            max_total_exposure_pct=get("max_total_exposure_pct", 1.00),
            max_category_exposure_pct=get("max_category_exposure_pct", 0.80),
            max_event_exposure_pct=get("max_event_exposure_pct", 0.30),
            daily_stop_loss_pct=get("daily_stop_loss_pct", 0.20),
            max_drawdown_pct=get("max_drawdown_pct", 0.50),
            max_concurrent_positions=get("max_concurrent_positions", 8),
            enable_position_review=get("enable_position_review", True),
            position_stop_loss_pct=get("position_stop_loss_pct", 0.20),
            take_profit_price=get("take_profit_price", 0.95),
            exit_edge_buffer=get("exit_edge_buffer", 0.05),
            review_reestimate_threshold_pct=get("review_reestimate_threshold_pct", 0.10),
            review_ensemble_size=get("review_ensemble_size", 3),
            stop_loss_requires_negative_edge=get("stop_loss_requires_negative_edge", True),
            initial_bankroll=get("initial_bankroll", 10000.0),
            polymarket_private_key=get("polymarket_private_key", ""),
            polymarket_funder_address=get("polymarket_funder_address", ""),
            polymarket_chain_id=get("polymarket_chain_id", 137),
            polymarket_signature_type=get("polymarket_signature_type", 0),
            polymarket_api_key=get("polymarket_api_key", ""),
            polymarket_api_secret=get("polymarket_api_secret", ""),
            polymarket_api_passphrase=get("polymarket_api_passphrase", ""),
            gamma_api_host=get("gamma_api_host", ""),
            clob_host=get("clob_host", ""),
            exchange_address=get("exchange_address", "0xE111180000d2663C0091e4f400237545B87B996B"),
            neg_risk_exchange_address=get("neg_risk_exchange_address", "0xe2222d279d744050d28e00520010520000310F59"),
            email_enabled=get("email_enabled", False),
            email_smtp_host=get("email_smtp_host", ""),
            email_smtp_port=get("email_smtp_port", 587),
            email_security=get("email_security", "auto"),
            email_use_tls=get("email_use_tls", True),
            email_user=get("email_user", ""),
            email_password=get("email_password", ""),
            email_to=get("email_to", ""),
            data_dir=_resolve_data_dir(data_dir_raw, data_dir_env is not None),
        )
