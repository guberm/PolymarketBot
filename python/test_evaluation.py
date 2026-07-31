import unittest

from analyze_estimates import brier_score, calibration_rows
from calibration import calibration_weights
from kalshi_shadow import market_match_score
from replay_estimates import ReplayConfig, replay


class EvaluationMetricTests(unittest.TestCase):
    def test_calibration_weights_are_gated_shrunk_and_capped(self):
        stats = {"good": (40, 2.0), "bad": (40, 12.0)}
        weights = calibration_weights(stats, ["good", "bad"], 40, .25, .65)
        self.assertGreater(weights["good"], weights["bad"])
        self.assertLessEqual(weights["good"], .65)
        self.assertAlmostEqual(sum(weights.values()), 1)
        self.assertEqual(calibration_weights(stats, ["good", "missing"], 40, .25, .65), {})

    def test_replay_is_deterministic_and_resolves_positions(self):
        rows = [
            {"condition_id": "a", "fair_probability": .8, "market_yes_price": .5,
             "market_no_price": .5, "category": "x", "event_title": "e"},
            {"record_type": "resolution", "condition_id": "a", "actual_outcome": 1},
        ]
        result = replay(rows, ReplayConfig(bankroll=100, min_edge=.1))
        self.assertEqual(result["trades"], 1)
        self.assertEqual(result["resolved_trades"], 1)
        self.assertGreater(result["realized_pnl"], 0)

    def test_brier_score(self):
        self.assertAlmostEqual(brier_score([0.8, 0.1], [1.0, 0.0]), 0.025)

    def test_calibration_buckets(self):
        rows = calibration_rows([0.82, 0.88, 0.12], [1.0, 0.0, 0.0], bucket_size=0.1)

        high = next(row for row in rows if row["bucket"] == "80-90%")
        low = next(row for row in rows if row["bucket"] == "10-20%")
        self.assertEqual(high["count"], 2)
        self.assertAlmostEqual(high["predicted"], 0.85)
        self.assertAlmostEqual(high["actual"], 0.5)
        self.assertEqual(low["count"], 1)
        self.assertAlmostEqual(low["actual"], 0.0)

    def test_market_match_requires_numbers_to_agree(self):
        self.assertGreater(
            market_match_score("Will Bitcoin exceed $150,000 in 2026?", "Bitcoin above $150,000 in 2026"),
            0.5,
        )
        self.assertEqual(
            market_match_score("Will Bitcoin exceed $150,000 in 2026?", "Bitcoin above $100,000 in 2026"),
            0.0,
        )


if __name__ == "__main__":
    unittest.main()
