#!/usr/bin/env python3
"""Seed the isolated deterministic five-round synthetic teacher demonstration."""
from __future__ import annotations

import argparse
import hashlib
import random
import re
import sqlite3
import sys
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import UUID

PROXY_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = PROXY_ROOT.parents[1]
DEFAULT_DB = PROXY_ROOT / "data" / "jaw_quiz_SYNTHETIC_5ROUND_DEMO.sqlite3"
QUESTION_BANK = PROJECT_ROOT / "Assets/JawAR/Quiz/Data/JawQuizStarterBank.asset"
REGION_MAP = PROJECT_ROOT / "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap.asset"
FIXED_SEED = 2026072405
DISCLOSURE = ("SYNTHETIC DEMONSTRATION DATA — These are fictional student interactions "
              "created to demonstrate the teacher workflow. They are not real learners or "
              "measured study results.")

sys.path.insert(0, str(PROXY_ROOT))
from app.database import Database  # noqa: E402

PROFILES = {
    1: ("left_right", "Persistent left/right confusion with modest improvement."),
    2: ("adjacent_regions", "Adjacent-region confusion with one persistent neighbouring pair."),
    3: ("terminology", "Terminology confusion, frequent Repeat use, and benefit from hints."),
    4: ("developing", "Developing learner with improving accuracy, speed, and independence."),
    5: ("strong", "Strong learner with occasional realistic mistakes and increasing speed."),
}
TARGETS = {
    1: [38, 36, 34, 32, 31],
    2: [35, 33, 32, 30, 29],
    3: [37, 35, 32, 29, 27],
    4: [40, 36, 33, 29, 26],
    5: [27, 26, 25, 28, 26],
}


def assets() -> tuple[list[tuple[str, str]], set[str]]:
    bank = QUESTION_BANK.read_text(encoding="utf-8")
    mapped = REGION_MAP.read_text(encoding="utf-8")
    pairs = re.findall(
        r"questionId:\s*([^\s]+)\s*\n\s*expectedRegionId:\s*([A-Za-z0-9_.:-]+)", bank)
    regions = set(re.findall(r"stableId:\s*([A-Za-z0-9_.:-]+)", mapped))
    if not pairs or {r for _, r in pairs} - regions:
        raise RuntimeError("Active question bank and surface-region map are inconsistent")
    if len(pairs) > 40:
        raise RuntimeError(
            f"Active bank has {len(pairs)} questions; complete coverage conflicts with 25–40 events")
    return pairs, regions


def opposite(region: str) -> str | None:
    if region.startswith("Left"):
        return "Right" + region[4:]
    if region.startswith("Right"):
        return "Left" + region[5:]
    return None


ADJACENT = {
    "LeftRamus": "LeftCoronoidProcess",
    "RightRamus": "RightMasseterInsertion",
    "LeftCondylarProcess": "LeftCoronoidProcess",
    "RightCondylarProcess": "RightCoronoidProcess",
}
TERMS = {
    "LeftMentalForamen": "MentalProtuberance",
    "RightMentalForamen": "MentalProtuberance",
    "MentalProtuberance": "MentalisOrigin",
    "MentalisOrigin": "MentalProtuberance",
}


@dataclass
class Event:
    student: int
    round: int
    question_id: str
    expected: str
    selected: str
    attempt: int
    seconds: float
    hint: int = 0
    repeat: int = 0
    skip: int = 0
    action: str = "first_selection"


