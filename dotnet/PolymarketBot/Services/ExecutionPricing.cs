namespace PolymarketBot.Services;

public readonly record struct BookLevel(double Price, double Size);

public readonly record struct ExecutionQuote(
    double Requested,
    double FilledQuantity,
    double FilledValue,
    double Vwap,
    double WorstPrice,
    bool Complete,
    long TimestampMs = 0,
    double AgeSeconds = 0);

public static class ExecutionPricing
{
    public static ExecutionQuote CalculateBuy(IEnumerable<BookLevel> levels, double amountUsd)
    {
        var requested = Math.Max(0, amountUsd);
        var remaining = requested;
        var shares = 0.0;
        var value = 0.0;
        var worst = 0.0;

        foreach (var level in levels.OrderBy(level => level.Price))
        {
            if (remaining <= 1e-9) break;
            if (level.Price is <= 0 or >= 1 || level.Size <= 0) continue;
            var spend = Math.Min(remaining, level.Price * level.Size);
            shares += spend / level.Price;
            value += spend;
            remaining -= spend;
            worst = level.Price;
        }

        var complete = requested > 0 && remaining <= Math.Max(1e-8, requested * 1e-8);
        return new ExecutionQuote(requested, shares, value,
            shares > 0 ? value / shares : 0, worst, complete);
    }

    public static ExecutionQuote CalculateSell(IEnumerable<BookLevel> levels, double shares)
    {
        var requested = Math.Max(0, shares);
        var remaining = requested;
        var sold = 0.0;
        var value = 0.0;
        var worst = 0.0;

        foreach (var level in levels.OrderByDescending(level => level.Price))
        {
            if (remaining <= 1e-9) break;
            if (level.Price is <= 0 or >= 1 || level.Size <= 0) continue;
            var quantity = Math.Min(remaining, level.Size);
            sold += quantity;
            value += quantity * level.Price;
            remaining -= quantity;
            worst = level.Price;
        }

        var complete = requested > 0 && remaining <= Math.Max(1e-8, requested * 1e-8);
        return new ExecutionQuote(requested, sold, value,
            sold > 0 ? value / sold : 0, worst, complete);
    }

    public static ExecutionQuote CalculateBuyShares(IEnumerable<BookLevel> levels, double shares)
    {
        var requested = Math.Max(0, shares);
        var remaining = requested;
        var bought = 0.0;
        var value = 0.0;
        var worst = 0.0;

        foreach (var level in levels.OrderBy(level => level.Price))
        {
            if (remaining <= 1e-9) break;
            if (level.Price is <= 0 or >= 1 || level.Size <= 0) continue;
            var quantity = Math.Min(remaining, level.Size);
            bought += quantity;
            value += quantity * level.Price;
            remaining -= quantity;
            worst = level.Price;
        }

        var complete = requested > 0 && remaining <= Math.Max(1e-8, requested * 1e-8);
        return new ExecutionQuote(requested, bought, value,
            bought > 0 ? value / bought : 0, worst, complete);
    }
}
