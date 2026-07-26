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

from app.backboard_five_round import ProposalError, discover_gpt55, validate_proposal
from app.backboard_pdf import LEADER_ENDPOINTS
from app.config import Settings
from app.five_round_database import FiveRoundDatabase
from app.main import create_app

ROOT = Path(__file__).resolve().parents[1]
SEED = ROOT / "scripts" / "seed_synthetic_5round_demo.py"
spec = importlib.util.spec_from_file_location("seed5", SEED)
seed5 = importlib.util.module_from_spec(spec)
assert spec and spec.loader
sys.modules[spec.name] = seed5
spec.loader.exec_module(seed5)


def make_db(tmp_path: Path) -> Path:
    path = tmp_path / "jaw_quiz_SYNTHETIC_5ROUND_DEMO.sqlite3"
    seed5.seed(path)
    return path


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def settings(path: Path) -> Settings:
    return Settings(path, False, "TEST_SECRET_NEVER_LOG", "https://app.backboard.io/api",
                    "", ("http://localhost",), max_attempts=1)


def proposal(regions: list[str], model: str = "gpt-5.5") -> dict:
    questions = []
    for index, region in enumerate(regions[:8], 1):
        questions.append({
            "id": index, "type": "left_right" if index < 5 else "diagram_label",
            "prompt": f"Identify the indicated local region {index}.",
            "target_region": region, "view": "front", "answer": region,
            "rationale": "Checks the fictional five-round pattern.",
            "evidence_basis": "Derived from the compact synthetic summary."})
    return {
        "synthetic": True, "student_label": "Synthetic Student 1", "model": model,
        "five_round_summary": "Left/right errors declined but persisted through Round 5.",
        "strengths": [{"region": regions[-1], "evidence": "Strong fictional first attempts."}],
        "needs_practice": [{"skill": "left/right", "evidence": "Persistent reversals",
                            "priority": "high"}],
        "confusion_pairs": [{"requested": "LeftRamus", "selected": "RightRamus",
                             "evidence_count": 5, "rounds_observed": [1, 2, 3, 4, 5]}],
        "progression": "Modest improvement.", "assessment_strategy": "Bilateral plus controls.",
        "questions": questions}


def test_seed_exact_counts_coverage_order_retries_and_idempotence(tmp_path):
    path = make_db(tmp_path)
    before = digest(path)
    with pytest.raises(FileExistsError):
        seed5.seed(path)
    assert digest(path) == before
    seed5.seed(path, replace=True)
    assert digest(path) == before
    with sqlite3.connect(path) as con:
        assert con.execute("SELECT COUNT(*) FROM students").fetchone()[0] == 5
        rows = con.execute("""SELECT student_id,round_number,COUNT(*),
          COUNT(DISTINCT question_id),MIN(response_time_seconds),MIN(utc_timestamp),
          MAX(utc_timestamp) FROM attempts JOIN attempt_demo_metadata USING(event_id)
          GROUP BY student_id,round_number ORDER BY student_id,round_number""").fetchall()
        assert len(rows) == 25
        assert all(25 <= r[2] <= 40 and r[3] == 23 and r[4] > 0 and r[5] <= r[6]
                   for r in rows)
        assert len({r[2] for r in rows}) > 1
        totals = [r[0] for r in con.execute(
            "SELECT COUNT(*) FROM attempts GROUP BY student_id ORDER BY student_id")]
        assert totals == [171, 159, 160, 164, 132]
        assert sum(totals) == 786
        assert con.execute("""SELECT COUNT(*) FROM attempts a WHERE attempt_number>1
          AND NOT EXISTS(SELECT 1 FROM attempts f WHERE f.student_id=a.student_id
          AND f.session_id=a.session_id AND f.question_id=a.question_id
          AND f.attempt_number=a.attempt_number-1 AND f.correct=0
          AND f.utc_timestamp<a.utc_timestamp)""").fetchone()[0] == 0


def test_profiles_progression_and_student1_round5_weakness(tmp_path):
    db = FiveRoundDatabase(make_db(tmp_path))
    rows = db.student_rows()
    assert [x["rounds_completed"] for x in rows] == [5] * 5
    assert len({x["profile_key"] for x in rows}) == 5
    details = [db.student_detail(f"student_synthetic_{i}") for i in range(1, 6)]
    one, two, three, four, five = details
    assert one["rounds"][-1]["left_right_reversals"] > 0
    assert one["rounds"][-1]["left_right_reversal_rate"] < one["rounds"][0]["left_right_reversal_rate"]
    assert any(x["expected"] == "LeftRamus" and x["selected"] == "RightRamus"
               for x in one["confusion_pairs"])
    assert three["repeats_used"] > max(x["repeats_used"] for x in details if x is not three)
    assert four["rounds"][-1]["first_attempt_accuracy"] > four["rounds"][0]["first_attempt_accuracy"]
    assert four["rounds"][-1]["average_response_time"] < four["rounds"][0]["average_response_time"]
    assert five["first_attempt_accuracy"] > one["first_attempt_accuracy"]
    assert five["first_attempt_accuracy"] < 1
    assert two["left_right_confusion_rate"] < one["left_right_confusion_rate"]


