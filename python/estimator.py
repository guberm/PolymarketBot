"""AI ensemble probability estimation for prediction markets.

Supported providers: anthropic, openai, gemini, openrouter, azure_openai

Multi-provider mode (multi_provider=true):
  Queries every provider that has an API key configured, scores each by
  stability, then returns an equal or calibration-gated weighted provider mean.
  Per-provider model fields: anthropic_model, openai_model, gemini_model, openrouter_model
"""

import json
import logging
import math
import statistics
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Optional

import requests

try:
    import anthropic as _anthropic
except ImportError:
    _anthropic = None  # type: ignore[assignment]

from config import BotConfig
from api_pricing import calculate_api_cost
from calibration import calibration_weights, load_provider_stats
from models import MarketInfo, Estimate
from runtime_safety import retry_delay_seconds

log = logging.getLogger("bot.estimator")

SYSTEM_PROMPT = """You are a calibrated probability estimator for prediction markets.
Given a market question, estimate the TRUE probability that the outcome resolves YES.

Rules:
- Output ONLY valid JSON: {"probability": 0.XX, "reasoning": "one sentence"}
- probability must be between 0.02 and 0.98
- Be well-calibrated: events you rate at 70% should happen ~70% of the time
- Use base rates, current knowledge, and logical reasoning
- The current market price reflects real-money consensus from many informed traders — treat it as a Bayesian prior. Only deviate significantly if you have strong specific reasoning.
- If deeply uncertain, stay close to the market price
- Keep reasoning under 50 words"""


def _build_user_prompt(market: MarketInfo) -> str:
    desc = market.description[:500] if market.description else "N/A"
    return (
        f"Market: {market.question}\n"
        f"Event: {market.event_title}\n"
        f"Description: {desc}\n"
        f"Category: {market.category}\n"
        f"Resolution date: {market.end_date or 'Unknown'}\n"
        f"Current market price: YES at {market.outcome_yes_price:.0%} / NO at {market.outcome_no_price:.0%}\n\n"
        f"Estimate the probability this resolves YES. Output JSON only."
    )


