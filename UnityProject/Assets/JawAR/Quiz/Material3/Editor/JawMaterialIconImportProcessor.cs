using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.Quiz.Material3.Editor
{
    /// <summary>
    /// Forces the Material Symbols icon PNGs to import as clean, uncompressed UI sprites so icon
    /// buttons stay crisp at small sizes and can be tinted per Material color token.
    /// </summary>
    public sealed class JawMaterialIconImportProcessor : AssetPostprocessor
    {
        private const string IconFolder = "Material3/Resources/JawMaterialIcons/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.Replace('\\', '/').Contains(IconFolder)) return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.spritePixelsPerUnit = 96f;
        }
    }
}
