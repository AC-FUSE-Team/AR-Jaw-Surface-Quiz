from __future__ import annotations

import csv
import io
import json
import sqlite3
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator


SCHEMA = """
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS students (
  student_id TEXT PRIMARY KEY, created_utc TEXT NOT NULL,
  assistant_id TEXT, thread_id TEXT, ai_summary TEXT NOT NULL DEFAULT '',
  recommended_regions_json TEXT NOT NULL DEFAULT '[]'
);
CREATE TABLE IF NOT EXISTS attempts (
  event_id TEXT PRIMARY KEY, student_id TEXT NOT NULL, session_id TEXT NOT NULL,
  question_id TEXT NOT NULL, object_id TEXT NOT NULL, region_map_version TEXT NOT NULL,
  expected_region_id TEXT NOT NULL, selected_region_id TEXT NOT NULL,
  correct INTEGER NOT NULL, response_time_seconds REAL NOT NULL,
  attempt_number INTEGER NOT NULL, hint_level INTEGER NOT NULL, utc_timestamp TEXT NOT NULL,
  received_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  backboard_state TEXT NOT NULL DEFAULT 'queued', backboard_reference TEXT NOT NULL DEFAULT '',
  FOREIGN KEY(student_id) REFERENCES students(student_id)
);
CREATE TABLE IF NOT EXISTS memory_events (
  student_id TEXT NOT NULL, policy_key TEXT NOT NULL, trigger_event_id TEXT NOT NULL,
  reason TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'queued', response_reference TEXT NOT NULL DEFAULT '',
  created_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY(student_id, policy_key),
  FOREIGN KEY(student_id) REFERENCES students(student_id),
  FOREIGN KEY(trigger_event_id) REFERENCES attempts(event_id)
);
CREATE TABLE IF NOT EXISTS teacher_state (
  id INTEGER PRIMARY KEY CHECK (id=1), assistant_id TEXT, thread_id TEXT,
  ai_summary TEXT NOT NULL DEFAULT ''
);
INSERT OR IGNORE INTO teacher_state(id) VALUES(1);
"""


