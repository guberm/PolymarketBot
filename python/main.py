"""Main orchestration loop for the Polymarket trading bot.

Usage:
    python main.py              # paper trading (default)
    python main.py --verbose    # debug logging
    python main.py --console    # human-readable CLI prints for each step
    LIVE_TRADING=true python main.py   # real orders (requires funded wallet)
"""

import argparse
import atexit
import logging
import signal
import sys
import time
from datetime import datetime, timezone

from uuid import uuid4

from config import BotConfig
from logger_setup import setup_logging
from market_scanner import MarketScanner
from estimator import Estimator
from kalshi_shadow import KalshiShadow
from models import MarketInfo, Side, Trade, TradeAction
from notifier import Notifier
from portfolio import Portfolio
from trader import PaperTrader, LiveTrader
from persistence import (
    append_estimate_evaluation,
    append_estimate_resolution,
    append_trade,
    get_resolution_candidates,
    load_snapshot,
    save_snapshot,
    track_resolutions,
    update_resolution_watchlist,
)
from runtime_safety import InstanceLock, TradingBlockedError, check_geoblock

log = logging.getLogger("bot.main")

# ANSI color codes for console output
GREEN = "\033[1;32m"
RED = "\033[1;31m"
YELLOW = "\033[1;33m"
RESET = "\033[0m"

# Enable ANSI colors on Windows
if sys.platform == "win32":
    import ctypes
    kernel32 = ctypes.windll.kernel32
    kernel32.SetConsoleMode(kernel32.GetStdHandle(-11), 7)


def ts():
    """Current timestamp for console prints."""
    return datetime.now().strftime("%H:%M:%S")


def build_review_market(pos) -> MarketInfo:
    yes_price = pos.current_price if pos.side == Side.YES else 1.0 - pos.current_price
    no_price = pos.current_price if pos.side == Side.NO else 1.0 - pos.current_price
    yes_price = max(0.01, min(0.99, yes_price))
    no_price = max(0.01, min(0.99, no_price))
    return MarketInfo(
        condition_id=pos.condition_id,
        question=pos.question,
        slug="",
        outcome_yes_price=yes_price,
        outcome_no_price=no_price,
        token_id_yes=pos.token_id if pos.side == Side.YES else "",
        token_id_no=pos.token_id if pos.side == Side.NO else "",
        liquidity=0.0,
        volume=0.0,
        volume_24hr=0.0,
        best_bid=0.0,
        best_ask=0.0,
        spread=0.0,
        end_date="",
        category=pos.category,
        event_title=pos.event_title or pos.question,
        description="Position review re-estimate before stop-loss exit.",
    )


def calculate_net_edges(market: MarketInfo, fair_probability: float, entry_buffer: float) -> tuple[float, float]:
    yes_execution_price = min(market.outcome_yes_price + entry_buffer, 0.99)
    no_execution_price = min(market.outcome_no_price + entry_buffer, 0.99)
    return (
        fair_probability - yes_execution_price,
        (1.0 - fair_probability) - no_execution_price,
    )


def effective_kelly_fraction(config: BotConfig) -> float:
    return min(config.kelly_fraction, 0.50) if config.live_trading and not config.allow_unsafe_risk else config.kelly_fraction


def effective_max_position_pct(config: BotConfig) -> float:
    return min(config.max_position_pct, 0.15) if config.live_trading and not config.allow_unsafe_risk else config.max_position_pct


def effective_max_exposure_pct(config: BotConfig) -> float:
    return min(config.max_total_exposure_pct, 0.90) if config.live_trading and not config.allow_unsafe_risk else config.max_total_exposure_pct


def effective_daily_stop_loss_pct(config: BotConfig) -> float:
    return min(config.daily_stop_loss_pct, 0.25) if config.live_trading and not config.allow_unsafe_risk else config.daily_stop_loss_pct


def effective_max_drawdown_pct(config: BotConfig) -> float:
    return min(config.max_drawdown_pct, 0.60) if config.live_trading and not config.allow_unsafe_risk else config.max_drawdown_pct


