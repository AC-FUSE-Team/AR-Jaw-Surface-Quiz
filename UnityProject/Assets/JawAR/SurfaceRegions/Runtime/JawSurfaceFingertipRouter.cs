using System;
using BMC.JawAR.SurfaceRegions;
using UnityEngine;

namespace BMC.JawAR
{
    /// <summary>
    /// Routes the physical fingertip pointer to the painted surface-region map instead of the
    /// legacy anatomy box colliders. Owns its own dwell timer/status/flash so it never runs
    /// concurrently with JawFingertipPointer's box-dwell pipeline for the same frame; when it
    /// declines a frame (no painted hit, mode allows fallback) the legacy box pipeline in
    /// JawFingertipPointer runs exactly as before.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JawSurfaceFingertipRouter : MonoBehaviour
    {
        public enum FingertipSelectionMode
        {
            ExistingBoxesOnly,
            SurfaceRegionsOnly,
            SurfaceThenBoxes
        }

        public FingertipSelectionMode mode = FingertipSelectionMode.ExistingBoxesOnly;
        public Camera targetCamera;
        public JawSurfaceRegionTarget surfaceTarget;
        public JawSurfaceRegionFeedback surfaceFeedback;
        public JawFingertipPointer fingertipPointer;
        public float maxDistance = 5f;
        public float dwellSeconds = 0.65f;

        private static readonly Color PendingColor = new(1f, 0.85f, 0.1f);
        private static readonly Color SelectedColor = new(0.1f, 1f, 0.35f);

        /// <summary>Overridable clock so dwell/recency logic is deterministic in EditMode tests.</summary>
        public Func<float> TimeProvider = () => Time.unscaledTime;

        public JawSurfaceRegionMap.RegionDefinition CurrentPointedRegion { get; private set; }
        public int CurrentPointedTriangleIndex { get; private set; } = -1;
        public JawSurfaceRegionMap.RegionDefinition LastSelectedRegion { get; private set; }
        public int LastSelectedTriangleIndex { get; private set; } = -1;
        public float LastSelectedTime { get; private set; } = float.NegativeInfinity;

        private JawSurfaceRegionMap.RegionDefinition candidateRegion;
        private float candidateSince;
        private JawSurfaceRegionMap.RegionDefinition selectedRegion;

        /// <summary>
        /// Called once per pointer frame by JawFingertipPointer. Returns true when this router
        /// owns the frame (legacy box logic must be skipped), false when the legacy box pipeline
        /// should run instead (fallback).
        /// </summary>
        public bool HandlePointerFrame(Vector2 screenPoint)
        {
            if (mode == FingertipSelectionMode.ExistingBoxesOnly)
            {
                ResetCandidate();
                return false;
            }

            var camera = targetCamera != null ? targetCamera : Camera.main;
            if (camera != null && surfaceTarget != null &&
                surfaceTarget.TryRaycast(camera.ScreenPointToRay(screenPoint), maxDistance,
                    out var hit, out var region))
            {
                return ProcessSurfaceHit(true, region, hit.triangleIndex);
            }

            return ProcessSurfaceHit(false, null, -1);
        }

        /// <summary>
        /// Core dwell state machine, separated from the raycast so it can be exercised directly
        /// by tests without needing a real MeshCollider/Physics raycast.
        /// </summary>
        public bool ProcessSurfaceHit(bool hasHit, JawSurfaceRegionMap.RegionDefinition region, int triangleIndex)
        {
            if (mode == FingertipSelectionMode.ExistingBoxesOnly)
            {
                ResetCandidate();
                return false;
            }

            if (!hasHit || region == null)
            {
                ResetCandidate();
                if (mode == FingertipSelectionMode.SurfaceRegionsOnly)
                {
                    fingertipPointer?.SetPointerColorExternal(PendingColor);
                    fingertipPointer?.SetHandStatusExternal("HAND FOUND — MOVE YOUR NAIL OVER A PAINTED REGION");
                    return true;
                }
                return false;
            }

            CurrentPointedRegion = region;
            CurrentPointedTriangleIndex = triangleIndex;

            if (region != candidateRegion)
            {
                candidateRegion = region;
                candidateSince = TimeProvider();
                selectedRegion = null;
            }

            var progress = Mathf.Clamp01((TimeProvider() - candidateSince) / Mathf.Max(0.1f, dwellSeconds));
            fingertipPointer?.SetPointerColorExternal(Color.Lerp(PendingColor, SelectedColor, progress));
            fingertipPointer?.SetHandStatusExternal(
                $"POINTING: {region.DisplayName} — HOLD {Mathf.CeilToInt((1f - progress) * dwellSeconds * 10f) / 10f:F1}s");

            if (progress >= 1f && selectedRegion != region)
            {
                selectedRegion = region;
                LastSelectedRegion = region;
                LastSelectedTriangleIndex = triangleIndex;
                LastSelectedTime = TimeProvider();
                surfaceFeedback?.Flash(region);
                fingertipPointer?.SetPointerColorExternal(SelectedColor);
                fingertipPointer?.SetHandStatusExternal($"SELECTED WITH FINGER: {region.DisplayName}");
                Debug.Log(
                    $"JAW_SURFACE_FINGERTIP_SELECT: id={region.StableId} name={region.DisplayName} triangle={triangleIndex}");
            }

            return true;
        }

        /// <summary>Live pointing takes priority; otherwise a completed selection within the grace window.</summary>
        public bool TryGetSelection(float recentSelectionSeconds, out JawAnatomySelectionResult result)
        {
            if (CurrentPointedRegion != null)
            {
                result = JawAnatomySelectionResult.FromSurfaceRegion(
                    CurrentPointedRegion, CurrentPointedTriangleIndex, TimeProvider());
                return true;
            }
            if (LastSelectedRegion != null && TimeProvider() - LastSelectedTime <= recentSelectionSeconds)
            {
                result = JawAnatomySelectionResult.FromSurfaceRegion(
                    LastSelectedRegion, LastSelectedTriangleIndex, LastSelectedTime);
                return true;
            }
            result = default;
            return false;
        }

        public void ResetCandidate()
        {
            CurrentPointedRegion = null;
            CurrentPointedTriangleIndex = -1;
            candidateRegion = null;
            candidateSince = 0f;
            selectedRegion = null;
        }
    }
}
