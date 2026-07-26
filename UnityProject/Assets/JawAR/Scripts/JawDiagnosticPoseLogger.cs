using UnityEngine;

namespace BMC.JawAR
{
    /// <summary>
    /// Bounded (~2 Hz) diagnostic trace shared by the quiz's and the working app's diagnostic
    /// builds: once the marker is locked, logs the jaw anchor's world pose (to confirm it stays
    /// frozen) alongside the live AR camera pose (to see how it moves), so the two apps'
    /// calibration behaviour can be compared side by side from equivalent physical test runs.
    /// Not attached to either app's persisted scene -- only to duplicate diagnostic-only builds.
    /// </summary>
    public sealed class JawDiagnosticPoseLogger : MonoBehaviour
    {
        public JawOpenCvArucoTracker jawTracker;
        [Range(0.1f, 2f)] public float logIntervalSeconds = 0.5f;

        private float nextLogTime;
        private bool baselineCaptured;
        private Vector3 jawBaselinePos;
        private Vector3 cameraBaselinePos;

        private void Update()
        {
            if (jawTracker == null || !jawTracker.WorldPoseLocked ||
                jawTracker.jawAnchorRoot == null || jawTracker.arCamera == null) return;
            if (Time.unscaledTime < nextLogTime) return;
            nextLogTime = Time.unscaledTime + logIntervalSeconds;

            var jaw = jawTracker.jawAnchorRoot;
            var cam = jawTracker.arCamera.transform;
            if (!baselineCaptured)
            {
                jawBaselinePos = jaw.position;
                cameraBaselinePos = cam.position;
                baselineCaptured = true;
            }

            var jawDriftMeters = Vector3.Distance(jaw.position, jawBaselinePos);
            var camMovedMeters = Vector3.Distance(cam.position, cameraBaselinePos);
            var jawEuler = jaw.rotation.eulerAngles;
            var camEuler = cam.rotation.eulerAngles;
            Debug.Log($"JAW_POSE_DIAG t={Time.unscaledTime:F2} " +
                      $"jawPos=({jaw.position.x:F4},{jaw.position.y:F4},{jaw.position.z:F4}) " +
                      $"jawRotEuler=({jawEuler.x:F1},{jawEuler.y:F1},{jawEuler.z:F1}) " +
                      $"jawDriftSinceLockMeters={jawDriftMeters:F5} " +
                      $"camPos=({cam.position.x:F4},{cam.position.y:F4},{cam.position.z:F4}) " +
                      $"camRotEuler=({camEuler.x:F1},{camEuler.y:F1},{camEuler.z:F1}) " +
                      $"camMovedSinceBaselineMeters={camMovedMeters:F4} " +
                      $"screen={Screen.width}x{Screen.height} orientation={Screen.orientation}");
        }
    }
}
