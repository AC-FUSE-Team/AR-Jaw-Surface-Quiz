using System;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    internal static class JawSurfaceRegionAssetUtility
    {
        public const float OverlayOffset = 0.00012f;

        public static string BindMapToMesh(JawSurfaceRegionMap map, Mesh mesh, JawSurfaceMeshCache cache)
        {
            var path = AssetDatabase.GetAssetPath(mesh);
            var guid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
            map.SetSourceMeshMetadata(mesh, guid, cache.Signature, cache.SubmeshIndexCounts);
            EditorUtility.SetDirty(map);
            return guid;
        }

        public static void RebuildPersistentOverlays(JawSurfaceRegionMap map, JawSurfaceMeshCache cache)
        {
            var mapPath = AssetDatabase.GetAssetPath(map);
            if (string.IsNullOrEmpty(mapPath)) throw new InvalidOperationException("Region map must be a saved asset.");
            foreach (var region in map.Regions)
            {
                var overlay = region.BakedOverlayMesh;
                if (region.TriangleCount == 0)
                {
                    if (overlay != null)
                    {
                        map.SetBakedOverlayMesh(region.StableId, null);
                        UnityEngine.Object.DestroyImmediate(overlay, true);
                    }
                    continue;
                }

                var generated = cache.BuildOverlayMesh(region.TriangleIndices,
                    $"JawSurfaceOverlay_{region.StableId}", OverlayOffset);
                if (overlay == null)
                {
                    overlay = generated;
                    overlay.hideFlags = HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(overlay, map);
                    map.SetBakedOverlayMesh(region.StableId, overlay);
                }
                else
                {
                    EditorUtility.CopySerialized(generated, overlay);
                    UnityEngine.Object.DestroyImmediate(generated);
                    EditorUtility.SetDirty(overlay);
                }
            }
            EditorUtility.SetDirty(map);
        }
    }
}
