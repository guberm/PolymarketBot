"""Single-process guard and CLOB order reconciliation helpers."""

import json
import os
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from pathlib import Path
from uuid import uuid4


GEOBLOCK_URL = "https://polymarket.com/api/geoblock"


@dataclass(frozen=True)
class GeoblockStatus:
    blocked: bool
    country: str = ""
    region: str = ""


class TradingBlockedError(RuntimeError):
    """Fatal live-trading rejection; the process must not retry."""


def check_geoblock(request_get=None, url: str = GEOBLOCK_URL) -> GeoblockStatus:
    """Fail closed unless Polymarket explicitly reports that this region is allowed."""
    if request_get is None:
        import requests
        request_get = requests.get
    response = request_get(url, timeout=10)
    response.raise_for_status()
    data = response.json()
    if not isinstance(data, dict) or not isinstance(data.get("blocked"), bool):
        raise ValueError("Invalid Polymarket geoblock response")
    return GeoblockStatus(data["blocked"], str(data.get("country", "")), str(data.get("region", "")))


@dataclass(frozen=True)
class OrderFill:
    status: str
    shares: float
    value: float
    price: float


def retry_delay_seconds(retry_after: str | None, attempt: int) -> float:
    """Honor Retry-After while bounding stalls; fallback is 1, 2, 4 seconds."""
    delay = float(2 ** max(0, attempt))
    if retry_after:
        try:
            delay = float(retry_after)
        except ValueError:
            try:
                target = parsedate_to_datetime(retry_after)
                if target.tzinfo is None:
                    target = target.replace(tzinfo=timezone.utc)
                delay = max(0.0, (target - datetime.now(timezone.utc)).total_seconds())
            except (TypeError, ValueError, OverflowError):
                pass
    return min(60.0, max(0.0, delay))


def parse_order_fill(data: dict, side: str, fallback_price: float) -> OrderFill:
    def number(*keys: str) -> float:
        for key in keys:
            try:
                value = float(data.get(key, 0) or 0)
                if value > 0:
                    return value
            except (TypeError, ValueError):
                pass
        return 0.0

    status = str(data.get("status", "") or "").upper()
    making = number("makingAmount", "making_amount")
    taking = number("takingAmount", "taking_amount")
    if side.upper() == "BUY":
        value, shares = making, taking
    else:
        shares, value = making, taking

    if shares <= 0:
        shares = number("size_matched", "sizeMatched", "matched_size")
    price = number("average_price", "avg_price", "price") or fallback_price
    if value <= 0 and shares > 0:
        value = shares * price
    if shares <= 0 and value > 0 and price > 0:
        shares = value / price
    return OrderFill(status, shares, value, value / shares if shares > 0 else price)


class OrderJournal:
    """Atomic journal for live order intents that have not reached durable portfolio state."""

    def __init__(self, data_dir: str):
        self.path = Path(data_dir) / "pending-orders.json"

    def begin(self, details: dict) -> str:
        records = self._load()
        intent_id = str(uuid4())
        records[intent_id] = {
            "intent_id": intent_id,
            "created_at": time.time(),
            **details,
        }
        self._save(records)
        return intent_id

    def submitted(self, intent_id: str, order_id: str) -> None:
        self._update(intent_id, order_id=order_id, submitted_at=time.time())

    def filled(self, intent_id: str, fill: OrderFill) -> None:
        self._update(
            intent_id,
            fill_status=fill.status,
            fill_shares=fill.shares,
            fill_value=fill.value,
            fill_price=fill.price,
            filled_at=time.time(),
        )

    def complete(self, intent_id: str) -> None:
        records = self._load()
        if records.pop(intent_id, None) is not None:
            self._save(records)

    def pending(self) -> list[dict]:
        return sorted(self._load().values(), key=lambda item: item.get("created_at", 0))

    def _update(self, intent_id: str, **changes) -> None:
        records = self._load()
        if intent_id not in records:
            raise KeyError(f"Unknown order intent: {intent_id}")
        records[intent_id].update(changes)
        self._save(records)

    def _load(self) -> dict[str, dict]:
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
            return value if isinstance(value, dict) else {}
        except (FileNotFoundError, OSError, json.JSONDecodeError):
            return {}

    def _save(self, records: dict[str, dict]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(self.path.suffix + ".tmp")
        tmp.write_text(json.dumps(records, indent=2), encoding="utf-8")
        os.replace(tmp, self.path)


class InstanceLock:
    """Cross-language lock based on atomic creation of one PID file."""

    def __init__(self, data_dir: str):
        self.path = Path(data_dir) / "bot.lock"
        self.acquired = False

    def acquire(self) -> bool:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        for _ in range(2):
            try:
                fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                with os.fdopen(fd, "w", encoding="utf-8") as stream:
                    json.dump({"pid": os.getpid()}, stream)
                self.acquired = True
                return True
            except FileExistsError:
                if self._owner_alive():
                    return False
                try:
                    self.path.unlink()
                except FileNotFoundError:
                    pass
        return False

    def release(self) -> None:
        if not self.acquired:
            return
        try:
            owner = json.loads(self.path.read_text(encoding="utf-8")).get("pid")
            if owner == os.getpid():
                self.path.unlink(missing_ok=True)
        except (OSError, ValueError, json.JSONDecodeError):
            pass
        self.acquired = False

    def _owner_alive(self) -> bool:
        try:
            pid = int(json.loads(self.path.read_text(encoding="utf-8")).get("pid", 0))
            if pid <= 0:
                return False
            if sys.platform == "win32":
                import ctypes
                handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
                if not handle:
                    return False
                ctypes.windll.kernel32.CloseHandle(handle)
                return True
            os.kill(pid, 0)
            return True
        except (OSError, ValueError, json.JSONDecodeError):
            try:
                return (self.path.stat().st_mtime + 5) > time.time()
            except OSError:
                return False
