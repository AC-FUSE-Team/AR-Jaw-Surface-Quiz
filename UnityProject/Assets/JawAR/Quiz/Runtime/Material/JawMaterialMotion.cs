using System.Collections;
using UnityEngine;

namespace BMC.JawAR.Quiz.Material3
{
    /// <summary>
    /// Small reusable Material-style motion helpers (150-300ms fades/scales) for drawer open/close,
    /// snackbar enter/exit, switch thumbs, and button press feedback. Each call is a short-lived
    /// coroutine started by the caller (e.g. <c>StartCoroutine(JawMaterialMotion.FadeTo(...))</c>);
    /// nothing here runs every frame on its own, and it never touches the AR camera, jaw mesh, or
    /// tracking root.
    /// </summary>
    public static class JawMaterialMotion
    {
        public static IEnumerator FadeTo(CanvasGroup group, float target, float duration)
        {
            if (group == null) yield break;
            var start = group.alpha;
            var t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(start, target, EaseOutCubic(Mathf.Clamp01(t / duration)));
                yield return null;
            }
            group.alpha = target;
        }

        public static IEnumerator ScaleTo(RectTransform rt, Vector3 target, float duration)
        {
            if (rt == null) yield break;
            var start = rt.localScale;
            var t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                rt.localScale = Vector3.Lerp(start, target, EaseOutCubic(Mathf.Clamp01(t / duration)));
                yield return null;
            }
            rt.localScale = target;
        }

        public static IEnumerator SlideX(RectTransform rt, float fromAnchoredX, float toAnchoredX, float duration)
        {
            if (rt == null) yield break;
            var t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                var x = Mathf.Lerp(fromAnchoredX, toAnchoredX, EaseOutCubic(Mathf.Clamp01(t / duration)));
                rt.anchoredPosition = new Vector2(x, rt.anchoredPosition.y);
                yield return null;
            }
            rt.anchoredPosition = new Vector2(toAnchoredX, rt.anchoredPosition.y);
        }

        /// <summary>Quick press-down/release scale pulse for button touch feedback.</summary>
        public static IEnumerator PressPulse(RectTransform rt)
        {
            if (rt == null) yield break;
            yield return ScaleTo(rt, Vector3.one * 0.94f, JawMaterialTheme.MotionFast * 0.5f);
            yield return ScaleTo(rt, Vector3.one, JawMaterialTheme.MotionFast * 0.5f);
        }

        private static float EaseOutCubic(float t)
        {
            var f = t - 1f;
            return f * f * f + 1f;
        }
    }
}
