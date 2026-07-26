using System;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR
{
    /// <summary>
    /// Listens for "what is that/this" and speaks the anatomy region under the fingertip pointer.
    /// </summary>
    public sealed class JawVoiceQuestionController : MonoBehaviour
    {
        public JawOpenCvArucoTracker jawTracker;
        public JawFingertipPointer fingertipPointer;
        public float recentSelectionSeconds = 8f;

        [Tooltip("When set, a current/recent painted surface-region selection is preferred over " +
                 "the legacy box zone. Leave unset to keep original box-only behaviour.")]
        public JawSurfaceFingertipRouter surfaceRouter;

        private const string BridgeClass = "com.omar.jawaruco.JawVoiceBridge";
        private AndroidJavaClass bridge;
        private Text voiceStatusText;
        private bool permissionRequested;
        private bool bridgeInitialized;
        private int lastQuestionSequence;
        private float nextStatusPoll;

        private void Start()
        {
            CreateUi();
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureMicrophonePermission();
#else
            SetStatus("VOICE QUESTIONS REQUIRE ANDROID");
#endif
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridge?.CallStatic("shutdown");
            }
            catch (Exception) { }
#endif
            bridge?.Dispose();
            bridge = null;
        }

        private void OnApplicationPause(bool paused)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (paused)
            {
                try
                {
                    bridge?.CallStatic("stopListening");
                }
                catch (Exception) { }
            }
#endif
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                EnsureMicrophonePermission();
                SetStatus("ALLOW MICROPHONE TO ASK: WHAT IS THAT?");
                return;
            }

            if (!bridgeInitialized)
            {
                InitializeBridge();
                return;
            }

            bool jawLocked = jawTracker != null && jawTracker.WorldPoseLocked;
            if (!jawLocked)
            {
                bridge.CallStatic("stopListening");
                SetStatus("VOICE READY AFTER JAW LOCK");
                return;
            }

            bridge.CallStatic("startListening");
            int sequence = bridge.CallStatic<int>("getQuestionSequence");
            if (sequence != lastQuestionSequence)
            {
                lastQuestionSequence = sequence;
                string phrase = bridge.CallStatic<string>("getLastQuestion");
                AnswerQuestion(phrase);
            }
            else if (Time.unscaledTime >= nextStatusPoll)
            {
                nextStatusPoll = Time.unscaledTime + 1f;
                string error = bridge.CallStatic<string>("getLastError");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    SetStatus("VOICE: " + error);
                }
                else if (bridge.CallStatic<bool>("isListening"))
                {
                    SetStatus("VOICE LISTENING — SAY “WHAT IS THAT?”");
                }
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void EnsureMicrophonePermission()
        {
            if (permissionRequested ||
                UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                return;
            }
            permissionRequested = true;
            UnityEngine.Android.Permission.RequestUserPermission(
                UnityEngine.Android.Permission.Microphone);
        }

        private void InitializeBridge()
        {
            try
            {
                bridge = new AndroidJavaClass(BridgeClass);
                bridgeInitialized = bridge.CallStatic<bool>("initialize");
                SetStatus(bridgeInitialized
                    ? "VOICE READY — SAY “WHAT IS THAT?” AFTER POINTING"
                    : "VOICE RECOGNITION UNAVAILABLE");
            }
            catch (Exception exception)
            {
                bridgeInitialized = false;
                Debug.LogError($"JAW_VOICE_START_FAILED: {exception}");
                SetStatus("VOICE RECOGNITION UNAVAILABLE");
            }
        }
#endif

        /// <summary>
        /// Resolves the current/recent selection to answer against: a painted surface region
        /// (preferred, when the optional router has one) or the legacy box zone (fallback, only
        /// when the fallback pipeline actually selected a box). Public so it can be exercised
        /// directly by tests without needing the Android voice bridge.
        /// </summary>
        public bool TryResolveSelection(out JawAnatomySelectionResult result)
        {
            if (surfaceRouter != null && surfaceRouter.TryGetSelection(recentSelectionSeconds, out result))
            {
                return true;
            }

            JawAnatomyZone zone = fingertipPointer != null
                ? fingertipPointer.CurrentPointedZone
                : null;
            if (zone == null && fingertipPointer != null &&
                Time.unscaledTime - fingertipPointer.LastSelectedTime <= recentSelectionSeconds)
            {
                zone = fingertipPointer.LastSelectedZone;
            }

            if (zone != null)
            {
                result = JawAnatomySelectionResult.FromLegacyZone(zone, Time.unscaledTime);
                return true;
            }

            result = default;
            return false;
        }

        private void AnswerQuestion(string recognizedPhrase)
        {
            string answer;
            if (TryResolveSelection(out var selection))
            {
                answer = "That is the " + selection.DisplayName + ".";
                SetStatus($"HEARD: {recognizedPhrase} — ANSWERING {selection.DisplayName}");
            }
            else
            {
                answer = "I heard you. Point your index finger at an anatomy region and hold it there.";
                SetStatus($"HEARD: {recognizedPhrase} — NO REGION SELECTED");
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridge?.CallStatic("speak", answer);
            }
            catch (Exception exception)
            {
                Debug.LogError($"JAW_VOICE_SPEAK_FAILED: {exception}");
                SetStatus("VOICE ANSWER FAILED");
            }
#endif
            Debug.Log($"JAW_VOICE_QUESTION: phrase={recognizedPhrase} answer={answer}");
        }

        private void CreateUi()
        {
            Canvas canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null) return;
            var go = new GameObject("Voice Question Status");
            go.transform.SetParent(canvas.transform, false);
            voiceStatusText = go.AddComponent<Text>();
            voiceStatusText.text = "VOICE STARTING";
            voiceStatusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            voiceStatusText.fontSize = 27;
            voiceStatusText.color = new Color(0.45f, 0.9f, 1f);
            voiceStatusText.alignment = TextAnchor.MiddleCenter;
            voiceStatusText.raycastTarget = false;
            RectTransform rect = voiceStatusText.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -255f);
            rect.sizeDelta = new Vector2(980f, 100f);
        }

        private void SetStatus(string message)
        {
            if (voiceStatusText != null && voiceStatusText.text != message)
            {
                voiceStatusText.text = message;
            }
        }
    }
}
