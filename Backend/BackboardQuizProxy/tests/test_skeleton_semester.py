from __future__ import annotations

import hashlib
import importlib.util
import json
import sqlite3
import sys
from pathlib import Path

import httpx
import pytest
from fastapi.testclient import TestClient

from app.backboard_semester import SemesterAnalysisError, SemesterBackboard, validate_analysis
from app.config import Settings
from app.main import create_app
from app.semester_database import SemesterDatabase
from app.skeleton_catalog import (ALLOWLIST, NON_LATERALIZED, PRESERVED_PDF,
                                  PRESERVED_PDF_SHA256, SKELETON_REGIONS)

ROOT = Path(__file__).resolve().parents[1]
SEED = ROOT / "scripts/seed_synthetic_skeleton_semester.py"
spec = importlib.util.spec_from_file_location("seedsemester", SEED)
seedsemester = importlib.util.module_from_spec(spec)
assert spec and spec.loader
sys.modules[spec.name] = seedsemester
spec.loader.exec_module(seedsemester)


def make_db(tmp_path: Path) -> Path:
    path = tmp_path / "skeleton_semester.sqlite3"
    seedsemester.seed(path)
    return path


def settings(path: Path) -> Settings:
    return Settings(path, False, "TEST_SECRET_NEVER_LOG", "https://app.backboard.io/api",
                    "", ("http://localhost",), max_attempts=1)


def analysis(scope: str, label: str = "") -> dict:
    return {
        "synthetic": True, "scope": scope, "student_label": label, "model": "gpt-5.5",
        "checkpoint_trends": [{"checkpoint": i, "observation": f"Trend {i}"}
                              for i in range(1, 6)],
        "performance_by_body_area": [
            {"body_area": x, "observation": "Calculated evidence."}
            for x in ("axial", "upper_limbs", "lower_limbs", "jaw")],
        "performance_by_side": [
            {"side": x, "observation": "Calculated evidence."}
            for x in ("left", "right", "midline")],
        "confusion_pairs": [{"expected": "LeftRadius", "selected": "LeftUlna",
                             "observation": "Observed fictional selections."}],
        "response_time_trend": "Responses became faster.",
        "support_trends": {"hints": "declined", "repeat": "declined", "skip": "rare"},
        "strongest_regions": ["Skull", "Pelvis"],
        "weakest_regions": ["LeftRadius", "RightUlna"],
        "persistent_misconceptions": ["Radius/ulna comparison remains useful."],
        "teacher_recommendations": ["Continue verified-region comparison practice."],
    }


def test_seed_exact_semester_counts_broad_coverage_and_allowlist(tmp_path):
    path = make_db(tmp_path)
    db = SemesterDatabase(path)
    rows = db.student_rows()
    assert len(rows) == 5
    assert [x["rounds_completed"] for x in rows] == [20] * 5
    totals = [x["attempts"] for x in rows]
    assert totals == [620, 588, 609, 579, 445]
    assert sum(totals) == 2841 and all(400 <= x <= 800 for x in totals)
    with sqlite3.connect(path) as con:
        used = {x[0] for x in con.execute("SELECT DISTINCT expected_region_id FROM attempts")}
        assert used <= ALLOWLIST and NON_LATERALIZED <= used
        rounds = con.execute("""SELECT student_id,round_number,COUNT(*),
          COUNT(DISTINCT body_area),COUNT(DISTINCT CASE WHEN module='whole_skeleton'
          THEN expected_region_id END) FROM attempts JOIN semester_event_metadata USING(event_id)
          GROUP BY student_id,round_number""").fetchall()
        assert len(rounds) == 100
        assert all(20 <= x[2] <= 40 and x[3] == 4 and x[4] == len(SKELETON_REGIONS)
                   for x in rounds)


def test_profiles_are_distinct_student1_spans_areas_and_controls_stay_distinct(tmp_path):
    db = SemesterDatabase(make_db(tmp_path))
    details = [db.student_detail(f"student_synthetic_{i}") for i in range(1, 6)]
    one, two, three, four, five = details
    assert len({x["profile_key"] for x in details}) == 5
    assert {x["body_area"] for x in one["reversal_by_body_area"] if x["reversals"]} == {
        "upper_limbs", "lower_limbs", "jaw"}
    assert set(one["paired_confusions"]) == {"Humerus", "Radius", "Ulna", "Femur", "Ramus"}
    assert all(one["paired_confusions"][x] > 0 for x in one["paired_confusions"])
    assert {x["region"] for x in one["non_lateralized_controls"]} == NON_LATERALIZED
    assert three["repeats_used"] > max(x["repeats_used"] for x in details if x is not three)
    assert four["checkpoints"][-1]["first_attempt_accuracy"] > four["checkpoints"][0]["first_attempt_accuracy"]
    assert four["checkpoints"][-1]["average_response_time"] < four["checkpoints"][0]["average_response_time"]
    assert five["first_attempt_accuracy"] > one["first_attempt_accuracy"] and five["first_attempt_accuracy"] < 1
    assert any(x["expected"].endswith("Radius") and x["selected"].endswith("Ulna")
               for x in two["confusion_pairs"])


