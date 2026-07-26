using TMPro;
using UnityEngine;

namespace BMC.JawAR.Quiz.Material3
{
    /// <summary>
    /// Material 3-inspired design tokens for the quiz UI: a Unity-native interpretation, not the
    /// official Google Material Components/Compose library. "Expressive" direction chosen after
    /// comparing three static mockups (see Artifacts/QuizMaterial3Redesign_20260722) — a deep
    /// teal/violet duotone with warm coral accents, generous rounding, and legibility safeguards
    /// (opaque scrims, icon+text pairing) since the real backdrop is an unpredictable AR camera
    /// feed, not the single reference photo used for the mockups.
    /// </summary>
    public static class JawMaterialTheme
    {
        // ---- Color tokens -------------------------------------------------
        public static readonly Color Primary = HexColor("#00A896");
        public static readonly Color OnPrimary = HexColor("#00201C");
        public static readonly Color Secondary = HexColor("#7C6BF2");
        public static readonly Color OnSecondary = HexColor("#FFFFFF");
        public static readonly Color Tertiary = HexColor("#FF8A5C");
        public static readonly Color OnTertiary = HexColor("#3A1400");

        public static readonly Color Success = HexColor("#4CD787");
        public static readonly Color Error = HexColor("#FF6B6B");
        public static readonly Color Warning = HexColor("#FFB020");
        public static readonly Color Info = HexColor("#7FB2FF");

        public static readonly Color Surface = HexColor("#0E101C", 0.80f);
        public static readonly Color SurfaceContainer = HexColor("#1E1A36", 0.90f);
        public static readonly Color SurfaceElevated = HexColor("#261F40", 0.97f);
        public static readonly Color Scrim = HexColor("#000000", 0.55f);

        public static readonly Color OnSurface = HexColor("#F5F3FF");
        public static readonly Color OnSurfaceVariant = HexColor("#CFC9E8");
        public static readonly Color OutlineFaint = HexColor("#FFFFFF", 0.14f);

        // ---- Spacing tokens (px at the UI's authored reference resolution) -
        public const float SpaceXs = 8f;
        public const float SpaceSm = 12f;
        public const float SpaceMd = 16f;
        public const float SpaceLg = 24f;
        public const float SpaceXl = 32f;

        // ---- Shape tokens ---------------------------------------------------
        public const float RadiusSmall = 14f;
        public const float RadiusMedium = 22f;
        public const float RadiusLarge = 32f;
        public const float RadiusPill = 999f;

        // ---- Touch target -----------------------------------------------
        public const float MinTouchTarget = 88f; // ~48dp-equivalent at this UI's working scale

        // ---- Motion tokens (seconds) --------------------------------------
        public const float MotionFast = 0.15f;
        public const float MotionMedium = 0.2f;
        public const float MotionSlow = 0.3f;

        // ---- Typography tokens --------------------------------------------
        public const int TypeQuestionSize = 36;
        public const int TypeProgressStatusSize = 22;
        public const int TypeButtonLabelSize = 25;
        public const int TypeDrawerSectionTitleSize = 20;
        public const int TypeSupportingSize = 22;

        private static TMP_FontAsset _regular;
        private static TMP_FontAsset _medium;
        private static TMP_FontAsset _bold;

        public static TMP_FontAsset FontRegular => _regular ??= LoadFont("Roboto-Regular SDF");
        public static TMP_FontAsset FontMedium => _medium ??= LoadFont("Roboto-Medium SDF");
        public static TMP_FontAsset FontBold => _bold ??= LoadFont("Roboto-Bold SDF");

        private static TMP_FontAsset LoadFont(string name)
        {
            var font = Resources.Load<TMP_FontAsset>($"JawMaterialFonts/{name}");
            if (font != null) return font;
            Debug.LogWarning($"JawMaterialTheme: font '{name}' not found under Resources/JawMaterialFonts; " +
                "falling back to TMP default font asset.");
            return TMP_Settings.defaultFontAsset;
        }

        private static Color HexColor(string hex, float alpha = 1f)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            c.a = alpha;
            return c;
        }
    }
}
