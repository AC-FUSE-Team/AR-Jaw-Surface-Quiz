#!/usr/bin/env python3
"""Create the isolated, deterministic Jaw Quiz synthetic teacher-demo database."""
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
DEFAULT_DB = PROXY_ROOT / "data" / "jaw_quiz_SYNTHETIC_DEMO.sqlite3"
QUESTION_BANK = PROJECT_ROOT / "Assets/JawAR/Quiz/Data/JawQuizStarterBank.asset"
REGION_MAP = PROJECT_ROOT / "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap.asset"
FIXED_SEED = 20260724

sys.path.insert(0, str(PROXY_ROOT))
from app.database import Database  # noqa: E402


PROFILES = {
    "student_synthetic_1": (
        "Synthetic Student 1", "left_right",
        "Recognizes jaw structures but frequently reverses anatomical left and right."
    ),
    "student_synthetic_2": (
        "Synthetic Student 2", "neighbouring_regions",
        "Distinguishes left and right but confuses adjacent ramus, coronoid, and condylar regions."
    ),
    "student_synthetic_3": (
        "Synthetic Student 3", "terminology",
        "Confuses similarly named mental regions, repeats prompts often, and benefits from hints."
    ),
    "student_synthetic_4": (
        "Synthetic Student 4", "developing",
        "Starts slowly with low accuracy, then improves across sessions while using fewer hints."
    ),
    "student_synthetic_5": (
        "Synthetic Student 5", "high_performing",
        "Answers quickly and accurately with occasional realistic errors."
    ),
}


def _assets() -> tuple[dict[str, str], set[str]]:
    bank = QUESTION_BANK.read_text(encoding="utf-8")
    region_map = REGION_MAP.read_text(encoding="utf-8")
    question_pairs = re.findall(
        r"questionId:\s*([^\s]+)\s*\n\s*expectedRegionId:\s*([A-Za-z0-9_.:-]+)", bank
    )
    questions = {region: qid for qid, region in question_pairs}
    regions = set(re.findall(r"stableId:\s*([A-Za-z0-9_.:-]+)", region_map))
    if not questions or not regions:
        raise RuntimeError("Could not inspect the current question bank and surface-region map")
    missing = set(questions) - regions
    if missing:
        raise RuntimeError(f"Question-bank regions missing from current map: {sorted(missing)}")
    return questions, regions


@dataclass
class Event:
    student: str
    session: str
    question_key: str
    expected: str
    selected: str
    attempt: int
    seconds: float
    hint: int = 0
    repeat: int = 0
    skip: int = 0
    eventual: int = 0


