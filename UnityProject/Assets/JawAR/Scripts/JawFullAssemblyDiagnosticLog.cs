using System.Globalization;
using System.Text;

namespace BMC.JawAR
{
    /// <summary>
    /// Pure, scene-independent helpers for the full-assembly calibration diagnostic:
    /// the per-sample pose/timestamp CSV schema and the UNVERIFIED_DIAGNOSTIC_CANDIDATE
    /// JSON export schema. Kept separate from the MonoBehaviour so both can be exercised
    /// directly by EditMode tests without a live AR session.
    /// </summary>
    public static class JawFullAssemblyDiagnosticLog
    {
        public const string CandidateStatus = "UNVERIFIED_DIAGNOSTIC_CANDIDATE";

        public static string PoseCsvHeader() =>
            "monotonic_s,cpu_image_timestamp_s,ar_pose_timestamp_s,aruco_detection_timestamp_s,pose_applied_timestamp_s," +
            "frame_timestamp_difference_ms,detection_latency_ms,frame_processing_ms,approx_fps," +
            "ar_session_state,tracking_state,finger_processing_enabled,consecutive_accepted_samples," +
            "marker_corner_0_x,marker_corner_0_y,marker_corner_1_x,marker_corner_1_y," +
            "marker_corner_2_x,marker_corner_2_y,marker_corner_3_x,marker_corner_3_y," +
            "reprojection_rms_px,detection_confidence," +
            "raw_px,raw_py,raw_pz,raw_qx,raw_qy,raw_qz,raw_qw," +
            "filtered_px,filtered_py,filtered_pz,filtered_qx,filtered_qy,filtered_qz,filtered_qw," +
            "locked_px,locked_py,locked_pz,locked_qx,locked_qy,locked_qz,locked_qw," +
            "adjust_x_mm,adjust_y_mm,adjust_z_mm,adjust_pitch_deg,adjust_yaw_deg,adjust_roll_deg,adjust_scale";

        public static string PoseCsvRow(
            double monotonicSeconds, double cpuImageTimestampSeconds, double arPoseTimestampSeconds,
            double arucoDetectionTimestampSeconds, double poseAppliedTimestampSeconds,
            double frameTimestampDifferenceMs, double detectionLatencyMs, double frameProcessingMs, double approxFps,
            string arSessionState, string trackingState, bool fingerProcessingEnabled, int consecutiveAcceptedSamples,
            float[] markerCornersPixels, float reprojectionRmsPixels, float detectionConfidence,
            PoseSample raw, PoseSample filtered, PoseSample locked,
            float adjustXMeters, float adjustYMeters, float adjustZMeters,
            float adjustPitchDeg, float adjustYawDeg, float adjustRollDeg, float adjustScale)
        {
            var row = new StringBuilder(768);
            row.Append(F(monotonicSeconds)).Append(',').Append(F(cpuImageTimestampSeconds)).Append(',')
                .Append(F(arPoseTimestampSeconds)).Append(',').Append(F(arucoDetectionTimestampSeconds)).Append(',')
                .Append(F(poseAppliedTimestampSeconds)).Append(',').Append(F(frameTimestampDifferenceMs)).Append(',')
                .Append(F(detectionLatencyMs)).Append(',').Append(F(frameProcessingMs)).Append(',').Append(F(approxFps)).Append(',')
                .Append(arSessionState).Append(',').Append(trackingState).Append(',')
                .Append(fingerProcessingEnabled ? "true" : "false").Append(',').Append(consecutiveAcceptedSamples).Append(',');
            for (int i = 0; i < 4; i++)
            {
                float x = markerCornersPixels != null && markerCornersPixels.Length > i * 2 ? markerCornersPixels[i * 2] : float.NaN;
                float y = markerCornersPixels != null && markerCornersPixels.Length > i * 2 + 1 ? markerCornersPixels[i * 2 + 1] : float.NaN;
                row.Append(F(x)).Append(',').Append(F(y)).Append(',');
            }
            row.Append(F(reprojectionRmsPixels)).Append(',').Append(F(detectionConfidence)).Append(',');
            AppendPose(row, raw);
            AppendPose(row, filtered);
            AppendPose(row, locked);
            row.Append(F(adjustXMeters * 1000.0)).Append(',').Append(F(adjustYMeters * 1000.0)).Append(',')
                .Append(F(adjustZMeters * 1000.0)).Append(',').Append(F(adjustPitchDeg)).Append(',')
                .Append(F(adjustYawDeg)).Append(',').Append(F(adjustRollDeg)).Append(',').Append(F(adjustScale));
            return row.ToString();
        }

        private static void AppendPose(StringBuilder row, PoseSample pose)
        {
            row.Append(F(pose.px)).Append(',').Append(F(pose.py)).Append(',').Append(F(pose.pz)).Append(',')
                .Append(F(pose.qx)).Append(',').Append(F(pose.qy)).Append(',').Append(F(pose.qz)).Append(',')
                .Append(F(pose.qw)).Append(',');
        }

