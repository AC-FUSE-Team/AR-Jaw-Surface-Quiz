using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    public static class JawQuizScreenshotCapture
    {
        private const string ScreenshotFolder = "Assets/JawAR/Quiz/Screenshots/";
        private const string BeforePath = ScreenshotFolder + "JawQuiz_Portrait_1080x2220_BeforeStart.png";
        private const string DuringPath = ScreenshotFolder + "JawQuiz_Portrait_1080x2220_DuringQuestion.png";
        private const string FeedbackPath = ScreenshotFolder + "JawQuiz_Portrait_1080x2220_Feedback.png";
        private const string DiagnosticsPath = ScreenshotFolder + "JawQuiz_Portrait_1080x2220_Diagnostics.png";
        private const string ShortPortraitPath = ScreenshotFolder + "JawQuiz_Portrait_1080x1920_DuringQuestion.png";
        private const string LandscapePath = ScreenshotFolder + "JawQuiz_Landscape_2220x1080_DuringQuestion.png";

        [MenuItem("Tools/Jaw Anatomy Quiz/Capture Responsive UI Screenshots")]
        public static void Capture()
        {
            EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
            var controller = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (controller == null || camera == null)
                throw new InvalidOperationException("Quiz controller or camera is missing.");

            foreach (var behaviour in camera.GetComponents<Behaviour>())
                if (behaviour != camera) behaviour.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.055f, 0.085f, 1f);

            controller.EnsureInterface();
            var canvas = controller.GetComponentInChildren<Canvas>(true);
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = Mathf.Max(camera.nearClipPlane + 0.001f, 0.011f);

            CaptureState(controller, camera, 1080, 2220, BeforePath, controller.PrepareScreenshotBeforeStart);
            CaptureState(controller, camera, 1080, 2220, DuringPath,
                () => controller.PrepareScreenshotPreview(false));
            CaptureState(controller, camera, 1080, 2220, FeedbackPath, controller.PrepareScreenshotFeedback);
            CaptureState(controller, camera, 1080, 2220, DiagnosticsPath,
                () => controller.PrepareScreenshotPreview(true));
            CaptureState(controller, camera, 1080, 1920, ShortPortraitPath,
                () => controller.PrepareScreenshotPreview(false));
            CaptureState(controller, camera, 2220, 1080, LandscapePath,
                () => controller.PrepareScreenshotPreview(false));

            Debug.Log("JAW_QUIZ_RESPONSIVE_SCREENSHOTS_READY count=6 folder=" + ScreenshotFolder);
        }

        private static void CaptureState(JawQuizSceneController controller, Camera camera,
            int width, int height, string path, Action prepare)
        {
            controller.SetPreviewResolution(width, height);
            prepare();
            Canvas.ForceUpdateCanvases();
            CaptureOne(camera, width, height, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void CaptureOne(Camera camera, int width, int height, string path)
        {
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

            var previousActive = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                Canvas.ForceUpdateCanvases();
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
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }
    }
}
