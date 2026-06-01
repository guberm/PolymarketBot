"""Summarize trade history for strategy tuning.

Usage:
    python analyze_trades.py
    python analyze_trades.py --trades ../data/trades.jsonl
"""

from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path


def load_trades(path: Path) -> list[dict]:
    trades: list[dict] = []
    if not path.exists():
        return trades
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            trades.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return trades


def matched_realized_pnl(trades: list[dict]) -> list[dict]:
    open_by_condition: dict[str, list[dict]] = defaultdict(list)
    realized: list[dict] = []

    for trade in trades:
        action = trade.get("action")
        condition_id = trade.get("condition_id", "")
        if action == "BUY":
            open_by_condition[condition_id].append(trade)
            continue
        if action != "SELL":
            continue

        sell_size = float(trade.get("size_usd") or 0)
        sell_shares = float(trade.get("shares") or 0)
        sell_price = float(trade.get("price") or 0)
        candidates = open_by_condition.get(condition_id) or []

        if candidates:
            best_idx = min(
                range(len(candidates)),
                key=lambda i: abs(float(candidates[i].get("size_usd") or 0) - sell_size)
                + abs(float(candidates[i].get("shares") or 0) - sell_shares),
            )
            buy = candidates.pop(best_idx)
            cost = float(buy.get("size_usd") or 0)
            entry_price = float(buy.get("price") or 0)
            edge = float(buy.get("edge_at_entry") or 0)
        else:
            cost = sell_size
            entry_price = 0.0
            edge = 0.0

        realized.append(
            {
                "pnl": sell_shares * sell_price - cost,
                "reason": trade.get("exit_reason") or "sell",
                "question": trade.get("question", ""),
                "side": trade.get("side", ""),
                "entry_price": entry_price,
                "exit_price": sell_price,
                "cost": cost,
                "edge": edge,
            }
        )

    return realized


def print_summary(trades: list[dict], realized: list[dict], worst: int) -> None:
    print(f"Trades: {len(trades)}")
    print(f"Actions: {dict(Counter(t.get('action') for t in trades))}")
    print(f"Exit reasons: {dict(Counter(t.get('exit_reason') or '(buy/open)' for t in trades))}")

    total_pnl = sum(r["pnl"] for r in realized)
    avg_pnl = total_pnl / len(realized) if realized else 0.0
    print(f"Matched realized: {len(realized)} exits, pnl=${total_pnl:.2f}, avg=${avg_pnl:.2f}")

    by_reason: dict[str, list[float]] = defaultdict(list)
    for row in realized:
        by_reason[row["reason"]].append(row["pnl"])

    print("\nPnL by exit reason:")
    for reason, pnls in sorted(by_reason.items(), key=lambda item: sum(item[1])):
        print(f"  {reason:16s} {len(pnls):3d}  total=${sum(pnls):8.2f}  avg=${sum(pnls) / len(pnls):7.2f}")

    buys = [t for t in trades if t.get("action") == "BUY"]
    if buys:
        avg_edge = sum(float(t.get("edge_at_entry") or 0) for t in buys) / len(buys)
        avg_kelly = sum(float(t.get("kelly_at_entry") or 0) for t in buys) / len(buys)
        print(f"\nBUYs: {len(buys)}, avg edge={avg_edge:.1%}, avg kelly={avg_kelly:.1%}")

    print(f"\nWorst {worst}:")
    for row in sorted(realized, key=lambda r: r["pnl"])[:worst]:
        print(
            f"  ${row['pnl']:7.2f} {row['reason']:14s} {row['side']:3s} "
            f"{row['entry_price']:.3f}->{row['exit_price']:.3f} "
            f"{row['question'][:90]}"
        )


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze Polymarket bot trade history")
    parser.add_argument("--trades", default="../data/trades.jsonl", help="Path to trades.jsonl")
    parser.add_argument("--worst", type=int, default=12, help="Number of worst exits to print")
    args = parser.parse_args()

    trades = load_trades(Path(args.trades))
    realized = matched_realized_pnl(trades)
    print_summary(trades, realized, args.worst)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