class Estimator:
    def __init__(self, config: BotConfig):
        self.config = config
        self._provider = config.ai_provider.lower()
        self.last_api_cost_usd = 0.0
        self._rate_limited_this_cycle: set[str] = set()
        self._rate_limit_lock = threading.Lock()
        self._calibration_stats = {}
        self._refresh_calibration()

        # Initialize Anthropic client whenever the key is present (single + multi-provider)
        if config.anthropic_api_key and _anthropic is not None:
            kwargs: dict = {"api_key": config.anthropic_api_key}
            if config.anthropic_api_host:
                kwargs["base_url"] = config.anthropic_api_host
            self._anthropic_client = _anthropic.Anthropic(**kwargs)
        else:
            self._anthropic_client = None

    # ── Public API ─────────────────────────────────────────────────────────

    def reset_cycle(self) -> None:
        with self._rate_limit_lock:
            self._rate_limited_this_cycle.clear()
        self._refresh_calibration()

    def _refresh_calibration(self) -> None:
        self._calibration_stats = (
            load_provider_stats(Path(self.config.data_dir) / "estimates.jsonl")
            if self.config.calibration_weighting_enabled else {}
        )

    def estimate(self, market: MarketInfo) -> Optional[Estimate]:
        """Run estimation. Uses multi-provider mode if configured."""
        started = time.monotonic()
        self.last_api_cost_usd = 0.0
        result = self._estimate_multi(market) if self.config.multi_provider else self._estimate_single(market)
        if result is not None:
            result.duration_seconds = time.monotonic() - started
        return result

    def verify_market_equivalence(self, market: MarketInfo, kalshi: dict):
        """Use one provider call to estimate whether two resolution criteria are equivalent."""
        provider = (self._get_configured_providers() or [self._provider])[0]
        comparison = MarketInfo(
            condition_id=f"kalshi:{kalshi.get('ticker', '')}",
            question=(
                "Will these two prediction markets always resolve to the same outcome? "
                f"Polymarket: {market.question} (ends {market.end_date or 'unknown'}). "
                f"Kalshi: {kalshi.get('title', '')} (closes {kalshi.get('close_time', 'unknown')})."
            ),
            slug="", outcome_yes_price=0.5, outcome_no_price=0.5,
            token_id_yes="", token_id_no="", liquidity=0, volume=0, volume_24hr=0,
            best_bid=0, best_ask=0, spread=0, end_date=market.end_date,
            category="market-equivalence", event_title="Cross-market equivalence check",
            description=(
                f"Polymarket rules: {market.description[:500] or 'N/A'}\n"
                f"Kalshi rules: {kalshi.get('rules_primary', '')} "
                f"{kalshi.get('rules_secondary', '')}"
            )[:1000],
        )
        result = self._single_call(comparison, provider)
        if result is None:
            return None
        probability, _, input_tokens, output_tokens = result
        cost = (calculate_api_cost(self.config.api_pricing, provider, input_tokens, output_tokens)
                if self.config.llm_cost_tracking_enabled else 0.0)
        return probability, input_tokens, output_tokens, cost

    # ── Single-provider estimation ─────────────────────────────────────────

    def _estimate_single(self, market: MarketInfo) -> Optional[Estimate]:
        """Ensemble estimation using the configured provider only."""
        raw_estimates: list[float] = []
        total_input = 0
        total_output = 0
        first_reasoning = ""

        for _ in range(self.config.ensemble_size):
            result = self._single_call(market, self._provider)
            if result is None:
                continue
            prob, reasoning, in_tok, out_tok = result
            total_input += in_tok
            total_output += out_tok
            if prob is None:
                continue
            raw_estimates.append(prob)
            if not first_reasoning:
                first_reasoning = reasoning

        cost = (calculate_api_cost(self.config.api_pricing, self._provider, total_input, total_output)
                if self.config.llm_cost_tracking_enabled else 0.0)
        self.last_api_cost_usd = cost
        return self._build_estimate(market, raw_estimates, total_input, total_output, first_reasoning,
                                    api_cost_usd=cost,
                                    provider_estimates={self._provider: statistics.mean(raw_estimates)} if raw_estimates else {})

    # ── Multi-provider estimation ──────────────────────────────────────────

    def _estimate_multi(self, market: MarketInfo) -> Optional[Estimate]:
        """Query all configured providers, score them, return trimmed mean."""
        configured = self._get_configured_providers()
        if not configured:
            log.warning("multi_provider=true but no providers configured — falling back to single")
            return self._estimate_single(market)

        # Distribute ensemble_size calls across providers (minimum 1 per provider)
        calls_per = max(1, math.ceil(self.config.ensemble_size / len(configured)))

        # Collect per-provider results: (provider, [probs], total_input, total_output, reasoning)
        provider_results: list[tuple] = []
        all_probs: list[float] = []
        total_input = 0
        total_output = 0
        total_cost = 0.0
        first_reasoning = ""

        def estimate_provider(provider: str):
            probs: list[float] = []
            p_input = 0
            p_output = 0
            p_reasoning = ""

            for _ in range(calls_per):
                result = self._single_call(market, provider)
                if result is None:
                    continue
                prob, reasoning, in_tok, out_tok = result
                p_input += in_tok
                p_output += out_tok
                if prob is None:
                    continue
                probs.append(prob)
                if not p_reasoning:
                    p_reasoning = reasoning

            return provider, probs, p_input, p_output, p_reasoning

        with ThreadPoolExecutor(max_workers=len(configured), thread_name_prefix="ai-provider") as pool:
            results = list(pool.map(estimate_provider, configured))

        for provider, probs, p_input, p_output, p_reasoning in results:
            total_input += p_input
            total_output += p_output
            if self.config.llm_cost_tracking_enabled:
                total_cost += calculate_api_cost(self.config.api_pricing, provider, p_input, p_output)
            if not probs:
                log.warning(f"  {provider}: no valid estimates — skipped")
                continue

            provider_results.append((provider, probs, p_input, p_output, p_reasoning))
            all_probs.extend(probs)
            if not first_reasoning:
                first_reasoning = p_reasoning

        self.last_api_cost_usd = total_cost
        if not provider_results:
            return None

        # ── Score each provider ──────────────────────────────────────────
        # Low variance is useful, but strong market deviation is not automatically
        # evidence of skill unless another layer confirms it.
        market_price = (market.outcome_yes_price + (1 - market.outcome_no_price)) / 2

        scored: list[tuple] = []  # (provider, mean, std, score)
        for provider, probs, _, _, _ in provider_results:
            mean = statistics.mean(probs)
            std = statistics.stdev(probs) if len(probs) > 1 else 0.0
            confidence = 1.0 / (std + 0.01)
            market_deviation = abs(mean - market_price)
            score = confidence / (1.0 + 8.0 * market_deviation)
            scored.append((provider, mean, std, score))

        scored.sort(key=lambda x: x[3], reverse=True)  # highest score first
        winner = scored[0][0]

        # ── Build breakdown log ────────────────────────────────────────────
        parts = []
        for provider, mean, std, score in scored:
            tag = "⭐" if provider == winner else "  "
            parts.append(f"{tag}{provider}={mean:.0%}(±{std:.2f},s={score:.3f})")
        breakdown = " | ".join(parts)
        log.info(f"Multi-provider [{market.question[:40]}]: consensus={statistics.mean(all_probs):.0%} | {breakdown}")

        # ── Final estimate: trimmed mean across ALL provider means ─────────
        # Use per-provider means (not raw calls) so each provider counts equally
        provider_means = [m for _, m, _, _ in scored]
        weights = calibration_weights(
            self._calibration_stats,
            [provider for provider, _, _, _ in scored],
            self.config.calibration_min_samples,
            self.config.calibration_shrinkage,
            self.config.calibration_max_provider_weight,
        )
        weighted_probability = (
            sum(mean * weights[provider] for provider, mean, _, _ in scored)
            if weights else None
        )
        if weights:
            log.info("Calibration weights: " + ", ".join(
                f"{provider}={weight:.0%}" for provider, weight in weights.items()
            ))

        return self._build_estimate(
            market, provider_means, total_input, total_output, first_reasoning,
            note=f"multi({len(scored)} providers, winner={winner})", api_cost_usd=total_cost,
            provider_estimates={provider: mean for provider, mean, _, _ in scored},
            fair_probability_override=weighted_probability,
        )

    def _get_configured_providers(self) -> list[str]:
        """Return providers that have API keys configured and are enabled."""
        c = self.config
        out = []
        if c.anthropic_enabled and c.anthropic_api_key:
            out.append("anthropic")
        if c.openai_enabled and c.openai_api_key:
            out.append("openai")
        if c.gemini_enabled and c.gemini_api_key:
            out.append("gemini")
        if c.openrouter_enabled and c.openrouter_api_key:
            out.append("openrouter")
        if c.azure_openai_enabled and c.azure_openai_api_key and c.azure_openai_endpoint and c.azure_openai_deployment:
            out.append("azure_openai")
        return out

    # Built-in defaults used when a per-provider model field is empty
    _PROVIDER_DEFAULTS = {
        "anthropic":    "claude-sonnet-4-6",
        "openai":       "gpt-4o",
        "gemini":       "gemini-2.0-flash",
        "openrouter":   "",
        "azure_openai": "",
    }

    def _get_model(self, provider: str) -> str:
        """Return the model to use for a given provider."""
        c = self.config
        per_provider = {
            "anthropic":    c.anthropic_model,
            "openai":       c.openai_model,
            "gemini":       c.gemini_model,
            "openrouter":   c.openrouter_model,
            "azure_openai": c.azure_openai_deployment,
        }
        return per_provider.get(provider) or self._PROVIDER_DEFAULTS.get(provider, "")

    # ── Shared estimate builder ────────────────────────────────────────────

    def _build_estimate(
        self,
        market: MarketInfo,
        raw_estimates: list[float],
        total_input: int,
        total_output: int,
        first_reasoning: str,
        note: str = "",
        api_cost_usd: float = 0.0,
        provider_estimates: Optional[dict[str, float]] = None,
        fair_probability_override: Optional[float] = None,
    ) -> Optional[Estimate]:
        if len(raw_estimates) < 1:
            return None

        if len(raw_estimates) < 2:
            log.warning(f"Only {len(raw_estimates)} valid estimates for: {market.question[:60]}")

        if len(raw_estimates) >= 4:
            trimmed = sorted(raw_estimates)[1:-1]
        else:
            trimmed = raw_estimates

        fair_prob = fair_probability_override if fair_probability_override is not None else statistics.mean(trimmed)
        confidence = statistics.stdev(raw_estimates) if len(raw_estimates) > 1 else 1.0

        if len(raw_estimates) >= 2 and confidence > self.config.max_estimate_std:
            log.info(
                f"SKIP (low confidence): {market.question[:50]}... "
                f"std={confidence:.3f} > max={self.config.max_estimate_std:.3f}"
            )
            return None

        label = f"[{note}] " if note else ""
        log.info(
            f"Estimate: {label}{market.question[:50]}... -> {fair_prob:.2%} "
            f"(n={len(raw_estimates)}, std={confidence:.3f})"
        )

        return Estimate(
            market_condition_id=market.condition_id,
            question=market.question,
            fair_probability=fair_prob,
            raw_estimates=raw_estimates,
            confidence=confidence,
            reasoning_summary=first_reasoning,
            input_tokens_used=total_input,
            output_tokens_used=total_output,
            api_cost_usd=api_cost_usd,
            provider_estimates=provider_estimates or {},
        )

    # ── Provider dispatch ──────────────────────────────────────────────────

    def _single_call(self, market: MarketInfo, provider: str):
        """Return (probability or None, reasoning, input tokens, output tokens), or None before usage exists."""
        with self._rate_limit_lock:
            if provider in self._rate_limited_this_cycle:
                log.debug(f"{provider} skipped — rate-limited this cycle")
                return None
        if provider == "anthropic":
            return self._call_anthropic(market)
        elif provider == "gemini":
            return self._call_gemini(market)
        elif provider in ("openai", "openrouter", "azure_openai"):
            return self._call_openai_compat(market, provider)
        else:
            log.error(f"Unknown AI provider: {provider}")
            return None

    def _parse_json_response(self, text: str):
        """Parse probability JSON from model response. Returns (prob, reasoning) or None."""
        try:
            if text.startswith("```"):
                lines = [l for l in text.split("\n") if not l.strip().startswith("```")]
                text = "\n".join(lines)
            data = json.loads(text)
            prob = float(data["probability"])
            reasoning = data.get("reasoning", "")
            prob = max(0.02, min(0.98, prob))
            return prob, reasoning
        except (json.JSONDecodeError, KeyError, ValueError) as e:
            log.debug(f"Failed to parse estimate response: {e}")
            return None

    # ── Anthropic ─────────────────────────────────────────────────────────

    def _call_anthropic(self, market: MarketInfo):
        if not self._anthropic_client:
            log.error("Anthropic client not initialized (missing api key)")
            return None
        for attempt in range(4):
            try:
                response = self._anthropic_client.messages.create(
                    model=self._get_model("anthropic"),
                    max_tokens=self.config.max_estimate_tokens,
                    temperature=self.config.ensemble_temperature,
                    system=SYSTEM_PROMPT,
                    messages=[{"role": "user", "content": _build_user_prompt(market)}],
                )
                in_tok = response.usage.input_tokens
                out_tok = response.usage.output_tokens
                text_block = next((b for b in response.content if hasattr(b, "text")), None)
                if text_block is None:
                    return None, "", in_tok, out_tok
                result = self._parse_json_response(text_block.text.strip())  # type: ignore[union-attr]
                if result is None:
                    return None, "", in_tok, out_tok
                prob, reasoning = result
                return prob, reasoning, in_tok, out_tok
            except Exception as e:
                if _anthropic is not None and isinstance(e, _anthropic.RateLimitError):
                    if attempt < 3:
                        response = getattr(e, "response", None)
                        delay = retry_delay_seconds(
                            getattr(response, "headers", {}).get("retry-after") if response else None, attempt)
                        log.warning(f"Anthropic rate limit — retrying in {delay:g}s")
                        time.sleep(delay)
                        continue
                    self._mark_rate_limited("anthropic")
                    return None
                if _anthropic is not None and isinstance(e, _anthropic.APIError):
                    log.error(f"Anthropic API error: {e}")
                    return None
                raise
        return None

    # ── OpenAI-compatible (OpenAI, OpenRouter, Azure OpenAI) ──────────────

    def _call_openai_compat(self, market: MarketInfo, provider: str):
        model = self._get_model(provider)

        if provider == "openai":
            host = (self.config.openai_api_host or "https://api.openai.com").rstrip("/")
            url = f"{host}/v1/chat/completions"
            headers = {"Authorization": f"Bearer {self.config.openai_api_key}"}
        elif provider == "openrouter":
            host = (self.config.openrouter_api_host or "https://openrouter.ai").rstrip("/")
            url = f"{host}/api/v1/chat/completions"
            headers = {"Authorization": f"Bearer {self.config.openrouter_api_key}"}
        else:  # azure_openai
            endpoint = self.config.azure_openai_endpoint.rstrip("/")
            deployment = self.config.azure_openai_deployment
            version = self.config.azure_openai_api_version or "2024-02-01"
            url = f"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version}"
            headers = {"api-key": self.config.azure_openai_api_key}
            model = deployment

        headers["Content-Type"] = "application/json"
        payload = {
            "model": model,
            "messages": [
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": _build_user_prompt(market)},
            ],
            "temperature": self.config.ensemble_temperature,
            "max_tokens": self.config.max_estimate_tokens,
        }

        try:
            resp = self._post_json_with_retry(provider, url, payload, headers)
            if resp is None:
                return None
            resp.raise_for_status()
            data = resp.json()
            text = data["choices"][0]["message"]["content"].strip()
            usage = data.get("usage", {})
            in_tok = usage.get("prompt_tokens", 0)
            out_tok = usage.get("completion_tokens", 0)
            result = self._parse_json_response(text)
            if result is None:
                return None, "", in_tok, out_tok
            prob, reasoning = result
            return prob, reasoning, in_tok, out_tok
        except requests.exceptions.HTTPError as e:
            log.error(f"{provider} API error: {e}")
            return None
        except Exception as e:
            log.debug(f"{provider} call failed: {e}")
            return None

    # ── Google Gemini ─────────────────────────────────────────────────────

    def _call_gemini(self, market: MarketInfo):
        model = self._get_model("gemini")
        host = (self.config.gemini_api_host or "https://generativelanguage.googleapis.com").rstrip("/")
        url = f"{host}/v1beta/models/{model}:generateContent?key={self.config.gemini_api_key}"
        payload = {
            "systemInstruction": {"parts": [{"text": SYSTEM_PROMPT}]},
            "contents": [{"role": "user", "parts": [{"text": _build_user_prompt(market)}]}],
            "generationConfig": {
                "temperature": self.config.ensemble_temperature,
                "maxOutputTokens": self.config.max_estimate_tokens,
            },
        }
        try:
            resp = self._post_json_with_retry(
                "gemini", url, payload, {"Content-Type": "application/json"})
            if resp is None:
                return None
            resp.raise_for_status()
            data = resp.json()
            text = data["candidates"][0]["content"]["parts"][0]["text"].strip()
            usage = data.get("usageMetadata", {})
            in_tok = usage.get("promptTokenCount", 0)
            out_tok = usage.get("candidatesTokenCount", 0)
            result = self._parse_json_response(text)
            if result is None:
                return None, "", in_tok, out_tok
            prob, reasoning = result
            return prob, reasoning, in_tok, out_tok
        except requests.exceptions.HTTPError as e:
            log.error(f"Gemini API error: {e}")
            return None
        except Exception as e:
            log.debug(f"Gemini call failed: {e}")
            return None

    def _post_json_with_retry(self, provider: str, url: str, payload: dict, headers: dict):
        for attempt in range(4):
            resp = requests.post(url, json=payload, headers=headers, timeout=30)
            if resp.status_code not in (429, 529):
                return resp
            if attempt < 3:
                delay = retry_delay_seconds(resp.headers.get("Retry-After"), attempt)
                log.warning(f"{provider} {resp.status_code} — retrying in {delay:g}s")
                time.sleep(delay)
                continue
            self._mark_rate_limited(provider)
            log.error(f"{provider} {resp.status_code}: skipped for the rest of this cycle")
        return None

    def _mark_rate_limited(self, provider: str) -> None:
        with self._rate_limit_lock:
            self._rate_limited_this_cycle.add(provider)

    # ── API key validation ────────────────────────────────────────────────

    def validate_api_key(self) -> bool:
        """Validate the configured provider's API key (or all providers in multi mode)."""
        if self.config.multi_provider:
            return self._validate_all_providers()
        return self._validate_provider(self._provider)

    def _validate_all_providers(self) -> bool:
        """Validate all configured providers. Returns False only if ALL fail."""
        configured = self._get_configured_providers()
        if not configured:
            log.error("multi_provider=true but no API keys are configured")
            return False
        results = {}
        for provider in configured:
            ok = self._validate_provider(provider)
            results[provider] = ok
            status = "✓" if ok else "✗"
            log.info(f"  {status} {provider}")
        if not any(results.values()):
            log.error("All configured providers failed validation")
            return False
        if not all(results.values()):
            failed = [p for p, ok in results.items() if not ok]
            log.warning(f"Some providers failed: {', '.join(failed)} — continuing with working providers")
        return True

    def _validate_provider(self, provider: str) -> bool:
        try:
            if provider == "anthropic":
                if not self._anthropic_client:
                    return False
                self._anthropic_client.messages.create(
                    model=self._get_model("anthropic"),
                    max_tokens=1,
                    messages=[{"role": "user", "content": "hi"}],
                )
                return True

            elif provider in ("openai", "openrouter", "azure_openai"):
                if provider == "openai":
                    host = (self.config.openai_api_host or "https://api.openai.com").rstrip("/")
                    url = f"{host}/v1/chat/completions"
                    auth_headers = {"Authorization": f"Bearer {self.config.openai_api_key}"}
                elif provider == "openrouter":
                    host = (self.config.openrouter_api_host or "https://openrouter.ai").rstrip("/")
                    url = f"{host}/api/v1/chat/completions"
                    auth_headers = {"Authorization": f"Bearer {self.config.openrouter_api_key}"}
                else:
                    endpoint = self.config.azure_openai_endpoint.rstrip("/")
                    deployment = self.config.azure_openai_deployment
                    version = self.config.azure_openai_api_version or "2024-02-01"
                    url = f"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version}"
                    auth_headers = {"api-key": self.config.azure_openai_api_key}
                resp = requests.post(
                    url,
                    json={"model": self._get_model(provider), "messages": [{"role": "user", "content": "hi"}], "max_tokens": 1},
                    headers={**auth_headers, "Content-Type": "application/json"},
                    timeout=10,
                )
                if resp.status_code in (401, 403):
                    return False
                return True

            elif provider == "gemini":
                host = (self.config.gemini_api_host or "https://generativelanguage.googleapis.com").rstrip("/")
                model = self._get_model("gemini")
                resp = requests.post(
                    f"{host}/v1beta/models/{model}:generateContent?key={self.config.gemini_api_key}",
                    json={"contents": [{"parts": [{"text": "hi"}]}], "generationConfig": {"maxOutputTokens": 1}},
                    headers={"Content-Type": "application/json"},
                    timeout=10,
                )
                if resp.status_code == 403:
                    return False
                if resp.status_code == 400 and "API key" in resp.text:
                    return False
                return True

        except Exception as e:
            if _anthropic is not None and isinstance(e, _anthropic.AuthenticationError):
                return False
            return True  # Network errors don't mean the key is bad

        return True
