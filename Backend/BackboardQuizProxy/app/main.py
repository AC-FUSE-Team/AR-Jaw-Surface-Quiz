from __future__ import annotations

import logging
import secrets
import threading
import time
from collections import defaultdict, deque
from pathlib import Path

from fastapi import Depends, FastAPI, Header, HTTPException, Request, Response
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse

from .instrumented_backboard import BackboardClient
from .config import Settings
from .dynamic_database import DynamicScalingDatabase
from .memory_policy import DeterministicMemoryPolicy
from .models import (AttemptRequest, AttemptResponse, HintRequest, HintResponse,
                     LearningEventRequest, LearningEventResponse, SessionSummaryRequest, StudentRequest)

log = logging.getLogger("jaw_quiz_proxy")


def create_app(settings: Settings | None = None, backboard_transport=None) -> FastAPI:
    settings = settings or Settings.from_environment()
    database = DynamicScalingDatabase(settings.database_path)
    backboard = BackboardClient(settings, database, backboard_transport)
    app = FastAPI(title="Jaw Surface Quiz Proxy", version="1.0.0", docs_url=None, redoc_url=None)
    app.state.settings, app.state.database, app.state.backboard = settings, database, backboard
    app.add_middleware(CORSMiddleware, allow_origins=list(settings.cors_origins),
                       allow_credentials=False, allow_methods=["GET", "POST"],
                       allow_headers=["Content-Type", "X-Quiz-Token"])

    request_times: dict[str, deque[float]] = defaultdict(deque)
    rate_lock = threading.Lock()

    @app.middleware("http")
    async def bounded_requests(request: Request, call_next):
        content_length = request.headers.get("content-length")
        if content_length:
            try:
                if int(content_length) > settings.max_payload_bytes:
                    return JSONResponse({"detail": "Payload too large"}, status_code=413)
            except ValueError:
                return JSONResponse({"detail": "Invalid content length"}, status_code=400)
        if request.url.path != "/health":
            client = request.client.host if request.client else "unknown"
            now = time.monotonic()
            with rate_lock:
                bucket = request_times[client]
                while bucket and now - bucket[0] >= settings.rate_limit_window_seconds:
                    bucket.popleft()
                if len(bucket) >= settings.rate_limit_requests:
                    return JSONResponse({"detail": "Rate limit exceeded"}, status_code=429)
                bucket.append(now)
        return await call_next(request)

    def authorize(request: Request, x_quiz_token: str = Header(default="")) -> None:
        if settings.quiz_proxy_token and not secrets.compare_digest(x_quiz_token, settings.quiz_proxy_token):
            raise HTTPException(401, "Invalid prototype token")

    @app.get("/health")
    def health():
        return {"status": "ok"}

    @app.get("/api/v1/status", dependencies=[Depends(authorize)])
    def status():
        return {"proxyConnected": True, "mode": "synthetic-local" if database.is_synthetic_demo() else ("mock" if settings.mock_mode else "backboard"),
                "syntheticDemo": database.is_synthetic_demo(),
                "externalServicesEnabled": False if database.is_synthetic_demo() else not settings.mock_mode,
                "backboardAvailable": False if database.is_synthetic_demo() else bool(settings.mock_mode or settings.backboard_api_key),
                "queuedLearningEvents": sum(row["state"] != "completed"
                                            for row in database.memory_event_rows())}

    @app.post("/api/v1/students", dependencies=[Depends(authorize)])
    def create_student(request: StudentRequest):
        return {"studentId": request.studentId, "created": database.ensure_student(request.studentId)}

    @app.post("/api/v1/attempts", response_model=AttemptResponse, dependencies=[Depends(authorize)])
    def attempt(request: AttemptRequest):
        inserted = database.insert_attempt(request.model_dump(mode="json"))
        return AttemptResponse(accepted=inserted, duplicate=not inserted, eventId=request.eventId,
                               queuedForBackboard=False)

    @app.post("/api/v1/learning-events", response_model=LearningEventResponse,
              dependencies=[Depends(authorize)])
    async def learning_event(request: LearningEventRequest):
        attempt_row = database.attempt(str(request.eventId))
        if attempt_row is None or attempt_row["student_id"] != request.studentId:
            raise HTTPException(404, "Attempt not found for student")
        if database.is_synthetic_demo():
            return LearningEventResponse(accepted=True, memoryAction="off",
                reason="synthetic_demo_local_only", source="local-policy")
        decision = DeterministicMemoryPolicy.evaluate(
            attempt_row, database.attempts_for_student(request.studentId))
        if not decision.should_write:
            return LearningEventResponse(accepted=True, memoryAction="off", reason=decision.reason,
                                         source="local-policy")
        existing = database.memory_event(request.studentId, decision.policy_key)
        if existing and existing["state"] == "completed":
            return LearningEventResponse(accepted=True, duplicate=True, memoryAction="Auto",
                reason=decision.reason, source="local-policy",
                responseReference=existing["response_reference"])
        if existing is None:
            database.reserve_memory_event(request.studentId, decision.policy_key,
                                          str(request.eventId), decision.reason)
        try:
            text, source, reference, _ = await backboard.write_learning_memory(
                request.studentId, attempt_row, decision)
            database.complete_memory_event(request.studentId, decision.policy_key,
                                           "completed", reference)
            database.save_attempt_backboard_reference(
                request.studentId, attempt_row["session_id"], attempt_row["question_id"], reference)
            return LearningEventResponse(accepted=True, memoryAction="Auto", reason=decision.reason,
                source=source, text=text, responseReference=reference)
        except RuntimeError:
            database.complete_memory_event(request.studentId, decision.policy_key, "queued")
            return LearningEventResponse(accepted=False, memoryAction="Auto", reason=decision.reason,
                source="local-fallback")

    @app.post("/api/v1/hints", response_model=HintResponse, dependencies=[Depends(authorize)])
    async def hint(request: HintRequest):
        try:
            if database.is_synthetic_demo():
                raise RuntimeError("External hinting disabled for synthetic demo")
            text, source, reference, usage = await backboard.hint(request)
            if not text: raise RuntimeError("empty response")
            database.save_attempt_backboard_reference(request.studentId, request.sessionId,
                                                       request.questionId, reference)
            return HintResponse(text=text, source=source, responseReference=reference,
                                suggestedReviewRegions=[request.expectedStableRegionId],
                                usage=usage)
        except RuntimeError:
            log.warning("Hint fallback used (secrets redacted)")
            return HintResponse(text=f"Review the location of {request.expectedStableRegionId} and try again.",
                                source="local-fallback", suggestedReviewRegions=[request.expectedStableRegionId])

    @app.post("/api/v1/session-summary", dependencies=[Depends(authorize)])
    async def session_summary(request: SessionSummaryRequest):
        detail = database.student_detail(request.studentId)
        if detail is None: raise HTTPException(404, "Unknown student")
        session = next((x for x in detail["recent_sessions"] if x["session_id"] == request.sessionId), None)
        calculated = session or {"attempts": 0, "accuracy": 0}
        weak = [row["region"] for row in detail["regions"] if row["accuracy"] < .7][:3]
        try:
            if database.is_synthetic_demo():
                raise RuntimeError("External summaries disabled for synthetic demo")
            if not settings.mock_mode and not settings.enable_teacher_ai:
                raise RuntimeError("Teacher AI is disabled")
            summary, reference = await backboard.teacher_summary(request.studentId,
                {"attempts": calculated["attempts"], "accuracy": calculated["accuracy"],
                 "weak_regions": weak})
        except RuntimeError:
            summary, reference = "Local summary available; AI summary is queued.", ""
        return {"studentId": request.studentId, "sessionId": request.sessionId,
                "calculated": calculated,
                "aiGenerated": {"summary": summary, "responseReference": reference,
                                "recommendedReviewRegions": weak}}

    @app.get("/api/v1/teacher/students", dependencies=[Depends(authorize)])
    def students(): return {"students": database.student_rows()}

    @app.get("/api/v1/teacher/students/{student_id}", dependencies=[Depends(authorize)])
    def student(student_id: str):
        if not StudentRequest(studentId=student_id): raise HTTPException(422)
        detail = database.student_detail(student_id)
        if detail is None: raise HTTPException(404, "Unknown student")
        return detail

    @app.get("/api/v1/teacher/regions", dependencies=[Depends(authorize)])
    def regions(): return {"regions": database.region_rows()}

    @app.get("/api/v1/teacher/export.csv", dependencies=[Depends(authorize)])
    def export_csv():
        name = "Jaw_Quiz_Synthetic_Demonstration_Attempts.csv" if database.is_synthetic_demo() else "jaw-quiz-attempts.csv"
        return Response(database.export_csv(), media_type="text/csv",
                        headers={"Content-Disposition": f"attachment; filename={name}"})

    @app.get("/api/v1/teacher/export.json", dependencies=[Depends(authorize)])
    def export_json():
        name = "Jaw_Quiz_Synthetic_Demonstration_Attempts.json" if database.is_synthetic_demo() else "jaw-quiz-attempts.json"
        return JSONResponse({"attempts": database.export_rows()},
                            headers={"Content-Disposition": f"attachment; filename={name}"})

    @app.post("/api/v1/teacher/students/{student_id}/tailored-test", dependencies=[Depends(authorize)])
    def tailored_test(student_id: str):
        if database.is_semester_demo():
            raise HTTPException(403, "PDF generation is disabled for the semester workflow")
        if not database.is_synthetic_demo():
            raise HTTPException(403, "Tailored test is available only in the synthetic demonstration")
        if student_id != "student_synthetic_1":
            raise HTTPException(403, "PDF generation is demonstrated for Synthetic Student 1")
        from .synthetic_pdf import generate_tailored_pdf
        path = generate_tailored_pdf(database)
        return {"filename": path.name,
                "downloadUrl": f"/api/v1/teacher/tailored-tests/{path.name}",
                "synthetic": True, "localOnly": True}

    @app.get("/api/v1/teacher/tailored-tests/{filename}", dependencies=[Depends(authorize)])
    def tailored_test_file(filename: str):
        if (database.is_semester_demo() or not database.is_synthetic_demo()
                or Path(filename).name != filename
                or not filename.startswith("Synthetic_Student_1_Tailored_Jaw_Test_")
                or not filename.endswith(".pdf")):
            raise HTTPException(404, "Synthetic tailored test not found")
        from .synthetic_pdf import DEFAULT_OUTPUT, FIVE_ROUND_OUTPUT
        root = FIVE_ROUND_OUTPUT if database.is_five_round_demo() else DEFAULT_OUTPUT
        path = root / filename
        if not path.is_file():
            raise HTTPException(404, "Synthetic tailored test not found")
        return FileResponse(path, media_type="application/pdf", filename=filename)

    from .five_round_routes import register as register_five_round_routes
    register_five_round_routes(app, settings, database, backboard_transport, authorize)
    from .semester_routes import register as register_semester_routes
    register_semester_routes(app, settings, database, backboard_transport, authorize)
    from .dynamic_routes import register as register_dynamic_routes
    register_dynamic_routes(app, settings, database, backboard_transport, authorize)

    @app.get("/teacher", response_class=HTMLResponse)
    def dashboard():
        page = ("dashboard_dynamic.html" if database.is_dynamic_scaling() else
                "dashboard_semester.html" if database.is_semester_demo() else
                "dashboard_5round.html" if database.is_five_round_demo() else "dashboard.html")
        return FileResponse(Path(__file__).with_name(page), media_type="text/html")

    return app


app = create_app()