class Builder:
    def __init__(self, questions: dict[str, str]):
        self.questions = questions
        self.rng = random.Random(FIXED_SEED)
        self.events: list[Event] = []

    def add(self, student: int, session: int, key: str, expected: str, selected: str,
            attempt: int = 1, base_seconds: float = 8, hint: int = 0, repeat: int = 0,
            skip: int = 0, eventual: int = 0) -> None:
        assert expected in self.questions and selected in self.questions
        jitter = self.rng.uniform(-0.65, 0.65)
        self.events.append(Event(
            f"student_synthetic_{student}", f"synthetic_s{student}_session_{session}",
            key, expected, selected, attempt, round(max(1.2, base_seconds + jitter), 2),
            hint, repeat, skip, eventual
        ))

    def correct(self, student: int, session: int, key: str, region: str, **kwargs) -> None:
        self.add(student, session, key, region, region, **kwargs)

    def correction(self, student: int, session: int, key: str, expected: str, wrong: str,
                   *, base_seconds: float, hint: int = 0, repeat: int = 0) -> None:
        self.add(student, session, key, expected, wrong, base_seconds=base_seconds,
                 hint=hint, repeat=repeat, eventual=1)
        self.add(student, session, key, expected, expected, attempt=2,
                 base_seconds=max(2.4, base_seconds * .48), hint=max(1, hint), eventual=1)

    def student_1(self) -> None:
        # Eight first-attempt ramus prompts: six bilateral reversals, four self-corrections.
        for session in range(1, 5):
            for side, other in (("LeftRamus", "RightRamus"), ("RightRamus", "LeftRamus")):
                key = f"ramus_{session}_{side}"
                if (session, side) in {(3, "LeftRamus"), (4, "RightRamus")}:
                    self.correct(1, session, key, side, base_seconds=7.1)
                elif session <= 2:
                    self.correction(1, session, key, side, other, base_seconds=12.2,
                                    hint=1 if session == 2 else 0)
                else:
                    self.add(1, session, key, side, other, base_seconds=10.1, hint=1)
            # Other bilateral reversals remain visible, while midline/control structures are strong.
            pair = ("LeftMentalForamen", "RightMentalForamen") if session % 2 else (
                "RightCondylarProcess", "LeftCondylarProcess")
            if session in (1, 3):
                self.correction(1, session, f"bilateral_{session}", pair[0], pair[1],
                                base_seconds=13.1, hint=1)
            else:
                self.add(1, session, f"bilateral_{session}", pair[0], pair[1],
                         base_seconds=11.8, hint=1)
            self.correct(1, session, f"midline_{session}", "MentalProtuberance",
                         base_seconds=5.8)
            self.correct(1, session, f"incisors_{session}", "LowerIncisors",
                         base_seconds=5.2)
        self.correct(1, 5, "alveolar_control", "AlveolarProcess", base_seconds=6.0)

    def student_2(self) -> None:
        adjacent = [
            ("LeftRamus", "LeftCoronoidProcess"),
            ("LeftCoronoidProcess", "LeftCondylarProcess"),
            ("RightRamus", "RightMasseterInsertion"),
            ("RightCondylarProcess", "RightCoronoidProcess"),
        ]
        for session in range(1, 6):
            for index, (expected, wrong) in enumerate(adjacent[:3]):
                key = f"adjacent_{session}_{index}"
                if (session + index) % 3:
                    self.correction(2, session, key, expected, wrong, base_seconds=11.0,
                                    hint=1 if session < 3 else 0)
                else:
                    self.correct(2, session, key, expected, base_seconds=8.4)
            self.correct(2, session, f"sidecheck_{session}", "RightMentalForamen",
                         base_seconds=6.8)
            self.correct(2, session, f"control_{session}", "MentalProtuberance",
                         base_seconds=6.1)

    def student_3(self) -> None:
        for session in range(1, 6):
            # A concept/word pair supported by the bank: mental foramen vs mental protuberance.
            if session <= 3:
                self.correction(3, session, f"mental_terms_{session}", "LeftMentalForamen",
                                "MentalProtuberance", base_seconds=14.2, hint=1,
                                repeat=1)
            else:
                self.correct(3, session, f"mental_terms_{session}", "LeftMentalForamen",
                             base_seconds=8.1, hint=1, repeat=1)
            self.correct(3, session, f"repeat_ramus_{session}", "RightRamus",
                         base_seconds=9.4, repeat=1 if session != 4 else 0)
            self.correct(3, session, f"hinted_mentalis_{session}", "MentalisOrigin",
                         base_seconds=8.8, hint=1 if session <= 3 else 0,
                         repeat=1 if session <= 2 else 0)
            self.correct(3, session, f"control_{session}", "AlveolarProcess",
                         base_seconds=7.3)
            if session <= 3:
                self.add(3, session, f"term_error_{session}", "MentalProtuberance",
                         "MentalisOrigin", base_seconds=12.9, repeat=1)
            else:
                self.correct(3, session, f"term_error_{session}", "MentalProtuberance",
                             base_seconds=7.7)

    def student_4(self) -> None:
        regions = ["LeftRamus", "RightRamus", "LeftCondylarProcess",
                   "RightMentalForamen", "MentalProtuberance", "LowerIncisors"]
        wrongs = ["RightRamus", "LeftRamus", "LeftCoronoidProcess",
                  "MentalProtuberance", "MentalisOrigin", "AlveolarProcess"]
        for session in range(1, 6):
            errors = {1: 4, 2: 3, 3: 2, 4: 1, 5: 1}[session]
            for index, (expected, wrong) in enumerate(zip(regions, wrongs)):
                key = f"trend_{session}_{index}"
                if index < errors:
                    if index % 2 == 0:
                        self.correction(4, session, key, expected, wrong,
                                        base_seconds=18.5 - session * 1.4,
                                        hint=1 if session <= 3 else 0)
                    else:
                        self.add(4, session, key, expected, wrong,
                                 base_seconds=18.5 - session * 1.4,
                                 hint=1 if session <= 3 else 0,
                                 skip=1 if session == 1 and index == 1 else 0)
                else:
                    self.correct(4, session, key, expected,
                                 base_seconds=15.5 - session * 1.6,
                                 hint=1 if session == 1 else 0)

    def student_5(self) -> None:
        regions = ["LeftRamus", "RightRamus", "LeftCondylarProcess",
                   "RightCoronoidProcess", "LeftMentalForamen", "RightMentalForamen",
                   "MentalProtuberance"]
        mistakes = {(2, 4): "MentalProtuberance", (5, 2): "LeftRamus"}
        for session in range(1, 6):
            for index, expected in enumerate(regions):
                key = f"fast_{session}_{index}"
                wrong = mistakes.get((session, index))
                if wrong:
                    self.correction(5, session, key, expected, wrong,
                                    base_seconds=7.0, hint=1 if session == 5 else 0)
                else:
                    self.correct(5, session, key, expected, base_seconds=4.7)

    def build(self) -> list[Event]:
        self.student_1()
        self.student_2()
        self.student_3()
        self.student_4()
        self.student_5()
        return self.events


def _uuid_for(event: Event) -> str:
    digest = hashlib.sha256(
        f"{FIXED_SEED}|{event.student}|{event.session}|{event.question_key}|{event.attempt}".encode()
    ).hexdigest()[:32]
    return str(UUID(digest))


