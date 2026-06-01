"""Portfolio management, risk checks, and Kelly criterion position sizing."""

import logging
import time
from typing import Optional

from config import BotConfig
from models import MarketInfo, Estimate, Signal, Side, Position, PortfolioSnapshot, ExitSignal, TopupCandidate

log = logging.getLogger("bot.portfolio")

# Approximate Claude API pricing (per million tokens)
INPUT_COST_PER_MTOK = 3.0
OUTPUT_COST_PER_MTOK = 15.0


class Portfolio:
    def __init__(self, config: BotConfig, snapshot: Optional[PortfolioSnapshot] = None):
        self.config = config
        if snapshot:
            self.bankroll = snapshot.bankroll
            self.initial_bankroll = snapshot.initial_bankroll
            self.positions = list(snapshot.positions)
            self.high_water_mark = snapshot.high_water_mark
            self.daily_start_value = snapshot.daily_start_value
            self.total_realized_pnl = snapshot.total_realized_pnl
            self.total_trades = snapshot.total_trades
            self.is_halted = snapshot.is_halted
        else:
            self.bankroll = config.initial_bankroll
            self.initial_bankroll = config.initial_bankroll
            self.positions = []
            self.high_water_mark = config.initial_bankroll
            self.daily_start_value = config.initial_bankroll
            self.total_realized_pnl = 0.0
            self.total_trades = 0
            self.is_halted = False

        self._recently_closed: dict[str, float] = {}  # condition_id -> unix timestamp of close
        self.total_api_cost = 0.0

    def snapshot(self) -> PortfolioSnapshot:
        return PortfolioSnapshot(
            bankroll=self.bankroll,
            initial_bankroll=self.initial_bankroll,
            positions=list(self.positions),
            high_water_mark=self.high_water_mark,
            daily_start_value=self.daily_start_value,
            total_realized_pnl=self.total_realized_pnl,
            total_trades=self.total_trades,
            is_halted=self.is_halted,
        )

    def total_exposure(self) -> float:
        return sum(p.size_usd for p in self.positions)

    def category_exposure(self, category: str) -> float:
        return sum(p.size_usd for p in self.positions if p.category == category)

    def has_position(self, condition_id: str) -> bool:
        return any(p.condition_id == condition_id for p in self.positions)

    # ── Signal generation ─────────────────────────────────────────────

    def generate_signal(self, market: MarketInfo, estimate: Estimate) -> Optional[Signal]:
        """Compare estimate to market price, return Signal if edge exceeds threshold."""
        fair = estimate.fair_probability

        yes_execution_price = self._estimate_buy_execution_price(market.outcome_yes_price)
        no_execution_price = self._estimate_buy_execution_price(market.outcome_no_price)
        yes_edge = fair - yes_execution_price
        no_edge = (1.0 - fair) - no_execution_price

        if yes_edge > no_edge and yes_edge > self.config.min_edge:
            side = Side.YES
            edge = yes_edge
            market_price = market.outcome_yes_price
            execution_price = yes_execution_price
        elif no_edge > self.config.min_edge:
            side = Side.NO
            edge = no_edge
            market_price = market.outcome_no_price
            execution_price = no_execution_price
        else:
            return None

        if execution_price <= 0 or execution_price >= 1:
            return None

        # Kelly criterion: f* = (b*p - q) / b
        b = (1.0 / execution_price) - 1.0  # decimal odds
        p = fair if side == Side.YES else (1.0 - fair)
        q = 1.0 - p
        kelly_raw = (b * p - q) / b if b > 0 else 0.0
        kelly_raw = max(0.0, kelly_raw)

        # Fractional Kelly + position cap (use portfolio value, not just cash)
        kelly_fraction = self.config.kelly_fraction
        max_position_pct = self.config.max_position_pct
        if self.config.live_trading and not self.config.allow_unsafe_risk:
            kelly_fraction = min(kelly_fraction, 0.50)
            max_position_pct = min(max_position_pct, 0.15)
        kelly = kelly_raw * kelly_fraction
        portfolio_val = self.bankroll + self.total_exposure()
        size_usd = kelly * portfolio_val
        size_usd = min(size_usd, portfolio_val * max_position_pct)
        size_usd = min(size_usd, self.bankroll)  # never exceed available cash

        if size_usd < self.config.min_trade_usd:
            return None

        # CLOB minimum: 5 tokens at the estimated executable BUY price.
        min_clob_usd = max(5.0 * execution_price, 1.0)
        if size_usd < min_clob_usd:
            log.debug(f"Position ${size_usd:.2f} below CLOB minimum ${min_clob_usd:.2f} (5 tokens @ exec {execution_price:.3f})")
            return None

        if (self.config.live_trading and not self.config.allow_unsafe_risk and self.bankroll > 0
                and min_clob_usd > self.bankroll * self.config.max_live_order_bankroll_pct):
            log.info(
                f"Live risk BLOCK: CLOB minimum ${min_clob_usd:.2f} would use "
                f"{min_clob_usd / self.bankroll:.0%} of bankroll "
                f"(limit {self.config.max_live_order_bankroll_pct:.0%})"
            )
            return None

        return Signal(
            market=market,
            estimate=estimate,
            side=side,
            edge=edge,
            market_price=market_price,
            execution_price=execution_price,
            kelly_fraction=kelly,
            position_size_usd=round(size_usd, 2),
            expected_value=round(size_usd * edge, 4),
        )

    def _estimate_buy_execution_price(self, market_price: float) -> float:
        return min(market_price + self.config.entry_price_buffer, 0.99)

    # ── Risk checks ───────────────────────────────────────────────────

    def check_risk(self, signal: Signal) -> bool:
        """Return True if the trade passes all risk limits."""
        if self.has_position(signal.market.condition_id):
            log.info(f"Risk BLOCK: already positioned in {signal.market.question[:40]}")
            return False

        cooldown_secs = self.config.scan_interval_minutes * 60 * 2  # 2 cycle cooldown
        closed_at = self._recently_closed.get(signal.market.condition_id)
        if closed_at is not None:
            elapsed = time.time() - closed_at
            if elapsed < cooldown_secs:
                remaining_min = (cooldown_secs - elapsed) / 60
                log.info(f"Risk BLOCK: recently closed {signal.market.question[:40]}... ({remaining_min:.0f}min cooldown remaining)")
                return False
            else:
                del self._recently_closed[signal.market.condition_id]

        if len(self.positions) >= self.config.max_concurrent_positions:
            log.info(f"Risk BLOCK: max positions ({self.config.max_concurrent_positions}) reached")
            return False

        pv = self.bankroll + self.total_exposure()
        new_exposure = self.total_exposure() + signal.position_size_usd
        max_total_exposure_pct = self.config.max_total_exposure_pct
        if self.config.live_trading and not self.config.allow_unsafe_risk:
            max_total_exposure_pct = min(max_total_exposure_pct, 0.90)
        max_allowed = pv * max_total_exposure_pct
        if new_exposure > max_allowed:
            log.info(f"Risk BLOCK: total exposure ${new_exposure:.2f} > limit ${max_allowed:.2f}")
            return False

        cat_exp = self.category_exposure(signal.market.category) + signal.position_size_usd
        cat_limit = pv * self.config.max_category_exposure_pct
        if cat_exp > cat_limit:
            log.info(f"Risk BLOCK: '{signal.market.category}' exposure ${cat_exp:.2f} > limit ${cat_limit:.2f}")
            return False

        # Daily stop loss (include open position value — deployed capital isn't lost)
        portfolio_value = self.bankroll + self.total_exposure()
        daily_pnl = portfolio_value - self.daily_start_value
        daily_stop_loss_pct = self.config.daily_stop_loss_pct
        if self.config.live_trading and not self.config.allow_unsafe_risk:
            daily_stop_loss_pct = min(daily_stop_loss_pct, 0.25)
        if daily_pnl < 0 and abs(daily_pnl) > self.daily_start_value * daily_stop_loss_pct:
            log.warning(f"HALT: Daily stop loss triggered (PnL=${daily_pnl:+.2f}, limit={daily_stop_loss_pct:.0%})")
            self.is_halted = True
            return False

        # Max drawdown from high water mark
        if self.high_water_mark > 0:
            drawdown = (self.high_water_mark - portfolio_value) / self.high_water_mark
            max_drawdown_pct = self.config.max_drawdown_pct
            if self.config.live_trading and not self.config.allow_unsafe_risk:
                max_drawdown_pct = min(max_drawdown_pct, 0.60)
            if drawdown > max_drawdown_pct:
                log.warning(f"HALT: Max drawdown {drawdown:.1%} exceeded (limit={max_drawdown_pct:.0%})")
                self.is_halted = True
                return False

        # Agent death — only when total portfolio value (free cash + open positions)
        # drops below $1. Negative bankroll from API costs while holding positions
        # is normal: positions will eventually resolve and return USDC.
        if self.bankroll + self.total_exposure() < 1.0:
            log.warning("HALT: Portfolio value < $1 — agent is dead")
            self.is_halted = True
            return False

        return True

    # ── Position management ───────────────────────────────────────────

    def open_position(self, position: Position) -> None:
        """Deduct cost from bankroll and track position."""
        self.bankroll -= position.size_usd
        self.total_trades += 1
        self.positions.append(position)
        log.info(
            f"Opened {position.side.value} on {position.question[:40]}... "
            f"${position.size_usd:.2f} @ {position.entry_price:.3f}"
        )

    def close_position(self, condition_id: str, exit_price: float) -> float:
        """Close position, return cost basis + PnL to bankroll. Returns realized PnL."""
        pos = next((p for p in self.positions if p.condition_id == condition_id), None)
        if pos is None:
            return 0.0

        pnl = pos.shares * (exit_price - pos.entry_price)
        self.bankroll += pos.size_usd + pnl
        self.total_realized_pnl += pnl
        self.positions = [p for p in self.positions if p.condition_id != condition_id]
        self._recently_closed[condition_id] = time.time()
        self.high_water_mark = max(self.high_water_mark, self.bankroll)

        log.info(f"Closed {pos.question[:40]}... PnL: ${pnl:+.2f}")
        return pnl

    def resolve_position(self, condition_id: str, won: bool) -> float:
        """Close a resolved market position. Won: payout = shares * $1.00. Lost: payout = $0.
        Returns realized PnL."""
        pos = next((p for p in self.positions if p.condition_id == condition_id), None)
        if pos is None:
            return 0.0

        payout = pos.shares if won else 0.0
        pnl = payout - pos.size_usd
        self.bankroll += payout
        self.total_realized_pnl += pnl
        self.total_trades += 1
        self.positions = [p for p in self.positions if p.condition_id != condition_id]
        self._recently_closed[condition_id] = time.time()
        self.high_water_mark = max(self.high_water_mark, self.bankroll)

        result = "WON" if won else "LOST"
        log.info(f"Resolved ({result}): {pos.question[:40]}... payout=${payout:.2f}, PnL=${pnl:+.2f}")
        return pnl

    # ── Position review ─────────────────────────────────────────────

    def update_position_prices(self, prices: dict[str, float]) -> None:
        """Update current_price and unrealized_pnl for all positions with fresh market data."""
        for pos in self.positions:
            if pos.token_id in prices:
                pos.current_price = prices[pos.token_id]
                pos.unrealized_pnl = pos.shares * (pos.current_price - pos.entry_price)

    def generate_exit_signals(self) -> list[ExitSignal]:
        """Tier 1: free rule-based exit checks on all positions."""
        signals = []
        for pos in self.positions:
            # Skip unsellable positions: penny prices or below CLOB minimum (5 tokens)
            if pos.current_price < 0.01:
                log.debug(f"Skip review: {pos.question[:40]}... (price {pos.current_price:.4f} < $0.01)")
                continue
            if pos.shares < 5.0:
                log.debug(f"Skip review: {pos.question[:40]}... ({pos.shares:.2f} tokens < 5 minimum)")
                continue

            pnl = pos.shares * (pos.current_price - pos.entry_price)
            pnl_pct = (pos.current_price - pos.entry_price) / pos.entry_price if pos.entry_price > 0 else 0.0

            # Stop-loss: price dropped too far from entry
            if pnl_pct < -self.config.position_stop_loss_pct:
                signals.append(ExitSignal(pos, "stop_loss", pos.current_price, pnl, pnl_pct))
                continue

            # Take-profit: price near certainty
            if pos.current_price >= self.config.take_profit_price:
                signals.append(ExitSignal(pos, "take_profit", pos.current_price, pnl, pnl_pct))
                continue

            # Edge-gone: market moved past our original fair estimate
            if pos.fair_estimate_at_entry > 0:
                # fair_for_side: what we estimated the correct price for our side was
                fair_for_side = pos.fair_estimate_at_entry if pos.side == Side.YES else (1.0 - pos.fair_estimate_at_entry)
                if pos.current_price > fair_for_side + self.config.exit_edge_buffer:
                    signals.append(ExitSignal(pos, "edge_gone", pos.current_price, pnl, pnl_pct))
                    continue

        return signals

    def generate_topup_candidates(self) -> list[TopupCandidate]:
        """Tiny positions (<5 tokens) that want to exit — need a top-up BUY to reach CLOB minimum."""
        candidates = []
        for pos in self.positions:
            if pos.current_price < 0.01:
                continue  # penny = unsellable even with top-up
            if pos.shares >= 5.0:
                continue  # can sell normally (handled by generate_exit_signals)

            if pos.entry_price <= 0:
                continue

            pnl_pct = (pos.current_price - pos.entry_price) / pos.entry_price

            # Check same exit conditions as generate_exit_signals
            exit_reason = None
            if pnl_pct < -self.config.position_stop_loss_pct:
                exit_reason = "stop_loss"
            elif pos.current_price >= self.config.take_profit_price:
                exit_reason = "take_profit"
            elif pos.fair_estimate_at_entry > 0:
                fair_for_side = pos.fair_estimate_at_entry if pos.side == Side.YES else (1.0 - pos.fair_estimate_at_entry)
                if pos.current_price > fair_for_side + self.config.exit_edge_buffer:
                    exit_reason = "edge_gone"

            if exit_reason is None:
                continue
            if exit_reason == "stop_loss" and self.config.stop_loss_requires_negative_edge:
                continue

            topup_cost = 5.0 * pos.current_price
            recovery_value = pos.shares * pos.current_price

            candidates.append(TopupCandidate(
                position=pos,
                exit_reason=exit_reason,
                tokens_to_buy=5.0,
                topup_cost=topup_cost,
                recovery_value=recovery_value,
            ))

        return candidates

    def add_to_position(self, condition_id: str, additional_shares: float, additional_cost: float) -> None:
        """Add tokens to an existing position (used for top-up before sell)."""
        pos = next((p for p in self.positions if p.condition_id == condition_id), None)
        if pos is None:
            return
        pos.shares += additional_shares
        pos.size_usd += additional_cost
        self.bankroll -= additional_cost
        log.info(
            f"Top-up: +{additional_shares:.2f} tokens (${additional_cost:.2f}) -> "
            f"{pos.question[:40]}... now {pos.shares:.2f} tokens"
        )

    def get_review_candidates(self) -> list[Position]:
        """Tier 2: positions that moved significantly and should be re-estimated by Claude."""
        candidates = []
        for pos in self.positions:
            if pos.entry_price <= 0:
                continue
            price_move = abs(pos.current_price - pos.entry_price) / pos.entry_price
            if price_move >= self.config.review_reestimate_threshold_pct:
                candidates.append(pos)
        # Review biggest positions first
        candidates.sort(key=lambda p: p.size_usd, reverse=True)
        return candidates

    # ── Balance sync ─────────────────────────────────────────────────

    def sync_balance(self, actual_usdc_balance: float) -> None:
        """Sync bankroll from actual on-chain USDC balance.

        On-chain USDC is always the free cash (bankroll) — conditional tokens
        are held separately in the CLOB and not reflected in USDC balance.
        Always syncs both up and down so the bot has an accurate view of
        spendable funds (handles resolved-position payouts, deposits, fees, etc.)
        """
        prev = self.bankroll
        diff = actual_usdc_balance - prev
        if abs(diff) <= 0.001:
            return
        self.bankroll = actual_usdc_balance
        if diff > 0:
            log.info(
                f"Balance sync (upward): ${prev:.2f} -> ${self.bankroll:.2f} "
                f"(+${diff:.2f}, {len(self.positions)} positions open)"
            )
        else:
            log.warning(
                f"Balance sync (downward): ${prev:.2f} -> ${self.bankroll:.2f} "
                f"(${diff:.2f}, {len(self.positions)} positions open)"
            )
        self.high_water_mark = max(self.high_water_mark, self.bankroll + self.total_exposure())

    # ── Cost tracking ─────────────────────────────────────────────────

    def record_api_cost(self, input_tokens: int, output_tokens: int) -> None:
        """Deduct inference cost from bankroll. The agent pays for its own thinking."""
        cost = (input_tokens * INPUT_COST_PER_MTOK / 1_000_000) + \
               (output_tokens * OUTPUT_COST_PER_MTOK / 1_000_000)
        self.total_api_cost += cost
        self.bankroll -= cost

    def remove_ghost_position(self, condition_id: str) -> None:
        """Remove a phantom position that has no actual on-chain tokens.
        Does NOT adjust bankroll — on-chain balance sync already reflects real USDC.
        Records the cost basis as a realized loss.
        """
        pos = next((p for p in self.positions if p.condition_id == condition_id), None)
        if pos is None:
            return
        pnl = -pos.size_usd
        self.total_realized_pnl += pnl
        self._recently_closed[condition_id] = time.time()
        self.positions = [p for p in self.positions if p.condition_id != condition_id]
        log.warning(
            f"Ghost removed: {pos.question[:40]}... "
            f"(${pos.size_usd:.2f} cost written off, PnL={pnl:+.2f})"
        )

    def reset_daily(self) -> None:
        """Reset daily tracking. Call at the start of each new trading day."""
        self.daily_start_value = self.bankroll
