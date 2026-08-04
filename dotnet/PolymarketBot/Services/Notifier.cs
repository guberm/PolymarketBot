using System.Text;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using PolymarketBot.Models;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace PolymarketBot.Services;

/// <summary>
/// Sends email notifications for bot state changes.
/// All methods silently swallow errors — a notification failure must never crash the bot.
/// Configure via env vars: EMAIL_ENABLED, EMAIL_SMTP_HOST, EMAIL_SMTP_PORT,
/// EMAIL_SECURITY, EMAIL_USE_TLS, EMAIL_USER, EMAIL_PASSWORD, EMAIL_TO.
/// </summary>
public sealed class Notifier
{
    private readonly BotConfig _config;
    private readonly ILogger<Notifier> _logger;
    private (int Port, bool ImplicitTls)? _smtpSessionAttempt;
    private const int SmtpTimeoutMs = 15000;

    // Header colors by event type
    private const string ColStarted      = "#1e3a5f";
    private const string ColTrade        = "#064e3b";
    private const string ColSellWin      = "#312e81";
    private const string ColSellLoss     = "#7f1d1d";
    private const string ColResolvedWon  = "#064e3b";
    private const string ColResolvedLost = "#7f1d1d";
    private const string ColHalted       = "#7f1d1d";
    private const string ColDaily        = "#1e3a5f";
    private const string ColFail         = "#78350f";
    private const string ColError        = "#7f1d1d";
    private const string ColStopped      = "#374151";
    private const string ColGhost        = "#581c87";

    private const string ColGreen = "#16a34a";
    private const string ColRed   = "#dc2626";
    private const string ColGray  = "#6b7280";
    private const string ColDark  = "#111827";

