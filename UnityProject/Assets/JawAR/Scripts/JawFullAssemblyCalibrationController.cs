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
    /// Diagnostic-build-only full assembly (marker + plaque + jaw) calibration overlay.
    /// Renders every layer as an independently toggleable diagnostic treatment, provides
    /// temporary translate/rotate/scale correction controls around the marker-relative CAD
    /// transform, and exports an UNVERIFIED_DIAGNOSTIC_CANDIDATE JSON for later, separate,
    /// manual review. Never writes to any production calibration file. All hierarchy
    /// (calibrationAdjustmentRoot, layer roots, jaw-only adjustment root, mesh instances) is
    /// built by JawFullPlaqueCalibrationDiagnosticBuild.cs and wired into this component's
    /// public fields -- this script only manipulates transforms/materials/UI at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JawFullAssemblyCalibrationController : MonoBehaviour
    {
        // ----- Wired by the build script -----
        public JawOpenCvArucoTracker tracker;
        public JawFingertipPointer fingertipPointer; // may be null if not present in this scene
        public Transform calibrationAdjustmentRoot;  // child of tracker.jawAnchorRoot
        public Transform markerOutlineLayerRoot;
        public Transform plaqueLayerRoot;
        public Transform jawLayerRoot;
        public Transform jawOnlyAdjustmentRoot;       // child of jawLayerRoot; expert-only
        public Transform jawMeshInstance;             // child of jawOnlyAdjustmentRoot
        public Transform completeAssemblyLayerRoot;
        public Transform axesLayerRoot;
        public Transform originMarkersLayerRoot;
        public Transform boundingBoxLayerRoot;
        public string configurationLabel = "fullplaque";

        // ----- Authoritative CAD/marker constants (see ArUco_pose_metadata.json) -----
        public const string CadMetadataSha256 = "97f29d8a4e749c11f98c902aaec604e763bfae2abad6013cffd4a36e2a1556bd";
        public const string DiagnosticBuildVersion = "JawFullPlaqueCalibrationDiagnostic_v1";
        public const string MarkerDictionary = "DICT_5X5_50";
        public const int MarkerId = 1;
        public const float MarkerBlackSquareMeters = 0.056f;
        // v35/v36's baked jaw table-contact correction (see JawArUcoSceneBuilder.InstantiateJawModel).
        public const float V35JawTableOffsetYMeters = -0.001391f;
        public const float CadJawTableOffsetYMeters = 0f;

        // Marker corners in this local space (marker plane, Y=0), CAD-derived, top-left clockwise.
        private static readonly Vector3[] MarkerCornersLocal =
        {
            new Vector3(-0.028f, 0f, 0.028f), new Vector3(0.028f, 0f, 0.028f),
            new Vector3(0.028f, 0f, -0.028f), new Vector3(-0.028f, 0f, -0.028f)
        };

        // CAD-derived local-space bounding boxes (marker origin = local zero), millimetres converted.
        private static readonly Vector3 PlaqueBoundsMin = new Vector3(-0.0587760918f, -0.006899998f, -0.0445978088f);
        private static readonly Vector3 PlaqueBoundsMax = new Vector3(0.0587760918f, 0f, 0.11283746f);
        private static readonly Vector3 AssemblyBoundsMin = new Vector3(-0.0587760918f, -0.006899998f, -0.0445978088f);
        private static readonly Vector3 AssemblyBoundsMax = new Vector3(0.0587760918f, 0.058954002f, 0.1376312f);

        private static readonly float[] TranslationIncrementsMeters = { 0.0005f, 0.001f, 0.005f };
        private static readonly float[] RotationIncrementsDegrees = { 0.1f, 0.5f, 1f };
        private static readonly float[] ScaleIncrementsFraction = { 0.001f, 0.005f, 0.01f };

        private const string BridgeClass = "com.omar.jawaruco.JawArucoBridge";

        // ----- Candidate correction (marker/plaque/jaw together) -----
        private float adjX, adjY, adjZ;
        private float adjPitch, adjYaw, adjRoll;
        private float adjScale = 1f;
        private float jawTableOffsetY = V35JawTableOffsetYMeters;
        private string jawBaselineLabel = "v35";

        // ----- Expert jaw-only correction (separate, off by default) -----
        private bool expertModeEnabled;
        private float jawOnlyX, jawOnlyY, jawOnlyZ;
        private float jawOnlyPitch, jawOnlyYaw, jawOnlyRoll;
        private float jawOnlyScale = 1f;

        private readonly List<Action> undoStack = new List<Action>(64);
        private bool frozen;
        private bool fingerProcessingEnabled;

        private sealed class Layer
        {
            public string Name;
            public Transform Root;
            public Color BaseColor = Color.white;
            public bool Visible = true;
            public float Opacity = 1f;
            public bool Wireframe;
            public bool SupportsWireframe;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<LineRenderer> WireframeLines = new List<LineRenderer>();
        }

        private readonly List<Layer> layers = new List<Layer>();
        private readonly List<JawFullAssemblyDiagnosticLog.ViewObservation> capturedViews =
            new List<JawFullAssemblyDiagnosticLog.ViewObservation>();

        private AndroidJavaClass bridge;
        private readonly List<string> poseRows = new List<string>(2048);
        private string liveLogPath;
        private string diagnosticDirectory;
        private double nextSampleTime;
        private int consecutiveAcceptedSamples;
        private byte[] managedGray;
        private byte[] portraitGray;
        private float lastFrameProcessingMs;
        private float lastReprojectionRmsPixels = float.NaN;
        private float[] lastMarkerCornersPixels;
        private JawFullAssemblyDiagnosticLog.PoseSample lastRawPose = JawFullAssemblyDiagnosticLog.PoseSample.Empty;
        private double lastFrameTime;
        private float approxFps;

        // UI
        private Text statusText;
        private GameObject layersPanel, calibratePanel, viewsPanel, logPanel;
        private readonly List<(Text label, Func<float> getter)> valueLabels = new List<(Text, Func<float>)>();

        private void Awake()
        {
            if (tracker == null) tracker = FindFirstObjectByType<JawOpenCvArucoTracker>();
            if (fingertipPointer == null) fingertipPointer = FindFirstObjectByType<JawFingertipPointer>();

            diagnosticDirectory = Path.Combine(Application.persistentDataPath, "JawFullPlaqueCalibrationDiagnostics");
            Directory.CreateDirectory(diagnosticDirectory);
            liveLogPath = Path.Combine(diagnosticDirectory,
                $"pose_log_{configurationLabel}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
            poseRows.Add(JawFullAssemblyDiagnosticLog.PoseCsvHeader());

            fingerProcessingEnabled = false;
            if (fingertipPointer != null) fingertipPointer.enabled = fingerProcessingEnabled;

            BuildLayers();
            ApplyCalibrationTransform();
            ApplyJawOnlyTransform();
            BuildUi();
            RefreshAllLayerVisuals();
        }

        private void OnDisable() => Flush();
        private void OnDestroy()
        {
            bridge?.Dispose();
            bridge = null;
        }

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridge = new AndroidJavaClass(BridgeClass);
                if (!bridge.CallStatic<bool>("initialize")) SetStatus("Diagnostic OpenCV startup failed.");
            }
            catch (Exception exception)
            {
                SetStatus("Diagnostic OpenCV unavailable: " + exception.Message);
            }
#endif
        }

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (lastFrameTime > 0) approxFps = (float)(1.0 / Math.Max(1e-6, now - lastFrameTime));
            lastFrameTime = now;

            if (tracker == null || now < nextSampleTime) return;
            nextSampleTime = now + 1.0 / 3.0; // 3 Hz independent diagnostic sampling, matches JawAlignmentDiagnosticController.
            SampleMarkerPose();
        }

        // ================= Layer construction / visuals =================

        private void BuildLayers()
        {
            layers.Add(BuildMeshLayer("Plaque", plaqueLayerRoot, new Color(0.15f, 0.55f, 1f, 0.35f), true, PlaqueBoundsMin, PlaqueBoundsMax));
            layers.Add(BuildMeshLayer("Jaw", jawLayerRoot, new Color(1f, 0.15f, 0.85f, 0.30f), false, Vector3.zero, Vector3.zero));
            layers.Add(BuildMeshLayer("Complete Assembly", completeAssemblyLayerRoot, new Color(1f, 0.45f, 0f, 0.28f), false, AssemblyBoundsMin, AssemblyBoundsMax));
            layers.Add(BuildMarkerOutlineLayer());
            layers.Add(BuildAxesLayer());
            layers.Add(BuildOriginMarkersLayer());
            layers.Add(BuildBoundingBoxLayer());

            // Default: only the marker outline visible until Stage 1 (marker) is confirmed,
            // per the guided multi-view workflow -- everything else starts hidden, not hidden-by-accident.
            foreach (var layer in layers) layer.Visible = layer.Name == "ArUco Marker Outline";
        }

        private Layer BuildMeshLayer(string name, Transform root, Color color, bool supportsWireframe,
            Vector3 boundsMin, Vector3 boundsMax)
        {
            var layer = new Layer { Name = name, Root = root, BaseColor = color, SupportsWireframe = supportsWireframe };
            if (root != null)
            {
                var material = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"));
                ConfigureTransparent(material, color);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    layer.Renderers.Add(renderer);
                }
                if (supportsWireframe)
                {
                    var wireframeRoot = new GameObject(name + "_Wireframe").transform;
                    wireframeRoot.SetParent(root, false);
                    BuildBoxWireframe(wireframeRoot, boundsMin, boundsMax, color, layer.WireframeLines);
                    wireframeRoot.gameObject.SetActive(false);
                }
            }
            return layer;
        }

        private Layer BuildMarkerOutlineLayer()
        {
            var layer = new Layer { Name = "ArUco Marker Outline", Root = markerOutlineLayerRoot,
                BaseColor = new Color(1f, 0.95f, 0.1f, 1f), SupportsWireframe = false };
            if (markerOutlineLayerRoot == null) return layer;
            var outline = CreateLineRenderer(markerOutlineLayerRoot, "Outline", layer.BaseColor, 0.0015f, loop: true);
            outline.positionCount = 4;
            for (int i = 0; i < 4; i++) outline.SetPosition(i, MarkerCornersLocal[i]);
            layer.WireframeLines.Add(outline);
            foreach (var corner in MarkerCornersLocal)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "CornerMarker";
                UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
                marker.transform.SetParent(markerOutlineLayerRoot, false);
                marker.transform.localPosition = corner;
                marker.transform.localScale = Vector3.one * 0.003f;
                var renderer = marker.GetComponent<Renderer>();
                var material = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"));
                material.color = layer.BaseColor;
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                layer.Renderers.Add(renderer);
            }
            return layer;
        }

        private Layer BuildAxesLayer()
        {
            var layer = new Layer { Name = "Coordinate Axes", Root = axesLayerRoot, SupportsWireframe = false };
            if (axesLayerRoot == null) return layer;
            const float armLength = 0.02f;
            layer.WireframeLines.Add(BuildAxisArm(axesLayerRoot, "X", Vector3.right * armLength, Color.red));
            layer.WireframeLines.Add(BuildAxisArm(axesLayerRoot, "Y", Vector3.up * armLength, Color.green));
            layer.WireframeLines.Add(BuildAxisArm(axesLayerRoot, "Z", Vector3.forward * armLength, Color.blue));
            return layer;
        }

        private LineRenderer BuildAxisArm(Transform parent, string label, Vector3 tip, Color color)
        {
            var line = CreateLineRenderer(parent, "Axis_" + label, color, 0.0012f, loop: false);
            line.positionCount = 2;
            line.SetPosition(0, Vector3.zero);
            line.SetPosition(1, tip);
            return line;
        }

        private Layer BuildOriginMarkersLayer()
        {
            var layer = new Layer { Name = "Origins (Marker + Jaw)", Root = originMarkersLayerRoot, SupportsWireframe = false };
            if (originMarkersLayerRoot == null) return layer;
            var markerOrigin = CreateOriginGizmo(originMarkersLayerRoot, "MarkerOrigin", new Color(1f, 1f, 1f));
            markerOrigin.transform.localPosition = Vector3.zero;
            layer.Renderers.Add(markerOrigin);
            if (jawLayerRoot != null)
            {
                var jawOrigin = CreateOriginGizmo(jawLayerRoot, "JawOrigin", new Color(1f, 0.5f, 0.9f));
                jawOrigin.transform.localPosition = Vector3.zero;
                layer.Renderers.Add(jawOrigin);
            }
            return layer;
        }

        private Renderer CreateOriginGizmo(Transform parent, string name, Color color)
        {
            var gizmo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            gizmo.name = name;
            UnityEngine.Object.DestroyImmediate(gizmo.GetComponent<Collider>());
            gizmo.transform.SetParent(parent, false);
            gizmo.transform.localScale = Vector3.one * 0.004f;
            var renderer = gizmo.GetComponent<Renderer>();
            var material = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"));
            material.color = color;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return renderer;
        }

        private Layer BuildBoundingBoxLayer()
        {
            var layer = new Layer { Name = "Bounding Boxes", Root = boundingBoxLayerRoot, SupportsWireframe = false };
            if (boundingBoxLayerRoot == null) return layer;
            BuildBoxWireframe(boundingBoxLayerRoot, PlaqueBoundsMin, PlaqueBoundsMax, new Color(0.3f, 0.8f, 1f), layer.WireframeLines);
            BuildBoxWireframe(boundingBoxLayerRoot, AssemblyBoundsMin, AssemblyBoundsMax, new Color(1f, 0.6f, 0.2f), layer.WireframeLines);
            return layer;
        }

        private void BuildBoxWireframe(Transform parent, Vector3 min, Vector3 max, Color color, List<LineRenderer> collector)
        {
            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z)
            };
            int[,] edges =
            {
                { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
                { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
                { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
            };
            for (int i = 0; i < edges.GetLength(0); i++)
            {
                var line = CreateLineRenderer(parent, "Edge_" + i, color, 0.001f, loop: false);
                line.positionCount = 2;
                line.SetPosition(0, corners[edges[i, 0]]);
                line.SetPosition(1, corners[edges[i, 1]]);
                collector.Add(line);
            }
        }

        private LineRenderer CreateLineRenderer(Transform parent, string name, Color color, float width, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = loop;
            line.widthMultiplier = width;
            line.numCapVertices = 2;
            var material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit"));
            material.color = color;
            line.sharedMaterial = material;
            line.startColor = color;
            line.endColor = color;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private static void ConfigureTransparent(Material material, Color color)
        {
            material.color = color;
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 3000;
        }

        private void RefreshAllLayerVisuals()
        {
            foreach (var layer in layers) ApplyLayerVisual(layer);
        }

        private void ApplyLayerVisual(Layer layer)
        {
            bool showSolid = layer.Visible && !layer.Wireframe;
            bool showWireframe = layer.Visible && layer.Wireframe && layer.SupportsWireframe;
            bool showSolidFallbackForNoWireframeSupport = layer.Visible && layer.Wireframe && !layer.SupportsWireframe;

            foreach (var renderer in layer.Renderers)
            {
                if (renderer == null) continue;
                renderer.enabled = showSolid || showSolidFallbackForNoWireframeSupport;
                if (renderer.sharedMaterial != null)
                {
                    var color = layer.BaseColor;
                    color.a = layer.BaseColor.a * layer.Opacity;
                    renderer.sharedMaterial.color = color;
                }
            }
            foreach (var line in layer.WireframeLines)
            {
                if (line == null) continue;
                bool isMeshWireframeToggle = layer.SupportsWireframe && (layer.Name == "Plaque");
                line.enabled = isMeshWireframeToggle ? showWireframe : layer.Visible;
                if (line.sharedMaterial != null)
                {
                    var color = line.startColor;
                    color.a = layer.Opacity;
                    line.startColor = color;
                    line.endColor = color;
                    line.sharedMaterial.color = color;
                }
            }
        }

        private void SetLayerVisible(string name, bool visible)
        {
            var layer = layers.Find(l => l.Name == name);
            if (layer == null) return;
            layer.Visible = visible;
            ApplyLayerVisual(layer);
        }

        private void SetLayerOpacity(string name, float opacity)
        {
            var layer = layers.Find(l => l.Name == name);
            if (layer == null) return;
            layer.Opacity = Mathf.Clamp01(opacity);
            ApplyLayerVisual(layer);
        }

        private void SetLayerWireframe(string name, bool wireframe)
        {
            var layer = layers.Find(l => l.Name == name);
            if (layer == null) return;
            layer.Wireframe = wireframe;
            ApplyLayerVisual(layer);
        }

        private void SetAllLayersVisible(bool visible)
        {
            foreach (var layer in layers) { layer.Visible = visible; ApplyLayerVisual(layer); }
        }

        // ================= Calibration transform =================

        private void ApplyCalibrationTransform()
        {
            if (calibrationAdjustmentRoot != null)
            {
                calibrationAdjustmentRoot.localPosition = new Vector3(adjX, adjY, adjZ);
                calibrationAdjustmentRoot.localRotation = Quaternion.Euler(adjPitch, adjYaw, adjRoll);
                calibrationAdjustmentRoot.localScale = Vector3.one * adjScale;
            }
            if (jawMeshInstance != null)
            {
                jawMeshInstance.localPosition = new Vector3(0f, jawTableOffsetY, 0f);
            }
            RefreshValueLabels();
        }

        private void ApplyJawOnlyTransform()
        {
            if (jawOnlyAdjustmentRoot != null)
            {
                jawOnlyAdjustmentRoot.localPosition = new Vector3(jawOnlyX, jawOnlyY, jawOnlyZ);
                jawOnlyAdjustmentRoot.localRotation = Quaternion.Euler(jawOnlyPitch, jawOnlyYaw, jawOnlyRoll);
                jawOnlyAdjustmentRoot.localScale = Vector3.one * jawOnlyScale;
            }
            RefreshValueLabels();
        }

        // Explicit per-field adjusters (delegates over struct fields aren't reliable across calls,
        // so each control path is spelled out rather than captured generically).
        private void AdjX(float d) { float p = adjX; adjX += d; undoStack.Add(() => { adjX = p; ApplyCalibrationTransform(); }); ApplyCalibrationTransform(); }
        private void AdjY(float d) { float p = adjY; adjY += d; undoStack.Add(() => { adjY = p; ApplyCalibrationTransform(); }); ApplyCalibrationTransform(); }
        private void AdjZ(float d) { float p = adjZ; adjZ += d; undoStack.Add(() => { adjZ = p; ApplyCalibrationTransform(); }); ApplyCalibrationTransform(); }
        private void AdjPitch(float d) { float p = adjPitch; adjPitch += d; undoStack.Add(() => { adjPitch = p; ApplyCalibrationTransform(); }); ApplyCalibrationTransform(); }
        private void AdjYaw(float d) { float p = adjYaw; adjYaw += d; undoStack.Add(() => { adjYaw = p; ApplyCalibrationTransform(); }); ApplyCalibrationTransform(); }
        private void AdjRoll(float d) { float p = adjRoll; adjRoll += d; undoStack.Add(() => { adjRoll = p; ApplyCalibrationTransform(); }); ApplyCalibrationTransform(); }
        private void AdjScale(float d) { float p = adjScale; adjScale = Mathf.Max(0.5f, adjScale + d); undoStack.Add(() => { adjScale = p; ApplyCalibrationTransform(); }); ApplyCalibrationTransform(); }

        private void JawOnlyAdjX(float d) { float p = jawOnlyX; jawOnlyX += d; undoStack.Add(() => { jawOnlyX = p; ApplyJawOnlyTransform(); }); ApplyJawOnlyTransform(); }
        private void JawOnlyAdjY(float d) { float p = jawOnlyY; jawOnlyY += d; undoStack.Add(() => { jawOnlyY = p; ApplyJawOnlyTransform(); }); ApplyJawOnlyTransform(); }
        private void JawOnlyAdjZ(float d) { float p = jawOnlyZ; jawOnlyZ += d; undoStack.Add(() => { jawOnlyZ = p; ApplyJawOnlyTransform(); }); ApplyJawOnlyTransform(); }
        private void JawOnlyAdjPitch(float d) { float p = jawOnlyPitch; jawOnlyPitch += d; undoStack.Add(() => { jawOnlyPitch = p; ApplyJawOnlyTransform(); }); ApplyJawOnlyTransform(); }
        private void JawOnlyAdjYaw(float d) { float p = jawOnlyYaw; jawOnlyYaw += d; undoStack.Add(() => { jawOnlyYaw = p; ApplyJawOnlyTransform(); }); ApplyJawOnlyTransform(); }
        private void JawOnlyAdjRoll(float d) { float p = jawOnlyRoll; jawOnlyRoll += d; undoStack.Add(() => { jawOnlyRoll = p; ApplyJawOnlyTransform(); }); ApplyJawOnlyTransform(); }
        private void JawOnlyAdjScale(float d) { float p = jawOnlyScale; jawOnlyScale = Mathf.Max(0.5f, jawOnlyScale + d); undoStack.Add(() => { jawOnlyScale = p; ApplyJawOnlyTransform(); }); ApplyJawOnlyTransform(); }

        private void Undo()
        {
            if (undoStack.Count == 0) { SetStatus("Nothing to undo."); return; }
            var action = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            action();
            SetStatus("Undid last adjustment.");
        }

        private void ResetToCadMetadata()
        {
            adjX = adjY = adjZ = adjPitch = adjYaw = adjRoll = 0f;
            adjScale = 1f;
            jawTableOffsetY = CadJawTableOffsetYMeters;
            jawBaselineLabel = "cad";
            undoStack.Clear();
            ApplyCalibrationTransform();
            SetStatus("Reset to raw CAD metadata (no v35 table-plane correction).");
        }

        private void ResetToV35Calibration()
        {
            adjX = adjY = adjZ = adjPitch = adjYaw = adjRoll = 0f;
            adjScale = 1f;
            jawTableOffsetY = V35JawTableOffsetYMeters;
            jawBaselineLabel = "v35";
            undoStack.Clear();
            ApplyCalibrationTransform();
            SetStatus("Reset to current v35/v36 calibration (includes 1.391mm table correction).");
        }

        private void ResetUnsavedAdjustment() => ResetToV35Calibration();

        // ================= Freeze / resume / finger processing =================

        private void ToggleFreeze()
        {
            frozen = !frozen;
            if (tracker != null) tracker.enabled = !frozen;
            SetStatus(frozen ? "Diagnostic pose FROZEN. Move the phone freely; overlay will not update." :
                "Tracking resumed.");
        }

        private void SetFingerProcessing(bool enabled)
        {
            fingerProcessingEnabled = enabled;
            if (fingertipPointer != null) fingertipPointer.enabled = enabled;
            SetStatus("Finger processing: " + (enabled ? "ENABLED" : "DISABLED"));
        }

        // ================= Pose sampling / logging =================

        private void SampleMarkerPose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (bridge == null || tracker?.cameraManager == null || tracker.arCamera == null) return;
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
                int width = output.x, height = output.y;
                double fx = intrinsics.focalLength.x * sx, fy = intrinsics.focalLength.y * sy;
                double cx = intrinsics.principalPoint.x * sx, cy = intrinsics.principalPoint.y * sy;
                byte[] pixels = managedGray;
                if (Screen.height >= Screen.width && output.x > output.y)
                {
                    pixels = RotateClockwise(managedGray, output.x, output.y);
                    width = output.y; height = output.x;
                    double oldFx = fx, oldCx = cx;
                    fx = fy; fy = oldFx;
                    cx = output.y - 1.0 - cy; cy = oldCx;
                }

                float[] result = bridge.CallStatic<float[]>("detectPose", pixels, width, height,
                    fx, fy, cx, cy, (double)tracker.blackSquareSizeMeters, tracker.dictionaryMarkerId);
                double completed = Time.realtimeSinceStartupAsDouble;
                lastFrameProcessingMs = (float)((completed - processingStart) * 1000.0);

                if (result == null || result.Length < 20)
                {
                    consecutiveAcceptedSamples = 0;
                    AppendPoseRow(image.timestamp, null, float.NaN, null);
                    return;
                }

                var camPose = JawAlignmentDiagnosticMath.OpenCvPoseInCamera(result);
                var worldPose = JawAlignmentDiagnosticMath.CameraLocalToWorld(
                    new Pose(tracker.arCamera.transform.position, tracker.arCamera.transform.rotation), camPose);
                lastReprojectionRmsPixels = JawAlignmentDiagnosticMath.ReprojectionRmsPixels(
                    result, fx, fy, cx, cy, tracker.blackSquareSizeMeters);
                lastRawPose = new JawFullAssemblyDiagnosticLog.PoseSample(
                    worldPose.position.x, worldPose.position.y, worldPose.position.z,
                    worldPose.rotation.x, worldPose.rotation.y, worldPose.rotation.z, worldPose.rotation.w);
                lastMarkerCornersPixels = new float[8];
                for (int i = 0; i < 8; i++) lastMarkerCornersPixels[i] = result.Length > 12 + i ? result[12 + i] : float.NaN;
                consecutiveAcceptedSamples++;
                AppendPoseRow(image.timestamp, worldPose, lastReprojectionRmsPixels, lastMarkerCornersPixels);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("JAW_FULL_ASSEMBLY_DIAG_FAILED: " + exception);
            }
            finally
            {
                image.Dispose();
            }
#endif
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

        private void AppendPoseRow(double imageTimestamp, Pose? worldPose, float reprojection, float[] cornersPixels)
        {
            var subsystem = tracker != null ? FindFirstObjectByType<ARSession>()?.subsystem : null;
            string trackingState = subsystem != null ? subsystem.trackingState.ToString() : "Unavailable";
            var locked = tracker != null && tracker.jawAnchorRoot != null
                ? new JawFullAssemblyDiagnosticLog.PoseSample(
                    tracker.jawAnchorRoot.position.x, tracker.jawAnchorRoot.position.y, tracker.jawAnchorRoot.position.z,
                    tracker.jawAnchorRoot.rotation.x, tracker.jawAnchorRoot.rotation.y,
                    tracker.jawAnchorRoot.rotation.z, tracker.jawAnchorRoot.rotation.w)
                : JawFullAssemblyDiagnosticLog.PoseSample.Empty;
            var raw = worldPose.HasValue
                ? new JawFullAssemblyDiagnosticLog.PoseSample(worldPose.Value.position.x, worldPose.Value.position.y,
                    worldPose.Value.position.z, worldPose.Value.rotation.x, worldPose.Value.rotation.y,
                    worldPose.Value.rotation.z, worldPose.Value.rotation.w)
                : JawFullAssemblyDiagnosticLog.PoseSample.Empty;

            string row = JawFullAssemblyDiagnosticLog.PoseCsvRow(
                Time.realtimeSinceStartupAsDouble, imageTimestamp, Time.realtimeSinceStartupAsDouble,
                imageTimestamp, Time.realtimeSinceStartupAsDouble, 0.0, lastFrameProcessingMs,
                lastFrameProcessingMs, approxFps, ARSession.state.ToString(), trackingState,
                fingerProcessingEnabled, consecutiveAcceptedSamples, cornersPixels, reprojection, float.NaN,
                raw, raw, locked, adjX, adjY, adjZ, adjPitch, adjYaw, adjRoll, adjScale);
            poseRows.Add(row);
            if (poseRows.Count % 6 == 0) Flush();
        }

        private void Flush()
        {
            if (string.IsNullOrEmpty(liveLogPath) || poseRows.Count == 0) return;
            File.WriteAllLines(liveLogPath, poseRows);
        }

        // ================= View capture / export =================

        private void CaptureView(string label)
        {
            var visibleLayerNames = new List<string>();
            foreach (var layer in layers) if (layer.Visible) visibleLayerNames.Add(layer.Name);
            string screenshotPath = Path.Combine(diagnosticDirectory,
                $"view_{label}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
            ScreenCapture.CaptureScreenshot(screenshotPath);

            var observation = new JawFullAssemblyDiagnosticLog.ViewObservation(
                label, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), lastRawPose,
                lastReprojectionRmsPixels, lastFrameProcessingMs, string.Join("+", visibleLayerNames),
                fingerProcessingEnabled, screenshotPath, adjX, adjY, adjZ, adjPitch, adjYaw, adjRoll, adjScale);
            capturedViews.Add(observation);
            Flush();
            SetStatus($"{label} view captured ({capturedViews.Count} total). Screenshot: {screenshotPath}");
        }

        private void ExportCandidateJson()
        {
            Flush();
            string json = JawFullAssemblyDiagnosticLog.BuildCandidateJson(
                CadMetadataSha256, DiagnosticBuildVersion, MarkerDictionary, MarkerId, MarkerBlackSquareMeters,
                0f, jawTableOffsetY, jawBaselineLabel,
                adjX, adjY, adjZ, adjPitch, adjYaw, adjRoll, adjScale,
                jawOnlyX, jawOnlyY, jawOnlyZ, jawOnlyPitch, jawOnlyYaw, jawOnlyRoll, jawOnlyScale,
                fingerProcessingEnabled, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                liveLogPath, capturedViews);
            string path = Path.Combine(diagnosticDirectory, $"candidate_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, json);
            SetStatus("Candidate exported (UNVERIFIED_DIAGNOSTIC_CANDIDATE):\n" + path);
        }

        private void RestartScene()
        {
            Flush();
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        // ================= UI =================

        private void BuildUi()
        {
            var root = new GameObject("Jaw Full Plaque Calibration Diagnostic UI");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 2220f);
            root.AddComponent<GraphicRaycaster>();

            statusText = Label(root.transform, "FULL ASSEMBLY CALIBRATION DIAGNOSTIC", 24,
                new Vector2(0.02f, 0.86f), new Vector2(0.98f, 0.99f));

            Button(root.transform, "Layers", () => ShowTab("layers"), new Vector2(0.01f, 0.795f), new Vector2(0.25f, 0.855f));
            Button(root.transform, "Calibrate", () => ShowTab("calibrate"), new Vector2(0.26f, 0.795f), new Vector2(0.50f, 0.855f));
            Button(root.transform, "Views", () => ShowTab("views"), new Vector2(0.51f, 0.795f), new Vector2(0.75f, 0.855f));
            Button(root.transform, "Log", () => ShowTab("log"), new Vector2(0.76f, 0.795f), new Vector2(0.99f, 0.855f));

            layersPanel = BuildLayersPanel(root.transform);
            calibratePanel = BuildCalibratePanel(root.transform);
            viewsPanel = BuildViewsPanel(root.transform);
            logPanel = BuildLogPanel(root.transform);
            ShowTab("layers");
        }

        private void ShowTab(string tab)
        {
            layersPanel.SetActive(tab == "layers");
            calibratePanel.SetActive(tab == "calibrate");
            viewsPanel.SetActive(tab == "views");
            logPanel.SetActive(tab == "log");
        }

        private GameObject BuildLayersPanel(Transform parent)
        {
            var panel = Panel(parent, "LayersPanel");
            float y = 0.77f;
            const float rowHeight = 0.052f;
            foreach (var layer in layers)
            {
                bool visible = layer.Visible;
                Toggle(panel.transform, layer.Name, visible, v => SetLayerVisible(layer.Name, v),
                    new Vector2(0.02f, y - rowHeight), new Vector2(0.62f, y));
                if (layer.SupportsWireframe)
                {
                    Button(panel.transform, "Wire", () => SetLayerWireframe(layer.Name, !layer.Wireframe),
                        new Vector2(0.64f, y - rowHeight), new Vector2(0.80f, y));
                }
                y -= rowHeight;
            }
            Button(panel.transform, "Physical Camera Only (hide all)", () => SetAllLayersVisible(false),
                new Vector2(0.02f, 0.02f), new Vector2(0.60f, 0.075f));
            Button(panel.transform, "Show All", () => SetAllLayersVisible(true),
                new Vector2(0.62f, 0.02f), new Vector2(0.98f, 0.075f));
            return panel;
        }

        private GameObject BuildCalibratePanel(Transform parent)
        {
            var panel = Panel(parent, "CalibratePanel");
            float y = 0.77f;
            y = AxisRow(panel.transform, y, "X", d => AdjX(d), TranslationIncrementsMeters, () => adjX);
            y = AxisRow(panel.transform, y, "Y (depth)", d => AdjY(d), TranslationIncrementsMeters, () => adjY);
            y = AxisRow(panel.transform, y, "Z", d => AdjZ(d), TranslationIncrementsMeters, () => adjZ);
            y = AxisRow(panel.transform, y, "Pitch", d => AdjPitch(d), RotationIncrementsDegrees, () => adjPitch);
            y = AxisRow(panel.transform, y, "Yaw", d => AdjYaw(d), RotationIncrementsDegrees, () => adjYaw);
            y = AxisRow(panel.transform, y, "Roll", d => AdjRoll(d), RotationIncrementsDegrees, () => adjRoll);
            y = AxisRow(panel.transform, y, "Scale", d => AdjScale(d), ScaleIncrementsFraction, () => adjScale);

            Button(panel.transform, "Undo", Undo, new Vector2(0.02f, y - 0.05f), new Vector2(0.24f, y));
            Button(panel.transform, "Reset: CAD", ResetToCadMetadata, new Vector2(0.26f, y - 0.05f), new Vector2(0.50f, y));
            Button(panel.transform, "Reset: v35", ResetToV35Calibration, new Vector2(0.52f, y - 0.05f), new Vector2(0.76f, y));
            Button(panel.transform, "Reset Unsaved", ResetUnsavedAdjustment, new Vector2(0.78f, y - 0.05f), new Vector2(0.98f, y));
            y -= 0.06f;
            Toggle(panel.transform, "Expert: Jaw-Only Adjustment (separate from marker/plaque)", expertModeEnabled,
                v => { expertModeEnabled = v; RebuildCalibratePanelExpertRows(); }, new Vector2(0.02f, y - 0.05f), new Vector2(0.98f, y));
            return panel;
        }

        private void RebuildCalibratePanelExpertRows()
        {
            // Simplicity over polish for a diagnostic tool: expert rows are toggled via
            // the jaw-only adjuster methods regardless of panel visibility; when disabled the
            // jaw-only transform is simply reset to identity so it can't silently linger.
            if (!expertModeEnabled)
            {
                jawOnlyX = jawOnlyY = jawOnlyZ = jawOnlyPitch = jawOnlyYaw = jawOnlyRoll = 0f;
                jawOnlyScale = 1f;
                ApplyJawOnlyTransform();
            }
        }

        private float AxisRow(Transform parent, float y, string label, Action<float> apply, float[] increments,
            Func<float> currentValue)
        {
            const float rowHeight = 0.075f;
            Label(parent, label, 20, new Vector2(0.02f, y - rowHeight), new Vector2(0.20f, y));
            var value = Label(parent, "0", 20, new Vector2(0.20f, y - rowHeight), new Vector2(0.36f, y));
            valueLabels.Add((value, currentValue));
            bool isTranslation = ReferenceEquals(increments, TranslationIncrementsMeters);
            bool isScale = ReferenceEquals(increments, ScaleIncrementsFraction);
            float[] widths = { 0.10f, 0.09f, 0.08f };
            float x = 0.36f;
            for (int i = 0; i < increments.Length; i++)
            {
                float inc = increments[i];
                Button(parent, "-" + IncrementLabel(inc, isTranslation, isScale), () => { apply(-inc); value.text = FormatValue(currentValue()); },
                    new Vector2(x, y - rowHeight), new Vector2(x + widths[i], y));
                x += widths[i];
            }
            for (int i = increments.Length - 1; i >= 0; i--)
            {
                float inc = increments[i];
                Button(parent, "+" + IncrementLabel(inc, isTranslation, isScale), () => { apply(inc); value.text = FormatValue(currentValue()); },
                    new Vector2(x, y - rowHeight), new Vector2(x + widths[i], y));
                x += widths[i];
            }
            value.text = FormatValue(currentValue());
            return y - rowHeight - 0.005f;
        }

        private static string IncrementLabel(float value, bool isTranslationMeters, bool isScaleFraction)
        {
            if (isTranslationMeters) return (value * 1000f).ToString("0.#", CultureInfo.InvariantCulture) + "mm";
            if (isScaleFraction) return (value * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";
            return value.ToString("0.#", CultureInfo.InvariantCulture) + "°";
        }

        private static string FormatValue(float value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

        private void RefreshValueLabels()
        {
            // Button callbacks update their own label directly; this hook exists so Reset/Undo
            // (which change several fields at once, outside any single button click) redraw all
            // of them so the displayed numbers never go stale.
            foreach (var (label, getter) in valueLabels)
            {
                if (label != null) label.text = FormatValue(getter());
            }
        }

        private GameObject BuildViewsPanel(Transform parent)
        {
            var panel = Panel(parent, "ViewsPanel");
            Button(panel.transform, "Capture FRONT", () => CaptureView("FRONT"), new Vector2(0.02f, 0.68f), new Vector2(0.49f, 0.75f));
            Button(panel.transform, "Capture LEFT", () => CaptureView("LEFT"), new Vector2(0.51f, 0.68f), new Vector2(0.98f, 0.75f));
            Button(panel.transform, "Capture RIGHT", () => CaptureView("RIGHT"), new Vector2(0.02f, 0.60f), new Vector2(0.49f, 0.67f));
            Button(panel.transform, "Capture ELEVATED", () => CaptureView("ELEVATED"), new Vector2(0.51f, 0.60f), new Vector2(0.98f, 0.67f));
            Button(panel.transform, "Pause / Freeze Pose", ToggleFreeze, new Vector2(0.02f, 0.50f), new Vector2(0.49f, 0.57f));
            Button(panel.transform, "Resume Tracking", () => { if (frozen) ToggleFreeze(); }, new Vector2(0.51f, 0.50f), new Vector2(0.98f, 0.57f));
            Toggle(panel.transform, "Finger Processing (start disabled)", fingerProcessingEnabled,
                SetFingerProcessing, new Vector2(0.02f, 0.40f), new Vector2(0.98f, 0.47f));
            Button(panel.transform, "Save Diagnostic Candidate", () => SetStatus($"Candidate state noted ({capturedViews.Count} views captured so far). Use Export Candidate JSON to write it."),
                new Vector2(0.02f, 0.30f), new Vector2(0.98f, 0.37f));
            Button(panel.transform, "Export Candidate JSON", ExportCandidateJson, new Vector2(0.02f, 0.20f), new Vector2(0.98f, 0.27f));
            Button(panel.transform, "Recalibrate / Relock (restart)", RestartScene, new Vector2(0.02f, 0.02f), new Vector2(0.98f, 0.09f));
            return panel;
        }

        private GameObject BuildLogPanel(Transform parent)
        {
            var panel = Panel(parent, "LogPanel");
            Label(panel.transform, "Pose/timestamp CSV log path:", 20, new Vector2(0.02f, 0.70f), new Vector2(0.98f, 0.78f));
            Label(panel.transform, "(see status area for the live path once diagnostics start writing)", 18,
                new Vector2(0.02f, 0.60f), new Vector2(0.98f, 0.70f));
            Button(panel.transform, "Flush Log Now", () => { Flush(); SetStatus("Flushed: " + liveLogPath); },
                new Vector2(0.02f, 0.45f), new Vector2(0.98f, 0.55f));
            return panel;
        }

        private GameObject Panel(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0.79f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return go;
        }

        private static Text Label(Transform parent, string value, int size, Vector2 min, Vector2 max)
        {
            var go = new GameObject("Label_" + value);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = value;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            return text;
        }

        private static void Button(Transform parent, string value, UnityEngine.Events.UnityAction action,
            Vector2 min, Vector2 max)
        {
            var go = new GameObject("Button_" + value);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = new Color(0.04f, 0.28f, 0.38f, 0.94f);
            var button = go.AddComponent<Button>();
            button.onClick.AddListener(action);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            Label(go.transform, value, 15, Vector2.zero, Vector2.one);
        }

        private static void Toggle(Transform parent, string label, bool initial, Action<bool> onChanged,
            Vector2 min, Vector2 max)
        {
            var go = new GameObject("Toggle_" + label);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            var background = go.AddComponent<Image>();
            background.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
            var toggle = go.AddComponent<Toggle>();

            var checkmarkGo = new GameObject("Checkmark");
            checkmarkGo.transform.SetParent(go.transform, false);
            var checkImage = checkmarkGo.AddComponent<Image>();
            checkImage.color = new Color(0.15f, 0.95f, 0.4f, 1f);
            var checkRect = checkmarkGo.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0f, 0f); checkRect.anchorMax = new Vector2(0.08f, 1f);
            checkRect.offsetMin = Vector2.zero; checkRect.offsetMax = Vector2.zero;

            toggle.targetGraphic = background;
            toggle.graphic = checkImage;
            toggle.isOn = initial;
            toggle.onValueChanged.AddListener(new UnityEngine.Events.UnityAction<bool>(onChanged));

            Label(go.transform, label, 16, new Vector2(0.10f, 0f), new Vector2(1f, 1f));
        }

        private void SetStatus(string value) { if (statusText != null) statusText.text = value; }
    }
}