        public readonly struct PoseSample
        {
            public readonly float px, py, pz, qx, qy, qz, qw;
            public PoseSample(float px, float py, float pz, float qx, float qy, float qz, float qw)
            {
                this.px = px; this.py = py; this.pz = pz;
                this.qx = qx; this.qy = qy; this.qz = qz; this.qw = qw;
            }
            public static readonly PoseSample Empty = new PoseSample(
                float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
        }

        public readonly struct ViewObservation
        {
            public readonly string viewLabel;
            public readonly string capturedAtUtc;
            public readonly PoseSample markerPose;
            public readonly float reprojectionRmsPixels;
            public readonly float frameProcessingMs;
            public readonly string visibleLayers;
            public readonly bool fingerProcessingEnabled;
            public readonly string screenshotPath;
            public readonly float adjustXMeters, adjustYMeters, adjustZMeters;
            public readonly float adjustPitchDeg, adjustYawDeg, adjustRollDeg, adjustScale;

            public ViewObservation(string viewLabel, string capturedAtUtc, PoseSample markerPose,
                float reprojectionRmsPixels, float frameProcessingMs, string visibleLayers,
                bool fingerProcessingEnabled, string screenshotPath,
                float adjustXMeters, float adjustYMeters, float adjustZMeters,
                float adjustPitchDeg, float adjustYawDeg, float adjustRollDeg, float adjustScale)
            {
                this.viewLabel = viewLabel;
                this.capturedAtUtc = capturedAtUtc;
                this.markerPose = markerPose;
                this.reprojectionRmsPixels = reprojectionRmsPixels;
                this.frameProcessingMs = frameProcessingMs;
                this.visibleLayers = visibleLayers;
                this.fingerProcessingEnabled = fingerProcessingEnabled;
                this.screenshotPath = screenshotPath;
                this.adjustXMeters = adjustXMeters;
                this.adjustYMeters = adjustYMeters;
                this.adjustZMeters = adjustZMeters;
                this.adjustPitchDeg = adjustPitchDeg;
                this.adjustYawDeg = adjustYawDeg;
                this.adjustRollDeg = adjustRollDeg;
                this.adjustScale = adjustScale;
            }
        }

        /// <summary>
        /// Builds the UNVERIFIED_DIAGNOSTIC_CANDIDATE export JSON. Pure string construction
        /// (no JsonUtility/Newtonsoft dependency) so the exact schema is directly testable.
        /// This never writes to disk itself and never touches any production calibration file
        /// -- callers own where (if anywhere) the returned string is saved.
        /// </summary>
        public static string BuildCandidateJson(
            string cadMetadataHashSha256, string diagnosticBuildVersion,
            string markerDictionary, int markerId, float markerBlackSquareMeters,
            float markerToPlaqueTranslationMeters, float plaqueToJawTableOffsetMeters, string plaqueToJawBaseline,
            float candidateXMeters, float candidateYMeters, float candidateZMeters,
            float candidatePitchDeg, float candidateYawDeg, float candidateRollDeg, float candidateUniformScale,
            float jawOnlyXMeters, float jawOnlyYMeters, float jawOnlyZMeters,
            float jawOnlyPitchDeg, float jawOnlyYawDeg, float jawOnlyRollDeg, float jawOnlyScale,
            bool fingerProcessingEnabled, string creationTimestampUtc, string poseLogPath,
            System.Collections.Generic.IReadOnlyList<ViewObservation> views)
        {
            var json = new StringBuilder(2048);
            json.Append("{\n");
            json.Append("  \"status\": \"").Append(CandidateStatus).Append("\",\n");
            json.Append("  \"diagnostic_build_version\": \"").Append(Escape(diagnosticBuildVersion)).Append("\",\n");
            json.Append("  \"source_cad_metadata_sha256\": \"").Append(Escape(cadMetadataHashSha256)).Append("\",\n");
            json.Append("  \"marker\": {\n");
            json.Append("    \"dictionary\": \"").Append(Escape(markerDictionary)).Append("\",\n");
            json.Append("    \"id\": ").Append(markerId).Append(",\n");
            json.Append("    \"black_square_size_meters\": ").Append(F(markerBlackSquareMeters)).Append('\n');
            json.Append("  },\n");
            json.Append("  \"marker_to_plaque_transform\": {\n");
            json.Append("    \"translation_meters\": [0, 0, 0],\n");
            json.Append("    \"rotation_euler_deg\": [0, 0, 0],\n");
            json.Append("    \"note\": \"CAD authors marker and plaque in one shared coordinate system; no separate transform in ArUco_pose_metadata.json.\"\n");
            json.Append("  },\n");
            json.Append("  \"plaque_to_jaw_transform\": {\n");
            json.Append("    \"baseline\": \"").Append(Escape(plaqueToJawBaseline)).Append("\",\n");
            json.Append("    \"table_offset_y_meters\": ").Append(F(plaqueToJawTableOffsetMeters)).Append('\n');
            json.Append("  },\n");
            json.Append("  \"candidate_correction_transform\": {\n");
            json.Append("    \"applies_to\": \"marker_and_plaque_and_jaw_together\",\n");
            json.Append("    \"translation_meters\": [").Append(F(candidateXMeters)).Append(", ")
                .Append(F(candidateYMeters)).Append(", ").Append(F(candidateZMeters)).Append("],\n");
            json.Append("    \"rotation_euler_deg_pitch_yaw_roll\": [").Append(F(candidatePitchDeg)).Append(", ")
                .Append(F(candidateYawDeg)).Append(", ").Append(F(candidateRollDeg)).Append("],\n");
            json.Append("    \"uniform_scale\": ").Append(F(candidateUniformScale)).Append('\n');
            json.Append("  },\n");
            json.Append("  \"expert_jaw_only_adjustment\": {\n");
            json.Append("    \"applies_to\": \"jaw_only_separate_from_marker_plaque_correction\",\n");
            json.Append("    \"translation_meters\": [").Append(F(jawOnlyXMeters)).Append(", ")
                .Append(F(jawOnlyYMeters)).Append(", ").Append(F(jawOnlyZMeters)).Append("],\n");
            json.Append("    \"rotation_euler_deg_pitch_yaw_roll\": [").Append(F(jawOnlyPitchDeg)).Append(", ")
                .Append(F(jawOnlyYawDeg)).Append(", ").Append(F(jawOnlyRollDeg)).Append("],\n");
            json.Append("    \"uniform_scale\": ").Append(F(jawOnlyScale)).Append('\n');
            json.Append("  },\n");
            json.Append("  \"final_composed_marker_to_jaw_transform_note\": ")
                .Append("\"plaque_to_jaw_transform (with table_offset_y) composed with candidate_correction_transform, then expert_jaw_only_adjustment applied last and only to the jaw layer\",\n");
            json.Append("  \"finger_processing_enabled\": ").Append(fingerProcessingEnabled ? "true" : "false").Append(",\n");
            json.Append("  \"pose_log_path\": \"").Append(Escape(poseLogPath ?? string.Empty)).Append("\",\n");
            json.Append("  \"creation_timestamp_utc\": \"").Append(Escape(creationTimestampUtc)).Append("\",\n");
            json.Append("  \"view_observations\": [\n");
            if (views != null)
            {
                for (int i = 0; i < views.Count; i++)
                {
                    var v = views[i];
                    json.Append("    {\n");
                    json.Append("      \"view\": \"").Append(Escape(v.viewLabel)).Append("\",\n");
                    json.Append("      \"captured_at_utc\": \"").Append(Escape(v.capturedAtUtc)).Append("\",\n");
                    json.Append("      \"marker_pose\": {\n");
                    json.Append("        \"position_meters\": [").Append(F(v.markerPose.px)).Append(", ")
                        .Append(F(v.markerPose.py)).Append(", ").Append(F(v.markerPose.pz)).Append("],\n");
                    json.Append("        \"rotation_quaternion\": [").Append(F(v.markerPose.qx)).Append(", ")
                        .Append(F(v.markerPose.qy)).Append(", ").Append(F(v.markerPose.qz)).Append(", ")
                        .Append(F(v.markerPose.qw)).Append("]\n");
                    json.Append("      },\n");
                    json.Append("      \"reprojection_rms_pixels\": ").Append(F(v.reprojectionRmsPixels)).Append(",\n");
                    json.Append("      \"frame_processing_ms\": ").Append(F(v.frameProcessingMs)).Append(",\n");
                    json.Append("      \"visible_layers\": \"").Append(Escape(v.visibleLayers)).Append("\",\n");
                    json.Append("      \"finger_processing_enabled\": ").Append(v.fingerProcessingEnabled ? "true" : "false").Append(",\n");
                    json.Append("      \"adjustment_at_capture\": {\n");
                    json.Append("        \"translation_meters\": [").Append(F(v.adjustXMeters)).Append(", ")
                        .Append(F(v.adjustYMeters)).Append(", ").Append(F(v.adjustZMeters)).Append("],\n");
                    json.Append("        \"rotation_euler_deg_pitch_yaw_roll\": [").Append(F(v.adjustPitchDeg)).Append(", ")
                        .Append(F(v.adjustYawDeg)).Append(", ").Append(F(v.adjustRollDeg)).Append("],\n");
                    json.Append("        \"uniform_scale\": ").Append(F(v.adjustScale)).Append('\n');
                    json.Append("      },\n");
                    json.Append("      \"screenshot_path\": \"").Append(Escape(v.screenshotPath ?? string.Empty)).Append("\"\n");
                    json.Append("    }").Append(i < views.Count - 1 ? "," : "").Append('\n');
                }
            }
            json.Append("  ]\n");
            json.Append("}\n");
            return json.ToString();
        }

        private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }
    }
}
