using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BMC.JawAR.SurfaceRegions.Editor
{
    internal sealed class JawSurfaceMeshCache : IDisposable
    {
        private readonly struct Edge : IEquatable<Edge>
        {
            private readonly int a;
            private readonly int b;

            public Edge(int left, int right)
            {
                a = Mathf.Min(left, right);
                b = Mathf.Max(left, right);
            }

            public bool Equals(Edge other) => a == other.a && b == other.b;
            public override bool Equals(object obj) => obj is Edge other && Equals(other);
            public override int GetHashCode() => unchecked((a * 397) ^ b);
        }

        private readonly struct EdgeOwner
        {
            public readonly int triangle;
            public readonly int slot;
            public EdgeOwner(int triangle, int slot) { this.triangle = triangle; this.slot = slot; }
        }

        private struct HeapNode
        {
            public int triangle;
            public float distance;
        }

        public Mesh Mesh { get; }
        public Vector3[] Vertices { get; }
        public int[] Triangles { get; }
        public Vector3[] TriangleCenters { get; }
        public Vector3[] TriangleNormals { get; }
        public int[] SubmeshIndexCounts { get; }
        public string Signature { get; }
        public int TriangleCount => Triangles.Length / 3;
        public int ConnectedComponentCount { get; private set; }
        public int LargestConnectedComponentTriangleCount { get; private set; }

        private readonly int[] adjacency;
        private readonly float[] bestDistances;
        private readonly int[] visitStamp;
        private readonly List<HeapNode> heap = new();
        private int currentStamp;

        public JawSurfaceMeshCache(Mesh mesh)
        {
            Mesh = mesh != null ? mesh : throw new ArgumentNullException(nameof(mesh));
            using var meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            var meshData = meshDataArray[0];
            Vertices = ReadPositions(meshData);
            SubmeshIndexCounts = new int[meshData.subMeshCount];
            Triangles = ReadFlattenedTriangles(meshData, SubmeshIndexCounts);
            TriangleCenters = new Vector3[TriangleCount];
            TriangleNormals = new Vector3[TriangleCount];
            adjacency = new int[TriangleCount * 3];
            Array.Fill(adjacency, -1);
            BuildTriangleDataAndAdjacency();
            MeasureConnectedComponents();
            bestDistances = new float[TriangleCount];
            visitStamp = new int[TriangleCount];
            Signature = ComputeSignature(Vertices, Triangles, SubmeshIndexCounts);
        }

        public void Dispose() { }

        public IReadOnlyList<int> CollectConnectedBrush(int seedTriangle, Transform meshTransform,
            float worldRadius, float normalAngleDegrees)
        {
            var result = new List<int>();
            if (seedTriangle < 0 || seedTriangle >= TriangleCount || meshTransform == null || worldRadius <= 0f)
                return result;

            currentStamp++;
            if (currentStamp == int.MaxValue)
            {
                Array.Clear(visitStamp, 0, visitStamp.Length);
                currentStamp = 1;
            }
            heap.Clear();
            SetDistance(seedTriangle, 0f);
            HeapPush(new HeapNode { triangle = seedTriangle, distance = 0f });
            var minimumDot = Mathf.Cos(Mathf.Clamp(normalAngleDegrees, 0f, 180f) * Mathf.Deg2Rad);

            while (heap.Count > 0)
            {
                var node = HeapPop();
                if (node.distance > worldRadius || node.distance > GetDistance(node.triangle) + 0.0000001f) continue;
                result.Add(node.triangle);
                var currentWorldCenter = meshTransform.TransformPoint(TriangleCenters[node.triangle]);
                for (var edgeSlot = 0; edgeSlot < 3; edgeSlot++)
                {
                    var neighbor = adjacency[node.triangle * 3 + edgeSlot];
                    if (neighbor < 0) continue;
                    if (Vector3.Dot(TriangleNormals[node.triangle], TriangleNormals[neighbor]) < minimumDot) continue;
                    var neighborWorldCenter = meshTransform.TransformPoint(TriangleCenters[neighbor]);
                    var distance = node.distance + Vector3.Distance(currentWorldCenter, neighborWorldCenter);
                    if (distance > worldRadius || distance >= GetDistance(neighbor)) continue;
                    SetDistance(neighbor, distance);
                    HeapPush(new HeapNode { triangle = neighbor, distance = distance });
                }
            }
            return result;
        }

        public void GetTriangleWorld(int triangleIndex, Transform transform, float normalOffset,
            out Vector3 a, out Vector3 b, out Vector3 c)
        {
            var offset = TransformNormal(transform, TriangleNormals[triangleIndex]) * normalOffset;
            var index = triangleIndex * 3;
            a = transform.TransformPoint(Vertices[Triangles[index]]) + offset;
            b = transform.TransformPoint(Vertices[Triangles[index + 1]]) + offset;
            c = transform.TransformPoint(Vertices[Triangles[index + 2]]) + offset;
        }

        public int GetSubmeshForTriangle(int triangleIndex)
        {
            return JawSurfaceTriangleLayout.GetSubmeshForTriangle(SubmeshIndexCounts, triangleIndex);
        }

        public Mesh BuildOverlayMesh(IReadOnlyList<int> triangleIndices, string meshName, float localNormalOffset)
        {
            if (triangleIndices == null || triangleIndices.Count == 0) return null;
            var vertices = new Vector3[triangleIndices.Count * 3];
            var normals = new Vector3[vertices.Length];
            var indices = new int[vertices.Length];
            for (var i = 0; i < triangleIndices.Count; i++)
            {
                var triangle = triangleIndices[i];
                if (triangle < 0 || triangle >= TriangleCount) continue;
                var source = triangle * 3;
                for (var corner = 0; corner < 3; corner++)
                {
                    var destination = i * 3 + corner;
                    vertices[destination] = Vertices[Triangles[source + corner]] +
                                            TriangleNormals[triangle] * localNormalOffset;
                    normals[destination] = TriangleNormals[triangle];
                    indices[destination] = destination;
                }
            }
            var overlay = new Mesh { name = meshName, indexFormat = IndexFormat.UInt32 };
            overlay.vertices = vertices;
            overlay.normals = normals;
            overlay.triangles = indices;
            overlay.RecalculateBounds();
            return overlay;
        }

        private void BuildTriangleDataAndAdjacency()
        {
            var openEdges = new Dictionary<Edge, EdgeOwner>(Triangles.Length);
            for (var triangle = 0; triangle < TriangleCount; triangle++)
            {
                var baseIndex = triangle * 3;
                var i0 = Triangles[baseIndex];
                var i1 = Triangles[baseIndex + 1];
                var i2 = Triangles[baseIndex + 2];
                var a = Vertices[i0];
                var b = Vertices[i1];
                var c = Vertices[i2];
                TriangleCenters[triangle] = (a + b + c) / 3f;
                TriangleNormals[triangle] = Vector3.Cross(b - a, c - a).normalized;
                AddEdge(openEdges, new Edge(i0, i1), triangle, 0);
                AddEdge(openEdges, new Edge(i1, i2), triangle, 1);
                AddEdge(openEdges, new Edge(i2, i0), triangle, 2);
            }
        }

        private void MeasureConnectedComponents()
        {
            var visited = new bool[TriangleCount];
            var queue = new Queue<int>();
            for (var seed = 0; seed < TriangleCount; seed++)
            {
                if (visited[seed]) continue;
                ConnectedComponentCount++;
                var size = 0;
                visited[seed] = true;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var triangle = queue.Dequeue();
                    size++;
                    for (var slot = 0; slot < 3; slot++)
                    {
                        var neighbor = adjacency[triangle * 3 + slot];
                        if (neighbor < 0 || visited[neighbor]) continue;
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
                LargestConnectedComponentTriangleCount = Mathf.Max(LargestConnectedComponentTriangleCount, size);
            }
        }

        private void AddEdge(Dictionary<Edge, EdgeOwner> openEdges, Edge edge, int triangle, int slot)
        {
            if (!openEdges.TryGetValue(edge, out var owner))
            {
                openEdges.Add(edge, new EdgeOwner(triangle, slot));
                return;
            }
            if (adjacency[owner.triangle * 3 + owner.slot] >= 0) return;
            adjacency[owner.triangle * 3 + owner.slot] = triangle;
            adjacency[triangle * 3 + slot] = owner.triangle;
        }

        private float GetDistance(int triangle) => visitStamp[triangle] == currentStamp
            ? bestDistances[triangle]
            : float.PositiveInfinity;

        private void SetDistance(int triangle, float distance)
        {
            visitStamp[triangle] = currentStamp;
            bestDistances[triangle] = distance;
        }

        private void HeapPush(HeapNode node)
        {
            heap.Add(node);
            var index = heap.Count - 1;
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (heap[parent].distance <= node.distance) break;
                heap[index] = heap[parent];
                index = parent;
            }
            heap[index] = node;
        }

        private HeapNode HeapPop()
        {
            var result = heap[0];
            var last = heap[^1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return result;
            var index = 0;
            while (true)
            {
                var left = index * 2 + 1;
                if (left >= heap.Count) break;
                var right = left + 1;
                var child = right < heap.Count && heap[right].distance < heap[left].distance ? right : left;
                if (heap[child].distance >= last.distance) break;
                heap[index] = heap[child];
                index = child;
            }
            heap[index] = last;
            return result;
        }

        private static Vector3[] ReadPositions(Mesh.MeshData meshData)
        {
            if (!meshData.HasVertexAttribute(VertexAttribute.Position) ||
                meshData.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 ||
                meshData.GetVertexAttributeDimension(VertexAttribute.Position) < 3)
                throw new InvalidOperationException("Jaw painter requires Float32 XYZ mesh positions.");
            var stream = meshData.GetVertexAttributeStream(VertexAttribute.Position);
            var offset = meshData.GetVertexAttributeOffset(VertexAttribute.Position);
            var stride = meshData.GetVertexBufferStride(stream);
            var raw = meshData.GetVertexData<byte>(stream).ToArray();
            var result = new Vector3[meshData.vertexCount];
            for (var i = 0; i < result.Length; i++)
            {
                var start = i * stride + offset;
                result[i] = new Vector3(BitConverter.ToSingle(raw, start),
                    BitConverter.ToSingle(raw, start + 4), BitConverter.ToSingle(raw, start + 8));
            }
            return result;
        }

        private static int[] ReadFlattenedTriangles(Mesh.MeshData meshData, int[] submeshIndexCounts)
        {
            var total = 0;
            for (var i = 0; i < meshData.subMeshCount; i++)
            {
                submeshIndexCounts[i] = meshData.GetSubMesh(i).indexCount;
                total += submeshIndexCounts[i];
            }
            var triangles = new int[total];
            var destination = 0;
            if (meshData.indexFormat == IndexFormat.UInt16)
            {
                var raw = meshData.GetIndexData<ushort>();
                CopySubmeshes(meshData, raw, triangles, ref destination);
            }
            else
            {
                var raw = meshData.GetIndexData<uint>();
                CopySubmeshes(meshData, raw, triangles, ref destination);
            }
            return triangles;
        }

        private static void CopySubmeshes<T>(Mesh.MeshData data, NativeArray<T> raw, int[] destination,
            ref int destinationOffset) where T : unmanaged
        {
            for (var submesh = 0; submesh < data.subMeshCount; submesh++)
            {
                var descriptor = data.GetSubMesh(submesh);
                for (var i = 0; i < descriptor.indexCount; i++)
                {
                    var value = Convert.ToUInt32(raw[descriptor.indexStart + i]);
                    destination[destinationOffset++] = checked((int)value + descriptor.baseVertex);
                }
            }
        }

        private static string ComputeSignature(Vector3[] vertices, int[] triangles, int[] submeshCounts)
        {
            using var memory = new MemoryStream(vertices.Length * 12 + triangles.Length * 4 + 128);
            using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
            {
                writer.Write(vertices.Length);
                writer.Write(triangles.Length / 3);
                writer.Write(submeshCounts.Length);
                foreach (var count in submeshCounts) writer.Write(count);
                foreach (var vertex in vertices)
                {
                    writer.Write(vertex.x); writer.Write(vertex.y); writer.Write(vertex.z);
                }
                foreach (var triangle in triangles) writer.Write(triangle);
            }
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(memory.ToArray());
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static Vector3 TransformNormal(Transform transform, Vector3 localNormal)
        {
            return transform.localToWorldMatrix.inverse.transpose.MultiplyVector(localNormal).normalized;
        }
    }
}
