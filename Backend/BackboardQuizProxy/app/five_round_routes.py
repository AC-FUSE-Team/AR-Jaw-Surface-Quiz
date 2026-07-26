from __future__ import annotations

import logging
from pathlib import Path

from fastapi import Depends, Header, HTTPException
from fastapi.responses import FileResponse

log = logging.getLogger("jaw_quiz_proxy.synthetic_backboard")


def register(app, settings, database, transport, authorize):
    @app.get("/api/v1/teacher/backboard/discovery", dependencies=[Depends(authorize)])
    async def discovery():
        if not database.is_five_round_demo():
            raise HTTPException(403, "Available only in the five-round synthetic demonstration")
        from .backboard_five_round import FiveRoundBackboard
        try:
            return await FiveRoundBackboard(settings, database, transport).discover()
        except Exception:
            log.warning("Backboard model discovery failed (credentials and payload redacted)")
            raise HTTPException(
                502, "Backboard model discovery failed; credentials may be missing or invalid")

    @app.get("/api/v1/teacher/backboard/status", dependencies=[Depends(authorize)])
    def status():
        if not database.is_five_round_demo():
            raise HTTPException(403, "Available only in the five-round synthetic demonstration")
        from .backboard_five_round import FiveRoundBackboard
        state = FiveRoundBackboard(settings, database, transport).state()
        return {key: state[key] for key in (
            "status", "provider", "model_name", "memory_mode", "model_discovery_operations", "storage_operations",
            "generation_operations", "total_operations", "completed_utc",
            "pdf_filename", "pdf_sha256")}

    @app.post("/api/v1/teacher/students/{student_id}/backboard-test",
              dependencies=[Depends(authorize)])
    async def generate(student_id: str, payload: dict):
        if not database.is_five_round_demo() or student_id != "student_synthetic_1":
            raise HTTPException(403, "Only fictional Synthetic Student 1 is permitted")
        if payload.get("confirmed") is not True:
            raise HTTPException(409, "Explicit Backboard usage confirmation is required")
        from .backboard_five_round import FiveRoundBackboard
        workflow = FiveRoundBackboard(settings, database, transport)
        state = workflow.state()
        regenerate = payload.get("regenerate") is True
        if state["status"] == "completed" and not regenerate:
            return workflow.cached_result()
        if regenerate and payload.get("confirmedRegeneration") is not True:
            raise HTTPException(409, "Explicit additional-usage confirmation is required")
        catalogue = await workflow.discover()
        if not catalogue["compatible"]:
            raise HTTPException(412, {
                "message": catalogue["reason"], "alternatives": catalogue["alternatives"]})
        if (payload.get("provider") != catalogue["provider"]
                or payload.get("model") != catalogue["model_name"]):
            raise HTTPException(
                409, "Confirmed provider/model does not match the current catalogue")
        try:
            return await workflow.generate(
                catalogue["provider"], catalogue["model_name"], regenerate=regenerate)
        except Exception as exc:
            log.warning("Controlled synthetic workflow failed type=%s (secrets redacted)",
                        type(exc).__name__)
            raise HTTPException(
                502, "Backboard generation or local validation failed; no automatic retry was made")

    @app.get("/api/v1/teacher/backboard-tests/{filename}",
             dependencies=[Depends(authorize)])
    def download(filename: str):
        if (not database.is_five_round_demo() or Path(filename).name != filename
                or not filename.startswith(
                    "Synthetic_Student_1_GPT55_Backboard_Tailored_Jaw_Test_")
                or not filename.endswith(".pdf")):
            raise HTTPException(404, "Backboard-assisted synthetic test not found")
        from .backboard_five_round import PDF_ROOT
        path = PDF_ROOT / filename
        if not path.is_file():
            raise HTTPException(404, "Backboard-assisted synthetic test not found")
        return FileResponse(path, media_type="application/pdf", filename=filename)
