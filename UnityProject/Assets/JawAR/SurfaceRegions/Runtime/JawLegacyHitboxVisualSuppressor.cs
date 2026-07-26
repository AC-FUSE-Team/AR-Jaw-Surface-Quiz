using UnityEngine;

namespace BMC.JawAR
{
    [DisallowMultipleComponent]
    public sealed class JawLegacyHitboxVisualSuppressor : MonoBehaviour
    {
        public Transform anatomyRoot;

        private Renderer[] renderers;

        private void Awake()
        {
            CacheAndHide();
            Debug.Log($"JAW_LEGACY_HITBOX_VISUALS_SUPPRESSED count={renderers?.Length ?? 0}");
        }

        private void OnEnable()
        {
            CacheAndHide();
        }

        private void LateUpdate()
        {
            if (renderers == null) CacheAndHide();
            if (renderers == null) return;
            foreach (var renderer in renderers)
            {
                if (renderer != null && renderer.enabled) renderer.enabled = false;
            }
        }

        private void CacheAndHide()
        {
            if (anatomyRoot == null) return;
            renderers = anatomyRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer != null) renderer.enabled = false;
            }
        }
    }
}
