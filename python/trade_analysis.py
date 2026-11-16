"""Trade-rationale audit: why does the ensemble disagree with the market?

Mirror of the .NET Services/TradeAnalysis.cs. Classifies each provider
estimate against the market price:
  ECHO        - within the band of the market price (repeats the price its
                prompt anchored it to; carries no information);
  INDEPENDENT - differs from the market (a real opinion);
  UNSTABLE    - the provider's own recent estimates for this market swing
                widely, so a trimmed mean can be an artifact of one draw.
When independent voices carry the edge while echoes confirm a deep market,
the likeliest explanation is a knowledge gap (recent events the models do
not know) - not skill. Verdict lines say so explicitly.
"""
from __future__ import annotations

import json
import logging
import math
import os
from dataclasses import dataclass
from typing import Optional

ECHO_BAND_PP = 3.0        # |est - market| <= 3pp -> echo
UNSTABLE_STD = 0.08       # per-provider std across recent cycles
THIN_MARKET_VOLUME = 10_000.0


@dataclass
class ProviderAnalysis:
    provider: str
    estimate: float
    delta_pp: float
    classification: str


def analyze(
    market_yes_price: float,
    provider_estimates: dict[str, float],
    recent_history: Optional[dict[str, list[float]]],
    volume: float,
    liquidity: float,
) -> tuple[list[ProviderAnalysis], list[str]]:
    """Pure classification - unit-testable, no I/O."""
    providers: list[ProviderAnalysis] = []
    verdicts: list[str] = []
    independents: list[str] = []
    echoes: list[str] = []
    unstable: list[str] = []

    for provider in sorted(provider_estimates):
        est = provider_estimates[provider]
        delta = (est - market_yes_price) * 100
        cls = "ECHO"
        hist = (recent_history or {}).get(provider)
        if hist and len(hist) >= 2:
            mean = sum(hist) / len(hist)
            std = math.sqrt(sum((x - mean) ** 2 for x in hist) / (len(hist) - 1))
            if std > UNSTABLE_STD:
                cls = f"UNSTABLE(std={std:.2f})"
                unstable.append(provider)
        if cls == "ECHO":
            cls = "ECHO" if abs(delta) <= ECHO_BAND_PP else "INDEPENDENT"
            if cls == "ECHO":
                echoes.append(provider)
            else:
                independents.append(provider)

        providers.append(ProviderAnalysis(provider, est, delta, cls))

    if len(independents) >= 2 and echoes:
        verdicts.append(
            f"PRIOR_VS_MARKET: {len(independents)} independent voices "
            f"({', '.join(independents)}) disagree while {len(echoes)} echo(es) "
            "confirm the market - likeliest a knowledge gap (recent events the "
            "models don't know), not skill. Weight this edge skeptically."
        )
    if len(independents) >= 2 and not echoes:
        verdicts.append(
            f"BROAD_DISAGREEMENT: all {len(independents)} providers disagree "
            "with the market - candidate mispricing, not anchoring."
        )
    if len(independents) == 1:
        verdicts.append(
            f"SINGLE_VOICE: only {independents[0]} is independent - the ensemble "
            f"edge leans on one opinion against {len(echoes)} echoes of the market price."
        )
    if unstable:
        verdicts.append(
            f"UNSTABLE: {', '.join(unstable)} - the ensemble edge may be a trim "
            "artifact of one volatile draw."
        )
    if volume < THIN_MARKET_VOLUME:
        verdicts.append(
            f"THIN_MARKET: volume ${volume:,.0f}, liquidity ${liquidity:,.0f} - "
            "the market price itself is unreliable."
        )
    return providers, verdicts


def recent_history(data_dir: str, condition_id: str, take: int = 5) -> dict[str, list[float]]:
    """Load the last `take` estimates per provider for this market from estimates.jsonl."""
    result: dict[str, list[float]] = {}
    path = os.path.join(data_dir, "estimates.jsonl")
    if not os.path.exists(path):
        return result
    try:
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                if condition_id not in line or '"provider_estimates"' not in line:
                    continue
                try:
                    rec = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if rec.get("record_type") != "evaluation":
                    continue
                for provider, value in (rec.get("provider_estimates") or {}).items():
                    result.setdefault(provider, []).append(float(value))
    except OSError:
        return result
    return {k: v[-take:] for k, v in result.items()}


def log_analysis(logger: logging.Logger, market, estimate, data_dir: str) -> None:
    """Emit the audit into the bot log at BUY time (mirror of .NET LogAnalysis)."""
    history = recent_history(data_dir, market.condition_id)
    providers, verdicts = analyze(
        market.outcome_yes_price,
        estimate.provider_estimates,
        history,
        getattr(market, "volume", 0.0),
        getattr(market, "liquidity", 0.0),
    )
    logger.info("TRADE ANALYSIS [%s]", market.question[:60])
    for p in providers:
        logger.info(
            "  %s=%.0f%% Δ%+.1fpp vs market %.0f%% → %s",
            p.provider, p.estimate * 100, p.delta_pp, market.outcome_yes_price * 100, p.classification,
        )
    for verdict in verdicts:
        logger.warning("  VERDICT: %s", verdict)
    if not verdicts:
        logger.info("  VERDICT: all estimates within echo band - no independent disagreement to audit")
