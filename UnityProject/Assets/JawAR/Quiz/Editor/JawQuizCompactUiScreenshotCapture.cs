using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz.Editor
{
    public static class JawQuizCompactUiScreenshotCapture
    {
        private const string Folder = "Artifacts/QuizPortraitUiRedesign_20260720/Screenshots";
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly (int width, int height, string suffix)[] Resolutions =
        {
            (1080, 2220, "1080x2220"), (1080, 1920, "1080x1920"), (2220, 1080, "2220x1080")
        };
        private static readonly string[] States = { "before", "active", "feedback", "drawer", "diagnostics" };

        [MenuItem("Tools/Jaw Anatomy Quiz/Capture Compact UI Verification Screenshots")]
        public static void Capture()
        {
            Directory.CreateDirectory(Folder);
            foreach (var resolution in Resolutions)
            foreach (var state in States)
                CaptureOneState(resolution.width, resolution.height, resolution.suffix, state);
            Debug.Log("JAW_QUIZ_COMPACT_SCREENSHOTS_READY count=15 folder=" + Folder);
        }

        private static void CaptureOneState(int width, int height, string suffix, string state)
        {
            EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
            var controller = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (controller == null || camera == null) throw new InvalidOperationException("Quiz controller or camera is missing.");
            foreach (var behaviour in camera.GetComponents<Behaviour>()) if (behaviour != camera) behaviour.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.055f, 0.085f, 1f);

            controller.EnsureInterface();
            var compact = controller.GetComponent<JawQuizCompactPortraitUi>() ?? controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            EnsureBuilt(compact);
            controller.SetPreviewResolution(width, height);
            Invoke(compact, "ApplyLayout", width, height);
            PrepareStaticState(controller, compact, state);

            var canvas = controller.GetComponentInChildren<Canvas>(true);
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = Mathf.Max(camera.nearClipPlane + 0.001f, 0.011f);
            Canvas.ForceUpdateCanvases();
            CaptureCamera(camera, width, height, Path.Combine(Folder, $"JawQuiz_{suffix}_{state}.png"));
        }

        private static void PrepareStaticState(JawQuizSceneController controller, JawQuizCompactPortraitUi compact, string state)
        {
            compact.CloseDrawer();
            controller.SetDiagnosticsVisible(false);
            var active = state != "before";
            Field<Text>(compact, "questionLabel").text = active ? "Find the Left Ramus" : "Press Start Quiz when you are ready.";
            Field<Text>(compact, "progressLabel").text = active ? "1 / 23" : "— / 23";
            Field<Text>(compact, "attemptLabel").text = active ? "Attempt 1 / 3" : "Attempt — / 3";
            Field<Button>(compact, "startButton").gameObject.SetActive(!active);
            Field<Button>(compact, "repeatButton").gameObject.SetActive(active);
            Field<Button>(compact, "hintButton").gameObject.SetActive(active);
            Field<Button>(compact, "skipButton").gameObject.SetActive(active);
            Field<Button>(compact, "nextButton").gameObject.SetActive(false);
            Field<Button>(compact, "nextButton").interactable = false;
            Invoke(compact, "SynchronizeActionRow");
            if (state == "feedback") compact.ShowFeedbackPreview("Incorrect — try the painted region farther back on the jaw.", new Color(1f, 0.4f, 0.38f));
            if (state == "drawer") compact.OpenDrawer();
            if (state == "diagnostics") compact.OpenDiagnosticsFromDrawer();
            Canvas.ForceUpdateCanvases();
        }

        private static void EnsureBuilt(JawQuizCompactPortraitUi compact)
        {
            if (Field<RectTransform>(compact, "hud") != null) return;
            Invoke(compact, "Awake");
        }

        private static T Field<T>(object target, string name) where T : class =>
            target.GetType().GetField(name, PrivateInstance)?.GetValue(target) as T;

        private static void Invoke(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, PrivateInstance)?.Invoke(target, args);

        private static void CaptureCamera(Camera camera, int width, int height, string path)
        {
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
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
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        public static void CaptureAndExit()
        {
            try { Capture(); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }
    }
}
