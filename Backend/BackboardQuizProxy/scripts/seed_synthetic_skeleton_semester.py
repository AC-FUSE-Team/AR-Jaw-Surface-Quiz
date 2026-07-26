#!/usr/bin/env python3
"""Create the deterministic 20-round whole-skeleton synthetic semester database."""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import sqlite3
import sys
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import UUID

PROXY_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROXY_ROOT))
from app.database import Database  # noqa: E402
from app.skeleton_catalog import (ALLOWLIST, NON_LATERALIZED, REGIONS,
                                  PRESERVED_PDF_SHA256, validate_project_sources)  # noqa: E402

DEFAULT_DB = PROXY_ROOT / "data" / "skeleton_SYNTHETIC_20ROUND_SEMESTER.sqlite3"
FIXED_SEED = 2026072420
DISCLOSURE = ("SYNTHETIC WHOLE-SKELETON DEMONSTRATION — The student histories are fictional. "
              "Backboard/GPT-5.5 is used for teacher-facing analysis. One preserved Student 1 "
              "jaw worksheet demonstrates the tailored assessment concept.")
MOBILE_DISCLOSURE = ("The jaw activity is the working mobile example. The semester dashboard uses "
                     "synthetic data to show how the same learning workflow can scale to the full "
                     "skeletal prototype.")

PROFILES = {
    1: ("left_right", "Bone names are generally understood; anatomical left/right remains the leading weakness."),
    2: ("upper_limb_neighbors", "Radius/ulna discrimination improves after targeted comparison practice."),
    3: ("upper_lower_terms", "Uses Repeat often and benefits from hints on plausible upper/lower long-bone comparisons."),
    4: ("developing", "Broad early difficulty improves steadily in accuracy, speed, and independence."),
    5: ("strong", "Strong mixed-region performance with occasional realistic errors; not perfect."),
}

# Twenty meaningful prompts per round. Repeated expected IDs represent distinct
# comparison/viewpoint prompts, not duplicate click events.
QUESTIONS = [
    ("skeleton.skull", "Skull"), ("skeleton.ribcage", "RibCage"),
    ("skeleton.pelvis", "Pelvis"),
    ("skeleton.left_humerus", "LeftHumerus"), ("skeleton.right_humerus", "RightHumerus"),
    ("skeleton.left_radius", "LeftRadius"), ("skeleton.right_radius", "RightRadius"),
    ("skeleton.left_ulna", "LeftUlna"), ("skeleton.right_ulna", "RightUlna"),
    ("skeleton.left_femur", "LeftFemur"), ("skeleton.right_femur", "RightFemur"),
    ("skeleton.left_foot", "LeftFoot"), ("skeleton.right_foot", "RightFoot"),
    ("jaw.left_ramus.front", "LeftRamus"), ("jaw.right_ramus.front", "RightRamus"),
    ("jaw.left_ramus.oblique", "LeftRamus"), ("jaw.right_ramus.oblique", "RightRamus"),
    ("compare.radius_ulna.left", "LeftRadius"), ("compare.radius_ulna.right", "RightUlna"),
    ("compare.long_bone", "LeftFemur"),
]

TARGETS = {
    1: [36,35,35,34,34,33,33,32,32,31,31,30,30,29,29,28,28,27,27,26],
    2: [34,34,33,33,32,32,31,31,30,30,29,29,28,28,27,27,26,25,25,24],
    3: [35,35,34,34,33,33,32,32,31,31,30,30,29,29,28,28,27,27,26,25],
    4: [38,37,36,35,34,33,32,31,30,29,28,27,26,25,24,24,23,23,22,22],
    5: [25,25,24,24,24,23,23,23,22,22,22,22,21,21,21,21,21,20,21,20],
}


def opposite(region: str) -> str | None:
    if region.startswith("Left"):
        candidate = "Right" + region[4:]
    elif region.startswith("Right"):
        candidate = "Left" + region[5:]
    else:
        return None
    return candidate if candidate in ALLOWLIST else None


