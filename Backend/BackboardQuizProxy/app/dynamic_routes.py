from __future__ import annotations

from fastapi import Depends, HTTPException


def register(app, settings, database, transport, authorize):
    def require_dynamic() -> None:
        if not database.is_dynamic_scaling():
            raise HTTPException(403, "Dynamic scaling demonstration only")

    @app.get("/api/v1/teacher/dynamic/status", dependencies=[Depends(authorize)])
    def status():
        require_dynamic()
        return database.dynamic_status()

    @app.get("/api/v1/teacher/dynamic/preview", dependencies=[Depends(authorize)])
    async def preview():
        require_dynamic()
        from .dynamic_backboard import DynamicBackboard
        workflow = DynamicBackboard(settings, database, transport)
        if not settings.backboard_api_key:
            return workflow.preview_next() | {"billing_verification": "offline_cached"}
        try:
            await workflow.refresh_spend()
            return workflow.preview_next()
        except Exception as exc:
            raise HTTPException(503, f"Billing verification unavailable: {type(exc).__name__}") from exc

    @app.get("/api/v1/teacher/dynamic/cached", dependencies=[Depends(authorize)])
    def cached():
        require_dynamic()
        students = []
        for row in database.student_rows():
            detail = database.student_detail(row["student_id"])
            students.append({
                "student_id": row["student_id"],
                "display_name": row["display_name"],
                "status": row["processing_status"],
                "included_in_x": row["included_in_x"],
                "checkpoint_analyses": detail.get("checkpoint_analyses", []),
                "legacy_semester_analysis": detail.get("backboard_analyses", []),
            })
        return {"synthetic": True, "students": students}

    @app.post("/api/v1/teacher/dynamic/process-next", dependencies=[Depends(authorize)])
    async def process_next(payload: dict):
        require_dynamic()
        if (payload.get("confirmed") is not True
                or payload.get("syntheticOnly") is not True):
            raise HTTPException(409, "Explicit synthetic-only confirmation is required")
        from .dynamic_backboard import DynamicBackboard, DynamicWorkflowError
        workflow = DynamicBackboard(settings, database, transport)
        try:
            await workflow.refresh_spend()
            preview_value = workflow.preview_next()
            confirmed_spend = payload.get("confirmedSpendUsd")
            if not isinstance(confirmed_spend, (int, float)) or abs(
                    float(confirmed_spend) - preview_value["reported_spend_usd"]) > .000001:
                raise HTTPException(409, "Reported spend changed; review and confirm again")
            if not preview_value["can_start"]:
                raise HTTPException(409, "The complete next-student workflow is not budget-safe")
            if preview_value["active_status"] == "not_seeded":
                from scripts.seed_dynamic_skeleton_scaling import add_student
                add_student(settings.database_path, preview_value["next_student_number"])
            maximum = int(payload.get("maxNewCheckpoints", 5))
            if maximum not in range(1, 6):
                raise HTTPException(422, "maxNewCheckpoints must be 1 through 5")
            return await workflow.process_student(
                preview_value["next_student_id"], max_new_checkpoints=maximum)
        except HTTPException:
            raise
        except DynamicWorkflowError as exc:
            raise HTTPException(409, str(exc)) from exc
        except Exception as exc:
            raise HTTPException(502, f"Controlled workflow failed: {type(exc).__name__}") from exc

    @app.post("/api/v1/teacher/dynamic/disable", dependencies=[Depends(authorize)])
    def disable(payload: dict):
        require_dynamic()
        if payload.get("confirmed") is not True:
            raise HTTPException(409, "Explicit disable confirmation is required")
        from .dynamic_backboard import DynamicBackboard
        return DynamicBackboard(settings, database, transport).disable()
