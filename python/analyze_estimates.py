"""Analyze estimates.jsonl calibration and decision quality."""

from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path


def brier_score(probabilities: list[float], outcomes: list[float]) -> float:
    if not probabilities or len(probabilities) != len(outcomes):
        return 0.0
    return sum((p - outcome) ** 2 for p, outcome in zip(probabilities, outcomes)) / len(probabilities)


def calibration_rows(
    probabilities: list[float], outcomes: list[float], bucket_size: float = 0.1
) -> list[dict]:
    buckets: dict[int, list[tuple[float, float]]] = defaultdict(list)
    bucket_count = max(1, round(1.0 / bucket_size))
    for probability, outcome in zip(probabilities, outcomes):
        index = min(bucket_count - 1, max(0, int(probability / bucket_size)))
        buckets[index].append((probability, outcome))

    result = []
    for index in sorted(buckets):
        values = buckets[index]
        lower = index * bucket_size
        upper = min(1.0, lower + bucket_size)
        result.append({
            "bucket": f"{lower * 100:.0f}-{upper:.0%}",
            "count": len(values),
            "predicted": sum(p for p, _ in values) / len(values),
            "actual": sum(outcome for _, outcome in values) / len(values),
        })
    return result


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


def print_summary(rows: list[dict]) -> None:
    resolutions = {
        row.get("condition_id"): float(row["actual_outcome"])
        for row in rows
        if row.get("record_type") == "resolution" and row.get("actual_outcome") is not None
    }
    evaluations = [row for row in rows if row.get("record_type", "evaluation") == "evaluation"]
    resolved = [row for row in evaluations if row.get("condition_id") in resolutions]

    print(f"Evaluations: {len(evaluations)}")
    print(f"Decisions: {dict(Counter(row.get('decision', 'unknown') for row in evaluations))}")
    print(f"Reasons: {dict(Counter(row.get('reason', 'unknown') for row in evaluations))}")
    print(f"Resolved evaluations: {len(resolved)}")
    by_provider: dict[str, list[dict]] = defaultdict(list)
    for row in evaluations:
        by_provider[row.get("provider", "unknown")].append(row)
    print("Provider metrics:")
    for provider, values in sorted(by_provider.items()):
        avg_duration = sum(float(row.get("duration_seconds") or 0) for row in values) / len(values)
        total_cost = sum(float(row.get("api_cost_usd") or 0) for row in values)
        print(f"  {provider:14s} n={len(values):4d} avg_latency={avg_duration:.2f}s cost=${total_cost:.4f}")
    if not resolved:
        return

    outcomes = [resolutions[row["condition_id"]] for row in resolved]
    ai = [float(row.get("fair_probability", 0.5)) for row in resolved]
    market = [float(row.get("market_yes_price", 0.5)) for row in resolved]
    print(f"AI Brier:     {brier_score(ai, outcomes):.4f}")
    print(f"Market Brier: {brier_score(market, outcomes):.4f}")
    provider_predictions: dict[str, list[tuple[float, float]]] = defaultdict(list)
    for row, outcome in zip(resolved, outcomes):
        for provider, probability in (row.get("provider_estimates") or {}).items():
            provider_predictions[provider].append((float(probability), outcome))
    for provider, values in sorted(provider_predictions.items()):
        print(f"{provider} Brier: {brier_score([p for p, _ in values], [o for _, o in values]):.4f} (n={len(values)})")
    print("Calibration:")
    for row in calibration_rows(ai, outcomes):
        print(
            f"  {row['bucket']:8s} n={row['count']:4d} "
            f"pred={row['predicted']:.1%} actual={row['actual']:.1%}"
        )


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze Polymarket probability estimates")
    parser.add_argument("--estimates", default="../data/estimates.jsonl")
    args = parser.parse_args()
    print_summary(load_jsonl(Path(args.estimates)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
