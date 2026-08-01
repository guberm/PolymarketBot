import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from historical_analogs import analyze
from models import Estimate, MarketInfo
from persistence import append_estimate_evaluation


def evaluation(condition_id: str, timestamp: float, price: float, fair: float, category: str = "politics") -> dict:
    return {
        "record_type": "evaluation",
        "timestamp": timestamp,
        "condition_id": condition_id,
        "category": category,
        "fair_probability": fair,
        "market_yes_price": price,
        "market_no_price": 1 - price,
        "liquidity": 10_000,
        "volume_24hr": 5_000,
        "spread": 0.02,
        "time_to_resolution_hours": 48,
        "provider_estimates": {"a": fair - 0.02, "b": fair + 0.02},
    }


class HistoricalAnalogTests(unittest.TestCase):
    def test_walk_forward_uses_one_episode_per_market_and_no_future_outcomes(self):
        rows = [
            evaluation("a", 10, .60, .65),
            evaluation("future", 12, .59, .60),
            evaluation("a", 15, .70, .68),  # overlapping observation of the same episode
            {"record_type": "resolution", "timestamp": 20, "condition_id": "a", "actual_outcome": 1},
            evaluation("b", 25, .20, .25),
            {"record_type": "resolution", "timestamp": 30, "condition_id": "b", "actual_outcome": 0},
            evaluation("target", 40, .58, .62),
            {"record_type": "resolution", "timestamp": 50, "condition_id": "target", "actual_outcome": 1},
            {"record_type": "resolution", "timestamp": 100, "condition_id": "future", "actual_outcome": 1},
        ]

        result = analyze(rows, neighbors=5, min_neighbors=2, min_predictions=1, folds=1)

        self.assertEqual(result["predictions"], 1)
        prediction = result["details"][0]
        self.assertEqual(prediction["condition_id"], "target")
        self.assertEqual(set(prediction["neighbor_ids"]), {"a", "b"})
        self.assertEqual(prediction["neighbor_count"], 2)
        self.assertGreater(prediction["analog_probability"], .5)
        self.assertGreater(prediction["future_move"]["max_favorable_mean"], 0)

    def test_evaluation_journal_contains_market_context(self):
        market = MarketInfo(
            condition_id="context", question="Context?", slug="context",
            outcome_yes_price=.55, outcome_no_price=.45,
            token_id_yes="yes", token_id_no="no",
            liquidity=12_345, volume=67_890, volume_24hr=2_345,
            best_bid=.54, best_ask=.56, spread=.02,
            end_date=(datetime.now(timezone.utc) + timedelta(hours=6)).isoformat(),
            category="politics", event_title="Election", description="",
        )
        estimate = Estimate("context", "Context?", .6, [.58, .62], .02, "")

        with tempfile.TemporaryDirectory() as data_dir:
            append_estimate_evaluation(market, estimate, data_dir, "multi", "skip", "test", track_watch=False)
            row = json.loads((Path(data_dir) / "estimates.jsonl").read_text(encoding="utf-8"))

        self.assertEqual(row["liquidity"], 12_345)
        self.assertEqual(row["volume_24hr"], 2_345)
        self.assertEqual(row["spread"], .02)
        self.assertEqual(row["end_date"], market.end_date)
        self.assertGreater(row["time_to_resolution_hours"], 5.9)
        self.assertLess(row["time_to_resolution_hours"], 6.1)


if __name__ == "__main__":
    unittest.main()
