using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BMC.JawAR.SurfaceRegions
{
    public sealed class JawSurfaceRegionFeedback : MonoBehaviour
    {
        public JawSurfaceRegionTarget target;
        public Color flashColor = new(1f, 0.48f, 0.03f, 0.82f);
        public float flashSeconds = 1.25f;

        private GameObject overlayObject;
        private MeshFilter overlayFilter;
        private MeshRenderer overlayRenderer;
        private Material sharedFlashMaterial;
        private Coroutine routine;

        public void Flash(JawSurfaceRegionMap.RegionDefinition region)
        {
            if (region?.BakedOverlayMesh == null || target == null) return;
            EnsureOverlay();
            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(FlashRoutine(region.BakedOverlayMesh));
        }

        private void EnsureOverlay()
        {
            if (overlayObject != null) return;
            overlayObject = new GameObject("JawSurfaceRegionRuntimeHighlight");
            overlayObject.transform.SetParent(target.meshCollider.transform, false);
            overlayFilter = overlayObject.AddComponent<MeshFilter>();
            overlayRenderer = overlayObject.AddComponent<MeshRenderer>();
            overlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            overlayRenderer.receiveShadows = false;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            sharedFlashMaterial = new Material(shader) { name = "JawSurfaceRegionFlash_Runtime", color = flashColor };
            if (sharedFlashMaterial.HasProperty("_BaseColor")) sharedFlashMaterial.SetColor("_BaseColor", flashColor);
            if (sharedFlashMaterial.HasProperty("_Color")) sharedFlashMaterial.SetColor("_Color", flashColor);
            sharedFlashMaterial.renderQueue = 3100;
            overlayRenderer.sharedMaterial = sharedFlashMaterial;
            overlayObject.SetActive(false);
        }

        private IEnumerator FlashRoutine(Mesh mesh)
        {
            overlayFilter.sharedMesh = mesh;
            overlayObject.SetActive(true);
            yield return new WaitForSecondsRealtime(flashSeconds);
            if (overlayObject != null) overlayObject.SetActive(false);
            routine = null;
        }

        private void OnDestroy()
        {
            if (sharedFlashMaterial != null) Destroy(sharedFlashMaterial);
        }
    }
}
