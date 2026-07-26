#!/usr/bin/env python3
"""Create and extend the isolated dynamic whole-skeleton scaling database."""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import shutil
import sqlite3
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import UUID

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from app.database import Database  # noqa: E402
from app.skeleton_catalog import ALLOWLIST, REGIONS, validate_project_sources  # noqa: E402
from scripts.seed_synthetic_skeleton_semester import Event, QUESTIONS  # noqa: E402

SOURCE_DB = ROOT / "data/skeleton_SYNTHETIC_20ROUND_SEMESTER.sqlite3"
DEFAULT_DB = ROOT / "data/skeleton_SYNTHETIC_DYNAMIC_SCALING.sqlite3"
FIXED_SEED = 2026072460
INITIAL_REPORTED_SPEND = 0.881412
PROFILE_FAMILIES = (
    "left_right_transfer",
    "radius_ulna_discrimination",
    "upper_lower_terminology",
    "axial_weakness",
    "appendicular_weakness",
    "upper_body_weakness",
    "lower_body_weakness",
    "visual_term_gap",
)
TRAJECTORIES = ("steady_gain", "late_regression", "uneven_retention",
                "slow_plateau", "late_acceleration")
SUPPORT_STYLES = ("independent", "hint_dependent", "repeat_dependent")
PACES = ("slow_deliberate", "fast_error_prone", "balanced")


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def profile_for(number: int) -> dict:
    if number < 6:
        raise ValueError("Dynamic profiles begin with Synthetic Student 6")
    offset = number - 6
    family = PROFILE_FAMILIES[offset % len(PROFILE_FAMILIES)]
    trajectory = TRAJECTORIES[(offset // len(PROFILE_FAMILIES)) % len(TRAJECTORIES)]
    support = SUPPORT_STYLES[(offset // 3) % len(SUPPORT_STYLES)]
    pace = PACES[(offset // 5) % len(PACES)]
    key = f"{family}__{trajectory}__{support}__{pace}__s{number}"
    description = (
        f"{family.replace('_', ' ')} with a {trajectory.replace('_', ' ')} trajectory; "
        f"{support.replace('_', ' ')} support use and {pace.replace('_', ' ')} pacing."
    )
    return {"key": key, "description": description, "family": family,
            "trajectory": trajectory, "support": support, "pace": pace}


def _priority(profile: dict, round_no: int) -> list[int]:
    family = profile["family"]
    selected: list[int] = []
    for index, (_, region) in enumerate(QUESTIONS):
        side = REGIONS[region]["side"]
        body = REGIONS[region]["body_area"]
        matches = (
            (family == "left_right_transfer" and side in {"left", "right"})
            or (family == "radius_ulna_discrimination" and region.endswith(("Radius", "Ulna")))
            or (family == "upper_lower_terminology" and region.endswith(("Humerus", "Femur")))
            or (family == "axial_weakness" and body == "axial")
            or (family == "appendicular_weakness" and body in {"upper_limbs", "lower_limbs"})
            or (family == "upper_body_weakness" and body in {"upper_limbs", "jaw"})
            or (family == "lower_body_weakness" and body == "lower_limbs")
            or (family == "visual_term_gap" and body in {"axial", "upper_limbs"})
        )
        if matches:
            selected.append(index)
    rest = [i for i in range(len(QUESTIONS)) if i not in selected]
    random.Random(FIXED_SEED + round_no * 101 + len(profile["key"])).shuffle(rest)
    return selected + rest


def _opposite(region: str) -> str | None:
    candidate = ("Right" + region[4:] if region.startswith("Left") else
                 "Left" + region[5:] if region.startswith("Right") else "")
    return candidate if candidate in ALLOWLIST else None


def _wrong_region(profile: dict, region: str, index: int, round_no: int) -> str:
    family = profile["family"]
    opposite = _opposite(region)
    if family == "left_right_transfer" and opposite:
        return opposite
    neighbor = {
        "LeftRadius": "LeftUlna", "LeftUlna": "LeftRadius",
        "RightRadius": "RightUlna", "RightUlna": "RightRadius",
    }
    if family == "radius_ulna_discrimination" and region in neighbor:
        return neighbor[region]
    upper_lower = {
        "LeftHumerus": "LeftFemur", "LeftFemur": "LeftHumerus",
        "RightHumerus": "RightFemur", "RightFemur": "RightHumerus",
    }
    if family == "upper_lower_terminology" and region in upper_lower:
        return upper_lower[region]
    candidates = sorted(ALLOWLIST - {region})
    return candidates[(index * 7 + round_no * 3 + len(profile["key"])) % len(candidates)]


def _error_count(profile: dict, round_no: int, number: int) -> int:
    phase = (round_no - 1) / 19
    trajectory = profile["trajectory"]
    if trajectory == "steady_gain":
        value = 14 - round(8 * phase)
    elif trajectory == "late_regression":
        value = 13 - round(7 * phase) + (5 if round_no >= 17 else 0)
    elif trajectory == "uneven_retention":
        value = 11 + (4 if round_no in {5, 10, 14, 19} else 0) - round(4 * phase)
    elif trajectory == "slow_plateau":
        value = 14 - min(5, round((round_no - 1) / 3))
    else:
        value = 14 - (round_no // 8) * 4 - (3 if round_no >= 17 else 0)
    if profile["pace"] == "fast_error_prone":
        value += 2
    if profile["family"] == "visual_term_gap" and round_no % 4 == 0:
        value += 1
    return max(4, min(18, value + (number % 3) - 1))


def round_events(number: int, round_no: int) -> list[Event]:
    profile = profile_for(number)
    rng = random.Random(FIXED_SEED + number * 1000 + round_no)
    errors = _error_count(profile, round_no, number)
    error_indices = set(_priority(profile, round_no)[:errors])
    pace_base = {"slow_deliberate": 15.5, "fast_error_prone": 6.2, "balanced": 10.5}[
        profile["pace"]]
    rows: list[Event] = []
    for index, (question_id, expected) in enumerate(QUESTIONS):
        wrong = index in error_indices
        improvement = min(4.5, round_no * .18)
        seconds = round(max(2.0, pace_base - improvement + rng.uniform(-.8, 1.4)), 2)
        support_trigger = wrong or (index + round_no + number) % 7 == 0
        hint = int(profile["support"] == "hint_dependent" and support_trigger)
        repeat = int(profile["support"] == "repeat_dependent" and support_trigger)
        skip = int(profile["trajectory"] == "slow_plateau" and wrong and round_no <= 3
                   and index == min(error_indices))
        selected = _wrong_region(profile, expected, index, round_no) if wrong else expected
        rows.append(Event(number, round_no, question_id, expected, selected, 1, seconds,
                          hint, repeat, skip,
                          "skip" if skip else ("incorrect_selection" if wrong else
                                               "first_selection")))
        if wrong:
            rows.append(Event(number, round_no, question_id, expected, expected, 2,
                              round(max(1.5, seconds * rng.uniform(.38, .62)), 2),
                              max(hint, int(profile["support"] != "independent")),
                              0, 0, "eventual_correction"))
    assert 20 <= len(rows) <= 40
    return rows


def _event_uuid(event: Event) -> str:
    raw = (f"{FIXED_SEED}|{event.student}|{event.round}|{event.question_id}|"
           f"{event.attempt}").encode()
    return str(UUID(hashlib.sha256(raw).hexdigest()[:32]))


def initialize(path: Path, source: Path = SOURCE_DB, *, replace: bool = False) -> dict:
    validate_project_sources()
    if path.exists():
        if not replace:
            raise FileExistsError(f"Refusing to replace dynamic scaling database: {path}")
        path.unlink()
        for suffix in ("-wal", "-shm"):
            Path(str(path) + suffix).unlink(missing_ok=True)
    if not source.is_file():
        raise FileNotFoundError(source)
    path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, path)
    Database(path)
    with sqlite3.connect(path) as con:
        con.executescript("""
          CREATE TABLE dynamic_budget_state(
            id INTEGER PRIMARY KEY CHECK(id=1),
            hard_ceiling_usd REAL NOT NULL,
            initial_reported_spend_usd REAL NOT NULL,
            reported_spend_usd REAL NOT NULL,
            reported_spend_verified_utc TEXT NOT NULL,
            spending_source TEXT NOT NULL,
            estimated_next_student_usd REAL NOT NULL,
            requests_disabled INTEGER NOT NULL DEFAULT 0,
            stop_reason TEXT NOT NULL DEFAULT '',
            last_model_check_utc TEXT NOT NULL DEFAULT '');
          CREATE TABLE dynamic_student_processing(
            student_id TEXT PRIMARY KEY,
            student_number INTEGER NOT NULL UNIQUE,
            cohort TEXT NOT NULL,
            analysis_plan TEXT NOT NULL,
            status TEXT NOT NULL,
            included_in_x INTEGER NOT NULL DEFAULT 0,
            assistant_id TEXT NOT NULL DEFAULT '',
            thread_id TEXT NOT NULL DEFAULT '',
            checkpoint_count INTEGER NOT NULL DEFAULT 0,
            storage_operation_count INTEGER NOT NULL DEFAULT 0,
            generation_count INTEGER NOT NULL DEFAULT 0,
            started_utc TEXT NOT NULL DEFAULT '',
            completed_utc TEXT NOT NULL DEFAULT '',
            start_spend_usd REAL,
            completion_spend_usd REAL,
            measured_cost_usd REAL,
            failure_reason TEXT NOT NULL DEFAULT '');
          CREATE TABLE dynamic_checkpoint_analysis(
            student_id TEXT NOT NULL,
            checkpoint INTEGER NOT NULL CHECK(checkpoint BETWEEN 1 AND 5),
            rounds_start INTEGER NOT NULL,
            rounds_end INTEGER NOT NULL,
            provider TEXT NOT NULL,
            model_name TEXT NOT NULL,
            completed_utc TEXT NOT NULL,
            analysis_json TEXT NOT NULL,
            input_hash TEXT NOT NULL,
            output_hash TEXT NOT NULL,
            verified_spend_after_usd REAL NOT NULL,
            PRIMARY KEY(student_id,checkpoint));
          CREATE TABLE dynamic_safe_receipts(
            operation_id INTEGER PRIMARY KEY AUTOINCREMENT,
            student_id TEXT NOT NULL,
            checkpoint INTEGER NOT NULL,
            operation_type TEXT NOT NULL,
            occurred_utc TEXT NOT NULL,
            provider TEXT NOT NULL DEFAULT '',
            model_name TEXT NOT NULL DEFAULT '',
            safe_input_hash TEXT NOT NULL DEFAULT '',
            safe_output_hash TEXT NOT NULL DEFAULT '',
            reported_spend_usd REAL,
            status TEXT NOT NULL);
        """)
        con.execute("""INSERT INTO dynamic_budget_state VALUES(
          1,5.0,?,?,?,'backboard_get_billing_usage_summary_all',0.50,0,'','')""",
                    (INITIAL_REPORTED_SPEND, INITIAL_REPORTED_SPEND, _utc_now()))
        con.execute("UPDATE demo_metadata SET value=? WHERE key='demo_version'",
                    ("skeleton_semester_20rounds_dynamic_scaling_2026-07-24",))
        con.execute("INSERT OR REPLACE INTO demo_metadata VALUES(?,?)",
                    ("dynamic_student_scaling", "true"))
        for number in range(1, 6):
            con.execute("""INSERT INTO dynamic_student_processing(
              student_id,student_number,cohort,analysis_plan,status,included_in_x,
              checkpoint_count,storage_operation_count,generation_count,
              started_utc,completed_utc,start_spend_usd,completion_spend_usd,measured_cost_usd)
              VALUES(?,?,'original_five','legacy_semester_rollup','complete',1,5,6,1,
              '2026-07-24T07:27:55Z','2026-07-24T07:33:15Z',NULL,0.8727,NULL)""",
                        (f"student_synthetic_{number}", number))
        con.commit()
    return {"database": str(path), "students": 5, "x": 5,
            "reported_spend_usd": INITIAL_REPORTED_SPEND}


def add_student(path: Path, number: int, analysis_plan: str = "five_checkpoint") -> dict:
    if analysis_plan not in {"five_checkpoint", "three_checkpoint"}:
        raise ValueError("analysis_plan must be five_checkpoint or three_checkpoint")
    validate_project_sources()
    profile = profile_for(number)
    created = _utc_now()
    base = datetime(2026, 1, 12, 14, 0, tzinfo=timezone.utc)
    all_events = [event for round_no in range(1, 21)
                  for event in round_events(number, round_no)]
    with sqlite3.connect(path) as con:
        exists = con.execute("SELECT 1 FROM students WHERE student_id=?",
                             (f"student_synthetic_{number}",)).fetchone()
        if exists:
            raise ValueError(f"Synthetic Student {number} is already locally seeded")
        sid = f"student_synthetic_{number}"
        con.execute("INSERT INTO students(student_id,created_utc) VALUES(?,?)", (sid, created))
        con.execute("INSERT INTO student_demo_metadata VALUES(?,?,1,?,?,?)",
                    (sid, f"Synthetic Student {number}", "synthetic_dynamic_scaling",
                     profile["key"], profile["description"]))
        positions: dict[int, int] = {}
        for event in all_events:
            pos = positions.get(event.round, 0)
            positions[event.round] = pos + 1
            timestamp = base + timedelta(days=(event.round - 1) * 7,
                                         hours=(number - 1) * 2,
                                         seconds=pos * 22 + int(event.seconds))
            event_id = _event_uuid(event)
            meta = REGIONS[event.expected]
            con.execute("""INSERT INTO attempts VALUES(
              ?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,'')""",
                        (event_id, sid, f"skeleton_s{number}_round_{event.round:02d}",
                         event.question_id, meta["module"], "skeleton-semester-v1",
                         event.expected, event.selected, int(event.expected == event.selected),
                         event.seconds, event.attempt, event.hint,
                         timestamp.isoformat().replace("+00:00", "Z"),
                         timestamp.isoformat().replace("+00:00", "Z"), "local-only"))
            presented = next(i for i, q in enumerate(QUESTIONS, 1)
                             if q[0] == event.question_id)
            con.execute("INSERT INTO semester_event_metadata VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                        (event_id, event.round, (event.round - 1) // 4 + 1, presented,
                         meta["body_area"], meta["side"], meta["module"], event.repeat,
                         event.skip, int(event.action == "eventual_correction"), event.action,
                         "synthetic_dynamic_scaling"))
        cohort = "dynamic_five_checkpoint" if analysis_plan == "five_checkpoint" else "dynamic_three_checkpoint"
        con.execute("""INSERT INTO dynamic_student_processing(
          student_id,student_number,cohort,analysis_plan,status,included_in_x)
          VALUES(?,?,?,?,'locally_seeded',0)""",
                    (sid, number, cohort, analysis_plan))
        counts = con.execute("""SELECT COUNT(*),COUNT(DISTINCT session_id)
          FROM attempts WHERE student_id=?""", (sid,)).fetchone()
        round_counts = [row[0] for row in con.execute("""SELECT COUNT(*)
          FROM attempts a JOIN semester_event_metadata m USING(event_id)
          WHERE student_id=? GROUP BY round_number ORDER BY round_number""", (sid,))]
        assert counts[1] == 20 and 400 <= counts[0] <= 800
        assert len(round_counts) == 20 and all(20 <= x <= 40 for x in round_counts)
        con.commit()
    return {"student_id": sid, "student_number": number, "events": counts[0],
            "rounds": counts[1], "profile_key": profile["key"], "synthetic": True, "analysis_plan": analysis_plan}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", type=Path, default=DEFAULT_DB)
    parser.add_argument("--initialize", action="store_true")
    parser.add_argument("--replace-dynamic-database", action="store_true")
    parser.add_argument("--add-student", type=int)
    parser.add_argument("--analysis-plan", choices=("five_checkpoint", "three_checkpoint"), default="five_checkpoint")
    args = parser.parse_args()
    if args.initialize:
        result = initialize(args.database, replace=args.replace_dynamic_database)
    elif args.add_student is not None:
        result = add_student(args.database, args.add_student, args.analysis_plan)
    else:
        parser.error("choose --initialize or --add-student N")
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
