"""Research judge: news-based veto before order execution (.NET parity).

Fetches recent news for a market question (Google News RSS), asks the judge
LLM whether the news supports the proposed trade, returns a verdict.
DENY blocks the order; UNCLEAR/CONFIRM let it through (fail-open).
"""
from __future__ import annotations

import json
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass

import httpx


@dataclass
class NewsItem:
    title: str
    source: str
    published: str


@dataclass
class ResearchVerdict:
    verdict: str  # CONFIRM | DENY | UNCLEAR
    confidence: float
    reason: str
    news_count: int


def fetch_news(question: str, max_items: int = 8, timeout: float = 15.0) -> list[NewsItem]:
    q = urllib.parse.quote(question[:120])
    url = f"https://news.google.com/rss/search?q={q}&hl=en-US&gl=US&ceid=US:en"
    items: list[NewsItem] = []
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            root = ET.fromstring(resp.read())
        for item in root.iter("item"):
            title = (item.findtext("title") or "").strip()
            if title:
                items.append(NewsItem(title, (item.findtext("source") or "").strip(),
                                      (item.findtext("pubDate") or "").strip()))
            if len(items) >= max_items:
                break
    except Exception:
        pass
    return items


def build_judge_prompt(market_question: str, side: str, market_price: float,
                       fair_estimate: float, news: list[NewsItem]) -> str:
    lines = [f"Market question: {market_question}",
             f"Our proposed trade: BUY {side} at market price {market_price:.1%} "
             f"(our ensemble fair estimate: {fair_estimate:.1%})",
             "Recent news headlines:"]
    for i, n in enumerate(news, 1):
        lines.append(f"{i}. [{n.source} | {n.published}] {n.title}")
    lines += [
        "",
        "Instructions:",
        "- Base the verdict ONLY on the news items above. No outside knowledge.",
        "- If the news shows the event already happened or the market is clearly stale, verdict = DENY.",
        "- If the news supports our trade direction, verdict = CONFIRM.",
        "- If the news is silent or ambiguous on the question, verdict = UNCLEAR.",
        'Return strict JSON: {"verdict":"CONFIRM|DENY|UNCLEAR","confidence":0.0,"reason":"one sentence"}',
    ]
    return "\n".join(lines)


SYSTEM = ("You are a strict research judge for prediction-market trades. "
          "Answer ONLY with a single JSON object, no prose around it.")


async def judge_async(client: httpx.AsyncClient, api_key: str, model: str,
                      prompt: str, timeout: float = 100.0) -> ResearchVerdict:
    """One anthropic completion; any failure returns UNCLEAR (fail-open)."""
    try:
        resp = await client.post(
            "https://api.anthropic.com/v1/messages",
            headers={"x-api-key": api_key, "anthropic-version": "2023-06-01"},
            json={"model": model, "max_tokens": 512, "temperature": 0.0,
                  "system": SYSTEM, "messages": [{"role": "user", "content": prompt}]},
            timeout=timeout,
        )
        resp.raise_for_status()
        text = resp.json()["content"][0]["text"].strip()
        return parse_verdict(text, 0)
    except Exception as exc:  # noqa: BLE001 — fail-open is the contract
        return ResearchVerdict("UNCLEAR", 0.0, f"research judge unavailable: {exc}", 0)


def judge_sync(config, market_question: str, side: str, market_price: float,
               fair_estimate: float, news: list[NewsItem]) -> ResearchVerdict:
    """Synchronous one-shot judge (the live loop is sync). Fail-open to UNCLEAR."""
    try:
        prompt = build_judge_prompt(market_question, side, market_price, fair_estimate, news)
        model = getattr(config, "anthropic_model", None) or "claude-sonnet-4-6"
        data = {
            "model": model, "max_tokens": 512, "temperature": 0.0,
            "system": SYSTEM, "messages": [{"role": "user", "content": prompt}],
        }
        req = urllib.request.Request(
            "https://api.anthropic.com/v1/messages",
            data=json.dumps(data).encode(),
            headers={"x-api-key": config.anthropic_api_key,
                     "anthropic-version": "2023-06-01",
                     "content-type": "application/json"},
        )
        with urllib.request.urlopen(req, timeout=100) as resp:
            body = json.loads(resp.read())
        text = body["content"][0]["text"].strip()
        return parse_verdict(text, len(news))
    except Exception as exc:  # noqa: BLE001 — fail-open is the contract
        return ResearchVerdict("UNCLEAR", 0.0, f"research judge unavailable: {exc}", len(news))


def parse_verdict(answer: str, news_count: int) -> ResearchVerdict:
    try:
        start, end = answer.find("{"), answer.rfind("}")
        if start < 0 or end <= start:
            return ResearchVerdict("UNCLEAR", 0.0, "judge returned no JSON", news_count)
        data = json.loads(answer[start:end + 1])
        verdict = str(data.get("verdict", "UNCLEAR")).upper()
        if verdict not in ("CONFIRM", "DENY", "UNCLEAR"):
            verdict = "UNCLEAR"
        confidence = float(data.get("confidence", 0.5))
        confidence = max(0.0, min(1.0, confidence))
        return ResearchVerdict(verdict, confidence, str(data.get("reason", "")), news_count)
    except Exception:
        return ResearchVerdict("UNCLEAR", 0.0, "judge JSON unparseable", news_count)
