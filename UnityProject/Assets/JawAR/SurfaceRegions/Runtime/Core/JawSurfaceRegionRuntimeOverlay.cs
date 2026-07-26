using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BMC.JawAR.SurfaceRegions
{
    [DisallowMultipleComponent]
    public sealed class JawSurfaceRegionRuntimeOverlay : MonoBehaviour
    {
        public JawSurfaceRegionTarget target;
        [Range(0.05f, 1f)] public float opacity = 0.58f;
        public bool showOnEnable = true;

        private readonly List<GameObject> overlayObjects = new();
        private Material sharedOverlayMaterial;

        private void OnEnable()
        {
            if (showOnEnable) ShowAll();
        }

        public void ShowAll()
        {
            Clear();
            if (target == null || target.regionMap == null || target.meshCollider == null) return;
            EnsureSharedMaterial();

            foreach (var region in target.regionMap.Regions)
            {
                if (region.BakedOverlayMesh == null || region.TriangleCount == 0) continue;
                var overlay = new GameObject("SurfaceRegion_" + region.StableId);
                overlay.transform.SetParent(target.meshCollider.transform, false);
                var filter = overlay.AddComponent<MeshFilter>();
                filter.sharedMesh = region.BakedOverlayMesh;
                var renderer = overlay.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = sharedOverlayMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                var color = region.DisplayColor;
                color.a = opacity;
                var properties = new MaterialPropertyBlock();
                properties.SetColor("_BaseColor", color);
                properties.SetColor("_Color", color);
                renderer.SetPropertyBlock(properties);
                overlayObjects.Add(overlay);
            }
        }

        public void Clear()
        {
            foreach (var overlay in overlayObjects)
            {
                if (overlay != null) Destroy(overlay);
            }
            overlayObjects.Clear();
        }

        private void EnsureSharedMaterial()
        {
            if (sharedOverlayMaterial != null) return;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Sprites/Default") ??
                         Shader.Find("Unlit/Color");
            sharedOverlayMaterial = new Material(shader)
            {
                name = "JawSurfaceRegions_RuntimeShared",
                renderQueue = 3000
            };
            sharedOverlayMaterial.SetOverrideTag("RenderType", "Transparent");
            if (sharedOverlayMaterial.HasProperty("_Surface")) sharedOverlayMaterial.SetFloat("_Surface", 1f);
            if (sharedOverlayMaterial.HasProperty("_Blend")) sharedOverlayMaterial.SetFloat("_Blend", 0f);
            if (sharedOverlayMaterial.HasProperty("_SrcBlend"))
                sharedOverlayMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (sharedOverlayMaterial.HasProperty("_DstBlend"))
                sharedOverlayMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (sharedOverlayMaterial.HasProperty("_ZWrite")) sharedOverlayMaterial.SetFloat("_ZWrite", 0f);
            if (sharedOverlayMaterial.HasProperty("_Cull")) sharedOverlayMaterial.SetFloat("_Cull", (float)CullMode.Off);
            sharedOverlayMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        private void OnDestroy()
        {
            Clear();
            if (sharedOverlayMaterial != null) Destroy(sharedOverlayMaterial);
        }
    }
}
