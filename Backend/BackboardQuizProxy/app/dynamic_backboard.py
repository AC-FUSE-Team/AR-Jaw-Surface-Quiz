from __future__ import annotations

import asyncio
import hashlib
import json
import re
from datetime import datetime, timezone

import httpx

from .backboard_five_round import discover_gpt55
from .skeleton_catalog import ALLOWLIST, UNSUPPORTED_NAMES

HARD_CEILING_USD = 5.0
CHECKPOINTS = 5
PAID_CALL_LOCK = asyncio.Lock()
UNSAFE = re.compile(r"https?://|<\s*/?\s*[a-z]|javascript:|(?:^|[\\/])\.\.", re.I)
BANNED_ANATOMY = re.compile(
    r"\b(" + "|".join(re.escape(x) for x in sorted(UNSUPPORTED_NAMES)) + r")s?\b", re.I)


class DynamicWorkflowError(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def safe_hash(value: object) -> str:
    return hashlib.sha256(json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()).hexdigest()


def validate_checkpoint_analysis(raw: str | dict, *, student_label: str,
                                 checkpoint: int) -> dict:
    try:
        value = json.loads(raw) if isinstance(raw, str) else raw
    except json.JSONDecodeError as exc:
        raise DynamicWorkflowError("GPT-5.5 checkpoint response was not strict JSON") from exc
    if not isinstance(value, dict):
        raise DynamicWorkflowError("Checkpoint analysis root must be an object")
    packed = json.dumps(value, ensure_ascii=True)
    if len(packed) > 50000 or UNSAFE.search(packed) or BANNED_ANATOMY.search(packed):
        raise DynamicWorkflowError("Unsafe content or unsupported anatomy rejected")
    required = {
        "synthetic", "scope", "student_label", "model", "checkpoint", "rounds",
        "current_strengths", "current_weaknesses", "persistent_confusion_pairs",
        "progress_since_previous_checkpoint", "response_time_and_hint_trends",
        "recommended_teaching_intervention", "focus_for_next_four_rounds", "evidence",
    }
    if required - set(value):
        raise DynamicWorkflowError("Checkpoint analysis schema is incomplete")
    if (value["synthetic"] is not True or value["scope"] != "student_checkpoint"
            or value["student_label"] != student_label or value["model"] != "gpt-5.5"
            or value["checkpoint"] != checkpoint):
        raise DynamicWorkflowError("Checkpoint identity or model provenance mismatch")
    for field in ("current_strengths", "current_weaknesses"):
        if not isinstance(value[field], list) or not set(value[field]) <= ALLOWLIST:
            raise DynamicWorkflowError(f"{field} contains a region outside the allowlist")
    if not isinstance(value["persistent_confusion_pairs"], list):
        raise DynamicWorkflowError("persistent_confusion_pairs must be a list")
    for pair in value["persistent_confusion_pairs"]:
        if pair.get("expected") not in ALLOWLIST or pair.get("selected") not in ALLOWLIST:
            raise DynamicWorkflowError("Confusion pair contains unsupported anatomy")
    if checkpoint == 5:
        if not value.get("final_semester_profile") or not value.get(
                "recommended_future_assessment_focus"):
            raise DynamicWorkflowError("Round-20 final semester fields are required")
    forbidden = {"questions", "pdf", "worksheet", "assessment_specification"}
    if forbidden & set(value):
        raise DynamicWorkflowError("Assessment/PDF fields are forbidden")
    return value


class DynamicBackboard:
    def __init__(self, settings, database, transport=None):
        self.settings = settings
        self.database = database
        self.transport = transport

    async def _request(self, method: str, path: str, **kwargs) -> dict:
        if not self.settings.backboard_api_key:
            raise DynamicWorkflowError("Backboard credentials are missing")
        async with httpx.AsyncClient(
                base_url=self.settings.backboard_base_url,
                timeout=httpx.Timeout(connect=10, read=180, write=30, pool=10),
                transport=self.transport) as client:
            response = await client.request(
                method, path, headers={"X-API-Key": self.settings.backboard_api_key}, **kwargs)
            response.raise_for_status()
        value = response.json()
        if not isinstance(value, dict):
            raise DynamicWorkflowError("Backboard returned an invalid object")
        return value

    def status(self) -> dict:
        return self.database.dynamic_status()

    async def discover(self) -> dict:
        found, skip = [], 0
        while True:
            page = await self._request("GET", "/models", params={
                "model_type": "llm", "provider": "openai", "skip": skip, "limit": 500})
            models = page.get("models", [])
            if not isinstance(models, list):
                raise DynamicWorkflowError("Backboard model catalogue format is invalid")
            found.extend(row for row in models if isinstance(row, dict))
            skip += len(models)
            if not models or skip >= int(page.get("total") or skip):
                break
        result = discover_gpt55({"models": found})
        if not result["compatible"] or result.get("provider") != "openai" or result.get(
                "model_name") != "gpt-5.5":
            raise DynamicWorkflowError("Exact catalogue-confirmed openai/gpt-5.5 is unavailable")
        with self.database.connect() as con:
            con.execute("UPDATE dynamic_budget_state SET last_model_check_utc=? WHERE id=1",
                        (utc_now(),))
        return result

    async def usage_summary(self) -> dict:
        value = await self._request(
            "GET", "/billing/usage/summary", params={"range": "all"})
        total = value.get("total_spending_usd")
        if not isinstance(total, (int, float)) or total < 0:
            raise DynamicWorkflowError("Official billing summary omitted total_spending_usd")
        return {
            "total_spending_usd": round(float(total), 6),
            "verified_utc": utc_now(),
            "source": "Backboard GET /billing/usage/summary?range=all",
        }

    async def refresh_spend(self, *, must_exceed: float | None = None,
                            polls: int = 12) -> dict:
        for index in range(polls):
            summary = await self.usage_summary()
            if must_exceed is None or summary["total_spending_usd"] > must_exceed:
                with self.database.connect() as con:
                    con.execute("""UPDATE dynamic_budget_state SET
                      reported_spend_usd=?,reported_spend_verified_utc=?,spending_source=?
                      WHERE id=1""", (summary["total_spending_usd"],
                                      summary["verified_utc"], summary["source"]))
                return summary
            if index + 1 < polls:
                await asyncio.sleep(5)
        raise DynamicWorkflowError(
            "Backboard billing remained stale after a paid operation; no further request is safe")

    def preview_next(self) -> dict:
        state = self.status()
        with self.database.connect() as con:
            active = con.execute("""SELECT student_id,student_number,status,checkpoint_count
              FROM dynamic_student_processing
              WHERE status IN ('locally_seeded','partially_processed','processing')
              ORDER BY student_number LIMIT 1""").fetchone()
            maximum = con.execute(
                "SELECT MAX(student_number) FROM dynamic_student_processing").fetchone()[0]
        number = int(active["student_number"]) if active else int(maximum) + 1
        projected = state["reported_spend_usd"] + state["estimated_next_student_usd"]
        return {
            **state,
            "next_student_number": number,
            "next_student_id": f"student_synthetic_{number}",
            "active_status": active["status"] if active else "not_seeded",
            "active_checkpoint_count": active["checkpoint_count"] if active else 0,
            "planned_gpt55_generations": CHECKPOINTS - (
                active["checkpoint_count"] if active else 0),
            "estimated_full_student_cost_usd": state["estimated_next_student_usd"],
            "projected_spend_after_completion_usd": round(projected, 6),
            "synthetic_only": True,
            "can_start": (not state["requests_disabled"]
                          and projected < state["hard_ceiling_usd"]),
        }

    def _receipt(self, student_id: str, checkpoint: int, operation_type: str,
                 status: str, **values) -> None:
        with self.database.connect() as con:
            con.execute("""INSERT INTO dynamic_safe_receipts(
              student_id,checkpoint,operation_type,occurred_utc,provider,model_name,
              safe_input_hash,safe_output_hash,reported_spend_usd,status)
              VALUES(?,?,?,?,?,?,?,?,?,?)""", (
                student_id, checkpoint, operation_type, utc_now(),
                values.get("provider", ""), values.get("model", ""),
                values.get("input_hash", ""), values.get("output_hash", ""),
                values.get("spend"), status))

    def _checkpoint_prompt(self, summary: dict, checkpoint: int,
                           previous: dict | None) -> dict:
        start, end = (checkpoint - 1) * 4 + 1, checkpoint * 4
        schema = {
            "synthetic": True, "scope": "student_checkpoint",
            "student_label": summary["student_label"], "model": "gpt-5.5",
            "checkpoint": checkpoint, "rounds": {"start": start, "end": end},
            "current_strengths": ["allowlisted region"],
            "current_weaknesses": ["allowlisted region"],
            "persistent_confusion_pairs": [{
                "expected": "allowlisted region", "selected": "allowlisted region",
                "evidence": "concise string"}],
            "progress_since_previous_checkpoint": "concise string",
            "response_time_and_hint_trends": {
                "response_time": "concise string", "hints": "concise string",
                "repeat_and_skip": "concise string"},
            "recommended_teaching_intervention": "concise string",
            "focus_for_next_four_rounds": ["concise string"],
            "evidence": ["specific calculated evidence"],
        }
        if checkpoint == 5:
            schema.update({
                "final_semester_profile": "concise string",
                "recommended_future_assessment_focus": ["concise string"],
            })
        return {
            "task": "Return a compact teacher-facing checkpoint analysis as strict JSON.",
            "constraints": {
                "synthetic_only": True, "provider": "openai",
                "model_field_must_equal": "gpt-5.5",
                "allowed_regions": sorted(ALLOWLIST),
                "no_questions_pdf_worksheet_or_web": True,
            },
            "required_schema": schema,
            "current_checkpoint_evidence": summary,
            "previous_checkpoint_analysis": previous,
        }

    def _estimate_after_checkpoint(self, student_id: str, checkpoint_delta: float) -> None:
        with self.database.connect() as con:
            row = con.execute("""SELECT checkpoint_count,start_spend_usd
              FROM dynamic_student_processing WHERE student_id=?""", (student_id,)).fetchone()
            if row["checkpoint_count"] == 1:
                estimate = max(.05, checkpoint_delta * CHECKPOINTS * 1.15)
                con.execute("""UPDATE dynamic_budget_state
                  SET estimated_next_student_usd=? WHERE id=1""", (round(estimate, 6),))

    def _estimate_after_student(self) -> float:
        with self.database.connect() as con:
            costs = [float(row[0]) for row in con.execute("""SELECT measured_cost_usd
              FROM dynamic_student_processing WHERE cohort='dynamic_five_checkpoint'
              AND status='complete' AND measured_cost_usd IS NOT NULL""")]
            if not costs:
                return self.status()["estimated_next_student_usd"]
            average = sum(costs) / len(costs)
            estimate = max(average * 1.10, max(costs) * 1.05)
            con.execute("UPDATE dynamic_budget_state SET estimated_next_student_usd=? WHERE id=1",
                        (round(estimate, 6),))
        return round(estimate, 6)

    async def process_student(self, student_id: str, *, max_new_checkpoints: int = 5) -> dict:
        if PAID_CALL_LOCK.locked():
            raise DynamicWorkflowError("Another paid Backboard workflow is already active")
        async with PAID_CALL_LOCK:
            return await self._process_locked(student_id, max_new_checkpoints=max_new_checkpoints)

    async def _process_locked(self, student_id: str, *, max_new_checkpoints: int) -> dict:
        state = self.status()
        if state["requests_disabled"]:
            raise DynamicWorkflowError("Further Backboard requests are disabled")
        with self.database.connect() as con:
            student = con.execute(
                "SELECT * FROM dynamic_student_processing WHERE student_id=?", (student_id,)).fetchone()
        if not student:
            raise DynamicWorkflowError("Student must be locally seeded before processing")
        student = dict(student)
        if student["status"] == "complete":
            return self.database.student_detail(student_id)["processing"]

        spend = await self.refresh_spend()
        remaining_checkpoints = CHECKPOINTS - int(student["checkpoint_count"])
        required = state["estimated_next_student_usd"]
        if student["checkpoint_count"]:
            required *= remaining_checkpoints / CHECKPOINTS
        if spend["total_spending_usd"] + required >= HARD_CEILING_USD:
            reason = "Estimated complete workflow would risk crossing the $5.00 ceiling"
            with self.database.connect() as con:
                con.execute("""UPDATE dynamic_budget_state
                  SET stop_reason=?,requests_disabled=1 WHERE id=1""", (reason,))
            raise DynamicWorkflowError(reason)
        catalogue = await self.discover()
        provider, model = catalogue["provider"], catalogue["model_name"]
        if provider != "openai" or model != "gpt-5.5":
            raise DynamicWorkflowError("Exact openai/gpt-5.5 provenance gate failed")
        if not student["started_utc"]:
            with self.database.connect() as con:
                con.execute("""UPDATE dynamic_student_processing SET started_utc=?,
                  start_spend_usd=?,status='processing' WHERE student_id=?""",
                            (utc_now(), spend["total_spending_usd"], student_id))
        processed_now = 0
        try:
            for checkpoint in range(int(student["checkpoint_count"]) + 1, CHECKPOINTS + 1):
                if processed_now >= max_new_checkpoints:
                    break
                before = (await self.refresh_spend())["total_spending_usd"]
                with self.database.connect() as con:
                    row = con.execute("""SELECT assistant_id,thread_id FROM
                      dynamic_student_processing WHERE student_id=?""", (student_id,)).fetchone()
                summary = self.database.compact_checkpoint_summary(student_id, checkpoint)
                summary_hash = safe_hash(summary)
                storage_payload = {
                    "content": json.dumps(summary, sort_keys=True, separators=(",", ":")),
                    "send_to_llm": "false", "stream": False, "memory": "Auto",
                    "web_search": "off", "metadata": {
                        "synthetic_demo": True, "fictional_student": student_id,
                        "checkpoint": checkpoint, "rounds": summary["rounds"]},
                }
                if row["thread_id"]:
                    storage_payload["thread_id"] = row["thread_id"]
                stored = await self._request("POST", "/threads/messages", json=storage_payload)
                thread_id = str(stored.get("thread_id") or row["thread_id"])
                assistant_id = str(stored.get("assistant_id") or row["assistant_id"])
                if not thread_id:
                    raise DynamicWorkflowError("Backboard did not return a persistent thread")
                with self.database.connect() as con:
                    con.execute("""UPDATE dynamic_student_processing SET assistant_id=?,thread_id=?,
                      storage_operation_count=storage_operation_count+1
                      WHERE student_id=?""", (assistant_id, thread_id, student_id))
                self._receipt(student_id, checkpoint, "storage", "success",
                              input_hash=summary_hash)

                with self.database.connect() as con:
                    previous_row = con.execute("""SELECT analysis_json FROM
                      dynamic_checkpoint_analysis WHERE student_id=? AND checkpoint=?""",
                                               (student_id, checkpoint - 1)).fetchone()
                previous = json.loads(previous_row[0]) if previous_row else None
                prompt = self._checkpoint_prompt(summary, checkpoint, previous)
                prompt_hash = safe_hash(prompt)
                generated = await self._request("POST", "/threads/messages", json={
                    "thread_id": thread_id, "assistant_id": assistant_id,
                    "content": json.dumps(prompt, sort_keys=True, separators=(",", ":")),
                    "stream": False, "memory": "Readonly",
                    "memory_response_citation": False, "web_search": "off",
                    "json_output": True, "send_to_llm": "true",
                    "llm_provider": "openai", "model_name": "gpt-5.5",
                    "metadata": {
                        "synthetic_demo": True, "analysis_only": True,
                        "fictional_student": student_id, "checkpoint": checkpoint},
                })
                if (generated.get("model_provider") != "openai"
                        or generated.get("model_name") != "gpt-5.5"):
                    raise DynamicWorkflowError("Backboard response provenance was not openai/gpt-5.5")
                analysis = validate_checkpoint_analysis(
                    generated.get("content", ""), student_label=summary["student_label"],
                    checkpoint=checkpoint)
                output_hash = safe_hash(analysis)
                verified = await self.refresh_spend(must_exceed=before)
                after = verified["total_spending_usd"]
                now = utc_now()
                with self.database.connect() as con:
                    con.execute("""INSERT INTO dynamic_checkpoint_analysis VALUES(
                      ?,?,?,?,?,?,?,?,?,?,?)""", (
                        student_id, checkpoint, summary["rounds"]["start"],
                        summary["rounds"]["end"], "openai", "gpt-5.5", now,
                        json.dumps(analysis, sort_keys=True, separators=(",", ":")),
                        prompt_hash, output_hash, after))
                    con.execute("""UPDATE dynamic_student_processing SET
                      status='partially_processed',checkpoint_count=?,
                      generation_count=generation_count+1 WHERE student_id=?""",
                                (checkpoint, student_id))
                self._receipt(student_id, checkpoint, "gpt55_generation", "success",
                              provider="openai", model="gpt-5.5", input_hash=prompt_hash,
                              output_hash=output_hash, spend=after)
                self._estimate_after_checkpoint(student_id, after - before)
                processed_now += 1

                with self.database.connect() as con:
                    start_spend = con.execute("""SELECT start_spend_usd FROM
                      dynamic_student_processing WHERE student_id=?""", (student_id,)).fetchone()[0]
                observed_per_checkpoint = max(.01, (after - float(start_spend)) / checkpoint)
                remaining = CHECKPOINTS - checkpoint
                if remaining and after + observed_per_checkpoint * remaining * 1.10 >= HARD_CEILING_USD:
                    raise DynamicWorkflowError(
                        "Observed checkpoint cost leaves insufficient budget to complete this student")

            with self.database.connect() as con:
                current = dict(con.execute("""SELECT * FROM dynamic_student_processing
                  WHERE student_id=?""", (student_id,)).fetchone())
            if current["checkpoint_count"] == CHECKPOINTS:
                final = await self.refresh_spend()
                cost = round(final["total_spending_usd"] - float(current["start_spend_usd"]), 6)
                with self.database.connect() as con:
                    con.execute("""UPDATE dynamic_student_processing SET status='complete',
                      included_in_x=1,completed_utc=?,completion_spend_usd=?,
                      measured_cost_usd=? WHERE student_id=?""",
                                (utc_now(), final["total_spending_usd"], cost, student_id))
                self._estimate_after_student()
            return self.database.student_detail(student_id)["processing"]
        except Exception as exc:
            with self.database.connect() as con:
                con.execute("""UPDATE dynamic_student_processing SET
                  status=CASE WHEN checkpoint_count>0 THEN 'partially_processed' ELSE 'failed' END,
                  failure_reason=? WHERE student_id=?""",
                            (type(exc).__name__ + ": " + str(exc), student_id))
            raise

    def disable(self, reason: str = "Teacher emergency disable control") -> dict:
        with self.database.connect() as con:
            con.execute("""UPDATE dynamic_budget_state SET requests_disabled=1,
              stop_reason=? WHERE id=1""", (reason,))
        return self.status()
