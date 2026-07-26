using UnityEngine;

namespace BMC.JawAR
{
    public sealed class JawAnatomyZone : MonoBehaviour
    {
        public string displayName;
        [TextArea(2, 5)] public string description;
        public string laterality = "Midline";
        public string referenceImageFile;
        public bool approximatePlacement = true;

        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? name : displayName;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.1f, 0.9f, 1f, 0.65f);
            var collider = GetComponent<BoxCollider>();
            if (collider == null)
            {
                return;
            }
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(collider.center, collider.size);
        }
    }
}
