from __future__ import annotations

import hashlib
import importlib.util
import re
import sqlite3
import subprocess
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.demo_database import DemoAwareDatabase
from app.main import create_app

PROXY_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = PROXY_ROOT.parents[1]
SEED_PATH = PROXY_ROOT / "scripts" / "seed_synthetic_teacher_demo.py"
DASHBOARD = PROXY_ROOT / "app" / "dashboard.html"
QUESTION_BANK = PROJECT_ROOT / "Assets/JawAR/Quiz/Data/JawQuizStarterBank.asset"
REGION_MAP = PROJECT_ROOT / "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap.asset"

spec = importlib.util.spec_from_file_location("synthetic_seed", SEED_PATH)
seed_module = importlib.util.module_from_spec(spec)
assert spec and spec.loader
sys.modules[spec.name] = seed_module
spec.loader.exec_module(seed_module)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def make_demo(tmp_path: Path) -> Path:
    path = tmp_path / "jaw_quiz_SYNTHETIC_DEMO.sqlite3"
    seed_module.seed(path)
    return path


def config(path: Path) -> Settings:
    return Settings(path, False, "SHOULD_NEVER_BE_USED", "https://invalid.example",
                    "", ("http://localhost:8765",))


def test_seed_is_deterministic_idempotent_and_requires_explicit_replacement(tmp_path):
    path = make_demo(tmp_path)
    first = digest(path)
    with pytest.raises(FileExistsError):
        seed_module.seed(path)
    assert digest(path) == first
    seed_module.seed(path, replace=True)
    assert digest(path) == first


def test_seed_does_not_read_or_modify_normal_databases(tmp_path, monkeypatch):
    normal = PROXY_ROOT / "data" / "quiz.sqlite3"
    before = digest(normal)
    real_connect = sqlite3.connect
    def guarded(target, *args, **kwargs):
        assert Path(target).resolve() != normal.resolve()
        return real_connect(target, *args, **kwargs)
    monkeypatch.setattr(sqlite3, "connect", guarded)
    seed_module.seed(tmp_path / "isolated.sqlite3")
    assert digest(normal) == before


def test_five_only_fictional_students_and_unique_behavioural_profiles(tmp_path):
    db = DemoAwareDatabase(make_demo(tmp_path))
    rows = db.student_rows()
    assert [x["display_name"] for x in rows] == [f"Synthetic Student {i}" for i in range(1, 6)]
    assert len(rows) == 5 and all(x["synthetic"] for x in rows)
    assert len({x["profile_key"] for x in rows}) == 5
    assert all(25 <= x["attempts"] <= 40 for x in rows)
    s3 = rows[2]
    assert s3["repeats_used"] > max(x["repeats_used"] for x in rows if x is not s3)
    s4 = db.student_detail("student_synthetic_4")
    assert s4["recent_sessions"][-1]["first_attempt_accuracy"] > s4["recent_sessions"][0]["first_attempt_accuracy"]
    assert s4["recent_sessions"][-1]["average_response_time"] < s4["recent_sessions"][0]["average_response_time"]


def test_student_1_left_right_pattern_and_student_5_performance(tmp_path):
    db = DemoAwareDatabase(make_demo(tmp_path))
    one = db.student_detail("student_synthetic_1")
    five = db.student_detail("student_synthetic_5")
    assert one["left_right_confusion_rate"] >= .60
    assert one["left_right_confusion_count"] >= 6
    assert any(x["expected"] == "LeftRamus" and x["selected"] == "RightRamus"
               for x in one["confusion_pairs"])
    assert five["accuracy"] > one["accuracy"] and five["accuracy"] < 1.0
    controls = {x["region"]: x["accuracy"] for x in one["regions"]}
    assert controls["MentalProtuberance"] == 1 and controls["LowerIncisors"] == 1


