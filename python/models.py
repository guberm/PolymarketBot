"""Shared data models for the Polymarket trading bot."""

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional
import time


class Side(Enum):
    YES = "YES"
    NO = "NO"


class TradeAction(Enum):
    BUY = "BUY"
    SELL = "SELL"


@dataclass
class MarketInfo:
    """A single binary market from the Gamma API."""
    condition_id: str
    question: str
    slug: str
    outcome_yes_price: float
    outcome_no_price: float
    token_id_yes: str
    token_id_no: str
    liquidity: float
    volume: float
    volume_24hr: float
    best_bid: float
    best_ask: float
    spread: float
    end_date: str  # ISO 8601
    category: str
    event_title: str
    description: str


@dataclass
class Estimate:
    """Result of Claude ensemble probability estimation."""
    market_condition_id: str
    question: str
    fair_probability: float  # Trimmed mean of ensemble
    raw_estimates: list[float]
    confidence: float  # Std dev (lower = more confident)
    reasoning_summary: str
    timestamp: float = field(default_factory=time.time)
    input_tokens_used: int = 0
    output_tokens_used: int = 0
    api_cost_usd: float = 0.0
    duration_seconds: float = 0.0
    provider_estimates: dict[str, float] = field(default_factory=dict)


@dataclass
class Signal:
    """A trading signal after comparing estimate to market price."""
    market: MarketInfo
    estimate: Estimate
    side: Side
    edge: float
    market_price: float  # Price we'd pay for the chosen side
    execution_price: float  # Estimated taker price after entry buffer
    kelly_fraction: float  # Raw Kelly fraction
    position_size_usd: float
    expected_value: float
    limit_price: float = 0.0  # Worst acceptable book level for the GTC order
    quote_age_seconds: float = 0.0


@dataclass
class Position:
    """An open position in the portfolio."""
    condition_id: str
    question: str
    side: Side
    token_id: str
    entry_price: float
    size_usd: float  # Cost basis
    shares: float
    current_price: float
    unrealized_pnl: float
    category: str
    event_title: str = ""
    opened_at: float = field(default_factory=time.time)
    order_id: Optional[str] = None
    fair_estimate_at_entry: float = 0.0  # Original Claude estimate (0 = unknown/legacy)
    liquidation_limit_price: float = 0.0
    book_depth_complete: bool = True
    quote_age_seconds: float = 0.0
    last_fresh_price: float = 0.0
    quote_failures: int = 0


@dataclass
class Trade:
    """A completed trade record."""
    trade_id: str  # UUID
    condition_id: str
    question: str
    side: Side
    action: TradeAction
    price: float
    size_usd: float
    shares: float
    timestamp: float
    order_id: Optional[str] = None
    is_paper: bool = True
    rationale: str = ""
    edge_at_entry: float = 0.0
    kelly_at_entry: float = 0.0
    exit_reason: str = ""
    quoted_vwap: float = 0.0
    slippage_bps: float = 0.0
    fill_status: str = ""


@dataclass
class ExitSignal:
    """Signal to close an existing position."""
    position: Position
    exit_reason: str  # "stop_loss", "take_profit", "edge_gone", "reestimate_exit"
    current_price: float
    unrealized_pnl: float
    pnl_pct: float  # PnL as fraction of entry price


@dataclass
class TopupCandidate:
    """Tiny position (<5 tokens) that wants to exit but needs a top-up BUY first."""
    position: Position
    exit_reason: str
    tokens_to_buy: float   # 5.0 (CLOB minimum for BUY order)
    topup_cost: float      # tokens_to_buy * current_price
    recovery_value: float  # position.shares * current_price (stuck capital to free)
    buy_vwap: float = 0.0
    buy_limit_price: float = 0.0
    sell_vwap: float = 0.0
    sell_limit_price: float = 0.0


@dataclass
class PortfolioSnapshot:
    """Complete portfolio state for persistence."""
    bankroll: float
    initial_bankroll: float
    positions: list[Position]
    high_water_mark: float
    daily_start_value: float
    total_realized_pnl: float
    total_trades: int
    is_halted: bool
    last_updated: float = field(default_factory=time.time)
    total_api_cost: float = 0.0
    daily_api_cost: float = 0.0
    daily_tracking_date: str = ""
    applied_order_ids: list[str] = field(default_factory=list)
