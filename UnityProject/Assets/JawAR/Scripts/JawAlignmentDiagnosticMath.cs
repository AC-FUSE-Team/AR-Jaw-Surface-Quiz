using System;
using UnityEngine;

namespace BMC.JawAR
{
    public static class JawAlignmentDiagnosticMath
    {
        public static Pose OpenCvPoseInCamera(float[] pose)
        {
            if (pose == null || pose.Length < 12)
                throw new ArgumentException("OpenCV pose must contain translation and a 3x3 rotation.", nameof(pose));
            Vector3 translation = CvVectorToUnity(pose[0], pose[1], pose[2]);
            Vector3 right = CvVectorToUnity(pose[3], pose[6], pose[9]).normalized;
            Vector3 normal = CvVectorToUnity(pose[5], pose[8], pose[11]).normalized;
            Vector3 forward = Vector3.Cross(right, normal).normalized;
            if (Vector3.Dot(normal, -translation.normalized) < 0f)
            {
                normal = -normal;
                forward = -forward;
            }
            return new Pose(translation, Quaternion.LookRotation(forward, normal));
        }

        public static Pose CameraLocalToWorld(Pose cameraWorld, Pose markerInCamera) => new(
            cameraWorld.position + cameraWorld.rotation * markerInCamera.position,
            cameraWorld.rotation * markerInCamera.rotation);

        public static float ReprojectionRmsPixels(float[] pose, double fx, double fy, double cx, double cy,
            float markerSizeMeters)
        {
            if (pose == null || pose.Length < 20) return float.NaN;
            float half = markerSizeMeters * 0.5f;
            var points = new[]
            {
                new Vector3(-half, half, 0f), new Vector3(half, half, 0f),
                new Vector3(half, -half, 0f), new Vector3(-half, -half, 0f)
            };
            double sum = 0.0;
            for (int i = 0; i < points.Length; i++)
            {
                var p = points[i];
                double x = pose[3] * p.x + pose[4] * p.y + pose[5] * p.z + pose[0];
                double y = pose[6] * p.x + pose[7] * p.y + pose[8] * p.z + pose[1];
                double z = pose[9] * p.x + pose[10] * p.y + pose[11] * p.z + pose[2];
                if (z <= 1e-8) return float.PositiveInfinity;
                double dx = fx * x / z + cx - pose[12 + i * 2];
                double dy = fy * y / z + cy - pose[13 + i * 2];
                sum += dx * dx + dy * dy;
            }
            return (float)Math.Sqrt(sum / points.Length);
        }

        public static int FindClosestTimestamp(double target, double[] timestamps, int count, double maxDifference)
        {
            int best = -1;
            double difference = double.PositiveInfinity;
            int limit = Math.Min(count, timestamps?.Length ?? 0);
            for (int i = 0; i < limit; i++)
            {
                double candidate = Math.Abs(timestamps[i] - target);
                if (candidate < difference) { difference = candidate; best = i; }
            }
            return difference <= maxDifference ? best : -1;
        }

        private static Vector3 CvVectorToUnity(float x, float y, float z) => new(x, -y, z);
    }
}
