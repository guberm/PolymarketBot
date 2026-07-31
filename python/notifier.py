"""Email notifications for bot state changes.

Configure via env vars:
    EMAIL_ENABLED=true
    EMAIL_SMTP_HOST=smtp.gmail.com
    EMAIL_SMTP_PORT=587
    EMAIL_USE_TLS=true         (STARTTLS; set false for SMTP_SSL on port 465)
    EMAIL_USER=mybot@gmail.com
    EMAIL_PASSWORD=app_password
    EMAIL_TO=me@example.com
"""

import logging
import re
import smtplib
import ssl
from datetime import datetime
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText

log = logging.getLogger("bot.notifier")

# ── Header colors by event type ──────────────────────────────────────────────
_COLORS = {
    "started":       "#1e3a5f",   # dark blue
    "trade":         "#064e3b",   # dark green
    "sell_win":      "#312e81",   # dark indigo
    "sell_loss":     "#7f1d1d",   # dark red
    "topup":         "#312e81",   # dark indigo
    "resolved_won":  "#064e3b",   # dark green
    "resolved_lost": "#7f1d1d",   # dark red
    "halted":        "#7f1d1d",   # dark red
    "daily":         "#1e3a5f",   # dark blue
    "fail":          "#78350f",   # dark amber
    "error":         "#7f1d1d",   # dark red
    "stopped":       "#374151",   # gray
    "ghost":         "#581c87",   # dark purple
}

_GREEN = "#16a34a"
_RED   = "#dc2626"
_GRAY  = "#6b7280"
_DARK  = "#111827"


def _pnl_color(value: float) -> str:
    return _GREEN if value >= 0 else _RED


def _build_html(icon: str, title: str, header_bg: str, sections: list, time_str: str) -> str:
    """Build a clean HTML email body with sections."""
    sections_html = ""
    for section in sections:
        rows_html = ""
        for row in section.get("rows", []):
            label = row[0]
            value = str(row[1])
            color = row[2] if len(row) > 2 else _DARK
            rows_html += (
                f'<tr>'
                f'<td style="padding:7px 0;font-size:14px;color:{_GRAY};'
                f'border-bottom:1px solid #f3f4f6;width:45%">{label}</td>'
                f'<td style="padding:7px 0;font-size:14px;font-weight:600;color:{color};'
                f'text-align:right;border-bottom:1px solid #f3f4f6">{value}</td>'
                f'</tr>'
            )
        sec_title = section.get("title", "")
        title_html = (
            f'<div style="font-size:11px;font-weight:700;letter-spacing:.06em;'
            f'text-transform:uppercase;color:#9ca3af;margin-bottom:8px;margin-top:4px">'
            f'{sec_title}</div>'
        ) if sec_title else ""
        sections_html += (
            f'<div style="margin-bottom:16px">'
            f'{title_html}'
            f'<table style="width:100%;border-collapse:collapse">{rows_html}</table>'
            f'</div>'
        )

    return (
        f'<!DOCTYPE html><html><body style="margin:0;padding:20px 12px;background:#f3f4f6;'
        f'font-family:-apple-system,BlinkMacSystemFont,\'Segoe UI\',Arial,sans-serif">'
        f'<div style="max-width:560px;margin:0 auto">'
        f'<div style="background:{header_bg};padding:18px 24px;border-radius:10px 10px 0 0">'
        f'<div style="font-size:20px;font-weight:700;color:#fff;line-height:1.3">{icon}&nbsp;{title}</div>'
        f'<div style="font-size:12px;color:rgba(255,255,255,.6);margin-top:5px">'
        f'Polymarket Bot &middot; {time_str}</div>'
        f'</div>'
        f'<div style="background:#fff;padding:24px;border-radius:0 0 10px 10px;'
        f'box-shadow:0 2px 12px rgba(0,0,0,.07)">'
        f'{sections_html}'
        f'<div style="margin-top:16px;padding-top:14px;border-top:1px solid #f3f4f6;'
        f'font-size:11px;color:#9ca3af;text-align:center">'
        f'Polymarket Bot &middot; {time_str}'
        f'</div>'
        f'</div>'
        f'</div>'
        f'</body></html>'
    )


