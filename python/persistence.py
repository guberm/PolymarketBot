"""JSON-based persistence for portfolio state and trade history."""

import json
import os
import time
from datetime import datetime
from enum import Enum
from typing import Optional

from models import Estimate, MarketInfo, PortfolioSnapshot, Position, Signal, Trade, Side, TradeAction

_PORTFOLIO_FILE = "portfolio.json"
_TRADES_FILE = "trades.jsonl"
_ESTIMATES_FILE = "estimates.jsonl"
_RESOLUTION_WATCHLIST_FILE = "resolution-watchlist.json"


class _Encoder(json.JSONEncoder):
    def default(self, obj):
        if hasattr(obj, "__dataclass_fields__"):
            return {k: getattr(obj, k) for k in obj.__dataclass_fields__}
        if isinstance(obj, Enum):
            return obj.value
        return super().default(obj)


def _decode_position(d: dict) -> Position:
    d = dict(d)  # shallow copy
    d["side"] = Side(d["side"])
    return Position(**d)


def save_snapshot(snapshot: PortfolioSnapshot, data_dir: str) -> None:
    """Atomically write portfolio state to JSON."""
    os.makedirs(data_dir, exist_ok=True)
    path = os.path.join(data_dir, _PORTFOLIO_FILE)
    data = {
        "bankroll": snapshot.bankroll,
        "initial_bankroll": snapshot.initial_bankroll,
        "positions": [json.loads(json.dumps(p, cls=_Encoder)) for p in snapshot.positions],
        "high_water_mark": snapshot.high_water_mark,
        "daily_start_value": snapshot.daily_start_value,
        "total_realized_pnl": snapshot.total_realized_pnl,
        "total_trades": snapshot.total_trades,
        "is_halted": snapshot.is_halted,
        "last_updated": snapshot.last_updated,
        "total_api_cost": snapshot.total_api_cost,
        "daily_api_cost": snapshot.daily_api_cost,
        "daily_tracking_date": snapshot.daily_tracking_date,
        "applied_order_ids": snapshot.applied_order_ids,
    }
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(data, f, indent=2)
    os.replace(tmp, path)


def load_snapshot(data_dir: str) -> Optional[PortfolioSnapshot]:
    """Load portfolio state from JSON. Returns None if no saved state."""
    path = os.path.join(data_dir, _PORTFOLIO_FILE)
    if not os.path.exists(path):
        return None
    with open(path) as f:
        data = json.load(f)
    positions = [_decode_position(p) for p in data.get("positions", [])]
    return PortfolioSnapshot(
        bankroll=data["bankroll"],
        initial_bankroll=data["initial_bankroll"],
        positions=positions,
        high_water_mark=data["high_water_mark"],
        daily_start_value=data["daily_start_value"],
        total_realized_pnl=data["total_realized_pnl"],
        total_trades=data["total_trades"],
        is_halted=data["is_halted"],
        last_updated=data.get("last_updated", time.time()),
        total_api_cost=data.get("total_api_cost", 0.0),
        daily_api_cost=data.get("daily_api_cost", 0.0),
        daily_tracking_date=data.get("daily_tracking_date", ""),
        applied_order_ids=data.get("applied_order_ids", []),
    )


def append_trade(trade: Trade, data_dir: str) -> None:
    """Append a trade record to the JSONL trade log."""
    os.makedirs(data_dir, exist_ok=True)
    path = os.path.join(data_dir, _TRADES_FILE)
    with open(path, "a") as f:
        f.write(json.dumps(trade, cls=_Encoder) + "\n")