@dataclass(frozen=True)
class Event:
    student: int
    round: int
    question_id: str
    expected: str
    selected: str
    attempt: int
    seconds: float
    hint: int
    repeat: int
    skip: int
    action: str


class Builder:
    def __init__(self):
        self.rng = random.Random(FIXED_SEED)

    def priority(self, student: int, round_no: int) -> list[int]:
        if student == 1:
            special = [i for i, (_, r) in enumerate(QUESTIONS) if opposite(r)]
        elif student == 2:
            special = [i for i, (_, r) in enumerate(QUESTIONS)
                       if r in {"LeftRadius", "RightRadius", "LeftUlna", "RightUlna"}]
        elif student == 3:
            special = [i for i, (_, r) in enumerate(QUESTIONS)
                       if r in {"LeftHumerus", "RightHumerus", "LeftFemur", "RightFemur"}]
        else:
            special = []
        rest = [i for i in range(len(QUESTIONS)) if i not in special]
        random.Random(FIXED_SEED + student * 100 + round_no).shuffle(rest)
        return special + rest

    def wrong(self, student: int, round_no: int, region: str, index: int) -> str:
        if student == 1 and opposite(region):
            return opposite(region) or region
        if student == 2:
            neighbors = {"LeftRadius": "LeftUlna", "LeftUlna": "LeftRadius",
                         "RightRadius": "RightUlna", "RightUlna": "RightRadius"}
            if region in neighbors:
                return neighbors[region]
        if student == 3:
            terms = {"LeftHumerus": "LeftFemur", "LeftFemur": "LeftHumerus",
                     "RightHumerus": "RightFemur", "RightFemur": "RightHumerus"}
            if region in terms:
                return terms[region]
        choices = [r for r in sorted(ALLOWLIST) if r != region]
        return choices[(index * 5 + round_no + student * 3) % len(choices)]

    def round_events(self, student: int, round_no: int) -> list[Event]:
        errors = TARGETS[student][round_no - 1] - len(QUESTIONS)
        error_indices = set(self.priority(student, round_no)[:errors])
        base = {
            1: 11.2 - .18 * round_no, 2: 12.2 - .27 * round_no,
            3: 14.0 - .35 * round_no, 4: 19.0 - .60 * round_no,
            5: 7.4 - .16 * round_no,
        }[student]
        rows: list[Event] = []
        for index, (qid, expected) in enumerate(QUESTIONS):
            wrong = index in error_indices
            seconds = round(max(2.0, base + self.rng.uniform(-1.0, 1.4)), 2)
            hint = int(wrong and ((student == 4 and round_no <= 12)
                                  or student == 3 or (student == 2 and round_no <= 8)
                                  or (student == 1 and round_no % 3 == 0)))
            repeat = int(student == 3 and (wrong or (index + round_no) % 4 == 0))
            skip = int(student == 4 and wrong and round_no <= 4 and index == min(error_indices))
            selected = self.wrong(student, round_no, expected, index) if wrong else expected
            rows.append(Event(student, round_no, qid, expected, selected, 1, seconds, hint,
                              repeat, skip, "skip" if skip else
                              ("incorrect_selection" if wrong else "first_selection")))
            if wrong:
                rows.append(Event(student, round_no, qid, expected, expected, 2,
                                  round(max(1.5, seconds * self.rng.uniform(.38, .60)), 2),
                                  max(hint, int(student in (1, 3, 4))), 0, 0,
                                  "eventual_correction"))
        assert len(rows) == TARGETS[student][round_no - 1]
        return rows

    def build(self) -> list[Event]:
        return [event for student in range(1, 6) for round_no in range(1, 21)
                for event in self.round_events(student, round_no)]


def event_uuid(event: Event) -> str:
    raw = (f"{FIXED_SEED}|{event.student}|{event.round}|{event.question_id}|"
           f"{event.attempt}").encode()
    return str(UUID(hashlib.sha256(raw).hexdigest()[:32]))


