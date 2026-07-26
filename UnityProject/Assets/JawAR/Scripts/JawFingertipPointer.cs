using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BMC.JawAR
{
    /// <summary>
    /// Feeds low-resolution portrait AR frames to MediaPipe and uses the index fingertip
    /// as a dwell pointer over the existing anatomy hitboxes.
    /// </summary>
    public sealed class JawFingertipPointer : MonoBehaviour
    {
        public ARCameraManager cameraManager;
        public Camera arCamera;
        public JawOpenCvArucoTracker jawTracker;
        public JawAnatomyTapController tapController;

        [Header("Hand tracking")]
        [Range(256, 768)] public int detectionLongEdge = 512;
        [Range(1f, 12f)] public float detectionsPerSecond = 6f;
        public float resultTimeoutSeconds = 0.55f;

        [Header("Pointing")]
        public JawAnatomyZone CurrentPointedZone { get; private set; }
        public JawAnatomyZone LastSelectedZone { get; private set; }
        public float LastSelectedTime { get; private set; } = float.NegativeInfinity;

        public float dwellSeconds = 0.65f;
        public float pointerAssistRadiusMeters = 0.012f;
        public float pointerSharpness = 22f;

        [Header("Surface region routing (optional experimental adapter)")]
        [Tooltip("When set, this router gets first refusal on each pointer frame before the " +
                 "legacy box raycast/dwell below runs. Leave unset to keep original behaviour.")]
        public JawSurfaceFingertipRouter surfaceRouter;

        private const string BridgeClass = "com.omar.jawaruco.JawHandLandmarkerBridge";
        private AndroidJavaClass bridge;
        private byte[] managedRgba;
        private byte[] portraitRgba;
        private float nextDetectionTime;
        private float lastNewResultTime = float.NegativeInfinity;
        private float lastSequence = -1f;
        private Vector2 smoothedScreenPoint;
        private bool hasSmoothedPoint;
        private JawAnatomyZone candidateZone;
        private JawAnatomyZone selectedZone;
        private float candidateSince;
        private Text pointerText;
        private Text handStatusText;

        private void Start()
        {
            CreateUi();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridge = new AndroidJavaClass(BridgeClass);
                if (!bridge.CallStatic<bool>("initialize"))
                {
                    SetHandStatus("HAND POINTER UNAVAILABLE");
                }
                else
                {
                    SetHandStatus("HAND POINTER READY — SHOW YOUR INDEX FINGER");
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"JAW_HAND_START_FAILED: {exception}");
                SetHandStatus("HAND POINTER UNAVAILABLE");
            }
#else
            SetHandStatus("HAND POINTER REQUIRES ANDROID");
#endif
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridge?.CallStatic("shutdown");
            }
            catch (Exception) { }
#endif
            bridge?.Dispose();
            bridge = null;
        }

        private void Update()
        {
            if (jawTracker == null || !jawTracker.WorldPoseLocked)
            {
                SetPointerVisible(false);
                SetHandStatus("LOCK JAW FIRST — THEN SHOW YOUR INDEX FINGER");
                ResetCandidate();
                surfaceRouter?.ResetCandidate();
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (bridge != null && Time.unscaledTime >= nextDetectionTime)
            {
                nextDetectionTime = Time.unscaledTime + 1f / Mathf.Max(1f, detectionsPerSecond);
                SubmitLatestFrame();
            }
            PollLatestResult();
#endif
        }

        private void SubmitLatestFrame()
        {
            if (cameraManager == null || !cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                return;
            }

            try
            {
                float scale = Mathf.Min(1f, detectionLongEdge / (float)Mathf.Max(image.width, image.height));
                var output = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(image.width * scale)),
                    Mathf.Max(1, Mathf.RoundToInt(image.height * scale)));
                var conversion = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, image.width, image.height),
                    outputDimensions = output,
                    outputFormat = TextureFormat.RGBA32,
                    transformation = XRCpuImage.Transformation.None
                };

                int byteCount = image.GetConvertedDataSize(conversion);
                using (var rgba = new NativeArray<byte>(byteCount, Allocator.Temp))
                {
                    image.Convert(conversion, rgba);
                    if (managedRgba == null || managedRgba.Length != byteCount)
                    {
                        managedRgba = new byte[byteCount];
                    }
                    rgba.CopyTo(managedRgba);

                    byte[] pixels = managedRgba;
                    int width = output.x;
                    int height = output.y;
                    if (Screen.height >= Screen.width && output.x > output.y)
                    {
                        pixels = RotateRgbaClockwise(managedRgba, output.x, output.y);
                        width = output.y;
                        height = output.x;
                    }
                    bridge.CallStatic<bool>("submitRgbaFrame", pixels, width, height);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"JAW_HAND_FRAME_FAILED: {exception}");
            }
            finally
            {
                image.Dispose();
            }
        }

        private byte[] RotateRgbaClockwise(byte[] source, int width, int height)
        {
            int count = width * height * 4;
            if (portraitRgba == null || portraitRgba.Length != count)
            {
                portraitRgba = new byte[count];
            }

            int rotatedWidth = height;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sourceOffset = (y * width + x) * 4;
                    int rotatedX = height - 1 - y;
                    int rotatedY = x;
                    int targetOffset = (rotatedY * rotatedWidth + rotatedX) * 4;
                    portraitRgba[targetOffset] = source[sourceOffset];
                    portraitRgba[targetOffset + 1] = source[sourceOffset + 1];
                    portraitRgba[targetOffset + 2] = source[sourceOffset + 2];
                    portraitRgba[targetOffset + 3] = source[sourceOffset + 3];
                }
            }
            return portraitRgba;
        }

        private void PollLatestResult()
        {
            float[] result;
            try
            {
                result = bridge.CallStatic<float[]>("getLatestFingertip");
            }
            catch (Exception exception)
            {
                Debug.LogError($"JAW_HAND_RESULT_FAILED: {exception}");
                return;
            }

            if (result == null || result.Length < 8)
            {
                if (Time.unscaledTime - lastNewResultTime > resultTimeoutSeconds)
                {
                    SetPointerVisible(false);
                    SetHandStatus("SHOW YOUR INDEX FINGER AND NAIL TO THE CAMERA");
                    ResetCandidate();
                    surfaceRouter?.ResetCandidate();
                }
                return;
            }

            if (!Mathf.Approximately(result[0], lastSequence))
            {
                lastSequence = result[0];
                lastNewResultTime = Time.unscaledTime;
            }
            if (Time.unscaledTime - lastNewResultTime > resultTimeoutSeconds)
            {
                SetPointerVisible(false);
                ResetCandidate();
                surfaceRouter?.ResetCandidate();
                return;
            }

            var detected = new Vector2(
                Mathf.Clamp01(result[1]) * Screen.width,
                (1f - Mathf.Clamp01(result[2])) * Screen.height);
            if (!hasSmoothedPoint)
            {
                smoothedScreenPoint = detected;
                hasSmoothedPoint = true;
            }
            else
            {
                float t = 1f - Mathf.Exp(-pointerSharpness * Time.unscaledDeltaTime);
                smoothedScreenPoint = Vector2.Lerp(smoothedScreenPoint, detected, t);
            }

            SetPointerVisible(true);
            pointerText.rectTransform.position = smoothedScreenPoint;

            if (surfaceRouter != null && surfaceRouter.HandlePointerFrame(smoothedScreenPoint))
            {
                ResetCandidate();
                return;
            }

            UpdatePointing(FindZone(smoothedScreenPoint));
        }

        private JawAnatomyZone FindZone(Vector2 screenPoint)
        {
            if (arCamera == null) return null;
            Ray ray = arCamera.ScreenPointToRay(screenPoint);
            JawAnatomyZone exact = ClosestZone(Physics.RaycastAll(ray, 5f));
            return exact != null
                ? exact
                : ClosestZone(Physics.SphereCastAll(ray, pointerAssistRadiusMeters, 5f));
        }

        private static JawAnatomyZone ClosestZone(RaycastHit[] hits)
        {
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            foreach (RaycastHit hit in hits)
            {
                JawAnatomyZone zone = hit.collider.GetComponentInParent<JawAnatomyZone>();
                if (zone != null) return zone;
            }
            return null;
        }

        private void UpdatePointing(JawAnatomyZone zone)
        {
            CurrentPointedZone = zone;
            if (zone == null)
            {
                pointerText.color = new Color(1f, 0.85f, 0.1f);
                SetHandStatus("HAND FOUND — MOVE YOUR NAIL OVER AN ANATOMY REGION");
                ResetCandidate();
                return;
            }

            if (zone != candidateZone)
            {
                candidateZone = zone;
                candidateSince = Time.unscaledTime;
                selectedZone = null;
            }

            float progress = Mathf.Clamp01((Time.unscaledTime - candidateSince) / Mathf.Max(0.1f, dwellSeconds));
            pointerText.color = Color.Lerp(new Color(1f, 0.85f, 0.1f), new Color(0.1f, 1f, 0.35f), progress);
            SetHandStatus($"POINTING: {zone.DisplayName} — HOLD {Mathf.CeilToInt((1f - progress) * dwellSeconds * 10f) / 10f:F1}s");

            if (progress >= 1f && selectedZone != zone)
            {
                selectedZone = zone;
                LastSelectedZone = zone;
                LastSelectedTime = Time.unscaledTime;
                tapController?.SelectZone(zone, "FINGERNAIL_POINTER");
                pointerText.color = new Color(0.1f, 1f, 0.35f);
                SetHandStatus($"SELECTED WITH FINGER: {zone.DisplayName}");
            }
        }

        private void ResetCandidate()
        {
            CurrentPointedZone = null;
            candidateZone = null;
            selectedZone = null;
            candidateSince = 0f;
        }

        private void CreateUi()
        {
            Canvas canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) return;

            pointerText = CreateText(canvas.transform, "Fingertip Pointer", "+", 68,
                new Color(1f, 0.85f, 0.1f), Vector2.zero, new Vector2(110f, 110f),
                new Vector2(0.5f, 0.5f));
            pointerText.fontStyle = FontStyle.Bold;
            pointerText.gameObject.SetActive(false);

            handStatusText = CreateText(canvas.transform, "Fingertip Status",
                "HAND POINTER STARTING", 29, new Color(1f, 0.9f, 0.25f),
                new Vector2(0f, -165f), new Vector2(980f, 100f), new Vector2(0.5f, 1f));
        }

        private static Text CreateText(Transform parent, string name, string value, int size,
            Color color, Vector2 anchoredPosition, Vector2 dimensions, Vector2 anchor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.text = value;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = dimensions;
            return text;
        }

        private void SetPointerVisible(bool visible)
        {
            if (pointerText != null && pointerText.gameObject.activeSelf != visible)
            {
                pointerText.gameObject.SetActive(visible);
            }
        }

        private void SetHandStatus(string message)
        {
            if (handStatusText != null && handStatusText.text != message)
            {
                handStatusText.text = message;
            }
        }

        /// <summary>Lets an owning JawSurfaceFingertipRouter drive the same status line this pointer displays.</summary>
        public void SetHandStatusExternal(string message) => SetHandStatus(message);

        /// <summary>Lets an owning JawSurfaceFingertipRouter drive the same pointer dot colour this pointer displays.</summary>
        public void SetPointerColorExternal(Color color)
        {
            if (pointerText != null) pointerText.color = color;
        }
    }
}
