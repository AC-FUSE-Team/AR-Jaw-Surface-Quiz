using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    public static class JawQuizLearningScreenshotCapture
    {
        private const string DirectoryPath = "Artifacts/BackboardQuizMock";

        public static void CaptureAndExit()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
                var controller = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(
                    FindObjectsInactive.Include);
                var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
                if (controller == null || camera == null) throw new InvalidOperationException("Quiz UI missing");
                foreach (var behaviour in camera.GetComponents<Behaviour>())
                    if (behaviour != camera) behaviour.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.025f, 0.055f, 0.085f, 1f);
                controller.EnsureInterface();
                var canvas = controller.GetComponentInChildren<Canvas>(true);
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = Mathf.Max(camera.nearClipPlane + 0.001f, 0.011f);
                controller.SetPreviewResolution(1080, 2220);
                CaptureState(controller, camera, "connected", "student_connected_1080x2220.png");
                CaptureState(controller, camera, "offline", "student_offline_queued_1080x2220.png");
                CaptureState(controller, camera, "synchronized", "student_synchronized_1080x2220.png");
                Debug.Log("JAW_QUIZ_LEARNING_SCREENSHOTS_READY " + DirectoryPath);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        private static void CaptureState(JawQuizSceneController controller, Camera camera,
            string state, string filename)
        {
            controller.PrepareLearningScreenshotPreview(state);
            Canvas.ForceUpdateCanvases();
            Capture(camera, 1080, 2220, Path.Combine(DirectoryPath, filename));
        }

        private static void Capture(Camera camera, int width, int height, string path)
        {
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var oldActive = RenderTexture.active;
            var oldTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target; RenderTexture.active = target; camera.Render();
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); texture.Apply(false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = oldTarget; RenderTexture.active = oldActive;
                UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
