using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    public static class JawSurfaceRegionPhoneBuild
    {
        public const string OutputPath = "build/JawArUcoAnatomy_SurfaceRegions_Test.apk";
        private const string TemporaryScenePath =
            "Assets/Scenes/JawArUcoAnatomy_SurfacePaint_PhoneBuild_TEMP.unity";

        [MenuItem("Tools/Jaw Anatomy/Build Experimental Surface APK")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(
                    JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath) == null)
                throw new FileNotFoundException("Experimental surface scene is missing.");

            try
            {
                AssetDatabase.DeleteAsset(TemporaryScenePath);
                if (!AssetDatabase.CopyAsset(
                        JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath, TemporaryScenePath))
                    throw new IOException("Could not create the temporary phone-build scene.");

                var scene = EditorSceneManager.OpenScene(TemporaryScenePath, OpenSceneMode.Single);
                var target = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>();
                var coordinator = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionSelectionCoordinator>(
                    FindObjectsInactive.Include);
                var fingertipRouter = UnityEngine.Object.FindFirstObjectByType<JawSurfaceFingertipRouter>(
                    FindObjectsInactive.Include);
                var tracker = UnityEngine.Object.FindFirstObjectByType<JawOpenCvArucoTracker>();
                if (target == null || target.regionMap == null || target.RendererMesh == null ||
                    target.meshCollider == null || coordinator == null || fingertipRouter == null || tracker == null)
                    throw new InvalidOperationException("Temporary surface scene is incomplete.");

                using (var cache = new JawSurfaceMeshCache(target.RendererMesh))
                {
                    var issues = target.regionMap.ValidateMesh(target.RendererMesh,
                        target.meshCollider.sharedMesh, cache.Signature, out var validation);
                    if (issues != JawSurfaceRegionMap.MeshValidationIssue.None)
                        throw new InvalidOperationException("Unsafe surface-map mesh mismatch: " + validation);
                }

                target.surfaceLookupEnabled = true;
                coordinator.selectionMode =
                    JawSurfaceRegionSelectionCoordinator.SelectionMode.SurfaceRegionsOnly;
                coordinator.enabled = true;

                // Painted triangles are now the only fingertip selection source. The legacy root
                // is inactive in this phone build: no renderers, colliders, zones, or fallback.
                fingertipRouter.mode = JawSurfaceFingertipRouter.FingertipSelectionMode.SurfaceRegionsOnly;
                var legacyController = coordinator.existingBoxController;
                if (legacyController != null)
                {
                    legacyController.enabled = false;
                    if (legacyController.anatomyRoot != null)
                        legacyController.anatomyRoot.gameObject.SetActive(false);
                    EditorUtility.SetDirty(legacyController);
                }
                coordinator.existingBoxController = null;
                if (fingertipRouter.fingertipPointer != null)
                {
                    fingertipRouter.fingertipPointer.tapController = null;
                    EditorUtility.SetDirty(fingertipRouter.fingertipPointer);
                }

                var persistentOverlay = target.GetComponent<JawSurfaceRegionRuntimeOverlay>();
                if (persistentOverlay == null)
                    persistentOverlay = target.gameObject.AddComponent<JawSurfaceRegionRuntimeOverlay>();
                persistentOverlay.target = target;
                persistentOverlay.opacity = 0.58f;
                persistentOverlay.showOnEnable = true;

                // Phone-test accuracy profile: allow ARCore to settle, retain a longer OpenCV pose
                // window, and require several stable windows before committing the world lock.
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

                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(coordinator);
                EditorUtility.SetDirty(fingertipRouter);
                EditorUtility.SetDirty(persistentOverlay);
                EditorUtility.SetDirty(tracker);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, TemporaryScenePath);

                Directory.CreateDirectory("build");
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
                        $"Experimental Android build failed: {report.summary.result}, " +
                        $"errors={report.summary.totalErrors}");

                Debug.Log($"JAW_SURFACE_PHONE_APK_COMPLETE path={OutputPath} " +
                          $"bytes={report.summary.totalSize} selectionMode=SurfaceRegionsOnly " +
                          $"fingertipMode=SurfaceRegionsOnly legacyBoxesActive=false " +
                          $"leftRightMembershipsCorrectedInSource=true " +
                          $"labelled={target.regionMap.TotalLabelledTriangleCount} " +
                          $"accuracyProfile=1280px_24of30_4windows");
            }
            finally
            {
                EditorSceneManager.OpenScene(
                    JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath, OpenSceneMode.Single);
                AssetDatabase.DeleteAsset(TemporaryScenePath);
                AssetDatabase.Refresh();
            }
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
    }
}
