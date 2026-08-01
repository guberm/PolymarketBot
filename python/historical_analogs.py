"""Leak-free walk-forward evaluation of historical prediction-market analogs."""

from __future__ import annotations

import argparse
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path


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


def _number(value, default: float = 0.0) -> float:
    try:
        result = float(value)
        return result if math.isfinite(result) else default
    except (TypeError, ValueError):
        return default


def _disagreement(row: dict) -> float:
    values = list((row.get("provider_estimates") or {}).values()) or row.get("raw_estimates") or []
    clean = [_number(value) for value in values]
    return statistics.pstdev(clean) if len(clean) > 1 else _number(row.get("confidence"))


def _episodes(rows: list[dict]) -> list[dict]:
    resolutions = {
        str(row.get("condition_id", "")): (_number(row.get("actual_outcome")), _number(row.get("timestamp")))
        for row in rows
        if row.get("record_type") == "resolution" and row.get("condition_id")
    }
    evaluations: dict[str, list[dict]] = defaultdict(list)
    for row in rows:
        if row.get("record_type", "evaluation") != "evaluation":
            continue
        condition_id = str(row.get("condition_id", ""))
        if condition_id in resolutions:
            evaluations[condition_id].append(row)

    episodes = []
    for condition_id, market_rows in evaluations.items():
        outcome, resolved_at = resolutions[condition_id]
        market_rows = sorted(
            (row for row in market_rows if _number(row.get("timestamp")) <= resolved_at),
            key=lambda row: _number(row.get("timestamp")),
        )
        if not market_rows or outcome not in (0.0, 1.0):
            continue
        # One independent episode per market. The third observation is the first
        # point with a small, entirely backward-looking price context.
        anchor_index = min(2, len(market_rows) - 1) if len(market_rows) >= 3 else 0
        anchor = market_rows[anchor_index]
        price = _number(anchor.get("market_yes_price"), .5)
        prior_prices = [_number(row.get("market_yes_price"), price) for row in market_rows[:anchor_index + 1]]
        prices = [_number(row.get("market_yes_price"), price) for row in market_rows[anchor_index:]]
        changes = [value - price for value in prices]
        episodes.append({
            "condition_id": condition_id,
            "observed_at": _number(anchor.get("timestamp")),
            "resolved_at": resolved_at,
            "outcome": outcome,
            "market_probability": price,
            "ai_probability": _number(anchor.get("fair_probability"), .5),
            "category": str(anchor.get("category") or "other").casefold(),
            "time_hours": _number(anchor.get("time_to_resolution_hours")),
            "liquidity": max(0.0, _number(anchor.get("liquidity"))),
            "volume_24hr": max(0.0, _number(anchor.get("volume_24hr"))),
            "spread": max(0.0, _number(anchor.get("spread"))),
            "disagreement": _disagreement(anchor),
            "recent_change": prior_prices[-1] - prior_prices[0],
            "volatility": statistics.pstdev(prior_prices) if len(prior_prices) > 1 else 0.0,
            "final_change": changes[-1],
            "max_favorable": max(changes),
            "max_adverse": min(changes),
        })
    return sorted(episodes, key=lambda episode: episode["observed_at"])


def _distance(left: dict, right: dict) -> float:
    log_gap = lambda key, scale: abs(math.log1p(left[key]) - math.log1p(right[key])) / scale
    return (
        3 * abs(left["market_probability"] - right["market_probability"])
        + log_gap("time_hours", 5)
        + log_gap("liquidity", 10)
        + log_gap("volume_24hr", 10)
        + 5 * abs(left["spread"] - right["spread"])
        + 3 * abs(left["disagreement"] - right["disagreement"])
        + 3 * abs(left["recent_change"] - right["recent_change"])
        + 3 * abs(left["volatility"] - right["volatility"])
        + (0.75 if left["category"] != right["category"] else 0.0)
    )


def _weighted_mean(values: list[float], weights: list[float]) -> float:
    total = sum(weights)
    return sum(value * weight for value, weight in zip(values, weights)) / total


def _weighted_median(values: list[float], weights: list[float]) -> float:
    midpoint = sum(weights) / 2
    cumulative = 0.0
    for value, weight in sorted(zip(values, weights)):
        cumulative += weight
        if cumulative >= midpoint:
            return value
    return values[-1]


def _brier(predictions: list[float], outcomes: list[float]) -> float:
    return sum((prediction - outcome) ** 2 for prediction, outcome in zip(predictions, outcomes)) / len(outcomes)


def _log_loss(predictions: list[float], outcomes: list[float]) -> float:
    clipped = [min(.999999, max(.000001, prediction)) for prediction in predictions]
    return -sum(
        outcome * math.log(prediction) + (1 - outcome) * math.log(1 - prediction)
        for prediction, outcome in zip(clipped, outcomes)
    ) / len(outcomes)


