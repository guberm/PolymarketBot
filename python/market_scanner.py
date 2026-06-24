"""Gamma API integration for market discovery and filtering."""

import json
import logging
import time
from datetime import datetime, timezone
from typing import Optional

import requests

from config import BotConfig
from models import MarketInfo

log = logging.getLogger("bot.scanner")

# Keywords for rough category classification from event slugs/titles
CATEGORY_KEYWORDS = {
    "politics": ["president", "election", "congress", "senate", "governor", "vote", "party",
                  "democrat", "republican", "trump", "biden", "political", "inaugur",
                  "legislation", "supreme court", "cabinet", "impeach", "primary"],
    "geopolitics": ["iran", "israel", "strike", "invade", "invasion", "war", "military",
                    "nato", "sanction", "nuclear", "missile", "ceasefire", "peace deal",
                    "china", "taiwan", "russia", "ukraine", "north korea", "tariff"],
    "sports": ["nfl", "nba", "mlb", "nhl", "soccer", "football", "basketball", "baseball",
               "tennis", "ufc", "fight", "championship", "super bowl", "world series",
               "premier league", "match", "game", "serie a", "ncaa", "ligue 1",
               "olympics", "medal", "la liga", "bundesliga", "win on 202", "rio open",
               "open:", "grand slam"],
    "crypto": ["bitcoin", "btc", "ethereum", "eth", "crypto", "solana", "sol", "token",
               "defi", "blockchain", "coin", "memecoin", "fdv", "airdrop"],
    "tech": ["ai model", "claude", "gpt", "openai", "anthropic", "google ai", "apple",
             "microsoft", "tesla", "spacex", "launch", "release", "chip", "semiconductor"],
    "social_media": ["tweet", "post", "elon musk", "follower", "subscriber", "tiktok",
                     "youtube", "instagram", "x.com"],
    "weather": ["weather", "temperature", "hurricane", "storm", "rainfall", "snow", "climate"],
    "entertainment": ["oscar", "grammy", "emmy", "movie", "film", "tv", "show", "album",
                      "music", "celebrity", "award", "box office"],
    "finance": ["fed", "interest rate", "inflation", "gdp", "stock", "market", "s&p",
                "nasdaq", "dow", "recession", "unemployment", "spx", "treasury"],
}


