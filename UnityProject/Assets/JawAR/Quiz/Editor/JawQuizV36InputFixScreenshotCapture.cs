using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz.Editor
{
    /// <summary>Visual QA screenshots for the v36 input-usability fix (see FINAL_REPORT.md item 9).</summary>
    public static class JawQuizV36InputFixScreenshotCapture
    {
        public const string Folder = "Artifacts/InputUsabilityFix_v36/Screenshots";
        private const string BackgroundPhoto =
            "/home/omar/JawRepair/PhoneCaptures_20260716/Screenshot_20260716-234522_Jaw ArUco Anatomy.jpg";
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int Width = 1080;
        private const int Height = 2220;

        private static readonly (string file, string state)[] Captures =
        {
            ("01_FindIt_AwaitingPhysicalAnswer.png", "find_awaiting"),
            ("02_FindIt_AfterCorrectSelection.png", "find_correct"),
            ("03_FindIt_AfterIncorrectSelection.png", "find_incorrect"),
            ("04_FindIt_ImmediatelyAfterNext.png", "find_next"),
            ("05_WhatIsThis_AfterScreenTap.png", "what_screen_tap"),
            ("06_WhatIsThis_AfterPhysicalPointing.png", "what_physical"),
            ("07_WhatIsThis_UnlabelledFeedback.png", "what_unlabelled"),
            ("08_TwoPlayer_PrivateTargetWithPrivacyExplanation.png", "two_confirm"),
            ("09_TwoPlayer_Player2NeutralAnswerState.png", "two_neutral"),
            ("10_InputNotReady_TrackingMessage.png", "not_ready")
        };

        [MenuItem("Tools/Jaw Anatomy Quiz/Capture v36 Input Fix Screenshots")]
        public static void Capture()
        {
            Directory.CreateDirectory(Folder);
            foreach (var capture in Captures) CaptureOne(capture);
            Debug.Log($"JAW_QUIZ_V36_SCREENSHOTS_READY count={Captures.Length} folder={Folder}");
        }

        public static void CaptureAndExit()
        {
            try { Capture(); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        private static void CaptureOne((string file, string state) capture)
        {
            EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
            var controller = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (controller == null || camera == null) throw new InvalidOperationException("Quiz controller or camera missing.");
            foreach (var behaviour in camera.GetComponents<Behaviour>()) if (behaviour != camera) behaviour.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

            // Edit Mode never invokes MonoBehaviour.Awake() automatically, so the scene
            // controller's own DetailedSelectionReceived subscription (Find It's grading path)
            // would otherwise never exist and simulated Find It selections would never be graded.
            typeof(JawQuizSceneController).GetMethod("Awake", PrivateInstance)?.Invoke(controller, null);
            controller.EnsureInterface();
            var compact = controller.GetComponent<JawQuizCompactPortraitUi>() ??
                          controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            if (compact.LearningModes == null)
                compact.GetType().GetMethod("Awake", PrivateInstance)?.Invoke(compact, null);
            controller.SetPreviewResolution(Width, Height);
            compact.SetPreviewResolution(Width, Height);
            var canvas = controller.GetComponentInChildren<Canvas>(true);
            InsertBackground(canvas);
            PrepareState(controller, compact, capture.state);
            // LateUpdate() (which mirrors the controller's live text/snackbar into the compact HUD)
            // never runs outside Play Mode, so every capture must force that sync explicitly or
            // the HUD keeps showing whatever it last displayed during BuildCompactInterface().
            compact.RefreshPreviewNow();
            // RefreshPreviewNow() re-evaluates the tracking banner from the (now-nulled) jawTracker,
            // so the "already armed" Find It screenshots must hide it again afterward.
            if (capture.state.StartsWith("find_", StringComparison.Ordinal)) HideTrackingBanner(controller);

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = Mathf.Max(camera.nearClipPlane + 0.001f, 0.011f);
            ForceText(canvas);
            CaptureCamera(camera, Width, Height, Path.Combine(Folder, capture.file));
        }

        private static void PrepareState(JawQuizSceneController controller, JawQuizCompactPortraitUi compact, string state)
        {
            var modes = compact.LearningModes;
            compact.CloseDrawer();
            switch (state)
            {
                case "find_awaiting":
                    PresentFindItQuestionWithoutNetworking(controller, modes);
                    break;
                case "find_correct":
                {
                    var engine = PresentFindItQuestionWithoutNetworking(controller, modes);
                    controller.selectionAdapter.SimulateDetailedSelection(
                        engine.CurrentQuestion.ExpectedRegionId, JawQuizSelectionSource.PhysicalFingertip);
                    break;
                }
                case "find_incorrect":
                {
                    var engine = PresentFindItQuestionWithoutNetworking(controller, modes);
                    var expected = engine.CurrentQuestion.ExpectedRegionId;
                    var wrong = expected == "LeftRamus" ? "RightRamus" : "LeftRamus";
                    controller.selectionAdapter.SimulateDetailedSelection(wrong, JawQuizSelectionSource.PhysicalFingertip);
                    break;
                }
                case "find_next":
                {
                    var engine = PresentFindItQuestionWithoutNetworking(controller, modes);
                    controller.selectionAdapter.SimulateDetailedSelection(
                        engine.CurrentQuestion.ExpectedRegionId, JawQuizSelectionSource.PhysicalFingertip);
                    controller.NextQuestion();
                    break;
                }
                case "what_screen_tap":
                    modes.SelectMode(JawQuizLearningMode.WhatIsThis);
                    controller.selectionAdapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
                    break;
                case "what_physical":
                    modes.SelectMode(JawQuizLearningMode.WhatIsThis);
                    controller.selectionAdapter.SimulateDetailedSelection("RightRamus", JawQuizSelectionSource.PhysicalFingertip);
                    break;
                case "what_unlabelled":
                    modes.SelectMode(JawQuizLearningMode.WhatIsThis);
                    controller.selectionAdapter.SimulateUnlabelledSelection();
                    break;
                case "two_confirm":
                    modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
                    controller.selectionAdapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
                    break;
                case "two_neutral":
                    BeginPlayerTwoTurn(controller, modes);
                    break;
                case "not_ready":
                    // Real jawTracker left in place, not yet WorldPoseLocked (no real AR session in
                    // the Editor) -- exactly as on a phone before the marker locks.
                    PresentFindItQuestionWithoutNetworking(controller, modes, trackingReady: false);
                    break;
            }
        }

        /// <summary>
        /// Presents the first Find It question the way StartQuiz() would, without its
        /// network-touching coroutines (SynchronizePendingAttempts / CheckProxyStatus) -- this
        /// screenshot tool has no live proxy and those calls would throw in a headless Editor run.
        /// </summary>
        private static JawQuizEngine PresentFindItQuestionWithoutNetworking(
            JawQuizSceneController controller, JawQuizLearningModesController modes, bool trackingReady = true)
        {
            modes.SelectMode(JawQuizLearningMode.FindIt);
            if (trackingReady) controller.jawTracker = null; // bypasses the AR-lock gate
            controller.selectionAdapter.acceptScreenInput = true;
            controller.selectionAdapter.acceptFingertipInput = true;
            controller.selectionAdapter.BlockingOverlayOpen = false;
            var engine = new JawQuizEngine(controller.questionBank.Questions, 3);
            typeof(JawQuizSceneController).GetField("engine", PrivateInstance)?.SetValue(controller, engine);
            engine.StartQuiz();
            typeof(JawQuizSceneController).GetMethod("PresentCurrentQuestion", PrivateInstance)
                ?.Invoke(controller, null);
            return engine;
        }

        // The compact HUD's tracking banner shows whenever the (now-nulled) jawTracker isn't
        // locked, which is correct for the "not ready" screenshot but is noise on the other Find It
        // screenshots, which are meant to show an already-armed, in-progress quiz.
        private static void HideTrackingBanner(JawQuizSceneController controller)
        {
            var compact = controller.GetComponent<JawQuizCompactPortraitUi>();
            var panel = typeof(JawQuizCompactPortraitUi).GetField("trackingPanel", PrivateInstance)
                ?.GetValue(compact) as RectTransform;
            panel?.gameObject.SetActive(false);
        }

        private static void BeginPlayerTwoTurn(JawQuizSceneController controller, JawQuizLearningModesController modes)
        {
            modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
            controller.selectionAdapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
            modes.ConfirmPrivateTarget();
            modes.PlayerTwoReady();
        }

        private static void InsertBackground(Canvas canvas)
        {
            var go = new GameObject("Representative Jaw Camera Background", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(canvas.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.SetAsFirstSibling();
            var texture = new Texture2D(2, 2);
            texture.LoadImage(File.ReadAllBytes(BackgroundPhoto));
            go.AddComponent<RawImage>().texture = texture;
        }

        private static void ForceText(Canvas canvas)
        {
            Canvas.ForceUpdateCanvases();
            foreach (var text in canvas.GetComponentsInChildren<TMP_Text>(true))
            {
                text.SetAllDirty();
                text.ForceMeshUpdate();
            }
            Canvas.ForceUpdateCanvases();
        }

        private static void CaptureCamera(Camera camera, int width, int height, string path)
        {
            var render = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = render;
                RenderTexture.active = render;
                camera.Render();
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(render);
            }
        }
    }
}