def test_dashboard_database_totals_and_navigation_are_network_free(tmp_path):
    calls = []
    def forbidden(request):
        calls.append(request)
        raise AssertionError("ordinary navigation must not contact Backboard")
    path = make_db(tmp_path)
    client = TestClient(create_app(settings(path), httpx.MockTransport(forbidden)))
    assert client.get("/health").status_code == 200
    students = client.get("/api/v1/teacher/students").json()["students"]
    assert sum(x["attempts"] for x in students) == 786
    assert client.get("/api/v1/teacher/students/student_synthetic_1").json()["rounds_completed"] == 5
    html = client.get("/teacher").text
    for text in ("SYNTHETIC DEMONSTRATION DATA", "Generate Local Demonstration Test",
                 "Generate with Backboard", "Use Previous Backboard Result",
                 "Continue with Backboard", "Synthetic"):
        assert text in html
    assert calls == []
    assert client.post("/api/v1/teacher/students/student_synthetic_1/backboard-test",
                       json={}).status_code == 409
    assert calls == []
    assert client.post("/api/v1/teacher/students/student_synthetic_2/backboard-test",
                       json={"confirmed": True}).status_code == 403


def test_model_discovery_exact_no_fallback_and_validation(tmp_path):
    unavailable = discover_gpt55({"models": [
        {"provider": "openai", "name": "gpt-4o", "supports_json_output": True}]})
    assert not unavailable["available"] and not unavailable["compatible"]
    found = discover_gpt55({"models": [
        {"provider": "provider-x", "name": "gpt-5.5", "supports_json_output": True}]})
    assert (found["provider"], found["model_name"], found["compatible"]) == (
        "provider-x", "gpt-5.5", True)
    allow = {region for _, region in seed5.assets()[0]}
    valid = proposal(sorted(allow & set(LEADER_ENDPOINTS)))
    assert validate_proposal(json.dumps(valid), allow, "gpt-5.5") == valid
    for mutation in ("region", "html", "duplicate", "model"):
        bad = proposal(sorted(allow & set(LEADER_ENDPOINTS)))
        if mutation == "region": bad["questions"][0]["target_region"] = "ImaginaryJaw"
        if mutation == "html": bad["questions"][0]["prompt"] = "<script>alert(1)</script>"
        if mutation == "duplicate": bad["questions"][1]["id"] = bad["questions"][0]["id"]
        if mutation == "model": bad["model"] = "gpt-4o"
        with pytest.raises(ProposalError):
            validate_proposal(bad, allow, "gpt-5.5")


def test_mocked_live_flow_discovery_precedes_generation_caches_and_uses_only_student1(tmp_path, monkeypatch):
    path = make_db(tmp_path)
    regions = sorted({region for _, region in seed5.assets()[0]} & set(LEADER_ENDPOINTS))
    calls: list[tuple[str, dict]] = []
    def handler(request: httpx.Request):
        body = json.loads(request.content or b"{}") if request.method == "POST" else {}
        calls.append((request.url.path, body))
        if request.method == "GET":
            return httpx.Response(200, json={"models": [{
                "provider": "provider-x", "name": "gpt-5.5",
                "supports_json_output": True}], "total": 1}, request=request)
        storage = body.get("send_to_llm") == "false"
        return httpx.Response(200, json={
            "assistant_id": "assistant-safe", "thread_id": "thread-safe",
            "message_id": "stored" if storage else "generated",
            "model_provider": None if storage else "provider-x",
            "model_name": None if storage else "gpt-5.5",
            "content": None if storage else json.dumps(proposal(regions))}, request=request)
    client = TestClient(create_app(settings(path), httpx.MockTransport(handler)))
    discovered = client.get("/api/v1/teacher/backboard/discovery").json()
    response = client.post("/api/v1/teacher/students/student_synthetic_1/backboard-test",
        json={"confirmed": True, "provider": discovered["provider"],
              "model": discovered["model_name"]})
    assert response.status_code == 200, response.text
    value = response.json()
    assert value["generationOperations"] == 1 and value["totalOperations"] == 8
    posts = [body for path_name, body in calls if path_name.endswith("/threads/messages")]
    assert len([x for x in posts if x.get("send_to_llm") == "false"]) == 5
    generations = [x for x in posts if x.get("send_to_llm") == "true"]
    assert len(generations) == 1
    assert generations[0]["llm_provider"] == "provider-x"
    assert generations[0]["model_name"] == "gpt-5.5"
    serialized = json.dumps(posts)
    assert "Synthetic Student 2" not in serialized and "student_synthetic_2" not in serialized
    assert "TEST_SECRET_NEVER_LOG" not in serialized
    before = len(calls)
    cached = client.post("/api/v1/teacher/students/student_synthetic_1/backboard-test",
        json={"confirmed": True, "provider": "provider-x", "model": "gpt-5.5"})
    assert cached.status_code == 200 and len(calls) == before
    assert client.post("/api/v1/teacher/students/student_synthetic_1/backboard-test",
        json={"confirmed": True, "regenerate": True, "provider": "provider-x",
              "model": "gpt-5.5"}).status_code == 409
