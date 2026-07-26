using BMC.JawAR.SurfaceRegions;

namespace BMC.JawAR
{
    public enum JawAnatomySelectionSource
    {
        SurfaceRegionFingertip,
        LegacyBoxFingertip
    }

    /// <summary>
    /// Shared selection result read by both fingertip feedback and voice answers, so the two
    /// never disagree about what was most recently pointed at.
    /// </summary>
    public readonly struct JawAnatomySelectionResult
    {
        public readonly JawAnatomySelectionSource Source;
        public readonly string StableId;
        public readonly string DisplayName;
        public readonly int TriangleIndex;
        public readonly JawAnatomyZone LegacyZone;
        public readonly float TimestampUnscaled;

        public JawAnatomySelectionResult(JawAnatomySelectionSource source, string stableId, string displayName,
            int triangleIndex, JawAnatomyZone legacyZone, float timestampUnscaled)
        {
            Source = source;
            StableId = stableId;
            DisplayName = displayName;
            TriangleIndex = triangleIndex;
            LegacyZone = legacyZone;
            TimestampUnscaled = timestampUnscaled;
        }

        public static JawAnatomySelectionResult FromSurfaceRegion(
            JawSurfaceRegionMap.RegionDefinition region, int triangleIndex, float timestampUnscaled)
        {
            return new JawAnatomySelectionResult(JawAnatomySelectionSource.SurfaceRegionFingertip,
                region.StableId, region.DisplayName, triangleIndex, null, timestampUnscaled);
        }

        public static JawAnatomySelectionResult FromLegacyZone(JawAnatomyZone zone, float timestampUnscaled)
        {
            return new JawAnatomySelectionResult(JawAnatomySelectionSource.LegacyBoxFingertip,
                null, zone.DisplayName, -1, zone, timestampUnscaled);
        }
    }
}
