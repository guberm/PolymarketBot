import json
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

from api_pricing import calculate_api_cost
from config import BotConfig
from estimator import Estimator
from execution import BookLevel, calculate_buy_quote, calculate_sell_quote
from market_scanner import MarketScanner
from models import Estimate, MarketInfo, Position, Side, Signal
from persistence import (
    get_resolution_candidates, load_snapshot, remove_resolution_watch, save_snapshot,
    track_resolution, track_resolutions, update_resolution_watchlist,
)
from portfolio import Portfolio
from runtime_safety import InstanceLock, OrderJournal, parse_order_fill, retry_delay_seconds


class ReliabilityTests(unittest.TestCase):
    def test_shared_execution_golden_vectors(self):
        vectors = json.loads((Path(__file__).parent.parent / "tests" / "golden_execution.json").read_text())
        for vector in vectors:
            levels = [BookLevel(*level) for level in vector["levels"]]
            quote = (calculate_buy_quote if vector["kind"] == "buy_usd" else calculate_sell_quote)(
                levels, vector["requested"]
            )
            self.assertEqual(quote.complete, vector["complete"], vector["name"])
            self.assertAlmostEqual(quote.filled_quantity, vector["filled_quantity"])
            self.assertAlmostEqual(quote.filled_value, vector["filled_value"])
            self.assertAlmostEqual(quote.vwap, vector["vwap"])
            self.assertAlmostEqual(quote.worst_price, vector["worst_price"])

    def test_partial_fill_parsing(self):
        fill = parse_order_fill({"status": "live", "size_matched": "4", "price": "0.55"}, "BUY", 0.6)
        self.assertEqual(fill.status, "LIVE")
        self.assertAlmostEqual(fill.shares, 4)
        self.assertAlmostEqual(fill.value, 2.2)

    def test_retry_delay_prefers_server_hint_and_is_bounded(self):
        self.assertEqual(retry_delay_seconds("7", 0), 7)
        self.assertEqual(retry_delay_seconds(None, 2), 4)
        self.assertEqual(retry_delay_seconds("999", 0), 60)

    def test_quote_failure_uses_haircut_then_zero(self):
        portfolio = Portfolio(BotConfig(quote_failure_grace_cycles=3, stale_quote_haircut_pct=0.25))
        portfolio.positions = [Position("m", "q", Side.YES, "t", .5, 5, 10, .4, -1, "x")]
        portfolio.update_position_quotes({})
        self.assertAlmostEqual(portfolio.positions[0].current_price, .3)
        portfolio.update_position_quotes({})
        self.assertAlmostEqual(portfolio.positions[0].current_price, .3)
        portfolio.update_position_quotes({})
        self.assertEqual(portfolio.positions[0].current_price, 0)

    def test_partial_sell_reduces_position(self):
        portfolio = Portfolio(BotConfig(initial_bankroll=10))
        portfolio.positions = [Position("m", "q", Side.YES, "t", .5, 5, 10, .6, 1, "x")]
        pnl = portfolio.reduce_position("m", 4, .6)
        self.assertAlmostEqual(pnl, .4)
        self.assertAlmostEqual(portfolio.bankroll, 12.4)
        self.assertAlmostEqual(portfolio.positions[0].shares, 6)
        self.assertAlmostEqual(portfolio.positions[0].size_usd, 3)

    def test_provider_specific_cost_and_instance_lock(self):
        self.assertAlmostEqual(calculate_api_cost("a=1/2,b=3/4", "b", 1_000_000, 500_000), 5.0)
        with tempfile.TemporaryDirectory() as directory:
            first, second = InstanceLock(directory), InstanceLock(directory)
            self.assertTrue(first.acquire())
            self.assertFalse(second.acquire())
            first.release()
            self.assertTrue(second.acquire())
            second.release()

    def test_invalid_model_json_still_counts_provider_cost(self):
        config = BotConfig(
            ai_provider="openai", openai_api_key="test", ensemble_size=1,
            api_pricing="openai=1/2",
        )
        response = Mock(status_code=200)
        response.raise_for_status.return_value = None
        response.json.return_value = {
            "choices": [{"message": {"content": "not json"}}],
            "usage": {"prompt_tokens": 1_000_000, "completion_tokens": 500_000},
        }
        market = MarketInfo("c", "q", "s", .5, .5, "y", "n", 1, 1, 1, .4, .6, .2,
                            "2030-01-01T00:00:00Z", "x", "e", "d")
        with patch("estimator.requests.post", return_value=response):
            estimator = Estimator(config)
            self.assertIsNone(estimator.estimate(market))
        self.assertAlmostEqual(estimator.last_api_cost_usd, 2.0)

    def test_multi_provider_calls_overlap(self):
        config = BotConfig(
            multi_provider=True, ensemble_size=2,
            openai_api_key="test", gemini_api_key="test",
        )
        estimator = Estimator(config)
        active = 0
        max_active = 0
        guard = threading.Lock()

        def fake_call(_market, _provider):
            nonlocal active, max_active
            with guard:
                active += 1
                max_active = max(max_active, active)
            time.sleep(.03)
            with guard:
                active -= 1
            return .5, "ok", 1, 1

        estimator._single_call = fake_call
        market = MarketInfo("c", "q", "s", .5, .5, "y", "n", 1, 1, 1, .4, .6, .2,
                            "2030-01-01T00:00:00Z", "x", "e", "d")
        self.assertIsNotNone(estimator.estimate(market))
        self.assertGreaterEqual(max_active, 2)

    def test_position_quote_reads_overlap(self):
        class FakeScanner:
            get_sell_quotes = MarketScanner.get_sell_quotes

            def __init__(self):
                self.active = 0
                self.max_active = 0
                self.guard = threading.Lock()

            def get_sell_quote(self, _token_id, shares):
                with self.guard:
                    self.active += 1
                    self.max_active = max(self.max_active, self.active)
                time.sleep(.03)
                with self.guard:
                    self.active -= 1
                return shares

        positions = [Position(str(i), "q", Side.YES, str(i), .5, 5, 10, .4, -1, "x") for i in range(3)]
        scanner = FakeScanner()
        quotes = scanner.get_sell_quotes(positions)
        self.assertEqual(len(quotes), 3)
        self.assertGreaterEqual(scanner.max_active, 2)

    def test_resolution_reads_overlap(self):
        class FakeScanner:
            check_market_resolutions = MarketScanner.check_market_resolutions

            def __init__(self):
                self.active = self.max_active = 0
                self.guard = threading.Lock()

            def check_market_resolution(self, _condition_id):
                with self.guard:
                    self.active += 1
                    self.max_active = max(self.max_active, self.active)
                time.sleep(.03)
                with self.guard:
                    self.active -= 1
                return None

        scanner = FakeScanner()
        self.assertEqual(len(scanner.check_market_resolutions(["a", "b", "c"])), 3)
        self.assertGreaterEqual(scanner.max_active, 2)

    def test_event_exposure_blocks_correlated_positions(self):
        portfolio = Portfolio(BotConfig(
            initial_bankroll=100, max_event_exposure_pct=.30,
            max_category_exposure_pct=1, max_total_exposure_pct=1,
        ))
        portfolio.open_position(Position(
            "held", "held", Side.YES, "held", .5, 20, 40, .5, 0, "politics",
            event_title="Election 2028",
        ))
        market = MarketInfo(
            "new", "new", "", .5, .5, "yes", "no", 100, 100, 100,
            .49, .51, .02, "", "politics", " election 2028 ", "",
        )
        estimate = Estimate("new", "new", .7, [.7], 0, "")
        signal = Signal(market, estimate, Side.YES, .2, .5, .5, .1, 15, 3)
        self.assertFalse(portfolio.check_risk(signal))
        market.event_title = "Different event"
        self.assertTrue(portfolio.check_risk(signal))

    def test_resolution_watchlist_tracks_unbought_markets(self):
        market = MarketInfo("c", "q", "s", .5, .5, "y", "n", 1, 1, 1, .4, .6, .2,
                            "2020-01-01T00:00:00Z", "x", "e", "d")
        with tempfile.TemporaryDirectory() as directory:
            track_resolution(market, directory)
            self.assertEqual(get_resolution_candidates(directory, 10), ["c"])
            remove_resolution_watch("c", directory)
            self.assertEqual(get_resolution_candidates(directory, 10), [])

            second = MarketInfo("d", "q", "s", .5, .5, "y", "n", 1, 1, 1, .4, .6, .2,
                                "2020-01-01T00:00:00Z", "x", "e", "d")
            track_resolutions([market, second], directory)
            update_resolution_watchlist(["c"], ["d"], directory, 1)
            self.assertEqual(get_resolution_candidates(directory, 10), [])

    def test_pending_order_journal_and_applied_id_survive_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            journal = OrderJournal(directory)
            intent_id = journal.begin({"kind": "BUY", "condition_id": "c", "side": "YES"})
            journal.submitted(intent_id, "order-1")
            journal.filled(intent_id, parse_order_fill(
                {"status": "matched", "size_matched": "2", "price": ".5"}, "BUY", .5))
            pending = OrderJournal(directory).pending()
            self.assertEqual(len(pending), 1)
            self.assertEqual(pending[0]["order_id"], "order-1")
            self.assertEqual(pending[0]["fill_shares"], 2)

            portfolio = Portfolio(BotConfig(initial_bankroll=10))
            portfolio.mark_order_applied("order-1")
            save_snapshot(portfolio.snapshot(), directory)
            resumed = Portfolio(BotConfig(initial_bankroll=10), load_snapshot(directory))
            self.assertTrue(resumed.has_applied_order("order-1"))

            journal.complete(intent_id)
            self.assertEqual(OrderJournal(directory).pending(), [])


if __name__ == "__main__":
    unittest.main()