def test_body_area_totals_equal_database_events(tmp_path):
    db = SemesterDatabase(make_db(tmp_path))
    for i in range(1, 6):
        detail = db.student_detail(f"student_synthetic_{i}")
        assert sum(x["events"] for x in detail["body_areas"]) == detail["attempts"]
        assert sum(x["events"] for x in detail["sides"]) == detail["attempts"]


def test_strict_analysis_validator_rejects_unsupported_regions_and_assessments():
    assert validate_analysis(analysis("student", "Synthetic Student 1"),
                             expected_scope="student",
                             student_label="Synthetic Student 1")
    bad = analysis("student", "Synthetic Student 1")
    bad["weakest_regions"] = ["Tibia"]
    with pytest.raises(SemesterAnalysisError):
        validate_analysis(bad, expected_scope="student", student_label="Synthetic Student 1")
    bad = analysis("student", "Synthetic Student 1")
    bad["teacher_recommendations"] = ["Review the clavicle."]
    with pytest.raises(SemesterAnalysisError):
        validate_analysis(bad, expected_scope="student", student_label="Synthetic Student 1")
    bad = analysis("student", "Synthetic Student 1")
    bad["questions"] = [{"target_region": "Skull"}]
    with pytest.raises(SemesterAnalysisError):
        validate_analysis(bad, expected_scope="student", student_label="Synthetic Student 1")


def test_only_preserved_student1_jaw_pdf_is_exposed_and_no_generator_runs(tmp_path, monkeypatch):
    assert hashlib.sha256(PRESERVED_PDF.read_bytes()).hexdigest() == PRESERVED_PDF_SHA256
    client = TestClient(create_app(settings(make_db(tmp_path))))
    detail1 = client.get("/api/v1/teacher/students/student_synthetic_1").json()
    assert detail1["preserved_assessment"]["student"] == "Synthetic Student 1"
    assert detail1["preserved_assessment"]["scope"] == "Lower jaw"
    assert "five-round" in detail1["preserved_assessment"]["historical_context"]
    assert "20-round" not in detail1["preserved_assessment"]["historical_context"]
    for i in range(2, 6):
        detail = client.get(f"/api/v1/teacher/students/student_synthetic_{i}").json()
        assert detail["preserved_assessment"] is None
        assert client.post(
            f"/api/v1/teacher/students/student_synthetic_{i}/tailored-test").status_code == 403
    assert client.post(
        "/api/v1/teacher/students/student_synthetic_1/tailored-test").status_code == 403
    response = client.get("/api/v1/teacher/preserved-jaw-assessment")
    assert response.status_code == 200
    assert hashlib.sha256(response.content).hexdigest() == PRESERVED_PDF_SHA256
    assert client.get("/api/v1/teacher/backboard-tests/not-the-preserved-file.pdf").status_code == 404
    assert client.get("/api/v1/teacher/tailored-tests/../../../../etc/passwd").status_code == 404
    html = client.get("/teacher").text
    assert "Existing Tailored Jaw Assessment" in html
    assert "was not generated from the current 20-round semester dataset" in html
    assert "Generate PDF" not in html and "Generate Tailored" not in html


def test_mocked_backboard_is_analysis_only_six_generations_and_30_storage_ops(tmp_path):
    calls = []
    def handler(request: httpx.Request):
        body = json.loads(request.content or b"{}") if request.method == "POST" else {}
        calls.append((request.method, request.url.path, body))
        if request.method == "GET":
            return httpx.Response(200, json={"models": [{
                "provider": "openai", "name": "gpt-5.5",
                "supports_json_output": True}], "total": 1}, request=request)
        generated = body.get("send_to_llm") == "true"
        scope = "class" if body.get("metadata", {}).get("class_rollup") else "student"
        content = json.loads(body.get("content", "{}"))
        label = content.get("constraints", {}).get("student_label", "")
        return httpx.Response(200, json={
            "assistant_id": "assistant-safe", "thread_id": "thread-safe",
            "model_provider": "openai" if generated else None,
            "model_name": "gpt-5.5" if generated else None,
            "cost_usd": .01 if generated else 0,
            "content": json.dumps(analysis(scope, label)) if generated else None}, request=request)
    path = make_db(tmp_path)
    workflow = SemesterBackboard(
        settings(path), SemesterDatabase(path), httpx.MockTransport(handler))
    catalogue = __import__("asyncio").run(workflow.discover())
    result = __import__("asyncio").run(
        workflow.run(catalogue["provider"], catalogue["model_name"]))
    posts = [x[2] for x in calls if x[0] == "POST"]
    storage = [x for x in posts if x.get("send_to_llm") == "false"]
    generations = [x for x in posts if x.get("send_to_llm") == "true"]
    assert len(storage) == 30 and len(generations) == 6
    assert result["storageOperations"] == 30 and result["generationOperations"] == 6
    assert result["verifiedSpendingUsd"] == pytest.approx(.06)
    packed = json.dumps(posts)
    for forbidden in ("assessment_specification", '"questions"', "create a test", "PDF"):
        assert forbidden not in packed
    assert all(x["model_name"] == "gpt-5.5" and x["memory"] == "Readonly"
               for x in generations)
