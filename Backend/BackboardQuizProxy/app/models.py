from __future__ import annotations

from datetime import datetime
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, field_validator

STUDENT_PATTERN = r"^student_[a-z0-9]{3,32}(?:_[a-z0-9]{1,32})*$"
ID_PATTERN = r"^[A-Za-z0-9_.:-]{1,96}$"


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class StudentRequest(StrictModel):
    studentId: str = Field(pattern=STUDENT_PATTERN)


class AttemptRequest(StrictModel):
    eventId: UUID
    studentId: str = Field(pattern=STUDENT_PATTERN)
    sessionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)
    questionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)
    objectId: Literal["jaw"]
    regionMapVersion: str = Field(min_length=1, max_length=160)
    expectedStableRegionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)
    selectedStableRegionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)
    correct: bool
    responseTimeSeconds: float = Field(ge=0, le=3600)
    attemptNumber: int = Field(ge=1, le=20)
    hintLevel: int = Field(ge=0, le=10)
    utcTimestamp: datetime
    synchronizationState: Literal["pending", "synced", "failed"] = "pending"
    backboardResponseReference: str = Field(default="", max_length=160)


class HintRequest(StrictModel):
    studentId: str = Field(pattern=STUDENT_PATTERN)
    sessionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)
    questionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)
    expectedStableRegionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)
    selectedStableRegionId: str = Field(default="Unknown", min_length=1, max_length=96, pattern=ID_PATTERN)
    correct: bool = False
    responseTimeSeconds: float = Field(default=0, ge=0, le=3600)
    attemptNumber: int = Field(default=1, ge=1, le=20)
    hintLevel: int = Field(default=1, ge=0, le=10)


class LearningEventRequest(StrictModel):
    eventId: UUID
    studentId: str = Field(pattern=STUDENT_PATTERN)


class LearningEventResponse(StrictModel):
    accepted: bool
    duplicate: bool = False
    memoryAction: Literal["Auto", "off"]
    reason: str = Field(max_length=96)
    source: Literal["mock", "backboard", "local-fallback", "local-policy"]
    text: str = Field(default="", max_length=500)
    responseReference: str = Field(default="", max_length=160)


class SessionSummaryRequest(StrictModel):
    studentId: str = Field(pattern=STUDENT_PATTERN)
    sessionId: str = Field(min_length=1, max_length=96, pattern=ID_PATTERN)


class AttemptResponse(StrictModel):
    accepted: bool
    duplicate: bool
    eventId: UUID
    queuedForBackboard: bool


class HintResponse(StrictModel):
    text: str = Field(max_length=500)
    source: Literal["mock", "backboard", "local-fallback"]
    responseReference: str = ""
    suggestedReviewRegions: list[str] = []
    usage: dict[str, int] = Field(default_factory=dict)


def bounded_text(value: str, limit: int = 500) -> str:
    cleaned = " ".join((value or "").split())
    return cleaned[:limit]
