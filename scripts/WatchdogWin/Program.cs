using System.Diagnostics;
using System.Text.Json.Nodes;

// Native watchdog: keeps the bot alive. Compiled exe (not powershell) so
// process cleanups that kill all powershell/dotnet instances do not take it
// down with the bot. Install: schtasks /Create /TN PMBWatchdogExe
//   /TR "<path>\PMBWatchdog.exe" /SC ONLOGON /F
var repo = args.Length > 0 ? args[0]
    : @"C:\Users\michael.guber\Desktop\my repos\polymarket_bot";
var log = @"C:\Users\Public\pmb-watchdog.log";
var stamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ";

void W(string msg)
{
    try { File.AppendAllText(log, stamp + msg + Environment.NewLine); }
    catch { /* log is best-effort */ }
}

try
{
    var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "polymarket_bot_config.json")))!.AsObject();
    var proxy = $"{cfg["proxy_type"]}://{cfg["proxy_username"]}:{cfg["proxy_password"]}@{cfg["proxy_host"]}:{cfg["proxy_port"]}";
    Environment.SetEnvironmentVariable("HTTPS_PROXY", proxy);
    Environment.SetEnvironmentVariable("HTTP_PROXY", proxy);
    // optional launch: polymarket_bot_config.json "watchdog_enabled": false
    // makes the watchdog exit immediately (checked first, before anything else).
    bool enabled = true;
    try { enabled = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "polymarket_bot_config.json")))!["watchdog_enabled"]?.GetValue<bool>() ?? true; } catch { }
    if (!enabled) { W("watchdog disabled via config - exiting"); return; }
    W("watchdog started (exe)");
    while (true)
    {
        try
        {
            if (Process.GetProcessesByName("dotnet").Length > 0) { Thread.Sleep(300_000); continue; }
            try { File.Delete(Path.Combine(repo, "data", "bot.lock")); } catch { }
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c cd /d \"{repo}\" && dotnet dotnet\\PolymarketBot\\bin\\Release\\net8.0\\PolymarketBot.dll --console >> \"C:\\Users\\Public\\pmb-native.out\" 2>&1")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = repo });
            W("bot started");
            Thread.Sleep(90_000);
            W(Process.GetProcessesByName("dotnet").Length > 0 ? "bot alive after 90s" : "bot died within 90s - retrying");
        }
        catch (Exception ex) { W($"error: {ex.Message}"); }
        Thread.Sleep(300_000);
    }
}
catch (Exception ex) { W($"fatal: {ex.Message}"); }
