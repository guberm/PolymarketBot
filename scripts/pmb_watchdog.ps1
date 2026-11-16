# PMB watchdog: keeps the Polymarket bot alive.
# Install: schtasks /Create /TN PMBWatchdog /TR "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\Users\Public\pmb_watchdog.ps1" /SC ONLOGON /F
# Manual restart flow: schtasks /End /TN PMBWatchdog -> do work -> schtasks /Run /TN PMBWatchdog
$repo = 'C:\Users\michael.guber\Desktop\my repos\polymarket_bot'
$wlog = 'C:\Users\Public\pmb-watchdog.log'
function W($msg) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $msg" | Out-File $wlog -Append -Encoding utf8 }
Set-Location $repo
W "watchdog started (pid $PID)"

# Compose proxy env once from the bot config (never printed).
$c = Get-Content "$repo\polymarket_bot_config.json" -Raw | ConvertFrom-Json
$env:HTTPS_PROXY = "$($c.proxy_type)://$($c.proxy_username):$($c.proxy_password)@$($c.proxy_host):$($c.proxy_port)"
$env:HTTP_PROXY = $env:HTTPS_PROXY

while ($true) {
  try {
    if (Get-Process dotnet -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 300; continue }

    Remove-Item "$repo\data\bot.lock" -ErrorAction SilentlyContinue
    # Proven launch pattern (mirrors the bridge launcher): cmd /c with append redirect.
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "cd /d `"$repo`" && dotnet dotnet\PolymarketBot\bin\Release\net8.0\PolymarketBot.dll --console >> C:\Users\Public\pmb-native.out 2>&1" -WindowStyle Hidden
    W "bot started"
    Start-Sleep -Seconds 90
    if (Get-Process dotnet -ErrorAction SilentlyContinue) { W "bot alive after 90s" }
    else { W "bot died within 90s of start - will retry on next loop" }
  } catch {
    W "watchdog error: $($_.Exception.Message)"
  }
  Start-Sleep -Seconds 300
}
