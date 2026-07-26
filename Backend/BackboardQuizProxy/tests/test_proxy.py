from __future__ import annotations

import asyncio
import json
import logging
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

import httpx
import pytest
from fastapi.testclient import TestClient

from app.instrumented_backboard import BackboardClient, BackboardStageError
from app.config import Settings
from app.database import Database
from app.main import create_app
from app.models import HintRequest


def settings(tmp_path: Path, *, mock=True, key="", token="") -> Settings:
    return Settings(tmp_path / "quiz.sqlite3", mock, key, "https://app.backboard.io/api",
                    token, ("http://localhost:8765",), timeout_seconds=.01, max_attempts=2)


def attempt(event_id=None, student="student_001", correct=False):
    return {"eventId":str(event_id or uuid4()),"studentId":student,"sessionId":"session_test",
      "questionId":"q_left_mental","objectId":"jaw","regionMapVersion":"data-v1:test",
      "expectedStableRegionId":"LeftMentalForamen",
      "selectedStableRegionId":"LeftMentalForamen" if correct else "MentalProtuberance",
      "correct":correct,"responseTimeSeconds":8.4,"attemptNumber":2,"hintLevel":1,
      "utcTimestamp":datetime.now(timezone.utc).isoformat(),"synchronizationState":"pending",
      "backboardResponseReference":""}


def test_validation_rejects_unknown_fields_and_real_names(tmp_path):
    client=TestClient(create_app(settings(tmp_path)))
    bad=attempt(); bad["cameraFrame"]="not allowed"
    assert client.post("/api/v1/attempts",json=bad).status_code==422
    bad=attempt(); bad["studentId"]="Omar Smith"
    assert client.post("/api/v1/attempts",json=bad).status_code==422


def test_mock_mode_health_and_hint_need_no_key_or_network(tmp_path):
    client=TestClient(create_app(settings(tmp_path)))
    assert client.get("/health").json()=={"status":"ok"}
    assert client.get("/api/v1/status").json()["mode"]=="mock"
    stored=attempt()
    assert client.post("/api/v1/attempts",json=stored).status_code==200
    payload={"studentId":stored["studentId"],"sessionId":stored["sessionId"],
      "questionId":stored["questionId"],
      "expectedStableRegionId":stored["expectedStableRegionId"],
      "selectedStableRegionId":stored["selectedStableRegionId"],
      "correct":False,"responseTimeSeconds":4,"attemptNumber":1,"hintLevel":1}
    response=client.post("/api/v1/hints",json=payload)
    assert response.status_code==200 and response.json()["source"]=="mock"
    row=client.get("/api/v1/teacher/export.json").json()["attempts"][0]
    assert row["backboard_state"]=="synced" and row["backboard_reference"].startswith("mock-")


def test_duplicate_event_is_idempotently_rejected(tmp_path):
    client=TestClient(create_app(settings(tmp_path))); payload=attempt()
    first=client.post("/api/v1/attempts",json=payload).json()
    second=client.post("/api/v1/attempts",json=payload).json()
    assert first["accepted"] is True and second["duplicate"] is True


def test_prototype_token_is_required_when_configured(tmp_path):
    client=TestClient(create_app(settings(tmp_path,token="local-secret")))
    assert client.post("/api/v1/attempts",json=attempt()).status_code==401
    assert client.post("/api/v1/attempts",json=attempt(),
                       headers={"X-Quiz-Token":"local-secret"}).status_code==200


def test_token_protects_student_teacher_status_and_exports(tmp_path):
    client=TestClient(create_app(settings(tmp_path,token="local-secret")))
    protected = [
        ("GET", "/api/v1/status", None),
        ("POST", "/api/v1/students", {"studentId":"student_001"}),
        ("POST", "/api/v1/hints", {"studentId":"student_001","sessionId":"s1",
          "questionId":"q1","expectedStableRegionId":"LeftRamus"}),
        ("GET", "/api/v1/teacher/students", None),
        ("GET", "/api/v1/teacher/regions", None),
        ("GET", "/api/v1/teacher/export.csv", None),
        ("GET", "/api/v1/teacher/export.json", None),
    ]
    for method,path,body in protected:
        assert client.request(method,path,json=body).status_code==401
        assert client.request(method,path,json=body,
                              headers={"X-Quiz-Token":"wrong"}).status_code==401
    assert client.get("/health").status_code==200


def test_payload_limit_and_rate_limit_are_bounded(tmp_path):
    cfg=replace(settings(tmp_path),max_payload_bytes=4096,rate_limit_requests=3,
                rate_limit_window_seconds=60)
    client=TestClient(create_app(cfg))
    assert client.post("/api/v1/attempts",content=b"x"*4097,
                       headers={"Content-Type":"application/json"}).status_code==413
    assert [client.get("/api/v1/status").status_code for _ in range(4)]==[200,200,200,429]


def test_students_and_statistics_are_calculated_and_isolated(tmp_path):
    client=TestClient(create_app(settings(tmp_path)))
    client.post("/api/v1/attempts",json=attempt(student="student_001",correct=False))
    client.post("/api/v1/attempts",json=attempt(student="student_001",correct=True))
    client.post("/api/v1/attempts",json=attempt(student="student_002",correct=True))
    one=client.get("/api/v1/teacher/students/student_001").json()
    two=client.get("/api/v1/teacher/students/student_002").json()
    assert one["attempts"]==2 and one["accuracy"]==.5
    assert two["attempts"]==1 and two["accuracy"]==1
    assert one["confusion_pairs"][0]["selected"]=="MentalProtuberance"


