using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BMC.JawAR
{
    /// <summary>
    /// Detects the physical OpenCV DICT_5X5_50 ID 1 marker from AR camera CPU frames.
    /// A two-tap plane placement remains available before the first automatic detection.
    /// </summary>
    public sealed class JawOpenCvArucoTracker : MonoBehaviour
    {
        [Header("AR references")]
        public ARCameraManager cameraManager;
        public ARRaycastManager raycastManager;
        public Camera arCamera;
        public Transform jawAnchorRoot;
        public Text statusText;

        [Header("Printed marker")]
        public int dictionaryMarkerId = 1;
        public float blackSquareSizeMeters = 0.056f;
        [Range(320, 1280)] public int detectionLongEdge = 960;
        [Range(1f, 20f)] public float detectionsPerSecond = 8f;

        [Header("Pose behaviour")]
        public bool smoothPose = true;
        [Range(1f, 40f)] public float positionSharpness = 18f;
        [Range(1f, 40f)] public float rotationSharpness = 18f;
        public bool keepLastPoseWhenLost = true;
        public float trackingTimeoutSeconds = 0.6f;
        public bool lockWorldPoseAfterStableDetection = true;
        [Range(3, 30)] public int stableDetectionsRequired = 8;
        [Range(0f, 3f)] public float trackingSettleSeconds = 0.75f;
        [Range(0.005f, 0.1f)] public float maxSampleDeviationMeters = 0.02f;
        [Range(0.5f, 30f)] public float maxSampleAngularDeviationDegrees = 10f;
        [Range(8, 30)] public int lockSampleWindowSize = 12;
        [Range(0.001f, 0.02f)] public float maxPositionSpreadMeters = 0.0035f;
        [Range(0.25f, 5f)] public float maxRotationSpreadDegrees = 1.5f;
        [Range(1, 10)] public int stableWindowsRequired = 3;
        [Range(0.15f, 1f)] public float maxSampleGapSeconds = 0.4f;
        public bool hideJawUntilStableLock = true;

        [Header("Post-lock drift correction")]
        // The marker stays printed and visible for the whole session, but until now the tracker
        // stopped looking at it the instant WorldPoseLocked became true and simply trusted
        // ARCore's own world tracking forever after -- so any ARCore visual-inertial drift that
        // accumulated as the phone moved was never corrected.
        //
        // A first attempt corrected against single fresh detections directly (reject anything
        // bigger than ~12mm as a bad frame, else blend a little of it in). Phone testing showed
        // real drift is regularly much larger than that -- tens of centimetres -- so that
        // approach barely engaged at all and made no real difference.
        //
        // This is a windowed-consensus design instead, mirroring the pre-lock stability gate
        // below: several consecutive post-lock detections must all agree tightly with each other
        // before a correction is trusted, however large it is relative to the current pose. A
        // single bad frame (glare, occlusion, an oblique-angle solvePnP ambiguity) won't agree
        // with the next few samples and gets discarded; a real, sustained drift will, and once
        // confirmed the target pose is set to the agreed-on position -- the existing per-frame
        // smoothing still makes that a visible glide, not a snap.
        public bool correctDriftAfterLock = true;
        [Range(0.5f, 10f)] public float postLockDetectionsPerSecond = 2f;
        // How many consecutive, mutually-consistent samples are required before a correction is
        // trusted. Lower = faster to react, higher = more resistant to a lucky run of bad frames.
        [Range(3, 15)] public int postLockWindowSize = 5;
        // How tightly the confirmation window's own samples must agree with each other.
        [Range(0.001f, 0.02f)] public float postLockMaxSpreadMeters = 0.006f;
        [Range(0.25f, 5f)] public float postLockMaxSpreadDegrees = 2f;
        // A fresh sample this far from the *current* target pose restarts the confirmation
        // window at the new candidate rather than averaging it in with old, unrelated samples --
        // this is what lets a large, real drift eventually get corrected instead of being capped.
        [Range(0.01f, 0.2f)] public float postLockMaxSampleDeviationMeters = 0.04f;
        [Range(1f, 30f)] public float postLockMaxSampleAngularDeviationDegrees = 10f;
        [Range(0.3f, 3f)] public float postLockMaxSampleGapSeconds = 1.5f;

        public bool IsTracking { get; private set; }
        public bool HasEverDetectedMarker { get; private set; }
        public bool WasManuallyPlaced { get; private set; }
        public bool WorldPoseLocked { get; private set; }

        private const string BridgeClass = "com.omar.jawaruco.JawArucoBridge";
        private static readonly List<ARRaycastHit> RaycastHits = new List<ARRaycastHit>();
        private AndroidJavaClass bridge;
        private Pose targetPose;
        private bool hasTargetPose;
        private float nextDetectionTime;
        private float lastDetectionTime = float.NegativeInfinity;
        private bool awaitingJawDirectionTap;
        private Pose manualCenterPose;
        private byte[] managedGray;
        private byte[] portraitGray;
        private float continuousTrackingStartTime = -1f;
        private readonly List<Pose> lockSamples = new List<Pose>(16);
        private int consecutiveStableWindows;
        private float lastAcceptedSampleTime = float.NegativeInfinity;
        private float lastPositionSpreadMeters;
        private float lastRotationSpreadDegrees;
        private readonly List<Pose> postLockSamples = new List<Pose>(16);
        private float lastPostLockAcceptedSampleTime = float.NegativeInfinity;

        private void Awake()
        {
            ResolveReferences();
            if (jawAnchorRoot != null) jawAnchorRoot.gameObject.SetActive(false);
            SetStatus("POINT CAMERA AT THE BLACK/WHITE JAW MARKER\nOr tap marker center, then tap toward jaw");
        }

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridge = new AndroidJavaClass(BridgeClass);
                if (!bridge.CallStatic<bool>("initialize"))
                    SetStatus("ARUCO STARTUP FAILED\nUse two-tap manual placement");
            }
            catch (Exception exception)
            {
                Debug.LogError($"JAW_ARUCO_BRIDGE_START_FAILED: {exception}");
                SetStatus("ARUCO PLUGIN UNAVAILABLE\nUse two-tap manual placement");
            }
#endif
        }

        private void OnDestroy()
        {
            bridge?.Dispose();
            bridge = null;
        }

        private void Update()
        {
            ResolveReferences();
            TryManualPlacementInput();

            // Before lock: detect at the full configured rate to build the stability window.
            // After lock: keep re-checking the marker, but at a slower, cheaper rate purely for
            // drift correction (or not at all if correctDriftAfterLock is off) -- the initial
            // stability-gated lock logic below never runs again either way once locked.
            float effectiveDetectionsPerSecond = WorldPoseLocked
                ? (correctDriftAfterLock ? postLockDetectionsPerSecond : 0f)
                : detectionsPerSecond;
            if (effectiveDetectionsPerSecond > 0f && Time.unscaledTime >= nextDetectionTime)
            {
                nextDetectionTime = Time.unscaledTime + 1f / Mathf.Max(1f, effectiveDetectionsPerSecond);
                TryDetectMarker();
            }

            IsTracking = WorldPoseLocked || Time.unscaledTime - lastDetectionTime <= trackingTimeoutSeconds;
            if (!WorldPoseLocked && lockSamples.Count > 0 &&
                Time.unscaledTime - lastAcceptedSampleTime > maxSampleGapSeconds)
            {
                ClearLockSamples();
                SetStatus("MARKER INTERRUPTED — HOLD STILL AND KEEP IT VISIBLE");
            }
            if (hasTargetPose && jawAnchorRoot != null && (IsTracking || WasManuallyPlaced || keepLastPoseWhenLost))
            {
                if (!smoothPose || !jawAnchorRoot.gameObject.activeSelf)
                {
                    jawAnchorRoot.SetPositionAndRotation(targetPose.position, targetPose.rotation);
                }
                else
                {
                    float pt = 1f - Mathf.Exp(-positionSharpness * Time.unscaledDeltaTime);
                    float rt = 1f - Mathf.Exp(-rotationSharpness * Time.unscaledDeltaTime);
                    jawAnchorRoot.position = Vector3.Lerp(jawAnchorRoot.position, targetPose.position, pt);
                    jawAnchorRoot.rotation = Quaternion.Slerp(jawAnchorRoot.rotation, targetPose.rotation, rt);
                }
                jawAnchorRoot.gameObject.SetActive(true);
            }

            if (HasEverDetectedMarker && !IsTracking && !WasManuallyPlaced && !WorldPoseLocked)
                SetStatus("MARKER LOST — HOLDING LAST JAW POSE");
        }

        private void TryDetectMarker()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (bridge == null || cameraManager == null || arCamera == null) return;
            // Skip frames before ARCore has a real tracked device pose. Detecting earlier lets a
            // stale identity-rotation camera transform corrupt the stable-lock average below.
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                continuousTrackingStartTime = -1f;
                return;
            }
            // ARCore reports "Tracking" quickly but its pose keeps refining for a bit after that as
            // more visual features get incorporated. Require a short settle window of continuous
            // tracking before trusting samples for the lock average, so an early rough pose can't
            // seed (or corrupt) the lock.
            if (continuousTrackingStartTime < 0f) continuousTrackingStartTime = Time.unscaledTime;
            if (Time.unscaledTime - continuousTrackingStartTime < trackingSettleSeconds) return;
            if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics)) return;
            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage)) return;

            try
            {
                int rawWidth = cpuImage.width;
                int rawHeight = cpuImage.height;
                float scale = Mathf.Min(1f, detectionLongEdge / (float)Mathf.Max(rawWidth, rawHeight));
                var output = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(rawWidth * scale)),
                    Mathf.Max(1, Mathf.RoundToInt(rawHeight * scale)));
                var conversion = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, rawWidth, rawHeight),
                    outputDimensions = output,
                    outputFormat = TextureFormat.R8,
                    transformation = XRCpuImage.Transformation.None
                };
                int byteCount = cpuImage.GetConvertedDataSize(conversion);
                using (var gray = new NativeArray<byte>(byteCount, Allocator.Temp))
                {
                    cpuImage.Convert(conversion, gray);
                    if (managedGray == null || managedGray.Length != byteCount) managedGray = new byte[byteCount];
                    gray.CopyTo(managedGray);

                    float sx = output.x / (float)intrinsics.resolution.x;
                    float sy = output.y / (float)intrinsics.resolution.y;
                    int detectionWidth = output.x;
                    int detectionHeight = output.y;
                    double fx = intrinsics.focalLength.x * sx;
                    double fy = intrinsics.focalLength.y * sy;
                    double cx = intrinsics.principalPoint.x * sx;
                    double cy = intrinsics.principalPoint.y * sy;
                    byte[] detectionGray = managedGray;

                    // Android supplies a landscape sensor image even while Unity's AR camera is
                    // portrait. OpenCV's pose axes must match Unity's displayed camera axes.
                    if (Screen.height >= Screen.width && output.x > output.y)
                    {
                        detectionGray = RotateGrayClockwise(managedGray, output.x, output.y);
                        detectionWidth = output.y;
                        detectionHeight = output.x;
                        double oldFx = fx;
                        double oldCx = cx;
                        fx = fy;
                        fy = oldFx;
                        cx = output.y - 1.0 - cy;
                        cy = oldCx;
                    }

                    float[] pose = bridge.CallStatic<float[]>("detectPose", detectionGray,
                        detectionWidth, detectionHeight, fx, fy, cx, cy,
                        (double)blackSquareSizeMeters, dictionaryMarkerId);
                    if (pose != null && pose.Length >= 12) ApplyOpenCvPose(pose);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"JAW_ARUCO_FRAME_FAILED: {exception}");
            }
            finally
            {
                cpuImage.Dispose();
            }
