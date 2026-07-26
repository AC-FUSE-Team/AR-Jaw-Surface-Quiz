using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    public static class JawSurfaceRegionDraftAuthoring
    {
        public const string DraftMapPath =
            "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap_CodexDraft.asset";

        [MenuItem("Tools/Jaw Anatomy/Regenerate Full Editable Codex Draft")]
        public static void GenerateDraftWithConfirmation()
        {
            if (!EditorUtility.DisplayDialog("Generate editable Codex draft?",
                    "This recreates only JawSurfaceRegionMap_CodexDraft.asset from the untouched empty baseline map " +
                    "and replaces any manual edits previously made to that draft. The working scene, boxes, and " +
                    "empty map are not changed.", "Regenerate Draft", "Cancel")) return;
            GenerateDraft();
        }

        [MenuItem("Tools/Jaw Anatomy/Use Editable Codex Draft Map")]
        public static void UseDraftMap() => AssignExperimentalMap(DraftMapPath);

        [MenuItem("Tools/Jaw Anatomy/Use Untouched Empty Baseline Map")]
        public static void UseEmptyBaselineMap() => AssignExperimentalMap(
            JawSurfaceRegionExperimentalSceneSetup.MapPath);

        public static void GenerateDraft()
        {
            var scene = EditorSceneManager.OpenScene(JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath,
                OpenSceneMode.Single);
            var target = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>();
            if (target == null || target.RendererMesh == null || target.meshCollider == null)
                throw new InvalidOperationException("Experimental jaw surface target is incomplete.");

            var baseline = AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(
                JawSurfaceRegionExperimentalSceneSetup.MapPath);
            if (baseline == null || baseline.TotalLabelledTriangleCount != 0)
                throw new InvalidOperationException("Safety stop: the expected empty baseline map is missing or no longer empty.");

            if (AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(DraftMapPath) != null)
                AssetDatabase.DeleteAsset(DraftMapPath);
            if (!AssetDatabase.CopyAsset(JawSurfaceRegionExperimentalSceneSetup.MapPath, DraftMapPath))
                throw new IOException("Could not copy the empty baseline region map.");
            AssetDatabase.ImportAsset(DraftMapPath, ImportAssetOptions.ForceSynchronousImport);
            var draft = AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(DraftMapPath);
            if (draft == null) throw new IOException("Could not load the copied Codex draft map.");

            using var cache = new JawSurfaceMeshCache(target.RendererMesh);
            var counts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            for (var triangle = 0; triangle < cache.TriangleCount; triangle++)
            {
                var center = cache.TriangleCenters[triangle];
                var normal = cache.TriangleNormals[triangle];
                var regionId = ClassifyDraftTriangle(center, normal);
                if (regionId == null || !draft.AssignTriangle(regionId, triangle, false, out _)) continue;
                counts.TryGetValue(regionId, out var count);
                counts[regionId] = count + 1;
            }

            JawSurfaceRegionAssetUtility.RebuildPersistentOverlays(draft, cache);
            target.regionMap = draft;
            target.surfaceLookupEnabled = false;
            EditorUtility.SetDirty(target);
            EditorUtility.SetDirty(draft);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath);
            AssetDatabase.SaveAssets();

            foreach (var region in draft.Regions)
                WritePreviewObj(cache, region, $"/tmp/JawSurfaceDraft_{region.StableId}.obj");
            var countParts = new System.Collections.Generic.List<string>();
            foreach (var region in draft.Regions)
                countParts.Add($"{region.StableId}={GetCount(counts, region.StableId)}");
            var countReport = string.Join(", ", countParts);
            Debug.Log($"JAW_SURFACE_DRAFT_CREATED map={DraftMapPath} regions=[{countReport}] " +
                      $"total={draft.TotalLabelledTriangleCount} overlaps={draft.OverlappingTriangleCount} " +
                      "baselinePreserved=true surfaceLookupEnabled=false");
        }

        public static void GenerateDraftAndExit()
        {
            try { GenerateDraft(); }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        private static int GetCount(System.Collections.Generic.Dictionary<string, int> counts, string id)
        {
            return counts.TryGetValue(id, out var count) ? count : 0;
        }

        private static string ClassifyDraftTriangle(Vector3 p, Vector3 n)
        {
            // Priority is deliberate: specific landmarks and attachment patches claim triangles
            // before broad bony regions. This preserves one unambiguous owner per triangle.
            if (IsLowerIncisorDraft(p)) return "LowerIncisors";

            var side = p.x < 0f ? "Left" : "Right";
            // Small paired openings on the lateral body. The ellipsoid is intentionally compact;
            // the user can enlarge or subtract it with the normal painter controls.
            if (InEllipsoid(p, new Vector3(p.x < 0f ? -0.0235f : 0.0235f,
                    0.0080f, 0.0750f), new Vector3(0.0048f, 0.0045f, 0.0060f)))
                return side + "MentalForamen";

            // Posterior superior process caps. The z split follows the notch between the anterior
            // coronoid process and posterior condylar process on this exact imported topology.
            if (p.y >= 0.0415f && p.z >= 0.1190f)
                return side + (p.z >= 0.1290f ? "CondylarProcess" : "CoronoidProcess");

            // Muscle attachment/reference patches, derived from the supplied cyan anatomy plates.
            // Surface-normal gates keep these on the visible outer/anterior cortex instead of the
            // hidden inner wall at the same XYZ coordinates.
            if (p.z >= 0.1080f && p.y <= 0.0300f &&
                (p.x < 0f ? p.x <= -0.0310f : p.x >= 0.0310f))
                return side + "MasseterInsertion";
            if (p.z >= 0.1110f && p.z < 0.1290f &&
                p.y >= 0.0270f && p.y < 0.0450f)
                return side + "TemporalisInsertion";
            if (p.z >= 0.0780f && p.z < 0.1020f &&
                p.y >= 0.0120f && p.y < 0.0225f && Mathf.Abs(p.x) >= 0.0210f)
                return side + "BuccinatorOrigin";
            if (p.z >= 0.0620f && p.z < 0.0780f &&
                p.y >= 0.0050f && p.y < 0.0145f && Mathf.Abs(p.x) >= 0.0200f)
                return side + "DepressorAnguliOrisOrigin";
            if (p.z >= 0.0550f && p.z < 0.0690f &&
                p.y >= 0.0030f && p.y < 0.0125f && Mathf.Abs(p.x) >= 0.0110f && Mathf.Abs(p.x) < 0.0230f)
                return side + "DepressorLabiiInferiorisOrigin";

            // Midline anterior regions use the anterior-facing normal (-Z). The orbicularis entry
            // is a reference band rather than a claimed muscle insertion into the mandible.
            if (Mathf.Abs(p.x) < 0.0105f && p.z < 0.0635f &&
                p.y >= 0.0060f && p.y < 0.0145f)
                return "MentalisOrigin";
            if (Mathf.Abs(p.x) < 0.0180f && p.z < 0.0665f &&
                p.y >= 0.0145f && p.y < 0.0205f)
                return "OrbicularisOrisReference";
            if (Mathf.Abs(p.x) < 0.0155f && p.z < 0.0625f && p.y < 0.0060f)
                return "MentalProtuberance";

            // Tooth-bearing crest/body, excluding the named lower incisors and specific patches.
            if (p.y >= 0.0140f && p.y < 0.0345f && p.z >= 0.0610f && p.z < 0.1110f)
                return "AlveolarProcess";

            // Broad posterior plates are last so specific processes and attachments remain queryable.
            if ((p.x < 0f ? p.x <= -0.0260f : p.x >= 0.0260f) &&
                p.y >= -0.0040f && p.y < 0.0430f && p.z >= 0.1000f)
                return side + "Ramus";

            return null;
        }

        private static bool InEllipsoid(Vector3 point, Vector3 center, Vector3 radius)
        {
            var d = point - center;
            return d.x * d.x / (radius.x * radius.x) +
                   d.y * d.y / (radius.y * radius.y) +
                   d.z * d.z / (radius.z * radius.z) <= 1f;
        }

        private static bool IsLowerIncisorDraft(Vector3 center)
        {
            // Four central lower tooth crowns on this marker-aligned mesh. Bounds intentionally
            // stop above the alveolar body and before the canine/lateral-body surfaces.
            return Mathf.Abs(center.x) <= 0.0115f &&
                   center.y >= 0.0175f && center.y <= 0.0315f &&
                   center.z >= 0.0520f && center.z <= 0.0715f;
        }

        private static void AssignExperimentalMap(string mapPath)
        {
            var scene = EditorSceneManager.OpenScene(JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath,
                OpenSceneMode.Single);
            var target = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>();
            var map = AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(mapPath);
            if (target == null || map == null) throw new InvalidOperationException("Target or requested map is missing.");
            target.regionMap = map;
            target.surfaceLookupEnabled = false;
            EditorUtility.SetDirty(target);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, JawSurfaceRegionExperimentalSceneSetup.ExperimentalScenePath);
            Debug.Log($"JAW_SURFACE_MAP_ASSIGNED scene={scene.path} map={mapPath} " +
                      $"labelled={map.TotalLabelledTriangleCount} surfaceLookupEnabled=false");
        }

        private static void WritePreviewObj(JawSurfaceMeshCache cache,
            JawSurfaceRegionMap.RegionDefinition region, string path)
        {
            using var writer = new StreamWriter(path, false);
            writer.WriteLine("# Temporary Codex draft overlay preview; not a Unity source asset.");
            writer.WriteLine("o " + region.StableId);
            var vertexIndex = 1;
            foreach (var triangle in region.TriangleIndices)
            {
                var source = triangle * 3;
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = cache.Vertices[cache.Triangles[source + corner]] +
                                 cache.TriangleNormals[triangle] * JawSurfaceRegionAssetUtility.OverlayOffset;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "v {0:R} {1:R} {2:R}",
                        vertex.x, vertex.y, vertex.z));
                }
                writer.WriteLine($"f {vertexIndex} {vertexIndex + 1} {vertexIndex + 2}");
                vertexIndex += 3;
            }
        }
    }
}
