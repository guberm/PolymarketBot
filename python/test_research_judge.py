"""Tests for the research judge mirror (parse + prompt + fail-open)."""
import pytest

from research_judge import NewsItem, ResearchVerdict, build_judge_prompt, parse_verdict


def test_parse_denay():
    v = parse_verdict('{"verdict":"DENY","confidence":0.9,"reason":"event already happened"}', 8)
    assert v.verdict == "DENY"
    assert v.confidence == pytest.approx(0.9)
    assert v.news_count == 8


def test_parse_confirm_with_prose():
    v = parse_verdict('Sure! {"verdict":"CONFIRM","confidence":0.7,"reason":"news supports"}', 5)
    assert v.verdict == "CONFIRM"


def test_parse_garbage_fails_open():
    v = parse_verdict("I cannot answer in JSON.", 3)
    assert v.verdict == "UNCLEAR"
    assert v.confidence == 0.0


def test_parse_invalid_verdict_clamped():
    v = parse_verdict('{"verdict":"MAYBE","confidence":2.0,"reason":"x"}', 2)
    assert v.verdict == "UNCLEAR"
    assert v.confidence == pytest.approx(1.0)


def test_prompt_contains_news_and_rules():
    news = [NewsItem("Anthropic raises $10B", "Reuters", "Mon, 17 Nov 2026"), NewsItem("x", "y", "z")]
    p = build_judge_prompt("Will Anthropic's market cap hit $400B?", "NO", 0.35, 0.20, news)
    assert "Anthropic raises $10B" in p
    assert "BUY NO" in p
    assert "verdict = DENY" in p
    assert "No outside knowledge" in p


def test_news_fail_open_verdict():
    v = ResearchVerdict("UNCLEAR", 0.0, "no recent news found", 0)
    assert v.verdict == "UNCLEAR"
