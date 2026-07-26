from __future__ import annotations

import json

from .semester_database import SemesterDatabase
from .skeleton_catalog import ALLOWLIST


class DynamicScalingDatabase(SemesterDatabase):
    """Semester adapter with processing state for the isolated scaling demo."""

    def student_rows(self) -> list[dict]:
        rows = super().student_rows()
        if not self.is_dynamic_scaling():
            return rows
        with self.connect() as con:
            processing = {
                row["student_id"]: dict(row)
                for row in con.execute("SELECT * FROM dynamic_student_processing")}
        for item in rows:
            state = processing.get(item["student_id"], {})
            item.update({
                "processing_status": state.get("status", "locally_seeded"),
                "included_in_x": bool(state.get("included_in_x", 0)),
                "analysis_plan": state.get("analysis_plan", ""),
                "cohort": state.get("cohort", ""),
                "checkpoint_count": state.get("checkpoint_count", 0),
                "storage_operation_count": state.get("storage_operation_count", 0),
                "generation_count": state.get("generation_count", 0),
                "cached": state.get("status") == "complete",
            })
        return rows

    @staticmethod
    def _recommendation(key: str) -> str:
        known = {
            "left_right": "Continue viewpoint changes and subject-left/viewer-left checks across paired arms, legs, and the focused jaw module.",
            "upper_limb_neighbors": "Use side-matched radius-versus-ulna comparison practice before mixed upper-limb retrieval.",
            "upper_lower_terms": "Pair humerus/femur comparisons with concise terminology cues; allow Repeat, then fade hints.",
            "developing": "Continue broad spiral review while reducing hints gradually; preserve axial controls as confidence anchors.",
            "strong": "Use faster mixed-view retrieval and occasional near-neighbour contrasts; retain realistic review.",
        }
        if key in known:
            return known[key]
        readable = key.split("__", 1)[0].replace("_", " ")
        return (f"Use targeted {readable} comparison practice, then check transfer with "
                "mixed verified-region retrieval and gradually faded support.")

    def student_detail(self, student_id: str) -> dict | None:
        detail = super().student_detail(student_id)
        if detail is None or not self.is_dynamic_scaling():
            return detail
        with self.connect() as con:
            processing_row = con.execute(
                "SELECT * FROM dynamic_student_processing WHERE student_id=?",
                (student_id,)).fetchone()
            analyses = [
                dict(row) | {"analysis": json.loads(row["analysis_json"])}
                for row in con.execute("""SELECT checkpoint,rounds_start,rounds_end,
                  provider,model_name,completed_utc,analysis_json,input_hash,output_hash,
                  verified_spend_after_usd FROM dynamic_checkpoint_analysis
                  WHERE student_id=? ORDER BY checkpoint""", (student_id,))]
        detail["processing"] = dict(processing_row) if processing_row else None
        detail["checkpoint_analyses"] = analyses
        return detail

    def compact_checkpoint_summary(self, student_id: str, checkpoint: int) -> dict:
        if checkpoint not in range(1, 6):
            raise ValueError("checkpoint must be 1 through 5")
        detail = self.student_detail(student_id)
        if not detail:
            raise ValueError("student unavailable")
        rounds_start, rounds_end = (checkpoint - 1) * 4 + 1, checkpoint * 4
        selected_rounds = [
            row for row in detail["rounds"]
            if rounds_start <= row["round_number"] <= rounds_end]
        with self.connect() as con:
            areas = [dict(row) for row in con.execute("""
              SELECT m.body_area,COUNT(*) interactions,
                ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4)
                  first_attempt_accuracy,
                ROUND(AVG(a.response_time_seconds),3) average_response_time,
                SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END) hints,
                SUM(m.repeat_used) repeats,SUM(m.skip_used) skips
              FROM attempts a JOIN semester_event_metadata m USING(event_id)
              WHERE a.student_id=? AND m.checkpoint=?
              GROUP BY m.body_area ORDER BY m.body_area""", (student_id, checkpoint))]
            confusions = [dict(row) for row in con.execute("""
              SELECT a.expected_region_id expected,a.selected_region_id selected,COUNT(*) count
              FROM attempts a JOIN semester_event_metadata m USING(event_id)
              WHERE a.student_id=? AND m.checkpoint=? AND a.attempt_number=1 AND a.correct=0
              GROUP BY a.expected_region_id,a.selected_region_id
              ORDER BY count DESC,expected,selected LIMIT 8""", (student_id, checkpoint))]
        return {
            "synthetic": True,
            "fictional_identifier": student_id,
            "student_label": detail["display_name"],
            "profile_key": detail["profile_key"],
            "profile_description": detail["profile_description"],
            "checkpoint": checkpoint,
            "rounds": {"start": rounds_start, "end": rounds_end},
            "round_metrics": selected_rounds,
            "body_area_metrics": areas,
            "confusion_pairs": confusions,
            "verified_region_allowlist": sorted(ALLOWLIST),
        }

    def dynamic_status(self) -> dict:
        if not self.is_dynamic_scaling():
            raise ValueError("not a dynamic scaling database")
        with self.connect() as con:
            budget = dict(con.execute(
                "SELECT * FROM dynamic_budget_state WHERE id=1").fetchone())
            counts = dict(con.execute("""SELECT
              COUNT(*) locally_seeded_students,
              SUM(CASE WHEN included_in_x=1 THEN 1 ELSE 0 END) current_x,
              SUM(CASE WHEN status='complete' THEN 1 ELSE 0 END) completed_students,
              SUM(CASE WHEN status='partially_processed' THEN 1 ELSE 0 END) partial_students,
              SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) failed_students,
              SUM(storage_operation_count) storage_operations,
              SUM(generation_count) generation_operations
              FROM dynamic_student_processing""").fetchone())
        return {
            "synthetic": True, **budget, **counts,
            "remaining_budget_usd": round(
                budget["hard_ceiling_usd"] - budget["reported_spend_usd"], 6),
        }
