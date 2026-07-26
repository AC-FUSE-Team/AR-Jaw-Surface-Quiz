from __future__ import annotations

import json
from .demo_database import DemoAwareDatabase


class FiveRoundDatabase(DemoAwareDatabase):
    """Round-aware analytics; legacy synthetic and normal databases retain their old path."""

    def is_five_round_demo(self) -> bool:
        if not self.is_synthetic_demo():
            return False
        with self.connect() as con:
            row = con.execute(
                "SELECT value FROM demo_metadata WHERE key='demo_version'").fetchone()
        return bool(row and str(row[0]).startswith("five_rounds_"))

    def student_rows(self) -> list[dict]:
        if not self.is_five_round_demo():
            return super().student_rows()
        with self.connect() as con:
            rows = con.execute("""
              SELECT s.student_id,dm.display_name,dm.synthetic,dm.source,dm.profile_key,
                dm.profile_description,COUNT(a.event_id) attempts,
                COUNT(DISTINCT a.session_id) rounds_completed,
                COALESCE(AVG(a.correct),0) accuracy,
                COALESCE(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),0)
                  first_attempt_accuracy,
                COALESCE(CAST(COUNT(a.event_id) AS REAL)/
                  NULLIF(COUNT(DISTINCT a.session_id||'|'||a.question_id),0),0)
                  average_attempts_per_question,
                COALESCE(AVG(a.response_time_seconds),0) average_response_time,
                SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END) hints_used,
                SUM(m.repeat_used) repeats_used,SUM(m.skip_used) skips_used,
                SUM(m.eventual_correction) eventual_corrections,
                0 queued_sync,s.ai_summary,s.recommended_regions_json
              FROM students s JOIN student_demo_metadata dm USING(student_id)
              LEFT JOIN attempts a USING(student_id)
              LEFT JOIN attempt_demo_metadata m USING(event_id)
              GROUP BY s.student_id ORDER BY s.student_id""").fetchall()
        result = []
        for row in rows:
            item = dict(row)
            for key in ("accuracy", "first_attempt_accuracy"):
                item[key] = round(item[key], 4)
            for key in ("average_attempts_per_question", "average_response_time"):
                item[key] = round(item[key], 3)
            item["synthetic"] = bool(item["synthetic"])
            item["recommended_review_regions"] = json.loads(item.pop("recommended_regions_json"))
            result.append(item)
        return result

    def student_detail(self, student_id: str) -> dict | None:
        if not self.is_five_round_demo():
            return super().student_detail(student_id)
        students = {row["student_id"]: row for row in self.student_rows()}
        if student_id not in students:
            return None
        with self.connect() as con:
            regions = [dict(row) for row in con.execute("""
              SELECT expected_region_id region,COUNT(*) attempts,
                ROUND(AVG(correct),4) accuracy,
                ROUND(AVG(CASE WHEN attempt_number=1 THEN correct END),4) first_attempt_accuracy,
                ROUND(AVG(response_time_seconds),3) average_response_time
              FROM attempts WHERE student_id=? GROUP BY expected_region_id""", (student_id,))]
            confusions = [dict(row) for row in con.execute("""
              SELECT expected_region_id expected,selected_region_id selected,COUNT(*) count,
                (SELECT COUNT(*) FROM attempts z WHERE z.student_id=a.student_id
                 AND z.expected_region_id=a.expected_region_id AND z.attempt_number=1)
                 first_attempt_opportunities,
                GROUP_CONCAT(DISTINCT m.round_number) rounds_observed
              FROM attempts a JOIN attempt_demo_metadata m USING(event_id)
              WHERE a.student_id=? AND a.attempt_number=1 AND a.correct=0
              GROUP BY expected_region_id,selected_region_id
              ORDER BY count DESC,expected,selected LIMIT 12""", (student_id,))]
            rounds = [dict(row) for row in con.execute("""
              SELECT m.round_number,a.session_id,COUNT(*) interactions,
                COUNT(DISTINCT a.question_id) questions_presented,
                SUM(CASE WHEN a.attempt_number=1 THEN 1 ELSE 0 END) first_attempts,
                ROUND(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),4)
                  first_attempt_accuracy,
                ROUND(AVG(a.correct),4) interaction_accuracy,
                ROUND(CAST(COUNT(*) AS REAL)/COUNT(DISTINCT a.question_id),3)
                  average_attempts_per_question,
                SUM(CASE WHEN a.attempt_number=1 AND EXISTS(
                  SELECT 1 FROM attempts z WHERE z.student_id=a.student_id
                  AND z.session_id=a.session_id AND z.question_id=a.question_id AND z.correct=1)
                  THEN 1 ELSE 0 END) questions_completed,
                ROUND(CAST(SUM(CASE WHEN a.attempt_number=1 AND EXISTS(
                  SELECT 1 FROM attempts z WHERE z.student_id=a.student_id
                  AND z.session_id=a.session_id AND z.question_id=a.question_id AND z.correct=1)
                  THEN 1 ELSE 0 END) AS REAL)/COUNT(DISTINCT a.question_id),4) eventual_accuracy,
                ROUND(AVG(a.response_time_seconds),3) average_response_time,
                SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END) hints_used,
                SUM(m.repeat_used) repeats_used,SUM(m.skip_used) skips_used,
                SUM(CASE WHEN a.attempt_number=1 AND
                  ((a.expected_region_id LIKE 'Left%' AND a.selected_region_id='Right'||substr(a.expected_region_id,5))
                  OR (a.expected_region_id LIKE 'Right%' AND a.selected_region_id='Left'||substr(a.expected_region_id,6)))
                  THEN 1 ELSE 0 END) left_right_reversals,
                SUM(CASE WHEN a.attempt_number=1 AND
                  (a.expected_region_id LIKE 'Left%' OR a.expected_region_id LIKE 'Right%')
                  THEN 1 ELSE 0 END) lateralized_first_attempts,
                MIN(a.utc_timestamp) started_utc,MAX(a.utc_timestamp) ended_utc
              FROM attempts a JOIN attempt_demo_metadata m USING(event_id)
              WHERE a.student_id=? GROUP BY m.round_number,a.session_id
              ORDER BY m.round_number""", (student_id,))]
        for round_row in rounds:
            n = round_row["lateralized_first_attempts"] or 0
            round_row["left_right_reversal_rate"] = round(
                (round_row["left_right_reversals"] or 0) / n, 4) if n else 0
            round_row["attempts"] = round_row["interactions"]
            round_row["accuracy"] = round_row["interaction_accuracy"]
        item = students[student_id]
        item.update({
            "regions": regions, "confusion_pairs": confusions, "rounds": rounds,
            "recent_sessions": rounds, "questions_presented": sum(x["questions_presented"] for x in rounds),
            "questions_completed": sum(x["questions_completed"] for x in rounds),
            "eventual_accuracy": round(sum(x["questions_completed"] for x in rounds) /
                                       max(1, sum(x["questions_presented"] for x in rounds)), 4),
            "strongest_regions": sorted(regions, key=lambda x: (-x["first_attempt_accuracy"], x["region"]))[:5],
            "weakest_regions": sorted(regions, key=lambda x: (x["first_attempt_accuracy"], x["region"]))[:5],
            "interpretation": self._interpretation(item["profile_key"]),
        })
        lateral = sum(x["lateralized_first_attempts"] for x in rounds)
        reversals = sum(x["left_right_reversals"] for x in rounds)
        item.update({"left_right_confusion_count": reversals,
                     "lateralized_first_attempts": lateral,
                     "left_right_confusion_rate": round(reversals / lateral, 4) if lateral else 0})
        if student_id == "student_synthetic_1":
            item["ramus_reversals"] = {}
            with self.connect() as con:
                for expected, selected in (("LeftRamus", "RightRamus"),
                                           ("RightRamus", "LeftRamus")):
                    item["ramus_reversals"][f"{expected}_to_{selected}"] = [
                        con.execute("""SELECT COUNT(*) FROM attempts WHERE student_id=?
                          AND session_id=? AND attempt_number=1 AND expected_region_id=?
                          AND selected_region_id=?""",
                          (student_id, r["session_id"], expected, selected)).fetchone()[0]
                        for r in rounds]
            item["misconception_improved"] = (
                rounds[-1]["left_right_reversal_rate"] < rounds[0]["left_right_reversal_rate"])
            item["assessment_priority_reason"] = (
                "Left/right reversals improve but remain the dominant Round 5 misconception, "
                "so bilateral discrimination remains the tailored assessment priority.")
        return item

    def compact_student1_summary(self) -> dict:
        detail = self.student_detail("student_synthetic_1")
        if not detail:
            raise ValueError("Synthetic Student 1 is unavailable")
        return {
            "synthetic": True, "student_label": detail["display_name"],
            "rounds": [{
                "round": r["round_number"], "interactions": r["interactions"],
                "questions_presented": r["questions_presented"],
                "first_attempt_accuracy": r["first_attempt_accuracy"],
                "eventual_accuracy": r["eventual_accuracy"],
                "average_response_seconds": r["average_response_time"],
                "hints": r["hints_used"], "repeats": r["repeats_used"], "skips": r["skips_used"],
                "left_right_reversals": r["left_right_reversals"],
                "left_right_reversal_rate": r["left_right_reversal_rate"],
            } for r in detail["rounds"]],
            "confusion_pairs": detail["confusion_pairs"][:8],
            "strongest_regions": [x["region"] for x in detail["strongest_regions"]],
            "weakest_regions": [x["region"] for x in detail["weakest_regions"]],
            "progression": {
                "improved": detail.get("misconception_improved", False),
                "priority_reason": detail.get("assessment_priority_reason", "")},
        }