    public Notifier(BotConfig config, ILogger<Notifier> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool Enabled =>
        _config.EmailEnabled &&
        !string.IsNullOrEmpty(_config.EmailSmtpHost) &&
        !string.IsNullOrEmpty(_config.EmailTo);

    public void Send(string subject, string htmlBody)
    {
        if (!Enabled) return;
        var stage = "message";
        try
        {
            var from = string.IsNullOrEmpty(_config.EmailUser)
                ? $"polymarket-bot@{_config.EmailSmtpHost}"
                : _config.EmailUser;
            var message = CreateMessage(from, _config.EmailTo, subject, htmlBody);

            SmtpClient? connected = null;
            (int Port, bool ImplicitTls)? connectedAttempt = null;
            Exception? lastConnectionError = null;
            var attempts = ConnectionAttempts(_config.EmailSecurity, _config.EmailSmtpPort,
                _config.EmailUseTls, _smtpSessionAttempt?.Port);
            for (var i = 0; i < attempts.Length; i++)
            {
                var attempt = attempts[i];
                var candidate = new SmtpClient { Timeout = SmtpTimeoutMs };
                try
                {
                    stage = $"connect:{attempt.Port}";
                    candidate.Connect(_config.EmailSmtpHost, attempt.Port,
                        attempt.ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
                    connected = candidate;
                    connectedAttempt = attempt;
                    if (i > 0)
                        _logger.LogWarning("SMTP connection unavailable ({Error}); using port {Port}",
                            lastConnectionError?.GetBaseException().Message, attempt.Port);
                    break;
                }
                catch (Exception ex)
                {
                    lastConnectionError = ex;
                    candidate.Dispose();
                }
            }
            if (connected is null) throw lastConnectionError ?? new IOException("SMTP connection failed");

            using var client = connected;
            if (!string.IsNullOrEmpty(_config.EmailUser))
            {
                stage = "authenticate";
                client.Authenticate(_config.EmailUser, _config.EmailPassword);
            }
            client.Capabilities = CapabilitiesForSend(client.Capabilities);
            stage = "send";
            client.Send(message);
            if (_config.EmailSecurity.Equals("auto", StringComparison.OrdinalIgnoreCase))
                _smtpSessionAttempt = connectedAttempt;
            stage = "disconnect";
            client.Disconnect(true);
            _logger.LogDebug("Email sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Email notification failed during {Stage}: {Error}", stage, ex.GetBaseException().Message);
        }
    }

    private static (int Port, bool ImplicitTls)[] ConnectionAttempts(
        string security, int port, bool useTls, int? sessionPort)
    {
        if (security.Equals("starttls", StringComparison.OrdinalIgnoreCase)) return [(587, false)];
        if (security.Equals("ssl", StringComparison.OrdinalIgnoreCase)) return [(465, true)];
        var preferred = sessionPort ?? (port == 465 || !useTls ? 465 : 587);
        return preferred == 465 ? [(465, true), (587, false)] : [(587, false), (465, true)];
    }

    private static MailKit.Net.Smtp.SmtpCapabilities CapabilitiesForSend(
        MailKit.Net.Smtp.SmtpCapabilities capabilities) =>
        capabilities & ~MailKit.Net.Smtp.SmtpCapabilities.Chunking;

    private static MimeMessage CreateMessage(string from, string to, string subject, string htmlBody)
    {
        var message = new MimeMessage
        {
            Subject = $"[Polymarket Bot] {subject}",
            Body = new TextPart("html")
            {
                Text = htmlBody,
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        return message;
    }

    // ── Convenience helpers ──────────────────────────────────────────────

    public void NotifyStarted(string mode, double bankroll, int positions)
    {
        var t = Now();
        var c = _config;

        // AI provider summary
        string providerStr;
        if (c.MultiProvider)
        {
            var enabled = new List<string>();
            if (c.AnthropicEnabled && !string.IsNullOrEmpty(c.AnthropicApiKey))   enabled.Add("anthropic");
            if (c.OpenAiEnabled    && !string.IsNullOrEmpty(c.OpenAiApiKey))      enabled.Add("openai");
            if (c.GeminiEnabled    && !string.IsNullOrEmpty(c.GeminiApiKey))      enabled.Add("gemini");
            if (c.OpenRouterEnabled && !string.IsNullOrEmpty(c.OpenRouterApiKey)) enabled.Add("openrouter");
            if (c.AzureOpenAiEnabled && !string.IsNullOrEmpty(c.AzureOpenAiApiKey)
                && !string.IsNullOrEmpty(c.AzureOpenAiEndpoint)
                && !string.IsNullOrEmpty(c.AzureOpenAiDeployment))               enabled.Add("azure_openai");
            providerStr = $"multi ({(enabled.Count > 0 ? string.Join(", ", enabled) : "none")})";
        }
        else
        {
            providerStr = c.AiProvider;
        }

        var html = BuildHtml("🟢", $"Bot Started — {mode} Mode", ColStarted,
            new[]
            {
                Section("Portfolio",
                    Row("Mode",           mode),
                    Row("Bankroll",       $"${bankroll:F2}"),
                    Row("Open positions", positions.ToString())),
                Section("AI",
                    Row("Provider",       providerStr),
                    Row("Ensemble size",  c.EnsembleSize.ToString()),
                    Row("Temperature",    c.EnsembleTemperature.ToString("F1")),
                    Row("Min edge",       $"{c.MinEdge:P0}")),
                Section("Risk limits",
                    Row("Max position",    $"{c.MaxPositionPct:P0}"),
                    Row("Max exposure",    $"{c.MaxTotalExposurePct:P0}"),
                    Row("Max category",    $"{c.MaxCategoryExposurePct:P0}"),
                    Row("Max event",       $"{c.MaxEventExposurePct:P0}"),
                    Row("Daily stop-loss", $"{c.DailyStopLossPct:P0}"),
                    Row("Max drawdown",    $"{c.MaxDrawdownPct:P0}"),
                    Row("Max positions",   c.MaxConcurrentPositions.ToString()),
                    Row("Kelly fraction",  c.KellyFraction.ToString("F2"))),
                Section("Scan",
                    Row("Interval",       $"{c.ScanIntervalMinutes} min"),
                    Row("Markets/cycle",  c.MarketsPerCycle.ToString()),
                    Row("Min liquidity",  $"${c.MinLiquidity:N0}"),
                    Row("Min volume 24h", $"${c.MinVolume24Hr:N0}"),
                    Row("Max spread",     $"{c.MaxSpread:P0}"))
            }, t);
        Send($"🟢 Started — {mode} mode", html);
    }

    public void NotifyTrade(Trade trade, Signal signal, Portfolio portfolio)
    {
        var t = Now();
        var html = BuildHtml("📈", $"New Position — {trade.Side} {Truncate(trade.Question, 50)}", ColTrade,
            new[] {
                Section("Trade Details",
                    Row("Market", Truncate(trade.Question, 70)),
                    Row("Side", trade.Side.ToString(), ColGreen),
                    Row("Price", $"{trade.Price:F4}"),
                    Row("Size", $"${trade.SizeUsd:F2}"),
                    Row("Shares", $"{trade.Shares:F2}"),
                    Row("Edge", $"{signal.Edge:P1}", ColGreen),
                    Row("Expected value", $"${signal.ExpectedValue:F2}", ColGreen)
                ),
                PortfolioSection(portfolio)
            }, t);
        Send($"📈 BUY {trade.Side} ${trade.SizeUsd:F2} — {Truncate(trade.Question, 60)}", html);
    }

    public void NotifySell(Trade trade, string exitReason, double pnlPct, Portfolio portfolio)
    {
        var t = Now();
        var sign = pnlPct >= 0 ? "+" : "";
        var pnlColor = PnlColor(pnlPct);
        var headerBg = pnlPct >= 0 ? ColSellWin : ColSellLoss;
        var html = BuildHtml("📉", $"Position Closed — {sign}{pnlPct:P1}", headerBg,
            new[] {
                Section("Exit Details",
                    Row("Market", Truncate(trade.Question, 70)),
                    Row("Exit reason", FormatReason(exitReason)),
                    Row("Exit price", $"{trade.Price:F4}"),
                    Row("P&L", $"{sign}{pnlPct:P1}", pnlColor),
                    Row("Recovered", $"${trade.SizeUsd:F2}")
                ),
                PortfolioSection(portfolio)
            }, t);
        Send($"📉 SELL ({exitReason}) {sign}{pnlPct:P1} — {Truncate(trade.Question, 60)}", html);
    }

    public void NotifyTopupSell(Trade trade, TopupCandidate tc, Portfolio portfolio)
    {
        var t = Now();
        var html = BuildHtml("🔄", "Tiny Position Rescued", ColSellWin,
            new[] {
                Section("Top-up & Sell",
                    Row("Market", Truncate(tc.Position.Question, 70)),
                    Row("Exit reason", FormatReason(tc.ExitReason)),
                    Row("Tokens bought (top-up)", $"{tc.TokensToBuy:F0}"),
                    Row("Total tokens sold", $"{tc.Position.Shares + tc.TokensToBuy:F2}"),
                    Row("Top-up cost", $"${tc.TopupCost:F2}", ColRed),
                    Row("Recovered", $"${tc.RecoveryValue:F2}", ColGreen)
                ),
                PortfolioSection(portfolio)
            }, t);
        Send($"🔄 TOPUP+SELL ({tc.ExitReason}) recovered ${tc.RecoveryValue:F2} — {Truncate(tc.Position.Question, 55)}", html);
    }

    public void NotifyResolved(Position pos, bool won, double pnl, Portfolio portfolio)
    {
        var t = Now();
        var result = won ? "WON" : "LOST";
        var icon = won ? "🏆" : "💔";
        var headerBg = won ? ColResolvedWon : ColResolvedLost;
        var payout = won ? pos.Shares : 0.0;
        var html = BuildHtml(icon, $"Market Resolved — {result}", headerBg,
            new[] {
                Section("Resolution",
                    Row("Market", Truncate(pos.Question, 70)),
                    Row("Result", result, won ? ColGreen : ColRed),
                    Row("Payout", $"${payout:F2}"),
                    Row("P&L", $"${pnl:+0.00;-0.00}", PnlColor(pnl)),
                    Row("Shares", $"{pos.Shares:F2}")
                ),
                PortfolioSection(portfolio)
            }, t);
        Send($"{icon} Resolved ({result}) P&L=${pnl:+0.00;-0.00} — {Truncate(pos.Question, 60)}", html);
    }

    public void NotifyGhostRemoved(Position pos, double lossUsd, Portfolio portfolio)
    {
        var t = Now();
        var html = BuildHtml("👻", "Ghost Position Removed", ColGhost,
            new[] {
                Section("Ghost Details",
                    Row("Market", Truncate(pos.Question, 70)),
                    Row("Side", pos.Side.ToString()),
                    Row("Written off", $"${lossUsd:F2}", ColRed),
                    Row("Note", "No on-chain tokens found", ColGray)
                ),
                PortfolioSection(portfolio)
            }, t);
        Send($"👻 Ghost removed — ${lossUsd:F2} written off — {Truncate(pos.Question, 60)}", html);
    }

    public void NotifyHalted(string reason, Portfolio portfolio)
    {
        var t = Now();
        var html = BuildHtml("⛔", "Bot Halted", ColHalted,
            new[] {
                Section("Halt Reason", Row("Reason", reason, ColRed)),
                PortfolioSection(portfolio)
            }, t);
        Send($"⛔ HALTED — {reason}", html);
    }

    public void NotifyDailyReset(Portfolio portfolio)
    {
        var t = Now();
        var pv = portfolio.Equity();
        var html = BuildHtml("🌅", "New Trading Day", ColDaily,
            new[] { Section("Daily Reset",
                Row("Portfolio value", $"${pv:F2}"),
                Row("Bankroll", $"${portfolio.Bankroll:F2}"),
                Row("Exposure", $"${portfolio.TotalExposure():F2}"),
                Row("Open positions", portfolio.Positions.Count.ToString()),
                Row("Cumulative P&L", $"${portfolio.TotalRealizedPnl:+0.00;-0.00}", PnlColor(portfolio.TotalRealizedPnl))
            ) }, t);
        Send($"🌅 Daily reset — portfolio ${pv:F2}", html);
    }

    public void NotifyBuyFail(MarketInfo market, Signal signal, string reason)
    {
        var t = Now();
        var html = BuildHtml("❌", "BUY Order Failed", ColFail,
            new[] { Section("Failed Order",
                Row("Market", Truncate(market.Question, 70)),
                Row("Side", signal.Side.ToString()),
                Row("Attempted price", $"{signal.MarketPrice:F4}"),
                Row("Attempted size", $"${signal.PositionSizeUsd:F2}"),
                Row("Edge", $"{signal.Edge:P1}"),
                Row("Reason", reason, ColRed)
            ) }, t);
        Send($"❌ BUY FAILED {signal.Side} ${signal.PositionSizeUsd:F2} — {Truncate(market.Question, 60)}", html);
    }

    public void NotifySellFail(Position position, string exitReason, string failReason)
    {
        var t = Now();
        var html = BuildHtml("⚠️", "SELL Order Failed", ColFail,
            new[] { Section("Failed Order",
                Row("Market", Truncate(position.Question, 70)),
                Row("Exit reason", FormatReason(exitReason)),
                Row("Attempted price", $"{position.CurrentPrice:F4}"),
                Row("Shares", $"{position.Shares:F2}"),
                Row("Reason", failReason, ColRed)
            ) }, t);
        Send($"⚠️ SELL FAILED ({exitReason}) — {Truncate(position.Question, 60)}", html);
    }

    public void NotifyTopupSellFail(TopupCandidate tc, string failReason)
    {
        var t = Now();
        var html = BuildHtml("⚠️", "Top-up & Sell Failed", ColFail,
            new[] { Section("Failed Operation",
                Row("Market", Truncate(tc.Position.Question, 70)),
                Row("Exit reason", FormatReason(tc.ExitReason)),
                Row("Current tokens", $"{tc.Position.Shares:F2}"),
                Row("Top-up cost", $"${tc.TopupCost:F2}"),
                Row("Reason", failReason, ColRed)
            ) }, t);
        Send($"⚠️ TOPUP+SELL FAILED ({tc.ExitReason}) — {Truncate(tc.Position.Question, 55)}", html);
    }

    public void NotifyError(int cycle, Exception ex)
    {
        var t = Now();
        var html = BuildHtml("🚨", $"Error in Cycle {cycle}", ColError,
            new[] { Section(string.Empty,
                Row("Cycle", cycle.ToString()),
                Row("Error", ex.Message.Length > 200 ? ex.Message[..200] : ex.Message, ColRed)
            ) }, t);
        Send($"🚨 Error in cycle {cycle}", html);
    }

    public void NotifyStopped(Portfolio portfolio)
    {
        var t = Now();
        var pv = portfolio.Equity();
        var pnl = portfolio.TotalRealizedPnl;
        var html = BuildHtml("🛑", "Bot Stopped", ColStopped,
            new[] { Section("Final Summary",
                Row("Portfolio value", $"${pv:F2}"),
                Row("Bankroll", $"${portfolio.Bankroll:F2}"),
                Row("Exposure", $"${portfolio.TotalExposure():F2}"),
                Row("Open positions", portfolio.Positions.Count.ToString()),
                Row("Total trades", portfolio.TotalTrades.ToString()),
                Row("Total API cost", $"${portfolio.TotalApiCost:F4}"),
                Row("Realized P&L", $"${pnl:+0.00;-0.00}", PnlColor(pnl))
            ) }, t);
        Send($"🛑 Stopped — portfolio ${pv:F2}, P&L ${pnl:+0.00;-0.00}", html);
    }

    // ── HTML builder ─────────────────────────────────────────────────────

    private record EmailRow(string Label, string Value, string Color = ColDark);
    private record EmailSection(string Title, EmailRow[] Rows);

    private static EmailRow Row(string label, string value, string color = ColDark)
        => new(label, value, color);

    private static EmailSection Section(string title, params EmailRow[] rows)
        => new(title, rows);

    private static EmailSection PortfolioSection(Portfolio portfolio)
    {
        var pv = portfolio.Equity();
        var pnl = portfolio.TotalRealizedPnl;
        return Section("Portfolio After",
            Row("Portfolio value", $"${pv:F2}"),
            Row("Bankroll", $"${portfolio.Bankroll:F2}"),
            Row("Exposure", $"${portfolio.TotalExposure():F2}"),
            Row("Open positions", portfolio.Positions.Count.ToString()),
            Row("Realized P&L", $"${pnl:+0.00;-0.00}", PnlColor(pnl))
        );
    }

    private static string BuildHtml(string icon, string title, string headerBg,
        IEnumerable<EmailSection> sections, string timeStr)
    {
        var sb = new StringBuilder();
        foreach (var section in sections)
        {
            var rowsSb = new StringBuilder();
            foreach (var row in section.Rows)
            {
                rowsSb.Append(
                    $"<tr>" +
                    $"<td style=\"padding:7px 0;font-size:14px;color:{ColGray};border-bottom:1px solid #f3f4f6;width:45%\">{row.Label}</td>" +
                    $"<td style=\"padding:7px 0;font-size:14px;font-weight:600;color:{row.Color};text-align:right;border-bottom:1px solid #f3f4f6\">{row.Value}</td>" +
                    $"</tr>");
            }
            var titleHtml = string.IsNullOrEmpty(section.Title) ? "" :
                $"<div style=\"font-size:11px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:#9ca3af;margin-bottom:8px;margin-top:4px\">{section.Title}</div>";
            sb.Append(
                $"<div style=\"margin-bottom:16px\">" +
                $"{titleHtml}" +
                $"<table style=\"width:100%;border-collapse:collapse\">{rowsSb}</table>" +
                $"</div>");
        }

        return
            $"<!DOCTYPE html><html><body style=\"margin:0;padding:20px 12px;background:#f3f4f6;" +
            $"font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Arial,sans-serif\">" +
            $"<div style=\"max-width:560px;margin:0 auto\">" +
            $"<div style=\"background:{headerBg};padding:18px 24px;border-radius:10px 10px 0 0\">" +
            $"<div style=\"font-size:20px;font-weight:700;color:#fff;line-height:1.3\">{icon}&nbsp;{title}</div>" +
            $"<div style=\"font-size:12px;color:rgba(255,255,255,.6);margin-top:5px\">Polymarket Bot &middot; {timeStr}</div>" +
            $"</div>" +
            $"<div style=\"background:#fff;padding:24px;border-radius:0 0 10px 10px;box-shadow:0 2px 12px rgba(0,0,0,.07)\">" +
            $"{sb}" +
            $"<div style=\"margin-top:16px;padding-top:14px;border-top:1px solid #f3f4f6;font-size:11px;color:#9ca3af;text-align:center\">" +
            $"Polymarket Bot &middot; {timeStr}" +
            $"</div></div></div></body></html>";
    }

    private static string PnlColor(double value) => value >= 0 ? ColGreen : ColRed;
    private static string FormatReason(string reason) =>
        string.Join(" ", reason.Split('_').Select(w => char.ToUpper(w[0]) + w[1..]));
    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
