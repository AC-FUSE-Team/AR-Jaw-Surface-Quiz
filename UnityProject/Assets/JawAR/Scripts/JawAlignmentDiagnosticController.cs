using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BMC.JawAR
{
    /// <summary>
    /// Diagnostic-build-only marker observer. It samples at 3 Hz after lock and never changes the jaw pose.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JawAlignmentDiagnosticController : MonoBehaviour
    {
        public JawOpenCvArucoTracker tracker;
        public string configurationLabel = "quiz";
        [Range(2f, 4f)] public float samplesPerSecond = 3f;

        private const string BridgeClass = "com.omar.jawaruco.JawArucoBridge";
        private const int HistoryCapacity = 180;
        private readonly List<FramePose> frameHistory = new(HistoryCapacity);
        private readonly List<string> rows = new(2048);
        private AndroidJavaClass bridge;
        private ARSession session;
        private Text status;
        private double nextSample;
        private Pose initialLockedPose;
        private bool capturedInitialLock;
        private string lastObservation = "No post-lock marker observation yet.";
        private string liveLogPath;
        private byte[] managedGray;
        private byte[] portraitGray;

        private readonly struct FramePose
        {
            public readonly double timestamp;
            public readonly Pose cameraPose;
            public FramePose(double timestamp, Pose cameraPose)
            {
                this.timestamp = timestamp;
                this.cameraPose = cameraPose;
            }
        }

        private void Awake()
        {
            if (tracker == null) tracker = FindFirstObjectByType<JawOpenCvArucoTracker>();
            session = FindFirstObjectByType<ARSession>();
            BuildInterface();
            string directory = Path.Combine(Application.persistentDataPath, "JawAlignmentDiagnostics");
            Directory.CreateDirectory(directory);
            liveLogPath = Path.Combine(directory,
                $"jaw_alignment_{configurationLabel}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
            rows.Add(Header());
            Flush();
        }

        private void OnEnable()
        {
            if (tracker?.cameraManager != null) tracker.cameraManager.frameReceived += OnCameraFrame;
        }

        private void OnDisable()
        {
            if (tracker?.cameraManager != null) tracker.cameraManager.frameReceived -= OnCameraFrame;
            Flush();
        }

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridge = new AndroidJavaClass(BridgeClass);
                if (!bridge.CallStatic<bool>("initialize")) SetStatus("OpenCV diagnostic startup failed.");
            }
            catch (Exception exception)
            {
                SetStatus("OpenCV diagnostic unavailable: " + exception.Message);
            }
#endif
        }

        private void OnDestroy()
        {
            bridge?.Dispose();
            bridge = null;
        }

        private void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            if (!args.timestampNs.HasValue || tracker?.arCamera == null) return;
            frameHistory.Add(new FramePose(args.timestampNs.Value * 1e-9,
                new Pose(tracker.arCamera.transform.position, tracker.arCamera.transform.rotation)));
            if (frameHistory.Count > HistoryCapacity) frameHistory.RemoveAt(0);
        }

        private void Update()
        {
            if (tracker == null || !tracker.WorldPoseLocked || tracker.jawAnchorRoot == null) return;
            if (!capturedInitialLock)
            {
                initialLockedPose = new Pose(tracker.jawAnchorRoot.position, tracker.jawAnchorRoot.rotation);
                capturedInitialLock = true;
            }
            if (Time.realtimeSinceStartupAsDouble < nextSample) return;
            nextSample = Time.realtimeSinceStartupAsDouble + 1.0 / Mathf.Clamp(samplesPerSecond, 2f, 4f);
            ObserveMarkerWithoutCorrection();
        }

        private void ObserveMarkerWithoutCorrection()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (bridge == null || tracker.cameraManager == null || tracker.arCamera == null) return;
            if (!tracker.cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics)) return;
            if (!tracker.cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return;
            double processingStart = Time.realtimeSinceStartupAsDouble;
            try
            {
                int rawWidth = image.width;
                int rawHeight = image.height;
                float scale = Mathf.Min(1f, tracker.detectionLongEdge / (float)Mathf.Max(rawWidth, rawHeight));
                var output = new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(rawWidth * scale)),
                    Mathf.Max(1, Mathf.RoundToInt(rawHeight * scale)));
                var conversion = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, rawWidth, rawHeight),
                    outputDimensions = output,
                    outputFormat = TextureFormat.R8,
                    transformation = XRCpuImage.Transformation.None
                };
                int count = image.GetConvertedDataSize(conversion);
                using var gray = new NativeArray<byte>(count, Allocator.Temp);
                image.Convert(conversion, gray);
                if (managedGray == null || managedGray.Length != count) managedGray = new byte[count];
                gray.CopyTo(managedGray);

                float sx = output.x / (float)intrinsics.resolution.x;
                float sy = output.y / (float)intrinsics.resolution.y;
                int width = output.x;
                int height = output.y;
                double fx = intrinsics.focalLength.x * sx;
                double fy = intrinsics.focalLength.y * sy;
                double cx = intrinsics.principalPoint.x * sx;
                double cy = intrinsics.principalPoint.y * sy;
                byte[] pixels = managedGray;
                if (Screen.height >= Screen.width && output.x > output.y)
                {
                    pixels = RotateClockwise(managedGray, output.x, output.y);
                    width = output.y;
                    height = output.x;
                    double oldFx = fx;
                    double oldCx = cx;
                    fx = fy;
                    fy = oldFx;
                    cx = output.y - 1.0 - cy;
                    cy = oldCx;
                }

                float[] result = bridge.CallStatic<float[]>("detectPose", pixels, width, height,
                    fx, fy, cx, cy, (double)tracker.blackSquareSizeMeters, tracker.dictionaryMarkerId);
                double completed = Time.realtimeSinceStartupAsDouble;
                if (result == null || result.Length < 20)
                {
                    AppendRow(image.timestamp, double.NaN, (completed - processingStart) * 1000.0,
                        rawWidth, rawHeight, width, height, intrinsics, fx, fy, cx, cy, null, null, float.NaN);
                    return;
                }

                int closest = FindClosestFrame(image.timestamp);
                double matchedTimestamp = closest >= 0 ? frameHistory[closest].timestamp : double.NaN;
                Pose cameraAtImage = closest >= 0
                    ? frameHistory[closest].cameraPose
                    : new Pose(tracker.arCamera.transform.position, tracker.arCamera.transform.rotation);
                Pose observed = JawAlignmentDiagnosticMath.CameraLocalToWorld(cameraAtImage,
                    JawAlignmentDiagnosticMath.OpenCvPoseInCamera(result));
                float reprojection = JawAlignmentDiagnosticMath.ReprojectionRmsPixels(result, fx, fy, cx, cy,
                    tracker.blackSquareSizeMeters);
                AppendRow(image.timestamp, matchedTimestamp, (completed - processingStart) * 1000.0,
                    rawWidth, rawHeight, width, height, intrinsics, fx, fy, cx, cy, result, observed, reprojection);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("JAW_ALIGNMENT_DIAG_FAILED: " + exception);
            }
            finally
            {
                image.Dispose();
            }
