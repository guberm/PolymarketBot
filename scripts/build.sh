#!/usr/bin/env bash
# scripts/build.sh - build PolymarketBot (Release) and run self-tests (Linux).
# Usage: ./scripts/build.sh
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROLL_FORWARD=Major

echo "=== SelfTests build ==="
dotnet build dotnet/PolymarketBot.SelfTests -c Release

echo "=== SelfTests run ==="
dotnet run --project dotnet/PolymarketBot.SelfTests -c Release --no-build

echo "=== Bot build (Release) ==="
dotnet build dotnet/PolymarketBot -c Release

echo "BUILD OK"
