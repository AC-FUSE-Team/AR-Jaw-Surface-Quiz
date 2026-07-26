using System;
using System.IO;
using System.Linq;
using System.Net;
using BMC.JawAR.SurfaceRegions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace BMC.JawAR.Quiz.Editor
{
    public static class JawQuizAndroidTestBuild
    {
        public const string OutputPath = "/home/omar/JawRepair/JawSurfaceQuiz_Portrait_ClaudeTrackingFix_Test.apk";
        public const string PackageId = "com.omar.jawsurfacequiztest";
        public const string ProductName = "Jaw Surface Quiz Test";
        public const string VersionName = "1.3.4-quiz-orientation-fix-test";
        public const int VersionCode = 20;

        // Separate, non-overwriting output for the bounded runtime pose-diagnostic build: the
        // orientation fix above was verified applied (aapt-confirmed) but did not resolve the
        // reported movement, so this build adds JawQuizSceneController's throttled JAW_QUIZ_DIAG
        // logcat trace (jaw anchor pose vs AR camera pose) to see what is actually moving.
        public const string DiagnosticOutputPath = "/home/omar/JawRepair/JawSurfaceQuiz_Portrait_ClaudeDiagnostic_Test.apk";
        public const string DiagnosticVersionName = "1.3.5-quiz-pose-diagnostic-test";
        public const int DiagnosticVersionCode = 21;

        // The diagnostic log proved the locked jaw pose never moves and the scene hierarchy is
        // byte-identical to the working app's, apart from documented quiz-only additions. The
        // remaining structural difference found: the quiz UI covers most of the screen with
        // near-opaque panels (only ~28% of screen height showed live camera), unlike the working
        // app's plain-text-only UI (full camera visibility). That limits how well a student can
        // frame/center the marker while calibrating, which plausibly explains a stable-but-wrong
        // lock. JawQuizSceneController's panel alpha was lowered to restore camera visibility;
        // this build keeps the diagnostic pose logging active so pose data is still available
        // alongside the UI fix.
        public const string UIVisibilityOutputPath = "/home/omar/JawRepair/JawSurfaceQuiz_Portrait_ClaudeUIVisibilityFix_Test.apk";
        public const string UIVisibilityVersionName = "1.3.6-quiz-ui-visibility-fix-test";
        public const int UIVisibilityVersionCode = 22;

        // The static-hold diagnostic proved the locked jaw pose never moves (0.00000 drift over
        // 66s), and the user confirmed the mismatch is a fixed lateral offset, not motion --
        // ruling out drift/orientation/UI causes already tried. The only two quiz lock events with
        // full diagnostic data both happened from ~50cm away, well beyond where a 0.056m marker
        // gives solvePnP enough corner-pixel resolution for an accurate (not just precise) pose;
        // that also matches the ~3x worse sample spread measured versus the one captured working
        // lock. JawOpenCvArucoTracker.maxLockDistanceMeters now rejects samples beyond 35cm.
        public const string LockDistanceFixOutputPath = "/home/omar/JawRepair/JawSurfaceQuiz_Portrait_ClaudeLockDistanceFix_Test.apk";
        public const string LockDistanceFixVersionName = "1.3.7-quiz-lock-distance-fix-test";
        public const int LockDistanceFixVersionCode = 23;

        // The confirmed-good APK declares Android FULL_USER (13). The earlier quiz correction
        // still declared USER_PORTRAIT (12), so it did not actually reproduce the good app's
        // display-rotation/camera-coordinate pipeline. This build matches FULL_USER exactly and
        // removes the unproven 35 cm lock-distance restriction from the shared tracker.
        public const string TrackingParityOutputPath =
            "/home/omar/JawRepair/JawSurfaceQuiz_WorkingTrackingParity_Test.apk";
        public const string TrackingParityVersionName = "1.3.8-quiz-working-tracking-parity-test";
        public const int TrackingParityVersionCode = 24;

        // All five user screenshots from v24 corresponded to 1.50-1.54 mm lock spreads. The same
        // phone/session achieved 0.55 mm and the captured good-app lock measured 0.80 mm. This
        // build accepts at most 1.0 mm without imposing any camera-to-marker distance rule.
        public const string HighQualityLockOutputPath =
            "/home/omar/JawRepair/JawSurfaceQuiz_HighQualityLock_Test.apk";
        public const string HighQualityLockVersionName = "1.3.9-quiz-high-quality-lock-test";
        public const int HighQualityLockVersionCode = 25;

        // Prepared only; the user must first confirm that the phone browser reaches /health.
        // The replaceable LAN token and private URL are injected into a temporary scene copy and
        // are never written to the protected quiz scene or ProjectSettings.
        public const string PhoneProxyOutputPath =
            "/home/omar/JawRepair/JawSurfaceQuiz_BackboardProxy_v29_CleartextAndTestingStateFix.apk";
        public const string PhoneProxyProductName = "Jaw Surface Quiz Backboard Test";
        public const string PhoneProxyVersionName = "1.4.3-quiz-proxy-cleartext-state-fix";
        public const int PhoneProxyVersionCode = 29;
        public const string PhoneProxyDefaultUrl = "http://192.168.2.244:8765";
        private const string TemporaryPhoneProxyScenePath =
            "Assets/JawAR/Quiz/Editor/JawSurfaceQuiz_PhoneProxy_Build.unity";

        private const string ManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";
        private const string ProjectSettingsPath = "ProjectSettings/ProjectSettings.asset";
        private const string EditorBuildSettingsPath = "ProjectSettings/EditorBuildSettings.asset";
        private const string MicrophonePermission =
            "    <uses-permission android:name=\"android.permission.RECORD_AUDIO\" />\n";
        private const string SpeechRecognitionQuery =
            "        <intent>\n            <action android:name=\"android.speech.RecognitionService\" />\n        </intent>\n";

        [MenuItem("Tools/Jaw Anatomy Quiz/Build Portrait Android Test APK")]
        public static void Build() => BuildInternal(OutputPath, VersionName, VersionCode);

        [MenuItem("Tools/Jaw Anatomy Quiz/Build Portrait Android Diagnostic APK")]
        public static void BuildDiagnostic() =>
            BuildInternal(DiagnosticOutputPath, DiagnosticVersionName, DiagnosticVersionCode);

        [MenuItem("Tools/Jaw Anatomy Quiz/Build Portrait Android UI Visibility Fix APK")]
        public static void BuildUIVisibilityFix() =>
            BuildInternal(UIVisibilityOutputPath, UIVisibilityVersionName, UIVisibilityVersionCode);

        [MenuItem("Tools/Jaw Anatomy Quiz/Build Portrait Android Lock Distance Fix APK")]
        public static void BuildLockDistanceFix() =>
            BuildInternal(LockDistanceFixOutputPath, LockDistanceFixVersionName, LockDistanceFixVersionCode);

        [MenuItem("Tools/Jaw Anatomy Quiz/Build Working Tracking Parity APK")]
        public static void BuildTrackingParity() =>
            BuildInternal(TrackingParityOutputPath, TrackingParityVersionName, TrackingParityVersionCode);

        [MenuItem("Tools/Jaw Anatomy Quiz/Build High Quality Lock APK")]
        public static void BuildHighQualityLock() =>
            BuildInternal(HighQualityLockOutputPath, HighQualityLockVersionName, HighQualityLockVersionCode);

        [MenuItem("Tools/Jaw Anatomy Quiz/Build Backboard LAN Test APK")]
        public static void BuildPhoneProxy()
        {
            var proxyUrl = Environment.GetEnvironmentVariable("QUIZ_PROXY_URL") ?? PhoneProxyDefaultUrl;
            var prototypeToken = Environment.GetEnvironmentVariable("QUIZ_PROXY_TOKEN") ?? string.Empty;
            ValidatePhoneProxyBuildConfiguration(proxyUrl, prototypeToken);
            BuildInternal(PhoneProxyOutputPath, PhoneProxyVersionName, PhoneProxyVersionCode,
                proxyUrl, prototypeToken, PhoneProxyProductName);
        }

        private static void BuildInternal(string outputPath, string versionName, int versionCode,
            string proxyUrl = "", string prototypeToken = "", string productName = ProductName)
        {
            if (File.Exists(outputPath))
                throw new IOException("Safety stop: output APK already exists and will not be overwritten: " + outputPath);

            var snapshot = BuildSettingsSnapshot.Capture();
            var manifestBytes = File.ReadAllBytes(ManifestPath);
            var projectSettingsBytes = File.ReadAllBytes(ProjectSettingsPath);
            var editorBuildSettingsBytes = File.ReadAllBytes(EditorBuildSettingsPath);
            var manifestChanged = false;
            var buildScenePath = JawQuizSceneBuilder.QuizScenePath;
            try
            {
                ValidateQuizScene();
                if (!string.IsNullOrEmpty(proxyUrl))
                    buildScenePath = CreateTemporaryPhoneProxyScene(proxyUrl, prototypeToken);
                ConfigureTemporaryBuildSettings(versionName, versionCode, productName,
                    allowInsecureHttp: !string.IsNullOrEmpty(proxyUrl));
                ValidateOrientationIsPortraitLocked();
                manifestChanged = string.IsNullOrEmpty(proxyUrl)
                    ? RemoveMicrophonePermission()
                    : ConfigurePhoneProxyManifest();

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ??
                                          throw new InvalidOperationException("Output directory is invalid."));
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { buildScenePath },
                    locationPathName = outputPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };
                var report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException(
                        $"Quiz Android build failed: {report.summary.result}, errors={report.summary.totalErrors}");

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                    throw new IOException("Unity reported success but the output APK is missing or empty.");

                Debug.Log($"JAW_QUIZ_ANDROID_TEST_APK_COMPLETE path={outputPath} " +
                          $"bytes={new FileInfo(outputPath).Length} package={PackageId} " +
                          $"product={productName} version={versionName}({versionCode}) " +
                          $"scene0={buildScenePath} orientationMode=FixedUprightPortrait " +
                          "graphics=OpenGLES3 architecture=ARM64 backend=IL2CPP microphone=false");
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemporaryPhoneProxyScenePath) != null)
                    AssetDatabase.DeleteAsset(TemporaryPhoneProxyScenePath);
                if (manifestChanged || !File.ReadAllBytes(ManifestPath).SequenceEqual(manifestBytes))
                {
                    File.WriteAllBytes(ManifestPath, manifestBytes);
                    AssetDatabase.ImportAsset(ManifestPath, ImportAssetOptions.ForceSynchronousImport);
                }
                snapshot.Restore();
                // Unity can serialize an explicitly equivalent default backend or resolved scene
                // GUID. Restore exact bytes so this isolated build leaves ProjectSettings untouched.
                File.WriteAllBytes(ProjectSettingsPath, projectSettingsBytes);
                File.WriteAllBytes(EditorBuildSettingsPath, editorBuildSettingsBytes);
                snapshot.AssertRestored(manifestBytes, projectSettingsBytes, editorBuildSettingsBytes);
                Debug.Log("JAW_QUIZ_ANDROID_TEST_SETTINGS_RESTORED");
            }
        }

        internal static void ValidatePhoneProxyBuildConfiguration(string proxyUrl, string prototypeToken)
        {
            if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
                uri.Port != 8765 || !IPAddress.TryParse(uri.Host, out var address) || !IsPrivateIpv4(address))
                throw new InvalidOperationException(
                    "QUIZ_PROXY_URL must be http://<private-ipv4>:8765 for the bounded phone build.");
            if (prototypeToken.Length < 32)
                throw new InvalidOperationException("QUIZ_PROXY_TOKEN is missing or too short.");
        }

        private static bool IsPrivateIpv4(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return false;
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        private static string CreateTemporaryPhoneProxyScene(string proxyUrl, string prototypeToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TemporaryPhoneProxyScenePath) ??
                                      throw new InvalidOperationException("Temporary scene path is invalid."));
            var source = EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
            if (!EditorSceneManager.SaveScene(source, TemporaryPhoneProxyScenePath, true))
                throw new IOException("Could not create the temporary phone-proxy scene copy.");
            var temporary = EditorSceneManager.OpenScene(TemporaryPhoneProxyScenePath, OpenSceneMode.Single);
            var controller = UnityEngine.Object.FindAnyObjectByType<JawQuizSceneController>(
                FindObjectsInactive.Include);
            if (controller == null) throw new InvalidOperationException("Temporary quiz controller is missing.");
            controller.learningProxyUrl = proxyUrl;
            controller.learningProxyPrototypeToken = prototypeToken;
            EditorUtility.SetDirty(controller);
            if (!EditorSceneManager.SaveScene(temporary))
                throw new IOException("Could not save temporary phone-proxy configuration.");
            return TemporaryPhoneProxyScenePath;
        }

        public static void BuildAndExit()
        {
            try
            {
                Build();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void BuildDiagnosticAndExit()
        {
            try
            {
                BuildDiagnostic();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void BuildUIVisibilityFixAndExit()
        {
            try
            {
                BuildUIVisibilityFix();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void BuildLockDistanceFixAndExit()
        {
            try
            {
                BuildLockDistanceFix();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void BuildTrackingParityAndExit()
        {
            try
            {
                BuildTrackingParity();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void BuildHighQualityLockAndExit()
        {
            try
            {
                BuildHighQualityLock();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void BuildPhoneProxyAndExit()
        {
            try
            {
                BuildPhoneProxy();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        private static void ValidateQuizScene()
        {
            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(JawQuizSceneBuilder.QuizScenePath);
            if (sceneAsset == null)
                throw new FileNotFoundException("Quiz scene is missing.", JawQuizSceneBuilder.QuizScenePath);

            var scene = EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
            if (scene.path != JawQuizSceneBuilder.QuizScenePath)
                throw new InvalidOperationException("Unexpected scene opened: " + scene.path);

            var target = UnityEngine.Object.FindAnyObjectByType<JawSurfaceRegionTarget>(FindObjectsInactive.Include);
            var presenter = UnityEngine.Object.FindAnyObjectByType<JawQuizPaintedRegionPresenter>(FindObjectsInactive.Include);
            var adapter = UnityEngine.Object.FindAnyObjectByType<JawQuizSurfaceSelectionAdapter>(FindObjectsInactive.Include);
            var controller = UnityEngine.Object.FindAnyObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            var coordinator = UnityEngine.Object.FindAnyObjectByType<JawSurfaceRegionSelectionCoordinator>(FindObjectsInactive.Include);
            var oldTap = UnityEngine.Object.FindAnyObjectByType<JawAnatomyTapController>(FindObjectsInactive.Include);
            var oldVoice = UnityEngine.Object.FindAnyObjectByType<JawVoiceQuestionController>(FindObjectsInactive.Include);
            var tracker = UnityEngine.Object.FindAnyObjectByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include);
            var camera = UnityEngine.Object.FindAnyObjectByType<Camera>(FindObjectsInactive.Include);

            if (target == null || presenter == null || adapter == null || controller == null ||
                coordinator == null || oldTap == null || oldVoice == null || tracker == null || camera == null)
                throw new InvalidOperationException("Quiz scene is missing required jaw, quiz, or AR components.");
            if (controller.jawTracker != tracker)
                throw new InvalidOperationException("Quiz controller is not wired to the active ArUco tracker.");
            if (AssetDatabase.GetAssetPath(target.regionMap) != JawQuizSceneBuilder.DraftMapPath)
                throw new InvalidOperationException("Quiz scene is not using the Codex draft surface map.");
            if (!target.surfaceLookupEnabled || !presenter.visibleByDefault)
                throw new InvalidOperationException("Surface lookup or painted-region default visibility is disabled.");
            if (!adapter.enabled || !adapter.acceptScreenInput || adapter.targetCamera != camera ||
                adapter.surfaceTarget != target)
                throw new InvalidOperationException("Quiz-only live-camera touch adapter is not correctly enabled.");
            if (coordinator.enabled || oldTap.enabled || oldVoice.enabled)
                throw new InvalidOperationException("A conflicting legacy quiz/tap/voice controller is enabled.");
            if (camera.GetComponent<ARCameraManager>() == null || camera.GetComponent<ARCameraBackground>() == null ||
                tracker.arCamera != camera || tracker.cameraManager != camera.GetComponent<ARCameraManager>())
                throw new InvalidOperationException("AR camera or ArUco tracker wiring is incomplete.");
            if (tracker.dictionaryMarkerId != 1 || !Mathf.Approximately(tracker.blackSquareSizeMeters, 0.056f))
                throw new InvalidOperationException("ArUco marker ID or physical size changed unexpectedly.");
            if (tracker.detectionLongEdge != 1280 || !Mathf.Approximately(tracker.detectionsPerSecond, 6f) ||
                !Mathf.Approximately(tracker.trackingSettleSeconds, 2f) || tracker.stableDetectionsRequired != 24 ||
                tracker.lockSampleWindowSize != 30 || !Mathf.Approximately(tracker.maxPositionSpreadMeters, 0.001f) ||
                !Mathf.Approximately(tracker.maxRotationSpreadDegrees, 1f) || tracker.stableWindowsRequired != 4 ||
                !Mathf.Approximately(tracker.maxSampleDeviationMeters, 0.015f) ||
                !Mathf.Approximately(tracker.maxSampleAngularDeviationDegrees, 7f))
                throw new InvalidOperationException("Quiz tracker does not match the phone-proven stability profile.");

            Debug.Log($"JAW_QUIZ_ANDROID_PREFLIGHT_OK scene={scene.path} map={JawQuizSceneBuilder.DraftMapPath} " +
                      "surfaceLookup=true paintedDefault=true adapter=true legacyControllers=false " +
                      "camera=liveAR marker=DICT_5X5_50_ID1_0.056m");
        }

        // Internal (not private) so JawQuizAndroidTestBuildOrientationTests can exercise the
        // orientation configuration directly without performing a full Android build.
        internal static void ConfigureTemporaryBuildSettings(string versionName = VersionName,
            int versionCode = VersionCode, string productName = ProductName,
            bool allowInsecureHttp = false)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("Could not switch the active build target to Android.");

            EditorBuildSettings.scenes =
                new[] { new EditorBuildSettingsScene(JawQuizSceneBuilder.QuizScenePath, true) };
            PlayerSettings.productName = productName;
            PlayerSettings.bundleVersion = versionName;
            PlayerSettings.Android.bundleVersionCode = versionCode;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);
            // Fixed upright portrait is intentional for the jaw quiz. Unity emits Android
            // PORTRAIT (1), while the source manifest independently pins the merged activity to
            // android:screenOrientation="portrait" so sensor/user rotation cannot override it.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            if (allowInsecureHttp)
                ConfigurePhoneProxyHttp();
        }

        internal static void ConfigurePhoneProxyHttp() =>
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

        internal static void ValidateOrientationIsPortraitLocked()
        {
            if (PlayerSettings.defaultInterfaceOrientation != UIOrientation.Portrait)
                throw new InvalidOperationException(
                    "Quiz build must use fixed upright Portrait orientation.");
            if (!PlayerSettings.allowedAutorotateToPortrait ||
                PlayerSettings.allowedAutorotateToPortraitUpsideDown ||
                PlayerSettings.allowedAutorotateToLandscapeLeft ||
                PlayerSettings.allowedAutorotateToLandscapeRight)
                throw new InvalidOperationException(
                    "Quiz build must disable reverse portrait and both landscape directions.");
        }

        private static bool RemoveMicrophonePermission()
        {
            var manifest = File.ReadAllText(ManifestPath);
            var withoutMicrophone = manifest.Replace(MicrophonePermission, string.Empty);
            if (withoutMicrophone == manifest)
                throw new InvalidOperationException("Expected microphone permission was not found in the shared manifest.");
            File.WriteAllText(ManifestPath, withoutMicrophone);
            AssetDatabase.ImportAsset(ManifestPath, ImportAssetOptions.ForceSynchronousImport);
            return true;
        }

        private static bool ConfigurePhoneProxyManifest()
        {
            var manifest = File.ReadAllText(ManifestPath);
            if (!manifest.Contains(MicrophonePermission) || !manifest.Contains(SpeechRecognitionQuery) ||
                !manifest.Contains("    <application>"))
                throw new InvalidOperationException(
                    "Shared manifest does not match the expected isolated phone-build template.");
            var configured = manifest
                .Replace(MicrophonePermission, string.Empty)
                .Replace(SpeechRecognitionQuery, string.Empty)
                .Replace("    <application>",
                    "    <application android:usesCleartextTraffic=\"true\">");
            File.WriteAllText(ManifestPath, configured);
            AssetDatabase.ImportAsset(ManifestPath, ImportAssetOptions.ForceSynchronousImport);
            return true;
        }

        private sealed class BuildSettingsSnapshot
        {
            private readonly EditorBuildSettingsScene[] scenes;
            private readonly string productName;
            private readonly string versionName;
            private readonly int versionCode;
            private readonly string packageId;
            private readonly UIOrientation orientation;
            private readonly bool portrait;
            private readonly bool portraitUpsideDown;
            private readonly bool landscapeLeft;
            private readonly bool landscapeRight;
            private readonly bool useDefaultGraphics;
            private readonly GraphicsDeviceType[] graphicsApis;
            private readonly AndroidArchitecture architectures;
            private readonly ScriptingImplementation scriptingBackend;
            private readonly InsecureHttpOption insecureHttpOption;
            private readonly BuildTarget activeBuildTarget;

            private BuildSettingsSnapshot()
            {
                scenes = EditorBuildSettings.scenes;
                productName = PlayerSettings.productName;
                versionName = PlayerSettings.bundleVersion;
                versionCode = PlayerSettings.Android.bundleVersionCode;
                packageId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
                orientation = PlayerSettings.defaultInterfaceOrientation;
                portrait = PlayerSettings.allowedAutorotateToPortrait;
                portraitUpsideDown = PlayerSettings.allowedAutorotateToPortraitUpsideDown;
                landscapeLeft = PlayerSettings.allowedAutorotateToLandscapeLeft;
                landscapeRight = PlayerSettings.allowedAutorotateToLandscapeRight;
                useDefaultGraphics = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
                graphicsApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
                architectures = PlayerSettings.Android.targetArchitectures;
                scriptingBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
                insecureHttpOption = PlayerSettings.insecureHttpOption;
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            }

            public static BuildSettingsSnapshot Capture() => new();

            public void Restore()
            {
                EditorBuildSettings.scenes = scenes;
                PlayerSettings.productName = productName;
                PlayerSettings.bundleVersion = versionName;
                PlayerSettings.Android.bundleVersionCode = versionCode;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, packageId);
                PlayerSettings.defaultInterfaceOrientation = orientation;
                PlayerSettings.allowedAutorotateToPortrait = portrait;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = portraitUpsideDown;
                PlayerSettings.allowedAutorotateToLandscapeLeft = landscapeLeft;
                PlayerSettings.allowedAutorotateToLandscapeRight = landscapeRight;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, useDefaultGraphics);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, graphicsApis);
                PlayerSettings.Android.targetArchitectures = architectures;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, scriptingBackend);
                PlayerSettings.insecureHttpOption = insecureHttpOption;
                if (activeBuildTarget != EditorUserBuildSettings.activeBuildTarget)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildPipeline.GetBuildTargetGroup(activeBuildTarget), activeBuildTarget);
                AssetDatabase.SaveAssets();
            }

            public void AssertRestored(byte[] manifestBytes, byte[] projectSettingsBytes,
                byte[] editorBuildSettingsBytes)
            {
                var restored = scenes.Select(s => $"{s.enabled}:{s.path}")
                    .SequenceEqual(EditorBuildSettings.scenes.Select(s => $"{s.enabled}:{s.path}")) &&
                    PlayerSettings.productName == productName &&
                    PlayerSettings.bundleVersion == versionName &&
                    PlayerSettings.Android.bundleVersionCode == versionCode &&
                    PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) == packageId &&
                    PlayerSettings.defaultInterfaceOrientation == orientation &&
                    PlayerSettings.allowedAutorotateToPortrait == portrait &&
                    PlayerSettings.allowedAutorotateToPortraitUpsideDown == portraitUpsideDown &&
                    PlayerSettings.allowedAutorotateToLandscapeLeft == landscapeLeft &&
                    PlayerSettings.allowedAutorotateToLandscapeRight == landscapeRight &&
                    PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) == useDefaultGraphics &&
                    graphicsApis.SequenceEqual(PlayerSettings.GetGraphicsAPIs(BuildTarget.Android)) &&
                    PlayerSettings.Android.targetArchitectures == architectures &&
                    PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == scriptingBackend &&
                    PlayerSettings.insecureHttpOption == insecureHttpOption &&
                    EditorUserBuildSettings.activeBuildTarget == activeBuildTarget &&
                    File.ReadAllBytes(ManifestPath).SequenceEqual(manifestBytes) &&
                    File.ReadAllBytes(ProjectSettingsPath).SequenceEqual(projectSettingsBytes) &&
                    File.ReadAllBytes(EditorBuildSettingsPath).SequenceEqual(editorBuildSettingsBytes);
                if (!restored) throw new InvalidOperationException("Temporary Android build settings were not fully restored.");
            }
        }
    }
}