def main():
    parser = argparse.ArgumentParser(description="Polymarket trading bot")
    parser.add_argument("--verbose", "-v", action="store_true", help="Debug logging")
    parser.add_argument("--console", "-c", action="store_true", help="Human-readable CLI prints for each step")
    parser.add_argument("--max-position-pct", type=float, help="Max %% of bankroll per position (e.g. 0.15)")
    parser.add_argument("--max-total-exposure-pct", type=float, help="Max %% of bankroll in open positions (e.g. 0.90)")
    parser.add_argument("--max-category-exposure-pct", type=float, help="Max %% per category (e.g. 0.50)")
    parser.add_argument("--max-event-exposure-pct", type=float, help="Max %% per event (e.g. 0.30)")
    parser.add_argument("--daily-stop-loss-pct", type=float, help="Halt if daily loss exceeds this %% (e.g. 0.20)")
    parser.add_argument("--max-drawdown-pct", type=float, help="Halt if drawdown exceeds this %% (e.g. 0.50)")
    parser.add_argument("--max-concurrent-positions", type=int, help="Max open positions (e.g. 20)")
    parser.add_argument("--once", action="store_true", help="Run exactly one cycle, then stop")
    args = parser.parse_args()

    config = BotConfig.from_env()
    con = args.console  # shorthand for console flag

    # CLI args override env vars
    if args.max_position_pct is not None:
        config.max_position_pct = args.max_position_pct
    if args.max_total_exposure_pct is not None:
        config.max_total_exposure_pct = args.max_total_exposure_pct
    if args.max_category_exposure_pct is not None:
        config.max_category_exposure_pct = args.max_category_exposure_pct
    if args.max_event_exposure_pct is not None:
        config.max_event_exposure_pct = args.max_event_exposure_pct
    if args.daily_stop_loss_pct is not None:
        config.daily_stop_loss_pct = args.daily_stop_loss_pct
    if args.max_drawdown_pct is not None:
        config.max_drawdown_pct = args.max_drawdown_pct
    if args.max_concurrent_positions is not None:
        config.max_concurrent_positions = args.max_concurrent_positions
    setup_logging(config.data_dir, verbose=args.verbose)
    instance_lock = InstanceLock(config.data_dir)
    if not instance_lock.acquire():
        log.error(f"Another bot instance owns {instance_lock.path}; refusing to start")
        return 2
    atexit.register(instance_lock.release)

    mode = "LIVE" if config.live_trading else "PAPER"
    log.info("=" * 60)
    log.info("Polymarket Bot")
    log.info(f"Mode: {mode} | Bankroll: ${config.initial_bankroll:.2f}")
    log.info(f"Min edge: {config.min_edge:.0%} | Max position: {effective_max_position_pct(config):.0%}")
    log.info(f"Scan interval: {config.scan_interval_minutes} min | Markets/cycle: {config.markets_per_cycle}")
    _mode_label = "multi" if config.multi_provider else config.ai_provider
    log.info(f"Ensemble: {config.ensemble_size}x [{_mode_label}]")
    log.info(
        f"LLM cost tracking: {'on' if config.llm_cost_tracking_enabled else 'off'}"
        + (f" | budgets: cycle=${config.max_cycle_api_cost_usd:.2f}, daily=${config.max_daily_api_cost_usd:.2f}"
           if config.llm_cost_tracking_enabled else "")
    )
    log.info(
        f"Risk caps: event={config.max_event_exposure_pct:.0%}, "
        f"category={config.max_category_exposure_pct:.0%}"
    )
    log.info(
        f"Quote safety: grace={config.quote_failure_grace_cycles} cycles, "
        f"stale haircut={config.stale_quote_haircut_pct:.0%} | "
        f"Resolution watch: {config.resolution_checks_per_cycle}/cycle, retry={config.resolution_retry_hours:g}h"
    )
    if config.live_trading and not config.allow_unsafe_risk and (
        effective_kelly_fraction(config) != config.kelly_fraction
        or effective_max_position_pct(config) != config.max_position_pct
        or effective_max_exposure_pct(config) != config.max_total_exposure_pct
        or effective_daily_stop_loss_pct(config) != config.daily_stop_loss_pct
        or effective_max_drawdown_pct(config) != config.max_drawdown_pct
    ):
        log.info(
            "Live guardrails active "
            f"(configured kelly={config.kelly_fraction:.2f}, pos={config.max_position_pct:.0%}, "
            f"exp={config.max_total_exposure_pct:.0%}, daily={config.daily_stop_loss_pct:.0%}, "
            f"dd={config.max_drawdown_pct:.0%})"
        )
    log.info("=" * 60)

    if con:
        print(f"\n{'='*60}")
        print(f"  POLYMARKET BOT — {mode} MODE")
        print(f"  Bankroll: ${config.initial_bankroll:.2f} | Min edge: {config.min_edge:.0%}")
        print(
            f"  Risk: {effective_max_position_pct(config):.0%}/pos, "
            f"{effective_max_exposure_pct(config):.0%}/total, "
            f"{effective_daily_stop_loss_pct(config):.0%}/daily-SL"
        )
        print(f"  Scan: every {config.scan_interval_minutes}min, {config.markets_per_cycle} markets/cycle")
        print(f"{'='*60}\n")

    _provider_key = {
        "anthropic":    config.anthropic_api_key,
        "openai":       config.openai_api_key,
        "gemini":       config.gemini_api_key,
        "openrouter":   config.openrouter_api_key,
        "azure_openai": config.azure_openai_api_key,
    }.get(config.ai_provider.lower(), "")
    if not _provider_key:
        log.error(f"API key for provider '{config.ai_provider}' is not set in config")
        sys.exit(1)

    # Load saved state or start fresh
    snapshot = load_snapshot(config.data_dir)
    portfolio = Portfolio(config, snapshot)
    if snapshot:
        # Clear a stale IsHalted flag if portfolio value is still healthy.
        if portfolio.is_halted and portfolio.equity() >= 1.0:
            portfolio.is_halted = False
            log.info(f"Cleared stale is_halted flag (portfolio equity ${portfolio.equity():.2f} is healthy)")
        log.info(f"Resumed from saved state: ${portfolio.bankroll:.2f} bankroll, {len(portfolio.positions)} positions")
        if con:
            print(f"[{ts()}] RESUME: ${portfolio.bankroll:.2f} bankroll, {len(portfolio.positions)} positions, ${portfolio.total_exposure():.2f} exposure")
    else:
        log.info("Starting fresh")
        if con:
            print(f"[{ts()}] START: fresh portfolio, ${portfolio.bankroll:.2f} bankroll")
        # Persist initial state immediately so portfolio.json exists from the start
        save_snapshot(portfolio.snapshot(), config.data_dir)

    if config.live_trading:
        try:
            geoblock = check_geoblock()
        except Exception as exc:
            log.critical(f"Cannot verify Polymarket geoblock status; refusing live trading: {exc}")
            return 3
        if geoblock.blocked:
            log.critical(
                f"Polymarket live trading is blocked in {geoblock.country or 'unknown'}/"
                f"{geoblock.region or 'unknown'}; refusing to start"
            )
            return 3
        log.info(f"Polymarket geoblock check passed ({geoblock.country}/{geoblock.region})")

    scanner = MarketScanner(config)
    estimator = Estimator(config)
    kalshi_shadow = KalshiShadow(config) if config.kalshi_shadow_enabled else None
    notifier = Notifier(config)

    if config.multi_provider:
        log.info("Validating all configured providers...")
    else:
        log.info(f"Validating {config.ai_provider} API key...")
    if not estimator.validate_api_key():
        if config.multi_provider:
            log.error("All configured AI providers failed — no AI available. Exiting.")
            if con:
                print(f"[{ts()}] {RED}ERROR: All AI providers failed. Check config.{RESET}")
        else:
            log.error(f"{config.ai_provider} API key is invalid or unauthorized. Exiting.")
            if con:
                print(f"[{ts()}] {RED}ERROR: {config.ai_provider} API key invalid. Check config.{RESET}")
        sys.exit(1)
    if config.multi_provider:
        log.info("Provider validation complete — at least one provider available.")
    else:
        log.info(f"{config.ai_provider} API key validated.")

    if config.live_trading:
        if not config.polymarket_private_key and not config.polymarket_api_key:
            log.error("POLYMARKET_PRIVATE_KEY or POLYMARKET_API_KEY required for live trading")
            sys.exit(1)
        trader = LiveTrader(config)
        if not trader.recover_pending_orders(portfolio):
            log.error("Pending live order recovery requires manual intervention; refusing to start")
            return 2

        # Sync bankroll from actual on-chain balance
        init_bal = trader.get_balance()
        if init_bal is not None:
            portfolio.sync_balance(init_bal)
            log.info(f"Initial USDC balance: ${init_bal:.2f}")
            if con:
                print(f"[{ts()}] BALANCE: ${init_bal:.2f} (on-chain)")
    else:
        trader = PaperTrader()

    notifier.notify_started(mode, portfolio.bankroll, len(portfolio.positions))

    # Graceful shutdown on Ctrl+C
    running = True

    def handle_shutdown(sig, frame):
        nonlocal running
        log.info("Shutdown requested...")
        if con:
            print(f"\n[{ts()}] SHUTDOWN requested (Ctrl+C)")
        running = False

    signal.signal(signal.SIGINT, handle_shutdown)

    cycle = 0
    exit_code = 0

    while running:
        cycle += 1

        if portfolio.is_halted:
            log.warning("Portfolio halted — stopping")
            if con:
                print(f"[{ts()}] {RED}HALTED: portfolio risk limit reached, stopping bot{RESET}")
            notifier.notify_halted("Risk limit reached", portfolio)
            break

        # Daily reset check
        today = datetime.now(timezone.utc).date().isoformat()
        if today != portfolio.daily_tracking_date:
            portfolio.reset_daily(today)
            save_snapshot(portfolio.snapshot(), config.data_dir)
            log.info(f"New day — daily start value reset to portfolio ${portfolio.daily_start_value:.2f}; daily API cost reset")
            if con:
                print(f"[{ts()}] NEW DAY: daily PnL/API reset, start=${portfolio.daily_start_value:.2f}")
            notifier.notify_daily_reset(portfolio)

        log.info(f"--- Cycle {cycle} ---")
        estimator.reset_cycle()
        cycle_api_cost_start = portfolio.total_api_cost

        # Sync on-chain USDC balance at start of each cycle (live trading only)
        if isinstance(trader, LiveTrader):
            cycle_bal = trader.get_balance()
            if cycle_bal is not None:
                portfolio.sync_balance(cycle_bal)

        pv = portfolio.equity()
        log.info(
            f"Portfolio: ${pv:.2f} "
            f"(bankroll=${portfolio.bankroll:.2f} + liquidation=${portfolio.liquidation_value():.2f}) | "
            f"{len(portfolio.positions)} positions"
        )

        if con:
            pv = portfolio.equity()
            print(f"\n{'─'*60}")
            print(f"[{ts()}] CYCLE {cycle}")
            print(f"  Portfolio: ${pv:.2f} (bankroll=${portfolio.bankroll:.2f} + liquidation=${portfolio.liquidation_value():.2f})")
            print(f"  Positions: {len(portfolio.positions)} | API today: ${portfolio.daily_api_cost:.4f} | total: ${portfolio.total_api_cost:.4f}")
            print(f"{'─'*60}")

        # ── Position review phase ─────────────────────────────────
        if config.enable_position_review and portfolio.positions:
            log.info(f"Reviewing {len(portfolio.positions)} open positions...")
            if con:
                print(f"[{ts()}] REVIEW: checking {len(portfolio.positions)} positions...")

            # Mark positions at full-size executable bid value, not midpoint.
            position_quotes = scanner.get_sell_quotes(portfolio.positions)
            portfolio.update_position_quotes(position_quotes)
            prices = {
                pos.token_id: pos.current_price
                for pos in portfolio.positions
                if pos.token_id in position_quotes
            }

            # Ghost check: verify actual on-chain balances (live trading only)
            if isinstance(trader, LiveTrader) and portfolio.positions:
                log.info(f"  Ghost check: verifying {len(portfolio.positions)} position balances...")
                ghost_cids = trader.verify_positions(list(portfolio.positions))
                for cid in ghost_cids:
                    ghost_pos = next((p for p in portfolio.positions if p.condition_id == cid), None)
                    if ghost_pos:
                        ghost_loss = ghost_pos.size_usd
                        log.warning(
                            f"  GHOST: {ghost_pos.question[:50]}... "
                            f"(0 tokens on-chain, writing off ${ghost_loss:.2f})"
                        )
                        if con:
                            print(f"[{ts()}]   {YELLOW}GHOST: {ghost_pos.question[:50]}... (no tokens on-chain, ${ghost_loss:.2f} lost){RESET}")
                        ghost_trade = Trade(
                            trade_id=str(uuid4()),
                            condition_id=ghost_pos.condition_id,
                            question=ghost_pos.question,
                            side=ghost_pos.side,
                            action=TradeAction.SELL,
                            price=0.0,
                            size_usd=ghost_pos.size_usd,
                            shares=ghost_pos.shares,
                            timestamp=time.time(),
                            is_paper=False,
                            rationale="Ghost position: no on-chain tokens found",
                            exit_reason="ghost",
                        )
                        portfolio.remove_ghost_position(cid)
                        append_trade(ghost_trade, config.data_dir)
                        save_snapshot(portfolio.snapshot(), config.data_dir)
                        notifier.notify_ghost_removed(ghost_pos, ghost_loss, portfolio)

            # Tier 0: check for resolved markets
            # Include both unpriced tokens AND penny positions (CLOB often returns
            # residual sub-cent prices for resolved markets)
            maybe_resolved = [
                p for p in portfolio.positions
                if p.token_id not in prices or prices.get(p.token_id, 0) < 0.01
            ]
            resolved_count = 0
            held_resolutions = scanner.check_market_resolutions(
                [pos.condition_id for pos in maybe_resolved]
            )
            for pos in maybe_resolved:
                resolution = held_resolutions.get(pos.condition_id)
                if resolution is None:
                    continue
                won = (pos.side.value == resolution["winning_side"])
                pnl = portfolio.resolve_position(pos.condition_id, won)
                result = "WON" if won else "LOST"
                resolved_count += 1
                color = GREEN if won else RED
                log.info(
                    f"  RESOLVED ({result}): {pos.question[:50]}... "
                    f"payout={'$%.2f' % (pos.shares if won else 0)}, PnL=${pnl:+.2f}"
                )
                if con:
                    print(f"[{ts()}]   {color}RESOLVED ({result}): {pos.question[:50]}... PnL=${pnl:+.2f}{RESET}")

                trade = Trade(
                    trade_id=str(uuid4()),
                    condition_id=pos.condition_id,
                    question=pos.question,
                    side=pos.side,
                    action=TradeAction.SELL,
                    price=1.0 if won else 0.0,
                    size_usd=pos.size_usd,
                    shares=pos.shares,
                    timestamp=time.time(),
                    is_paper=not config.live_trading,
                    rationale=f"Market resolved: {result}",
                    exit_reason=f"resolved_{result.lower()}",
                )
                append_trade(trade, config.data_dir)
                append_estimate_resolution(
                    pos.condition_id,
                    1.0 if resolution["winning_side"] == "YES" else 0.0,
                    config.data_dir,
                )
                save_snapshot(portfolio.snapshot(), config.data_dir)
                notifier.notify_resolved(pos, won, pnl, portfolio)

            if resolved_count > 0:
                log.info(f"  {resolved_count} market(s) resolved")
                if con:
                    print(f"[{ts()}]   {resolved_count} market(s) resolved, bankroll now ${portfolio.bankroll:.2f}")

            # Tier 1: free rule-based exit checks
            penny_count = sum(1 for p in portfolio.positions if p.current_price < 0.01)
            tiny_count = sum(1 for p in portfolio.positions if p.current_price >= 0.01 and p.shares < 5.0)
            exit_signals = portfolio.generate_exit_signals()
            exits_this_cycle = 0

            skip_parts = []
            if penny_count > 0:
                skip_parts.append(f"{penny_count} penny (price<$0.01)")
            if tiny_count > 0:
                skip_parts.append(f"{tiny_count} tiny (<5 tokens)")
            if skip_parts:
                skip_msg = ", ".join(skip_parts)
                log.info(f"  Skipping unsellable: {skip_msg}")
                if con:
                    print(f"[{ts()}]   {YELLOW}SKIP unsellable: {skip_msg}{RESET}")

            if exit_signals:
                log.info(f"  Found {len(exit_signals)} exit signals")
                if con:
                    print(f"[{ts()}]   Found {len(exit_signals)} exit signal(s)")
            else:
                log.info("  No exit signals — all positions OK")
                if con:
                    print(f"[{ts()}]   {GREEN}All positions OK, no exits needed{RESET}")

            for es in exit_signals:
                if not running or portfolio.is_halted:
                    break

                if es.exit_reason == "stop_loss" and config.stop_loss_requires_negative_edge:
                    review_market = build_review_market(es.position)
                    log.info(f"  STOP-LOSS CHECK: re-estimating {es.position.question[:50]}... before selling")
                    stop_estimate = estimator.estimate(review_market)
                    if config.llm_cost_tracking_enabled:
                        portfolio.record_api_cost_usd(estimator.last_api_cost_usd)
                    if stop_estimate is not None:
                        es.position.fair_estimate_at_entry = stop_estimate.fair_probability
                        fair_for_side = (
                            stop_estimate.fair_probability
                            if es.position.side == Side.YES
                            else 1.0 - stop_estimate.fair_probability
                        )
                        remaining_edge = fair_for_side - es.current_price
                        if remaining_edge > config.exit_edge_buffer:
                            log.info(
                                f"  HOLD stop-loss: {es.position.question[:50]}... "
                                f"still has edge {remaining_edge:.1%} after re-estimate"
                            )
                            if con:
                                print(
                                    f"[{ts()}]   HOLD stop-loss: {es.position.question[:45]}... "
                                    f"edge still {remaining_edge:.1%}"
                                )
                            save_snapshot(portfolio.snapshot(), config.data_dir)
                            continue
                    else:
                        log.warning("  STOP-LOSS CHECK failed: selling with rule-based stop-loss fallback")

                log.info(
                    f"  EXIT {es.exit_reason}: {es.position.question[:50]}... "
                    f"entry={es.position.entry_price:.4f} -> {es.current_price:.4f} "
                    f"(PnL={es.pnl_pct:+.1%})"
                )
                if con:
                    print(f"[{ts()}]   EXIT ({es.exit_reason}): {es.position.question[:50]}...")
                    print(f"[{ts()}]     {es.position.entry_price:.4f} -> {es.current_price:.4f} PnL={es.pnl_pct:+.1%}")

                trade = trader.execute_sell(es, portfolio)
                if trade:
                    # Re-sync bankroll from on-chain USDC after sell to correct
                    # partial-fill accounting drift.
                    if isinstance(trader, LiveTrader):
                        sell_bal = trader.get_balance()
                        if sell_bal is not None:
                            portfolio.sync_balance(sell_bal)
                            log.info(f"On-chain USDC after sell: ${sell_bal:.2f}")
                    save_snapshot(portfolio.snapshot(), config.data_dir)
                    append_trade(trade, config.data_dir)
                    if isinstance(trader, LiveTrader):
                        trader.confirm_applied_orders(portfolio)
                    exits_this_cycle += 1
                    notifier.notify_sell(trade, es.exit_reason, es.pnl_pct, portfolio)
                    if con:
                        print(f"[{ts()}]     {GREEN}SOLD OK{RESET}")
                else:
                    notifier.notify_sell_fail(es.position, es.exit_reason, "below CLOB minimum or order not filled")
                    if con:
                        print(f"[{ts()}]     {RED}SELL FAILED (min 5 tokens or order not filled){RESET}")

            # Tier 1.5: top-up-and-sell for tiny positions with exit signals
            topup_candidates = portfolio.generate_topup_candidates()
            if topup_candidates:
                log.info(f"  Found {len(topup_candidates)} topup candidate(s) (tiny positions with exit signals)")
                if con:
                    print(f"[{ts()}]   Found {len(topup_candidates)} topup candidate(s) (buy 5 tokens -> sell all)")

            for tc in topup_candidates:
                if not running or portfolio.is_halted:
                    break

                topup_quotes = scanner.get_topup_quotes(
                    tc.position.token_id, tc.tokens_to_buy, tc.position.shares + tc.tokens_to_buy
                )
                if topup_quotes is None or not all(quote.complete for quote in topup_quotes):
                    log.info(f"  SKIP topup: fresh two-leg book depth unavailable for {tc.position.question[:40]}...")
                    continue
                buy_quote, sell_quote = topup_quotes
                tc.topup_cost = buy_quote.filled_value
                tc.recovery_value = sell_quote.filled_value
                tc.buy_vwap = buy_quote.vwap
                tc.buy_limit_price = buy_quote.worst_price
                tc.sell_vwap = sell_quote.vwap
                tc.sell_limit_price = sell_quote.worst_price

                if tc.topup_cost > portfolio.bankroll:
                    log.info(
                        f"  SKIP topup: {tc.position.question[:40]}... "
                        f"cost=${tc.topup_cost:.2f} > bankroll=${portfolio.bankroll:.2f}"
                    )
                    if con:
                        print(
                            f"[{ts()}]   {YELLOW}SKIP topup: can't afford ${tc.topup_cost:.2f} "
                            f"(bankroll=${portfolio.bankroll:.2f}){RESET}"
                        )
                    continue

                log.info(
                    f"  TOPUP+SELL ({tc.exit_reason}): {tc.position.question[:40]}... "
                    f"{tc.position.shares:.2f} tokens, buy 5 more @ VWAP {tc.buy_vwap:.4f} "
                    f"(cost=${tc.topup_cost:.2f}, recover=${tc.recovery_value:.2f})"
                )
                if con:
                    print(
                        f"[{ts()}]   TOPUP ({tc.exit_reason}): {tc.position.question[:40]}..."
                    )
                    print(
                        f"[{ts()}]     {tc.position.shares:.2f} tokens + buy 5 @ VWAP {tc.buy_vwap:.4f} "
                        f"(cost=${tc.topup_cost:.2f})"
                    )

                trade = trader.execute_topup_and_sell(tc, portfolio)
                save_snapshot(portfolio.snapshot(), config.data_dir)
                if isinstance(trader, LiveTrader):
                    trader.confirm_applied_orders(portfolio)
                if trade:
                    append_trade(trade, config.data_dir)
                    exits_this_cycle += 1
                    notifier.notify_topup_sell(trade, tc, portfolio)
                    if con:
                        print(f"[{ts()}]     {GREEN}TOPUP+SELL OK (freed ${tc.recovery_value:.2f}){RESET}")
                else:
                    notifier.notify_topup_sell_fail(tc, "order not filled")
                    if con:
                        print(f"[{ts()}]     {RED}TOPUP+SELL FAILED{RESET}")

            if con:
                print(
                    f"[{ts()}] REVIEW: {exits_this_cycle} exits, "
                    f"bankroll=${portfolio.bankroll:.2f}, "
                    f"{len(portfolio.positions)} positions remaining"
                )

        # Resolve calibration outcomes for evaluated markets we never bought.
        held_condition_ids = {position.condition_id for position in portfolio.positions}
        resolution_candidates = get_resolution_candidates(
            config.data_dir, config.resolution_checks_per_cycle
        )
        watched_ids = [cid for cid in resolution_candidates if cid not in held_condition_ids]
        watched_resolutions = scanner.check_market_resolutions(watched_ids)
        deferred_ids = []
        resolved_ids = []
        for condition_id in resolution_candidates:
            if condition_id in held_condition_ids:
                deferred_ids.append(condition_id)
                continue
            resolution = watched_resolutions.get(condition_id)
            if resolution is None:
                deferred_ids.append(condition_id)
                continue
            append_estimate_resolution(
                condition_id,
                1.0 if resolution["winning_side"] == "YES" else 0.0,
                config.data_dir,
                remove_watch=False,
            )
            resolved_ids.append(condition_id)
        update_resolution_watchlist(
            deferred_ids, resolved_ids, config.data_dir, config.resolution_retry_hours
        )

        if not portfolio.check_portfolio_risk():
            log.warning("Portfolio risk limit reached — stopping before market scan")
            if con:
                print(f"[{ts()}] {RED}HALTED: portfolio risk limit reached, stopping before scan{RESET}")
            save_snapshot(portfolio.snapshot(), config.data_dir)
            notifier.notify_halted("Portfolio risk limit reached", portfolio)
            break

        try:
            # Skip scan entirely if bankroll can't fund the smallest possible trade.
            # Saves ~10s Gamma API call when no trade is possible.
            min_pos_pre = config.max_position_pct * portfolio.bankroll
            min_required = max(min_pos_pre, config.min_trade_usd)
            trades_this_cycle = 0

            if portfolio.bankroll < min_required:
                log.info(
                    f"Bankroll ${portfolio.bankroll:.2f} too low to trade "
                    f"(min ~${min_required:.2f}) — skipping scan"
                )
                if con:
                    print(f"[{ts()}] SCAN SKIP: bankroll ${portfolio.bankroll:.2f} < min ${min_required:.2f}")
                markets = []
                eligible = []
            else:
                log.info("Scanning markets...")
                if con:
                    print(f"[{ts()}] SCAN: fetching markets...")
                markets = scanner.scan()
                eligible = markets[:config.markets_per_cycle]
                if kalshi_shadow:
                    kalshi_shadow.refresh()

                if con:
                    print(f"[{ts()}] SCAN: {len(markets)} total, evaluating top {len(eligible)}")

            # Pre-check: skip estimation entirely if exposure is at the limit
            # Use liquidation equity as the exposure-limit denominator.
            pv = portfolio.equity()
            exposure_room = config.max_total_exposure_pct * pv - portfolio.total_exposure()
            min_realistic_position = config.max_position_pct * pv * 0.5
            # Also can't trade more than available cash
            exposure_room = min(exposure_room, portfolio.bankroll)
            at_capacity = exposure_room < min_realistic_position
            if at_capacity:
                log.info(
                    f"Exposure near limit: room=${exposure_room:.2f} < min realistic position=${min_realistic_position:.2f} — skipping estimation to save API costs"
                )
                if con:
                    print(f"[{ts()}] EXPOSURE FULL: room=${exposure_room:.2f} < ${min_realistic_position:.2f}, skipping evaluations")

            evaluated_markets = []
            for i, market in enumerate(eligible, 1):
                if not running or portfolio.is_halted:
                    break

                # Skip markets we already hold
                if portfolio.has_position(market.condition_id):
                    log.info(f"  [{i}/{len(eligible)}] SKIP (already held): {market.question[:60]}")
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] SKIP (held): {market.question[:55]}")
                    continue

                # Skip estimation entirely if at exposure limit (saves API costs)
                if at_capacity:
                    continue

                if config.llm_cost_tracking_enabled:
                    cycle_api_cost = portfolio.total_api_cost - cycle_api_cost_start
                    cycle_api_budget = config.max_cycle_api_cost_usd
                    daily_api_budget = config.max_daily_api_cost_usd
                    if cycle_api_cost >= cycle_api_budget:
                        log.info(
                            f"  API cycle budget reached (${cycle_api_cost:.4f} >= ${cycle_api_budget:.4f}) "
                            f"— skipping remaining evaluations"
                        )
                        if con:
                            print(
                                f"[{ts()}]   API BUDGET: cycle ${cycle_api_cost:.4f}/${cycle_api_budget:.4f}, "
                                f"skipping remaining evaluations"
                            )
                        break
                    if portfolio.daily_api_cost >= daily_api_budget:
                        log.info(
                            f"  API daily budget reached (${portfolio.daily_api_cost:.4f} >= ${daily_api_budget:.4f}) "
                            f"— skipping remaining evaluations"
                        )
                        if con:
                            print(
                                f"[{ts()}]   API BUDGET: daily ${portfolio.daily_api_cost:.4f}/${daily_api_budget:.4f}, "
                                f"skipping remaining evaluations"
                            )
                        break

                # Skip estimation if we can't afford the CLOB minimum for either side.
                # Conservative pre-book affordability check; the fresh book is checked after AI.
                best_price = min(market.outcome_yes_price, market.outcome_no_price)
                min_clob_cost = max(5.0 * min(best_price + config.entry_price_buffer, 0.99), 1.0)
                if portfolio.bankroll < min_clob_cost:
                    log.info(
                        f"  [{i}/{len(eligible)}] SKIP (can't afford CLOB min ${min_clob_cost:.2f}): "
                        f"{market.question[:50]}"
                    )
                    if con:
                        print(
                            f"[{ts()}]   [{i:>2}/{len(eligible)}] SKIP (need ${min_clob_cost:.2f} "
                            f"for 5 tokens, have ${portfolio.bankroll:.2f})"
                        )
                    continue

                # Estimate fair value
                log.info(f"  [{i}/{len(eligible)}] Evaluating: {market.question[:60]}...")
                if con:
                    print(f"[{ts()}]   [{i:>2}/{len(eligible)}] EVAL: {market.question[:55]}...")
                estimate = estimator.estimate(market)
                if config.llm_cost_tracking_enabled:
                    portfolio.record_api_cost_usd(estimator.last_api_cost_usd)
                if estimate is None:
                    log.info(f"  [{i}/{len(eligible)}] SKIP (no usable estimate)")
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] -> {YELLOW}SKIP (no usable estimate){RESET}")
                    continue
                evaluated_markets.append(market)

                # Track provider spend against API budgets without changing trading bankroll.
                kalshi_reference = None
                if kalshi_shadow:
                    kalshi_reference, verify_cost = kalshi_shadow.find_reference(market, estimator)
                    if config.llm_cost_tracking_enabled:
                        portfolio.record_api_cost_usd(verify_cost)

                # Only halt if total portfolio value is truly depleted
                if portfolio.equity() < 1.0:
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "skip", "portfolio_dead", kalshi_reference=kalshi_reference,
                        track_watch=False,
                    )
                    log.warning("Portfolio value < $1 — agent is dead")
                    if con:
                        print(f"[{ts()}] {RED}DEAD: portfolio value depleted{RESET}")
                    portfolio.is_halted = True
                    notifier.notify_halted("Portfolio value < $1 — agent is dead", portfolio)
                    break

                # Generate signal
                signal_obj = portfolio.generate_signal(market, estimate)
                if signal_obj is None:
                    yes_edge, no_edge = calculate_net_edges(market, estimate.fair_probability, config.entry_price_buffer)
                    best_edge = max(yes_edge, no_edge)
                    log.info(
                        f"  [{i}/{len(eligible)}] SKIP (no net edge): "
                        f"fair={estimate.fair_probability:.1%} vs market={market.outcome_yes_price:.1%} "
                        f"(net_edge={best_edge:+.1%}, need>{config.min_edge:.0%})"
                    )
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] -> {estimate.fair_probability:.0%} (edge={best_edge:+.1%}) SKIP")
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "skip", "kelly_or_clob_min" if best_edge > config.min_edge else "no_net_edge",
                        kalshi_reference=kalshi_reference, track_watch=False,
                    )
                    continue

                # The estimate may take tens of seconds. Re-price the provisional
                # signal from the current ask book and reject stale/thin books.
                token_id = market.token_id_yes if signal_obj.side == Side.YES else market.token_id_no
                execution_quote = scanner.get_buy_quote(token_id, signal_obj.position_size_usd)
                if execution_quote is None:
                    log.info(f"  [{i}/{len(eligible)}] SKIP (fresh CLOB book unavailable)")
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] -> {YELLOW}NO FRESH BOOK{RESET}")
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "skip", "no_fresh_book", signal_obj, kalshi_reference, False,
                    )
                    continue
                if not execution_quote.complete:
                    log.info(
                        f"  [{i}/{len(eligible)}] SKIP (insufficient ask depth): "
                        f"requested=${signal_obj.position_size_usd:.2f}, available=${execution_quote.filled_value:.2f}"
                    )
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] -> {YELLOW}THIN BOOK{RESET}")
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "skip", "insufficient_book_depth", signal_obj, kalshi_reference, False,
                    )
                    continue

                repriced = portfolio.reprice_signal(
                    signal_obj,
                    execution_quote.vwap,
                    execution_quote.worst_price,
                    execution_quote.age_seconds,
                )
                if repriced is None:
                    log.info(
                        f"  [{i}/{len(eligible)}] SKIP (edge disappeared at VWAP "
                        f"{execution_quote.vwap:.3f})"
                    )
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] -> edge disappeared at book VWAP")
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "skip", "edge_disappeared_at_vwap", signal_obj, kalshi_reference, False,
                    )
                    continue
                signal_obj = repriced

                # Risk check
                if not portfolio.check_risk(signal_obj):
                    log.info(
                        f"  [{i}/{len(eligible)}] SKIP (risk limit): "
                        f"{signal_obj.side.value} {market.question[:40]}... "
                        f"${signal_obj.position_size_usd:.2f}"
                    )
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] -> {estimate.fair_probability:.0%} {YELLOW}RISK BLOCKED{RESET}")
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "skip", "risk_blocked", signal_obj, kalshi_reference, False,
                    )
                    continue

                # Execute
                log.info(
                    f"  [{i}/{len(eligible)}] >>> BUYING {signal_obj.side.value} "
                    f"{market.question[:50]}... ${signal_obj.position_size_usd:.2f} "
                    f"@ book VWAP {signal_obj.execution_price:.3f} (limit {signal_obj.limit_price:.3f})"
                )
                if con:
                    print(f"[{ts()}]   [{i:>2}/{len(eligible)}] -> {estimate.fair_probability:.0%} edge={signal_obj.edge:.1%}")
                    print(f"[{ts()}]   [{i:>2}/{len(eligible)}] >>> BUY {signal_obj.side.value} ${signal_obj.position_size_usd:.2f} @ VWAP {signal_obj.execution_price:.3f}...")

                trade = trader.execute(signal_obj, portfolio)
                if trade:
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] {GREEN}TRADE OK{RESET} (EV=${signal_obj.expected_value:.2f})")

                    # Re-sync bankroll from on-chain USDC after each trade.
                    if isinstance(trader, LiveTrader):
                        bal = trader.get_balance()
                        if bal is not None:
                            portfolio.sync_balance(bal)
                            log.info(f"On-chain USDC after trade: ${bal:.2f}")
                            if con:
                                print(f"[{ts()}]   USDC balance: ${bal:.2f}")

                    save_snapshot(portfolio.snapshot(), config.data_dir)
                    append_trade(trade, config.data_dir)
                    if isinstance(trader, LiveTrader):
                        trader.confirm_applied_orders(portfolio)
                    trades_this_cycle += 1
                    notifier.notify_trade(trade, signal_obj, portfolio)

                    log.info(
                        f"  [{i}/{len(eligible)}] TRADE OK: {trade.side.value} {market.question[:50]}... "
                        f"${trade.size_usd:.2f} @ {trade.price:.3f} "
                        f"(edge={signal_obj.edge:.1%}, EV=${signal_obj.expected_value:.2f})"
                    )
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "buy", "executed", signal_obj, kalshi_reference, False,
                    )
                else:
                    log.warning(f"  [{i}/{len(eligible)}] TRADE FAILED: order execution error")
                    notifier.notify_buy_fail(market, signal_obj, "order execution error")
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] {RED}TRADE FAILED{RESET}")
                    append_estimate_evaluation(
                        market, estimate, config.data_dir,
                        "multi" if config.multi_provider else config.ai_provider,
                        "skip", "execution_failed", signal_obj, kalshi_reference, False,
                    )

            track_resolutions(evaluated_markets, config.data_dir)

            # Cycle summary
            log.info(
                f"Cycle {cycle}: {trades_this_cycle} trades | "
                f"Bankroll: ${portfolio.bankroll:.2f} | "
                f"Positions: {len(portfolio.positions)} | "
                f"Exposure: ${portfolio.total_exposure():.2f} | "
                f"API today: ${portfolio.daily_api_cost:.4f} | "
                f"API total: ${portfolio.total_api_cost:.4f} | "
                f"Realized PnL: ${portfolio.total_realized_pnl:+.2f}"
            )

            if con:
                pv = portfolio.equity()
                print(f"\n[{ts()}] SUMMARY: {trades_this_cycle} trades this cycle")
                print(f"  Portfolio: ${pv:.2f} | Bankroll: ${portfolio.bankroll:.2f} | Exposure: ${portfolio.total_exposure():.2f}")
                print(f"  Positions: {len(portfolio.positions)} | API today: ${portfolio.daily_api_cost:.4f} | total: ${portfolio.total_api_cost:.4f} | PnL: ${portfolio.total_realized_pnl:+.2f}")

            save_snapshot(portfolio.snapshot(), config.data_dir)

            if args.once:
                log.info(f"Run-once complete — stopping after cycle {cycle}")
                if con:
                    print(f"[{ts()}] ONCE: completed one cycle, stopping")
                break

        except TradingBlockedError as e:
            exit_code = 3
            running = False
            log.critical(f"Emergency stop after definitive CLOB 403: {e}")
            if con:
                print(f"\n[{ts()}] {RED}EMERGENCY STOP: {e}{RESET}")
            notifier.notify_error(cycle, e)
            break
        except Exception as e:
            log.error(f"Cycle {cycle} error: {e}", exc_info=True)
            if con:
                print(f"\n[{ts()}] {RED}ERROR: {e}{RESET}")
            notifier.notify_error(cycle, e)

        # Sleep in 1-second ticks for responsive shutdown
        if running:
            log.info(f"Next scan in {config.scan_interval_minutes} min")
            if con:
                print(f"[{ts()}] WAIT: sleeping {config.scan_interval_minutes} min...")
            for _ in range(config.scan_interval_minutes * 60):
                if not running:
                    break
                time.sleep(1)

    # Final save
    save_snapshot(portfolio.snapshot(), config.data_dir)
    notifier.notify_stopped(portfolio)
    log.info(
        f"Bot stopped | Final bankroll: ${portfolio.bankroll:.2f} | "
        f"Total trades: {portfolio.total_trades} | "
        f"Total API cost: ${portfolio.total_api_cost:.4f} | "
        f"Realized PnL: ${portfolio.total_realized_pnl:+.2f}"
    )
    if con:
        pv = portfolio.equity()
        print(f"\n{'='*60}")
        print(f"[{ts()}] BOT STOPPED")
        print(f"  Final portfolio: ${pv:.2f} | Bankroll: ${portfolio.bankroll:.2f}")
        print(f"  Total trades: {portfolio.total_trades} | API cost: ${portfolio.total_api_cost:.4f}")
        print(f"  Realized PnL: ${portfolio.total_realized_pnl:+.2f}")
        print(f"{'='*60}")
    instance_lock.release()
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
