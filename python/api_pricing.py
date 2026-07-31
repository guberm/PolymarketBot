"""Provider-specific inference cost calculation."""


def parse_api_pricing(spec: str) -> dict[str, tuple[float, float]]:
    prices: dict[str, tuple[float, float]] = {}
    for item in spec.split(","):
        try:
            provider, rates = item.strip().split("=", 1)
            input_rate, output_rate = rates.split("/", 1)
            prices[provider.strip().lower()] = (float(input_rate), float(output_rate))
        except (ValueError, TypeError):
            continue
    return prices


def calculate_api_cost(spec: str, provider: str, input_tokens: int, output_tokens: int) -> float:
    input_rate, output_rate = parse_api_pricing(spec).get(provider.lower(), (0.0, 0.0))
    return (input_tokens * input_rate + output_tokens * output_rate) / 1_000_000
