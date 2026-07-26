using System;
using System.IO;
using System.Reflection;
using BMC.JawAR;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

namespace BMC.JawAR.Editor
{
    public static class JawArUcoSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/JawArUcoAnatomy_AR.unity";
        private const string ModelPath = "Assets/JawAR/Models/JawMarkerAlignedUnity.obj";
        private const string CalibrationBoardModelPath = "Assets/JawAR/Models/JawArUcoBoardCalibration.obj";
        private const string MarkerPath = "Assets/JawAR/Tracking/ArUco_DICT_5X5_50_ID1.png";
        private const string LibraryPath = "Assets/JawAR/Tracking/JawMarkerLibrary.asset";
        private const string MarkerName = "JawAruco5x5Id1";
        private const float MarkerBlackSquareMeters = 0.056f;

        // Set false once the jaw is confirmed to line up and this overlay is no longer needed;
        // it adds ~30MB (full printed board+jaw geometry) purely as a calibration aid.
        private const bool IncludeCalibrationBoardOverlay = false;

        private const string BoneMaterialPath = "Assets/JawAR/Materials/JawOverlay.mat";
        private const string ZoneMaterialPath = "Assets/JawAR/Materials/JawAnatomyZone.mat";
        private const string CalibrationBoardMaterialPath = "Assets/JawAR/Materials/JawArUcoBoardCalibration.mat";

        private struct ZoneDefinition
        {
            public string group;
            public string name;
            public string displayName;
            public string laterality;
            public string description;
            public string reference;
            public Vector3 position;
            public Vector3 size;

            public ZoneDefinition(string group, string name, string displayName, string laterality,
                string description, string reference, Vector3 position, Vector3 size)
            {
                this.group = group;
                this.name = name;
                this.displayName = displayName;
                this.laterality = laterality;
                this.description = description;
                this.reference = reference;
                this.position = position;
                this.size = size;
            }
        }

