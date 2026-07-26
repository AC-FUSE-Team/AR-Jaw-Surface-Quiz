from __future__ import annotations

import asyncio
import logging
import time
from uuid import uuid4

import httpx

from .config import Settings
from .database import Database
from .memory_policy import MemoryDecision
from .models import HintRequest, bounded_text

log = logging.getLogger("jaw_quiz_proxy.backboard")


class BackboardStageError(RuntimeError):
    def __init__(self, stage: str, exception_class: str):
        super().__init__(f"{stage} failed ({exception_class})")
        self.stage = stage
        self.exception_class = exception_class


class BackboardClient:
    def __init__(self, settings: Settings, database: Database, transport=None):
        self.settings, self.database, self.transport = settings, database, transport

    async def list_assistants(self) -> list[dict]:
        response = await self._stage_request(
            "assistant_lookup", "GET", "/assistants", self.settings.assistant_read_timeout_seconds,
            params={"skip": 0, "limit": 200})
        data = response.json()
        if isinstance(data, list):
            return [item for item in data if isinstance(item, dict)]
        if isinstance(data, dict):
            items = data.get("assistants", data.get("data", []))
            return [item for item in items if isinstance(item, dict)] if isinstance(items, list) else []
        return []

    async def hint(self, request: HintRequest) -> tuple[str, str, str, dict[str, int]]:
        if self.settings.mock_mode:
            text = (f"Look for {request.expectedStableRegionId} by comparing its location with "
                    f"{request.selectedStableRegionId}. Take your time and try the highlighted anatomy again.")
            return bounded_text(text), "mock", "mock-" + str(uuid4()), {}
        if not self.settings.backboard_api_key:
            raise RuntimeError("Backboard real mode requires BACKBOARD_API_KEY")

        assistant_id, _ = self.database.assistant_state(request.studentId)
        if not assistant_id:
            assistant_id = await self._create_student_assistant(request.studentId)

        prompt = (
            f"Student {request.studentId} was asked to identify {request.expectedStableRegionId}. "
            f"They selected {request.selectedStableRegionId}. The deterministic local result was "
            f"{'correct' if request.correct else 'incorrect'}. Response time was "
            f"{request.responseTimeSeconds:.1f} seconds. This was attempt {request.attemptNumber} "
            f"and hint level {request.hintLevel}. Use any saved educational memory only to personalize "
            "the explanation. Do not write or update memory. Do not change the correctness result. "
            "Return a supportive explanation of no more than two sentences."
        )
        payload = {"assistant_id": assistant_id, "content": prompt, "stream": False,
                   "memory": "Readonly", "web_search": "off",
                   "llm_provider": "openai", "model_name": "gpt-4o"}
        response = await self._stage_request(
            "message_send", "POST", "/threads/messages", self.settings.message_read_timeout_seconds,
            json=payload)
        data = response.json()
        returned_thread = str(data.get("thread_id") or "")
        self.database.save_assistant_state(request.studentId, assistant_id, returned_thread)
        usage = {
            "inputTokens": int(data.get("input_tokens") or 0),
            "outputTokens": int(data.get("output_tokens") or 0),
            "totalTokens": int(data.get("total_tokens") or 0)
        }
        return bounded_text(data.get("content", "")), "backboard", str(data.get("message_id", "")), usage

    async def write_learning_memory(self, student_id: str, attempt: dict,
                                    decision: MemoryDecision) -> tuple[str, str, str, dict[str, int]]:
        if not decision.should_write:
            return "", "local-policy", "", {}
        if self.settings.mock_mode:
            text = (f"Mock learning memory recorded for {attempt['expected_region_id']} and its "
                    f"recurring confusion with {attempt['selected_region_id']}.")
            return bounded_text(text), "mock", "mock-memory-" + str(uuid4()), {}
        if not self.settings.backboard_api_key:
            raise RuntimeError("Backboard real mode requires BACKBOARD_API_KEY")

        assistant_id, _ = self.database.assistant_state(student_id)
        if not assistant_id:
            assistant_id = await self._create_student_assistant(student_id)
        relevant_attempts = [row for row in self.database.attempts_for_student(student_id)
                             if not row["correct"]
                             and row["expected_region_id"] == attempt["expected_region_id"]
                             and row["selected_region_id"] == attempt["selected_region_id"]]
        occurrence_count = max(2, len(relevant_attempts))
        prompt = (
            f"Student {student_id} selected {attempt['selected_region_id']} when asked for "
            f"{attempt['expected_region_id']} on {occurrence_count} separate deterministic quiz attempts. "
            f"This meets the documented durable-learning policy {decision.reason}. Remember only "
            "this anonymized recurring educational difficulty for future practice. Do not infer "
            "personal traits or change any correctness result. Respond with one supportive sentence."
        )
        payload = {"assistant_id": assistant_id, "content": prompt, "stream": False,
                   "memory": "Auto", "web_search": "off",
                   "llm_provider": "openai", "model_name": "gpt-4o"}
        response = await self._stage_request(
            "memory_write", "POST", "/threads/messages", self.settings.message_read_timeout_seconds,
            json=payload)
        data = response.json()
        self.database.save_assistant_state(student_id, assistant_id, str(data.get("thread_id") or ""))
        usage = {
            "inputTokens": int(data.get("input_tokens") or 0),
            "outputTokens": int(data.get("output_tokens") or 0),
            "totalTokens": int(data.get("total_tokens") or 0)
        }
        return bounded_text(data.get("content", "")), "backboard", str(data.get("message_id", "")), usage

    async def teacher_summary(self, student_id: str, calculated: dict) -> tuple[str, str]:
        if self.settings.mock_mode:
            return "Mock summary: continue reviewing low-accuracy regions.", "mock"
        if not self.settings.backboard_api_key:
            raise RuntimeError("Backboard real mode requires BACKBOARD_API_KEY")
        assistant_id, thread_id = self.database.teacher_assistant_state()
        if not assistant_id:
            response = await self._stage_request(
                "assistant_create", "POST", "/assistants", self.settings.assistant_read_timeout_seconds,
                json={"name": "Jaw quiz teacher aggregate summaries",
                      "system_prompt": "Use only anonymized, aggregated educational statistics. Never invent "
                                       "numbers, change grades, or request identifying information."})
            assistant_id = str(response.json()["assistant_id"])
            self.database.save_teacher_assistant_state(assistant_id, "", "")
        prompt = (f"Summarize anonymized learner {student_id}. SQLite calculated "
                  f"{calculated['attempts']} attempts and accuracy {calculated['accuracy']:.3f}. "
                  "Return a teacher-readable learning summary without adding numerical claims.")
        payload = {"assistant_id": assistant_id, "content": prompt, "stream": False,
                   "memory": "Auto", "web_search": "off"}
        if thread_id:
            payload["thread_id"] = thread_id
        response = await self._stage_request(
            "message_send", "POST", "/threads/messages", self.settings.message_read_timeout_seconds,
            json=payload)
        data = response.json()
        summary = bounded_text(data.get("content", ""))
        self.database.save_teacher_assistant_state(assistant_id, data.get("thread_id", thread_id), summary)
        self.database.save_student_summary(student_id, summary, [])
        return summary, str(data.get("message_id", ""))

    async def _create_student_assistant(self, student_id: str) -> str:
        response = await self._stage_request(
            "assistant_create", "POST", "/assistants", self.settings.assistant_read_timeout_seconds,
            json={
                "name": f"Jaw quiz learner {student_id}",
                "system_prompt": "Use only anonymized educational attempt patterns. Never alter grading. "
                                 "Give supportive anatomy hints in at most two sentences.",
                "custom_fact_extraction_prompt": "Extract only recurring anatomy difficulties, confusion "
                                                 "pairs, hint needs, and learning progress."
            })
        assistant_id = str(response.json()["assistant_id"])
        if not assistant_id:
            raise BackboardStageError("assistant_create", "MissingAssistantId")
        # Critical ordering: persist the mapping before any message is attempted.
        self.database.save_assistant_state(student_id, assistant_id, "")
        return assistant_id

    async def _stage_request(self, stage: str, method: str, path: str,
                             read_timeout: float, **kwargs) -> httpx.Response:
        headers = {"X-API-Key": self.settings.backboard_api_key}
        attempts = max(1, self.settings.max_attempts)
        last_error: Exception | None = None
        for attempt in range(1, attempts + 1):
            started = time.perf_counter()
            log.info("%s_started attempt=%d", stage, attempt)
            try:
                timeout = httpx.Timeout(
                    connect=self.settings.connect_timeout_seconds,
                    read=read_timeout,
                    write=self.settings.write_timeout_seconds,
                    pool=self.settings.pool_timeout_seconds)
                async with httpx.AsyncClient(base_url=self.settings.backboard_base_url,
                                              timeout=timeout, transport=self.transport) as client:
                    response = await client.request(method, path, headers=headers, **kwargs)
                    response.raise_for_status()
                elapsed = round((time.perf_counter() - started) * 1000)
                log.info("%s_completed elapsed_ms=%d http_status=%d attempt=%d",
                         stage, elapsed, response.status_code, attempt)
                return response
            except (httpx.TimeoutException, httpx.NetworkError, httpx.HTTPStatusError) as error:
                last_error = error
                elapsed = round((time.perf_counter() - started) * 1000)
                exception_class = type(error).__name__
                timeout_stage = stage if isinstance(error, httpx.TimeoutException) else "none"
                log.warning("%s_failed elapsed_ms=%d exception_class=%s timeout_stage=%s attempt=%d",
                            stage, elapsed, exception_class, timeout_stage, attempt)
                if attempt < attempts:
                    await asyncio.sleep(0.1 * attempt)
        raise BackboardStageError(stage, type(last_error).__name__ if last_error else "Unknown") from last_error
