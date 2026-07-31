"""Order-book execution pricing shared by paper and live trading."""

from dataclasses import dataclass


@dataclass(frozen=True)
class BookLevel:
    price: float
    size: float


@dataclass(frozen=True)
class ExecutionQuote:
    requested: float
    filled_quantity: float
    filled_value: float
    vwap: float
    worst_price: float
    complete: bool
    timestamp_ms: int = 0
    age_seconds: float = 0.0


def calculate_buy_quote(levels: list[BookLevel], amount_usd: float) -> ExecutionQuote:
    """Walk asks from cheapest to most expensive for a USD-denominated BUY."""
    requested = max(0.0, amount_usd)
    remaining = requested
    shares = value = worst = 0.0

    for level in sorted(levels, key=lambda item: item.price):
        if remaining <= 1e-9:
            break
        if not (0 < level.price < 1) or level.size <= 0:
            continue
        level_value = level.price * level.size
        spend = min(remaining, level_value)
        shares += spend / level.price
        value += spend
        remaining -= spend
        worst = level.price

    complete = requested > 0 and remaining <= max(1e-8, requested * 1e-8)
    return ExecutionQuote(
        requested=requested,
        filled_quantity=shares,
        filled_value=value,
        vwap=value / shares if shares > 0 else 0.0,
        worst_price=worst,
        complete=complete,
    )


def calculate_buy_shares_quote(levels: list[BookLevel], shares: float) -> ExecutionQuote:
    """Walk asks from cheapest to most expensive for a token-denominated BUY."""
    requested = max(0.0, shares)
    remaining = requested
    bought = value = worst = 0.0

    for level in sorted(levels, key=lambda item: item.price):
        if remaining <= 1e-9:
            break
        if not (0 < level.price < 1) or level.size <= 0:
            continue
        quantity = min(remaining, level.size)
        bought += quantity
        value += quantity * level.price
        remaining -= quantity
        worst = level.price

    complete = requested > 0 and remaining <= max(1e-8, requested * 1e-8)
    return ExecutionQuote(requested, bought, value, value / bought if bought else 0.0, worst, complete)


def calculate_sell_quote(levels: list[BookLevel], shares: float) -> ExecutionQuote:
    """Walk bids from most expensive to cheapest for a token-denominated SELL."""
    requested = max(0.0, shares)
    remaining = requested
    sold = value = worst = 0.0

    for level in sorted(levels, key=lambda item: item.price, reverse=True):
        if remaining <= 1e-9:
            break
        if not (0 < level.price < 1) or level.size <= 0:
            continue
        quantity = min(remaining, level.size)
        sold += quantity
        value += quantity * level.price
        remaining -= quantity
        worst = level.price

    complete = requested > 0 and remaining <= max(1e-8, requested * 1e-8)
    return ExecutionQuote(
        requested=requested,
        filled_quantity=sold,
        filled_value=value,
        vwap=value / sold if sold > 0 else 0.0,
        worst_price=worst,
        complete=complete,
    )