        [MenuItem("Tools/Jaw AR/Build Jaw ArUco Anatomy Scene")]
        public static void BuildScene()
        {
            Directory.CreateDirectory("Assets/JawAR/Materials");
            ConfigureModelImporter();
            ConfigureMarkerImporter();
            AssetDatabase.Refresh();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "JawArUcoAnatomy_AR";

            var sessionObject = new GameObject("AR Session");
            sessionObject.AddComponent<ARSession>();
            // Creates HandheldARInputDevice pose controls consumed by TrackedPoseDriver.
            sessionObject.AddComponent<ARInputManager>();

            var xrOrigin = CreateXROrigin(out var camera);
            var raycastManager = xrOrigin.gameObject.AddComponent<ARRaycastManager>();
            xrOrigin.gameObject.AddComponent<ARPlaneManager>();

            var jawRoot = new GameObject("JawMarkerAlignedRoot");
            jawRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            jawRoot.transform.localScale = Vector3.one;

            // The OBJ was authored marker-relative with +Z already pointing toward the physical jaw.
            var contentRoot = new GameObject("MarkerContent_Jawward");
            contentRoot.transform.SetParent(jawRoot.transform, false);
            contentRoot.transform.localRotation = Quaternion.identity;

            var mandibleRoot = new GameObject("Mandible");
            mandibleRoot.transform.SetParent(contentRoot.transform, false);
            InstantiateJawModel(mandibleRoot.transform);

            var hitboxRoot = new GameObject("AnatomyHitboxes_EDITABLE");
            hitboxRoot.transform.SetParent(contentRoot.transform, false);
            BuildAnatomyZones(hitboxRoot.transform);

            if (IncludeCalibrationBoardOverlay)
            {
                var calibrationRoot = new GameObject("CalibrationBoardOverlay_DEBUG_REMOVE_WHEN_DONE");
                calibrationRoot.transform.SetParent(contentRoot.transform, false);
                InstantiateCalibrationBoard(calibrationRoot.transform);
            }

            var ui = BuildPhoneUi();
            var tracker = xrOrigin.gameObject.AddComponent<JawOpenCvArucoTracker>();
            tracker.cameraManager = camera.GetComponent<ARCameraManager>();
            tracker.raycastManager = raycastManager;
            tracker.arCamera = camera;
            tracker.jawAnchorRoot = jawRoot.transform;
            tracker.statusText = ui.status;
            tracker.dictionaryMarkerId = 1;
            tracker.blackSquareSizeMeters = MarkerBlackSquareMeters;
            tracker.keepLastPoseWhenLost = true;
            tracker.smoothPose = true;

            var tapController = jawRoot.AddComponent<JawAnatomyTapController>();
            tapController.targetCamera = camera;
            tapController.anatomyRoot = hitboxRoot.transform;
            tapController.promptText = ui.prompt;
            tapController.feedbackText = ui.feedback;
            tapController.hitboxesVisible = false;
            tapController.tapAssistRadiusMeters = 0.012f;

            var fingertipPointer = xrOrigin.gameObject.AddComponent<JawFingertipPointer>();
            fingertipPointer.cameraManager = camera.GetComponent<ARCameraManager>();
            fingertipPointer.arCamera = camera;
            fingertipPointer.jawTracker = tracker;
            fingertipPointer.tapController = tapController;
            fingertipPointer.detectionsPerSecond = 6f;

            var voiceController = xrOrigin.gameObject.AddComponent<JawVoiceQuestionController>();
            voiceController.jawTracker = tracker;
            voiceController.fingertipPointer = fingertipPointer;
            voiceController.recentSelectionSeconds = 8f;

            EnsureEventSystem();
            ConfigureAndroid();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceSynchronousImport);
            var sceneGuid = new GUID(AssetDatabase.AssetPathToGUID(ScenePath));
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(sceneGuid, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"JAW_AR_BUILD_COMPLETE: scene={ScenePath} OpenCV=DICT_5X5_50 markerId=1 size={MarkerBlackSquareMeters}m manualFallback=twoTap");
        }

        public static void BuildSceneAndExit()
        {
            BuildScene();
            EditorApplication.Exit(0);
        }

        [MenuItem("Tools/Jaw AR/Build Android APK")]
        public static void BuildAndroidApk()
        {
            BuildScene();
            Directory.CreateDirectory("build");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "build/JawArUcoAnatomy.apk",
                target = BuildTarget.Android,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Jaw AR Android build failed: {report.summary.result}, errors={report.summary.totalErrors}");
            }
            Debug.Log($"JAW_AR_APK_BUILD_COMPLETE: path={options.locationPathName} bytes={report.summary.totalSize}");
        }

        public static void BuildAndroidApkAndExit()
        {
            BuildAndroidApk();
            EditorApplication.Exit(0);
        }

        private static XROrigin CreateXROrigin(out Camera camera)
        {
            var originObject = new GameObject("XR Origin");
            var origin = originObject.AddComponent<XROrigin>();
            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(originObject.transform, false);
            origin.CameraFloorOffsetObject = cameraOffset;
            // Handheld ARCore already reports the camera's real, absolute tracked position.
            // XROrigin's default "Device" height compensation (~1.12m) simulates a VR headset's
            // eye height above an assumed floor; applied here it lifts all world content (and the
            // locked jaw anchor) by that same amount, which read as "the jaw floats far above
            // the physical jaw." Explicitly request Device mode with zero added height.
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
            origin.CameraYOffset = 0f;

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(cameraOffset.transform, false);
            camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.nearClipPlane = 0.025f;
            camera.farClipPlane = 20f;
            cameraObject.AddComponent<ARCameraManager>();
            cameraObject.AddComponent<ARCameraBackground>();

            // ARCameraManager supplies camera frames, but it does not move the Unity Camera.
            // Match AR Foundation's official XR Origin setup so ARCore device motion updates
            // this transform and world-space jaw content remains fixed as the phone moves.
            var trackedPoseDriver = cameraObject.AddComponent<TrackedPoseDriver>();
            trackedPoseDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            trackedPoseDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            var positionAction = new InputAction("Position",
                binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
            positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");
            var rotationAction = new InputAction("Rotation",
                binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
            rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");
            trackedPoseDriver.positionInput = new InputActionProperty(positionAction);
            trackedPoseDriver.rotationInput = new InputActionProperty(rotationAction);

            origin.Camera = camera;
            return origin;
        }

        private static void InstantiateJawModel(Transform parent)
        {
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (modelAsset == null)
            {
                throw new FileNotFoundException($"Jaw model was not imported: {ModelPath}");
            }

            var instance = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException($"Could not instantiate {ModelPath}");
            }
            instance.name = "VirtualJawOverlay_MarkerAligned";
            instance.transform.SetParent(parent, false);
            // JawMarkerAlignedUnity.obj was exported from the standalone repaired jaw STL, whose
            // lowest vertex sits at model Z=38.391mm. The verified board+jaw assembly STL (used
            // for the calibration overlay) puts the true table-contact plane at model Z=37.0mm -
            // a 1.391mm gap that reads as the jaw floating slightly above the real one.
            instance.transform.localPosition = new Vector3(0f, -0.001391f, 0f);
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            var material = CreateTransparentMaterial(BoneMaterialPath,
                new Color(0.15f, 0.85f, 1f, 0.22f));
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private static void InstantiateCalibrationBoard(Transform parent)
        {
            AssetDatabase.ImportAsset(CalibrationBoardModelPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(CalibrationBoardModelPath) is ModelImporter boardImporter)
            {
                boardImporter.globalScale = 1f;
                boardImporter.useFileScale = false;
                boardImporter.importCameras = false;
                boardImporter.importLights = false;
                boardImporter.importAnimation = false;
                boardImporter.importNormals = ModelImporterNormals.Import;
                boardImporter.SaveAndReimport();
            }

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(CalibrationBoardModelPath);
            if (modelAsset == null)
            {
                throw new FileNotFoundException($"Calibration board model was not imported: {CalibrationBoardModelPath}");
            }

            var instance = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException($"Could not instantiate {CalibrationBoardModelPath}");
            }
            instance.name = "FullAssembly_MarkerAligned_CALIBRATION_ONLY";
            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // Bright orange and semi-transparent: distinct from the cyan jaw and green hitboxes,
            // and see-through enough that the real board stays visible underneath for comparison.
            var material = CreateTransparentMaterial(CalibrationBoardMaterialPath,
                new Color(1f, 0.45f, 0f, 0.35f));
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private static void BuildAnatomyZones(Transform root)
        {
            var definitions = ZoneDefinitions();
            var material = CreateTransparentMaterial(ZoneMaterialPath,
                new Color(0.05f, 1f, 0.45f, 0.30f));

            foreach (var definition in definitions)
            {
                var group = root.Find(definition.group);
                if (group == null)
                {
                    var groupObject = new GameObject(definition.group);
                    groupObject.transform.SetParent(root, false);
                    group = groupObject.transform;
                }

                var zoneObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                zoneObject.name = definition.name;
                zoneObject.transform.SetParent(group, false);
                zoneObject.transform.localPosition = definition.position;
                zoneObject.transform.localRotation = Quaternion.identity;
                zoneObject.transform.localScale = definition.size;

                var renderer = zoneObject.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                var zone = zoneObject.AddComponent<JawAnatomyZone>();
                zone.displayName = definition.displayName;
                zone.laterality = definition.laterality;
                zone.description = definition.description;
                zone.referenceImageFile = definition.reference;
                zone.approximatePlacement = true;
            }
        }

        private static ZoneDefinition[] ZoneDefinitions()
        {
            const string sideNote = "Model-side assignment is provisional because the STL is symmetric; verify anatomical left/right on the physical print.";
            return new[]
            {
                Zone("Masseter_Insertion", "SideA_NegativeX", "Masseter muscle insertion", "Model -X side",
                    "Lateral ramus and angle of the mandible. " + sideNote, "masseter muscle insertion.png",
                    new Vector3(-0.043f, 0.019f, 0.114f), new Vector3(0.024f, 0.038f, 0.045f)),
                Zone("Masseter_Insertion", "SideB_PositiveX", "Masseter muscle insertion", "Model +X side",
                    "Lateral ramus and angle of the mandible. " + sideNote, "masseter muscle insertion.png",
                    new Vector3(0.043f, 0.019f, 0.114f), new Vector3(0.024f, 0.038f, 0.045f)),

                Zone("Temporalis_Insertion", "SideA_NegativeX", "Temporalis muscle insertion", "Model -X side",
                    "Coronoid process and anterior border of the ramus. " + sideNote, "Temporalis muscle insertion.png",
                    new Vector3(-0.038f, 0.047f, 0.116f), new Vector3(0.018f, 0.029f, 0.032f)),
                Zone("Temporalis_Insertion", "SideB_PositiveX", "Temporalis muscle insertion", "Model +X side",
                    "Coronoid process and anterior border of the ramus. " + sideNote, "Temporalis muscle insertion.png",
                    new Vector3(0.038f, 0.047f, 0.116f), new Vector3(0.018f, 0.029f, 0.032f)),

                Zone("Buccinator_Origin", "SideA_NegativeX", "Buccinator muscle origin", "Model -X side",
                    "Approximate molar alveolar/oblique-line region. " + sideNote, "buccinator muscle origin.png",
                    new Vector3(-0.032f, 0.022f, 0.078f), new Vector3(0.024f, 0.020f, 0.032f)),
                Zone("Buccinator_Origin", "SideB_PositiveX", "Buccinator muscle origin", "Model +X side",
                    "Approximate molar alveolar/oblique-line region. " + sideNote, "buccinator muscle origin.png",
                    new Vector3(0.032f, 0.022f, 0.078f), new Vector3(0.024f, 0.020f, 0.032f)),

                Zone("Depressor_Anguli_Oris_Origin", "SideA_NegativeX", "Depressor anguli oris origin", "Model -X side",
                    "Oblique line on the anterior-lateral mandibular body. " + sideNote, "depressor anguli oris muscle origin.png",
                    new Vector3(-0.026f, 0.011f, 0.061f), new Vector3(0.020f, 0.018f, 0.024f)),
                Zone("Depressor_Anguli_Oris_Origin", "SideB_PositiveX", "Depressor anguli oris origin", "Model +X side",
                    "Oblique line on the anterior-lateral mandibular body. " + sideNote, "depressor anguli oris muscle origin.png",
                    new Vector3(0.026f, 0.011f, 0.061f), new Vector3(0.020f, 0.018f, 0.024f)),

                Zone("Depressor_Labii_Inferioris_Origin", "SideA_NegativeX", "Depressor labii inferioris origin", "Model -X side",
                    "Anterior mandibular body between the oblique line and midline. " + sideNote, "depressor labii inferioris.png",
                    new Vector3(-0.014f, 0.012f, 0.054f), new Vector3(0.015f, 0.017f, 0.018f)),
                Zone("Depressor_Labii_Inferioris_Origin", "SideB_PositiveX", "Depressor labii inferioris origin", "Model +X side",
                    "Anterior mandibular body between the oblique line and midline. " + sideNote, "depressor labii inferioris.png",
                    new Vector3(0.014f, 0.012f, 0.054f), new Vector3(0.015f, 0.017f, 0.018f)),

                Zone("Mentalis_Origin", "Midline", "Mentalis muscle origin", "Midline",
                    "Incisive fossa/anterior midline region of the mandible.", "mentalis muscle origin.png",
                    new Vector3(0f, 0.015f, 0.051f), new Vector3(0.018f, 0.020f, 0.017f))
            };
        }

        private static ZoneDefinition Zone(string group, string name, string displayName, string laterality,
            string description, string reference, Vector3 position, Vector3 size)
        {
            return new ZoneDefinition(group, name, displayName, laterality, description, reference, position, size);
        }

        private static (Text prompt, Text feedback, Text status) BuildPhoneUi()
        {
            var canvasObject = new GameObject("Jaw AR UI");
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 2340);
            canvasObject.AddComponent<GraphicRaycaster>();

            var prompt = CreateText(canvasObject.transform, "Prompt",
                "Tap a jaw anatomy region", 44, Color.white,
                new Vector2(0f, -90f), new Vector2(980f, 130f), new Vector2(0.5f, 1f));
            var feedback = CreateText(canvasObject.transform, "Anatomy Feedback", "", 34,
                new Color(0.2f, 1f, 0.85f), Vector2.zero, new Vector2(980f, 420f), new Vector2(0.5f, 0.5f));
            var status = CreateText(canvasObject.transform, "Marker Status",
                "POINT CAMERA AT JAW MARKER", 32, Color.white,
                new Vector2(0f, 85f), new Vector2(980f, 100f), new Vector2(0.5f, 0f));
            return (prompt, feedback, status);
        }

        private static Text CreateText(Transform parent, string name, string value, int size, Color color,
            Vector2 anchoredPosition, Vector2 dimensions, Vector2 anchor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.text = value;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            // Informational labels must not create invisible rectangles that reject anatomy taps.
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = dimensions;
            return text;
        }

        private static void EnsureEventSystem()
        {
            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
        }

        private static Material CreateTransparentMaterial(string path, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 3000;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static XRReferenceImageLibrary CreateOrUpdateReferenceLibrary()
        {
            var markerTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(MarkerPath);
            if (markerTexture == null)
            {
                throw new FileNotFoundException($"Marker texture was not imported: {MarkerPath}");
            }

            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }

            var serialized = new SerializedObject(library);
            var images = serialized.FindProperty("m_Images");
            images.arraySize = 1;
            var image = images.GetArrayElementAtIndex(0);
            SetIfPresent(image, "m_Name", MarkerName);
            SetIfPresent(image, "m_Texture", markerTexture);
            SetIfPresent(image, "m_SpecifySize", true);
            SetIfPresent(image, "m_Size", Vector2.one * MarkerBlackSquareMeters);
            SetSerializedGuid(image.FindPropertyRelative("m_SerializedGuid"), Guid.NewGuid().ToString("N"));
            SetSerializedGuid(image.FindPropertyRelative("m_SerializedTextureGuid"),
                AssetDatabase.AssetPathToGUID(MarkerPath));
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(library);
            return library;
        }

        private static void SetIfPresent(SerializedProperty parent, string name, object value)
        {
            var property = parent.FindPropertyRelative(name);
            if (property == null) return;
            switch (property.propertyType)
            {
                case SerializedPropertyType.String: property.stringValue = value?.ToString() ?? ""; break;
                case SerializedPropertyType.ObjectReference: property.objectReferenceValue = value as UnityEngine.Object; break;
                case SerializedPropertyType.Boolean: property.boolValue = value is bool flag && flag; break;
                case SerializedPropertyType.Vector2: property.vector2Value = value is Vector2 vector ? vector : Vector2.zero; break;
            }
        }

        private static void SetSerializedGuid(SerializedProperty property, string hexadecimalGuid)
        {
            if (property == null || string.IsNullOrWhiteSpace(hexadecimalGuid))
            {
                return;
            }

            var bytes = System.Guid.ParseExact(hexadecimalGuid, "N").ToByteArray();
            var low = property.FindPropertyRelative("m_GuidLow");
            var high = property.FindPropertyRelative("m_GuidHigh");
            if (low != null) low.longValue = unchecked((long)BitConverter.ToUInt64(bytes, 0));
            if (high != null) high.longValue = unchecked((long)BitConverter.ToUInt64(bytes, 8));
        }

        private static void ConfigureModelImporter()
        {
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(ModelPath) is ModelImporter importer)
            {
                importer.globalScale = 1f;
                importer.useFileScale = false;
                importer.importCameras = false;
                importer.importLights = false;
                importer.importAnimation = false;
                importer.importNormals = ModelImporterNormals.Import;
                importer.meshCompression = ModelImporterMeshCompression.Medium;
                importer.SaveAndReimport();
            }
        }

        private static void ConfigureMarkerImporter()
        {
            AssetDatabase.ImportAsset(MarkerPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(MarkerPath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.Texture2D;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                var android = importer.GetPlatformTextureSettings("Android");
                android.overridden = true;
                android.format = TextureImporterFormat.RGBA32;
                android.textureCompression = TextureImporterCompression.Uncompressed;
                android.maxTextureSize = 1024;
                importer.SetPlatformTextureSettings(android);
                importer.SaveAndReimport();
            }
        }

        private static void ConfigureAndroid()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.productName = "Jaw ArUco Anatomy";
            PlayerSettings.bundleVersion = "1.3.0";
            PlayerSettings.Android.bundleVersionCode = 16;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.omar.jawarucoanatomy");
            TryEnableArCore();
        }

        private static void TryEnableArCore()
        {
            var getOrCreate = typeof(XRGeneralSettingsPerBuildTarget)
                .GetMethod("GetOrCreate", BindingFlags.NonPublic | BindingFlags.Static);
            var settings = getOrCreate?.Invoke(null, null) as XRGeneralSettingsPerBuildTarget;
            if (settings == null) return;
            if (!settings.HasSettingsForBuildTarget(BuildTargetGroup.Android))
                settings.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!settings.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                settings.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            var manager = settings.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            manager.automaticLoading = true;
            manager.automaticRunning = true;
            EditorUtility.SetDirty(manager);
            XRPackageMetadataStore.AssignLoader(manager,
                "UnityEngine.XR.ARCore.ARCoreLoader", BuildTargetGroup.Android);
        }
    }
}
