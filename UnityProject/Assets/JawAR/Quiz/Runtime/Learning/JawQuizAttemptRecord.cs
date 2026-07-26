using System;

namespace BMC.JawAR.Quiz.Learning
{
    public static class JawQuizSyncState
    {
        public const string Pending = "pending";
        public const string Synced = "synced";
        public const string Failed = "failed";
    }

    [Serializable]
    public sealed class JawQuizAttemptRecord
    {
        public string eventId;
        public string studentId;
        public string sessionId;
        public string questionId;
        public string objectId;
        public string regionMapVersion;
        public string expectedStableRegionId;
        public string selectedStableRegionId;
        public bool correct;
        public float responseTimeSeconds;
        public int attemptNumber;
        public int hintLevel;
        public string utcTimestamp;
        public string synchronizationState;
        public string backboardResponseReference;

        public static JawQuizAttemptRecord Create(string student, string session, string question,
            string mapVersion, string expected, string selected, bool wasCorrect, float responseSeconds,
            int attempt, int hints)
        {
            return new JawQuizAttemptRecord
            {
                eventId = Guid.NewGuid().ToString("D"),
                studentId = student ?? string.Empty,
                sessionId = session ?? string.Empty,
                questionId = question ?? string.Empty,
                objectId = "jaw",
                regionMapVersion = mapVersion ?? string.Empty,
                expectedStableRegionId = expected ?? string.Empty,
                selectedStableRegionId = selected ?? string.Empty,
                correct = wasCorrect,
                responseTimeSeconds = Math.Max(0f, responseSeconds),
                attemptNumber = Math.Max(1, attempt),
                hintLevel = Math.Max(0, hints),
                utcTimestamp = DateTime.UtcNow.ToString("O"),
                synchronizationState = JawQuizSyncState.Pending,
                backboardResponseReference = string.Empty
            };
        }
    }

    [Serializable]
    internal sealed class JawQuizSyncJournalEntry
    {
        public string eventId;
        public string state;
        public string responseReference;
        public string utcTimestamp;
    }

    [Serializable]
    internal sealed class JawQuizAttemptArray
    {
        public JawQuizAttemptRecord[] attempts;
    }
}
