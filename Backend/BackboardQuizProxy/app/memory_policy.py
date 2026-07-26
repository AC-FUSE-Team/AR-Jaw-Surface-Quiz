from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class MemoryDecision:
    mode: str
    reason: str
    policy_key: str

    @property
    def should_write(self) -> bool:
        return self.mode == "Auto"


class DeterministicMemoryPolicy:
    """Pure SQLite-derived policy. Backboard never decides whether an event is durable."""

    REPEATED_ERROR_COUNT = 2
    RECURRING_PAIR_COUNT = 2
    REPEATED_HINT_COUNT = 2
    WEAK_REGION_MIN_ATTEMPTS = 3
    WEAK_REGION_MAX_ACCURACY = 0.5

    @classmethod
    def evaluate(cls, attempt: dict, history: list[dict]) -> MemoryDecision:
        if bool(attempt.get("correct")):
            return MemoryDecision("off", "ordinary_correct_answer", "")

        expected = str(attempt.get("expected_region_id") or "")
        selected = str(attempt.get("selected_region_id") or "")
        relevant = [row for row in history if row.get("expected_region_id") == expected]
        incorrect = [row for row in relevant if not bool(row.get("correct"))]
        pair = [row for row in incorrect if row.get("selected_region_id") == selected]
        hinted = [row for row in relevant if int(row.get("hint_level") or 0) > 0]

        if selected and len(pair) >= cls.RECURRING_PAIR_COUNT:
            return MemoryDecision(
                "Auto", "recurring_confusion_pair",
                f"confusion:{expected}:{selected}")
        if len(incorrect) >= cls.REPEATED_ERROR_COUNT:
            return MemoryDecision("Auto", "repeated_expected_region_error", f"region-error:{expected}")
        if len(hinted) >= cls.REPEATED_HINT_COUNT:
            return MemoryDecision("Auto", "repeated_hint_usage", f"region-hints:{expected}")
        if len(relevant) >= cls.WEAK_REGION_MIN_ATTEMPTS:
            accuracy = sum(bool(row.get("correct")) for row in relevant) / len(relevant)
            if accuracy <= cls.WEAK_REGION_MAX_ACCURACY:
                return MemoryDecision("Auto", "persistent_weak_region", f"weak-region:{expected}")
        return MemoryDecision("off", "not_durable_yet", "")
