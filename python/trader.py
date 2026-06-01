"""Trade execution — paper trading and live Polymarket CLOB orders."""

import logging
import time
from typing import Optional
from uuid import uuid4

from config import BotConfig
from models import Signal, Trade, Position, Side, TradeAction, ExitSignal, TopupCandidate
from portfolio import Portfolio

log = logging.getLogger("bot.trader")


class PaperTrader:
    """Simulated execution — deducts from bankroll, tracks positions, no real orders."""

    def execute(self, signal: Signal, portfolio: Portfolio) -> Optional[Trade]:
        market = signal.market
        price = signal.execution_price
        size_usd = signal.position_size_usd
        shares = size_usd / price if price > 0 else 0.0
        token_id = market.token_id_yes if signal.side == Side.YES else market.token_id_no

        position = Position(
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            token_id=token_id,
            entry_price=price,
            size_usd=size_usd,
            shares=shares,
            current_price=price,
            unrealized_pnl=0.0,
            category=market.category,
            fair_estimate_at_entry=signal.estimate.fair_probability,
        )
        portfolio.open_position(position)

        return Trade(
            trade_id=str(uuid4()),
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            action=TradeAction.BUY,
            price=price,
            size_usd=size_usd,
            shares=shares,
            timestamp=time.time(),
            is_paper=True,
            rationale=signal.estimate.reasoning_summary,
            edge_at_entry=signal.edge,
            kelly_at_entry=signal.kelly_fraction,
        )

    def execute_sell(self, exit_signal: ExitSignal, portfolio: Portfolio) -> Optional[Trade]:
        pos = exit_signal.position
        pnl = portfolio.close_position(pos.condition_id, exit_signal.current_price)

        return Trade(
            trade_id=str(uuid4()),
            condition_id=pos.condition_id,
            question=pos.question,
            side=pos.side,
            action=TradeAction.SELL,
            price=exit_signal.current_price,
            size_usd=pos.size_usd,
            shares=pos.shares,
            timestamp=time.time(),
            is_paper=True,
            rationale=f"Exit: {exit_signal.exit_reason}",
            exit_reason=exit_signal.exit_reason,
        )

    def execute_topup_and_sell(self, candidate: TopupCandidate, portfolio: Portfolio) -> Optional[Trade]:
        pos = candidate.position
        price = pos.current_price

        # Step 1: simulate BUY 5 tokens
        buy_shares = candidate.tokens_to_buy
        buy_cost = candidate.topup_cost
        portfolio.add_to_position(pos.condition_id, buy_shares, buy_cost)

        # Step 2: simulate SELL all tokens
        exit_signal = ExitSignal(pos, candidate.exit_reason, price,
                                 pos.shares * (price - pos.entry_price),
                                 (price - pos.entry_price) / pos.entry_price if pos.entry_price > 0 else 0.0)
        return self.execute_sell(exit_signal, portfolio)


