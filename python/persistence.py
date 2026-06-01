"""JSON-based persistence for portfolio state and trade history."""

import json
import os
import time
from enum import Enum
from typing import Optional

from models import PortfolioSnapshot, Position, Trade, Side, TradeAction

_PORTFOLIO_FILE = "portfolio.json"
_TRADES_FILE = "trades.jsonl"


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
    )


def append_trade(trade: Trade, data_dir: str) -> None:
    """Append a trade record to the JSONL trade log."""
    os.makedirs(data_dir, exist_ok=True)
    path = os.path.join(data_dir, _TRADES_FILE)
    with open(path, "a") as f:
        f.write(json.dumps(trade, cls=_Encoder) + "\n")
