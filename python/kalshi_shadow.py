"""Optional read-only Kalshi reference lookup for evaluated Polymarket markets."""

import logging
import re
from typing import Optional

import requests

from config import BotConfig
from models import MarketInfo

log = logging.getLogger("bot.kalshi_shadow")

_NUMBER_RE = re.compile(r"\d[\d,]*(?:\.\d+)?")
_WORD_RE = re.compile(r"[a-z0-9]+")


def _tokens(text: str) -> tuple[set[str], set[str]]:
    numbers = {value.replace(",", "") for value in _NUMBER_RE.findall(text.lower())}
    words = set(_WORD_RE.findall(_NUMBER_RE.sub(" ", text.lower()))) | numbers
    return words, numbers


def market_match_score(left: str, right: str) -> float:
    """Return token overlap, but never match markets with different numbers."""
    left_tokens, left_numbers = _tokens(left)
    right_tokens, right_numbers = _tokens(right)
    if left_numbers != right_numbers:
        return 0.0
    union = left_tokens | right_tokens
    return len(left_tokens & right_tokens) / len(union) if union else 0.0


class KalshiShadow:
    def __init__(self, config: BotConfig):
        self.config = config
        self._markets: list[dict] = []

    def refresh(self) -> None:
        """Fetch a bounded public market snapshot once for the current cycle."""
        try:
            response = requests.get(
                f"{self.config.kalshi_api_host.rstrip('/')}/markets",
                params={
                    "status": "open",
                    "mve_filter": "exclude",
                    "limit": max(1, min(self.config.kalshi_markets_limit, 1000)),
                },
                timeout=20,
            )
            response.raise_for_status()
            self._markets = [
                market for market in response.json().get("markets", [])
                if market.get("title") and not market.get("mve_collection_ticker")
            ]
            log.info("Kalshi shadow: loaded %d open reference markets", len(self._markets))
        except Exception as exc:
            self._markets = []
            log.warning("Kalshi shadow refresh failed: %s", exc)

    def find_reference(self, market: MarketInfo, estimator) -> tuple[Optional[dict], float]:
        best = None
        best_score = 0.0
        for candidate in self._markets:
            score = market_match_score(market.question, candidate.get("title", ""))
            if score > best_score:
                best, best_score = candidate, score

        if best is None or best_score < self.config.kalshi_min_match_score:
            return None, 0.0

        verification = estimator.verify_market_equivalence(market, best)
        probability = verification[0] if verification else None
        api_cost = verification[3] if verification else 0.0
        reference = {
            "ticker": best.get("ticker", ""),
            "title": best.get("title", ""),
            "close_time": best.get("close_time", ""),
            "yes_bid": _price(best.get("yes_bid_dollars")),
            "yes_ask": _price(best.get("yes_ask_dollars")),
            "liquidity": _price(best.get("liquidity_dollars")),
            "match_score": best_score,
            "llm_probability": probability,
            "same_market": probability is not None and probability >= self.config.kalshi_llm_same_threshold,
            "verified": probability is not None,
        }
        return reference, api_cost


def _price(value) -> float:
    try:
        return float(value or 0)
    except (TypeError, ValueError):
        return 0.0