class LiveTrader:
    """Real execution via Polymarket CLOB API using py-clob-client."""

    def __init__(self, config: BotConfig):
        try:
            from py_clob_client.client import ClobClient
        except ImportError:
            raise RuntimeError(
                "Live trading requires py-clob-client: pip install py-clob-client"
            )

        self.client = ClobClient(
            config.clob_host,
            key=config.polymarket_private_key or None,
            chain_id=config.polymarket_chain_id,
            signature_type=config.polymarket_signature_type,
            funder=config.polymarket_funder_address or None,
        )

        # Use pre-generated CLOB API credentials if provided, otherwise derive
        if config.polymarket_api_key and config.polymarket_api_secret:
            from py_clob_client.clob_types import ApiCreds
            self.client.set_api_creds(ApiCreds(
                api_key=config.polymarket_api_key,
                api_secret=config.polymarket_api_secret,
                api_passphrase=config.polymarket_api_passphrase,
            ))
        else:
            self.client.set_api_creds(self.client.create_or_derive_api_creds())
        log.info("Live CLOB client initialized")

    def get_balance(self) -> Optional[float]:
        """Fetch actual USDC balance from CLOB API. Returns balance in USD."""
        try:
            from py_clob_client.clob_types import BalanceAllowanceParams, AssetType
            params = BalanceAllowanceParams(asset_type=AssetType.COLLATERAL)
            resp = self.client.get_balance_allowance(params)
            balance_raw = float(resp.get("balance", 0))
            # py-clob-client returns balance in atomic USDC units (6 decimals)
            return balance_raw / 1_000_000.0
        except Exception as e:
            log.warning(f"Balance check failed: {e}")
            return None

    def execute(self, signal: Signal, portfolio: Portfolio) -> Optional[Trade]:
        from py_clob_client.clob_types import OrderArgs, OrderType
        from py_clob_client.order_builder.constants import BUY

        market = signal.market
        # Add configured aggression so the buy order crosses the spread
        # and fills immediately as a taker. Edge >>8% covers this tiny cost.
        price = min(round(signal.market_price + self.config.entry_price_buffer, 2), 0.99)
        size_usd = signal.position_size_usd
        token_id = market.token_id_yes if signal.side == Side.YES else market.token_id_no

        try:
            order_args = OrderArgs(
                token_id=token_id,
                amount=size_usd,
                price=price,
                side=BUY,
            )
            signed_order = self.client.create_order(order_args)
            resp = self.client.post_order(signed_order, OrderType.GTC)

            order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            # Parse actual fill amounts (may differ from requested due to price improvement)
            actual_cost = size_usd  # fallback
            actual_shares = size_usd / price if price > 0 else 0.0
            try:
                making = float(resp.get("makingAmount", 0))
                taking = float(resp.get("takingAmount", 0))
                if making > 0:
                    actual_cost = making
                if taking > 0:
                    actual_shares = taking
            except (ValueError, TypeError):
                pass
            log.info(f"CLOB GTC order submitted: {order_id}")

        except Exception as e:
            log.error(f"CLOB order failed: {e}")
            return None

        # GTC orders — poll for fill. With +2-tick aggression the order should
        # cross the spread and fill as a taker within a few seconds.
        matched = False
        for attempt in range(5):
            time.sleep(3)
            try:
                order_info = self.client.get_order(order_id)
                status = order_info.get("status") if isinstance(order_info, dict) else None
                log.info(f"GTC order poll {attempt+1}: status={status}")
                if status == "MATCHED":
                    matched = True
                    break
                if status in ("CANCELLED", "DELAYED"):
                    break
            except Exception as e:
                log.warning(f"Order status check failed: {e}")
                break

        if not matched:
            log.warning(f"GTC order not filled after 15s, cancelling: {order_id}")
            try:
                self.client.cancel(order_id)
            except Exception as e:
                log.warning(f"Cancel failed: {e}")
            return None

        # Use actual fill amounts from CLOB
        actual_price = actual_cost / actual_shares if actual_shares > 0 else price
        log.info(f"Fill: requested ${size_usd:.2f}, actual ${actual_cost:.2f} ({actual_shares:.2f} shares @ {actual_price:.4f})")

        position = Position(
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            token_id=token_id,
            entry_price=actual_price,
            size_usd=actual_cost,
            shares=actual_shares,
            current_price=actual_price,
            unrealized_pnl=0.0,
            category=market.category,
            order_id=order_id,
            fair_estimate_at_entry=signal.estimate.fair_probability,
        )
        portfolio.open_position(position)

        return Trade(
            trade_id=str(uuid4()),
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            action=TradeAction.BUY,
            price=actual_price,
            size_usd=actual_cost,
            shares=actual_shares,
            timestamp=time.time(),
            order_id=order_id,
            is_paper=False,
            rationale=signal.estimate.reasoning_summary,
            edge_at_entry=signal.edge,
            kelly_at_entry=signal.kelly_fraction,
        )

    def _get_actual_conditional_balance(self, token_id: str) -> Optional[float]:
        """Refresh CLOB cache and return actual on-chain conditional token balance."""
        from py_clob_client.clob_types import BalanceAllowanceParams, AssetType
        try:
            # Refresh CLOB's cached view of on-chain state
            self.client.update_balance_allowance(
                BalanceAllowanceParams(asset_type=AssetType.CONDITIONAL, token_id=token_id, signature_type=-1)
            )
        except Exception as e:
            log.debug(f"Conditional allowance update: {e}")
        try:
            resp = self.client.get_balance_allowance(
                BalanceAllowanceParams(asset_type=AssetType.CONDITIONAL, token_id=token_id, signature_type=-1)
            )
            balance_raw = float(resp.get("balance", 0))
            balance = balance_raw / 1_000_000.0
            log.info(f"[COND-BALANCE] {token_id[:12]}: {balance:.2f} tokens on-chain")
            return balance
        except Exception as e:
            log.warning(f"Conditional balance check failed: {e}")
            return None

    def verify_positions(self, positions: list) -> list[str]:
        """Check tracked positions against actual CLOB token balances.
        Returns list of condition_ids with zero on-chain balance (ghost positions).
        A position is considered a ghost if on-chain balance < 0.1 tokens.
        """
        ghosts = []
        for pos in positions:
            balance = self._get_actual_conditional_balance(pos.token_id)
            if balance is not None and balance < 0.1:
                log.warning(
                    f"Ghost detected: {pos.question[:50]}... "
                    f"(tracked={pos.shares:.2f} tokens, on-chain={balance:.2f})"
                )
                ghosts.append(pos.condition_id)
        return ghosts

    def execute_sell(self, exit_signal: ExitSignal, portfolio: Portfolio) -> Optional[Trade]:
        from py_clob_client.clob_types import OrderArgs, OrderType
        from py_clob_client.order_builder.constants import SELL

        pos = exit_signal.position
        price = exit_signal.current_price

        # Get actual on-chain balance — GTC BUY orders can partially fill.
        # Portfolio may record more shares than actually settled on-chain.
        sell_shares = pos.shares
        actual_balance = self._get_actual_conditional_balance(pos.token_id)
        if actual_balance is not None and actual_balance < sell_shares:
            log.warning(f"Partial-fill detected: portfolio={sell_shares:.2f} tokens, on-chain={actual_balance:.2f}; selling actual amount")
            sell_shares = actual_balance

        if price < 0.01:
            log.warning(f"SKIP SELL (price {price:.4f} too low for CLOB): {pos.question[:40]}")
            return None

        if sell_shares < 5.0:
            log.warning(f"SKIP SELL (below CLOB minimum 5 tokens): {pos.question[:40]} {sell_shares:.2f} shares")
            return None

        try:
            order_args = OrderArgs(
                token_id=pos.token_id,
                amount=sell_shares,  # SELL amount is in tokens
                price=price,
                side=SELL,
            )
            signed_order = self.client.create_order(order_args)
            resp = self.client.post_order(signed_order, OrderType.GTC)
            order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            log.info(f"CLOB SELL GTC order submitted: {order_id}")
        except Exception as e:
            log.error(f"CLOB SELL order failed: {e}")
            return None

        # Poll for fill (same pattern as BUY)
        matched = False
        for attempt in range(3):
            time.sleep(2)
            try:
                order_info = self.client.get_order(order_id)
                status = order_info.get("status") if isinstance(order_info, dict) else None
                log.info(f"SELL GTC poll {attempt+1}: status={status}")
                if status == "MATCHED":
                    matched = True
                    break
                if status in ("CANCELLED", "DELAYED"):
                    break
            except Exception as e:
                log.warning(f"Sell order status check failed: {e}")
                break

        if not matched:
            log.warning(f"SELL order not filled after 15s, cancelling: {order_id}")
            try:
                self.client.cancel(order_id)
            except Exception:
                pass
            return None

        # Close position in portfolio (returns capital + PnL to bankroll)
        pnl = portfolio.close_position(pos.condition_id, price)
        log.info(f"SOLD: {pos.question[:40]}... PnL=${pnl:+.2f} ({exit_signal.exit_reason})")

        return Trade(
            trade_id=str(uuid4()),
            condition_id=pos.condition_id,
            question=pos.question,
            side=pos.side,
            action=TradeAction.SELL,
            price=price,
            size_usd=pos.size_usd,
            shares=sell_shares,
            timestamp=time.time(),
            order_id=order_id,
            is_paper=False,
            rationale=f"Exit: {exit_signal.exit_reason}",
            exit_reason=exit_signal.exit_reason,
        )

    def execute_topup_and_sell(self, candidate: TopupCandidate, portfolio: Portfolio) -> Optional[Trade]:
        """Buy 5 tokens to reach CLOB minimum, then sell all tokens to exit stuck position."""
        from py_clob_client.clob_types import OrderArgs, OrderType
        from py_clob_client.order_builder.constants import BUY, SELL

        pos = candidate.position
        price = pos.current_price

        # Step 1: BUY 5 tokens to top up position
        buy_usd = candidate.topup_cost
        log.info(f"TOPUP BUY: {pos.question[:40]}... 5 tokens @ {price:.4f} (${buy_usd:.2f})")

        try:
            buy_args = OrderArgs(
                token_id=pos.token_id,
                amount=buy_usd,
                price=price,
                side=BUY,
            )
            signed_order = self.client.create_order(buy_args)
            resp = self.client.post_order(signed_order, OrderType.GTC)
            buy_order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            log.info(f"TOPUP BUY GTC order submitted: {buy_order_id}")
        except Exception as e:
            log.error(f"TOPUP BUY order failed: {e}")
            return None

        # Poll for BUY fill
        buy_matched = False
        for attempt in range(3):
            time.sleep(2)
            try:
                order_info = self.client.get_order(buy_order_id)
                status = order_info.get("status") if isinstance(order_info, dict) else None
                log.info(f"TOPUP BUY poll {attempt+1}: status={status}")
                if status == "MATCHED":
                    buy_matched = True
                    break
                if status in ("CANCELLED", "DELAYED"):
                    break
            except Exception as e:
                log.warning(f"TOPUP BUY status check failed: {e}")
                break

        if not buy_matched:
            log.warning(f"TOPUP BUY not filled after 15s, cancelling: {buy_order_id}")
            try:
                self.client.cancel(buy_order_id)
            except Exception:
                pass
            return None

        # BUY filled — update position in portfolio
        portfolio.add_to_position(pos.condition_id, 5.0, buy_usd)

        # Step 2: SELL all tokens (now >= 5). Use actual on-chain balance to avoid
        # "not enough balance" if earlier BUY was also partially filled.
        actual_balance = self._get_actual_conditional_balance(pos.token_id)
        total_shares = pos.shares  # already updated by add_to_position
        if actual_balance is not None and actual_balance < total_shares:
            log.warning(f"Partial-fill detected in topup: portfolio={total_shares:.2f}, on-chain={actual_balance:.2f}; selling actual")
            total_shares = actual_balance
        log.info(f"TOPUP SELL: {total_shares:.2f} tokens @ {price:.4f}")

        try:
            sell_args = OrderArgs(
                token_id=pos.token_id,
                amount=total_shares,
                price=price,
                side=SELL,
            )
            signed_order = self.client.create_order(sell_args)
            resp = self.client.post_order(signed_order, OrderType.GTC)
            sell_order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            log.info(f"TOPUP SELL GTC order submitted: {sell_order_id}")
        except Exception as e:
            log.error(f"TOPUP SELL order failed (position now has {total_shares:.2f} tokens): {e}")
            return None

        # Poll for SELL fill
        sell_matched = False
        for attempt in range(3):
            time.sleep(2)
            try:
                order_info = self.client.get_order(sell_order_id)
                status = order_info.get("status") if isinstance(order_info, dict) else None
                log.info(f"TOPUP SELL poll {attempt+1}: status={status}")
                if status == "MATCHED":
                    sell_matched = True
                    break
                if status in ("CANCELLED", "DELAYED"):
                    break
            except Exception as e:
                log.warning(f"TOPUP SELL status check failed: {e}")
                break

        if not sell_matched:
            log.warning(
                f"TOPUP SELL not filled after 15s, cancelling: {sell_order_id} "
                f"(position now sellable with {total_shares:.2f} tokens)"
            )
            try:
                self.client.cancel(sell_order_id)
            except Exception:
                pass
            return None

        # Both orders filled — close position
        pnl = portfolio.close_position(pos.condition_id, price)
        log.info(f"TOPUP+SELL complete: {pos.question[:40]}... PnL=${pnl:+.2f} ({candidate.exit_reason})")

        return Trade(
            trade_id=str(uuid4()),
            condition_id=pos.condition_id,
            question=pos.question,
            side=pos.side,
            action=TradeAction.SELL,
            price=price,
            size_usd=pos.size_usd,
            shares=total_shares,
            timestamp=time.time(),
            order_id=sell_order_id,
            is_paper=False,
            rationale=f"Topup+Exit: {candidate.exit_reason}",
            exit_reason=candidate.exit_reason,
        )
