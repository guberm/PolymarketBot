import unittest

from config import BotConfig
from execution import BookLevel, calculate_buy_quote, calculate_buy_shares_quote, calculate_sell_quote
from models import PortfolioSnapshot, Position, Side
from portfolio import Portfolio


class ExecutionPricingTests(unittest.TestCase):
    def test_token_denominated_buy_walks_asks(self):
        quote = calculate_buy_shares_quote([BookLevel(0.5, 3), BookLevel(0.6, 4)], 5)
        self.assertTrue(quote.complete)
        self.assertAlmostEqual(quote.filled_value, 2.7)
        self.assertAlmostEqual(quote.vwap, 0.54)
        self.assertAlmostEqual(quote.worst_price, 0.6)

    def test_buy_walks_asks_and_returns_vwap(self):
        quote = calculate_buy_quote(
            [BookLevel(0.50, 10), BookLevel(0.40, 10)],
            amount_usd=6.0,
        )

        self.assertTrue(quote.complete)
        self.assertAlmostEqual(quote.filled_quantity, 14.0)
        self.assertAlmostEqual(quote.vwap, 6.0 / 14.0)
        self.assertAlmostEqual(quote.worst_price, 0.50)

    def test_buy_reports_insufficient_depth(self):
        quote = calculate_buy_quote([BookLevel(0.40, 10)], amount_usd=10.0)

        self.assertFalse(quote.complete)
        self.assertAlmostEqual(quote.filled_value, 4.0)
        self.assertAlmostEqual(quote.filled_quantity, 10.0)

    def test_sell_walks_bids_and_returns_vwap(self):
        quote = calculate_sell_quote(
            [BookLevel(0.50, 10), BookLevel(0.60, 10)],
            shares=15.0,
        )

        self.assertTrue(quote.complete)
        self.assertAlmostEqual(quote.filled_value, 8.5)
        self.assertAlmostEqual(quote.vwap, 8.5 / 15.0)
        self.assertAlmostEqual(quote.worst_price, 0.50)


class LiquidationEquityTests(unittest.TestCase):
    def test_drawdown_uses_liquidation_value_not_cost_basis(self):
        config = BotConfig(
            initial_bankroll=100.0,
            daily_stop_loss_pct=0.20,
            max_drawdown_pct=0.90,
        )
        portfolio = Portfolio(config)
        portfolio.bankroll = 50.0
        portfolio.positions = [
            Position(
                condition_id="market-1",
                question="Test",
                side=Side.YES,
                token_id="token-1",
                entry_price=0.50,
                size_usd=50.0,
                shares=100.0,
                current_price=0.20,
                unrealized_pnl=-30.0,
                category="test",
            )
        ]

        self.assertAlmostEqual(portfolio.liquidation_value(), 20.0)
        self.assertAlmostEqual(portfolio.equity(), 70.0)
        self.assertFalse(portfolio.check_portfolio_risk())

    def test_healthy_refreshed_portfolio_clears_persisted_halt(self):
        position = Position(
            condition_id="recovery-market",
            question="Recovery test",
            side=Side.YES,
            token_id="recovery-token",
            entry_price=0.50,
            size_usd=5.0,
            shares=10.0,
            current_price=0.60,
            unrealized_pnl=1.0,
            category="test",
        )
        portfolio = Portfolio(BotConfig(daily_stop_loss_pct=1.0, max_drawdown_pct=0.99), PortfolioSnapshot(
            bankroll=0.50,
            initial_bankroll=10.0,
            positions=[position],
            high_water_mark=6.50,
            daily_start_value=6.50,
            total_realized_pnl=0.0,
            total_trades=0,
            is_halted=True,
        ))

        self.assertTrue(portfolio.check_portfolio_risk())
        self.assertFalse(portfolio.is_halted)

    def test_missing_position_quotes_defer_risk_check(self):
        portfolio = Portfolio(BotConfig(initial_bankroll=.98, quote_failure_grace_cycles=3))
        portfolio.positions = [
            Position("market", "Quote outage", Side.YES, "token", .5, 5, 10, .4, -1, "test")
        ]

        for _ in range(3):
            portfolio.update_position_quotes({})

        self.assertEqual(portfolio.positions[0].current_price, 0)
        self.assertTrue(portfolio.should_defer_risk_check())
        self.assertTrue(portfolio.check_portfolio_risk())
        self.assertFalse(portfolio.is_halted)


if __name__ == "__main__":
    unittest.main()
