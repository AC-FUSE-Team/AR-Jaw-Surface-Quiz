using System.Collections.Generic;
using UnityEngine;

namespace BMC.JawAR.Quiz.Material3
{
    /// <summary>
    /// Procedurally builds and caches rounded-rectangle sprites for the Material shape tokens.
    /// Each distinct radius is rendered once into a small 9-sliced texture and reused for the
    /// lifetime of the process — never regenerated per frame or per widget instance.
    /// </summary>
    public static class JawMaterialSprites
    {
        private const int Texel = 128; // texture size the radius is authored against; 9-sliced so it scales cleanly
        private static readonly Dictionary<float, Sprite> Cache = new();

        public static Sprite RoundedRect(float radius)
        {
            if (Cache.TryGetValue(radius, out var cached) && cached != null) return cached;

            var size = Texel;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                name = $"JawMaterialRoundedRect_{radius}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var r = Mathf.Clamp(radius, 0f, size / 2f);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var alpha = CoverageAlpha(x, y, size, r);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            var border = Mathf.Min(r + 2f, size / 2f - 1f);
            var sprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            sprite.name = $"JawMaterialRoundedRect_{radius}";

            Cache[radius] = sprite;
            return sprite;
        }

        public static Sprite Pill() => RoundedRect(JawMaterialTheme.RadiusPill > Texel / 2f ? Texel / 2f : JawMaterialTheme.RadiusPill);

        private static float CoverageAlpha(int x, int y, int size, float radius)
        {
            // Signed distance to a rounded box (Inigo Quilez formula), anti-aliased over ~1px.
            var half = size / 2f;
            var px = x + 0.5f - half;
            var py = y + 0.5f - half;
            var qx = Mathf.Abs(px) - (half - radius);
            var qy = Mathf.Abs(py) - (half - radius);
            var outsideX = Mathf.Max(qx, 0f);
            var outsideY = Mathf.Max(qy, 0f);
            var dist = Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
            return Mathf.Clamp01(0.5f - dist);
        }
    }
}