def seed(path: Path, *, replace: bool = False) -> dict[str, int]:
    path = path.resolve()
    if path.exists():
        if not replace:
            raise FileExistsError(
                f"Refusing to replace existing synthetic database: {path}\n"
                "Run again with --replace-synthetic-demo to recreate only this demo file."
            )
        path.unlink()
        for suffix in ("-wal", "-shm"):
            sidecar = Path(str(path) + suffix)
            if sidecar.exists():
                sidecar.unlink()
    questions, _ = _assets()
    Database(path)
    events = Builder(questions).build()
    base = datetime(2026, 2, 2, 14, 0, tzinfo=timezone.utc)
    with sqlite3.connect(path) as con:
        con.executescript("""
        CREATE TABLE demo_metadata (
          key TEXT PRIMARY KEY, value TEXT NOT NULL
        );
        CREATE TABLE student_demo_metadata (
          student_id TEXT PRIMARY KEY, display_name TEXT NOT NULL,
          synthetic INTEGER NOT NULL CHECK(synthetic=1), source TEXT NOT NULL,
          profile_key TEXT NOT NULL, profile_description TEXT NOT NULL,
          FOREIGN KEY(student_id) REFERENCES students(student_id)
        );
        CREATE TABLE attempt_demo_metadata (
          event_id TEXT PRIMARY KEY, repeat_used INTEGER NOT NULL DEFAULT 0,
          skip_used INTEGER NOT NULL DEFAULT 0, eventual_correction INTEGER NOT NULL DEFAULT 0,
          source TEXT NOT NULL DEFAULT 'synthetic_demo',
          FOREIGN KEY(event_id) REFERENCES attempts(event_id)
        );
        """)
        con.executemany("INSERT INTO demo_metadata(key,value) VALUES(?,?)", [
            ("synthetic_demo", "true"),
            ("fixed_seed", str(FIXED_SEED)),
            ("disclosure", "SYNTHETIC DEMONSTRATION DATA"),
            ("external_services", "disabled"),
        ])
        for student, (label, profile, description) in PROFILES.items():
            con.execute(
                "INSERT INTO students(student_id,created_utc) VALUES(?,?)",
                (student, base.isoformat().replace("+00:00", "Z"))
            )
            con.execute(
                """INSERT INTO student_demo_metadata
                   (student_id,display_name,synthetic,source,profile_key,profile_description)
                   VALUES(?,?,1,'synthetic_demo',?,?)""",
                (student, label, profile, description)
            )
        offsets: dict[tuple[str, str], int] = {}
        for event in events:
            key = (event.student, event.session)
            offset = offsets.get(key, 0)
            session_number = int(event.session.rsplit("_", 1)[1])
            student_number = int(event.student.rsplit("_", 1)[1])
            timestamp = base + timedelta(days=(student_number - 1) * 2 + session_number * 7,
                                         minutes=offset)
            offsets[key] = offset + max(1, round(event.seconds / 2))
            event_id = _uuid_for(event)
            con.execute(
                """INSERT INTO attempts(event_id,student_id,session_id,question_id,object_id,
                   region_map_version,expected_region_id,selected_region_id,correct,
                   response_time_seconds,attempt_number,hint_level,utc_timestamp,received_utc,
                   backboard_state,backboard_reference)
                   VALUES(?,?,?,?,?,'data-v1:synthetic-demo',?,?,?,?,?,?,?,?,'local-only','')""",
                (event_id, event.student, event.session,
                 f"{questions[event.expected]}--{event.question_key}", "jaw",
                 event.expected, event.selected, int(event.expected == event.selected),
                 event.seconds, event.attempt, event.hint,
                 timestamp.isoformat().replace("+00:00", "Z"),
                 timestamp.isoformat().replace("+00:00", "Z"))
            )
            con.execute(
                """INSERT INTO attempt_demo_metadata
                   (event_id,repeat_used,skip_used,eventual_correction,source)
                   VALUES(?,?,?,?, 'synthetic_demo')""",
                (event_id, event.repeat, event.skip, event.eventual)
            )
    counts = {student: 0 for student in PROFILES}
    for event in events:
        counts[event.student] += 1
    return counts


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Seed the isolated local Jaw Quiz synthetic teacher demonstration"
    )
    parser.add_argument("--database", type=Path, default=DEFAULT_DB)
    parser.add_argument("--replace-synthetic-demo", action="store_true",
                        help="Explicitly replace the selected synthetic demo database")
    args = parser.parse_args()
    try:
        counts = seed(args.database, replace=args.replace_synthetic_demo)
    except FileExistsError as exc:
        print(exc, file=sys.stderr)
        return 2
    print(f"Created isolated synthetic demonstration database: {args.database.resolve()}")
    print(f"Fixed seed: {FIXED_SEED}; fictional students: {len(counts)}; events: {sum(counts.values())}")
    for student, count in counts.items():
        print(f"  {PROFILES[student][0]}: {count} attempt events")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