class MarketScanner:
    def __init__(self, config: BotConfig):
        self.config = config
        self.session = requests.Session()
        self.session.headers.update({"Accept": "application/json"})
        self.base_url = config.gamma_api_host

    def scan(self) -> list[MarketInfo]:
        """Fetch all active markets, filter, and return eligible MarketInfo list."""
        raw_events = self._fetch_all_events()
        markets = []

        for event in raw_events:
            event_title = event.get("title", "")
            event_slug = event.get("slug", "")
            description = event.get("description", "")
            category = self._categorize(event_title, event_slug)

            for mkt in event.get("markets", []):
                parsed = self._parse_market(mkt, event_title, description, category)
                if parsed is not None:
                    markets.append(parsed)

        # Sort by 24h volume descending (highest activity first)
        markets.sort(key=lambda m: m.volume_24hr, reverse=True)
        log.info(f"Scan complete: {len(raw_events)} events, {len(markets)} eligible markets")
        return markets

    def _fetch_all_events(self) -> list[dict]:
        """Fetch all active events with pagination."""
        all_events = []
        offset = 0
        limit = 100

        while True:
            page = self._fetch_events_page(offset, limit)
            if not page:
                break
            all_events.extend(page)
            if len(page) < limit:
                break
            offset += limit

        return all_events

    def _fetch_events_page(self, offset: int, limit: int = 100) -> list[dict]:
        """Single page fetch with retry logic."""
        url = f"{self.base_url}/events"
        params = {
            "active": "true",
            "closed": "false",
            "limit": limit,
            "offset": offset,
        }

        for attempt in range(3):
            try:
                resp = self.session.get(url, params=params, timeout=30)
                if resp.status_code == 429:
                    wait = 2 ** attempt
                    log.warning(f"Rate limited on /events, retrying in {wait}s")
                    time.sleep(wait)
                    continue
                if resp.status_code == 422 and offset > 0:
                    log.debug(f"Reached end of /events pagination at offset {offset}")
                    return []
                resp.raise_for_status()
                return resp.json()
            except requests.RequestException as e:
                if attempt < 2:
                    wait = 2 ** attempt
                    log.warning(f"Error fetching events (attempt {attempt + 1}): {e}, retrying in {wait}s")
                    time.sleep(wait)
                else:
                    log.error(f"Failed to fetch events after 3 attempts: {e}")
                    return []

        return []

    def _parse_market(self, mkt: dict, event_title: str, description: str,
                      category: str) -> Optional[MarketInfo]:
        """Parse a single market dict into MarketInfo. Returns None if filtered out."""
        try:
            # Must be active and accepting orders
            if not mkt.get("active", False) or mkt.get("closed", False):
                return None

            # Parse outcome prices (JSON-encoded string)
            outcomes_raw = mkt.get("outcomes", "[]")
            if isinstance(outcomes_raw, str):
                outcomes = json.loads(outcomes_raw)
            else:
                outcomes = outcomes_raw

            # Only binary markets
            if len(outcomes) != 2:
                return None

            prices_raw = mkt.get("outcomePrices", "[]")
            if isinstance(prices_raw, str):
                prices = json.loads(prices_raw)
            else:
                prices = prices_raw

            if len(prices) != 2:
                return None

            yes_price = float(prices[0])
            no_price = float(prices[1])

            # Parse token IDs (JSON-encoded string)
            tokens_raw = mkt.get("clobTokenIds", "[]")
            if isinstance(tokens_raw, str):
                tokens = json.loads(tokens_raw)
            else:
                tokens = tokens_raw

            if len(tokens) != 2:
                return None

            token_yes = tokens[0]
            token_no = tokens[1]

            # Liquidity and volume filters
            liquidity = float(mkt.get("liquidity", 0) or 0)
            volume = float(mkt.get("volume", 0) or 0)
            volume_24hr = float(mkt.get("volume24hr", 0) or 0)

            if liquidity < self.config.min_liquidity:
                return None
            if volume_24hr < self.config.min_volume_24hr:
                return None

            # Price filter — skip markets where neither side is in the tradeable range
            # Markets at extreme prices (e.g. YES=0.001, NO=0.999) have no FOK liquidity
            min_p = self.config.min_market_price
            max_p = 1.0 - min_p
            if not (min_p <= yes_price <= max_p or min_p <= no_price <= max_p):
                return None

            # Time to resolution filter
            end_date_str = mkt.get("endDate", "")
            if end_date_str:
                try:
                    end_date = datetime.fromisoformat(end_date_str.replace("Z", "+00:00"))
                    now = datetime.now(timezone.utc)
                    hours_left = (end_date - now).total_seconds() / 3600
                    if hours_left < self.config.min_time_to_resolution_hours:
                        return None
                except (ValueError, TypeError):
                    pass  # If we can't parse the date, don't filter on it

            # Spread
            best_bid = float(mkt.get("bestBid", 0) or 0)
            best_ask = float(mkt.get("bestAsk", 0) or 0)
            spread = best_ask - best_bid if best_ask > best_bid else 0.0

            # Filter wide spreads — indicates thin liquidity and poor fill quality
            if best_bid > 0 and best_ask > 0 and spread > self.config.max_spread:
                return None

            question = mkt.get("question", event_title)
            slug = mkt.get("slug", "")
            mkt_description = mkt.get("description", description)

            return MarketInfo(
                condition_id=mkt.get("conditionId", ""),
                question=question,
                slug=slug,
                outcome_yes_price=yes_price,
                outcome_no_price=no_price,
                token_id_yes=token_yes,
                token_id_no=token_no,
                liquidity=liquidity,
                volume=volume,
                volume_24hr=volume_24hr,
                best_bid=best_bid,
                best_ask=best_ask,
                spread=spread,
                end_date=end_date_str,
                category=category,
                event_title=event_title,
                description=mkt_description or "",
            )

        except (KeyError, ValueError, TypeError) as e:
            log.debug(f"Failed to parse market: {e}")
            return None

    def _categorize(self, title: str, slug: str) -> str:
        """Classify market category from event title/slug using keyword matching."""
        text = f"{title} {slug}".lower()
        for category, keywords in CATEGORY_KEYWORDS.items():
            if any(kw in text for kw in keywords):
                return category
        return "other"

    def check_market_resolution(self, condition_id: str) -> Optional[dict]:
        """Check if a market has resolved via CLOB API.
        Returns {"winning_side": "YES"|"NO"} if resolved, None if still active."""
        try:
            url = f"{self.config.clob_host}/markets/{condition_id}"
            resp = self.session.get(url, timeout=10)
            if resp.status_code == 404:
                return None
            resp.raise_for_status()
            data = resp.json()

            if not data.get("closed", False):
                return None

            # CLOB returns tokens array with winner flag
            tokens = data.get("tokens", [])
            for token in tokens:
                if token.get("winner", False):
                    outcome = token.get("outcome", "").upper()
                    if outcome == "YES":
                        return {"winning_side": "YES"}
                    elif outcome == "NO":
                        return {"winning_side": "NO"}

            # Market closed but no winner flagged yet
            return None
        except Exception as e:
            log.debug(f"Resolution check failed for {condition_id[:20]}...: {e}")
            return None

    def get_market_prices(self, token_ids: list[str]) -> dict[str, float]:
        """Fetch current prices for multiple tokens. Returns dict of token_id -> midpoint price."""
        prices = {}
        for tid in token_ids:
            p = self.get_market_price(tid)
            if p is not None and p > 0:
                prices[tid] = p
        return prices

    def get_market_price(self, token_id: str) -> Optional[float]:
        """Fetch current price for a single token from the CLOB API."""
        try:
            url = f"{self.config.clob_host}/midpoint"
            params = {"token_id": token_id}
            resp = self.session.get(url, params=params, timeout=10)
            resp.raise_for_status()
            data = resp.json()
            return float(data.get("mid", 0))
        except Exception as e:
            log.debug(f"Failed to get price for {token_id[:20]}...: {e}")
            return None
