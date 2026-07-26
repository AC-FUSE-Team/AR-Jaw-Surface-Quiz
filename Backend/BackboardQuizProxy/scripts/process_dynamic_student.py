#!/usr/bin/env python3
"""Process at most one synthetic student, sequentially and budget-gated."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import sys
from dataclasses import replace
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.config import Settings  # noqa: E402
from app.dynamic_backboard import DynamicBackboard  # noqa: E402
from app.dynamic_database import DynamicScalingDatabase  # noqa: E402
from app.skeleton_catalog import PRESERVED_PDF, PRESERVED_PDF_SHA256  # noqa: E402
from scripts.seed_dynamic_skeleton_scaling import DEFAULT_DB, add_student  # noqa: E402


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


async def run(args) -> dict:
    before = digest(PRESERVED_PDF)
    if before != PRESERVED_PDF_SHA256:
        raise RuntimeError("Preserved Student 1 PDF failed the preflight hash gate")
    settings = replace(Settings.from_environment(), database_path=args.database)
    database = DynamicScalingDatabase(args.database)
    if not database.is_dynamic_scaling():
        raise RuntimeError("Configured database is not the dynamic scaling database")
    workflow = DynamicBackboard(settings, database)
    await workflow.refresh_spend()
    preview = workflow.preview_next()
    if args.preview:
        result = preview
    else:
        number = args.student if args.student is not None else preview["next_student_number"]
        student_id = f"student_synthetic_{number}"
        with database.connect() as con:
            exists = con.execute(
                "SELECT 1 FROM dynamic_student_processing WHERE student_id=?",
                (student_id,)).fetchone()
        if not exists:
            if not args.seed_if_needed:
                raise RuntimeError("Student is not locally seeded; pass --seed-if-needed")
            if number != preview["next_student_number"]:
                raise RuntimeError("Only the next sequential student may be seeded")
            add_student(args.database, number)
        processed = await workflow.process_student(
            student_id, max_new_checkpoints=args.max_new_checkpoints)
        result = {
            "student_id": student_id,
            "status": processed["status"],
            "included_in_x": bool(processed["included_in_x"]),
            "checkpoint_count": processed["checkpoint_count"],
            "storage_operation_count": processed["storage_operation_count"],
            "generation_count": processed["generation_count"],
            "started_utc": processed["started_utc"],
            "completed_utc": processed["completed_utc"],
            "measured_cost_usd": processed["measured_cost_usd"],
            "cumulative_reported_spend_usd": database.dynamic_status()["reported_spend_usd"],
            "current_x": database.dynamic_status()["current_x"],
        }
    after = digest(PRESERVED_PDF)
    if after != before:
        raise RuntimeError("Preserved Student 1 PDF changed during the workflow")
    return {
        **result,
        "preserved_pdf_pre_sha256": before,
        "preserved_pdf_post_sha256": after,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", type=Path, default=DEFAULT_DB)
    parser.add_argument("--student", type=int)
    parser.add_argument("--max-new-checkpoints", type=int, choices=range(1, 6), default=5)
    parser.add_argument("--seed-if-needed", action="store_true")
    parser.add_argument("--preview", action="store_true")
    args = parser.parse_args()
    print(json.dumps(asyncio.run(run(args)), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
