from __future__ import annotations

import json
import sqlite3

from .database import Database


class DemoAwareDatabase(Database):
    """Adds analytics only when demo-only companion tables are present."""

    def student_rows(self) -> list[dict]:
        if not self.is_synthetic_demo():
            rows = super().student_rows()
            for item in rows:
                item.update({"display_name": item["student_id"], "synthetic": False,
                             "source": "quiz", "profile_key": "", "profile_description": "",
                             "first_attempt_accuracy": item["accuracy"],
                             "average_attempts_per_question": 1.0,
                             "repeats_used": 0, "skips_used": 0,
                             "eventual_corrections": 0})
            return rows
        with self.connect() as con:
            rows = con.execute("""
              SELECT s.student_id, dm.display_name,dm.synthetic,dm.source,dm.profile_key,
                dm.profile_description,COUNT(a.event_id) attempts,
                COALESCE(AVG(a.correct),0.0) accuracy,
                COALESCE(AVG(CASE WHEN a.attempt_number=1 THEN a.correct END),0.0)
                  first_attempt_accuracy,
                COALESCE(CAST(COUNT(a.event_id) AS REAL) /
                  NULLIF(COUNT(DISTINCT a.session_id || '|' || a.question_id),0),0.0)
                  average_attempts_per_question,
                COALESCE(AVG(a.response_time_seconds),0.0) average_response_time,
                COALESCE(SUM(CASE WHEN a.hint_level>0 THEN 1 ELSE 0 END),0) hints_used,
                COALESCE(SUM(m.repeat_used),0) repeats_used,
                COALESCE(SUM(m.skip_used),0) skips_used,
                COALESCE(SUM(m.eventual_correction),0) eventual_corrections,
                0 queued_sync,s.ai_summary,s.recommended_regions_json
              FROM students s JOIN student_demo_metadata dm ON dm.student_id=s.student_id
              LEFT JOIN attempts a ON a.student_id=s.student_id
              LEFT JOIN attempt_demo_metadata m ON m.event_id=a.event_id
              GROUP BY s.student_id ORDER BY s.student_id""").fetchall()
        result = []
        for row in rows:
            item = dict(row)
            for key in ("accuracy", "first_attempt_accuracy"):
                item[key] = round(item[key], 4)
            item["average_attempts_per_question"] = round(item["average_attempts_per_question"], 3)
            item["average_response_time"] = round(item["average_response_time"], 3)
            item["synthetic"] = bool(item["synthetic"])
            item["recommended_review_regions"] = json.loads(item.pop("recommended_regions_json"))
            result.append(item)
        return result

    def student_detail(self, student_id: str) -> dict | None:
        if not self.is_synthetic_demo():
            item = super().student_detail(student_id)
            if item:
                item.update({"display_name": student_id, "synthetic": False,
                             "left_right_confusion_count": 0,
                             "left_right_confusion_rate": 0.0,
                             "lateralized_first_attempts": 0,
                             "interpretation": "Calculated local quiz summary; no diagnostic inference is made."})
            return item
        students = {row["student_id"]: row for row in self.student_rows()}
        if student_id not in students:
            return None
        with self.connect() as con:
            regions = [dict(row) for row in con.execute("""
              SELECT expected_region_id region,COUNT(*) attempts,ROUND(AVG(correct),4) accuracy,
                ROUND(AVG(CASE WHEN attempt_number=1 THEN correct END),4) first_attempt_accuracy,
                ROUND(AVG(response_time_seconds),3) average_response_time
              FROM attempts WHERE student_id=? GROUP BY expected_region_id
              ORDER BY accuracy,attempts DESC,region""", (student_id,))]
            confusion = [dict(row) for row in con.execute("""
              SELECT a.expected_region_id expected,a.selected_region_id selected,COUNT(*) count,
                (SELECT COUNT(*) FROM attempts total WHERE total.student_id=a.student_id
                 AND total.expected_region_id=a.expected_region_id
                 AND total.attempt_number=1) first_attempt_opportunities
              FROM attempts a WHERE a.student_id=? AND a.correct=0 AND a.attempt_number=1
              GROUP BY a.expected_region_id,a.selected_region_id
              ORDER BY count DESC,expected,selected LIMIT 10""", (student_id,))]
            sessions = [dict(row) for row in con.execute("""
              SELECT session_id,COUNT(*) attempts,ROUND(AVG(correct),4) accuracy,
                ROUND(AVG(CASE WHEN attempt_number=1 THEN correct END),4) first_attempt_accuracy,
                ROUND(AVG(response_time_seconds),3) average_response_time,
                SUM(CASE WHEN hint_level>0 THEN 1 ELSE 0 END) hints_used,
                MIN(utc_timestamp) started_utc,MAX(utc_timestamp) ended_utc
              FROM attempts WHERE student_id=? GROUP BY session_id ORDER BY started_utc""",
              (student_id,))]
            lr = con.execute("""
              SELECT SUM(CASE WHEN attempt_number=1 AND
                ((expected_region_id LIKE 'Left%' AND selected_region_id='Right'||substr(expected_region_id,5))
                OR (expected_region_id LIKE 'Right%' AND selected_region_id='Left'||substr(expected_region_id,6)))
                THEN 1 ELSE 0 END) reversals,
                SUM(CASE WHEN attempt_number=1 AND
                (expected_region_id LIKE 'Left%' OR expected_region_id LIKE 'Right%')
                THEN 1 ELSE 0 END) opportunities
              FROM attempts WHERE student_id=?""", (student_id,)).fetchone()
        item = students[student_id]
        opportunities = lr["opportunities"] or 0
        item.update({"regions": regions, "confusion_pairs": confusion,
                     "recent_sessions": sessions,
                     "left_right_confusion_count": lr["reversals"] or 0,
                     "lateralized_first_attempts": opportunities})
        item["left_right_confusion_rate"] = round(item["left_right_confusion_count"] / opportunities, 4) if opportunities else 0.0
        item["interpretation"] = self._interpretation(item["profile_key"])
        return item

    @staticmethod
    def _interpretation(profile: str) -> str:
        return {
          "left_right": "Synthetic Student 1 generally recognizes jaw structures but frequently reverses anatomical left and right. Performance is stronger on non-lateralized structures. A targeted assessment should emphasize left/right discrimination from multiple viewpoints. This is a pattern observed in fictional quiz interactions, not a diagnosis or validated learning conclusion.",
          "neighbouring_regions": "This fictional learner distinguishes left and right but often selects a nearby process or attachment region. Review of boundaries between adjacent structures may be useful.",
          "terminology": "This fictional learner repeats prompts more often and confuses similarly named mental-region terms; hinted attempts are generally stronger.",
          "developing": "This fictional learner shows a positive session-by-session trend: responses become faster and more accurate while hint use declines.",
          "high_performing": "This fictional learner is consistently quick and accurate, with a small number of realistic errors and little support use.",
        }.get(profile, "Calculated local quiz summary; no diagnostic inference is made.")
