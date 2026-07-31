"""Calibration-derived provider weights for the live ensemble."""

import json
from pathlib import Path


def load_provider_stats(path: Path) -> dict[str, tuple[int, float]]:
    if not path.exists():
        return {}

    def rows():
        with path.open(encoding="utf-8") as stream:
            for line in stream:
                try:
                    yield json.loads(line)
                except (json.JSONDecodeError, ValueError):
                    continue

    outcomes = {}
    for row in rows():
        if row.get("record_type") == "resolution":
            try:
                outcomes[str(row.get("condition_id"))] = float(row["actual_outcome"])
            except (KeyError, TypeError, ValueError):
                continue
    totals: dict[str, list[float]] = {}
    for row in rows():
        outcome = outcomes.get(str(row.get("condition_id")))
        if outcome is None or row.get("record_type", "evaluation") != "evaluation":
            continue
        for provider, probability in (row.get("provider_estimates") or {}).items():
            try:
                probability = float(probability)
            except (TypeError, ValueError):
                continue
            count, squared_error = totals.setdefault(provider, [0, 0.0])
            totals[provider] = [count + 1, squared_error + (probability - outcome) ** 2]
    return {provider: (int(values[0]), values[1]) for provider, values in totals.items()}


def calibration_weights(
    stats: dict[str, tuple[int, float]], providers: list[str], min_samples: int,
    shrinkage: float, max_weight: float,
) -> dict[str, float]:
    if not providers or any(stats.get(provider, (0, 0))[0] < min_samples for provider in providers):
        return {}
    if len(providers) == 1:
        return {providers[0]: 1.0}
    inverse = {
        provider: 1 / max(stats[provider][1] / stats[provider][0], 0.01)
        for provider in providers
    }
    total = sum(inverse.values())
    equal = 1 / len(providers)
    shrinkage = min(1.0, max(0.0, shrinkage))
    desired = {
        provider: shrinkage * equal + (1 - shrinkage) * inverse[provider] / total
        for provider in providers
    }
    cap = max(equal, min(1.0, max_weight))
    fixed: set[str] = set()
    while True:
        remaining = [provider for provider in providers if provider not in fixed]
        mass = 1 - cap * len(fixed)
        scale = mass / sum(desired[provider] for provider in remaining)
        newly_fixed = {provider for provider in remaining if desired[provider] * scale > cap}
        if not newly_fixed:
            return {
                provider: cap if provider in fixed else desired[provider] * scale
                for provider in providers
            }
        fixed.update(newly_fixed)