#endif
        }

        private void AppendRow(double imageTimestamp, double frameTimestamp, double detectionLatencyMs,
            int rawWidth, int rawHeight, int width, int height, XRCameraIntrinsics intrinsics,
            double fx, double fy, double cx, double cy, float[] solve, Pose? observed, float reprojection)
        {
            Pose cameraNow = new(tracker.arCamera.transform.position, tracker.arCamera.transform.rotation);
            Pose jawWorld = new(tracker.jawAnchorRoot.position, tracker.jawAnchorRoot.rotation);
            Pose jawLocal = new(tracker.jawAnchorRoot.localPosition, tracker.jawAnchorRoot.localRotation);
            float positionDelta = observed.HasValue
                ? Vector3.Distance(initialLockedPose.position, observed.Value.position) : float.NaN;
            float angleDelta = observed.HasValue
                ? Quaternion.Angle(initialLockedPose.rotation, observed.Value.rotation) : float.NaN;
            var subsystem = session != null ? session.subsystem : null;
            string trackingState = subsystem != null ? subsystem.trackingState.ToString() : "Unavailable";
            string reason = subsystem != null ? subsystem.notTrackingReason.ToString() : "Unavailable";
            double timestampDifferenceMs = double.IsNaN(frameTimestamp)
                ? double.NaN : Math.Abs(frameTimestamp - imageTimestamp) * 1000.0;
            float tx = solve != null ? solve[0] : float.NaN;
            float ty = solve != null ? solve[1] : float.NaN;
            float tz = solve != null ? solve[2] : float.NaN;

            string row = string.Join(",", new[]
            {
                F(Time.realtimeSinceStartupAsDouble), F(imageTimestamp), F(frameTimestamp), F(timestampDifferenceMs),
                F(detectionLatencyMs), ARSession.state.ToString(), trackingState, reason,
                PoseFields(cameraNow), PoseFields(jawWorld), PoseFields(jawLocal), PoseFields(initialLockedPose),
                observed.HasValue ? PoseFields(observed.Value) : EmptyPoseFields(), F(positionDelta * 1000f), F(angleDelta),
                F(reprojection), F(tx), F(ty), F(tz), Screen.orientation.ToString(),
                rawWidth.ToString(), rawHeight.ToString(), width.ToString(), height.ToString(),
                intrinsics.resolution.x.ToString(), intrinsics.resolution.y.ToString(), F(fx), F(fy), F(cx), F(cy)
            });
            rows.Add(row);
            if (rows.Count % 6 == 0) Flush();
            lastObservation = observed.HasValue
                ? $"Marker vs lock: {positionDelta * 1000f:F1} mm / {angleDelta:F2}°\n" +
                  $"Reprojection: {reprojection:F2}px  depth: {tz:F3}m  frame match: {timestampDifferenceMs:F1}ms\n" +
                  $"AR: {trackingState} ({reason})"
                : $"Marker not visible. AR: {trackingState} ({reason})";
            SetStatus(lastObservation);
            Debug.Log("JAW_ALIGNMENT_DIAG " + row);
        }

        private int FindClosestFrame(double timestamp)
        {
            int best = -1;
            double difference = double.PositiveInfinity;
            for (int i = 0; i < frameHistory.Count; i++)
            {
                double candidate = Math.Abs(frameHistory[i].timestamp - timestamp);
                if (candidate < difference) { difference = candidate; best = i; }
            }
            return difference <= 0.1 ? best : -1;
        }

        private byte[] RotateClockwise(byte[] source, int width, int height)
        {
            int count = width * height;
            if (portraitGray == null || portraitGray.Length != count) portraitGray = new byte[count];
            int rotatedWidth = height;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                portraitGray[x * rotatedWidth + height - 1 - y] = source[y * width + x];
            return portraitGray;
        }

        private void CaptureReference(string label)
        {
            rows.Add($"#REFERENCE,{label},{F(Time.realtimeSinceStartupAsDouble)},{lastObservation.Replace(',', ';').Replace('\n', ' ')}");
            Flush();
            SetStatus(label + " reference captured.\n" + lastObservation);
        }

        private void Export()
        {
            Flush();
            SetStatus("Diagnostic log exported:\n" + liveLogPath);
        }

        private void Recalibrate()
        {
            Flush();
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        private void Flush()
        {
            if (string.IsNullOrEmpty(liveLogPath) || rows.Count == 0) return;
            File.WriteAllLines(liveLogPath, rows);
        }

        private void BuildInterface()
        {
            var root = new GameObject("Jaw Alignment Diagnostic UI");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 2220f);
            root.AddComponent<GraphicRaycaster>();
            status = Label(root.transform, "ALIGNMENT DIAGNOSTIC — observation only", 28,
                new Vector2(0.02f, 0.80f), new Vector2(0.98f, 0.98f));
            Button(root.transform, "Capture Front Reference", () => CaptureReference("FRONT"),
                new Vector2(0.02f, 0.16f), new Vector2(0.49f, 0.23f));
            Button(root.transform, "Capture Left-Side Reference", () => CaptureReference("LEFT"),
                new Vector2(0.51f, 0.16f), new Vector2(0.98f, 0.23f));
            Button(root.transform, "Capture Right-Side Reference", () => CaptureReference("RIGHT"),
                new Vector2(0.02f, 0.09f), new Vector2(0.49f, 0.15f));
            Button(root.transform, "Export Diagnostic Log", Export,
                new Vector2(0.51f, 0.09f), new Vector2(0.98f, 0.15f));
            Button(root.transform, "Recalibrate / Relock (manual restart)", Recalibrate,
                new Vector2(0.02f, 0.015f), new Vector2(0.98f, 0.08f));
        }

        private static Text Label(Transform parent, string value, int size, Vector2 min, Vector2 max)
        {
            var go = new GameObject("Diagnostic Status");
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = value;
            text.fontSize = size;
            text.color = new Color(1f, 0.78f, 0.15f);
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            return text;
        }

        private static void Button(Transform parent, string value, UnityEngine.Events.UnityAction action,
            Vector2 min, Vector2 max)
        {
            var go = new GameObject(value);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = new Color(0.04f, 0.28f, 0.38f, 0.94f);
            var button = go.AddComponent<Button>();
            button.onClick.AddListener(action);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            Label(go.transform, value, 25, Vector2.zero, Vector2.one);
        }

        private void SetStatus(string value) { if (status != null) status.text = value; }
        private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string PoseFields(Pose pose) => string.Join(",", new[]
        {
            F(pose.position.x), F(pose.position.y), F(pose.position.z),
            F(pose.rotation.x), F(pose.rotation.y), F(pose.rotation.z), F(pose.rotation.w)
        });
        private static string EmptyPoseFields() => ",,,,,,";
        private static string Header() =>
            "monotonic_s,cpu_image_timestamp_s,matched_ar_frame_timestamp_s,frame_timestamp_difference_ms,detection_latency_ms," +
            "ar_session_state,tracking_state,not_tracking_reason," +
            "camera_px,camera_py,camera_pz,camera_qx,camera_qy,camera_qz,camera_qw," +
            "jaw_world_px,jaw_world_py,jaw_world_pz,jaw_world_qx,jaw_world_qy,jaw_world_qz,jaw_world_qw," +
            "jaw_local_px,jaw_local_py,jaw_local_pz,jaw_local_qx,jaw_local_qy,jaw_local_qz,jaw_local_qw," +
            "locked_px,locked_py,locked_pz,locked_qx,locked_qy,locked_qz,locked_qw," +
            "observed_px,observed_py,observed_pz,observed_qx,observed_qy,observed_qz,observed_qw," +
            "position_delta_mm,angular_delta_deg,reprojection_rms_px,solvepnp_tx,solvepnp_ty,solvepnp_depth," +
            "orientation,raw_width,raw_height,detection_width,detection_height,intrinsics_width,intrinsics_height,fx,fy,cx,cy";
    }
}
