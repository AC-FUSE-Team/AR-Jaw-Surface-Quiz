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
    public static class JawQuizThreeModesScreenshotCapture
    {
        public const string Folder = "Artifacts/ThreeLearningModes_v35/Screenshots";
        private const string BackgroundPhoto =
            "/home/omar/JawRepair/PhoneCaptures_20260716/Screenshot_20260716-234522_Jaw ArUco Anatomy.jpg";
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly (int width, int height, string file, string state)[] Captures =
        {
            (1080, 2220, "01_ModeSelection_1080x2220.png", "modes"),
            (1080, 2220, "02_FindIt_Active_1080x2220.png", "find"),
            (1080, 2220, "03_WhatIsThis_BeforeSelection_1080x2220.png", "what_before"),
            (1080, 2220, "04_WhatIsThis_LeftRamus_1080x2220.png", "what_named"),
            (1080, 2220, "05_TwoPlayer_PrivateSelection_1080x2220.png", "two_choose"),
            (1080, 2220, "06_TwoPlayer_TargetConfirmation_1080x2220.png", "two_confirm"),
            (1080, 2220, "07_TwoPlayer_Player2Neutral_1080x2220.png", "two_neutral"),
            (1080, 2220, "08_TwoPlayer_Correct_1080x2220.png", "two_correct"),
            (1080, 2220, "09_TwoPlayer_IncorrectRetry_1080x2220.png", "two_incorrect"),
            (1080, 2220, "10_Drawer_OverlaySetting_1080x2220.png", "drawer_overlay"),
            (1080, 1920, "11_ModeSelection_1080x1920.png", "modes"),
            (1080, 1920, "12_WhatIsThis_LeftRamus_1080x1920.png", "what_named")
        };

        [MenuItem("Tools/Jaw Anatomy Quiz/Capture v35 Three Mode Screenshots")]
        public static void Capture()
        {
            Directory.CreateDirectory(Folder);
            foreach (var capture in Captures) CaptureOne(capture);
            Debug.Log($"JAW_QUIZ_THREE_MODE_SCREENSHOTS_READY count={Captures.Length} folder={Folder}");
        }

        public static void CaptureAndExit()
        {
            try { Capture(); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        private static void CaptureOne((int width, int height, string file, string state) capture)
        {
            EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
            var controller = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (controller == null || camera == null) throw new InvalidOperationException("Quiz controller or camera missing.");
            foreach (var behaviour in camera.GetComponents<Behaviour>()) if (behaviour != camera) behaviour.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

            controller.EnsureInterface();
            var compact = controller.GetComponent<JawQuizCompactPortraitUi>() ??
                          controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            if (compact.LearningModes == null)
                compact.GetType().GetMethod("Awake", PrivateInstance)?.Invoke(compact, null);
            controller.SetPreviewResolution(capture.width, capture.height);
            compact.SetPreviewResolution(capture.width, capture.height);
            var canvas = controller.GetComponentInChildren<Canvas>(true);
            InsertBackground(canvas);
            PrepareState(controller, compact, capture.state);

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = Mathf.Max(camera.nearClipPlane + 0.001f, 0.011f);
            ForceText(canvas);
            CaptureCamera(camera, capture.width, capture.height, Path.Combine(Folder, capture.file));
        }

        private static void PrepareState(JawQuizSceneController controller, JawQuizCompactPortraitUi compact, string state)
        {
            var modes = compact.LearningModes;
            compact.CloseDrawer();
            switch (state)
            {
                case "find":
                    modes.SelectMode(JawQuizLearningMode.FindIt);
                    Field<TMP_Text>(compact, "questionLabel").text = "Find the Left Ramus";
                    Field<TMP_Text>(compact, "progressChipLabel").text = "1 / 23";
                    Field<TMP_Text>(compact, "attemptChipLabel").text = "Attempt 1 / 3";
                    Field<Button>(compact, "startButton").gameObject.SetActive(false);
                    foreach (var name in new[] { "repeatButton", "hintButton", "skipButton" })
                        Field<Button>(compact, name).gameObject.SetActive(true);
                    break;
                case "what_before":
                    modes.SelectMode(JawQuizLearningMode.WhatIsThis);
                    break;
                case "what_named":
                    modes.SelectMode(JawQuizLearningMode.WhatIsThis);
                    controller.selectionAdapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
                    break;
                case "two_choose":
                    modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
                    break;
                case "two_confirm":
                    modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
                    controller.selectionAdapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
                    break;
                case "two_neutral":
                    BeginPlayerTwoTurn(controller, modes);
                    break;
                case "two_correct":
                    BeginPlayerTwoTurn(controller, modes);
                    controller.selectionAdapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.PhysicalFingertip);
                    break;
                case "two_incorrect":
                    BeginPlayerTwoTurn(controller, modes);
                    controller.selectionAdapter.SimulateDetailedSelection("RightRamus", JawQuizSelectionSource.PhysicalFingertip);
                    break;
                case "drawer_overlay":
                    modes.SelectMode(JawQuizLearningMode.WhatIsThis);
                    compact.OpenDrawer();
                    var drawer = Field<RectTransform>(compact, "drawer");
                    if (drawer != null) drawer.anchoredPosition = Vector2.zero;
                    compact.RefreshPreviewNow();
                    break;
            }
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

        private static T Field<T>(object target, string name) where T : class =>
            target.GetType().GetField(name, PrivateInstance)?.GetValue(target) as T;

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
