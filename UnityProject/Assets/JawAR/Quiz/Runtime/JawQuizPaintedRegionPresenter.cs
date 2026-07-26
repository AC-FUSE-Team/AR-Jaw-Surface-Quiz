using System.Collections;
using System.Collections.Generic;
using BMC.JawAR.SurfaceRegions;
using UnityEngine;
using UnityEngine.Rendering;

namespace BMC.JawAR.Quiz
{
    public enum JawPaintedRegionOverlayMode
    {
        Hidden,
        SelectedOnly,
        AllRegions
    }

    [DisallowMultipleComponent]
    public sealed class JawQuizPaintedRegionPresenter : MonoBehaviour
    {
        public JawSurfaceRegionTarget target;
        [Range(0.05f, 1f)] public float opacity = 0.58f;
        public bool visibleByDefault = true;

        public bool PaintedRegionsVisible { get; private set; }
        public bool VirtualJawVisible { get; private set; } = true;
        public string HighlightedRegionId { get; private set; } = string.Empty;
        public JawPaintedRegionOverlayMode OverlayMode { get; private set; }

        private readonly Dictionary<string, MeshRenderer> renderers = new();
        private Material sharedMaterial;
        private Coroutine emphasisRoutine;

        private void Awake()
        {
            BuildIfNeeded();
            SetPaintedRegionsVisible(visibleByDefault);
        }

        public void BuildIfNeeded()
        {
            if (renderers.Count > 0 || target?.regionMap == null || target.meshCollider == null) return;
            EnsureMaterial();
            foreach (var region in target.regionMap.Regions)
            {
                if (region.BakedOverlayMesh == null || region.TriangleCount == 0) continue;
                var overlay = new GameObject("QuizPaint_" + region.StableId);
                overlay.transform.SetParent(target.meshCollider.transform, false);
                var filter = overlay.AddComponent<MeshFilter>();
                filter.sharedMesh = region.BakedOverlayMesh;
                var renderer = overlay.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = sharedMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                ApplyRegionColor(renderer, region.DisplayColor, opacity);
                renderers.Add(region.StableId, renderer);
            }
        }

        public void SetPaintedRegionsVisible(bool visible)
        {
            SetOverlayMode(visible ? JawPaintedRegionOverlayMode.AllRegions :
                JawPaintedRegionOverlayMode.Hidden);
        }

        public void TogglePaintedRegions() => SetOverlayMode(
            OverlayMode == JawPaintedRegionOverlayMode.Hidden
                ? JawPaintedRegionOverlayMode.AllRegions
                : JawPaintedRegionOverlayMode.Hidden);

        public void SetOverlayMode(JawPaintedRegionOverlayMode mode, string selectedStableId = null)
        {
            BuildIfNeeded();
            if (selectedStableId != null)
                HighlightedRegionId = target?.regionMap?.GetRegion(selectedStableId) != null
                    ? selectedStableId : string.Empty;
            OverlayMode = mode;
            PaintedRegionsVisible = mode != JawPaintedRegionOverlayMode.Hidden;
            ApplyVisibility();
        }

        public void ClearHighlight()
        {
            HighlightedRegionId = string.Empty;
            ApplyVisibility();
        }

        public void SetVirtualJawVisible(bool visible)
        {
            BuildIfNeeded();
            VirtualJawVisible = visible;
            SetBaseJawRendererVisible(visible);
            ApplyVisibility();
        }

        public void ToggleVirtualJaw() => SetVirtualJawVisible(!VirtualJawVisible);

        public bool HighlightOnly(string stableId)
        {
            BuildIfNeeded();
            if (target?.regionMap?.GetRegion(stableId) == null) return false;
            HighlightedRegionId = stableId;
            OverlayMode = JawPaintedRegionOverlayMode.SelectedOnly;
            PaintedRegionsVisible = true;
            ApplyVisibility();
            return true;
        }

        public void ShowAllRegions()
        {
            HighlightedRegionId = string.Empty;
            OverlayMode = JawPaintedRegionOverlayMode.AllRegions;
            PaintedRegionsVisible = true;
            ApplyVisibility();
        }

        public void BrieflyEmphasize(string stableId)
        {
            if (!renderers.TryGetValue(stableId, out var renderer)) return;
            if (emphasisRoutine != null) StopCoroutine(emphasisRoutine);
            emphasisRoutine = StartCoroutine(Emphasize(renderer));
        }

        private IEnumerator Emphasize(MeshRenderer renderer)
        {
            var transformToPulse = renderer.transform;
            var original = transformToPulse.localScale;
            transformToPulse.localScale = original * 1.018f;
            yield return new WaitForSecondsRealtime(0.28f);
            if (transformToPulse != null) transformToPulse.localScale = original;
            emphasisRoutine = null;
        }

        private void ApplyVisibility()
        {
            foreach (var pair in renderers)
                pair.Value.enabled = VirtualJawVisible && OverlayMode != JawPaintedRegionOverlayMode.Hidden &&
                                     (OverlayMode == JawPaintedRegionOverlayMode.AllRegions ||
                                      (!string.IsNullOrEmpty(HighlightedRegionId) && pair.Key == HighlightedRegionId));
        }

        private void SetBaseJawRendererVisible(bool visible)
        {
            if (target == null) return;
            if (target.meshFilter != null)
            {
                foreach (var renderer in target.meshFilter.GetComponents<Renderer>())
                    renderer.enabled = visible;
            }
            if (target.skinnedMeshRenderer != null)
                target.skinnedMeshRenderer.enabled = visible;
        }

        private void EnsureMaterial()
        {
            if (sharedMaterial != null) return;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            sharedMaterial = new Material(shader)
            {
                name = "JawQuizPaintedRegions_Runtime",
                renderQueue = 3000
            };
            sharedMaterial.SetOverrideTag("RenderType", "Transparent");
            if (sharedMaterial.HasProperty("_Surface")) sharedMaterial.SetFloat("_Surface", 1f);
            if (sharedMaterial.HasProperty("_SrcBlend")) sharedMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (sharedMaterial.HasProperty("_DstBlend")) sharedMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (sharedMaterial.HasProperty("_ZWrite")) sharedMaterial.SetFloat("_ZWrite", 0f);
            if (sharedMaterial.HasProperty("_Cull")) sharedMaterial.SetFloat("_Cull", (float)CullMode.Off);
            sharedMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        private static void ApplyRegionColor(Renderer renderer, Color color, float alpha)
        {
            color.a = alpha;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }

        private void OnDestroy()
        {
            if (sharedMaterial == null) return;
            if (Application.isPlaying)
                Destroy(sharedMaterial);
            else
                DestroyImmediate(sharedMaterial);
        }
    }
}
