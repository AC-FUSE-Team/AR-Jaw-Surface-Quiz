#!/usr/bin/env python3
"""Run the one-shot, analysis-only Backboard semester workflow."""
from __future__ import annotations

import asyncio
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.backboard_semester import SemesterBackboard  # noqa: E402
from app.config import Settings  # noqa: E402
from app.semester_database import SemesterDatabase  # noqa: E402
from app.skeleton_catalog import PRESERVED_PDF, PRESERVED_PDF_SHA256  # noqa: E402

ARTIFACT = ROOT.parents[1] / "Artifacts/SyntheticWholeSkeletonSemester_2026-07-24"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


async def run() -> dict:
    before = digest(PRESERVED_PDF) if PRESERVED_PDF.is_file() else "missing"
    if before != PRESERVED_PDF_SHA256:
        raise RuntimeError(f"Preserved PDF gate failed: {before}")
    settings = Settings.from_environment()
    database = SemesterDatabase(settings.database_path)
    if not database.is_semester_demo():
        raise RuntimeError("Configured database is not the skeleton semester database")
    workflow = SemesterBackboard(settings, database)
    state = workflow.state()
    if state["status"] == "completed":
        result = workflow.cached_result()
    else:
        catalogue = await workflow.discover()
        if not catalogue["compatible"]:
            raise RuntimeError(f"Exact compatible GPT-5.5 unavailable: {catalogue['reason']}")
        result = await workflow.run(catalogue["provider"], catalogue["model_name"])
    after = digest(PRESERVED_PDF)
    if after != before:
        raise RuntimeError("Preserved PDF changed during analysis workflow")
    receipt = {
        "timestamp": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "synthetic": True, "workflow": "whole_skeleton_semester_teacher_analysis",
        "provider": result["provider"], "model": result["model"],
        "backboard_operation_count": result["totalOperations"],
        "storage_operation_count": result["storageOperations"],
        "gpt55_generation_count": result["generationOperations"],
        "verified_spending_usd": result["verifiedSpendingUsd"],
        "spending_source": result["spendingSource"],
        "hard_spending_ceiling_usd": 5.0,
        "pdf_generated": False,
        "preserved_pdf_pre_sha256": before,
        "preserved_pdf_post_sha256": after,
    }
    ARTIFACT.joinpath("receipts").mkdir(parents=True, exist_ok=True)
    path = ARTIFACT / "receipts/semester_backboard_receipt.json"
    path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return receipt


if __name__ == "__main__":
    print(json.dumps(asyncio.run(run()), indent=2))
