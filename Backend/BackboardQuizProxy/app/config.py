from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


def _truthy(value: str | None) -> bool:
    return (value or "").strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    database_path: Path
    mock_mode: bool
    backboard_api_key: str
    backboard_base_url: str
    quiz_proxy_token: str
    cors_origins: tuple[str, ...]
    timeout_seconds: float = 5.0
    max_attempts: int = 1

    connect_timeout_seconds: float = 10.0
    assistant_read_timeout_seconds: float = 60.0
    message_read_timeout_seconds: float = 180.0
    write_timeout_seconds: float = 30.0
    pool_timeout_seconds: float = 10.0
    max_payload_bytes: int = 32768
    rate_limit_requests: int = 60
    rate_limit_window_seconds: int = 60
    enable_teacher_ai: bool = False
    @classmethod
    def from_environment(cls) -> "Settings":
        key = os.getenv("BACKBOARD_API_KEY", "").strip()
        root = Path(__file__).resolve().parents[1]
        origins = tuple(x.strip() for x in os.getenv(
            "QUIZ_CORS_ORIGINS", "http://127.0.0.1:8765,http://localhost:8765").split(",") if x.strip())
        return cls(
            database_path=Path(os.getenv("QUIZ_PROXY_DB", root / "data" / "quiz.sqlite3")),
            mock_mode=_truthy(os.getenv("BACKBOARD_MOCK")) or not key,
            backboard_api_key=key,
            backboard_base_url="https://app.backboard.io/api",
            quiz_proxy_token=os.getenv("QUIZ_PROXY_TOKEN", "").strip(),
            cors_origins=origins,
            max_attempts=max(1, int(os.getenv("BACKBOARD_MAX_ATTEMPTS", "1"))),
            max_payload_bytes=max(4096, int(os.getenv("QUIZ_MAX_PAYLOAD_BYTES", "32768"))),
            rate_limit_requests=max(10, int(os.getenv("QUIZ_RATE_LIMIT_REQUESTS", "60"))),
            rate_limit_window_seconds=max(10, int(os.getenv("QUIZ_RATE_LIMIT_WINDOW_SECONDS", "60"))),
            enable_teacher_ai=_truthy(os.getenv("QUIZ_ENABLE_TEACHER_AI")),
        )
