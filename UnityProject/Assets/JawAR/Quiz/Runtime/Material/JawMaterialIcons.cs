using System.Collections.Generic;
using UnityEngine;

namespace BMC.JawAR.Quiz.Material3
{
    /// <summary>
    /// Looks up Material Symbols-derived icon sprites (see Material3/THIRD_PARTY_NOTICES.md for
    /// source/license) by name, caching each after its first load.
    /// </summary>
    public static class JawMaterialIcons
    {
        public const string Menu = "menu";
        public const string Close = "close";
        public const string VolumeOff = "volume_off";
        public const string VolumeUp = "volume_up";
        public const string Repeat = "repeat";
        public const string Lightbulb = "lightbulb";
        public const string SkipNext = "skip_next";
        public const string ArrowForward = "arrow_forward";
        public const string Person = "person";
        public const string Wifi = "wifi";
        public const string CloudOff = "cloud_off";
        public const string Visibility = "visibility";
        public const string Build = "build";
        public const string CheckCircle = "check_circle";
        public const string Error = "error";
        public const string Warning = "warning";

        private static readonly Dictionary<string, Sprite> Cache = new();

        public static Sprite Get(string iconName)
        {
            if (Cache.TryGetValue(iconName, out var cached) && cached != null) return cached;

            var sprite = Resources.Load<Sprite>($"JawMaterialIcons/{iconName}");
            if (sprite == null)
                Debug.LogWarning($"JawMaterialIcons: icon '{iconName}' not found under Resources/JawMaterialIcons.");
            Cache[iconName] = sprite;
            return sprite;
        }
    }
}
