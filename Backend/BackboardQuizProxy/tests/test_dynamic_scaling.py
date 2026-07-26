from __future__ import annotations

import asyncio
import hashlib
import importlib.util
import json
import sqlite3
import sys
from pathlib import Path

import httpx
import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.dynamic_backboard import (PAID_CALL_LOCK, DynamicBackboard,
                                   DynamicWorkflowError, validate_checkpoint_analysis)
from app.dynamic_database import DynamicScalingDatabase
from app.main import create_app
from app.skeleton_catalog import ALLOWLIST, PRESERVED_PDF, PRESERVED_PDF_SHA256

ROOT = Path(__file__).resolve().parents[1]
SEED = ROOT / "scripts/seed_dynamic_skeleton_scaling.py"
spec = importlib.util.spec_from_file_location("dynamic_seed", SEED)
dynamic_seed = importlib.util.module_from_spec(spec)
assert spec and spec.loader
sys.modules[spec.name] = dynamic_seed
spec.loader.exec_module(dynamic_seed)


def make_db(tmp_path: Path, students: int = 0) -> Path:
    path = tmp_path / "dynamic.sqlite3"
    dynamic_seed.initialize(path)
    for number in range(6, 6 + students):
        dynamic_seed.add_student(path, number)
    return path


def settings(path: Path) -> Settings:
    return Settings(path, False, "TEST_SECRET_NEVER_LOG", "https://app.backboard.io/api",
                    "", ("http://localhost",), max_attempts=1)


def analysis(label: str, checkpoint: int) -> dict:
    value = {
        "synthetic": True, "scope": "student_checkpoint", "student_label": label,
        "model": "gpt-5.5", "checkpoint": checkpoint,
        "rounds": {"start": (checkpoint - 1) * 4 + 1, "end": checkpoint * 4},
        "current_strengths": ["Skull", "Pelvis"],
        "current_weaknesses": ["LeftRadius", "RightUlna"],
        "persistent_confusion_pairs": [{
            "expected": "LeftRadius", "selected": "LeftUlna",
            "evidence": "Observed in fictional checkpoint events."}],
        "progress_since_previous_checkpoint": "Calculated accuracy and pacing changed.",
        "response_time_and_hint_trends": {
            "response_time": "declined", "hints": "declined",
            "repeat_and_skip": "rare"},
        "recommended_teaching_intervention": "Use side-matched comparison practice.",
        "focus_for_next_four_rounds": ["Contrast radius and ulna."],
        "evidence": ["Four local fictional rounds were summarized."],
    }
    if checkpoint == 5:
        value["final_semester_profile"] = "Improved with one persistent confusion pair."
        value["recommended_future_assessment_focus"] = [
            "Mixed-view verified-region retrieval."]
    return value


class MockBackboard:
    def __init__(self, *, stale: bool = False, model: str = "gpt-5.5"):
        self.spend = .881412
        self.stale = stale
        self.model = model
        self.calls = []
        self.threads = 0

    def handler(self, request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content or b"{}") if request.method == "POST" else {}
        self.calls.append((request.method, request.url.path, body))
        if request.url.path.endswith("/billing/usage/summary"):
            return httpx.Response(200, json={"total_spending_usd": self.spend}, request=request)
        if request.url.path.endswith("/models"):
            return httpx.Response(200, json={"models": [{
                "provider": "openai", "name": self.model,
                "supports_json_output": True}], "total": 1}, request=request)
        if body.get("send_to_llm") == "false":
            if not body.get("thread_id"):
                self.threads += 1
            if not self.stale:
                self.spend = round(self.spend + .002, 6)
            return httpx.Response(200, json={
                "assistant_id": f"assistant-{self.threads}",
                "thread_id": body.get("thread_id") or f"thread-{self.threads}",
                "content": None}, request=request)
        prompt = json.loads(body["content"])
        checkpoint = prompt["required_schema"]["checkpoint"]
        label = prompt["required_schema"]["student_label"]
        if not self.stale:
            self.spend = round(self.spend + .048, 6)
        return httpx.Response(200, json={
            "assistant_id": body["assistant_id"], "thread_id": body["thread_id"],
            "model_provider": "openai", "model_name": "gpt-5.5",
            "content": json.dumps(analysis(label, checkpoint))}, request=request)


def test_dynamic_students_have_20_rounds_bounded_events_and_unique_profiles(tmp_path):
    path = make_db(tmp_path, students=15)
    db = DynamicScalingDatabase(path)
    rows = db.student_rows()
    assert len(rows) == 20
    assert len({row["profile_key"] for row in rows}) == 20
    assert all(row["synthetic"] and row["rounds_completed"] == 20 for row in rows)
    assert all(400 <= row["attempts"] <= 800 for row in rows)
    with sqlite3.connect(path) as con:
        round_counts = con.execute("""SELECT student_id,round_number,COUNT(*)
          FROM attempts JOIN semester_event_metadata USING(event_id)
          GROUP BY student_id,round_number""").fetchall()
        assert len(round_counts) == 400
        assert all(20 <= row[2] <= 40 for row in round_counts)
        assert {row[0] for row in con.execute(
            "SELECT DISTINCT expected_region_id FROM attempts")} <= ALLOWLIST


