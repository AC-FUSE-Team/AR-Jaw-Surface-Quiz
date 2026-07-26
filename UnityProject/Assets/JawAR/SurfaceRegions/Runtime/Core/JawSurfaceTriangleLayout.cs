using System.Collections.Generic;

namespace BMC.JawAR.SurfaceRegions
{
    public static class JawSurfaceTriangleLayout
    {
        public static int GetTriangleCount(IReadOnlyList<int> submeshIndexCounts)
        {
            var count = 0;
            if (submeshIndexCounts == null) return count;
            for (var i = 0; i < submeshIndexCounts.Count; i++) count += submeshIndexCounts[i] / 3;
            return count;
        }

        public static int GetFlattenedTriangleOffset(IReadOnlyList<int> submeshIndexCounts, int submesh)
        {
            if (submeshIndexCounts == null || submesh < 0 || submesh >= submeshIndexCounts.Count) return -1;
            var offset = 0;
            for (var i = 0; i < submesh; i++) offset += submeshIndexCounts[i] / 3;
            return offset;
        }

        public static int GetSubmeshForTriangle(IReadOnlyList<int> submeshIndexCounts, int triangleIndex)
        {
            if (submeshIndexCounts == null || triangleIndex < 0) return -1;
            var start = 0;
            for (var submesh = 0; submesh < submeshIndexCounts.Count; submesh++)
            {
                var count = submeshIndexCounts[submesh] / 3;
                if (triangleIndex < start + count) return submesh;
                start += count;
            }
            return -1;
        }
    }
}
