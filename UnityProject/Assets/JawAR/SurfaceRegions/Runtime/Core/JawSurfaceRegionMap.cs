using System;
using System.Collections.Generic;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions
{
    [CreateAssetMenu(fileName = "JawSurfaceRegionMap", menuName = "Jaw Anatomy/Surface Region Map")]
    public sealed class JawSurfaceRegionMap : ScriptableObject
    {
        public const int CurrentDataVersion = 1;

        [Serializable]
        public sealed class RegionDefinition
        {
            [SerializeField] private string stableId;
            [SerializeField] private string displayName;
            [SerializeField] private Color displayColor = Color.cyan;
            [SerializeField] private int[] triangleIndices = Array.Empty<int>();
            [SerializeField] private Mesh bakedOverlayMesh;

            public string StableId => stableId;
            public string DisplayName => displayName;
            public Color DisplayColor => displayColor;
            public IReadOnlyList<int> TriangleIndices => triangleIndices;
            public int TriangleCount => triangleIndices?.Length ?? 0;
            public Mesh BakedOverlayMesh => bakedOverlayMesh;

            internal RegionDefinition(string id, string name, Color color)
            {
                stableId = id;
                displayName = name;
                displayColor = color;
            }

            internal bool Contains(int triangleIndex)
            {
                return triangleIndices != null && Array.BinarySearch(triangleIndices, triangleIndex) >= 0;
            }

            internal bool Add(int triangleIndex)
            {
                triangleIndices ??= Array.Empty<int>();
                var position = Array.BinarySearch(triangleIndices, triangleIndex);
                if (position >= 0) return false;
                position = ~position;
                var result = new int[triangleIndices.Length + 1];
                Array.Copy(triangleIndices, 0, result, 0, position);
                result[position] = triangleIndex;
                Array.Copy(triangleIndices, position, result, position + 1, triangleIndices.Length - position);
                triangleIndices = result;
                return true;
            }

            internal bool Remove(int triangleIndex)
            {
                if (triangleIndices == null) return false;
                var position = Array.BinarySearch(triangleIndices, triangleIndex);
                if (position < 0) return false;
                var result = new int[triangleIndices.Length - 1];
                Array.Copy(triangleIndices, 0, result, 0, position);
                Array.Copy(triangleIndices, position + 1, result, position, result.Length - position);
                triangleIndices = result;
                return true;
            }

            internal void Clear() => triangleIndices = Array.Empty<int>();
            internal void SetColor(Color value) => displayColor = value;
            internal void SetOverlayMesh(Mesh value) => bakedOverlayMesh = value;
        }

        [Flags]
        public enum MeshValidationIssue
        {
            None = 0,
            MissingMesh = 1 << 0,
            MeshReferenceChanged = 1 << 1,
            ColliderMeshDiffers = 1 << 2,
            VertexCountChanged = 1 << 3,
            TriangleCountChanged = 1 << 4,
            SubmeshCountChanged = 1 << 5,
            SubmeshStructureChanged = 1 << 6,
            SignatureChanged = 1 << 7
        }

        [SerializeField] private int dataVersion = CurrentDataVersion;
        [SerializeField] private Mesh sourceMesh;
        [SerializeField] private string sourceMeshAssetGuid;
        [SerializeField] private string sourceMeshName;
        [SerializeField] private int vertexCount;
        [SerializeField] private int triangleCount;
        [SerializeField] private int submeshCount;
        [SerializeField] private int[] submeshIndexCounts = Array.Empty<int>();
        [SerializeField] private string meshSignatureSha256;
        [SerializeField] private string createdUtc;
        [SerializeField] private string modifiedUtc;
        [SerializeField] private List<RegionDefinition> regions = new();

        public int DataVersion => dataVersion;
        public Mesh SourceMesh => sourceMesh;
        public string SourceMeshAssetGuid => sourceMeshAssetGuid;
        public string SourceMeshName => sourceMeshName;
        public int VertexCount => vertexCount;
        public int TriangleCount => triangleCount;
        public int SubmeshCount => submeshCount;
        public IReadOnlyList<int> SubmeshIndexCounts => submeshIndexCounts;
        public string MeshSignatureSha256 => meshSignatureSha256;
        public string CreatedUtc => createdUtc;
        public string ModifiedUtc => modifiedUtc;
        public IReadOnlyList<RegionDefinition> Regions => regions;

        public int TotalLabelledTriangleCount
        {
            get
            {
                var unique = new HashSet<int>();
                foreach (var region in regions)
                    foreach (var triangle in region.TriangleIndices)
                        unique.Add(triangle);
                return unique.Count;
            }
        }

        public int OverlappingTriangleCount
        {
            get
            {
                var seen = new HashSet<int>();
                var overlaps = new HashSet<int>();
                foreach (var region in regions)
                    foreach (var triangle in region.TriangleIndices)
                        if (!seen.Add(triangle)) overlaps.Add(triangle);
                return overlaps.Count;
            }
        }

        public void InitializeDefaultRegions()
        {
            if (regions.Count != 0) return;
            var definitions = new[]
            {
                "LowerIncisors", "LeftRamus", "RightRamus", "LeftCondylarProcess",
                "RightCondylarProcess", "LeftCoronoidProcess", "RightCoronoidProcess",
                "LeftMentalForamen", "RightMentalForamen", "MentalProtuberance", "AlveolarProcess",
                "LeftMasseterInsertion", "RightMasseterInsertion", "LeftTemporalisInsertion",
                "RightTemporalisInsertion", "LeftBuccinatorOrigin", "RightBuccinatorOrigin",
                "LeftDepressorAnguliOrisOrigin", "RightDepressorAnguliOrisOrigin",
                "LeftDepressorLabiiInferiorisOrigin", "RightDepressorLabiiInferiorisOrigin",
                "MentalisOrigin", "OrbicularisOrisReference"
            };
            for (var i = 0; i < definitions.Length; i++)
            {
                var hue = Mathf.Repeat(i * 0.61803398875f, 1f);
                regions.Add(new RegionDefinition(definitions[i], SplitPascalCase(definitions[i]),
                    Color.HSVToRGB(hue, 0.58f, 1f)));
            }
            var now = DateTime.UtcNow.ToString("O");
            createdUtc = now;
            modifiedUtc = now;
            dataVersion = CurrentDataVersion;
        }

        public void SetSourceMeshMetadata(Mesh mesh, string assetGuid, string signature, int[] indexCounts)
        {
            sourceMesh = mesh;
            sourceMeshAssetGuid = assetGuid ?? string.Empty;
            sourceMeshName = mesh != null ? mesh.name : string.Empty;
            vertexCount = mesh != null ? mesh.vertexCount : 0;
            submeshCount = mesh != null ? mesh.subMeshCount : 0;
            submeshIndexCounts = indexCounts != null ? (int[])indexCounts.Clone() : Array.Empty<int>();
            triangleCount = 0;
            foreach (var count in submeshIndexCounts) triangleCount += count / 3;
            meshSignatureSha256 = signature ?? string.Empty;
            Touch();
        }

        public MeshValidationIssue ValidateMesh(Mesh rendererMesh, Mesh colliderMesh, string computedSignature,
            out string message)
        {
            var issues = MeshValidationIssue.None;
            if (rendererMesh == null || colliderMesh == null)
            {
                issues |= MeshValidationIssue.MissingMesh;
                message = "Renderer mesh or MeshCollider mesh is missing.";
                return issues;
            }
            if (rendererMesh != sourceMesh) issues |= MeshValidationIssue.MeshReferenceChanged;
            if (colliderMesh != rendererMesh) issues |= MeshValidationIssue.ColliderMeshDiffers;
            if (rendererMesh.vertexCount != vertexCount) issues |= MeshValidationIssue.VertexCountChanged;
            if (rendererMesh.subMeshCount != submeshCount) issues |= MeshValidationIssue.SubmeshCountChanged;

            var currentTriangles = 0;
            var structureChanged = rendererMesh.subMeshCount != submeshIndexCounts.Length;
            for (var i = 0; i < rendererMesh.subMeshCount; i++)
            {
                var count = checked((int)rendererMesh.GetIndexCount(i));
                currentTriangles += count / 3;
                if (i >= submeshIndexCounts.Length || submeshIndexCounts[i] != count) structureChanged = true;
            }
            if (currentTriangles != triangleCount) issues |= MeshValidationIssue.TriangleCountChanged;
            if (structureChanged) issues |= MeshValidationIssue.SubmeshStructureChanged;
            if (!string.IsNullOrEmpty(computedSignature) &&
                !string.Equals(computedSignature, meshSignatureSha256, StringComparison.OrdinalIgnoreCase))
                issues |= MeshValidationIssue.SignatureChanged;

            message = issues == MeshValidationIssue.None
                ? $"Valid: {vertexCount:N0} vertices, {triangleCount:N0} triangles, {submeshCount} submesh(es)."
                : "Unsafe mesh mismatch: " + issues + ". Saved triangle labels will not be applied.";
            return issues;
        }

        public RegionDefinition GetRegion(string stableId)
        {
            return regions.Find(region => string.Equals(region.StableId, stableId, StringComparison.Ordinal));
        }

        public bool TryGetRegionForTriangle(int triangleIndex, out RegionDefinition region)
        {
            region = null;
            if (triangleIndex < 0 || triangleIndex >= triangleCount) return false;
            foreach (var candidate in regions)
            {
                if (!candidate.Contains(triangleIndex)) continue;
                region = candidate;
                return true;
            }
            return false;
        }

        public bool AssignTriangle(string stableId, int triangleIndex, bool allowReassign,
            out string previousOwnerId)
        {
            previousOwnerId = null;
            if (triangleIndex < 0 || triangleIndex >= triangleCount) return false;
            var target = GetRegion(stableId);
            if (target == null) return false;
            if (TryGetRegionForTriangle(triangleIndex, out var owner))
            {
                previousOwnerId = owner.StableId;
                if (owner == target) return false;
                if (!allowReassign) return false;
                owner.Remove(triangleIndex);
            }
            var changed = target.Add(triangleIndex);
            if (changed) Touch();
            return changed;
        }

        public bool EraseTriangle(string stableId, int triangleIndex)
        {
            var target = GetRegion(stableId);
            var changed = target != null && target.Remove(triangleIndex);
            if (changed) Touch();
            return changed;
        }

        public bool ClearRegion(string stableId)
        {
            var target = GetRegion(stableId);
            if (target == null || target.TriangleCount == 0) return false;
            target.Clear();
            Touch();
            return true;
        }

        public bool SetRegionColor(string stableId, Color color)
        {
            var target = GetRegion(stableId);
            if (target == null || target.DisplayColor == color) return false;
            target.SetColor(color);
            Touch();
            return true;
        }

        public void SetBakedOverlayMesh(string stableId, Mesh mesh)
        {
            GetRegion(stableId)?.SetOverlayMesh(mesh);
        }

        private void Touch() => modifiedUtc = DateTime.UtcNow.ToString("O");

        private static string SplitPascalCase(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var result = value[0].ToString();
            for (var i = 1; i < value.Length; i++)
                result += char.IsUpper(value[i]) ? " " + value[i] : value[i].ToString();
            return result;
        }
    }
}
