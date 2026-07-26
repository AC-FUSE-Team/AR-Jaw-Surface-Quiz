using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BMC.JawAR
{
    public sealed class JawAnatomyTapController : MonoBehaviour
    {
        public Camera targetCamera;
        public Transform anatomyRoot;
        public Text promptText;
        public Text feedbackText;
        public float feedbackSeconds = 2.5f;
        public bool hitboxesVisible = false;
        [Tooltip("World-space radius used only when an exact tap misses. Helps thin side views remain tappable.")]
        public float tapAssistRadiusMeters = 0.012f;

        private Coroutine feedbackRoutine;

        private void Awake()
        {
            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
            SetHitboxesVisible(hitboxesVisible);
            if (promptText != null)
            {
                promptText.text = "Tap a jaw anatomy region";
            }
            if (feedbackText != null)
            {
                feedbackText.text = "";
            }
        }

        private void Update()
        {
            if (Input.touchCount > 0)
            {
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began && !IsOverUi(touch.fingerId))
                {
                    CheckScreenPoint(touch.position);
                }
                return;
            }

            if (Input.GetMouseButtonDown(0) && !IsOverUi(-1))
            {
                CheckScreenPoint(Input.mousePosition);
            }
        }

        public void SetHitboxesVisible(bool visible)
        {
            hitboxesVisible = visible;
            if (anatomyRoot == null)
            {
                return;
            }
            foreach (var renderer in anatomyRoot.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = visible;
            }
        }

        private bool IsOverUi(int fingerId)
        {
            if (EventSystem.current == null)
            {
                return false;
            }
            return fingerId >= 0
                ? EventSystem.current.IsPointerOverGameObject(fingerId)
                : EventSystem.current.IsPointerOverGameObject();
        }

        private void CheckScreenPoint(Vector2 screenPoint)
        {
            var cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null)
            {
                return;
            }

            Ray ray = cameraToUse.ScreenPointToRay(screenPoint);
            if (TryShowClosestZone(Physics.RaycastAll(ray, 5f)))
            {
                return;
            }

            // From an oblique view even a physically large region can have a narrow screen-space
            // silhouette. Give near-misses a small world-space tolerance without changing the
            // authored anatomy volumes. Exact ray hits above always take priority.
            if (tapAssistRadiusMeters > 0f)
            {
                TryShowClosestZone(Physics.SphereCastAll(ray, tapAssistRadiusMeters, 5f));
            }
        }

        private bool TryShowClosestZone(RaycastHit[] hits)
        {
            System.Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            foreach (var hit in hits)
            {
                var zone = hit.collider.GetComponentInParent<JawAnatomyZone>();
                if (zone == null)
                {
                    continue;
                }

                SelectZone(zone, "TOUCH");
                Debug.Log($"JAW_ANATOMY_TAP: zone={zone.DisplayName} side={zone.laterality} localPoint={anatomyRoot?.InverseTransformPoint(hit.point)}");
                return true;
            }
            return false;
        }

        public void SelectZone(JawAnatomyZone zone, string source)
        {
            if (zone == null)
            {
                return;
            }
            var text = zone.DisplayName;
            if (!string.IsNullOrWhiteSpace(zone.laterality))
            {
                text += " (" + zone.laterality + ")";
            }
            if (!string.IsNullOrWhiteSpace(zone.description))
            {
                text += "\n" + zone.description;
            }
            if (zone.approximatePlacement)
            {
                text += "\nApproximate screenshot-guided zone; editable in the Unity Hierarchy.";
            }
            ShowFeedback(text, zone);
            Debug.Log($"JAW_ANATOMY_SELECT: source={source} zone={zone.DisplayName} side={zone.laterality}");
        }

        private void ShowFeedback(string message, JawAnatomyZone zone)
        {
            if (feedbackRoutine != null)
            {
                StopCoroutine(feedbackRoutine);
            }
            feedbackRoutine = StartCoroutine(FeedbackRoutine(message, zone));
        }

        private IEnumerator FeedbackRoutine(string message, JawAnatomyZone zone)
        {
            if (feedbackText != null)
            {
                feedbackText.text = message;
                feedbackText.color = new Color(0.2f, 1f, 0.85f);
            }

            var renderers = zone.GetComponentsInChildren<Renderer>(true);
            var oldColors = new Color[renderers.Length];
            var oldEnabled = new bool[renderers.Length];
            for (var index = 0; index < renderers.Length; index++)
            {
                oldEnabled[index] = renderers[index].enabled;
                renderers[index].enabled = true;
                oldColors[index] = renderers[index].material.color;
                renderers[index].material.color = new Color(1f, 0.75f, 0.05f, 0.7f);
            }

            yield return new WaitForSecondsRealtime(feedbackSeconds);

            for (var index = 0; index < renderers.Length; index++)
            {
                if (renderers[index] != null)
                {
                    renderers[index].material.color = oldColors[index];
                    renderers[index].enabled = oldEnabled[index];
                }
            }
            if (feedbackText != null)
            {
                feedbackText.text = "";
            }
            feedbackRoutine = null;
        }
    }
}