def test_csv_and_json_export_are_authoritative_sqlite_rows(tmp_path):
    client=TestClient(create_app(settings(tmp_path))); client.post("/api/v1/attempts",json=attempt())
    csv=client.get("/api/v1/teacher/export.csv")
    js=client.get("/api/v1/teacher/export.json")
    assert csv.status_code==200 and "LeftMentalForamen" in csv.text
    assert len(js.json()["attempts"])==1


def test_missing_api_key_real_mode_falls_back_without_request(tmp_path):
    client=TestClient(create_app(settings(tmp_path,mock=False,key="")))
    payload={"studentId":"student_001","sessionId":"session_test","questionId":"q1",
      "expectedStableRegionId":"LeftRamus","selectedStableRegionId":"RightRamus",
      "correct":False,"responseTimeSeconds":4,"attemptNumber":1,"hintLevel":1}
    assert client.post("/api/v1/hints",json=payload).json()["source"]=="local-fallback"


def test_memory_policy_first_error_off_second_pair_auto_and_duplicate(tmp_path):
    client=TestClient(create_app(settings(tmp_path)))
    first=attempt(); second=attempt()
    assert client.post("/api/v1/attempts",json=first).status_code==200
    off=client.post("/api/v1/learning-events",json={"eventId":first["eventId"],
        "studentId":"student_001"}).json()
    assert off["memoryAction"]=="off" and off["reason"]=="not_durable_yet"
    assert client.post("/api/v1/attempts",json=second).status_code==200
    auto=client.post("/api/v1/learning-events",json={"eventId":second["eventId"],
        "studentId":"student_001"}).json()
    duplicate=client.post("/api/v1/learning-events",json={"eventId":second["eventId"],
        "studentId":"student_001"}).json()
    assert auto["accepted"] and auto["memoryAction"]=="Auto" and auto["source"]=="mock"
    assert duplicate["accepted"] and duplicate["duplicate"]
    assert len(client.app.state.database.memory_event_rows())==1


def test_readonly_hint_never_uses_auto_or_existing_thread(tmp_path):
    cfg=settings(tmp_path,mock=False,key="TEST_KEY")
    database=Database(cfg.database_path)
    database.save_assistant_state("student_001","assistant-test","old-thread")
    message_calls=0
    def handler(request:httpx.Request):
        nonlocal message_calls
        assert request.url.path.endswith("/threads/messages")
        message_calls+=1
        body=json.loads(request.content)
        assert body["memory"]=="Readonly" and "thread_id" not in body
        return httpx.Response(200,json={"assistant_id":"assistant-test","thread_id":"new-thread",
          "message_id":"message-test","content":"Readonly personalized hint.",
          "input_tokens":10,"output_tokens":4,"total_tokens":14},request=request)
    client=TestClient(create_app(cfg,httpx.MockTransport(handler)))
    response=client.post("/api/v1/hints",json={"studentId":"student_001","sessionId":"s1",
      "questionId":"q1","expectedStableRegionId":"LeftRamus",
      "selectedStableRegionId":"RightRamus","correct":False})
    assert response.status_code==200 and response.json()["source"]=="backboard"
    assert message_calls==1


def test_real_policy_route_uses_one_auto_message_and_is_idempotent(tmp_path):
    cfg=settings(tmp_path,mock=False,key="TEST_KEY")
    database=Database(cfg.database_path)
    database.save_assistant_state("student_001","assistant-test","")
    calls=0
    def handler(request:httpx.Request):
        nonlocal calls; calls+=1
        body=json.loads(request.content)
        assert body["memory"]=="Auto" and "thread_id" not in body
        assert "on 2 separate deterministic quiz attempts" in body["content"]
        return httpx.Response(200,json={"assistant_id":"assistant-test","thread_id":"memory-thread",
          "message_id":"memory-message","content":"Supportive memory response."},request=request)
    client=TestClient(create_app(cfg,httpx.MockTransport(handler)))
    first=attempt(); second=attempt()
    client.post("/api/v1/attempts",json=first);client.post("/api/v1/attempts",json=second)
    body={"eventId":second["eventId"],"studentId":"student_001"}
    assert client.post("/api/v1/learning-events",json=body).json()["source"]=="backboard"
    assert client.post("/api/v1/learning-events",json=body).json()["duplicate"] is True
    assert calls==1


def test_real_teacher_ai_is_disabled_by_default(tmp_path):
    cfg=settings(tmp_path,mock=False,key="TEST_KEY")
    def forbidden(_request): raise AssertionError("teacher request must not occur")
    client=TestClient(create_app(cfg,httpx.MockTransport(forbidden)))
    stored=attempt();client.post("/api/v1/attempts",json=stored)
    response=client.post("/api/v1/session-summary",json={"studentId":"student_001",
      "sessionId":"session_test"})
    assert response.status_code==200
    assert response.json()["aiGenerated"]["summary"].startswith("Local summary")


def test_timeout_retries_are_bounded_and_secret_is_redacted(tmp_path,caplog):
    calls=0
    def timeout(_request):
        nonlocal calls; calls+=1
        raise httpx.ReadTimeout("simulated")
    cfg=settings(tmp_path,mock=False,key="TOP_SECRET_VALUE")
    client=BackboardClient(cfg,Database(cfg.database_path),httpx.MockTransport(timeout))
    request=HintRequest(studentId="student_001",sessionId="session_test",questionId="q1",
      expectedStableRegionId="LeftRamus",selectedStableRegionId="RightRamus")
    with caplog.at_level(logging.WARNING), pytest.raises(RuntimeError): asyncio.run(client.hint(request))
    assert calls==2 and "TOP_SECRET_VALUE" not in caplog.text
