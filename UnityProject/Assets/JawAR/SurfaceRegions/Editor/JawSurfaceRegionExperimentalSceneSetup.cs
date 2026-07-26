using System;
using System.IO;
using BMC.JawAR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    public static class JawSurfaceRegionExperimentalSceneSetup
    {
        public const string WorkingScenePath = "Assets/Scenes/JawArUcoAnatomy_AR.unity";
        public const string ExperimentalScenePath = "Assets/Scenes/JawArUcoAnatomy_SurfacePaint_AR.unity";
        public const string MapPath = "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap.asset";

        [MenuItem("Tools/Jaw Anatomy/Prepare Experimental Surface-Paint Scene")]
        public static void PrepareExperimentalScene()
        {
            if (!File.Exists(ExperimentalScenePath))
                throw new FileNotFoundException("Duplicate the working scene first.", ExperimentalScenePath);

            var scene = EditorSceneManager.OpenScene(ExperimentalScenePath, OpenSceneMode.Single);
            if (scene.path == WorkingScenePath)
                throw new InvalidOperationException("Safety stop: refusing to modify the working jaw scene.");

            var jawRoot = GameObject.Find("JawMarkerAlignedRoot");
            var jawObject = GameObject.Find("VirtualJawOverlay_MarkerAligned");
            var meshFilter = jawObject != null ? jawObject.GetComponentInChildren<MeshFilter>(true) : null;
            if (jawRoot == null || meshFilter == null || meshFilter.sharedMesh == null)
                throw new InvalidOperationException("Could not find the expected jaw root and MeshFilter in the duplicate scene.");

            var collider = meshFilter.GetComponent<MeshCollider>();
            if (collider == null) collider = Undo.AddComponent<MeshCollider>(meshFilter.gameObject);
            collider.sharedMesh = meshFilter.sharedMesh;
            collider.convex = false;
            collider.isTrigger = false;

            // NOTE: re-running this method always (re)binds target.regionMap to the empty
            // baseline (MapPath) below, even if the scene was previously pointed at the Codex
            // draft. After running this, if painted data should be active, re-run
            // JawSurfaceRegionDraftAuthoring.UseDraftMap ("Tools/Jaw Anatomy/Use Editable Codex
            // Draft Map") to restore Assets/.../JawSurfaceRegionMap_CodexDraft.asset.
            using var cache = new JawSurfaceMeshCache(meshFilter.sharedMesh);
            var map = AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(MapPath);
            if (map == null)
            {
                map = ScriptableObject.CreateInstance<JawSurfaceRegionMap>();
                map.InitializeDefaultRegions();
                JawSurfaceRegionAssetUtility.BindMapToMesh(map, meshFilter.sharedMesh, cache);
                AssetDatabase.CreateAsset(map, MapPath);
            }
            else
            {
                var issues = map.ValidateMesh(meshFilter.sharedMesh, collider.sharedMesh, cache.Signature, out var message);
                if (issues != JawSurfaceRegionMap.MeshValidationIssue.None)
                    throw new InvalidOperationException("Existing region map does not match the duplicate-scene jaw. " + message);
            }

            var experiment = jawRoot.transform.Find("SurfaceRegions_EXPERIMENTAL")?.gameObject;
            if (experiment == null)
            {
                experiment = new GameObject("SurfaceRegions_EXPERIMENTAL");
                Undo.RegisterCreatedObjectUndo(experiment, "Create surface-region experiment root");
                experiment.transform.SetParent(jawRoot.transform, false);
            }

            var target = experiment.GetComponent<JawSurfaceRegionTarget>() ??
                         Undo.AddComponent<JawSurfaceRegionTarget>(experiment);
            target.meshFilter = meshFilter;
            target.skinnedMeshRenderer = null;
            target.meshCollider = collider;
            target.regionMap = map;
            target.surfaceLookupEnabled = false;

            var feedback = experiment.GetComponent<JawSurfaceRegionFeedback>() ??
                           Undo.AddComponent<JawSurfaceRegionFeedback>(experiment);
            feedback.target = target;

            var coordinator = jawRoot.GetComponent<JawSurfaceRegionSelectionCoordinator>();
            if (coordinator == null)
            {
                coordinator = Undo.AddComponent<JawSurfaceRegionSelectionCoordinator>(jawRoot);
                coordinator.enabled = false;
            }
            else coordinator.enabled = false;
            coordinator.selectionMode = JawSurfaceRegionSelectionCoordinator.SelectionMode.ExistingBoxesOnly;
            coordinator.targetCamera = UnityEngine.Object.FindFirstObjectByType<Camera>();
            coordinator.surfaceTarget = target;
            coordinator.surfaceFeedback = feedback;
            coordinator.existingBoxController = jawRoot.GetComponent<JawAnatomyTapController>();

            // Fingertip (hand-tracked) selection routing: same "safe default off" pattern as the
            // tap coordinator above. JawSurfaceRegionPhoneBuild flips this to SurfaceThenBoxes for
            // the experimental phone build only; the persistent scene stays box-only until then.
            var fingertipPointer = UnityEngine.Object.FindFirstObjectByType<JawFingertipPointer>(FindObjectsInactive.Include);
            var voiceController = UnityEngine.Object.FindFirstObjectByType<JawVoiceQuestionController>(FindObjectsInactive.Include);
            var fingertipRouter = experiment.GetComponent<JawSurfaceFingertipRouter>() ??
                                   Undo.AddComponent<JawSurfaceFingertipRouter>(experiment);
            fingertipRouter.mode = JawSurfaceFingertipRouter.FingertipSelectionMode.ExistingBoxesOnly;
            fingertipRouter.targetCamera = coordinator.targetCamera;
            fingertipRouter.surfaceTarget = target;
            fingertipRouter.surfaceFeedback = feedback;
            fingertipRouter.fingertipPointer = fingertipPointer;
            if (fingertipPointer != null) fingertipPointer.surfaceRouter = fingertipRouter;
            if (voiceController != null) voiceController.surfaceRouter = fingertipRouter;

            EditorUtility.SetDirty(collider);
            EditorUtility.SetDirty(target);
            EditorUtility.SetDirty(feedback);
            EditorUtility.SetDirty(coordinator);
            EditorUtility.SetDirty(fingertipRouter);
            if (fingertipPointer != null) EditorUtility.SetDirty(fingertipPointer);
            if (voiceController != null) EditorUtility.SetDirty(voiceController);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ExperimentalScenePath))
                throw new IOException("Unity failed to save the experimental scene.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"JAW_SURFACE_SETUP_COMPLETE scene={ExperimentalScenePath} mesh={meshFilter.sharedMesh.name} " +
                      $"vertices={cache.Vertices.Length} triangles={cache.TriangleCount} submeshes={cache.SubmeshIndexCounts.Length} " +
                      $"signature={cache.Signature} colliderSameMesh={collider.sharedMesh == meshFilter.sharedMesh} " +
                      "selectionMode=ExistingBoxesOnly coordinatorEnabled=false surfaceLookupEnabled=false " +
                      $"fingertipRouterMode=ExistingBoxesOnly fingertipPointerWired={fingertipPointer != null} " +
                      $"voiceControllerWired={voiceController != null}");
        }

        public static void PrepareExperimentalSceneAndExit()
        {
            try { PrepareExperimentalScene(); }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        [MenuItem("Tools/Jaw Anatomy/Open Experimental Surface-Paint Scene")]
        public static void OpenExperimentalScene()
        {
            EditorSceneManager.OpenScene(ExperimentalScenePath, OpenSceneMode.Single);
        }
    }
}