def _portfolio_section(portfolio) -> dict:
    pv = portfolio.equity()
    pnl = portfolio.total_realized_pnl
    return {
        "title": "Portfolio After",
        "rows": [
            ("Portfolio value", f"${pv:.2f}", _DARK),
            ("Bankroll",        f"${portfolio.bankroll:.2f}", _DARK),
            ("Exposure",        f"${portfolio.total_exposure():.2f}", _DARK),
            ("Open positions",  str(len(portfolio.positions)), _DARK),
            ("Realized P&L",   f"${pnl:+.2f}", _pnl_color(pnl)),
        ],
    }


def _html_to_plain(html: str) -> str:
    """Strip HTML tags for plain-text fallback."""
    text = re.sub(r'<[^>]+>', ' ', html)
    text = re.sub(r'&middot;', '·', text)
    text = re.sub(r'&nbsp;', ' ', text)
    text = re.sub(r'[ \t]+', ' ', text)
    text = re.sub(r'\n{3,}', '\n\n', text)
    return text.strip()


class Notifier:
    """Sends HTML email notifications for bot state changes.

    All send methods silently swallow errors — a notification failure must
    never crash the bot.
    """

    def __init__(self, config):
        self._config = config

    @property
    def enabled(self) -> bool:
        return (
            self._config.email_enabled
            and bool(self._config.email_smtp_host)
            and bool(self._config.email_to)
        )

    def send(self, subject: str, html_body: str) -> None:
        """Send an HTML email with plain-text fallback. No-op if disabled."""
        if not self.enabled:
            return
        cfg = self._config
        try:
            msg = MIMEMultipart("alternative")
            msg["Subject"] = f"[Polymarket Bot] {subject}"
            msg["From"] = cfg.email_user or f"polymarket-bot@{cfg.email_smtp_host}"
            msg["To"] = cfg.email_to
            msg.attach(MIMEText(_html_to_plain(html_body), "plain"))
            msg.attach(MIMEText(html_body, "html"))

            context = ssl.create_default_context()
            if cfg.email_use_tls:
                smtp = None
                try:
                    smtp = smtplib.SMTP(cfg.email_smtp_host, cfg.email_smtp_port, timeout=5)
                    smtp.ehlo()
                    smtp.starttls(context=context)
                    smtp.ehlo()
                except (OSError, smtplib.SMTPException) as primary_error:
                    if cfg.email_smtp_port != 587:
                        raise
                    if smtp is not None:
                        smtp.close()
                    log.warning(f"SMTP 587 unavailable ({primary_error}); retrying with implicit TLS on 465")
                    smtp = smtplib.SMTP_SSL(cfg.email_smtp_host, 465, timeout=5, context=context)
                with smtp:
                    if cfg.email_user and cfg.email_password:
                        smtp.login(cfg.email_user, cfg.email_password)
                    smtp.sendmail(msg["From"], [cfg.email_to], msg.as_string())
            else:
                with smtplib.SMTP_SSL(cfg.email_smtp_host, cfg.email_smtp_port, timeout=5, context=context) as smtp:
                    if cfg.email_user and cfg.email_password:
                        smtp.login(cfg.email_user, cfg.email_password)
                    smtp.sendmail(msg["From"], [cfg.email_to], msg.as_string())

            log.debug(f"Email sent: {subject}")
        except Exception as e:
            log.warning(f"Email notification failed: {e}")

    # ── Notification methods ───────────────────────────────────────────────

    def notify_started(self, mode: str, bankroll: float, positions: int) -> None:
        c = self._config
        t = _now()

        # AI provider summary
        if c.multi_provider:
            enabled = []
            if c.anthropic_enabled and c.anthropic_api_key:   enabled.append("anthropic")
            if c.openai_enabled and c.openai_api_key:         enabled.append("openai")
            if c.gemini_enabled and c.gemini_api_key:         enabled.append("gemini")
            if c.openrouter_enabled and c.openrouter_api_key: enabled.append("openrouter")
            if c.azure_openai_enabled and c.azure_openai_api_key and c.azure_openai_endpoint and c.azure_openai_deployment:
                enabled.append("azure_openai")
            provider_str = f"multi ({', '.join(enabled) or 'none'})"
        else:
            provider_str = c.ai_provider

        html = _build_html(
            icon="🟢", title=f"Bot Started — {mode} Mode",
            header_bg=_COLORS["started"],
            sections=[
                {"title": "Portfolio", "rows": [
                    ("Mode",           mode,                       _DARK),
                    ("Bankroll",       f"${bankroll:.2f}",         _DARK),
                    ("Open positions", str(positions),             _DARK),
                ]},
                {"title": "AI", "rows": [
                    ("Provider",       provider_str,               _DARK),
                    ("Ensemble size",  str(c.ensemble_size),       _DARK),
                    ("Temperature",    str(c.ensemble_temperature), _DARK),
                    ("Min edge",       f"{c.min_edge:.0%}",        _DARK),
                ]},
                {"title": "Risk limits", "rows": [
                    ("Max position",     f"{c.max_position_pct:.0%}",         _DARK),
                    ("Max exposure",     f"{c.max_total_exposure_pct:.0%}",   _DARK),
                    ("Max category",     f"{c.max_category_exposure_pct:.0%}", _DARK),
                    ("Max event",        f"{c.max_event_exposure_pct:.0%}",    _DARK),
                    ("Daily stop-loss",  f"{c.daily_stop_loss_pct:.0%}",      _DARK),
                    ("Max drawdown",     f"{c.max_drawdown_pct:.0%}",         _DARK),
                    ("Max positions",    str(c.max_concurrent_positions),     _DARK),
                    ("Kelly fraction",   str(c.kelly_fraction),               _DARK),
                ]},
                {"title": "Scan", "rows": [
                    ("Interval",      f"{c.scan_interval_minutes} min",       _DARK),
                    ("Markets/cycle", str(c.markets_per_cycle),               _DARK),
                    ("Min liquidity", f"${c.min_liquidity:,.0f}",             _DARK),
                    ("Min volume 24h",f"${c.min_volume_24hr:,.0f}",           _DARK),
                    ("Max spread",    f"{c.max_spread:.0%}",                  _DARK),
                ]},
            ],
            time_str=t,
        )
        self.send(f"🟢 Started — {mode} mode", html)

    def notify_trade(self, trade, signal, portfolio) -> None:
        t = _now()
        html = _build_html(
            icon="📈", title=f"New Position — {trade.side.value} {trade.question[:50]}",
            header_bg=_COLORS["trade"],
            sections=[
                {"title": "Trade Details", "rows": [
                    ("Market", trade.question[:70], _DARK),
                    ("Side", trade.side.value, _GREEN),
                    ("Price", f"{trade.price:.4f}", _DARK),
                    ("Size", f"${trade.size_usd:.2f}", _DARK),
                    ("Shares", f"{trade.shares:.2f}", _DARK),
                    ("Edge", f"{signal.edge:.1%}", _GREEN),
                    ("Expected value", f"${signal.expected_value:.2f}", _GREEN),
                ]},
                _portfolio_section(portfolio),
            ],
            time_str=t,
        )
        self.send(f"📈 BUY {trade.side.value} ${trade.size_usd:.2f} — {trade.question[:60]}", html)

    def notify_sell(self, trade, exit_reason: str, pnl_pct: float, portfolio) -> None:
        t = _now()
        sign = "+" if pnl_pct >= 0 else ""
        pnl_color = _pnl_color(pnl_pct)
        header_bg = _COLORS["sell_win"] if pnl_pct >= 0 else _COLORS["sell_loss"]
        html = _build_html(
            icon="📉", title=f"Position Closed — {sign}{pnl_pct:.1%}",
            header_bg=header_bg,
            sections=[
                {"title": "Exit Details", "rows": [
                    ("Market", trade.question[:70], _DARK),
                    ("Exit reason", exit_reason.replace("_", " ").title(), _DARK),
                    ("Exit price", f"{trade.price:.4f}", _DARK),
                    ("P&L", f"{sign}{pnl_pct:.1%}", pnl_color),
                    ("Recovered", f"${trade.size_usd:.2f}", _DARK),
                ]},
                _portfolio_section(portfolio),
            ],
            time_str=t,
        )
        self.send(f"📉 SELL ({exit_reason}) {sign}{pnl_pct:.1%} — {trade.question[:60]}", html)

    def notify_topup_sell(self, trade, tc, portfolio) -> None:
        t = _now()
        html = _build_html(
            icon="🔄", title="Tiny Position Rescued",
            header_bg=_COLORS["topup"],
            sections=[
                {"title": "Top-up & Sell", "rows": [
                    ("Market", tc.position.question[:70], _DARK),
                    ("Exit reason", tc.exit_reason.replace("_", " ").title(), _DARK),
                    ("Tokens bought (top-up)", f"{tc.tokens_to_buy:.0f}", _DARK),
                    ("Total tokens sold", f"{tc.position.shares + tc.tokens_to_buy:.2f}", _DARK),
                    ("Top-up cost", f"${tc.topup_cost:.2f}", _RED),
                    ("Recovered", f"${tc.recovery_value:.2f}", _GREEN),
                ]},
                _portfolio_section(portfolio),
            ],
            time_str=t,
        )
        self.send(f"🔄 TOPUP+SELL ({tc.exit_reason}) recovered ${tc.recovery_value:.2f} — {tc.position.question[:55]}", html)

    def notify_resolved(self, position, won: bool, pnl: float, portfolio) -> None:
        t = _now()
        result = "WON" if won else "LOST"
        icon = "🏆" if won else "💔"
        header_bg = _COLORS["resolved_won"] if won else _COLORS["resolved_lost"]
        payout = position.shares if won else 0.0
        html = _build_html(
            icon=icon, title=f"Market Resolved — {result}",
            header_bg=header_bg,
            sections=[
                {"title": "Resolution", "rows": [
                    ("Market", position.question[:70], _DARK),
                    ("Result", result, _GREEN if won else _RED),
                    ("Payout", f"${payout:.2f}", _DARK),
                    ("P&L", f"${pnl:+.2f}", _pnl_color(pnl)),
                    ("Shares", f"{position.shares:.2f}", _DARK),
                ]},
                _portfolio_section(portfolio),
            ],
            time_str=t,
        )
        self.send(f"{icon} Resolved ({result}) P&L=${pnl:+.2f} — {position.question[:60]}", html)

    def notify_ghost_removed(self, position, loss_usd: float, portfolio) -> None:
        t = _now()
        html = _build_html(
            icon="👻", title="Ghost Position Removed",
            header_bg=_COLORS["ghost"],
            sections=[
                {"title": "Ghost Details", "rows": [
                    ("Market", position.question[:70], _DARK),
                    ("Side", position.side.value, _DARK),
                    ("Written off", f"${loss_usd:.2f}", _RED),
                    ("Note", "No on-chain tokens found", _GRAY),
                ]},
                _portfolio_section(portfolio),
            ],
            time_str=t,
        )
        self.send(f"👻 Ghost removed — ${loss_usd:.2f} written off — {position.question[:60]}", html)

    def notify_halted(self, reason: str, portfolio) -> None:
        t = _now()
        html = _build_html(
            icon="⛔", title="Bot Halted",
            header_bg=_COLORS["halted"],
            sections=[
                {"title": "Halt Reason", "rows": [("Reason", reason, _RED)]},
                _portfolio_section(portfolio),
            ],
            time_str=t,
        )
        self.send(f"⛔ HALTED — {reason}", html)

    def notify_daily_reset(self, portfolio) -> None:
        t = _now()
        pv = portfolio.equity()
        html = _build_html(
            icon="🌅", title="New Trading Day",
            header_bg=_COLORS["daily"],
            sections=[{"title": "Daily Reset", "rows": [
                ("Portfolio value", f"${pv:.2f}", _DARK),
                ("Bankroll",        f"${portfolio.bankroll:.2f}", _DARK),
                ("Exposure",        f"${portfolio.total_exposure():.2f}", _DARK),
                ("Open positions",  str(len(portfolio.positions)), _DARK),
                ("Cumulative P&L", f"${portfolio.total_realized_pnl:+.2f}", _pnl_color(portfolio.total_realized_pnl)),
            ]}],
            time_str=t,
        )
        self.send(f"🌅 Daily reset — portfolio ${pv:.2f}", html)

    def notify_buy_fail(self, market, signal, reason: str) -> None:
        t = _now()
        html = _build_html(
            icon="❌", title="BUY Order Failed",
            header_bg=_COLORS["fail"],
            sections=[{"title": "Failed Order", "rows": [
                ("Market", market.question[:70], _DARK),
                ("Side", signal.side.value, _DARK),
                ("Attempted price", f"{signal.market_price:.4f}", _DARK),
                ("Attempted size", f"${signal.position_size_usd:.2f}", _DARK),
                ("Edge", f"{signal.edge:.1%}", _DARK),
                ("Reason", reason, _RED),
            ]}],
            time_str=t,
        )
        self.send(f"❌ BUY FAILED {signal.side.value} ${signal.position_size_usd:.2f} — {market.question[:60]}", html)

    def notify_sell_fail(self, position, exit_reason: str, fail_reason: str) -> None:
        t = _now()
        html = _build_html(
            icon="⚠️", title="SELL Order Failed",
            header_bg=_COLORS["fail"],
            sections=[{"title": "Failed Order", "rows": [
                ("Market", position.question[:70], _DARK),
                ("Exit reason", exit_reason.replace("_", " ").title(), _DARK),
                ("Attempted price", f"{position.current_price:.4f}", _DARK),
                ("Shares", f"{position.shares:.2f}", _DARK),
                ("Reason", fail_reason, _RED),
            ]}],
            time_str=t,
        )
        self.send(f"⚠️ SELL FAILED ({exit_reason}) — {position.question[:60]}", html)

    def notify_topup_sell_fail(self, tc, fail_reason: str) -> None:
        t = _now()
        html = _build_html(
            icon="⚠️", title="Top-up & Sell Failed",
            header_bg=_COLORS["fail"],
            sections=[{"title": "Failed Operation", "rows": [
                ("Market", tc.position.question[:70], _DARK),
                ("Exit reason", tc.exit_reason.replace("_", " ").title(), _DARK),
                ("Current tokens", f"{tc.position.shares:.2f}", _DARK),
                ("Top-up cost", f"${tc.topup_cost:.2f}", _DARK),
                ("Reason", fail_reason, _RED),
            ]}],
            time_str=t,
        )
        self.send(f"⚠️ TOPUP+SELL FAILED ({tc.exit_reason}) — {tc.position.question[:55]}", html)

    def notify_error(self, cycle: int, error: Exception) -> None:
        t = _now()
        html = _build_html(
            icon="🚨", title=f"Error in Cycle {cycle}",
            header_bg=_COLORS["error"],
            sections=[{"rows": [
                ("Cycle", str(cycle), _DARK),
                ("Error", str(error)[:200], _RED),
            ]}],
            time_str=t,
        )
        self.send(f"🚨 Error in cycle {cycle}", html)

    def notify_stopped(self, portfolio) -> None:
        t = _now()
        pv = portfolio.equity()
        pnl = portfolio.total_realized_pnl
        html = _build_html(
            icon="🛑", title="Bot Stopped",
            header_bg=_COLORS["stopped"],
            sections=[{"title": "Final Summary", "rows": [
                ("Portfolio value", f"${pv:.2f}", _DARK),
                ("Bankroll",        f"${portfolio.bankroll:.2f}", _DARK),
                ("Exposure",        f"${portfolio.total_exposure():.2f}", _DARK),
                ("Open positions",  str(len(portfolio.positions)), _DARK),
                ("Total trades",    str(portfolio.total_trades), _DARK),
                ("Total API cost",  f"${portfolio.total_api_cost:.4f}", _DARK),
                ("Realized P&L",   f"${pnl:+.2f}", _pnl_color(pnl)),
            ]}],
            time_str=t,
        )
        self.send(f"🛑 Stopped — portfolio ${pv:.2f}, P&L ${pnl:+.2f}", html)


def _now() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")
