using System;
using System.Collections.Generic;
using System.Linq;

namespace BMC.JawAR.Quiz.Learning
{
    public readonly struct JawQuizMemoryDecision
    {
        public readonly bool ShouldWrite;
        public readonly string Reason;
        public readonly string PolicyKey;
        public JawQuizMemoryDecision(bool shouldWrite, string reason, string policyKey)
        { ShouldWrite = shouldWrite; Reason = reason; PolicyKey = policyKey; }
    }

    /// <summary>Pure local policy mirrored by the proxy against authoritative SQLite rows.</summary>
    public static class JawQuizMemoryPolicy
    {
        public const int RepeatedErrorCount = 2;
        public const int RecurringPairCount = 2;
        public const int RepeatedHintCount = 2;
        public const int WeakRegionMinimumAttempts = 3;
        public const float WeakRegionMaximumAccuracy = 0.5f;

        public static JawQuizMemoryDecision Evaluate(JawQuizAttemptRecord attempt,
            IEnumerable<JawQuizAttemptRecord> history)
        {
            if (attempt == null || attempt.correct)
                return new JawQuizMemoryDecision(false, "ordinary_correct_answer", string.Empty);
            var relevant = (history ?? Array.Empty<JawQuizAttemptRecord>())
                .Where(item => item != null && item.studentId == attempt.studentId &&
                               item.expectedStableRegionId == attempt.expectedStableRegionId).ToList();
            var incorrect = relevant.Where(item => !item.correct).ToList();
            var pairCount = incorrect.Count(item =>
                item.selectedStableRegionId == attempt.selectedStableRegionId);
            if (!string.IsNullOrEmpty(attempt.selectedStableRegionId) && pairCount >= RecurringPairCount)
                return new JawQuizMemoryDecision(true, "recurring_confusion_pair",
                    $"confusion:{attempt.expectedStableRegionId}:{attempt.selectedStableRegionId}");
            if (incorrect.Count >= RepeatedErrorCount)
                return new JawQuizMemoryDecision(true, "repeated_expected_region_error",
                    "region-error:" + attempt.expectedStableRegionId);
            if (relevant.Count(item => item.hintLevel > 0) >= RepeatedHintCount)
                return new JawQuizMemoryDecision(true, "repeated_hint_usage",
                    "region-hints:" + attempt.expectedStableRegionId);
            if (relevant.Count >= WeakRegionMinimumAttempts &&
                relevant.Count(item => item.correct) / (float)relevant.Count <= WeakRegionMaximumAccuracy)
                return new JawQuizMemoryDecision(true, "persistent_weak_region",
                    "weak-region:" + attempt.expectedStableRegionId);
            return new JawQuizMemoryDecision(false, "not_durable_yet", string.Empty);
        }
    }
}
