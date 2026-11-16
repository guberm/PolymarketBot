#!/usr/bin/env python3
"""Launch the Windows PolymarketBot via the Hermes MCP bridge (windows-pc, 127.0.0.1:17713).

Session helper for the bot operator (not used by the bot itself).

Usage (Hermes session with the windows-pc SSH tunnel active):
    export LAUNCH_PROXY_URL="http://user:password@2.29.155.27:8888"   # from polymarket_bot_config.json
    python3 scripts/launch_via_bridge.py

IMPORTANT: the log redirect uses >> (append), NOT >. A > redirect must
TRUNCATE pmb-native.out, but the already-running instance holds that file
open - the second cmd then dies on the redirect before dotnet starts and
the launch fails silently ("Launched" from the bridge, nothing happens).
Append never conflicts, and the instance lock (bot.lock) rejects the
second instance cleanly ("Another bot instance owns ...").
"""
import json
import os
import socket
import sys
import time

REPO = r"C:\Users\michael.guber\Desktop\my repos\polymarket_bot"
LOG = r"C:\Users\Public\pmb-native.out"
BRIDGE = ("127.0.0.1", 17713)

proxy = os.environ.get("LAUNCH_PROXY_URL")
if not proxy:
    sys.exit("Set LAUNCH_PROXY_URL first (never hardcode credentials - this file is in a public repo)")

s = socket.create_connection(BRIDGE, timeout=15)
s.settimeout(40)


def send(obj):
    s.sendall((json.dumps(obj) + "\n").encode())


def recv():
    buf = b""
    try:
        while b"\n" not in buf:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
    except socket.timeout:
        pass
    return buf.decode(errors="replace")


send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
      "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                 "clientInfo": {"name": "hermes", "version": "1.0"}}})
recv()
time.sleep(0.3)
send({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
      "params": {"name": "launch_app",
                 "arguments": {"path": r"C:\Windows\System32\cmd.exe",
                               "additional_arguments": ["/c",
                                   f'cd /d "{REPO}" && '
                                   f'set "HTTPS_PROXY={proxy}" && set "HTTP_PROXY={proxy}" && '
                                   f'dotnet dotnet\\PolymarketBot\\bin\\Release\\net8.0\\PolymarketBot.dll --console >> "{LOG}" 2>&1']}}})
print("LAUNCH:", recv()[:150])
s.close()
