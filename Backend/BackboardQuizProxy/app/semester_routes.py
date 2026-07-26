from __future__ import annotations

import hashlib
from fastapi import Depends, HTTPException
from fastapi.responses import FileResponse

from .skeleton_catalog import PRESERVED_PDF, PRESERVED_PDF_SHA256


def register(app, settings, database, transport, authorize):
    @app.get("/api/v1/teacher/class-summary", dependencies=[Depends(authorize)])
    def class_summary():
        if not database.is_semester_demo():
            raise HTTPException(403, "Semester demonstration only")
        return database.class_summary()

    @app.get("/api/v1/teacher/semester/backboard/status",
             dependencies=[Depends(authorize)])
    def status():
        if not database.is_semester_demo():
            raise HTTPException(403, "Semester demonstration only")
        from .backboard_semester import SemesterBackboard
        return SemesterBackboard(settings, database, transport).cached_result()

    @app.get("/api/v1/teacher/semester/backboard/discovery",
             dependencies=[Depends(authorize)])
    async def discovery():
        if not database.is_semester_demo():
            raise HTTPException(403, "Semester demonstration only")
        from .backboard_semester import SemesterBackboard
        return await SemesterBackboard(settings, database, transport).discover()

    @app.post("/api/v1/teacher/semester/backboard/analyze",
              dependencies=[Depends(authorize)])
    async def analyze(payload: dict):
        if not database.is_semester_demo():
            raise HTTPException(403, "Semester demonstration only")
        if payload.get("confirmed") is not True:
            raise HTTPException(409, "Explicit synthetic Backboard analysis confirmation required")
        from .backboard_semester import SemesterBackboard
        workflow = SemesterBackboard(settings, database, transport)
        state = workflow.state()
        if state["status"] == "completed":
            return workflow.cached_result()
        catalogue = await workflow.discover()
        if not catalogue["compatible"]:
            raise HTTPException(412, catalogue["reason"])
        if (payload.get("provider") != catalogue["provider"]
                or payload.get("model") != catalogue["model_name"]):
            raise HTTPException(409, "Confirmed provider/model does not match catalogue")
        return await workflow.run(catalogue["provider"], catalogue["model_name"])

    @app.get("/api/v1/teacher/preserved-jaw-assessment",
             dependencies=[Depends(authorize)])
    def preserved_jaw_assessment():
        if not database.is_semester_demo() or not PRESERVED_PDF.is_file():
            raise HTTPException(404, "Preserved assessment not found")
        digest = hashlib.sha256(PRESERVED_PDF.read_bytes()).hexdigest()
        if digest != PRESERVED_PDF_SHA256:
            raise HTTPException(409, "Preserved assessment failed SHA-256 verification")
        return FileResponse(PRESERVED_PDF, media_type="application/pdf",
                            filename=PRESERVED_PDF.name)
