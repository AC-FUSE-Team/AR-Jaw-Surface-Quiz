using UnityEngine;

namespace BMC.JawAR.Quiz
{
    [RequireComponent(typeof(RectTransform))]
    public sealed class JawQuizSafeArea : MonoBehaviour
    {
        private Rect lastSafeArea;
        private Vector2Int lastScreen;

        private void OnEnable() => Apply();
        private void Update()
        {
            if (lastSafeArea != Screen.safeArea || lastScreen.x != Screen.width || lastScreen.y != Screen.height)
                Apply();
        }

        private void Apply()
        {
            var safe = Screen.safeArea;
            var rect = (RectTransform)transform;
            var min = safe.position;
            var max = safe.position + safe.size;
            min.x /= Mathf.Max(1, Screen.width);
            min.y /= Mathf.Max(1, Screen.height);
            max.x /= Mathf.Max(1, Screen.width);
            max.y /= Mathf.Max(1, Screen.height);
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            lastSafeArea = safe;
            lastScreen = new Vector2Int(Screen.width, Screen.height);
        }
    }
}
