# scripts/build.ps1 - build PolymarketBot (Release) and run self-tests.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROLL_FORWARD = 'Major'
$root = Split-Path -Parent $PSScriptRoot

Write-Output '=== SelfTests build ==='
dotnet build (Join-Path $root 'dotnet\PolymarketBot.SelfTests') -c Release
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Output '=== SelfTests run ==='
dotnet run --project (Join-Path $root 'dotnet\PolymarketBot.SelfTests') -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Output '=== Bot build (Release) ==='
dotnet build (Join-Path $root 'dotnet\PolymarketBot') -c Release
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Output 'BUILD OK'
