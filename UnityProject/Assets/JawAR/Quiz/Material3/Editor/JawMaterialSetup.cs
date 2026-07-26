using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace BMC.JawAR.Quiz.Material3.Editor
{
    /// <summary>
    /// One-off setup for the Material 3-inspired quiz UI: imports TMP essential resources (if not
    /// already present) and bakes the Roboto TTFs into dynamic-atlas TMP SDF font assets under
    /// Resources so JawMaterialTheme can Resources.Load them at runtime. Run via
    /// Tools/Jaw Anatomy Quiz/Material 3/Setup Fonts, or headlessly via
    /// -executeMethod BMC.JawAR.Quiz.Material3.Editor.JawMaterialSetup.SetupAndExit.
    /// </summary>
    public static class JawMaterialSetup
    {
        private const string SourceFontDir = "Assets/JawAR/Quiz/Material3/Fonts";
        private const string FontAssetDir = "Assets/JawAR/Quiz/Material3/Resources/JawMaterialFonts";
        private static readonly string[] Weights = { "Regular", "Medium", "Bold" };

        [MenuItem("Tools/Jaw Anatomy Quiz/Material 3/Setup Fonts")]
        public static void Setup()
        {
            ImportEssentialResourcesIfMissing();
            GenerateFontAssets();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Headless entry point. TMP Essential Resources import is asynchronous (it only completes
        /// after AssetDatabase.importPackageCompleted fires), so font generation is chained from
        /// that callback rather than run immediately after — running it immediately would race the
        /// still-in-flight import and throw inside TMP_FontAsset.CreateFontAsset.
        /// </summary>
        public static void SetupAndExit()
        {
            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var tmpFolder = Path.Combine(projectRoot!, "Assets", "TextMesh Pro");
                if (Directory.Exists(tmpFolder))
                {
                    FinishSetupAndExit();
                    return;
                }

                AssetDatabase.importPackageCompleted += _ => FinishSetupAndExit();
                AssetDatabase.importPackageFailed += (_, err) =>
                {
                    Debug.LogError($"JawMaterialSetup: TMP Essential Resources import failed: {err}");
                    EditorApplication.Exit(1);
                };
                ImportEssentialResourcesIfMissing();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"JawMaterialSetup failed: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static void FinishSetupAndExit()
        {
            try
            {
                GenerateFontAssets();
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"JawMaterialSetup failed: {e}");
                EditorApplication.Exit(1);
            }
        }

        public static void ImportEssentialsAndExit()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var tmpFolder = Path.Combine(projectRoot!, "Assets", "TextMesh Pro");
            if (Directory.Exists(tmpFolder))
            {
                Debug.Log("JawMaterialSetup: TMP Essential Resources already present.");
                EditorApplication.Exit(0);
                return;
            }

            AssetDatabase.importPackageCompleted += _ =>
            {
                Debug.Log("JawMaterialSetup: TMP Essential Resources import completed.");
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(0);
            };
            AssetDatabase.importPackageFailed += (_, err) =>
            {
                Debug.LogError($"JawMaterialSetup: TMP Essential Resources import failed: {err}");
                EditorApplication.Exit(1);
            };
            AssetDatabase.importPackageCancelled += _ =>
            {
                Debug.LogError("JawMaterialSetup: TMP Essential Resources import cancelled.");
                EditorApplication.Exit(1);
            };

            ImportEssentialResourcesIfMissing();
        }

        public static void GenerateFontsAndExit()
        {
            try
            {
                GenerateFontAssets();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"JawMaterialSetup (generate fonts) failed: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static void ImportEssentialResourcesIfMissing()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var tmpFolder = Path.Combine(projectRoot!, "Assets", "TextMesh Pro");
            if (Directory.Exists(tmpFolder))
            {
                Debug.Log("JawMaterialSetup: TMP Essential Resources already present.");
                return;
            }

            var packageCache = Path.Combine(projectRoot, "Library", "PackageCache");
            string package = Directory.Exists(packageCache)
                ? Directory.GetDirectories(packageCache)
                    .FirstOrDefault(d => Path.GetFileName(d).StartsWith("com.unity.ugui"))
                : null;
            if (package == null)
            {
                Debug.LogWarning("JawMaterialSetup: could not locate com.unity.ugui package cache " +
                    "to import TMP Essential Resources; continuing without it.");
                return;
            }
            var pkgPath = Path.Combine(package, "Package Resources", "TMP Essential Resources.unitypackage");
            Debug.Log($"JawMaterialSetup: importing TMP Essential Resources from {pkgPath} (exists={File.Exists(pkgPath)})");
            if (File.Exists(pkgPath))
                AssetDatabase.ImportPackage(pkgPath, false);
        }

        // ASCII printable range plus the few non-ASCII glyphs this UI actually uses (em dash,
        // bullet, curly quotes as a safety margin). Baked in statically at asset-creation time so
        // no on-device/runtime dynamic SDF generation is ever required for this UI's text.
        private static readonly string CharacterSet = BuildCharacterSet();

        private static string BuildCharacterSet()
        {
            var chars = new System.Text.StringBuilder();
            for (var c = (char)32; c <= (char)126; c++) chars.Append(c);
            chars.Append('—'); // —
            chars.Append('•'); // •
            chars.Append('‘').Append('’').Append('“').Append('”');
            return chars.ToString();
        }

        private static void GenerateFontAssets()
        {
            Directory.CreateDirectory(FontAssetDir);
            foreach (var weight in Weights)
            {
                var ttfPath = $"{SourceFontDir}/Roboto-{weight}.ttf";
                var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
                if (font == null)
                {
                    Debug.LogError($"JawMaterialSetup: source font not found at {ttfPath}");
                    continue;
                }

                var assetPath = $"{FontAssetDir}/Roboto-{weight} SDF.asset";
                if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath) != null)
                    continue; // already generated, don't rebuild every run

                // GlyphRenderMode.SMOOTH (plain anti-aliased bitmap, not a signed-distance-field)
                // deliberately, not SDFAA: the SDF shader's screen-space-derivative-based edge
                // anti-aliasing renders fully invisible text in this environment's software GL
                // renderer (verified — mesh/material/atlas/UVs are all populated correctly, only
                // the SDF fragment shader output is blank), while the simpler bitmap shader path
                // renders correctly, same as legacy UI.Text already does here.
                var fontAsset = TMP_FontAsset.CreateFontAsset(
                    font,
                    90,
                    9,
                    GlyphRenderMode.SMOOTH,
                    1024,
                    1024,
                    AtlasPopulationMode.Dynamic,
                    enableMultiAtlasSupport: true);
                fontAsset.name = $"Roboto-{weight} SDF";

                // CreateFontAsset with Dynamic mode starts with an EMPTY atlas (0 glyphs) — text
                // only renders once characters are actually baked in. Bake the full set now, once,
                // at setup time, then freeze the asset to Static so it never needs on-device
                // dynamic generation.
                if (!fontAsset.TryAddCharacters(CharacterSet, out var missing))
                    Debug.LogWarning($"JawMaterialSetup: Roboto-{weight} SDF missing glyphs for: {missing}");
                fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
                fontAsset.ReadFontAssetDefinition();

                AssetDatabase.CreateAsset(fontAsset, assetPath);
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
                if (fontAsset.atlasTextures is { Length: > 0 } && fontAsset.atlasTextures[0] != null)
                    AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
                Debug.Log($"JawMaterialSetup: generated {assetPath} with {fontAsset.characterTable?.Count} characters baked in");
            }
        }
    }
}
