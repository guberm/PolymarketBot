"""Trade execution — paper trading and live Polymarket CLOB orders."""

import logging
import time
from typing import Optional
from uuid import uuid4

from config import BotConfig
from models import Signal, Trade, Position, Side, TradeAction, ExitSignal, TopupCandidate
from portfolio import Portfolio
from runtime_safety import OrderFill, OrderJournal, TradingBlockedError, parse_order_fill

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
            event_title=market.event_title,
            fair_estimate_at_entry=signal.estimate.fair_probability,
            liquidation_limit_price=signal.limit_price,
            quote_age_seconds=signal.quote_age_seconds,
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
            quoted_vwap=signal.execution_price,
            fill_status="MATCHED",
        )

    def execute_sell(self, exit_signal: ExitSignal, portfolio: Portfolio) -> Optional[Trade]:
        pos = exit_signal.position
        if not pos.book_depth_complete or pos.liquidation_limit_price <= 0:
            log.warning(f"SKIP PAPER SELL (insufficient bid depth): {pos.question[:40]}")
            return None
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
            quoted_vwap=exit_signal.current_price,
            fill_status="MATCHED",
        )

    def execute_topup_and_sell(self, candidate: TopupCandidate, portfolio: Portfolio) -> Optional[Trade]:
        pos = candidate.position
        price = candidate.sell_vwap

        # Step 1: simulate BUY 5 tokens
        buy_shares = candidate.tokens_to_buy
        buy_cost = candidate.topup_cost
        portfolio.add_to_position(pos.condition_id, buy_shares, buy_cost)
        pos.book_depth_complete = True
        pos.liquidation_limit_price = candidate.sell_limit_price

        # Step 2: simulate SELL all tokens
        exit_signal = ExitSignal(pos, candidate.exit_reason, price,
                                 pos.shares * (price - pos.entry_price),
                                 (price - pos.entry_price) / pos.entry_price if pos.entry_price > 0 else 0.0)
        return self.execute_sell(exit_signal, portfolio)


