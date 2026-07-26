#!/usr/bin/env python3
"""GET-only, sanitized billing verification for the completed semester run."""
from __future__ import annotations

import json
import os
import sqlite3
import sys
from pathlib import Path
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
DB = ROOT / "data/skeleton_SYNTHETIC_20ROUND_SEMESTER.sqlite3"
RECEIPT = ROOT.parents[1] / (
    "Artifacts/SyntheticWholeSkeletonSemester_2026-07-24/receipts/"
    "semester_backboard_receipt.json")
WINDOW_START = "2026-07-24T07:27:55"
WINDOW_END = "2026-07-24T07:33:15"


def main() -> int:
    key = os.environ.get("BACKBOARD_API_KEY", "").strip()
    if not key:
        raise RuntimeError("BACKBOARD_API_KEY is missing")
    request = Request(
        "https://app.backboard.io/api/billing/usage/recent?page=1&page_size=100",
        headers={"X-API-Key": key})
    with urlopen(request, timeout=30) as response:
        value = json.load(response)
    rows = [x for x in value.get("data", [])
            if WINDOW_START <= str(x.get("date", "")) <= WINDOW_END]
    generations = [x for x in rows if x.get("model") == "gpt-5.5"]
    if len(generations) != 6:
        raise RuntimeError(f"Expected 6 GPT-5.5 billing events, found {len(generations)}")
    total = round(sum(float(x.get("amount_deducted_usd") or 0) for x in rows), 8)
    if not 0 < total < 5:
        raise RuntimeError(f"Verified spending is outside the authorized range: {total}")
    with sqlite3.connect(DB) as con:
        con.execute("""UPDATE semester_backboard_state
          SET verified_spending_usd=?,spending_source='backboard_billing_usage_recent'
          WHERE id=1 AND status='completed'""", (total,))
    receipt = json.loads(RECEIPT.read_text(encoding="utf-8"))
    receipt.update({
        "verified_spending_usd": total,
        "spending_source": "Backboard GET /billing/usage/recent amount_deducted_usd",
        "billing_window_utc": {"start": WINDOW_START, "end": WINDOW_END},
        "billing_event_count": len(rows),
        "gpt55_billing_event_count": len(generations),
    })
    RECEIPT.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "verified_spending_usd": total,
        "billing_event_count": len(rows),
        "gpt55_billing_event_count": len(generations),
        "models": sorted({str(x.get("model")) for x in rows}),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
