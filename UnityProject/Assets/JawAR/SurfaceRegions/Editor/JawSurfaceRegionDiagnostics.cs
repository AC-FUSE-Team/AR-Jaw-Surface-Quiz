using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BMC.JawAR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    public static class JawSurfaceRegionDiagnostics
    {
        [MenuItem("Tools/Jaw Anatomy/Run Surface-Region Diagnostics")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath,
                OpenSceneMode.Single);
            var target = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>();
            var coordinator = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionSelectionCoordinator>(
                FindObjectsInactive.Include);
            if (target == null || target.RendererMesh == null || target.meshCollider == null || target.regionMap == null)
                throw new InvalidOperationException("Experimental surface target is incomplete.");

            var stopwatch = Stopwatch.StartNew();
            using var cache = new JawSurfaceMeshCache(target.RendererMesh);
            stopwatch.Stop();
            var cacheMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            var sampleCount = Math.Min(5000, cache.TriangleCount);
            var sample = Enumerable.Range(0, sampleCount).ToArray();
            stopwatch.Restart();
            var sampleOverlay = cache.BuildOverlayMesh(sample, "JawSurfaceOverlayTimingSample", 0.00012f);
            stopwatch.Stop();
            var overlayMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            UnityEngine.Object.DestroyImmediate(sampleOverlay);

            stopwatch.Restart();
            foreach (var region in target.regionMap.Regions)
            {
                if (region.TriangleCount == 0) continue;
                var fullOverlay = cache.BuildOverlayMesh(region.TriangleIndices,
                    "JawSurfaceFullTiming_" + region.StableId, 0.00012f);
                UnityEngine.Object.DestroyImmediate(fullOverlay);
            }
            stopwatch.Stop();
            var currentOverlayMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            var issues = target.regionMap.ValidateMesh(target.RendererMesh, target.meshCollider.sharedMesh,
                cache.Signature, out var validation);
            var payloadBytes = (long)target.regionMap.TotalLabelledTriangleCount * sizeof(int);
            var fullPayloadBytes = (long)cache.TriangleCount * sizeof(int);
            var rawColliderTopologyBytes = (long)cache.Vertices.Length * 12L + (long)cache.Triangles.Length * 4L;
            var experimentalHitboxes = CaptureHitboxes();
            Debug.Log($"JAW_SURFACE_DIAGNOSTICS scene={scene.path} validation={validation} issues={issues} " +
                      $"vertices={cache.Vertices.Length} triangles={cache.TriangleCount} submeshes={cache.SubmeshIndexCounts.Length} " +
                      $"components={cache.ConnectedComponentCount} largestComponentTriangles={cache.LargestConnectedComponentTriangleCount} " +
                      $"signature={cache.Signature} colliderSameMesh={target.meshCollider.sharedMesh == target.RendererMesh} " +
                      $"cacheAdjacencySignatureMs={cacheMilliseconds:F2} overlaySampleTriangles={sampleCount} " +
                      $"overlaySampleMs={overlayMilliseconds:F2} currentDraftOverlayMs={currentOverlayMilliseconds:F2} " +
                      $"currentLabelPayloadBytes={payloadBytes} " +
                      $"fullLabelPayloadBytes={fullPayloadBytes} rawColliderTopologyBytes={rawColliderTopologyBytes} " +
                      $"surfaceLookupEnabled={target.surfaceLookupEnabled} coordinatorEnabled={coordinator != null && coordinator.enabled} " +
                      $"selectionMode={coordinator?.selectionMode.ToString() ?? "Missing"} labelled={target.regionMap.TotalLabelledTriangleCount} " +
                      $"overlaps={target.regionMap.OverlappingTriangleCount}");

            EditorSceneManager.OpenScene(JawSurfaceRegionExperimentalSceneSetup.WorkingScenePath, OpenSceneMode.Single);
            var originalHitboxes = CaptureHitboxes();
            Debug.Log($"JAW_SURFACE_HITBOX_COMPARE identical={experimentalHitboxes == originalHitboxes} " +
                      $"originalCount={UnityEngine.Object.FindObjectsByType<JawAnatomyZone>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length}");
        }

        private static string CaptureHitboxes()
        {
            var zones = UnityEngine.Object.FindObjectsByType<JawAnatomyZone>(FindObjectsInactive.Include,
                FindObjectsSortMode.None).OrderBy(zone => HierarchyPath(zone.transform)).ToArray();
            var result = new StringBuilder();
            foreach (var zone in zones)
            {
                var collider = zone.GetComponent<BoxCollider>();
                result.Append(HierarchyPath(zone.transform)).Append('|')
                    .Append(zone.transform.localPosition.ToString("R")).Append('|')
                    .Append(zone.transform.localRotation.ToString("R")).Append('|')
                    .Append(zone.transform.localScale.ToString("R")).Append('|')
                    .Append(zone.displayName).Append('|').Append(zone.laterality).Append('|')
                    .Append(zone.description).Append('|').Append(zone.referenceImageFile).Append('|')
                    .Append(zone.approximatePlacement).Append('|')
                    .Append(collider != null ? collider.center.ToString("R") : "NO_COLLIDER").Append('|')
                    .Append(collider != null ? collider.size.ToString("R") : "NO_COLLIDER").Append('|')
                    .Append(collider != null && collider.enabled).Append('\n');
            }
            return result.ToString();
        }

        private static string HierarchyPath(Transform transform)
        {
            var path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        public static void RunAndExit()
        {
            try { Run(); }
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