class Builder:
    def __init__(self, questions: list[tuple[str, str]]):
        self.questions = questions
        self.regions = [region for _, region in questions]
        self.rng = random.Random(FIXED_SEED)

    def wrong_for(self, student: int, round_no: int, region: str, index: int) -> str:
        if student == 1 and opposite(region) in self.regions:
            return opposite(region)  # type: ignore[return-value]
        if student == 2 and region in ADJACENT:
            return ADJACENT[region]
        if student == 3 and region in TERMS:
            return TERMS[region]
        candidates = [r for r in self.regions if r != region]
        return candidates[(index * 7 + student * 3 + round_no) % len(candidates)]

    def error_priority(self, student: int, round_no: int) -> list[int]:
        special = {
            1: [i for i, (_, r) in enumerate(self.questions) if opposite(r) in self.regions],
            2: [i for i, (_, r) in enumerate(self.questions) if r in ADJACENT],
            3: [i for i, (_, r) in enumerate(self.questions) if r in TERMS],
        }.get(student, [])
        rest = [i for i in range(len(self.questions)) if i not in special]
        local = random.Random(FIXED_SEED + student * 100 + round_no)
        local.shuffle(rest)
        return special + rest

    def round_events(self, student: int, round_no: int) -> list[Event]:
        target = TARGETS[student][round_no - 1]
        retry_count = target - len(self.questions)
        priority = self.error_priority(student, round_no)
        # Every additional row is a correction of a meaningful first-selection error.
        error_indices = set(priority[:retry_count])
        result: list[Event] = []
        base_time = {
            1: 9.8 - round_no * .35,
            2: 10.5 - round_no * .55,
            3: 12.0 - round_no * .85,
            4: 17.0 - round_no * 1.9,
            5: 6.2 - round_no * .35,
        }[student]
        for index, (qid, region) in enumerate(self.questions):
            seconds = max(2.0, base_time + self.rng.uniform(-1.2, 1.5))
            is_error = index in error_indices
            hint = int(
                (student == 1 and is_error and round_no in (2, 3))
                or (student == 2 and is_error and round_no <= 2)
                or (student == 3 and is_error and round_no <= 4)
                or (student == 4 and is_error and round_no <= 3))
            repeat = int(student == 3 and (is_error or (index + round_no) % 5 == 0))
            skip = int(student == 4 and round_no == 1 and index == priority[0])
            selected = self.wrong_for(student, round_no, region, index) if is_error else region
            result.append(Event(student, round_no, qid, region, selected, 1,
                                round(seconds, 2), hint, repeat, skip,
                                "skip" if skip else ("incorrect_selection" if is_error else "first_selection")))
            if is_error:
                result.append(Event(student, round_no, qid, region, region, 2,
                                    round(max(1.5, seconds * self.rng.uniform(.38, .58)), 2),
                                    max(hint, int(student in (1, 3, 4))), 0, 0,
                                    "eventual_correction"))
        assert len(result) == target
        return result

    def build(self) -> list[Event]:
        return [event for student in range(1, 6) for round_no in range(1, 6)
                for event in self.round_events(student, round_no)]


def event_uuid(event: Event) -> str:
    raw = (f"{FIXED_SEED}|{event.student}|{event.round}|{event.question_id}|"
           f"{event.attempt}").encode()
    return str(UUID(hashlib.sha256(raw).hexdigest()[:32]))


def validate(con: sqlite3.Connection, question_count: int) -> dict:
    con.row_factory = sqlite3.Row
    students = con.execute("SELECT COUNT(*) n FROM students").fetchone()["n"]
    sessions = con.execute(
        "SELECT student_id,round_number,COUNT(*) n,COUNT(DISTINCT question_id) q "
        "FROM attempts JOIN attempt_demo_metadata USING(event_id) "
        "GROUP BY student_id,round_number ORDER BY student_id,round_number").fetchall()
    totals = con.execute(
        "SELECT student_id,COUNT(*) n FROM attempts GROUP BY student_id ORDER BY student_id").fetchall()
    total = con.execute("SELECT COUNT(*) n FROM attempts").fetchone()["n"]
    assert students == 5 and len(sessions) == 25
    assert all(25 <= row["n"] <= 40 and row["q"] == question_count for row in sessions)
    assert all(125 <= row["n"] <= 200 for row in totals)
    assert 625 <= total <= 1000
    assert len({row["n"] for row in sessions}) > 1
    bad_retry = con.execute("""
      SELECT COUNT(*) n FROM attempts a WHERE a.attempt_number>1 AND NOT EXISTS(
        SELECT 1 FROM attempts f WHERE f.student_id=a.student_id
        AND f.session_id=a.session_id AND f.question_id=a.question_id
        AND f.attempt_number=a.attempt_number-1 AND f.utc_timestamp<a.utc_timestamp)""").fetchone()["n"]
    assert bad_retry == 0
    return {"students": students, "rounds": len(sessions), "events": total,
            "rounds_by_student": {
                f"Synthetic Student {s}": TARGETS[s] for s in range(1, 6)}}