class Database:
    def __init__(self, path: Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as con:
            con.executescript(SCHEMA)

    @contextmanager
    def connect(self) -> Iterator[sqlite3.Connection]:
        con = sqlite3.connect(self.path, timeout=5)
        con.row_factory = sqlite3.Row
        con.execute("PRAGMA foreign_keys=ON")
        try:
            yield con
            con.commit()
        finally:
            con.close()

    def _has_table(self, con: sqlite3.Connection, name: str) -> bool:
        return con.execute(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (name,)
        ).fetchone() is not None

    def is_synthetic_demo(self) -> bool:
        with self.connect() as con:
            if not self._has_table(con, "demo_metadata"):
                return False
            row = con.execute(
                "SELECT value FROM demo_metadata WHERE key='synthetic_demo'"
            ).fetchone()
        return bool(row and row["value"] == "true")

    def ensure_student(self, student_id: str) -> bool:
        with self.connect() as con:
            cursor = con.execute(
                "INSERT OR IGNORE INTO students(student_id,created_utc) VALUES(?,CURRENT_TIMESTAMP)",
                (student_id,))
            return cursor.rowcount == 1

    def insert_attempt(self, data: dict) -> bool:
        self.ensure_student(data["studentId"])
        values = (
            str(data["eventId"]), data["studentId"], data["sessionId"], data["questionId"],
            data["objectId"], data["regionMapVersion"], data["expectedStableRegionId"],
            data["selectedStableRegionId"], int(data["correct"]), data["responseTimeSeconds"],
            data["attemptNumber"], data["hintLevel"], str(data["utcTimestamp"]),
        )
        with self.connect() as con:
            cursor = con.execute("""
              INSERT OR IGNORE INTO attempts(event_id,student_id,session_id,question_id,object_id,
              region_map_version,expected_region_id,selected_region_id,correct,response_time_seconds,
              attempt_number,hint_level,utc_timestamp,backboard_state)
              VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,'stored')""", values)
            return cursor.rowcount == 1

    def attempt(self, event_id: str) -> dict | None:
        with self.connect() as con:
            row = con.execute("SELECT * FROM attempts WHERE event_id=?", (event_id,)).fetchone()
        return dict(row) if row else None

    def attempts_for_student(self, student_id: str) -> list[dict]:
        with self.connect() as con:
            rows = con.execute(
                "SELECT * FROM attempts WHERE student_id=? ORDER BY received_utc,rowid", (student_id,)
            ).fetchall()
        return [dict(row) for row in rows]

    def reserve_memory_event(self, student_id: str, policy_key: str,
                             event_id: str, reason: str) -> bool:
        with self.connect() as con:
            cursor = con.execute("""
              INSERT OR IGNORE INTO memory_events(student_id,policy_key,trigger_event_id,reason)
              VALUES(?,?,?,?)""", (student_id, policy_key, event_id, reason))
            return cursor.rowcount == 1

    def memory_event(self, student_id: str, policy_key: str) -> dict | None:
        with self.connect() as con:
            row = con.execute("""SELECT * FROM memory_events
              WHERE student_id=? AND policy_key=?""", (student_id, policy_key)).fetchone()
        return dict(row) if row else None

    def complete_memory_event(self, student_id: str, policy_key: str,
                              state: str, reference: str = "") -> None:
        normalized = state if state in {"completed", "queued", "failed"} else "failed"
        with self.connect() as con:
            con.execute("""UPDATE memory_events SET state=?,response_reference=?,
              updated_utc=CURRENT_TIMESTAMP WHERE student_id=? AND policy_key=?""",
              (normalized, reference or "", student_id, policy_key))

    def memory_event_rows(self) -> list[dict]:
        with self.connect() as con:
            return [dict(row) for row in con.execute(
                "SELECT * FROM memory_events ORDER BY created_utc,student_id,policy_key")]

    def student_rows(self) -> list[dict]:
        with self.connect() as con:
            rows = con.execute("""
              SELECT s.student_id, COUNT(a.event_id) attempts,
                COALESCE(AVG(a.correct),0.0) accuracy,
                COALESCE(AVG(a.response_time_seconds),0.0) average_response_time,
                COALESCE(SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END),0) hints_used,
                COALESCE(SUM(CASE WHEN a.backboard_state='queued' THEN 1 ELSE 0 END),0) queued_sync,
                s.ai_summary, s.recommended_regions_json
              FROM students s LEFT JOIN attempts a ON a.student_id=s.student_id
              GROUP BY s.student_id ORDER BY s.student_id""").fetchall()
        return [dict(row) | {"accuracy": round(row["accuracy"], 4),
                             "average_response_time": round(row["average_response_time"], 3),
                             "recommended_review_regions": json.loads(row["recommended_regions_json"])}
                for row in rows]

    def student_detail(self, student_id: str) -> dict | None:
        students = {row["student_id"]: row for row in self.student_rows()}
        if student_id not in students: return None
        with self.connect() as con:
            regions = [dict(row) for row in con.execute("""
              SELECT expected_region_id region, COUNT(*) attempts, ROUND(AVG(correct),4) accuracy,
              ROUND(AVG(response_time_seconds),3) average_response_time
              FROM attempts WHERE student_id=? GROUP BY expected_region_id
              ORDER BY accuracy, attempts DESC, region""", (student_id,))]
            confusion = [dict(row) for row in con.execute("""
              SELECT expected_region_id expected, selected_region_id selected, COUNT(*) count
              FROM attempts WHERE student_id=? AND correct=0 GROUP BY expected_region_id,selected_region_id
              ORDER BY count DESC,expected,selected LIMIT 10""", (student_id,))]
            sessions = [dict(row) for row in con.execute("""
              SELECT session_id,COUNT(*) attempts,ROUND(AVG(correct),4) accuracy,
              MIN(utc_timestamp) started_utc,MAX(utc_timestamp) ended_utc
              FROM attempts WHERE student_id=? GROUP BY session_id ORDER BY ended_utc DESC LIMIT 10""",
              (student_id,))]
        return students[student_id] | {"regions": regions, "confusion_pairs": confusion,
                                       "recent_sessions": sessions}

    def region_rows(self) -> list[dict]:
        with self.connect() as con:
            return [dict(row) for row in con.execute("""
              SELECT expected_region_id region,COUNT(*) attempts,ROUND(AVG(correct),4) accuracy,
              ROUND(AVG(response_time_seconds),3) average_response_time,
              SUM(CASE WHEN hint_level>0 THEN 1 ELSE 0 END) hints_used
              FROM attempts GROUP BY expected_region_id ORDER BY accuracy,attempts DESC,region""")]

    def export_rows(self) -> list[dict]:
        with self.connect() as con:
            return [dict(row) for row in con.execute("SELECT * FROM attempts ORDER BY utc_timestamp,event_id")]

    def export_csv(self) -> str:
        rows = self.export_rows()
        output = io.StringIO()
        fields = list(rows[0].keys()) if rows else ["event_id", "student_id", "session_id"]
        writer = csv.DictWriter(output, fieldnames=fields)
        writer.writeheader(); writer.writerows(rows)
        return output.getvalue()

    def assistant_state(self, student_id: str) -> tuple[str, str]:
        self.ensure_student(student_id)
        with self.connect() as con:
            row = con.execute("SELECT assistant_id,thread_id FROM students WHERE student_id=?",
                              (student_id,)).fetchone()
        return (row["assistant_id"] or "", row["thread_id"] or "")

    def save_assistant_state(self, student_id: str, assistant: str, thread: str) -> None:
        self.ensure_student(student_id)
        with self.connect() as con:
            con.execute("UPDATE students SET assistant_id=?,thread_id=? WHERE student_id=?",
                        (assistant, thread, student_id))

    def teacher_assistant_state(self) -> tuple[str, str]:
        with self.connect() as con:
            row = con.execute("SELECT assistant_id,thread_id FROM teacher_state WHERE id=1").fetchone()
        return (row["assistant_id"] or "", row["thread_id"] or "")

    def save_teacher_assistant_state(self, assistant: str, thread: str, summary: str) -> None:
        with self.connect() as con:
            con.execute("UPDATE teacher_state SET assistant_id=?,thread_id=?,ai_summary=? WHERE id=1",
                        (assistant, thread, summary))

    def save_student_summary(self, student_id: str, summary: str, regions: list[str]) -> None:
        with self.connect() as con:
            con.execute("UPDATE students SET ai_summary=?,recommended_regions_json=? WHERE student_id=?",
                        (summary, json.dumps(regions), student_id))

    def save_attempt_backboard_reference(self, student_id: str, session_id: str,
                                         question_id: str, reference: str) -> None:
        with self.connect() as con:
            con.execute("""UPDATE attempts SET backboard_state='synced',backboard_reference=?
              WHERE event_id=(
                SELECT event_id FROM attempts
                WHERE student_id=? AND session_id=? AND question_id=?
                ORDER BY received_utc DESC,rowid DESC LIMIT 1
              )""", (reference, student_id, session_id, question_id))
