using System;
using System.IO;
using System.Linq;
using BMC.JawAR.Quiz;
using BMC.JawAR.SurfaceRegions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace BMC.JawAR.Editor
{
    /// <summary>Creates isolated, side-by-side, no-network alignment diagnostic APKs.</summary>
    public static class JawAlignmentDiagnosticBuild
    {
        public const string QuizOutput = "/home/omar/JawRepair/JawAlignmentDiag_Quiz_NoNetwork_v30.apk";
        public const string GoodOutput = "/home/omar/JawRepair/JawAlignmentDiag_Good_NoNetwork_v17.apk";
        private const string QuizSource = "Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity";
        private const string GoodSource = "Assets/Scenes/JawArUcoAnatomy_SurfacePaint_AR.unity";
        private const string TempScene = "Assets/JawAR/Editor/JawAlignmentDiagnostic_TEMP.unity";
        private const string Manifest = "Assets/Plugins/Android/AndroidManifest.xml";
        private const string ProjectSettingsFile = "ProjectSettings/ProjectSettings.asset";
        private const string BuildSettingsFile = "ProjectSettings/EditorBuildSettings.asset";
        private const string Microphone =
            "    <uses-permission android:name=\"android.permission.RECORD_AUDIO\" />\n";
        private const string SpeechQuery =
            "        <intent>\n            <action android:name=\"android.speech.RecognitionService\" />\n        </intent>\n";

        [MenuItem("Tools/Jaw Alignment/Build Quiz No-Network Diagnostic")]
        public static void BuildQuiz() => Build(QuizSource, QuizOutput,
            "com.omar.jawsurfacequizalignmentdiag", "Jaw Quiz Alignment Diagnostic", "quiz", false, 30);

        [MenuItem("Tools/Jaw Alignment/Build Good No-Network Diagnostic")]
        public static void BuildGood() => Build(GoodSource, GoodOutput,
            "com.omar.jawgoodalignmentdiag", "Jaw Good Alignment Diagnostic", "good", true, 17);

        private static void Build(string source, string output, string package, string product,
            string label, bool configureGoodProfile, int versionCode)
        {
            if (File.Exists(output)) throw new IOException("Safety stop: diagnostic APK already exists: " + output);
            byte[] manifest = File.ReadAllBytes(Manifest);
            byte[] projectSettings = File.ReadAllBytes(ProjectSettingsFile);
            byte[] buildSettings = File.ReadAllBytes(BuildSettingsFile);
            var snapshot = SettingsSnapshot.Capture();
            try
            {
                AssetDatabase.DeleteAsset(TempScene);
                if (!AssetDatabase.CopyAsset(source, TempScene))
                    throw new IOException("Could not create diagnostic scene copy from " + source);
                var scene = EditorSceneManager.OpenScene(TempScene, OpenSceneMode.Single);
                var tracker = UnityEngine.Object.FindFirstObjectByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include);
                if (tracker == null) throw new InvalidOperationException("Diagnostic scene has no ArUco tracker.");

                // Hard no-network boundary: this is a tracking diagnostic, so the component that
                // creates a proxy client, polls health, syncs attempts, and exposes network buttons
                // is removed from the temporary scene. The protected source scene is untouched.
                var quizController = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
                if (quizController != null) UnityEngine.Object.DestroyImmediate(quizController);
                foreach (var voice in UnityEngine.Object.FindObjectsByType<JawVoiceQuestionController>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None)) voice.enabled = false;
                // Avoid local hand-landmarker model work during the tracking-only investigation.
                foreach (var fingertip in UnityEngine.Object.FindObjectsByType<JawFingertipPointer>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None)) fingertip.enabled = false;

                if (configureGoodProfile) ConfigureKnownGoodTemporaryProfile(tracker);
                var diagnostic = tracker.gameObject.AddComponent<JawAlignmentDiagnosticController>();
                diagnostic.tracker = tracker;
                diagnostic.configurationLabel = label;
                diagnostic.samplesPerSecond = 3f;
                ValidateRuntimeConfiguration(tracker, diagnostic);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, TempScene))
                    throw new IOException("Could not save diagnostic temporary scene.");

                ConfigurePlayer(package, product, label, versionCode);
                ConfigureNoNetworkManifest();
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { TempScene },
                    locationPathName = output,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException($"Diagnostic build failed: {report.summary.result}, errors={report.summary.totalErrors}");
                Debug.Log($"JAW_ALIGNMENT_DIAGNOSTIC_APK_COMPLETE label={label} path={output} bytes={report.summary.totalSize} network=false corrections=false");
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

        private static void ConfigureKnownGoodTemporaryProfile(JawOpenCvArucoTracker tracker)
        {
            tracker.detectionLongEdge = 1280;
            tracker.detectionsPerSecond = 6f;
            tracker.trackingSettleSeconds = 2f;
            tracker.stableDetectionsRequired = 24;
            tracker.lockSampleWindowSize = 30;
            tracker.maxPositionSpreadMeters = 0.0032f;
            tracker.maxRotationSpreadDegrees = 1f;
            tracker.stableWindowsRequired = 4;
            tracker.maxSampleDeviationMeters = 0.015f;
            tracker.maxSampleAngularDeviationDegrees = 7f;

            var target = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>(FindObjectsInactive.Include);
            if (target != null)
            {
                target.surfaceLookupEnabled = true;
                var overlay = target.GetComponent<JawSurfaceRegionRuntimeOverlay>() ??
                              target.gameObject.AddComponent<JawSurfaceRegionRuntimeOverlay>();
                overlay.target = target;
                overlay.opacity = 0.58f;
                overlay.showOnEnable = true;
            }
        }

        private static void ValidateRuntimeConfiguration(JawOpenCvArucoTracker tracker,
            JawAlignmentDiagnosticController diagnostic)
        {
            if (UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1 ||
                UnityEngine.Object.FindObjectsByType<ARCameraManager>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1 ||
                UnityEngine.Object.FindObjectsByType<ARInputManager>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1 ||
                UnityEngine.Object.FindObjectsByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1 ||
                UnityEngine.Object.FindObjectsByType<JawAlignmentDiagnosticController>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1)
                throw new InvalidOperationException("Diagnostic scene must have exactly one camera/input manager/tracker/diagnostic controller.");
            if (tracker.dictionaryMarkerId != 1 || Mathf.Abs(tracker.blackSquareSizeMeters - 0.056f) > 1e-6f)
                throw new InvalidOperationException("Protected marker calibration changed.");
            if (diagnostic.samplesPerSecond < 2f || diagnostic.samplesPerSecond > 4f)
                throw new InvalidOperationException("Diagnostic sampling must remain bounded to 2–4 Hz.");
            if (UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include) != null)
                throw new InvalidOperationException("No-network diagnostic still contains the quiz learning controller.");
        }

        private static void ConfigurePlayer(string package, string product, string label, int versionCode)
        {
            PlayerSettings.productName = product;
            PlayerSettings.bundleVersion = "1.0.0-alignment-" + label;
            PlayerSettings.Android.bundleVersionCode = versionCode;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, package);
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = true;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
        }

        private static void ConfigureNoNetworkManifest()
        {
            string text = File.ReadAllText(Manifest).Replace(Microphone, string.Empty).Replace(SpeechQuery, string.Empty);
            // INTERNET is deliberately absent from these tracking-only APKs.
            text = text.Replace("    <uses-permission android:name=\"android.permission.INTERNET\" />\n", string.Empty);
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