def test_first_checkpoint_calibrates_and_partial_student_is_not_counted(tmp_path):
    path = make_db(tmp_path, students=1)
    mock = MockBackboard()
    workflow = DynamicBackboard(
        settings(path), DynamicScalingDatabase(path), httpx.MockTransport(mock.handler))
    result = asyncio.run(workflow.process_student(
        "student_synthetic_6", max_new_checkpoints=1))
    status = workflow.status()
    assert result["status"] == "partially_processed"
    assert result["checkpoint_count"] == 1 and result["generation_count"] == 1
    assert result["included_in_x"] == 0 and status["current_x"] == 5
    assert status["reported_spend_usd"] == pytest.approx(.931412)
    assert status["estimated_next_student_usd"] == pytest.approx(.2875)


def test_complete_student_uses_five_sequential_generations_one_thread_and_caches(tmp_path):
    path = make_db(tmp_path, students=2)
    mock = MockBackboard()
    workflow = DynamicBackboard(
        settings(path), DynamicScalingDatabase(path), httpx.MockTransport(mock.handler))
    first = asyncio.run(workflow.process_student("student_synthetic_6"))
    second = asyncio.run(workflow.process_student("student_synthetic_7"))
    assert first["status"] == second["status"] == "complete"
    assert workflow.status()["current_x"] == 7
    posts = [body for method, path, body in mock.calls if method == "POST"]
    generations = [body for body in posts if body.get("send_to_llm") == "true"]
    storage = [body for body in posts if body.get("send_to_llm") == "false"]
    assert len(generations) == len(storage) == 10
    assert all(body["llm_provider"] == "openai"
               and body["model_name"] == "gpt-5.5" for body in generations)
    by_student = {}
    for body in posts:
        student = body["metadata"]["fictional_student"]
        by_student.setdefault(student, set()).add(
            body.get("thread_id") or f"thread-{len(by_student) + 1}")
    with sqlite3.connect(path) as con:
        threads = con.execute("""SELECT student_id,thread_id FROM
          dynamic_student_processing WHERE student_number>=6 ORDER BY student_number""").fetchall()
        assert len({row[1] for row in threads}) == 2
        assert all(row[1] for row in threads)
    before = len(mock.calls)
    cached = asyncio.run(workflow.process_student("student_synthetic_6"))
    assert cached["status"] == "complete" and len(mock.calls) == before


def test_stale_billing_stops_without_another_generation(tmp_path, monkeypatch):
    path = make_db(tmp_path, students=1)
    mock = MockBackboard(stale=True)
    async def no_sleep(_):
        return None
    monkeypatch.setattr("app.dynamic_backboard.asyncio.sleep", no_sleep)
    workflow = DynamicBackboard(
        settings(path), DynamicScalingDatabase(path), httpx.MockTransport(mock.handler))
    with pytest.raises(DynamicWorkflowError, match="billing remained stale"):
        asyncio.run(workflow.process_student("student_synthetic_6"))
    generations = [body for method, _, body in mock.calls
                   if method == "POST" and body.get("send_to_llm") == "true"]
    assert len(generations) == 1
    assert workflow.status()["current_x"] == 5


def test_gpt55_only_no_fallback_and_emergency_disable(tmp_path):
    path = make_db(tmp_path, students=1)
    wrong = MockBackboard(model="gpt-4o")
    workflow = DynamicBackboard(
        settings(path), DynamicScalingDatabase(path), httpx.MockTransport(wrong.handler))
    with pytest.raises(DynamicWorkflowError, match="gpt-5.5 is unavailable"):
        asyncio.run(workflow.process_student("student_synthetic_6"))
    assert not [body for method, _, body in wrong.calls if method == "POST"]
    workflow.disable()
    with pytest.raises(DynamicWorkflowError, match="disabled"):
        asyncio.run(workflow.process_student("student_synthetic_6"))


def test_no_concurrent_paid_workflows(tmp_path):
    path = make_db(tmp_path, students=1)
    workflow = DynamicBackboard(settings(path), DynamicScalingDatabase(path))
    async def check():
        await PAID_CALL_LOCK.acquire()
        try:
            with pytest.raises(DynamicWorkflowError, match="already active"):
                await workflow.process_student("student_synthetic_6")
        finally:
            PAID_CALL_LOCK.release()
    asyncio.run(check())


def test_dashboard_is_dynamic_paginated_and_has_only_safe_controls(tmp_path):
    path = make_db(tmp_path, students=7)
    client = TestClient(create_app(settings(path)))
    html = client.get("/teacher").text
    for text in ("Process Next Synthetic Student", "Review Cached Analysis",
                 "Disable Further Backboard Requests", "statusFilter", "pageSize"):
        assert text in html
    assert "process everyone" in html.lower()
    assert "Process Everyone" not in html
    assert html.count("/api/v1/teacher/preserved-jaw-assessment") == 1
    assert "Generate PDF" not in html and "gpt-4o" not in html
    rows = client.get("/api/v1/teacher/students").json()["students"]
    assert len(rows) == 12 and sum(row["included_in_x"] for row in rows) == 5


def test_validator_and_pdf_immutability():
    assert validate_checkpoint_analysis(
        analysis("Synthetic Student 6", 5),
        student_label="Synthetic Student 6", checkpoint=5)
    bad = analysis("Synthetic Student 6", 5)
    bad["current_weaknesses"] = ["Tibia"]
    with pytest.raises(DynamicWorkflowError):
        validate_checkpoint_analysis(
            bad, student_label="Synthetic Student 6", checkpoint=5)
    assert hashlib.sha256(PRESERVED_PDF.read_bytes()).hexdigest() == PRESERVED_PDF_SHA256
