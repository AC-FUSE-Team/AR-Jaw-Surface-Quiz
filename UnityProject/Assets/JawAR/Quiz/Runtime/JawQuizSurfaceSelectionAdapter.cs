using System;
using BMC.JawAR.SurfaceRegions;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BMC.JawAR.Quiz
{
    public enum JawQuizSelectionSource
    {
        ScreenTap,
        PhysicalFingertip,
        Simulation
    }

    public enum JawQuizSurfaceHitKind
    {
        LabelledRegion,
        UnlabelledTriangle,
        EmptySpace
    }

    public readonly struct JawQuizSurfaceSelection
    {
        public readonly JawQuizSurfaceHitKind HitKind;
        public readonly JawQuizSelectionSource Source;
        public readonly string StableId;
        public readonly string DisplayName;
        public readonly int TriangleIndex;
        public readonly Guid EventId;
        public readonly float Timestamp;

        public JawQuizSurfaceSelection(JawQuizSurfaceHitKind hitKind, JawQuizSelectionSource source,
            string stableId, string displayName, int triangleIndex, Guid eventId, float timestamp)
        {
            HitKind = hitKind;
            Source = source;
            StableId = stableId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            TriangleIndex = triangleIndex;
            EventId = eventId;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Quiz-only input adapter. It never changes region ownership or legacy hitboxes. It is also
    /// the single authoritative router for anatomical selections: exactly one <see cref="Publish"/>
    /// call is made per input, carrying a unique event id, and the arming gate below is the only
    /// place that decides whether a selection is even attempted, so mode controllers never need to
    /// (and must not) re-implement input timing themselves.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JawQuizSurfaceSelectionAdapter : MonoBehaviour
    {
        public Camera targetCamera;
        public JawSurfaceRegionTarget surfaceTarget;
        public JawSurfaceRegionFeedback surfaceFeedback;
        public JawSurfaceFingertipRouter fingertipRouter;
        public float maxDistance = 5f;
        public bool acceptScreenInput = true;
        public bool acceptFingertipInput = true;
        [Min(2f)] public float tapMovementThresholdPixels = 24f;
        [Min(0f)] public float defaultArmingDebounceSeconds = 0.2f;

        /// <summary>Overridable clock so arming/debounce logic is deterministic in EditMode tests.</summary>
        public Func<float> TimeProvider = () => Time.unscaledTime;

        /// <summary>
        /// True once <see cref="Arm"/> has completed its debounce/release/neutral requirements.
        /// Screen taps and physical selections are only ever forwarded while this is true.
        /// </summary>
        public bool AcceptingSelections { get; set; }
        public bool BlockingOverlayOpen { get; set; }

        /// <summary>
        /// Whether the tracked jaw is locked and its collider/region-map lookup is ready. Callers
        /// (scene controller) must keep this current; while false, <see cref="Arm"/> is refused and
        /// no anatomical input is ever accepted or graded.
        /// </summary>
        public bool TrackingReady { get; set; } = true;

        public event Action<string, string, int, bool> SelectionReceived;
        public event Action<JawQuizSurfaceSelection> DetailedSelectionReceived;

        private float lastForwardedFingertipTime = float.NegativeInfinity;
        private int trackedFingerId = int.MinValue;
        private Vector2 pointerDownPosition;
        private bool pointerDownEligible;
        private bool mouseTracking;

        // Defaults to "already past debounce" so tests/tools that set AcceptingSelections directly
        // (bypassing Arm()) keep their old immediate-accept behaviour; only an explicit Arm() call
        // introduces a real debounce window.
        private float armedAt = float.NegativeInfinity;
        private bool waitingForFingertipNeutral;
        private bool waitingForTouchRelease;

        private void Update()
        {
            // Not logged here: this branch is reached on every frame the jaw is unlocked, and a
            // per-frame Debug.Log would both spam logcat and allocate every frame. Rejections are
            // only logged at the point a gesture actually completed (see SelectScreenPoint /
            // PollFingertip), which is bounded by real user interaction, not frame rate.
            if (!TrackingReady || !AcceptingSelections || BlockingOverlayOpen)
            {
                CancelPendingTap();
                return;
            }
            if (waitingForTouchRelease) PollTouchRelease();
            if (acceptFingertipInput) PollFingertip();
            if (!acceptScreenInput) return;

            if (Input.touchCount > 0)
            {
                HandleTouch(Input.GetTouch(0));
                return;
            }
            HandleMouse();
        }

        /// <summary>
        /// Marks the input system ready to accept a new deliberate selection after a state
        /// transition (Start Quiz, Next, Retry, mode switch, Player 2 Ready, Next Challenge, closing
        /// feedback, returning from the drawer, resuming the app). This is the single place that
        /// prevents a stale/held selection from the previous state being graded against the new one:
        /// it (1) requires <see cref="TrackingReady"/>, (2) discards any touch/mouse press already in
        /// flight and requires a fresh press, (3) snapshots the fingertip router's last completed
        /// dwell selection as "already seen" so it cannot be replayed, (4) if a finger is currently
        /// resting on a region, requires it to lift before a new dwell selection counts, and
        /// (5) enforces a short debounce interval before anything is graded.
        /// </summary>
        public bool Arm(float debounceSeconds = -1f)
        {
            if (!TrackingReady)
            {
                AcceptingSelections = false;
                return false;
            }
            var debounce = debounceSeconds >= 0f ? debounceSeconds : defaultArmingDebounceSeconds;
            armedAt = CurrentTime() + Mathf.Max(0f, debounce);
            CancelPendingTap();
            waitingForTouchRelease = Input.touchCount > 0 || Input.GetMouseButton(0);
            if (fingertipRouter != null)
            {
                lastForwardedFingertipTime = fingertipRouter.LastSelectedTime;
                waitingForFingertipNeutral = fingertipRouter.CurrentPointedRegion != null;
            }
            else
            {
                waitingForFingertipNeutral = false;
            }
            AcceptingSelections = true;
            return true;
        }

        /// <summary>Immediately stops accepting input (feedback/evaluation/transition periods).</summary>
        public void Disarm()
        {
            AcceptingSelections = false;
            CancelPendingTap();
        }

        public void SelectScreenPoint(Vector2 screenPoint)
        {
            if (!IsArmedAndDebounced() || surfaceTarget == null || surfaceTarget.meshCollider == null) return;
            var cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null) return;
            var ray = cameraToUse.ScreenPointToRay(screenPoint);
            if (!surfaceTarget.meshCollider.Raycast(ray, out var hit, maxDistance))
            {
                Publish(JawQuizSurfaceHitKind.EmptySpace, JawQuizSelectionSource.ScreenTap,
                    string.Empty, "Outside jaw surface", -1);
                return;
            }

            if (surfaceTarget.TryGetRegion(hit, out var region))
            {
                surfaceFeedback?.Flash(region);
                Publish(JawQuizSurfaceHitKind.LabelledRegion, JawQuizSelectionSource.ScreenTap,
                    region.StableId, region.DisplayName, hit.triangleIndex);
            }
            else
            {
                Publish(JawQuizSurfaceHitKind.UnlabelledTriangle, JawQuizSelectionSource.ScreenTap,
                    string.Empty, "Unlabelled surface", hit.triangleIndex);
            }
        }

        public bool SimulateRegionSelection(string stableRegionId)
        {
            if (!AcceptingSelections || surfaceTarget?.regionMap == null) return false;
            var region = surfaceTarget.regionMap.GetRegion(stableRegionId);
            if (region == null) return false;
            surfaceFeedback?.Flash(region);
            Publish(JawQuizSurfaceHitKind.LabelledRegion, JawQuizSelectionSource.Simulation,
                region.StableId, region.DisplayName, -1);
            return true;
        }

        public void SimulateUnlabelledSelection()
        {
            if (AcceptingSelections)
                Publish(JawQuizSurfaceHitKind.UnlabelledTriangle, JawQuizSelectionSource.Simulation,
                    string.Empty, "Unlabelled surface", -1);
        }

        public bool SimulateDetailedSelection(string stableRegionId, JawQuizSelectionSource source)
        {
            if (!AcceptingSelections || surfaceTarget?.regionMap == null) return false;
            var region = surfaceTarget.regionMap.GetRegion(stableRegionId);
            if (region == null) return false;
            Publish(JawQuizSurfaceHitKind.LabelledRegion, source,
                region.StableId, region.DisplayName, -1);
            return true;
        }

        public void SimulateEmptySpaceTap()
        {
            if (AcceptingSelections)
                Publish(JawQuizSurfaceHitKind.EmptySpace, JawQuizSelectionSource.ScreenTap,
                    string.Empty, "Outside jaw surface", -1);
        }

        private bool IsArmedAndDebounced()
        {
            if (!AcceptingSelections || BlockingOverlayOpen || !TrackingReady) return false;
            if (CurrentTime() < armedAt)
            {
                LogRejected("ScreenTap", "n/a", JawQuizDiagnostics.RejectReason.Debouncing);
                return false;
            }
            return true;
        }

        private float CurrentTime() => TimeProvider != null ? TimeProvider() : Time.unscaledTime;

        private void PollFingertip()
        {
            if (fingertipRouter == null) return;
            if (waitingForFingertipNeutral)
            {
                if (fingertipRouter.CurrentPointedRegion != null) return;
                waitingForFingertipNeutral = false;
                // Anything that completed while we were waiting for the lift (including the very
                // dwell that was resting at Arm() time) counts as "already seen" -- only a genuinely
                // fresh dwell that completes *after* this point should ever be forwarded.
                lastForwardedFingertipTime = fingertipRouter.LastSelectedTime;
                return;
            }
            if (fingertipRouter.LastSelectedRegion == null ||
                fingertipRouter.LastSelectedTime <= lastForwardedFingertipTime) return;
            if (CurrentTime() < armedAt)
            {
                // Debounce window: mark this dwell as seen so it is not replayed once the
                // debounce elapses (the student must re-dwell, not have an old dwell "catch up").
                return;
            }
            lastForwardedFingertipTime = fingertipRouter.LastSelectedTime;
            var region = fingertipRouter.LastSelectedRegion;
            Publish(JawQuizSurfaceHitKind.LabelledRegion, JawQuizSelectionSource.PhysicalFingertip,
                region.StableId, region.DisplayName, fingertipRouter.LastSelectedTriangleIndex);
        }

        private void PollTouchRelease()
        {
            var stillDown = Input.touchCount > 0 || Input.GetMouseButton(0);
            if (!stillDown) waitingForTouchRelease = false;
        }

        private void HandleTouch(Touch touch)
        {
            if (waitingForTouchRelease) return;
            if (touch.phase == TouchPhase.Began)
            {
                trackedFingerId = touch.fingerId;
                pointerDownPosition = touch.position;
                pointerDownEligible = !IsOverUi(touch.fingerId) && !BlockingOverlayOpen;
                return;
            }
            if (touch.fingerId != trackedFingerId) return;
            if (touch.phase == TouchPhase.Moved &&
                Vector2.Distance(pointerDownPosition, touch.position) > tapMovementThresholdPixels)
                pointerDownEligible = false;
            if (touch.phase != TouchPhase.Ended && touch.phase != TouchPhase.Canceled) return;
            var shouldSelect = touch.phase == TouchPhase.Ended && pointerDownEligible &&
                               !IsOverUi(touch.fingerId) && !BlockingOverlayOpen &&
                               Vector2.Distance(pointerDownPosition, touch.position) <= tapMovementThresholdPixels;
            CancelPendingTap();
            if (shouldSelect) SelectScreenPoint(touch.position);
            else if (touch.phase == TouchPhase.Ended)
                LogRejected("ScreenTap", touch.position.ToString(), JawQuizDiagnostics.RejectReason.PointerOverUiOrMoved);
        }

        private void HandleMouse()
        {
            if (waitingForTouchRelease) return;
            if (Input.GetMouseButtonDown(0))
            {
                mouseTracking = true;
                pointerDownPosition = Input.mousePosition;
                pointerDownEligible = !IsOverUi(-1) && !BlockingOverlayOpen;
            }
            if (!mouseTracking) return;
            if (Input.GetMouseButton(0) &&
                Vector2.Distance(pointerDownPosition, (Vector2)Input.mousePosition) > tapMovementThresholdPixels)
                pointerDownEligible = false;
            if (!Input.GetMouseButtonUp(0)) return;
            var release = (Vector2)Input.mousePosition;
            var shouldSelect = pointerDownEligible && !IsOverUi(-1) && !BlockingOverlayOpen &&
                               Vector2.Distance(pointerDownPosition, release) <= tapMovementThresholdPixels;
            CancelPendingTap();
            if (shouldSelect) SelectScreenPoint(release);
        }

        private void CancelPendingTap()
        {
            trackedFingerId = int.MinValue;
            pointerDownEligible = false;
            mouseTracking = false;
        }

        private void Publish(JawQuizSurfaceHitKind hitKind, JawQuizSelectionSource source,
            string stableId, string displayName, int triangleIndex)
        {
            var selection = new JawQuizSurfaceSelection(hitKind, source, stableId, displayName,
                triangleIndex, Guid.NewGuid(), CurrentTime());
            JawQuizDiagnostics.LogSelection(selection);
            DetailedSelectionReceived?.Invoke(selection);
            SelectionReceived?.Invoke(stableId, displayName, triangleIndex,
                source == JawQuizSelectionSource.Simulation);
            JawQuizDiagnostics.LogSelectionOutcome(selection.EventId);
        }

        private void LogRejected(string source, string screenPos, JawQuizDiagnostics.RejectReason reason)
        {
            JawQuizDiagnostics.LogRejected(source, screenPos, reason);
        }

        private static bool IsOverUi(int fingerId)
        {
            if (EventSystem.current == null) return false;
            return fingerId >= 0
                ? EventSystem.current.IsPointerOverGameObject(fingerId)
                : EventSystem.current.IsPointerOverGameObject();
        }
    }
}
