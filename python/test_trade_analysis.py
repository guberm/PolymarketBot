"""Tests for the trade-rationale audit (mirror of .NET self-test fixture)."""
import pytest
from trade_analysis import analyze


CLANCY_ESTIMATES = {"anthropic": 0.04, "azure_openai": 0.39, "custom": 0.20, "jev": 0.05}
CLANCY_HISTORY = {
    "custom": [0.39, 0.22, 0.39, 0.20],
    "anthropic": [0.04, 0.05, 0.04, 0.05],
}


def test_classification_and_verdicts():
    providers, verdicts = analyze(0.385, CLANCY_ESTIMATES, CLANCY_HISTORY, 78_582.0, 18_156.0)
    by_name = {p.provider: p for p in providers}
    assert by_name["azure_openai"].classification == "ECHO"
    assert by_name["anthropic"].classification == "INDEPENDENT"
    assert by_name["jev"].classification == "INDEPENDENT"
    assert by_name["custom"].classification.startswith("UNSTABLE")
    assert any("PRIOR_VS_MARKET" in v for v in verdicts)
    assert any("UNSTABLE: custom" in v for v in verdicts)
    assert not any("THIN_MARKET" in v for v in verdicts)


def test_thin_market_flagged():
    _, verdicts = analyze(0.5, {"a": 0.4}, None, 500.0, 100.0)
    assert any("THIN_MARKET" in v for v in verdicts)


def test_no_disagreement_no_verdicts():
    _, verdicts = analyze(0.5, {"a": 0.51, "b": 0.49}, None, 78_582.0, 18_156.0)
    assert verdicts == []


def test_single_voice():
    _, verdicts = analyze(0.5, {"echo1": 0.51, "echo2": 0.49, "solo": 0.30}, None, 78_582.0, 18_156.0)
    assert any("SINGLE_VOICE: only solo" in v for v in verdicts)
