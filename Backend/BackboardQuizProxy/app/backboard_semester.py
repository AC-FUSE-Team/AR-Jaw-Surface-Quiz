from __future__ import annotations

import hashlib
import json
import re
from datetime import datetime, timezone

import httpx

from .backboard_five_round import discover_gpt55
from .skeleton_catalog import ALLOWLIST, UNSUPPORTED_NAMES

MAX_GENERATIONS = 6
HARD_SPENDING_CEILING_USD = 5.0
RESERVED_MAX_PER_GENERATION_USD = 0.75
UNSAFE = re.compile(r"https?://|<\s*/?\s*[a-z]|javascript:|(?:^|[\\/])\\.\\.", re.I)
BANNED_ANATOMY = re.compile(
    r"\b(" + "|".join(re.escape(x) for x in sorted(UNSUPPORTED_NAMES)) + r")s?\b", re.I)


class SemesterAnalysisError(ValueError):
    pass


def _hash(value: dict) -> str:
    return hashlib.sha256(json.dumps(
        value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def validate_analysis(raw: str | dict, *, expected_scope: str,
                      student_label: str = "") -> dict:
    try:
        value = json.loads(raw) if isinstance(raw, str) else raw
    except json.JSONDecodeError as exc:
        raise SemesterAnalysisError("Backboard response was not strict JSON") from exc
    if not isinstance(value, dict):
        raise SemesterAnalysisError("Analysis root must be an object")
    serialized = json.dumps(value, ensure_ascii=True)
    if len(serialized) > 60000 or UNSAFE.search(serialized) or BANNED_ANATOMY.search(serialized):
        raise SemesterAnalysisError("Unsafe content or unsupported anatomy rejected")
    forbidden = {"questions", "assessment", "assessment_specification", "pdf", "worksheet"}
    if forbidden & set(value):
        raise SemesterAnalysisError("Assessment/PDF fields are forbidden in semester analysis")
    common = {"synthetic", "scope", "model", "checkpoint_trends", "performance_by_body_area",
              "performance_by_side", "confusion_pairs", "response_time_trend",
              "support_trends", "strongest_regions", "weakest_regions",
              "persistent_misconceptions", "teacher_recommendations"}
    if common - set(value) or value.get("synthetic") is not True:
        raise SemesterAnalysisError("Analysis schema is incomplete")
    if value["scope"] != expected_scope or value["model"] != "gpt-5.5":
        raise SemesterAnalysisError("Scope/model provenance mismatch")
    if expected_scope == "student" and value.get("student_label") != student_label:
        raise SemesterAnalysisError("Student identity mismatch")
    for field in ("strongest_regions", "weakest_regions"):
        if not isinstance(value[field], list) or not set(value[field]) <= ALLOWLIST:
            raise SemesterAnalysisError(f"{field} contains a region outside the allowlist")
    for row in value["performance_by_body_area"]:
        if row.get("body_area") not in {"axial", "upper_limbs", "lower_limbs", "jaw"}:
            raise SemesterAnalysisError("Unsupported body area")
    for row in value["performance_by_side"]:
        if row.get("side") not in {"left", "right", "midline"}:
            raise SemesterAnalysisError("Unsupported anatomical side")
    for pair in value["confusion_pairs"]:
        if pair.get("expected") not in ALLOWLIST or pair.get("selected") not in ALLOWLIST:
            raise SemesterAnalysisError("Confusion pair contains a region outside the allowlist")
    return value


class SemesterBackboard:
    def __init__(self, settings, database, transport=None):
        self.settings, self.database, self.transport = settings, database, transport

    async def _request(self, method: str, path: str, **kwargs) -> dict:
        if not self.settings.backboard_api_key:
            raise RuntimeError("Backboard credentials are missing")
        async with httpx.AsyncClient(
                base_url=self.settings.backboard_base_url,
                timeout=httpx.Timeout(connect=10, read=180, write=30, pool=10),
                transport=self.transport) as client:
            response = await client.request(
                method, path, headers={"X-API-Key": self.settings.backboard_api_key}, **kwargs)
            response.raise_for_status()
        value = response.json()
        if not isinstance(value, dict):
            raise RuntimeError("Backboard returned an invalid response")
        return value

    def state(self) -> dict:
        with self.database.connect() as con:
            return dict(con.execute("SELECT * FROM semester_backboard_state WHERE id=1").fetchone())

    async def discover(self) -> dict:
        found, skip = [], 0
        while True:
            page = await self._request("GET", "/models", params={
                "model_type": "llm", "skip": skip, "limit": 500})
            models = page.get("models", [])
            if not isinstance(models, list):
                raise RuntimeError("Backboard model catalogue format is invalid")
            found.extend(x for x in models if isinstance(x, dict))
            skip += len(models)
            if not models or skip >= int(page.get("total") or skip):
                break
        result = discover_gpt55({"models": found})
        with self.database.connect() as con:
            con.execute("""UPDATE semester_backboard_state SET
              model_discovery_operations=model_discovery_operations+1,
              total_operations=total_operations+1 WHERE id=1""")
        return result

    @staticmethod
    def _reported_cost(value: dict) -> float | None:
        candidates = [
            value.get("cost_usd"), value.get("spending_usd"),
            (value.get("usage") or {}).get("cost_usd") if isinstance(value.get("usage"), dict) else None,
        ]
        for candidate in candidates:
            if isinstance(candidate, (int, float)) and candidate >= 0:
                return float(candidate)
        return None

    async def run(self, provider: str, model_name: str) -> dict:
        state = self.state()
        if state["status"] == "completed":
            return self.cached_result()
        if model_name != "gpt-5.5":
            raise SemesterAnalysisError("Exact GPT-5.5 is required")
        if MAX_GENERATIONS * RESERVED_MAX_PER_GENERATION_USD > HARD_SPENDING_CEILING_USD:
            raise SemesterAnalysisError("Conservative spending reservation exceeds the hard ceiling")
        storage_ops = generation_ops = 0
        reported_cost = 0.0
        all_costs_reported = True
        student_summaries = []
        for number in range(1, 6):
            sid = f"student_synthetic_{number}"
            summary = self.database.compact_student_summary(sid)
            assistant_id = thread_id = ""
            for checkpoint in summary["checkpoints"]:
                payload = {
                    "content": json.dumps({
                        "synthetic": True, "student_label": summary["student_label"],
                        "verified_region_allowlist": summary["verified_region_allowlist"],
                        "checkpoint": checkpoint}, sort_keys=True),
                    "send_to_llm": "false", "stream": False, "memory": "Auto",
                    "web_search": "off", "metadata": {
                        "synthetic_demo": True, "semester_checkpoint": checkpoint["checkpoint"]}}
                if thread_id:
                    payload["thread_id"] = thread_id
                elif assistant_id:
                    payload["assistant_id"] = assistant_id
                stored = await self._request("POST", "/threads/messages", json=payload)
                assistant_id = str(stored.get("assistant_id") or assistant_id)
                thread_id = str(stored.get("thread_id") or thread_id)
                storage_ops += 1
            generated = await self._request("POST", "/threads/messages", json={
                "thread_id": thread_id, "assistant_id": assistant_id,
                "content": json.dumps(self._prompt("student", summary, summary["student_label"]),
                                      sort_keys=True),
                "stream": False, "memory": "Readonly", "memory_response_citation": False,
                "web_search": "off", "json_output": True, "send_to_llm": "true",
                "llm_provider": provider, "model_name": model_name,
                "metadata": {"synthetic_demo": True, "analysis_only": True}})
            analysis = validate_analysis(generated.get("content", ""), expected_scope="student",
                                         student_label=summary["student_label"])
            cost = self._reported_cost(generated)
            if cost is None:
                all_costs_reported = False
            else:
                reported_cost += cost
            generation_ops += 1
            if generation_ops > MAX_GENERATIONS or reported_cost > HARD_SPENDING_CEILING_USD:
                raise SemesterAnalysisError("Hard Backboard spending/operation ceiling exceeded")
            self._save(f"student:{number}", "student", sid, provider, model_name, analysis)
            student_summaries.append({
                "student_label": summary["student_label"],
                "checkpoints": summary["checkpoints"], "body_areas": summary["body_areas"],
                "sides": summary["sides"], "strongest_regions": analysis["strongest_regions"],
                "weakest_regions": analysis["weakest_regions"],
                "persistent_misconceptions": analysis["persistent_misconceptions"]})
        assistant_id = thread_id = ""
        for row in student_summaries:
            stored = await self._request("POST", "/threads/messages", json={
                "content": json.dumps({"synthetic": True, **row}, sort_keys=True),
                "send_to_llm": "false", "stream": False, "memory": "Auto",
                "web_search": "off", "metadata": {"synthetic_demo": True, "class_rollup": True},
                **({"thread_id": thread_id} if thread_id else
                   {"assistant_id": assistant_id} if assistant_id else {})})
            assistant_id = str(stored.get("assistant_id") or assistant_id)
            thread_id = str(stored.get("thread_id") or thread_id)
            storage_ops += 1
        class_payload = {"verified_region_allowlist": sorted(ALLOWLIST),
                         "student_summaries": student_summaries}
        generated = await self._request("POST", "/threads/messages", json={
            "thread_id": thread_id, "assistant_id": assistant_id,
            "content": json.dumps(self._prompt("class", class_payload), sort_keys=True),
            "stream": False, "memory": "Readonly", "memory_response_citation": False,
            "web_search": "off", "json_output": True, "send_to_llm": "true",
            "llm_provider": provider, "model_name": model_name,
            "metadata": {"synthetic_demo": True, "analysis_only": True, "class_rollup": True}})
        analysis = validate_analysis(generated.get("content", ""), expected_scope="class")
        cost = self._reported_cost(generated)
        if cost is None:
            all_costs_reported = False
        else:
            reported_cost += cost
        generation_ops += 1
        if generation_ops != MAX_GENERATIONS or reported_cost > HARD_SPENDING_CEILING_USD:
            raise SemesterAnalysisError("Generation count or spending ceiling validation failed")
        self._save("class:semester", "class", "", provider, model_name, analysis)
        completed = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        discovery = int(state["model_discovery_operations"])
        with self.database.connect() as con:
            con.execute("""UPDATE semester_backboard_state SET status='completed',
              provider=?,model_name=?,storage_operations=?,generation_operations=?,
              total_operations=?,verified_spending_usd=?,spending_source=?,
              completed_utc=? WHERE id=1""",
              (provider, model_name, storage_ops, generation_ops,
               discovery + storage_ops + generation_ops, reported_cost,
               "backboard_response" if all_costs_reported else "not_reported_by_backboard",
               completed))
        return self.cached_result()

    def _save(self, key: str, scope: str, student_id: str, provider: str,
              model: str, analysis: dict) -> None:
        now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        packed = json.dumps(analysis, sort_keys=True, separators=(",", ":"))
        with self.database.connect() as con:
            con.execute("""INSERT OR REPLACE INTO semester_analysis
              VALUES(?,?,?,?,?,?,?,?)""",
              (key, scope, student_id, provider, model, now, packed, _hash(analysis)))

    @staticmethod
    def _prompt(scope: str, evidence: dict, student_label: str = "") -> dict:
        return {
            "task": "Produce compact teacher-facing whole-skeleton learning analysis as strict JSON.",
            "constraints": {
                "synthetic": True, "scope": scope, "student_label": student_label,
                "model_field_must_equal": "gpt-5.5",
                "allowed_regions": sorted(ALLOWLIST),
                "no_assessment_questions_pdf_or_worksheet_fields": True,
                "no_regions_outside_allowlist": True, "no_web_or_external_sources": True},
            "required_schema": {
                "synthetic": True, "scope": scope, "student_label": student_label,
                "model": "gpt-5.5",
                "checkpoint_trends": [{"checkpoint": 1, "observation": "string"}],
                "performance_by_body_area": [{"body_area": "axial|upper_limbs|lower_limbs|jaw",
                                               "observation": "string"}],
                "performance_by_side": [{"side": "left|right|midline", "observation": "string"}],
                "confusion_pairs": [{"expected": "allowlisted region",
                                     "selected": "allowlisted region", "observation": "string"}],
                "response_time_trend": "string",
                "support_trends": {"hints": "string", "repeat": "string", "skip": "string"},
                "strongest_regions": ["allowlisted region"],
                "weakest_regions": ["allowlisted region"],
                "persistent_misconceptions": ["string"],
                "teacher_recommendations": ["string"]},
            "local_evidence": evidence,
        }

    def cached_result(self) -> dict:
        state = self.state()
        return {
            "status": state["status"], "provider": state["provider"],
            "model": state["model_name"],
            "storageOperations": state["storage_operations"],
            "generationOperations": state["generation_operations"],
            "totalOperations": state["total_operations"],
            "verifiedSpendingUsd": state["verified_spending_usd"],
            "spendingSource": state["spending_source"],
            "completedUtc": state["completed_utc"],
        }