def append_estimate_evaluation(
    market: MarketInfo,
    estimate: Estimate,
    data_dir: str,
    provider: str,
    decision: str,
    reason: str,
    signal: Optional[Signal] = None,
    kalshi_reference: Optional[dict] = None,
    track_watch: bool = True,
) -> None:
    """Append one final decision for a successfully evaluated market."""
    os.makedirs(data_dir, exist_ok=True)
    path = os.path.join(data_dir, _ESTIMATES_FILE)
    record = {
        "record_type": "evaluation",
        "timestamp": time.time(),
        "condition_id": market.condition_id,
        "question": market.question,
        "category": market.category,
        "event_title": market.event_title,
        "provider": provider,
        "fair_probability": estimate.fair_probability,
        "raw_estimates": estimate.raw_estimates,
        "confidence": estimate.confidence,
        "api_cost_usd": estimate.api_cost_usd,
        "duration_seconds": estimate.duration_seconds,
        "provider_estimates": estimate.provider_estimates,
        "market_yes_price": market.outcome_yes_price,
        "market_no_price": market.outcome_no_price,
        "side": signal.side.value if signal else "",
        "execution_vwap": signal.execution_price if signal else 0.0,
        "limit_price": signal.limit_price if signal else 0.0,
        "quote_age_seconds": signal.quote_age_seconds if signal else 0.0,
        "edge": signal.edge if signal else 0.0,
        "position_size_usd": signal.position_size_usd if signal else 0.0,
        "decision": decision,
        "reason": reason,
        "kalshi": kalshi_reference,
    }
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(record, ensure_ascii=False) + "\n")
    if track_watch:
        track_resolution(market, data_dir)


def append_estimate_resolution(
    condition_id: str, actual_outcome: float, data_dir: str, remove_watch: bool = True
) -> None:
    os.makedirs(data_dir, exist_ok=True)
    path = os.path.join(data_dir, _ESTIMATES_FILE)
    record = {
        "record_type": "resolution",
        "timestamp": time.time(),
        "condition_id": condition_id,
        "actual_outcome": actual_outcome,
    }
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(record) + "\n")
    if remove_watch:
        remove_resolution_watch(condition_id, data_dir)


def track_resolution(market: MarketInfo, data_dir: str) -> None:
    track_resolutions([market], data_dir)


def track_resolutions(markets: list[MarketInfo], data_dir: str) -> None:
    watch = _load_resolution_watchlist(data_dir)
    changed = False
    for market in markets:
        if market.condition_id in watch:
            continue
        next_check = time.time()
        if market.end_date:
            try:
                next_check = max(next_check, datetime.fromisoformat(market.end_date.replace("Z", "+00:00")).timestamp())
            except ValueError:
                pass
        watch[market.condition_id] = {
            "condition_id": market.condition_id,
            "question": market.question,
            "end_date": market.end_date,
            "next_check_at": next_check,
        }
        changed = True
    if changed:
        _save_resolution_watchlist(watch, data_dir)


def get_resolution_candidates(data_dir: str, limit: int) -> list[str]:
    now = time.time()
    watch = _load_resolution_watchlist(data_dir)
    ready = sorted(watch.values(), key=lambda item: item.get("next_check_at", 0))
    return [item["condition_id"] for item in ready if item.get("next_check_at", 0) <= now][:max(0, limit)]


def defer_resolution_check(condition_id: str, data_dir: str, hours: float) -> None:
    update_resolution_watchlist([condition_id], [], data_dir, hours)


def remove_resolution_watch(condition_id: str, data_dir: str) -> None:
    update_resolution_watchlist([], [condition_id], data_dir, 0)


def update_resolution_watchlist(
    defer_ids: list[str], remove_ids: list[str], data_dir: str, hours: float
) -> None:
    watch = _load_resolution_watchlist(data_dir)
    changed = False
    next_check = time.time() + max(0.1, hours) * 3600
    for condition_id in defer_ids:
        if condition_id in watch:
            watch[condition_id]["next_check_at"] = next_check
            changed = True
    for condition_id in remove_ids:
        changed = watch.pop(condition_id, None) is not None or changed
    if changed:
        _save_resolution_watchlist(watch, data_dir)


def _load_resolution_watchlist(data_dir: str) -> dict[str, dict]:
    path = os.path.join(data_dir, _RESOLUTION_WATCHLIST_FILE)
    try:
        with open(path, encoding="utf-8") as stream:
            value = json.load(stream)
            return value if isinstance(value, dict) else {}
    except (FileNotFoundError, OSError, json.JSONDecodeError):
        return {}


def _save_resolution_watchlist(watch: dict[str, dict], data_dir: str) -> None:
    os.makedirs(data_dir, exist_ok=True)
    path = os.path.join(data_dir, _RESOLUTION_WATCHLIST_FILE)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as stream:
        json.dump(watch, stream, indent=2, ensure_ascii=False)
    os.replace(tmp, path)