#endif
        }

        private byte[] RotateGrayClockwise(byte[] source, int width, int height)
        {
            int count = width * height;
            if (portraitGray == null || portraitGray.Length != count) portraitGray = new byte[count];
            int rotatedWidth = height;
            for (int y = 0; y < height; y++)
            {
                int sourceRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    int rotatedX = height - 1 - y;
                    int rotatedY = x;
                    portraitGray[rotatedY * rotatedWidth + rotatedX] = source[sourceRow + x];
                }
            }
            return portraitGray;
        }

        // Public so Editor tests can exercise lock sampling with synthetic pose data without
        // needing the Android ArUco bridge.
        public void ApplyOpenCvPose(float[] pose)
        {
            Vector3 cameraPosition = CvVectorToUnity(pose[0], pose[1], pose[2]);
            Vector3 markerRight = CvVectorToUnity(pose[3], pose[6], pose[9]).normalized;
            Vector3 markerNormalOut = CvVectorToUnity(pose[5], pose[8], pose[11]).normalized;
            Vector3 rootForward = Vector3.Cross(markerRight, markerNormalOut).normalized;
            if (Vector3.Dot(markerNormalOut, -cameraPosition.normalized) < 0f)
            {
                markerNormalOut = -markerNormalOut;
                rootForward = -rootForward;
            }

            Quaternion cameraLocalRotation = Quaternion.LookRotation(rootForward, markerNormalOut);
            Pose detectedWorldPose = new Pose(arCamera.transform.TransformPoint(cameraPosition),
                arCamera.transform.rotation * cameraLocalRotation);
            lastDetectionTime = Time.unscaledTime;
            HasEverDetectedMarker = true;
            WasManuallyPlaced = false;

            // Once locked, the stability-gate accumulation below no longer applies -- that logic
            // exists to reach a good initial lock, not to maintain one. Route to the separate,
            // much gentler drift-correction path instead and leave lockSamples/consecutiveStable-
            // Windows untouched.
            if (WorldPoseLocked)
            {
                ApplyPostLockCorrection(detectedWorldPose);
                return;
            }

            if (!lockWorldPoseAfterStableDetection)
            {
                targetPose = detectedWorldPose;
                hasTargetPose = true;
                SetStatus("LIVE JAW TRACKING");
                return;
            }

            if (lockSamples.Count > 0 &&
                (Time.unscaledTime - lastAcceptedSampleTime > maxSampleGapSeconds ||
                 Vector3.Distance(targetPose.position, detectedWorldPose.position) > maxSampleDeviationMeters ||
                 Quaternion.Angle(targetPose.rotation, detectedWorldPose.rotation) > maxSampleAngularDeviationDegrees))
            {
                ResetLockSamples(detectedWorldPose);
                SetStatus("POSE CHANGED — HOLD PHONE STILL");
                return;
            }

            lastAcceptedSampleTime = Time.unscaledTime;
            lockSamples.Add(detectedWorldPose);
            int windowLimit = Mathf.Max(stableDetectionsRequired, lockSampleWindowSize);
            if (lockSamples.Count > windowLimit) lockSamples.RemoveAt(0);

            CalculateWindowStatistics(lockSamples, out targetPose, out lastPositionSpreadMeters,
                out lastRotationSpreadDegrees);

            int minimumSamples = Mathf.Max(3, stableDetectionsRequired);
            if (lockSamples.Count < minimumSamples)
            {
                SetStatus($"HOLD STILL — COLLECTING {lockSamples.Count}/{minimumSamples}");
                return;
            }

            bool stable = lastPositionSpreadMeters <= maxPositionSpreadMeters &&
                lastRotationSpreadDegrees <= maxRotationSpreadDegrees;
            consecutiveStableWindows = stable ? consecutiveStableWindows + 1 : 0;

            if (stable && consecutiveStableWindows >= Mathf.Max(1, stableWindowsRequired))
            {
                WorldPoseLocked = true;
                hasTargetPose = true;
                jawAnchorRoot.gameObject.SetActive(true);
                jawAnchorRoot.SetPositionAndRotation(targetPose.position, targetPose.rotation);
                Debug.Log($"JAW_WORLD_LOCK_ACCEPTED: samples={lockSamples.Count} " +
                    $"positionSpreadMm={lastPositionSpreadMeters * 1000f:F2} " +
                    $"rotationSpreadDeg={lastRotationSpreadDegrees:F2} " +
                    $"cameraWorldPosition={arCamera.transform.position}");
                SetStatus("JAW LOCKED IN PLACE — MOVE CAMERA AROUND IT");
            }
            else if (stable)
            {
                SetStatus($"GOOD — KEEP STILL {consecutiveStableWindows}/{stableWindowsRequired}\n" +
                    $"spread {lastPositionSpreadMeters * 1000f:F1} mm / {lastRotationSpreadDegrees:F1} deg");
            }
            else
            {
                SetStatus($"TOO UNSTABLE — HOLD STILL\n" +
                    $"spread {lastPositionSpreadMeters * 1000f:F1} mm / {lastRotationSpreadDegrees:F1} deg");
            }
        }

        // Public so Editor tests can exercise drift correction with synthetic pose data without
        // needing the Android ArUco bridge.
        public void ApplyPostLockCorrection(Pose detectedWorldPose)
        {
            if (!correctDriftAfterLock) return;

            if (postLockSamples.Count > 0)
            {
                // Compare against the window's own most recent sample, NOT the last confirmed
                // targetPose -- targetPose only updates once consensus is reached below, so
                // comparing against it would make a genuinely consistent run of new samples keep
                // "looking like a big jump" relative to the stale target and never accumulate.
                // Comparing against the window's own trend lets a real, sustained drift build
                // consensus, while a single bad frame still gets isolated and discarded as soon
                // as normal readings resume (it fails to agree with what comes right after it).
                Pose reference = postLockSamples[postLockSamples.Count - 1];
                bool gapTooLong = Time.unscaledTime - lastPostLockAcceptedSampleTime > postLockMaxSampleGapSeconds;
                bool tooFarFromTrend = Vector3.Distance(reference.position, detectedWorldPose.position) > postLockMaxSampleDeviationMeters ||
                    Quaternion.Angle(reference.rotation, detectedWorldPose.rotation) > postLockMaxSampleAngularDeviationDegrees;
                if (gapTooLong || tooFarFromTrend) postLockSamples.Clear();
            }

            lastPostLockAcceptedSampleTime = Time.unscaledTime;
            postLockSamples.Add(detectedWorldPose);
            if (postLockSamples.Count > postLockWindowSize) postLockSamples.RemoveAt(0);
            if (postLockSamples.Count < postLockWindowSize) return; // not enough agreement yet

            CalculateWindowStatistics(postLockSamples, out Pose meanPose, out float positionSpread,
                out float rotationSpread);
            if (positionSpread <= postLockMaxSpreadMeters && rotationSpread <= postLockMaxSpreadDegrees)
            {
                // Confirmed by consensus -- trust it. The existing per-frame position/rotation
                // smoothing (positionSharpness/rotationSharpness) still makes jawAnchorRoot glide
                // to this over a fraction of a second rather than snapping instantly.
                targetPose = meanPose;
            }
        }

        private void ResetLockSamples(Pose firstSample)
        {
            lockSamples.Clear();
            lockSamples.Add(firstSample);
            targetPose = firstSample;
            consecutiveStableWindows = 0;
            lastAcceptedSampleTime = Time.unscaledTime;
            if (hideJawUntilStableLock && jawAnchorRoot != null) jawAnchorRoot.gameObject.SetActive(false);
        }

        private void ClearLockSamples()
        {
            lockSamples.Clear();
            consecutiveStableWindows = 0;
            lastAcceptedSampleTime = float.NegativeInfinity;
            lastPositionSpreadMeters = 0f;
            lastRotationSpreadDegrees = 0f;
            if (hideJawUntilStableLock && jawAnchorRoot != null) jawAnchorRoot.gameObject.SetActive(false);
        }

        // Shared by the pre-lock stability gate (lockSamples) and the post-lock drift-correction
        // confirmation window (postLockSamples) -- same "does this run of samples agree with
        // itself" math, just applied to whichever window is asking.
        private static void CalculateWindowStatistics(List<Pose> samples, out Pose meanPose,
            out float positionSpread, out float rotationSpread)
        {
            Vector3 meanPosition = Vector3.zero;
            Quaternion meanRotation = samples[0].rotation;
            for (int i = 0; i < samples.Count; i++)
            {
                meanPosition += samples[i].position;
                if (i > 0)
                {
                    meanRotation = Quaternion.Slerp(meanRotation, samples[i].rotation, 1f / (i + 1f));
                }
            }
            meanPosition /= samples.Count;

            float positionSquareSum = 0f;
            float rotationSquareSum = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                float positionError = Vector3.Distance(samples[i].position, meanPosition);
                float rotationError = Quaternion.Angle(samples[i].rotation, meanRotation);
                positionSquareSum += positionError * positionError;
                rotationSquareSum += rotationError * rotationError;
            }

            positionSpread = Mathf.Sqrt(positionSquareSum / samples.Count);
            rotationSpread = Mathf.Sqrt(rotationSquareSum / samples.Count);
            meanPose = new Pose(meanPosition, meanRotation);
        }

        private static Vector3 CvVectorToUnity(float x, float y, float z)
        {
            return new Vector3(x, -y, z);
        }

        private void TryManualPlacementInput()
        {
            if (HasEverDetectedMarker || raycastManager == null || Input.touchCount != 1) return;
            Touch touch = Input.GetTouch(0);
            if (touch.phase != TouchPhase.Began) return;
            if (!raycastManager.Raycast(touch.position, RaycastHits, TrackableType.PlaneWithinPolygon)) return;

            Pose hit = RaycastHits[0].pose;
            if (!awaitingJawDirectionTap)
            {
                manualCenterPose = hit;
                awaitingJawDirectionTap = true;
                SetStatus("NOW TAP ON THE STAND TOWARD THE JAW");
                return;
            }

            Vector3 normal = manualCenterPose.up;
            Vector3 jawDirection = Vector3.ProjectOnPlane(hit.position - manualCenterPose.position, normal).normalized;
            if (jawDirection.sqrMagnitude < 0.5f) return;
            targetPose = new Pose(manualCenterPose.position, Quaternion.LookRotation(-jawDirection, normal));
            hasTargetPose = true;
            HasEverDetectedMarker = true;
            WasManuallyPlaced = true;
            WorldPoseLocked = true;
            awaitingJawDirectionTap = false;
            SetStatus("MANUAL JAW WORLD ANCHOR LOCKED");
        }

        private void ResolveReferences()
        {
            if (cameraManager == null) cameraManager = FindAnyObjectByType<ARCameraManager>();
            if (raycastManager == null) raycastManager = FindAnyObjectByType<ARRaycastManager>();
            if (arCamera == null) arCamera = Camera.main;
        }

        private void SetStatus(string message)
        {
            if (statusText != null && statusText.text != message) statusText.text = message;
        }
    }
}
