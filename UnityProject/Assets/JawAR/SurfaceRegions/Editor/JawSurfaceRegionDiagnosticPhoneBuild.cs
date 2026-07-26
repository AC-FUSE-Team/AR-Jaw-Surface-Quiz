using System;
using System.IO;
using System.Linq;
using BMC.JawAR;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    /// <summary>
    /// Diagnostic-only clone of the confirmed-working phone build (JawSurfaceRegionPhoneBuild),
    /// used to compare its actual lock/camera-pose behaviour against the quiz app's from
    /// equivalent physical test runs. Builds a separate temporary scene copy and a separate
    /// package id so this installs side by side on the phone without ever touching or overwriting
    /// the real working scene or its APK.
    /// </summary>
    public static class JawSurfaceRegionDiagnosticPhoneBuild
    {
        public const string OutputPath = "/home/omar/JawRepair/JawArUcoAnatomy_ClaudeDiagnostic_Test.apk";
        public const string PackageId = "com.omar.jawarucoanatomy.diagnostic";
        public const string ProductName = "Jaw ArUco Diagnostic Test";
        public const string VersionName = "1.3.0-working-pose-diagnostic-test";
        public const int VersionCode = 1;

        private const string TemporaryScenePath =
            "Assets/Scenes/JawArUcoAnatomy_SurfacePaint_DiagnosticBuild_TEMP.unity";
        private const string ProjectSettingsPath = "ProjectSettings/ProjectSettings.asset";
        private const string EditorBuildSettingsPath = "ProjectSettings/EditorBuildSettings.asset";

        [MenuItem("Tools/Jaw Anatomy/Build Working-App Diagnostic APK")]
        public static void Build()
        {
            if (File.Exists(OutputPath))
                throw new IOException("Safety stop: output APK already exists and will not be overwritten: " + OutputPath);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(
                    JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath) == null)
                throw new FileNotFoundException("Experimental surface scene is missing.");

            var projectSettingsBytes = File.ReadAllBytes(ProjectSettingsPath);
            var editorBuildSettingsBytes = File.ReadAllBytes(EditorBuildSettingsPath);
            var originalScenes = EditorBuildSettings.scenes;
            var originalProductName = PlayerSettings.productName;
            var originalVersionName = PlayerSettings.bundleVersion;
            var originalVersionCode = PlayerSettings.Android.bundleVersionCode;
            var originalPackageId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);

            try
            {
                AssetDatabase.DeleteAsset(TemporaryScenePath);
                if (!AssetDatabase.CopyAsset(
                        JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath, TemporaryScenePath))
                    throw new IOException("Could not create the temporary diagnostic phone-build scene.");

                var scene = EditorSceneManager.OpenScene(TemporaryScenePath, OpenSceneMode.Single);
                var target = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>();
                var coordinator = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionSelectionCoordinator>(
                    FindObjectsInactive.Include);
                var fingertipRouter = UnityEngine.Object.FindFirstObjectByType<JawSurfaceFingertipRouter>(
                    FindObjectsInactive.Include);
                var tracker = UnityEngine.Object.FindFirstObjectByType<JawOpenCvArucoTracker>();
                if (target == null || coordinator == null || fingertipRouter == null || tracker == null)
                    throw new InvalidOperationException("Temporary diagnostic scene is incomplete.");

                target.surfaceLookupEnabled = true;
                coordinator.selectionMode =
                    JawSurfaceRegionSelectionCoordinator.SelectionMode.SurfaceRegionsOnly;
                coordinator.enabled = true;
                fingertipRouter.mode = JawSurfaceFingertipRouter.FingertipSelectionMode.SurfaceRegionsOnly;

                var persistentOverlay = target.GetComponent<JawSurfaceRegionRuntimeOverlay>();
                if (persistentOverlay == null)
                    persistentOverlay = target.gameObject.AddComponent<JawSurfaceRegionRuntimeOverlay>();
                persistentOverlay.target = target;
                persistentOverlay.opacity = 0.58f;
                persistentOverlay.showOnEnable = true;

                // Exact same accuracy profile as the confirmed-working phone build and the quiz
                // build, so this is a true apples-to-apples comparison.
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

                var logger = tracker.gameObject.GetComponent<JawDiagnosticPoseLogger>() ??
                             tracker.gameObject.AddComponent<JawDiagnosticPoseLogger>();
                logger.jawTracker = tracker;

                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(coordinator);
                EditorUtility.SetDirty(fingertipRouter);
                EditorUtility.SetDirty(persistentOverlay);
                EditorUtility.SetDirty(tracker);
                EditorUtility.SetDirty(logger);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, TemporaryScenePath);

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                    !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                    throw new InvalidOperationException("Could not switch the active build target to Android.");
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(TemporaryScenePath, true) };
                PlayerSettings.productName = ProductName;
                PlayerSettings.bundleVersion = VersionName;
                PlayerSettings.Android.bundleVersionCode = VersionCode;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);

                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ??
                                          throw new InvalidOperationException("Output directory is invalid."));
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { TemporaryScenePath },
                    locationPathName = OutputPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };
                var report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException(
                        $"Diagnostic Android build failed: {report.summary.result}, " +
                        $"errors={report.summary.totalErrors}");

                if (!File.Exists(OutputPath) || new FileInfo(OutputPath).Length <= 0)
                    throw new IOException("Unity reported success but the output APK is missing or empty.");

                Debug.Log($"JAW_WORKING_DIAGNOSTIC_APK_COMPLETE path={OutputPath} " +
                          $"bytes={new FileInfo(OutputPath).Length} package={PackageId} " +
                          $"version={VersionName}({VersionCode}) accuracyProfile=1280px_24of30_4windows");
            }
            finally
            {
                EditorSceneManager.OpenScene(
                    JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath, OpenSceneMode.Single);
                AssetDatabase.DeleteAsset(TemporaryScenePath);

                EditorBuildSettings.scenes = originalScenes;
                PlayerSettings.productName = originalProductName;
                PlayerSettings.bundleVersion = originalVersionName;
                PlayerSettings.Android.bundleVersionCode = originalVersionCode;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, originalPackageId);
                File.WriteAllBytes(ProjectSettingsPath, projectSettingsBytes);
                File.WriteAllBytes(EditorBuildSettingsPath, editorBuildSettingsBytes);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                var restored = EditorBuildSettings.scenes.Select(s => $"{s.enabled}:{s.path}")
                    .SequenceEqual(originalScenes.Select(s => $"{s.enabled}:{s.path}")) &&
                    PlayerSettings.productName == originalProductName &&
                    PlayerSettings.bundleVersion == originalVersionName &&
                    PlayerSettings.Android.bundleVersionCode == originalVersionCode &&
                    PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) == originalPackageId &&
                    File.ReadAllBytes(ProjectSettingsPath).SequenceEqual(projectSettingsBytes) &&
                    File.ReadAllBytes(EditorBuildSettingsPath).SequenceEqual(editorBuildSettingsBytes);
                if (!restored)
                    throw new InvalidOperationException("Temporary diagnostic build settings were not fully restored.");
                Debug.Log("JAW_WORKING_DIAGNOSTIC_SETTINGS_RESTORED");
            }
        }

        public static void BuildAndExit()
        {
            try { Build(); }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }
    }
}
