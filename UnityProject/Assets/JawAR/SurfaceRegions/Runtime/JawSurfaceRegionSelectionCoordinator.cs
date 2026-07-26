using System;
using BMC.JawAR.SurfaceRegions;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BMC.JawAR
{
    [DisallowMultipleComponent]
    public sealed class JawSurfaceRegionSelectionCoordinator : MonoBehaviour
    {
        public enum SelectionMode
        {
            ExistingBoxesOnly,
            SurfaceRegionsOnly,
            SurfaceThenBoxes,
            BoxesThenSurface
        }

        public SelectionMode selectionMode = SelectionMode.ExistingBoxesOnly;
        public Camera targetCamera;
        public JawSurfaceRegionTarget surfaceTarget;
        public JawSurfaceRegionFeedback surfaceFeedback;
        public JawAnatomyTapController existingBoxController;
        public float maxDistance = 5f;

        private bool boxControllerWasEnabled;

        private void OnEnable()
        {
            if (existingBoxController == null) return;
            boxControllerWasEnabled = existingBoxController.enabled;
            existingBoxController.enabled = selectionMode == SelectionMode.ExistingBoxesOnly;
        }

        private void OnDisable()
        {
            if (existingBoxController != null) existingBoxController.enabled = boxControllerWasEnabled;
        }

        private void Update()
        {
            if (selectionMode == SelectionMode.ExistingBoxesOnly) return;
            if (Input.touchCount > 0)
            {
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began && !IsOverUi(touch.fingerId)) Select(touch.position);
                return;
            }
            if (Input.GetMouseButtonDown(0) && !IsOverUi(-1)) Select(Input.mousePosition);
        }

        private void Select(Vector2 screenPoint)
        {
            var cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null) return;
            var ray = cameraToUse.ScreenPointToRay(screenPoint);
            switch (selectionMode)
            {
                case SelectionMode.SurfaceRegionsOnly:
                    TrySurface(ray);
                    break;
                case SelectionMode.SurfaceThenBoxes:
                    if (!TrySurface(ray)) TryBoxes(ray);
                    break;
                case SelectionMode.BoxesThenSurface:
                    if (!TryBoxes(ray)) TrySurface(ray);
                    break;
            }
        }

        private bool TrySurface(Ray ray)
        {
            if (surfaceTarget == null || !surfaceTarget.TryRaycast(ray, maxDistance, out var hit, out var region))
                return false;
            surfaceFeedback?.Flash(region);
            Debug.Log($"JAW_SURFACE_REGION_SELECT: id={region.StableId} name={region.DisplayName} triangle={hit.triangleIndex}");
            return true;
        }

        private bool TryBoxes(Ray ray)
        {
            if (existingBoxController == null) return false;
            var hits = Physics.RaycastAll(ray, maxDistance);
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            foreach (var hit in hits)
            {
                var zone = hit.collider.GetComponentInParent<JawAnatomyZone>();
                if (zone == null) continue;
                existingBoxController.SelectZone(zone, "SURFACE_REGION_COORDINATOR");
                return true;
            }
            return false;
        }

        private static bool IsOverUi(int fingerId)
        {
            if (EventSystem.current == null) return false;
            return fingerId >= 0
                ? EventSystem.current.IsPointerOverGameObject(fingerId)
                : EventSystem.current.IsPointerOverGameObject();
        }
    }
}
