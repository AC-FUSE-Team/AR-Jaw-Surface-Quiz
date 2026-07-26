using System;
using System.IO;
using System.Linq;
using BMC.JawAR.Quiz;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace BMC.JawAR.Editor
{
    /// <summary>
    /// Builds the isolated full-assembly (marker + plaque + jaw) calibration diagnostic APK.
    /// New package ID, new APK filename -- cannot overwrite v35/v34 or any existing diagnostic
    /// app. Duplicates the production quiz scene (the same tracking implementation used by
    /// v35/v36) into a temporary scene, adds the full-assembly diagnostic overlay and controls,
    /// builds, then restores every protected editor input byte-for-byte.
    /// </summary>
    public static class JawFullPlaqueCalibrationDiagnosticBuild
    {
        // v3: replaces v2's single-sample outlier-rejection correction (too weak against real
        // multi-cm ARCore drift) with a windowed-consensus design that can accept a large,
        // sustained, mutually-consistent correction, not just a small one.
        public const string OutputApk = "/home/omar/JawRepair/JawFullPlaqueCalibrationDiagnostic_v3.apk";
        public const string PackageId = "com.omar.jawfullplaquecalibrationdiag";
        private const string SourceScene = "Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity";
        private const string TempScene = "Assets/JawAR/Editor/JawFullPlaqueCalibrationDiagnostic_TEMP.unity";
        private const string Manifest = "Assets/Plugins/Android/AndroidManifest.xml";
        private const string ProjectSettingsFile = "ProjectSettings/ProjectSettings.asset";
        private const string BuildSettingsFile = "ProjectSettings/EditorBuildSettings.asset";

        private const string PlaqueModelPath = "Assets/JawAR/Models/JawPlaqueMarkerAligned.obj";
        private const string JawModelPath = "Assets/JawAR/Models/JawMarkerAlignedUnity.obj";
        private const string AssemblyModelPath = "Assets/JawAR/Models/JawArUcoBoardCalibration.obj";

        private const string Microphone =
            "    <uses-permission android:name=\"android.permission.RECORD_AUDIO\" />\n";
        private const string SpeechQuery =
            "        <intent>\n            <action android:name=\"android.speech.RecognitionService\" />\n        </intent>\n";
        private const string Internet =
            "    <uses-permission android:name=\"android.permission.INTERNET\" />\n";

        [MenuItem("Tools/Jaw Alignment/Build Full Plaque Calibration Diagnostic")]
        public static void Build()
        {
            if (File.Exists(OutputApk))
                throw new IOException("Safety stop: diagnostic APK already exists: " + OutputApk);
            string[] protectedApkNames =
            {
                "JawSurfaceQuiz_v35_ThreeLearningModes.apk", "JawSurfaceQuiz_v36_InputUsabilityFix.apk",
                "JawSurfaceQuiz_BackboardProxy_v34_Material3UI_PortraitLocked.apk",
                "JawAlignmentDiag_Good_NoNetwork_v17.apk", "JawAlignmentDiag_Good_NoNetwork_v18.apk",
                "JawAlignmentDiag_Quiz_NoNetwork_v30.apk", "JawAlignmentDiag_Quiz_NoNetwork_v31.apk"
            };
            if (protectedApkNames.Contains(Path.GetFileName(OutputApk)))
                throw new InvalidOperationException("Refusing to build: output filename collides with a protected production/diagnostic APK.");

            byte[] manifest = File.ReadAllBytes(Manifest);
            byte[] projectSettings = File.ReadAllBytes(ProjectSettingsFile);
            byte[] buildSettings = File.ReadAllBytes(BuildSettingsFile);
            var snapshot = SettingsSnapshot.Capture();
            try
            {
                // Only the new plaque asset needs its importer configured here. JawModelPath and
                // AssemblyModelPath are pre-existing production assets already configured by
                // JawArUcoSceneBuilder.cs; re-running an importer config against them (even with
                // seemingly matching settings) can silently rewrite their .meta file, which would
                // violate "existing production files remain unchanged". Leave them untouched.
                ConfigureModelImporter(PlaqueModelPath);
                AssetDatabase.Refresh();

                AssetDatabase.DeleteAsset(TempScene);
                if (!AssetDatabase.CopyAsset(SourceScene, TempScene))
                    throw new IOException("Could not create diagnostic scene copy from " + SourceScene);
                var scene = EditorSceneManager.OpenScene(TempScene, OpenSceneMode.Single);

                var tracker = UnityEngine.Object.FindFirstObjectByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include);
                if (tracker == null) throw new InvalidOperationException("Diagnostic scene has no ArUco tracker.");
                if (tracker.jawAnchorRoot == null) throw new InvalidOperationException("Tracker has no jawAnchorRoot.");

                // Hard no-network boundary, same as the existing alignment diagnostics: this is a
                // tracking/registration diagnostic, not the learning app. The protected source
                // scene asset is untouched -- only this temporary copy is edited.
                var quizController = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
                if (quizController != null) UnityEngine.Object.DestroyImmediate(quizController);
                foreach (var voice in UnityEngine.Object.FindObjectsByType<JawVoiceQuestionController>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None)) voice.enabled = false;

                // Hide (do not destroy) the production jaw overlay/hitboxes/UI in this temp copy so
                // they don't visually double up with the new independently-toggleable diagnostic
                // layers; the fingertip pointer is left present (not destroyed) so the controller's
                // runtime "Finger Processing" toggle has a real component to enable/disable.
                foreach (var renderer in tracker.jawAnchorRoot.GetComponentsInChildren<Renderer>(true))
                    if (renderer.transform.name == "VirtualJawOverlay_MarkerAligned" ||
                        renderer.transform.parent?.name == "AnatomyHitboxes_EDITABLE" ||
                        renderer.transform.parent?.parent?.name == "AnatomyHitboxes_EDITABLE")
                        renderer.gameObject.SetActive(false);
                var productionUi = GameObject.Find("Jaw AR UI");
                if (productionUi != null) productionUi.SetActive(false);
                var quizUi = GameObject.Find("Jaw Quiz UI") ?? GameObject.Find("Jaw Quiz Compact UI");
                if (quizUi != null) quizUi.SetActive(false);

                var controller = BuildCalibrationHierarchy(tracker);

                ValidateRuntimeConfiguration(tracker, controller);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, TempScene))
                    throw new IOException("Could not save diagnostic temporary scene.");

                ConfigurePlayer();
                ConfigureManifest();
                Directory.CreateDirectory(Path.GetDirectoryName(OutputApk) ?? ".");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { TempScene },
                    locationPathName = OutputApk,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException($"Diagnostic build failed: {report.summary.result}, errors={report.summary.totalErrors}");
                Debug.Log($"JAW_FULL_PLAQUE_CALIBRATION_DIAGNOSTIC_APK_COMPLETE path={OutputApk} bytes={report.summary.totalSize} package={PackageId}");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempScene);
                snapshot.Restore();
                File.WriteAllBytes(Manifest, manifest);
                File.WriteAllBytes(ProjectSettingsFile, projectSettings);
                File.WriteAllBytes(BuildSettingsFile, buildSettings);
                AssetDatabase.ImportAsset(Manifest, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.Refresh();
                if (!File.ReadAllBytes(Manifest).SequenceEqual(manifest) ||
                    !File.ReadAllBytes(ProjectSettingsFile).SequenceEqual(projectSettings) ||
                    !File.ReadAllBytes(BuildSettingsFile).SequenceEqual(buildSettings))
                    throw new InvalidOperationException("Protected build inputs were not restored byte-for-byte.");
            }
        }

        public static void BuildAndExit()
        {
            Build();
            EditorApplication.Exit(0);
        }

        private static JawFullAssemblyCalibrationController BuildCalibrationHierarchy(JawOpenCvArucoTracker tracker)
        {
            var calibrationAdjustmentRoot = new GameObject("CalibrationAdjustmentRoot_DIAGNOSTIC").transform;
            calibrationAdjustmentRoot.SetParent(tracker.jawAnchorRoot, false);
            calibrationAdjustmentRoot.localPosition = Vector3.zero;
            calibrationAdjustmentRoot.localRotation = Quaternion.identity;
            calibrationAdjustmentRoot.localScale = Vector3.one;

            var markerOutlineLayer = new GameObject("MarkerOutlineLayer").transform;
            markerOutlineLayer.SetParent(calibrationAdjustmentRoot, false);

            var plaqueLayer = new GameObject("PlaqueLayer").transform;
            plaqueLayer.SetParent(calibrationAdjustmentRoot, false);
            InstantiateModel(PlaqueModelPath, plaqueLayer, "PlaqueMesh_MarkerAligned");

            var jawLayerRoot = new GameObject("JawLayer").transform;
            jawLayerRoot.SetParent(calibrationAdjustmentRoot, false);
            var jawOnlyAdjustmentRoot = new GameObject("JawOnlyAdjustmentRoot_EXPERT").transform;
            jawOnlyAdjustmentRoot.SetParent(jawLayerRoot, false);
            var jawMeshInstance = InstantiateModel(JawModelPath, jawOnlyAdjustmentRoot, "JawMesh_MarkerAligned_Diagnostic");

            var completeAssemblyLayer = new GameObject("CompleteAssemblyLayer").transform;
            completeAssemblyLayer.SetParent(calibrationAdjustmentRoot, false);
            InstantiateModel(AssemblyModelPath, completeAssemblyLayer, "AssemblyMesh_MarkerAligned");

            var axesLayer = new GameObject("AxesLayer").transform;
            axesLayer.SetParent(calibrationAdjustmentRoot, false);
            var originMarkersLayer = new GameObject("OriginMarkersLayer").transform;
            originMarkersLayer.SetParent(calibrationAdjustmentRoot, false);
            var boundingBoxLayer = new GameObject("BoundingBoxLayer").transform;
            boundingBoxLayer.SetParent(calibrationAdjustmentRoot, false);

            var fingertip = UnityEngine.Object.FindFirstObjectByType<JawFingertipPointer>(FindObjectsInactive.Include);

            var controller = tracker.gameObject.AddComponent<JawFullAssemblyCalibrationController>();
            controller.tracker = tracker;
            controller.fingertipPointer = fingertip;
            controller.configurationLabel = "fullplaque";
            controller.calibrationAdjustmentRoot = calibrationAdjustmentRoot;
            controller.markerOutlineLayerRoot = markerOutlineLayer;
            controller.plaqueLayerRoot = plaqueLayer;
            controller.jawLayerRoot = jawLayerRoot;
            controller.jawOnlyAdjustmentRoot = jawOnlyAdjustmentRoot;
            controller.jawMeshInstance = jawMeshInstance;
            controller.completeAssemblyLayerRoot = completeAssemblyLayer;
            controller.axesLayerRoot = axesLayer;
            controller.originMarkersLayerRoot = originMarkersLayer;
            controller.boundingBoxLayerRoot = boundingBoxLayer;
            return controller;
        }

        private static Transform InstantiateModel(string path, Transform parent, string instanceName)
        {
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (modelAsset == null) throw new FileNotFoundException($"Diagnostic model was not imported: {path}");
            var instance = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
            if (instance == null) throw new InvalidOperationException($"Could not instantiate {path}");
            instance.name = instanceName;
            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            return instance.transform;
        }

        private static void ConfigureModelImporter(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is ModelImporter importer)
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

        private static void ValidateRuntimeConfiguration(JawOpenCvArucoTracker tracker,
            JawFullAssemblyCalibrationController controller)
        {
            if (UnityEngine.Object.FindObjectsByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1 ||
                UnityEngine.Object.FindObjectsByType<JawFullAssemblyCalibrationController>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1)
                throw new InvalidOperationException("Diagnostic scene must have exactly one tracker and one calibration controller.");
            if (tracker.dictionaryMarkerId != 1 || Mathf.Abs(tracker.blackSquareSizeMeters - 0.056f) > 1e-6f)
                throw new InvalidOperationException("Protected marker calibration changed.");
            if (UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include) != null)
                throw new InvalidOperationException("No-network diagnostic still contains the quiz learning controller.");
            if (controller.calibrationAdjustmentRoot == null || controller.jawMeshInstance == null ||
                controller.plaqueLayerRoot == null || controller.completeAssemblyLayerRoot == null)
                throw new InvalidOperationException("Calibration diagnostic hierarchy is incomplete.");
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Jaw Full Plaque Calibration Diagnostic";
            PlayerSettings.bundleVersion = "1.2.0-fullplaque-diagnostic-windowedcorrection";
            PlayerSettings.Android.bundleVersionCode = 3;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);
            // Fixed portrait, per spec -- unlike the existing alignment diagnostics (which auto-rotate).
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
        }

        private static void ConfigureManifest()
        {
            string text = File.ReadAllText(Manifest)
                .Replace(Microphone, string.Empty)
                .Replace(SpeechQuery, string.Empty)
                .Replace(Internet, string.Empty);
            File.WriteAllText(Manifest, text);
            AssetDatabase.ImportAsset(Manifest, ImportAssetOptions.ForceSynchronousImport);
        }

        private sealed class SettingsSnapshot
        {
            private readonly string product = PlayerSettings.productName;
            private readonly string version = PlayerSettings.bundleVersion;
            private readonly int code = PlayerSettings.Android.bundleVersionCode;
            private readonly string package = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            private readonly UIOrientation orientation = PlayerSettings.defaultInterfaceOrientation;
            private readonly bool portrait = PlayerSettings.allowedAutorotateToPortrait;
            private readonly bool upsideDown = PlayerSettings.allowedAutorotateToPortraitUpsideDown;
            private readonly bool left = PlayerSettings.allowedAutorotateToLandscapeLeft;
            private readonly bool right = PlayerSettings.allowedAutorotateToLandscapeRight;
            private readonly bool defaultGraphics = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
            private readonly GraphicsDeviceType[] graphics = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            private readonly AndroidArchitecture architectures = PlayerSettings.Android.targetArchitectures;
            private readonly ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
            private readonly InsecureHttpOption insecureHttp = PlayerSettings.insecureHttpOption;
            public static SettingsSnapshot Capture() => new();
            public void Restore()
            {
                PlayerSettings.productName = product;
                PlayerSettings.bundleVersion = version;
                PlayerSettings.Android.bundleVersionCode = code;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, package);
                PlayerSettings.defaultInterfaceOrientation = orientation;
                PlayerSettings.allowedAutorotateToPortrait = portrait;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = upsideDown;
                PlayerSettings.allowedAutorotateToLandscapeLeft = left;
                PlayerSettings.allowedAutorotateToLandscapeRight = right;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, defaultGraphics);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, graphics);
                PlayerSettings.Android.targetArchitectures = architectures;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, backend);
                PlayerSettings.insecureHttpOption = insecureHttp;
                AssetDatabase.SaveAssets();
            }
        }
    }
}
