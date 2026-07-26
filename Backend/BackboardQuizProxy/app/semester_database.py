from __future__ import annotations

import json
from .five_round_database import FiveRoundDatabase
from .skeleton_catalog import (ALLOWLIST, PRESERVED_PDF, PRESERVED_PDF_SHA256,
                               PRESERVED_PDF_TIMESTAMP, PRESERVED_PDF_TITLE)


class SemesterDatabase(FiveRoundDatabase):
    def is_semester_demo(self) -> bool:
        if not self.is_synthetic_demo():
            return False
        with self.connect() as con:
            row = con.execute(
                "SELECT value FROM demo_metadata WHERE key='demo_version'").fetchone()
        return bool(row and str(row[0]).startswith("skeleton_semester_20rounds_"))

    def is_dynamic_scaling(self) -> bool:
        if not self.is_semester_demo():
            return False
        with self.connect() as con:
            row = con.execute(
                "SELECT value FROM demo_metadata WHERE key='dynamic_student_scaling'").fetchone()
        return bool(row and row[0] == "true")

    def student_rows(self) -> list[dict]:
        if not self.is_semester_demo():
            return super().student_rows()
        with self.connect() as con:
            rows = con.execute("""
              SELECT s.student_id,dm.display_name,dm.synthetic,dm.source,dm.profile_key,
                dm.profile_description,COUNT(a.event_id) attempts,
                COUNT(DISTINCT a.session_id) rounds_completed,
                ROUND(AVG(a.correct),4) accuracy,
                ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4) first_attempt_accuracy,
                ROUND(CAST(COUNT(a.event_id) AS REAL)/
                  COUNT(DISTINCT a.session_id||'|'||a.question_id),3) average_attempts_per_question,
                ROUND(AVG(a.response_time_seconds),3) average_response_time,
                SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END) hints_used,
                SUM(m.repeat_used) repeats_used,SUM(m.skip_used) skips_used,
                SUM(m.eventual_correction) eventual_corrections,0 queued_sync,
                s.ai_summary,s.recommended_regions_json
              FROM students s JOIN student_demo_metadata dm USING(student_id)
              LEFT JOIN attempts a USING(student_id)
              LEFT JOIN semester_event_metadata m USING(event_id)
              GROUP BY s.student_id ORDER BY s.student_id""").fetchall()
        result = []
        for row in rows:
            item = dict(row)
            item["synthetic"] = bool(item["synthetic"])
            item["recommended_review_regions"] = json.loads(item.pop("recommended_regions_json"))
            result.append(item)
        return result

    def _breakdown(self, student_id: str, column: str) -> list[dict]:
        if column not in {"body_area", "anatomical_side", "module"}:
            raise ValueError("invalid breakdown")
        with self.connect() as con:
            return [dict(x) for x in con.execute(f"""
              SELECT m.{column} category,COUNT(*) events,
                SUM(CASE WHEN a.attempt_number=1 THEN 1 ELSE 0 END) first_attempts,
                ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4) first_attempt_accuracy,
                ROUND(AVG(a.response_time_seconds),3) average_response_time,
                SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END) hints,
                SUM(m.repeat_used) repeats,SUM(m.skip_used) skips
              FROM attempts a JOIN semester_event_metadata m USING(event_id)
              WHERE a.student_id=? GROUP BY m.{column} ORDER BY m.{column}""",
              (student_id,))]

    def student_detail(self, student_id: str) -> dict | None:
        if not self.is_semester_demo():
            return super().student_detail(student_id)
        students = {x["student_id"]: x for x in self.student_rows()}
        if student_id not in students:
            return None
        with self.connect() as con:
            regions = [dict(x) for x in con.execute("""
              SELECT a.expected_region_id region,c.body_area,c.anatomical_side,c.module,
                COUNT(*) events,SUM(CASE WHEN a.attempt_number=1 THEN 1 ELSE 0 END) first_attempts,
                ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4) first_attempt_accuracy,
                ROUND(AVG(a.response_time_seconds),3) average_response_time
              FROM attempts a JOIN semester_region_catalog c
                ON c.region_id=a.expected_region_id WHERE a.student_id=?
              GROUP BY a.expected_region_id ORDER BY a.expected_region_id""", (student_id,))]
            rounds = [dict(x) for x in con.execute("""
              SELECT m.round_number,m.checkpoint,a.session_id,COUNT(*) interactions,
                COUNT(DISTINCT a.question_id) questions_presented,
                ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4) first_attempt_accuracy,
                ROUND(AVG(a.correct),4) interaction_accuracy,
                ROUND(AVG(a.response_time_seconds),3) average_response_time,
                SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END) hints_used,
                SUM(m.repeat_used) repeats_used,SUM(m.skip_used) skips_used,
                SUM(CASE WHEN a.attempt_number=1 AND
                  ((a.expected_region_id LIKE 'Left%' AND
                    a.selected_region_id='Right'||substr(a.expected_region_id,5))
                  OR (a.expected_region_id LIKE 'Right%' AND
                    a.selected_region_id='Left'||substr(a.expected_region_id,6)))
                  THEN 1 ELSE 0 END) left_right_reversals,
                SUM(CASE WHEN a.attempt_number=1 AND
                  (a.expected_region_id LIKE 'Left%' OR a.expected_region_id LIKE 'Right%')
                  THEN 1 ELSE 0 END) lateralized_first_attempts
              FROM attempts a JOIN semester_event_metadata m USING(event_id)
              WHERE a.student_id=? GROUP BY m.round_number,a.session_id ORDER BY m.round_number""",
              (student_id,))]
            confusions = [dict(x) for x in con.execute("""
              SELECT a.expected_region_id expected,a.selected_region_id selected,
                c.body_area,COUNT(*) count,GROUP_CONCAT(DISTINCT m.round_number) rounds_observed
              FROM attempts a JOIN semester_event_metadata m USING(event_id)
              JOIN semester_region_catalog c ON c.region_id=a.expected_region_id
              WHERE a.student_id=? AND a.attempt_number=1 AND a.correct=0
              GROUP BY a.expected_region_id,a.selected_region_id
              ORDER BY count DESC,expected,selected LIMIT 20""", (student_id,))]
            checkpoints = [dict(x) for x in con.execute("""
              SELECT m.checkpoint,COUNT(*) events,
                ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4) first_attempt_accuracy,
                ROUND(AVG(a.response_time_seconds),3) average_response_time,
                SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END) hints,
                SUM(m.repeat_used) repeats,SUM(m.skip_used) skips
              FROM attempts a JOIN semester_event_metadata m USING(event_id)
              WHERE a.student_id=? GROUP BY m.checkpoint ORDER BY m.checkpoint""",
              (student_id,))]
            analyses = [dict(x) | {"analysis": json.loads(x["analysis_json"])}
                        for x in con.execute("""SELECT scope_key,scope_type,provider,model_name,
                          completed_utc,analysis_json FROM semester_analysis
                          WHERE student_id=? ORDER BY scope_key""", (student_id,))]
        for r in rounds:
            n = r["lateralized_first_attempts"] or 0
            r["left_right_reversal_rate"] = round((r["left_right_reversals"] or 0) / n, 4) if n else 0
        lateral = sum(r["lateralized_first_attempts"] or 0 for r in rounds)
        reversals = sum(r["left_right_reversals"] or 0 for r in rounds)
        item = students[student_id]
        item.update({
            "regions": regions, "rounds": rounds, "recent_sessions": rounds,
            "confusion_pairs": confusions, "checkpoints": checkpoints,
            "body_areas": self._breakdown(student_id, "body_area"),
            "sides": self._breakdown(student_id, "anatomical_side"),
            "modules": self._breakdown(student_id, "module"),
            "left_right_confusion_count": reversals,
            "lateralized_first_attempts": lateral,
            "left_right_confusion_rate": round(reversals / lateral, 4) if lateral else 0,
            "strongest_regions": sorted(regions, key=lambda x: (-x["first_attempt_accuracy"], x["region"]))[:5],
            "weakest_regions": sorted(regions, key=lambda x: (x["first_attempt_accuracy"], x["region"]))[:5],
            "backboard_analyses": analyses,
            "teacher_recommendation": self._recommendation(item["profile_key"]),
            "scope_label": "Whole-body skeletal regions currently represented by the prototype.",
            "preserved_assessment": self.preserved_assessment() if student_id == "student_synthetic_1" else None,
        })
        if student_id == "student_synthetic_1":
            item["reversal_by_body_area"] = [
                {"body_area": area,
                 "reversals": sum(x["count"] for x in confusions if x["body_area"] == area
                                  and self._opposites(x["expected"], x["selected"])),
                 "lateralized_first_attempts": next(
                     (x["first_attempts"] for x in item["body_areas"] if x["category"] == area), 0)}
                for area in ("upper_limbs", "lower_limbs", "jaw")]
            item["paired_confusions"] = {
                bone: sum(x["count"] for x in confusions
                          if x["expected"] in {f"Left{bone}", f"Right{bone}"}
                          and self._opposites(x["expected"], x["selected"]))
                for bone in ("Humerus", "Radius", "Ulna", "Femur", "Ramus")}
            item["non_lateralized_controls"] = [
                x for x in regions if x["region"] in {"Skull", "RibCage", "Pelvis"}]
        return item

    @staticmethod
    def _opposites(expected: str, selected: str) -> bool:
        return ((expected.startswith("Left") and selected == "Right" + expected[4:])
                or (expected.startswith("Right") and selected == "Left" + expected[5:]))

    @staticmethod
    def _recommendation(key: str) -> str:
        return {
            "left_right": "Continue viewpoint changes and subject-left/viewer-left checks across paired arms, legs, and the focused jaw module.",
            "upper_limb_neighbors": "Use side-matched radius-versus-ulna comparison practice before mixed upper-limb retrieval.",
            "upper_lower_terms": "Pair humerus/femur comparisons with concise terminology cues; allow Repeat, then fade hints.",
            "developing": "Continue broad spiral review while reducing hints gradually; preserve axial controls as confidence anchors.",
            "strong": "Use faster mixed-view retrieval and occasional near-neighbour contrasts; retain realistic review.",
        }[key]

    def region_rows(self) -> list[dict]:
        if not self.is_semester_demo():
            return super().region_rows()
        with self.connect() as con:
            return [dict(x) for x in con.execute("""
              SELECT c.region_id region,c.body_area,c.anatomical_side,c.module,
                COUNT(a.event_id) events,ROUND(AVG(a.correct),4) accuracy
              FROM semester_region_catalog c LEFT JOIN attempts a
                ON a.expected_region_id=c.region_id GROUP BY c.region_id ORDER BY c.region_id""")]

    def preserved_assessment(self) -> dict:
        return {
            "title": PRESERVED_PDF_TITLE, "student": "Synthetic Student 1",
            "scope": "Lower jaw", "generation_timestamp": PRESERVED_PDF_TIMESTAMP,
            "sha256": PRESERVED_PDF_SHA256,
            "verification_status": "verified" if PRESERVED_PDF.is_file() else "missing",
            "historical_context": ("Existing tailored jaw assessment generated during the verified "
                                   "five-round Backboard demonstration."),
            "download_url": "/api/v1/teacher/preserved-jaw-assessment",
        }

    def class_summary(self) -> dict:
        students = self.student_rows()
        with self.connect() as con:
            areas = [dict(x) for x in con.execute("""SELECT m.body_area category,COUNT(*) events,
              ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4) first_attempt_accuracy
              FROM attempts a JOIN semester_event_metadata m USING(event_id)
              GROUP BY m.body_area ORDER BY m.body_area""")]
            analyses = [dict(x) | {"analysis": json.loads(x["analysis_json"])}
                        for x in con.execute("""SELECT scope_key,scope_type,provider,model_name,
                          completed_utc,analysis_json FROM semester_analysis
                          WHERE scope_type='class'""")]
        return {"synthetic": True, "students": students, "body_areas": areas,
                "region_allowlist": sorted(ALLOWLIST), "backboard_analyses": analyses}

    def compact_student_summary(self, student_id: str) -> dict:
        d = self.student_detail(student_id)
        if not d:
            raise ValueError("student unavailable")
        return {
            "synthetic": True, "student_label": d["display_name"],
            "verified_region_allowlist": sorted(ALLOWLIST),
            "checkpoints": d["checkpoints"], "body_areas": d["body_areas"],
            "sides": d["sides"], "confusion_pairs": d["confusion_pairs"][:12],
            "strongest_regions": [x["region"] for x in d["strongest_regions"]],
            "weakest_regions": [x["region"] for x in d["weakest_regions"]],
            "left_right_reversal_rate": d["left_right_confusion_rate"],
            "teacher_recommendation": d["teacher_recommendation"],
        }
