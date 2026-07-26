from __future__ import annotations

import asyncio
import logging
from pathlib import Path

import httpx
import pytest

from app.config import Settings
from app.database import Database
from app.instrumented_backboard import BackboardClient, BackboardStageError
from app.models import HintRequest


def config(tmp_path: Path, key: str = "TEST_SECRET_VALUE") -> Settings:
    return Settings(
        database_path=tmp_path / "instrumented.sqlite3",
        mock_mode=False,
        backboard_api_key=key,
        backboard_base_url="https://app.backboard.io/api",
        quiz_proxy_token="",
        cors_origins=("http://localhost",),
        timeout_seconds=.01,
        max_attempts=1,
        connect_timeout_seconds=.01,
        assistant_read_timeout_seconds=.01,
        message_read_timeout_seconds=.01,
        write_timeout_seconds=.01,
        pool_timeout_seconds=.01)


def hint(student: str = "student_999") -> HintRequest:
    return HintRequest(
        studentId=student,
        sessionId="verification_session_002",
        questionId="verification_right_mental_foramen",
        expectedStableRegionId="RightMentalForamen",
        selectedStableRegionId="MentalProtuberance",
        correct=False,
        responseTimeSeconds=7.2,
        attemptNumber=1,
        hintLevel=0)


def test_assistant_timeout_is_stage_specific_and_never_sends_message(tmp_path, caplog):
    counts = {"assistant": 0, "message": 0}

    def handler(request: httpx.Request):
        if request.url.path.endswith("/assistants"):
            counts["assistant"] += 1
        else:
            counts["message"] += 1
        raise httpx.ReadTimeout("simulated", request=request)

    client = BackboardClient(config(tmp_path), Database(tmp_path / "instrumented.sqlite3"),
                             httpx.MockTransport(handler))
    with caplog.at_level(logging.INFO), pytest.raises(BackboardStageError):
        asyncio.run(client.hint(hint()))
    assert counts == {"assistant": 1, "message": 0}
    assert "assistant_create_started" in caplog.text
    assert "assistant_create_failed" in caplog.text
    assert "timeout_stage=assistant_create" in caplog.text
    assert "message_send_started" not in caplog.text


def test_mapping_is_saved_before_exactly_one_message_attempt(tmp_path, caplog):
    database = Database(tmp_path / "instrumented.sqlite3")
    counts = {"assistant": 0, "message": 0}

    def handler(request: httpx.Request):
        if request.url.path.endswith("/assistants"):
            counts["assistant"] += 1
            return httpx.Response(200, json={"assistant_id": "assistant-test-999"}, request=request)
        counts["message"] += 1
        assert database.assistant_state("student_999")[0] == "assistant-test-999"
        return httpx.Response(200, json={
            "content": "Supportive test response.", "assistant_id": "assistant-test-999",
            "thread_id": "thread-test-999", "message_id": "message-test-999",
            "input_tokens": 10, "output_tokens": 5, "total_tokens": 15}, request=request)

    client = BackboardClient(config(tmp_path), database, httpx.MockTransport(handler))
    with caplog.at_level(logging.INFO):
        text, source, reference, usage = asyncio.run(client.hint(hint()))
    assert counts == {"assistant": 1, "message": 1}
    assert database.assistant_state("student_999") == ("assistant-test-999", "thread-test-999")
    assert (text, source, reference) == ("Supportive test response.", "backboard", "message-test-999")
    assert usage["totalTokens"] == 15
    assert "assistant_create_completed" in caplog.text
    assert "message_send_completed" in caplog.text


def test_message_timeout_keeps_mapping_and_logs_no_secret_or_payload(tmp_path, caplog):
    database = Database(tmp_path / "instrumented.sqlite3")
    counts = {"assistant": 0, "message": 0}

    def handler(request: httpx.Request):
        if request.url.path.endswith("/assistants"):
            counts["assistant"] += 1
            return httpx.Response(200, json={"assistant_id": "assistant-test-999"}, request=request)
        counts["message"] += 1
        raise httpx.ReadTimeout("simulated", request=request)

    client = BackboardClient(config(tmp_path), database, httpx.MockTransport(handler))
    with caplog.at_level(logging.INFO), pytest.raises(BackboardStageError):
        asyncio.run(client.hint(hint()))
    assert counts == {"assistant": 1, "message": 1}
    assert database.assistant_state("student_999") == ("assistant-test-999", "")
    assert "message_send_started" in caplog.text
    assert "message_send_failed" in caplog.text
    assert "timeout_stage=message_send" in caplog.text
    for forbidden in ("TEST_SECRET_VALUE", "RightMentalForamen", "MentalProtuberance",
                      "verification_session_002"):
        assert forbidden not in caplog.text


def test_lookup_timeout_has_lookup_stage_and_no_payload(tmp_path, caplog):
    def handler(request: httpx.Request):
        raise httpx.ConnectTimeout("simulated", request=request)

    client = BackboardClient(config(tmp_path), Database(tmp_path / "instrumented.sqlite3"),
                             httpx.MockTransport(handler))
    with caplog.at_level(logging.INFO), pytest.raises(BackboardStageError):
        asyncio.run(client.list_assistants())
    assert "assistant_lookup_started" in caplog.text
    assert "assistant_lookup_failed" in caplog.text
    assert "timeout_stage=assistant_lookup" in caplog.text
    assert "TEST_SECRET_VALUE" not in caplog.text