class LiveTrader:
    """Real execution via the Polymarket CLOB V2 API."""

    def __init__(self, config: BotConfig):
        try:
            from py_clob_client_v2 import ApiCreds, ClobClient
        except ImportError:
            raise RuntimeError(
                "Live trading requires py-clob-client-v2>=1.1.0: pip install py-clob-client-v2"
            )

        creds = None
        if config.polymarket_api_key and config.polymarket_api_secret:
            creds = ApiCreds(
                api_key=config.polymarket_api_key,
                api_secret=config.polymarket_api_secret,
                api_passphrase=config.polymarket_api_passphrase,
            )

        self.client = ClobClient(
            host=config.clob_host,
            key=config.polymarket_private_key or None,
            chain_id=config.polymarket_chain_id,
            creds=creds,
            signature_type=config.polymarket_signature_type,
            funder=config.polymarket_funder_address or None,
        )

        if creds is None:
            self.client.set_api_creds(self.client.create_or_derive_api_key())
        self.journal = OrderJournal(config.data_dir)
        log.info("Live CLOB client initialized")

    def _handle_order_post_failure(self, intent_id: str, exc: Exception, action: str) -> None:
        status = getattr(exc, "status_code", None)
        if isinstance(status, int) and 400 <= status < 500:
            self.journal.complete(intent_id)
            if status == 403:
                raise TradingBlockedError(f"CLOB rejected {action} with HTTP 403: {exc}") from exc
            log.error(f"CLOB rejected {action} with HTTP {status}: {exc}")
            return
        log.error(f"CLOB {action} outcome unknown; intent retained for recovery: {exc}")

    def confirm_applied_orders(self, portfolio: Portfolio) -> None:
        for record in self.journal.pending():
            if portfolio.has_applied_order(record.get("order_id", "")):
                self.journal.complete(record["intent_id"])

    def recover_pending_orders(self, portfolio: Portfolio) -> bool:
        """Cancel/reconcile durable intents and apply fills exactly once before live trading resumes."""
        from persistence import append_trade, save_snapshot

        for record in self.journal.pending():
            intent_id = record["intent_id"]
            order_id = record.get("order_id", "")
            if order_id and portfolio.has_applied_order(order_id):
                self.journal.complete(intent_id)
                continue
            if not order_id:
                log.critical(
                    f"Uncertain live order intent {intent_id}: POST outcome unknown; "
                    "refusing live trading until pending-orders.json is reconciled manually"
                )
                return False

            fill = None
            if record.get("fill_shares", 0) > 0:
                fill = OrderFill(
                    record.get("fill_status", "PARTIAL"), record["fill_shares"],
                    record.get("fill_value", 0), record.get("fill_price", record.get("limit_price", 0)),
                )
            else:
                try:
                    info = self.client.get_order(order_id)
                    if isinstance(info, dict):
                        fill = self._reconcile_order(
                            order_id, info, "BUY" if "BUY" in record.get("kind", "") else "SELL",
                            record.get("limit_price", 0), record.get("requested", 0), attempts=0,
                            delay_seconds=0,
                        )
                except Exception as exc:
                    log.error(f"Cannot reconcile pending order {order_id}: {exc}")
                    return False
                if fill is not None:
                    self.journal.filled(intent_id, fill)

            if fill is None:
                self.journal.complete(intent_id)
                continue
            trade = self._apply_recovered_order(record, fill, portfolio)
            if trade is None:
                return False
            save_snapshot(portfolio.snapshot(), portfolio.config.data_dir)
            append_trade(trade, portfolio.config.data_dir)
            self.journal.complete(intent_id)
            log.warning(f"Recovered pending {record.get('kind')} order {order_id} ({fill.shares:.4f} shares)")
        return True

    def _apply_recovered_order(self, record: dict, fill: OrderFill, portfolio: Portfolio) -> Optional[Trade]:
        order_id = record["order_id"]
        kind = record.get("kind", "")
        side = Side(record.get("side", "YES"))
        if kind == "BUY":
            if portfolio.has_position(record["condition_id"]):
                log.critical(f"Pending BUY {order_id} conflicts with an existing position")
                return None
            portfolio.open_position(Position(
                condition_id=record["condition_id"], question=record.get("question", "Recovered order"),
                side=side, token_id=record.get("token_id", ""), entry_price=fill.price,
                size_usd=fill.value, shares=fill.shares, current_price=fill.price, unrealized_pnl=0,
                category=record.get("category", "other"), event_title=record.get("event_title", ""),
                order_id=order_id,
                fair_estimate_at_entry=record.get("fair_estimate", 0),
                liquidation_limit_price=record.get("limit_price", 0),
            ))
        elif kind == "TOPUP_BUY":
            if not portfolio.has_position(record["condition_id"]):
                log.critical(f"Pending TOPUP BUY {order_id} has no position to update")
                return None
            portfolio.add_to_position(record["condition_id"], fill.shares, fill.value)
        elif kind in ("SELL", "TOPUP_SELL"):
            if not portfolio.has_position(record["condition_id"]):
                log.critical(f"Pending SELL {order_id} has no position to reduce")
                return None
            portfolio.reduce_position(record["condition_id"], fill.shares, fill.price)
        else:
            log.critical(f"Unknown pending order kind: {kind}")
            return None

        portfolio.mark_order_applied(order_id)
        return Trade(
            trade_id=str(uuid4()), condition_id=record["condition_id"],
            question=record.get("question", "Recovered order"), side=side,
            action=TradeAction.BUY if "BUY" in kind else TradeAction.SELL,
            price=fill.price, size_usd=fill.value, shares=fill.shares, timestamp=time.time(),
            order_id=order_id, is_paper=False, rationale=f"Recovered after restart: {kind}",
            exit_reason=record.get("exit_reason", ""), quoted_vwap=record.get("quoted_vwap", 0),
            fill_status=fill.status,
        )

    def get_balance(self) -> Optional[float]:
        """Fetch actual USDC balance from CLOB API. Returns balance in USD."""
        try:
            from py_clob_client_v2 import AssetType, BalanceAllowanceParams
            params = BalanceAllowanceParams(asset_type=AssetType.COLLATERAL)
            resp = self.client.get_balance_allowance(params)
            balance_raw = float(resp.get("balance", 0))
            # py-clob-client returns balance in atomic USDC units (6 decimals)
            return balance_raw / 1_000_000.0
        except Exception as e:
            log.warning(f"Balance check failed: {e}")
            return None

    def _reconcile_order(
        self, order_id: str, initial: dict, side: str, limit_price: float,
        requested: float, attempts: int = 5, delay_seconds: int = 3,
    ) -> Optional[OrderFill]:
        best = parse_order_fill(initial, side, limit_price)
        matched = best.status == "MATCHED"
        if matched and best.shares <= 0 and limit_price > 0:
            best = (OrderFill("MATCHED", requested / limit_price, requested, limit_price)
                    if side == "BUY" else OrderFill("MATCHED", requested, requested * limit_price, limit_price))
        for attempt in range(0 if matched else attempts):
            time.sleep(delay_seconds)
            try:
                info = self.client.get_order(order_id)
                if not isinstance(info, dict):
                    continue
                fill = parse_order_fill(info, side, limit_price)
                if fill.shares >= best.shares:
                    best = fill
                log.info(f"{side} order poll {attempt + 1}: status={fill.status}, filled={fill.shares:.4f}")
                if fill.status == "MATCHED":
                    matched = True
                    break
                if fill.status in ("CANCELLED", "DELAYED"):
                    break
            except Exception as exc:
                log.warning(f"Order status check failed: {exc}")
                break

        if not matched:
            try:
                from py_clob_client_v2 import OrderPayload
                self.client.cancel_order(OrderPayload(orderID=order_id))
            except Exception as exc:
                log.warning(f"Cancel failed: {exc}")
            try:
                final = self.client.get_order(order_id)
                if isinstance(final, dict):
                    fill = parse_order_fill(final, side, limit_price)
                    if fill.shares >= best.shares:
                        best = fill
            except Exception as exc:
                log.warning(f"Final order reconciliation failed: {exc}")

        if best.shares <= 1e-9:
            return None
        if best.status != "MATCHED":
            best = OrderFill("PARTIAL", best.shares, best.value, best.price)
        return best

    def execute(self, signal: Signal, portfolio: Portfolio) -> Optional[Trade]:
        from py_clob_client_v2 import OrderArgs, OrderType, Side as ClobSide

        market = signal.market
        # The pre-trade order-book quote already selected the worst acceptable level.
        price = signal.limit_price or signal.execution_price
        size_usd = signal.position_size_usd
        token_id = market.token_id_yes if signal.side == Side.YES else market.token_id_no

        try:
            order_args = OrderArgs(
                token_id=token_id,
                price=price,
                size=size_usd / price,
                side=ClobSide.BUY,
            )
            signed_order = self.client.create_order(order_args)
        except Exception as e:
            log.error(f"CLOB order creation failed: {e}")
            return None

        intent_id = self.journal.begin({
            "kind": "BUY", "condition_id": market.condition_id, "question": market.question,
            "side": signal.side.value, "token_id": token_id, "requested": size_usd,
            "limit_price": price, "category": market.category, "event_title": market.event_title,
            "fair_estimate": signal.estimate.fair_probability, "edge": signal.edge,
            "kelly": signal.kelly_fraction, "quoted_vwap": signal.execution_price,
        })
        try:
            resp = self.client.post_order(signed_order, OrderType.GTC)

            order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            self.journal.submitted(intent_id, order_id)
            log.info(f"CLOB GTC order submitted: {order_id}")

        except Exception as e:
            self._handle_order_post_failure(intent_id, e, "BUY")
            return None

        fill = self._reconcile_order(order_id, resp, "BUY", price, size_usd)
        if fill is None:
            self.journal.complete(intent_id)
            return None
        self.journal.filled(intent_id, fill)

        actual_cost, actual_shares, actual_price = fill.value, fill.shares, fill.price
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
            event_title=market.event_title,
            order_id=order_id,
            fair_estimate_at_entry=signal.estimate.fair_probability,
            liquidation_limit_price=signal.limit_price,
            quote_age_seconds=signal.quote_age_seconds,
        )
        portfolio.open_position(position)
        portfolio.mark_order_applied(order_id)

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
            quoted_vwap=signal.execution_price,
            slippage_bps=(actual_price - signal.execution_price) / signal.execution_price * 10_000 if signal.execution_price else 0.0,
            fill_status=fill.status,
        )

    def _get_actual_conditional_balance(self, token_id: str) -> Optional[float]:
        """Refresh CLOB cache and return actual on-chain conditional token balance."""
        from py_clob_client_v2 import AssetType, BalanceAllowanceParams
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
        from py_clob_client_v2 import OrderArgs, OrderType, Side as ClobSide

        pos = exit_signal.position
        if not pos.book_depth_complete or pos.liquidation_limit_price <= 0:
            log.warning(f"SKIP SELL (insufficient bid depth): {pos.question[:40]}")
            return None
        price = pos.liquidation_limit_price

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
                price=price,
                size=sell_shares,
                side=ClobSide.SELL,
            )
            signed_order = self.client.create_order(order_args)
        except Exception as e:
            log.error(f"CLOB SELL order creation failed: {e}")
            return None

        intent_id = self.journal.begin({
            "kind": "SELL", "condition_id": pos.condition_id, "question": pos.question,
            "side": pos.side.value, "token_id": pos.token_id, "requested": sell_shares,
            "limit_price": price, "category": pos.category,
            "quoted_vwap": exit_signal.current_price, "exit_reason": exit_signal.exit_reason,
        })
        try:
            resp = self.client.post_order(signed_order, OrderType.GTC)
            order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            self.journal.submitted(intent_id, order_id)
            log.info(f"CLOB SELL GTC order submitted: {order_id}")
        except Exception as e:
            self._handle_order_post_failure(intent_id, e, "SELL")
            return None

        fill = self._reconcile_order(order_id, resp, "SELL", price, sell_shares, attempts=3, delay_seconds=2)
        if fill is None:
            self.journal.complete(intent_id)
            return None
        self.journal.filled(intent_id, fill)

        pnl = portfolio.reduce_position(pos.condition_id, fill.shares, fill.price)
        portfolio.mark_order_applied(order_id)
        log.info(f"SOLD: {pos.question[:40]}... PnL=${pnl:+.2f} ({exit_signal.exit_reason})")

        return Trade(
            trade_id=str(uuid4()),
            condition_id=pos.condition_id,
            question=pos.question,
            side=pos.side,
            action=TradeAction.SELL,
            price=fill.price,
            size_usd=fill.value,
            shares=fill.shares,
            timestamp=time.time(),
            order_id=order_id,
            is_paper=False,
            rationale=f"Exit: {exit_signal.exit_reason}",
            exit_reason=exit_signal.exit_reason,
            quoted_vwap=exit_signal.current_price,
            slippage_bps=(exit_signal.current_price - fill.price) / exit_signal.current_price * 10_000 if exit_signal.current_price else 0.0,
            fill_status=fill.status,
        )

    def execute_topup_and_sell(self, candidate: TopupCandidate, portfolio: Portfolio) -> Optional[Trade]:
        """Buy 5 tokens to reach CLOB minimum, then sell all tokens to exit stuck position."""
        from py_clob_client_v2 import OrderArgs, OrderType, Side as ClobSide

        pos = candidate.position
        buy_price = candidate.buy_limit_price
        sell_price = candidate.sell_limit_price

        # Step 1: BUY 5 tokens to top up position
        buy_usd = candidate.topup_cost
        log.info(f"TOPUP BUY: {pos.question[:40]}... 5 tokens @ limit {buy_price:.4f} (${buy_usd:.2f})")

        try:
            buy_args = OrderArgs(
                token_id=pos.token_id,
                price=buy_price,
                size=candidate.tokens_to_buy,
                side=ClobSide.BUY,
            )
            signed_order = self.client.create_order(buy_args)
        except Exception as e:
            log.error(f"TOPUP BUY order creation failed: {e}")
            return None

        buy_intent_id = self.journal.begin({
            "kind": "TOPUP_BUY", "condition_id": pos.condition_id, "question": pos.question,
            "side": pos.side.value, "token_id": pos.token_id, "requested": buy_usd,
            "limit_price": buy_price, "category": pos.category, "quoted_vwap": candidate.buy_vwap,
        })
        try:
            resp = self.client.post_order(signed_order, OrderType.GTC)
            buy_order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            self.journal.submitted(buy_intent_id, buy_order_id)
            log.info(f"TOPUP BUY GTC order submitted: {buy_order_id}")
        except Exception as e:
            self._handle_order_post_failure(buy_intent_id, e, "TOPUP BUY")
            return None

        buy_fill = self._reconcile_order(buy_order_id, resp, "BUY", buy_price, buy_usd, attempts=3, delay_seconds=2)
        if buy_fill is None:
            self.journal.complete(buy_intent_id)
            return None
        self.journal.filled(buy_intent_id, buy_fill)

        # BUY filled — update position in portfolio
        portfolio.add_to_position(pos.condition_id, buy_fill.shares, buy_fill.value)
        portfolio.mark_order_applied(buy_order_id)

        # Step 2: SELL all tokens (now >= 5). Use actual on-chain balance to avoid
        # "not enough balance" if earlier BUY was also partially filled.
        actual_balance = self._get_actual_conditional_balance(pos.token_id)
        total_shares = pos.shares  # already updated by add_to_position
        if actual_balance is not None and actual_balance < total_shares:
            log.warning(f"Partial-fill detected in topup: portfolio={total_shares:.2f}, on-chain={actual_balance:.2f}; selling actual")
            total_shares = actual_balance
        if total_shares < 5.0:
            log.warning(f"TOPUP partial fill left only {total_shares:.2f} tokens; cannot SELL yet")
            return None
        log.info(f"TOPUP SELL: {total_shares:.2f} tokens @ limit {sell_price:.4f}")

        try:
            sell_args = OrderArgs(
                token_id=pos.token_id,
                price=sell_price,
                size=total_shares,
                side=ClobSide.SELL,
            )
            signed_order = self.client.create_order(sell_args)
        except Exception as e:
            log.error(f"TOPUP SELL order creation failed (position now has {total_shares:.2f} tokens): {e}")
            return None

        sell_intent_id = self.journal.begin({
            "kind": "TOPUP_SELL", "condition_id": pos.condition_id, "question": pos.question,
            "side": pos.side.value, "token_id": pos.token_id, "requested": total_shares,
            "limit_price": sell_price, "category": pos.category,
            "quoted_vwap": candidate.sell_vwap, "exit_reason": candidate.exit_reason,
        })
        try:
            resp = self.client.post_order(signed_order, OrderType.GTC)
            sell_order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            self.journal.submitted(sell_intent_id, sell_order_id)
            log.info(f"TOPUP SELL GTC order submitted: {sell_order_id}")
        except Exception as e:
            self._handle_order_post_failure(sell_intent_id, e, "TOPUP SELL")
            return None

        sell_fill = self._reconcile_order(sell_order_id, resp, "SELL", sell_price, total_shares, attempts=3, delay_seconds=2)
        if sell_fill is None:
            self.journal.complete(sell_intent_id)
            return None
        self.journal.filled(sell_intent_id, sell_fill)

        pnl = portfolio.reduce_position(pos.condition_id, sell_fill.shares, sell_fill.price)
        portfolio.mark_order_applied(sell_order_id)
        log.info(f"TOPUP+SELL complete: {pos.question[:40]}... PnL=${pnl:+.2f} ({candidate.exit_reason})")

        return Trade(
            trade_id=str(uuid4()),
            condition_id=pos.condition_id,
            question=pos.question,
            side=pos.side,
            action=TradeAction.SELL,
            price=sell_fill.price,
            size_usd=sell_fill.value,
            shares=sell_fill.shares,
            timestamp=time.time(),
            order_id=sell_order_id,
            is_paper=False,
            rationale=f"Topup+Exit: {candidate.exit_reason}",
            exit_reason=candidate.exit_reason,
            quoted_vwap=candidate.sell_vwap,
            slippage_bps=(candidate.sell_vwap - sell_fill.price) / candidate.sell_vwap * 10_000 if candidate.sell_vwap else 0.0,
            fill_status=sell_fill.status,
        )
