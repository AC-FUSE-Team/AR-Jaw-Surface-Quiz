from __future__ import annotations

import asyncio
import logging
from uuid import uuid4

import httpx

from .config import Settings
from .database import Database
from .models import HintRequest, bounded_text

log = logging.getLogger("jaw_quiz_proxy.backboard")


class BackboardClient:
    def __init__(self, settings: Settings, database: Database, transport=None):
        self.settings, self.database, self.transport = settings, database, transport

    async def hint(self, request: HintRequest) -> tuple[str, str, str, dict[str, int]]:
        if self.settings.mock_mode:
            text = (f"Look for {request.expectedStableRegionId} by comparing its location with "
                    f"{request.selectedStableRegionId}. Take your time and try the highlighted anatomy again.")
            return bounded_text(text), "mock", "mock-" + str(uuid4()), {}
        if not self.settings.backboard_api_key:
            raise RuntimeError("Backboard real mode requires BACKBOARD_API_KEY")
        assistant_id, thread_id = self.database.assistant_state(request.studentId)
        if not assistant_id:
            assistant_id = await self._create_student_assistant(request.studentId)
        prompt = (
            f"Student {request.studentId} was asked to identify {request.expectedStableRegionId}. "
            f"They selected {request.selectedStableRegionId}. The deterministic local result was "
            f"{'correct' if request.correct else 'incorrect'}. Response time was "
            f"{request.responseTimeSeconds:.1f} seconds. This was attempt {request.attemptNumber} "
            f"and hint level {request.hintLevel}. Remember only educational patterns useful for "
            "future practice. Do not change the correctness result. Return a supportive explanation "
            "of no more than two sentences."
        )
        payload = {"assistant_id": assistant_id, "content": prompt, "stream": False,
                   "memory": "Auto", "web_search": "off"}
        if thread_id: payload["thread_id"] = thread_id
        response = await self._request("POST", "/threads/messages", json=payload)
        data = response.json()
        self.database.save_assistant_state(request.studentId, assistant_id, data.get("thread_id", thread_id))
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
            response = await self._request("POST", "/assistants", json={
                "name": "Jaw quiz teacher aggregate summaries",
                "system_prompt": "Use only anonymized, aggregated educational statistics. Never invent numbers, "
                                 "change grades, or request identifying information."
            })
            assistant_id = str(response.json()["assistant_id"])
        prompt = (
            f"Summarize anonymized learner {student_id}. SQLite calculated {calculated['attempts']} attempts "
            f"and accuracy {calculated['accuracy']:.3f}. Weak calculated regions: "
            f"{', '.join(calculated.get('weak_regions', [])) or 'none yet'}. "
            "Return a teacher-readable learning summary without adding numerical claims."
        )
        payload = {"assistant_id": assistant_id, "content": prompt, "stream": False,
                   "memory": "Auto", "web_search": "off"}
        if thread_id: payload["thread_id"] = thread_id
        response = await self._request("POST", "/threads/messages", json=payload)
        data = response.json()
        summary = bounded_text(data.get("content", ""))
        self.database.save_teacher_assistant_state(assistant_id, data.get("thread_id", thread_id), summary)
        self.database.save_student_summary(student_id, summary, [])
        return summary, str(data.get("message_id", ""))

    async def _create_student_assistant(self, student_id: str) -> str:
        response = await self._request("POST", "/assistants", json={
            "name": f"Jaw quiz learner {student_id}",
            "system_prompt": "Use only anonymized educational attempt patterns. Never alter grading. "
                             "Give supportive anatomy hints in at most two sentences.",
            "custom_fact_extraction_prompt": "Extract only recurring anatomy difficulties, confusion "
                                             "pairs, hint needs, and learning progress."
        })
        return str(response.json()["assistant_id"])

    async def _request(self, method: str, path: str, **kwargs) -> httpx.Response:
        headers = {"X-API-Key": self.settings.backboard_api_key}
        error = None
        for attempt in range(self.settings.max_attempts):
            try:
                async with httpx.AsyncClient(base_url=self.settings.backboard_base_url,
                    timeout=self.settings.timeout_seconds, transport=self.transport) as client:
                    response = await client.request(method, path, headers=headers, **kwargs)
                    response.raise_for_status()
                    return response
            except (httpx.TimeoutException, httpx.NetworkError, httpx.HTTPStatusError) as exc:
                error = exc
                log.warning("Backboard request failed attempt=%s type=%s (credentials redacted)",
                            attempt + 1, type(exc).__name__)
                if attempt + 1 < self.settings.max_attempts: await asyncio.sleep(0.1 * (attempt + 1))
        raise RuntimeError("Backboard request failed after bounded retries") from error

# Compatibility import: all callers, including older local scripts, receive the stage-instrumented
# implementation. The legacy definition above is retained only to avoid a destructive file rewrite.
from .instrumented_backboard import BackboardClient as BackboardClient
