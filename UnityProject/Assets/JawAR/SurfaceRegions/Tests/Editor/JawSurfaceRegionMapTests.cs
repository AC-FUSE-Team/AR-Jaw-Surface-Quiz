using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.SurfaceRegions.Tests
{
    public sealed class JawSurfaceRegionMapTests
    {
        private JawSurfaceRegionMap map;
        private Mesh mesh;

        [SetUp]
        public void SetUp()
        {
            mesh = new Mesh { name = "TwoSubmeshTestJaw" };
            mesh.vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.up,
                Vector3.forward, Vector3.right + Vector3.forward, Vector3.up + Vector3.forward
            };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            map = ScriptableObject.CreateInstance<JawSurfaceRegionMap>();
            map.InitializeDefaultRegions();
            map.SetSourceMeshMetadata(mesh, "test-guid", "signature-a", new[] { 3, 3 });
        }

        [TearDown]
        public void TearDown()
        {
            if (map != null && !AssetDatabase.Contains(map)) Object.DestroyImmediate(map);
            if (mesh != null) Object.DestroyImmediate(mesh);
        }

        [Test]
        public void RegionCreation_ContainsAllInitialEmptyRegions()
        {
            Assert.AreEqual(23, map.Regions.Count);
            Assert.NotNull(map.GetRegion("LowerIncisors"));
            Assert.NotNull(map.GetRegion("LeftRamus"));
            Assert.AreEqual(0, map.TotalLabelledTriangleCount);
        }

        [Test]
        public void TriangleAssignmentAndErasure_RoundTrip()
        {
            Assert.True(map.AssignTriangle("LowerIncisors", 0, false, out _));
            Assert.AreEqual("LowerIncisors", GetOwner(0));
            Assert.True(map.EraseTriangle("LowerIncisors", 0));
            Assert.False(map.TryGetRegionForTriangle(0, out _));
        }

        [Test]
        public void OneLabelPerTriangle_RejectsImplicitReassignment()
        {
            Assert.True(map.AssignTriangle("LowerIncisors", 0, false, out _));
            Assert.False(map.AssignTriangle("LeftRamus", 0, false, out var previous));
            Assert.AreEqual("LowerIncisors", previous);
            Assert.AreEqual("LowerIncisors", GetOwner(0));
            Assert.AreEqual(0, map.OverlappingTriangleCount);
        }

        [Test]
        public void ExplicitReassignment_MovesOwnership()
        {
            map.AssignTriangle("LowerIncisors", 0, false, out _);
            Assert.True(map.AssignTriangle("LeftRamus", 0, true, out var previous));
            Assert.AreEqual("LowerIncisors", previous);
            Assert.AreEqual("LeftRamus", GetOwner(0));
            Assert.AreEqual(0, map.GetRegion("LowerIncisors").TriangleCount);
        }

        [Test]
        public void InvalidTriangleIndex_IsRejected()
        {
            Assert.False(map.AssignTriangle("LowerIncisors", -1, false, out _));
            Assert.False(map.AssignTriangle("LowerIncisors", 2, false, out _));
            Assert.False(map.TryGetRegionForTriangle(2, out _));
        }

        [Test]
        public void FlattenedSubmeshMapping_UsesSubmeshOrder()
        {
            var counts = new[] { 6, 9, 3 };
            Assert.AreEqual(2, JawSurfaceTriangleLayout.GetFlattenedTriangleOffset(counts, 1));
            Assert.AreEqual(5, JawSurfaceTriangleLayout.GetFlattenedTriangleOffset(counts, 2));
            Assert.AreEqual(0, JawSurfaceTriangleLayout.GetSubmeshForTriangle(counts, 1));
            Assert.AreEqual(1, JawSurfaceTriangleLayout.GetSubmeshForTriangle(counts, 4));
            Assert.AreEqual(2, JawSurfaceTriangleLayout.GetSubmeshForTriangle(counts, 5));
            Assert.AreEqual(6, JawSurfaceTriangleLayout.GetTriangleCount(counts));
        }

        [Test]
        public void MeshSignatureValidation_RejectsChangedSignatureAndSubmeshStructure()
        {
            var issues = map.ValidateMesh(mesh, mesh, "signature-b", out _);
            Assert.AreNotEqual(0, issues & JawSurfaceRegionMap.MeshValidationIssue.SignatureChanged);
            mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 0 }, 0);
            issues = map.ValidateMesh(mesh, mesh, "signature-a", out _);
            Assert.AreNotEqual(0, issues & JawSurfaceRegionMap.MeshValidationIssue.SubmeshStructureChanged);
            Assert.AreNotEqual(0, issues & JawSurfaceRegionMap.MeshValidationIssue.TriangleCountChanged);
        }

        [Test]
        public void SerializationAndReload_PreservesStableLookup()
        {
            const string folder = "Assets/JawAR/SurfaceRegions/Tests/Temp";
            const string path = folder + "/SerializationMap.asset";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets/JawAR/SurfaceRegions/Tests", "Temp");
            try
            {
                map.AssignTriangle("LowerIncisors", 1, false, out _);
                AssetDatabase.CreateAsset(map, path);
                AssetDatabase.SaveAssets();
                map = null;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var reloaded = AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(path);
                Assert.NotNull(reloaded);
                Assert.True(reloaded.TryGetRegionForTriangle(1, out var region));
                Assert.AreEqual("LowerIncisors", region.StableId);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void UndoRedo_RestoresTriangleAssignment()
        {
            Undo.RecordObject(map, "Test surface paint");
            map.AssignTriangle("LowerIncisors", 0, false, out _);
            Undo.FlushUndoRecordObjects();
            Assert.AreEqual("LowerIncisors", GetOwner(0));
            Undo.PerformUndo();
            Assert.False(map.TryGetRegionForTriangle(0, out _));
            Undo.PerformRedo();
            Assert.AreEqual("LowerIncisors", GetOwner(0));
        }

        private string GetOwner(int triangle)
        {
            Assert.True(map.TryGetRegionForTriangle(triangle, out var region));
            return region.StableId;
        }
    }
}
