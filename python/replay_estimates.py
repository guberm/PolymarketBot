"""Deterministically replay recorded estimate decisions without external APIs."""

from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass
class ReplayConfig:
    bankroll: float = 100.0
    min_edge: float = 0.12
    entry_buffer: float = 0.02
    kelly_fraction: float = 0.15
    max_position_pct: float = 0.15
    max_total_exposure_pct: float = 1.0
    max_category_exposure_pct: float = 0.80
    max_event_exposure_pct: float = 0.30
    min_trade_usd: float = 0.50


def load_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            rows.append(json.loads(line))
        except (json.JSONDecodeError, ValueError):
            continue
    return rows


def replay(rows: list[dict], config: ReplayConfig) -> dict:
    bankroll = config.bankroll
    positions: dict[str, dict] = {}
    trades = signals = risk_blocked = resolved = 0
    expected_value = realized_pnl = max_exposure = 0.0

    for row in rows:
        condition_id = str(row.get("condition_id", ""))
        if row.get("record_type") == "resolution":
            position = positions.pop(condition_id, None)
            if position is None:
                continue
            won = (position["side"] == "YES") == (float(row.get("actual_outcome", 0)) == 1.0)
            payout = position["shares"] if won else 0.0
            bankroll += payout
            realized_pnl += payout - position["size"]
            resolved += 1
            continue
        if row.get("record_type", "evaluation") != "evaluation" or condition_id in positions:
            continue

        fair = float(row.get("fair_probability", 0.5))
        yes_price = min(float(row.get("market_yes_price", 0)) + config.entry_buffer, 0.99)
        no_price = min(float(row.get("market_no_price", 0)) + config.entry_buffer, 0.99)
        yes_edge, no_edge = fair - yes_price, (1 - fair) - no_price
        side, price, probability, edge = (
            ("YES", yes_price, fair, yes_edge)
            if yes_edge > no_edge else ("NO", no_price, 1 - fair, no_edge)
        )
        if edge <= config.min_edge or not 0 < price < 1:
            continue
        signals += 1

        exposure = sum(position["size"] for position in positions.values())
        equity = bankroll + exposure
        odds = 1 / price - 1
        kelly_raw = max(0.0, (odds * probability - (1 - probability)) / odds)
        size = min(kelly_raw * config.kelly_fraction * equity,
                   config.max_position_pct * equity, bankroll)
        category = str(row.get("category", "other"))
        event = str(row.get("event_title", "")).strip().casefold()
        category_exposure = sum(p["size"] for p in positions.values() if p["category"] == category)
        event_exposure = sum(p["size"] for p in positions.values() if event and p["event"] == event)
        blocked = (
            size < max(config.min_trade_usd, 5 * price, 1.0)
            or exposure + size > equity * config.max_total_exposure_pct
            or category_exposure + size > equity * config.max_category_exposure_pct
            or (event and event_exposure + size > equity * config.max_event_exposure_pct)
        )
        if blocked:
            risk_blocked += 1
            continue

        shares = size / price
        positions[condition_id] = {"side": side, "shares": shares, "size": size,
                                   "category": category, "event": event}
        bankroll -= size
        trades += 1
        expected_value += edge * shares
        max_exposure = max(max_exposure, exposure + size)

    return {
        "evaluations": sum(row.get("record_type", "evaluation") == "evaluation" for row in rows),
        "signals": signals, "trades": trades, "risk_blocked": risk_blocked,
        "resolved_trades": resolved, "open_positions": len(positions),
        "ending_bankroll": round(bankroll, 6),
        "open_exposure": round(sum(p["size"] for p in positions.values()), 6),
        "max_exposure": round(max_exposure, 6),
        "expected_value": round(expected_value, 6),
        "realized_pnl": round(realized_pnl, 6),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--estimates", default="../data/estimates.jsonl")
    defaults = asdict(ReplayConfig())
    for name, default in defaults.items():
        parser.add_argument(f"--{name.replace('_', '-')}", type=float, default=default)
    args = parser.parse_args()
    config = ReplayConfig(**{name: getattr(args, name) for name in defaults})
    print(json.dumps(replay(load_jsonl(Path(args.estimates)), config), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