def test_dashboard_totals_disclosure_badges_button_and_no_network(tmp_path):
    path = make_demo(tmp_path)
    def forbidden(_request):
        raise AssertionError("No network or Backboard request is permitted in synthetic demo")
    import httpx
    client = TestClient(create_app(config(path), httpx.MockTransport(forbidden)))
    status = client.get("/api/v1/status").json()
    assert status["mode"] == "synthetic-local"
    assert status["syntheticDemo"] is True and status["externalServicesEnabled"] is False
    students = client.get("/api/v1/teacher/students").json()["students"]
    with sqlite3.connect(path) as con:
        assert sum(x["attempts"] for x in students) == con.execute("SELECT COUNT(*) FROM attempts").fetchone()[0]
    detail = client.get("/api/v1/teacher/students/student_synthetic_1").json()
    with sqlite3.connect(path) as con:
        event_id, session_id, question_id = con.execute("SELECT event_id,session_id,question_id FROM attempts LIMIT 1").fetchone()
    learning = client.post("/api/v1/learning-events", json={"eventId":event_id,"studentId":"student_synthetic_1"})
    assert learning.json()["source"] == "local-policy"
    hint = client.post("/api/v1/hints", json={"studentId":"student_synthetic_1","sessionId":session_id,"questionId":question_id,"expectedStableRegionId":"LeftRamus","selectedStableRegionId":"RightRamus"})
    assert hint.json()["source"] == "local-fallback"
    summary = client.post("/api/v1/session-summary", json={"studentId":"student_synthetic_1","sessionId":session_id})
    assert summary.json()["aiGenerated"]["summary"].startswith("Local summary")
    assert detail["attempts"] == students[0]["attempts"]
    html = DASHBOARD.read_text(encoding="utf-8")
    assert "SYNTHETIC DEMONSTRATION DATA" in html
    assert "synthetic-badge" in html and "Generate Tailored Paper Test" in html
    assert "PDF generation is demonstrated for Synthetic Student 1" in html


def test_local_pdf_has_two_pages_disclosure_key_existing_regions_and_no_third_party_image(tmp_path):
    pytest.importorskip("reportlab")
    from app.synthetic_pdf import QUESTIONS, generate_tailored_pdf
    db = DemoAwareDatabase(make_demo(tmp_path))
    pdf = generate_tailored_pdf(db, tmp_path / "pdf")
    assert pdf.is_file() and "Synthetic_Student_1_Tailored_Jaw_Test_" in pdf.name
    data = pdf.read_bytes()
    assert data.startswith(b"%PDF-")
    text = subprocess.check_output(["pdftotext", str(pdf), "-"]).decode("utf-8")
    assert "Tailored Lower-Jaw Assessment" in text
    assert "Teacher Answer Key & Rationale" in text
    assert "SYNTHETIC DEMONSTRATION DATA" in text and "SYNTHETIC DEMO" in text
    assert text.count("Page ") == 2
    question_regions = {region for _, region, _, _ in QUESTIONS}
    bank_regions = set(re.findall(r"expectedRegionId:\s*([A-Za-z0-9_.:-]+)", QUESTION_BANK.read_text()))
    map_regions = set(re.findall(r"stableId:\s*([A-Za-z0-9_.:-]+)", REGION_MAP.read_text()))
    assert question_regions <= bank_regions & map_regions
    assert len(QUESTIONS) == 10
    assert all(f"{number}." in text or f"{number}\n" in text for number, *_ in QUESTIONS)
    assert "RegisteredNurseRN" not in text and "third-party" not in text.lower()
    assert (tmp_path / "pdf" / "assets" / "project_jaw_front_oblique.png").is_file()


def test_pdf_endpoint_only_allows_student_1(tmp_path, monkeypatch):
    path = make_demo(tmp_path)
    generated = tmp_path / "Synthetic_Student_1_Tailored_Jaw_Test_fixed.pdf"
    generated.write_bytes(b"%PDF-local-test")
    import app.synthetic_pdf
    monkeypatch.setattr(app.synthetic_pdf, "generate_tailored_pdf", lambda database: generated)
    client = TestClient(create_app(config(path)))
    assert client.post("/api/v1/teacher/students/student_synthetic_1/tailored-test").status_code == 200
    assert client.post("/api/v1/teacher/students/student_synthetic_2/tailored-test").status_code == 403