def _metrics(predictions: list[dict]) -> dict:
    outcomes = [row["actual_outcome"] for row in predictions]
    result = {}
    for name, key in (("analog", "analog_probability"), ("ai", "ai_probability"), ("market", "market_probability")):
        values = [row[key] for row in predictions]
        result[f"{name}_brier"] = round(_brier(values, outcomes), 6)
        result[f"{name}_log_loss"] = round(_log_loss(values, outcomes), 6)
    return result


def analyze(
    rows: list[dict], neighbors: int = 20, min_neighbors: int = 10,
    min_predictions: int = 100, folds: int = 3,
) -> dict:
    min_neighbors = max(1, min_neighbors)
    neighbors = max(min_neighbors, neighbors)
    min_predictions = max(1, min_predictions)
    folds = max(1, folds)
    episodes = _episodes(rows)
    predictions = []
    for target in episodes:
        history = [candidate for candidate in episodes if candidate["resolved_at"] < target["observed_at"]]
        if len(history) < min_neighbors:
            continue
        nearest = sorted(history, key=lambda candidate: _distance(target, candidate))[:neighbors]
        weights = [1 / (.05 + _distance(target, candidate)) for candidate in nearest]
        outcomes = [candidate["outcome"] for candidate in nearest]
        final_changes = [candidate["final_change"] for candidate in nearest]
        predictions.append({
            "condition_id": target["condition_id"],
            "observed_at": target["observed_at"],
            "actual_outcome": target["outcome"],
            "analog_probability": _weighted_mean(outcomes, weights),
            "ai_probability": target["ai_probability"],
            "market_probability": target["market_probability"],
            "neighbor_count": len(nearest),
            "neighbor_ids": [candidate["condition_id"] for candidate in nearest],
            "agreement": abs(_weighted_mean(outcomes, weights) - .5) * 2,
            "future_move": {
                "probability_up": _weighted_mean([float(change > 0) for change in final_changes], weights),
                "mean": _weighted_mean(final_changes, weights),
                "median": _weighted_median(final_changes, weights),
                "max_favorable_mean": _weighted_mean([candidate["max_favorable"] for candidate in nearest], weights),
                "max_adverse_mean": _weighted_mean([candidate["max_adverse"] for candidate in nearest], weights),
            },
        })

    metrics = _metrics(predictions) if predictions else {}
    fold_metrics = []
    for index in range(folds):
        start = index * len(predictions) // folds
        end = (index + 1) * len(predictions) // folds
        if end > start:
            fold_metrics.append({"fold": index + 1, "predictions": end - start, **_metrics(predictions[start:end])})

    beats_baselines = bool(metrics) and all(
        metrics["analog_brier"] < metrics[f"{baseline}_brier"]
        and metrics["analog_log_loss"] < metrics[f"{baseline}_log_loss"]
        for baseline in ("ai", "market")
    )
    stable_folds = len(fold_metrics) == folds and all(
        fold["analog_brier"] < fold["ai_brier"] and fold["analog_brier"] < fold["market_brier"]
        and fold["analog_log_loss"] < fold["ai_log_loss"]
        and fold["analog_log_loss"] < fold["market_log_loss"]
        for fold in fold_metrics
    )
    ready = len(predictions) >= min_predictions and beats_baselines and stable_folds
    reasons = []
    if len(predictions) < min_predictions:
        reasons.append(f"need {min_predictions - len(predictions)} more walk-forward predictions")
    if predictions and not beats_baselines:
        reasons.append("does not beat both AI and market baselines")
    if predictions and not stable_folds:
        reasons.append("improvement is not stable across chronological folds")

    return {
        "resolved_unique_markets": len(episodes),
        "predictions": len(predictions),
        "skipped_without_prior_history": len(episodes) - len(predictions),
        "neighbors": neighbors,
        "min_neighbors": min_neighbors,
        "metrics": metrics,
        "folds": fold_metrics,
        "live_gate": {"ready": ready, "reasons": reasons or ["all offline gates passed"]},
        "details": predictions,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--estimates", default="../data/estimates.jsonl")
    parser.add_argument("--neighbors", type=int, default=20)
    parser.add_argument("--min-neighbors", type=int, default=10)
    parser.add_argument("--min-predictions", type=int, default=100)
    parser.add_argument("--folds", type=int, default=3)
    parser.add_argument("--details", action="store_true")
    args = parser.parse_args()
    result = analyze(load_jsonl(Path(args.estimates)), args.neighbors, args.min_neighbors,
                     args.min_predictions, args.folds)
    if not args.details:
        result.pop("details")
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