def seed(path: Path, *, replace: bool = False) -> dict:
    path = path.resolve()
    if path.exists():
        if not replace:
            raise FileExistsError(
                f"Refusing to replace existing five-round synthetic database: {path}\n"
                "Use --replace-synthetic-5round-demo only for this versioned file.")
        path.unlink()
        for suffix in ("-wal", "-shm"):
            sidecar = Path(str(path) + suffix)
            if sidecar.exists():
                sidecar.unlink()
    questions, _ = assets()
    Database(path)
    events = Builder(questions).build()
    base = datetime(2026, 3, 2, 14, 0, tzinfo=timezone.utc)
    with sqlite3.connect(path) as con:
        con.executescript("""
          CREATE TABLE demo_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
          CREATE TABLE student_demo_metadata(
            student_id TEXT PRIMARY KEY,display_name TEXT NOT NULL,
            synthetic INTEGER NOT NULL CHECK(synthetic=1),source TEXT NOT NULL,
            profile_key TEXT NOT NULL,profile_description TEXT NOT NULL);
          CREATE TABLE attempt_demo_metadata(
            event_id TEXT PRIMARY KEY,round_number INTEGER NOT NULL CHECK(round_number BETWEEN 1 AND 5),
            presented_index INTEGER NOT NULL,repeat_used INTEGER NOT NULL DEFAULT 0,
            skip_used INTEGER NOT NULL DEFAULT 0,eventual_correction INTEGER NOT NULL DEFAULT 0,
            action_type TEXT NOT NULL,source TEXT NOT NULL DEFAULT 'synthetic_5round_demo');
          CREATE TABLE backboard_demo_state(
            id INTEGER PRIMARY KEY CHECK(id=1),status TEXT NOT NULL DEFAULT 'not_started',
            provider TEXT NOT NULL DEFAULT '',model_name TEXT NOT NULL DEFAULT '',
            memory_mode TEXT NOT NULL DEFAULT 'Auto/Readonly',assistant_id TEXT NOT NULL DEFAULT '',
            thread_id TEXT NOT NULL DEFAULT '',model_discovery_operations INTEGER NOT NULL DEFAULT 0,
            storage_operations INTEGER NOT NULL DEFAULT 0,
            generation_operations INTEGER NOT NULL DEFAULT 0,total_operations INTEGER NOT NULL DEFAULT 0,
            completed_utc TEXT NOT NULL DEFAULT '',summary_hash TEXT NOT NULL DEFAULT '',
            response_hash TEXT NOT NULL DEFAULT '',response_json TEXT NOT NULL DEFAULT '',
            pdf_filename TEXT NOT NULL DEFAULT '',pdf_sha256 TEXT NOT NULL DEFAULT '');
          INSERT INTO backboard_demo_state(id) VALUES(1);
        """)
        con.executemany("INSERT INTO demo_metadata VALUES(?,?)", [
            ("synthetic_demo", "true"), ("demo_version", "five_rounds_2026-07-24"),
            ("fixed_seed", str(FIXED_SEED)), ("disclosure", DISCLOSURE),
            ("external_services", "explicit_backboard_action_only"),
            ("question_count", str(len(questions)))])
        for student, (profile, description) in PROFILES.items():
            sid = f"student_synthetic_{student}"
            con.execute("INSERT INTO students(student_id,created_utc) VALUES(?,?)",
                        (sid, base.isoformat().replace("+00:00", "Z")))
            con.execute("INSERT INTO student_demo_metadata VALUES(?,?,1,?,?,?)",
                        (sid, f"Synthetic Student {student}", "synthetic_5round_demo",
                         profile, description))
        positions: dict[tuple[int, int], int] = {}
        for event in events:
            key = (event.student, event.round)
            position = positions.get(key, 0)
            timestamp = base + timedelta(days=(event.round - 1) * 7,
                                         hours=(event.student - 1) * 2,
                                         seconds=position * 24 + int(event.seconds))
            positions[key] = position + 1
            sid = f"student_synthetic_{event.student}"
            session = f"synthetic5_s{event.student}_round_{event.round}"
            eid = event_uuid(event)
            con.execute("""INSERT INTO attempts VALUES(
              ?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,'')""",
              (eid, sid, session, event.question_id, "jaw", "data-v1:synthetic-5round",
               event.expected, event.selected, int(event.expected == event.selected),
               event.seconds, event.attempt, event.hint,
               timestamp.isoformat().replace("+00:00", "Z"),
               timestamp.isoformat().replace("+00:00", "Z"), "local-only"))
            presented = next(i for i, (qid, _) in enumerate(questions, 1) if qid == event.question_id)
            con.execute("INSERT INTO attempt_demo_metadata VALUES(?,?,?,?,?,?,?,?)",
                        (eid, event.round, presented, event.repeat, event.skip,
                         int(event.action == "eventual_correction"), event.action,
                         "synthetic_5round_demo"))
        result = validate(con, len(questions))
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", type=Path, default=DEFAULT_DB)
    parser.add_argument("--replace-synthetic-5round-demo", action="store_true")
    args = parser.parse_args()
    try:
        result = seed(args.database, replace=args.replace_synthetic_5round_demo)
    except FileExistsError as exc:
        print(exc, file=sys.stderr)
        return 2
    print(f"Created: {args.database.resolve()}")
    print(f"Fixed seed {FIXED_SEED}; {result['students']} students; "
          f"{result['rounds']} rounds; {result['events']} events")
    for label, rounds in result["rounds_by_student"].items():
        print(f"{label}: {rounds} = {sum(rounds)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
