using System;
using System.Collections.Generic;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions
{
    public sealed class JawSurfaceRegionTarget : MonoBehaviour
    {
        public MeshFilter meshFilter;
        public SkinnedMeshRenderer skinnedMeshRenderer;
        public MeshCollider meshCollider;
        public JawSurfaceRegionMap regionMap;
        [Tooltip("Experimental gate. Leave off until the mesh/collider diagnostic passes.")]
        public bool surfaceLookupEnabled;

        private Dictionary<int, JawSurfaceRegionMap.RegionDefinition> lookup;

        public Mesh RendererMesh => meshFilter != null
            ? meshFilter.sharedMesh
            : skinnedMeshRenderer != null ? skinnedMeshRenderer.sharedMesh : null;

        private void Awake() => RebuildLookup();

        public void RebuildLookup()
        {
            lookup = new Dictionary<int, JawSurfaceRegionMap.RegionDefinition>();
            if (regionMap == null) return;
            foreach (var region in regionMap.Regions)
                foreach (var triangle in region.TriangleIndices)
                    lookup.TryAdd(triangle, region);
        }

        public bool TryGetRegion(RaycastHit hit, out JawSurfaceRegionMap.RegionDefinition region)
        {
            region = null;
            if (!surfaceLookupEnabled || regionMap == null || meshCollider == null ||
                hit.collider != meshCollider || meshCollider.sharedMesh != RendererMesh ||
                hit.triangleIndex < 0 || hit.triangleIndex >= regionMap.TriangleCount)
                return false;
            lookup ??= new Dictionary<int, JawSurfaceRegionMap.RegionDefinition>();
            if (lookup.Count == 0 && regionMap.TotalLabelledTriangleCount > 0) RebuildLookup();
            return lookup.TryGetValue(hit.triangleIndex, out region);
        }

        public bool TryRaycast(Ray ray, float maxDistance, out RaycastHit hit,
            out JawSurfaceRegionMap.RegionDefinition region)
        {
            hit = default;
            region = null;
            return meshCollider != null && meshCollider.Raycast(ray, out hit, maxDistance) &&
                   TryGetRegion(hit, out region);
        }
    }
}
