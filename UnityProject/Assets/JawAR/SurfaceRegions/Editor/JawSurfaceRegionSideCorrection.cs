using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    public static class JawSurfaceRegionSideCorrection
    {
        public const string BackupPath =
            "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap_CodexDraft_PreSideSwapBackup.asset";

        [MenuItem("Tools/Jaw Anatomy/Correct Editable Draft Left Right (One Time)")]
        public static void CorrectWithConfirmation()
        {
            if (!EditorUtility.DisplayDialog("Correct all paired left/right surface regions?",
                    "This swaps triangle memberships and baked overlays in the editable Codex draft. " +
                    "A pre-swap backup is created first. This must be run only once.",
                    "Create Backup and Correct", "Cancel")) return;
            Correct();
        }

        public static void Correct()
        {
            var map = AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(
                JawSurfaceRegionDraftAuthoring.DraftMapPath);
            if (map == null) throw new FileNotFoundException("Editable Codex draft map is missing.");
            if (AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(BackupPath) != null)
                throw new InvalidOperationException(
                    "Safety stop: the pre-side-swap backup already exists, so correction may already be applied.");
            if (!AssetDatabase.CopyAsset(JawSurfaceRegionDraftAuthoring.DraftMapPath, BackupPath))
                throw new IOException("Could not create the pre-side-swap draft backup.");

            var suffixes = new[]
            {
                "Ramus",
                "CondylarProcess",
                "CoronoidProcess",
                "MentalForamen",
                "MasseterInsertion",
                "TemporalisInsertion",
                "BuccinatorOrigin",
                "DepressorAnguliOrisOrigin",
                "DepressorLabiiInferiorisOrigin"
            };
            foreach (var suffix in suffixes)
                SwapPair(map, "Left" + suffix, "Right" + suffix);

            EditorUtility.SetDirty(map);
            AssetDatabase.SaveAssets();
            Debug.Log($"JAW_SURFACE_LEFT_RIGHT_CORRECTED map={JawSurfaceRegionDraftAuthoring.DraftMapPath} " +
                      $"backup={BackupPath} pairedGroups={suffixes.Length} " +
                      $"labelled={map.TotalLabelledTriangleCount} overlaps={map.OverlappingTriangleCount}");
        }

        public static void CorrectAndExit()
        {
            try { Correct(); }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        private static void SwapPair(JawSurfaceRegionMap map, string leftId, string rightId)
        {
            var left = map.GetRegion(leftId);
            var right = map.GetRegion(rightId);
            if (left == null || right == null)
                throw new InvalidOperationException($"Missing paired regions {leftId}/{rightId}.");

            var leftTriangles = CopyTriangles(left);
            var rightTriangles = CopyTriangles(right);
            var leftOverlay = left.BakedOverlayMesh;
            var rightOverlay = right.BakedOverlayMesh;
            map.ClearRegion(leftId);
            map.ClearRegion(rightId);
            foreach (var triangle in rightTriangles)
                if (!map.AssignTriangle(leftId, triangle, false, out _))
                    throw new InvalidOperationException($"Could not assign triangle {triangle} to {leftId}.");
            foreach (var triangle in leftTriangles)
                if (!map.AssignTriangle(rightId, triangle, false, out _))
                    throw new InvalidOperationException($"Could not assign triangle {triangle} to {rightId}.");
            map.SetBakedOverlayMesh(leftId, rightOverlay);
            map.SetBakedOverlayMesh(rightId, leftOverlay);
        }

        private static int[] CopyTriangles(JawSurfaceRegionMap.RegionDefinition region)
        {
            var result = new int[region.TriangleCount];
            for (var index = 0; index < result.Length; index++)
                result[index] = region.TriangleIndices[index];
            return result;
        }
    }
}
