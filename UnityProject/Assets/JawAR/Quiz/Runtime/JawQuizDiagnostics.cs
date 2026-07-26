using UnityEngine;

namespace BMC.JawAR.Quiz
{
    /// <summary>
    /// Development-only structured selection/rejection logging. Every accepted or rejected
    /// anatomical input produces exactly one log line carrying the fields needed to reconstruct
    /// what happened without a screen recording: timestamp, mode/state, input source, pointer
    /// phase/position, pointer-over-UI and tracking-ready results, collider hit/miss, triangle
    /// index, resolved region (or "Unlabelled"), accept/reject and why, whether grading and TTS
    /// ran, the active question id, and arming/transition status.
    ///
    /// No camera image, credential, proxy token, or API key is ever logged. Logging is gated by
    /// <see cref="Enabled"/> (Editor and Development Build by default, off in a release build) and
    /// only ever runs at the point a discrete gesture completes or is rejected — never per frame —
    /// so it never allocates on every Update tick.
    /// </summary>
    public static class JawQuizDiagnostics
    {
        public enum RejectReason
        {
            None,
            Debouncing,
            PointerOverUiOrMoved,
            TrackingNotReady,
            NotAccepting,
            AwaitingFingertipRelease
        }

        /// <summary>On by default in the Editor and Development Builds; false in a release build.</summary>
        public static bool Enabled = Debug.isDebugBuild;

        // Shared context the scene/mode controllers keep current. Plain field writes only — no
        // allocation — so updating this every state transition is free.
        public static string CurrentMode = "Unknown";
        public static string CurrentModeState = "Unknown";
        public static string CurrentQuestionId = "none";
        public static bool ArmingInProgress;

        private static bool gradingInvokedThisEvent;
        private static bool ttsInvokedThisEvent;

        /// <summary>Call from a grading path while handling a selection, before <see cref="LogSelection"/> fires.</summary>
        public static void NoteGradingInvoked() => gradingInvokedThisEvent = true;

        /// <summary>Call from a TTS path while handling a selection, before <see cref="LogSelection"/> fires.</summary>
        public static void NoteTtsInvoked() => ttsInvokedThisEvent = true;

        private static void ResetPerEventFlags()
        {
            gradingInvokedThisEvent = false;
            ttsInvokedThisEvent = false;
        }

        /// <summary>
        /// Logs one accepted selection. Called by the adapter immediately before it invokes the
        /// selection events (so downstream grading/TTS calls made synchronously from those events
        /// can still be captured), and again is not needed after — callers use
        /// <see cref="NoteGradingInvoked"/>/<see cref="NoteTtsInvoked"/> during the same call stack.
        /// </summary>
        public static void LogSelection(JawQuizSurfaceSelection selection)
        {
            ResetPerEventFlags();
            if (!Enabled) return;
            var region = selection.HitKind == JawQuizSurfaceHitKind.LabelledRegion
                ? selection.StableId
                : selection.HitKind == JawQuizSurfaceHitKind.UnlabelledTriangle ? "Unlabelled" : "None";
            Debug.Log("JAW_QUIZ_SELECT " +
                      $"t={selection.Timestamp:F3} id={selection.EventId:N} " +
                      $"mode={CurrentMode} state={CurrentModeState} question={CurrentQuestionId} " +
                      $"source={selection.Source} hit={selection.HitKind} region={region} " +
                      $"triangle={selection.TriangleIndex} accepted=true " +
                      $"grading=pending tts=pending arming={ArmingInProgress}");
        }

        /// <summary>
        /// Emits the grading/TTS outcome for the most recently logged selection. Callers should
        /// invoke this once grading and any TTS call for that selection have both had a chance to
        /// run (i.e. at the end of the synchronous handling of one selection event).
        /// </summary>
        public static void LogSelectionOutcome(System.Guid eventId)
        {
            if (!Enabled) return;
            Debug.Log("JAW_QUIZ_SELECT_OUTCOME " +
                      $"id={eventId:N} grading={gradingInvokedThisEvent} tts={ttsInvokedThisEvent}");
        }

        public static void LogRejected(string source, string screenPosition, RejectReason reason)
        {
            if (!Enabled) return;
            Debug.Log("JAW_QUIZ_REJECT " +
                      $"t={Time.unscaledTime:F3} mode={CurrentMode} state={CurrentModeState} " +
                      $"question={CurrentQuestionId} source={source} pos={screenPosition} " +
                      $"accepted=false reason={reason} arming={ArmingInProgress}");
        }
    }
}
