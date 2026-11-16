using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace PolymarketDashboard;

/// <summary>Native WinForms replacement for the Electron dashboard core:
/// process/status view, live log tail, start/stop (watchdog-aware) and the
/// trading-knob settings editor. API-key editing stays in the old Electron
/// dashboard or hand-editing of polymarket_bot_config.json.</summary>
public sealed class MainForm : Form
{
    private const string Repo = @"C:\Users\michael.guber\Desktop\my repos\polymarket_bot";
    private const string LogPath = @"C:\Users\Public\pmb-native.out";
    private static readonly string ConfigPath = Path.Combine(Repo, "polymarket_bot_config.json");
    private static readonly string PortfolioPath = Path.Combine(Repo, "data", "portfolio.json");

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private long _logPos;

    private readonly Label _lblProcess = new() { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
    private readonly Label _lblWatchdog = new() { AutoSize = true };
    private readonly ListBox _lstStatus = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9F), ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(20, 20, 24), ForeColor = Color.FromArgb(200, 210, 200),
    };
    private readonly Dictionary<string, TextBox> _num = new();
    private readonly Dictionary<string, CheckBox> _chk = new();

    public MainForm()
    {
        Text = "PolymarketBot — native dashboard";
        MinimumSize = new Size(1000, 660);
        Size = new Size(1120, 740);
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(8), WrapContents = false };
        top.Controls.Add(new Label { Text = "Process:", AutoSize = true });
        top.Controls.Add(_lblProcess);
        top.Controls.Add(new Label { Text = "    Watchdog:", AutoSize = true });
        top.Controls.Add(_lblWatchdog);
        top.Controls.Add(new Label { Text = "    ", AutoSize = true });
        top.Controls.Add(MakeButton("▶ Start bot", StartBot));
        top.Controls.Add(MakeButton("■ Stop bot", StopBot));
        top.Controls.Add(MakeButton("■ Stop watchdog too", () => RunHidden("schtasks", "/End /TN PMBWatchdog")));

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 230, ColumnCount = 5, AutoSize = true };
        var lblHint = new Label
        {
            Text = "Trading settings (Save rewrites config; Start/Stop applies)",
            AutoSize = true,
        };
        bottom.Controls.Add(lblHint, 0, 0);
        bottom.SetColumnSpan(lblHint, 5);
        AddNum(bottom, "min_edge", "Min Edge", 1);
        AddNum(bottom, "max_estimate_std", "Max Std", 2);
        AddNum(bottom, "markets_per_cycle", "Markets/Cycle", 3);
        AddNum(bottom, "scan_interval_minutes", "Scan Interval (min)", 4);
        AddChk(bottom, "jev_enabled", "Jev", 1);
        AddChk(bottom, "custom_enabled", "Custom (TokenHarbor)", 2);
        AddChk(bottom, "live_trading", "Live Trading", 3);
        var btnSave = MakeButton("💾 Save settings", SaveSettings);
        bottom.Controls.Add(btnSave, 4, 4);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 380 };
        split.Panel1.Controls.Add(_lstStatus);
        split.Panel2.Controls.Add(_txtLog);
        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);

        Load += (_, _) => { LoadSettings(); RefreshStatus(); };
        _timer.Tick += (_, _) => { RefreshStatus(); TailLog(); };
        _timer.Start();
        TailLog(initial: true);
    }

    private void AddNum(TableLayoutPanel p, string key, string label, int row)
    {
        p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var tb = new TextBox { Width = 90, Anchor = AnchorStyles.Left };
        _num[key] = tb;
        p.Controls.Add(tb, 1, row);
    }

    private void AddChk(TableLayoutPanel p, string key, string label, int col)
    {
        var cb = new CheckBox { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
        _chk[key] = cb;
        p.Controls.Add(cb, col, 4);
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ── Settings ───────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            foreach (var (key, tb) in _num)
                if (node[key] is { } v) tb.Text = v.ToString();
            foreach (var (key, cb) in _chk)
                if (node[key] is { } v) cb.Checked = v.GetValue<bool>();
        }
        catch (Exception ex) { MessageBox.Show($"Config load failed: {ex.Message}"); }
    }

    private void SaveSettings()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            foreach (var (key, tb) in _num)
                if (double.TryParse(tb.Text, out var d)) node[key] = d;
            foreach (var (key, cb) in _chk)
                node[key] = cb.Checked;
            File.WriteAllText(ConfigPath,
                node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));  // no BOM
            MessageBox.Show("Saved. Restart the bot (Stop → Start) to apply.", "Settings");
        }
        catch (Exception ex) { MessageBox.Show($"Save failed: {ex.Message}"); }
    }

    // ── Process control ────────────────────────────────────────────────

    private static void RunHidden(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });

    private void StartBot()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            var proxy = $"{node["proxy_type"]}://{node["proxy_username"]}:{node["proxy_password"]}@{node["proxy_host"]}:{node["proxy_port"]}";
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c cd /d \"{Repo}\" && set \"HTTPS_PROXY={proxy}\" && set \"HTTP_PROXY={proxy}\" && " +
                $"dotnet dotnet\\PolymarketBot\\bin\\Release\\net8.0\\PolymarketBot.dll --console >> \"{LogPath}\" 2>&1")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Repo,
            });
            RunHidden("schtasks", "/Run /TN PMBWatchdog");  // revive the watchdog if it was ended
        }
        catch (Exception ex) { MessageBox.Show($"Start failed: {ex.Message}"); }
    }

    private void StopBot()
    {
        RunHidden("schtasks", "/End /TN PMBWatchdog");  // keep the watchdog from reviving it
        foreach (var p in Process.GetProcessesByName("dotnet")) p.Kill(true);
    }

    // ── Status + log tail ──────────────────────────────────────────────

    private void RefreshStatus()
    {
        var dotnet = Process.GetProcessesByName("dotnet");
        _lblProcess.Text = dotnet.Length > 0
            ? $"RUNNING (pid {dotnet[0].Id}, since {dotnet[0].StartTime:HH:mm})"
            : "STOPPED";
        _lblProcess.ForeColor = dotnet.Length > 0 ? Color.Green : Color.Red;

        try
        {
            var taskOut = RunCapture("schtasks", "/Query /TN PMBWatchdog /FO LIST");
            var running = taskOut.Split('\n').Any(l => l.Trim().StartsWith("Status:") && l.Contains("Running"));
            _lblWatchdog.Text = running ? "Running" : "Not running";
        }
        catch { _lblWatchdog.Text = "?"; }

        try
        {
            _lstStatus.BeginUpdate();
            _lstStatus.Items.Clear();
            var node = JsonNode.Parse(File.ReadAllText(PortfolioPath))!.AsObject();
            var bankroll = node["bankroll"]?.GetValue<double>() ?? 0;
            var positions = node["positions"]?.AsArray() ?? new JsonArray();
            double exposure = 0, unrealized = 0;
            foreach (var p in positions)
            {
                var o = p!.AsObject();
                var q = o["question"]?.GetValue<string>() ?? "?";
                exposure += o["size_usd"]?.GetValue<double>() ?? 0;
                var pnl = o["unrealized_pnl"]?.GetValue<double>() ?? 0;
                unrealized += pnl;
                _lstStatus.Items.Add(
                    $"  {q[..Math.Min(58, q.Length)]} | {o["side"]} {o["shares"]} @ {o["entry_price"]:0.00} → {o["current_price"]:0.00} | PnL {pnl:+0.00;-0.00}");
            }
            _lstStatus.Items.Insert(0,
                $"Bankroll ${bankroll:0.00} | {positions.Count} pos | exposure ${exposure:0.00} | unrealized {unrealized:+0.00;-0.00}");
            _lstStatus.EndUpdate();
        }
        catch { /* portfolio.json mid-write — the next tick retries */ }
    }

    private static string RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        return p.StandardOutput.ReadToEnd();
    }

    private void TailLog(bool initial = false)
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _logPos) { _logPos = 0; if (!initial) _txtLog.Clear(); }
            fs.Seek(_logPos, SeekOrigin.Begin);
            var toRead = (int)Math.Min(fs.Length - _logPos, initial ? 40_000 : 200_000);
            if (toRead <= 0 && !initial) return;
            var chunk = new byte[toRead];
            var read = fs.Read(chunk, 0, toRead);
            _logPos += read;
            var text = Encoding.UTF8.GetString(chunk, 0, read).Replace("\0", "");
            if (initial)
            {
                _txtLog.Text = text;
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            }
            else if (text.Length > 0)
            {
                _txtLog.AppendText(text);
                if (_txtLog.TextLength > 400_000) _txtLog.Text = _txtLog.Text[^300_000..];
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            }
        }
        catch (IOException) { /* log held mid-write — the next tick retries */ }
    }
}