def validate(con: sqlite3.Connection) -> dict:
    con.row_factory = sqlite3.Row
    sessions = con.execute("""SELECT student_id,round_number,COUNT(*) n,
      COUNT(DISTINCT body_area) areas,COUNT(DISTINCT CASE WHEN module='whole_skeleton'
      THEN expected_region_id END) skeleton_regions
      FROM attempts JOIN semester_event_metadata USING(event_id)
      GROUP BY student_id,round_number""").fetchall()
    totals = [r[0] for r in con.execute(
        "SELECT COUNT(*) FROM attempts GROUP BY student_id ORDER BY student_id")]
    used = {r[0] for r in con.execute(
        "SELECT DISTINCT expected_region_id FROM attempts")}
    assert len(sessions) == 100 and all(20 <= r["n"] <= 40 for r in sessions)
    assert all(r["areas"] == 4 and r["skeleton_regions"] == 13 for r in sessions)
    assert all(400 <= n <= 800 for n in totals) and 2000 <= sum(totals) <= 4000
    assert used <= ALLOWLIST and NON_LATERALIZED <= used
    return {"students": 5, "rounds": 100, "events": sum(totals),
            "events_by_student": totals, "allowlist": sorted(ALLOWLIST)}


def seed(path: Path, *, replace: bool = False) -> dict:
    validate_project_sources()
    path = path.resolve()
    if path.exists():
        if not replace:
            raise FileExistsError(f"Refusing to replace existing semester database: {path}")
        path.unlink()
        for suffix in ("-wal", "-shm"):
            sidecar = Path(str(path) + suffix)
            if sidecar.exists():
                sidecar.unlink()
    Database(path)
    events = Builder().build()
    base = datetime(2026, 1, 12, 14, 0, tzinfo=timezone.utc)
    with sqlite3.connect(path) as con:
        con.executescript("""
          CREATE TABLE demo_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
          CREATE TABLE student_demo_metadata(
            student_id TEXT PRIMARY KEY,display_name TEXT NOT NULL,
            synthetic INTEGER NOT NULL CHECK(synthetic=1),source TEXT NOT NULL,
            profile_key TEXT NOT NULL,profile_description TEXT NOT NULL);
          CREATE TABLE semester_region_catalog(
            region_id TEXT PRIMARY KEY,body_area TEXT NOT NULL,anatomical_side TEXT NOT NULL,
            module TEXT NOT NULL,selection_source_json TEXT NOT NULL,
            support_status TEXT NOT NULL CHECK(support_status='fully_supported_selectable'));
          CREATE TABLE semester_event_metadata(
            event_id TEXT PRIMARY KEY,round_number INTEGER NOT NULL CHECK(round_number BETWEEN 1 AND 20),
            checkpoint INTEGER NOT NULL CHECK(checkpoint BETWEEN 1 AND 5),
            presented_index INTEGER NOT NULL,body_area TEXT NOT NULL,anatomical_side TEXT NOT NULL,
            module TEXT NOT NULL,repeat_used INTEGER NOT NULL DEFAULT 0,
            skip_used INTEGER NOT NULL DEFAULT 0,eventual_correction INTEGER NOT NULL DEFAULT 0,
            action_type TEXT NOT NULL,source TEXT NOT NULL DEFAULT 'synthetic_skeleton_semester');
          CREATE TABLE semester_analysis(
            scope_key TEXT PRIMARY KEY,scope_type TEXT NOT NULL,student_id TEXT NOT NULL DEFAULT '',
            provider TEXT NOT NULL,model_name TEXT NOT NULL,completed_utc TEXT NOT NULL,
            analysis_json TEXT NOT NULL,response_hash TEXT NOT NULL);
          CREATE TABLE semester_backboard_state(
            id INTEGER PRIMARY KEY CHECK(id=1),status TEXT NOT NULL DEFAULT 'not_started',
            provider TEXT NOT NULL DEFAULT '',model_name TEXT NOT NULL DEFAULT '',
            model_discovery_operations INTEGER NOT NULL DEFAULT 0,
            storage_operations INTEGER NOT NULL DEFAULT 0,
            generation_operations INTEGER NOT NULL DEFAULT 0,
            total_operations INTEGER NOT NULL DEFAULT 0,
            verified_spending_usd REAL NOT NULL DEFAULT 0,
            spending_source TEXT NOT NULL DEFAULT 'not_reported',
            completed_utc TEXT NOT NULL DEFAULT '');
          INSERT INTO semester_backboard_state(id) VALUES(1);
        """)
        con.executemany("INSERT INTO demo_metadata VALUES(?,?)", [
            ("synthetic_demo", "true"), ("demo_version", "skeleton_semester_20rounds_2026-07-24"),
            ("fixed_seed", str(FIXED_SEED)), ("disclosure", DISCLOSURE),
            ("mobile_disclosure", MOBILE_DISCLOSURE),
            ("prototype_scope", "Whole-body skeletal regions currently represented by the prototype."),
            ("pdf_policy", "one_preserved_student1_jaw_pdf_no_generation"),
            ("preserved_pdf_sha256", PRESERVED_PDF_SHA256),
            ("external_services", "explicit_semester_backboard_analysis_only")])
        for region, meta in REGIONS.items():
            con.execute("INSERT INTO semester_region_catalog VALUES(?,?,?,?,?,'fully_supported_selectable')",
                        (region, meta["body_area"], meta["side"], meta["module"],
                         json.dumps(meta["selection"], separators=(",", ":"))))
        for student, (key, description) in PROFILES.items():
            sid = f"student_synthetic_{student}"
            con.execute("INSERT INTO students(student_id,created_utc) VALUES(?,?)",
                        (sid, base.isoformat().replace("+00:00", "Z")))
            con.execute("INSERT INTO student_demo_metadata VALUES(?,?,1,?,?,?)",
                        (sid, f"Synthetic Student {student}", "synthetic_skeleton_semester",
                         key, description))
        positions: dict[tuple[int, int], int] = {}
        for event in events:
            key = (event.student, event.round)
            pos = positions.get(key, 0)
            positions[key] = pos + 1
            timestamp = base + timedelta(days=(event.round - 1) * 7,
                                         hours=(event.student - 1) * 2,
                                         seconds=pos * 22 + int(event.seconds))
            sid = f"student_synthetic_{event.student}"
            session = f"skeleton_s{event.student}_round_{event.round:02d}"
            eid = event_uuid(event)
            meta = REGIONS[event.expected]
            con.execute("""INSERT INTO attempts VALUES(
              ?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,'')""",
              (eid, sid, session, event.question_id, meta["module"],
               "skeleton-semester-v1", event.expected, event.selected,
               int(event.expected == event.selected), event.seconds, event.attempt,
               event.hint, timestamp.isoformat().replace("+00:00", "Z"),
               timestamp.isoformat().replace("+00:00", "Z"), "local-only"))
            presented = next(i for i, q in enumerate(QUESTIONS, 1) if q[0] == event.question_id)
            con.execute("INSERT INTO semester_event_metadata VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                        (eid, event.round, (event.round - 1) // 4 + 1, presented,
                         meta["body_area"], meta["side"], meta["module"], event.repeat,
                         event.skip, int(event.action == "eventual_correction"), event.action,
                         "synthetic_skeleton_semester"))
        return validate(con)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", type=Path, default=DEFAULT_DB)
    parser.add_argument("--replace-synthetic-semester", action="store_true")
    args = parser.parse_args()
    try:
        result = seed(args.database, replace=args.replace_synthetic_semester)
    except FileExistsError as exc:
        print(exc, file=sys.stderr)
        return 2
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
