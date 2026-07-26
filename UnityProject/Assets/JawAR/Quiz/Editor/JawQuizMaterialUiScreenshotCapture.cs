using System;
using System.IO;
using System.Reflection;
using BMC.JawAR.Quiz.Material3;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz.Editor
{
    /// <summary>
    /// Verification screenshots for the Material 3-inspired redesign. Unlike the v32 compact-UI
    /// capture tool (which renders over a flat solid color), this keeps a real on-device AR capture
    /// photo as the backdrop so screenshots show a representative camera/jaw scene rather than an
    /// empty background.
    /// </summary>
    public static class JawQuizMaterialUiScreenshotCapture
    {
        private const string Folder = "Artifacts/QuizMaterial3Redesign_20260722/Screenshots";
        private const string PortraitPhoto = "/home/omar/JawRepair/PhoneCaptures_20260716/Screenshot_20260716-234522_Jaw ArUco Anatomy.jpg";
        private const string LandscapePhoto = "/home/omar/JawRepair/PhoneCaptures_20260716/Screenshot_20260716-234532_Jaw ArUco Anatomy.jpg";
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly (int width, int height, string suffix, string state)[] Captures =
        {
            (1080, 2220, "1080x2220", "start"),
            (1080, 2220, "1080x2220", "active"),
            (1080, 2220, "1080x2220", "next_available"),
            (1080, 2220, "1080x2220", "correct"),
            (1080, 2220, "1080x2220", "incorrect"),
            (1080, 2220, "1080x2220", "tracking_collecting"),
            (1080, 2220, "1080x2220", "tracking_locked"),
            (1080, 2220, "1080x2220", "drawer"),
            (1080, 2220, "1080x2220", "offline_queued"),
            (1080, 2220, "1080x2220", "diagnostics"),
            (1080, 1920, "1080x1920", "active"),
            (2220, 1080, "2220x1080", "active"),
        };

        [MenuItem("Tools/Jaw Anatomy Quiz/Material 3/Capture Verification Screenshots")]
        public static void Capture()
        {
            Directory.CreateDirectory(Folder);
            foreach (var c in Captures)
                CaptureOneState(c.width, c.height, c.suffix, c.state);
            Debug.Log($"JAW_QUIZ_MATERIAL3_SCREENSHOTS_READY count={Captures.Length} folder={Folder}");
        }

        public static void CaptureAndExit()
        {
            try { Capture(); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); return; }
            EditorApplication.Exit(0);
        }

        private static void CaptureOneState(int width, int height, string suffix, string state)
        {
            EditorSceneManager.OpenScene(JawQuizSceneBuilder.QuizScenePath, OpenSceneMode.Single);
            var controller = UnityEngine.Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            if (controller == null || camera == null) throw new InvalidOperationException("Quiz controller or camera is missing.");
            foreach (var behaviour in camera.GetComponents<Behaviour>()) if (behaviour != camera) behaviour.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

            controller.EnsureInterface();
            var compact = controller.GetComponent<JawQuizCompactPortraitUi>() ?? controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            EnsureBuilt(compact);
            controller.SetPreviewResolution(width, height);
            compact.SetPreviewResolution(width, height);

            var canvas = controller.GetComponentInChildren<Canvas>(true);
            InsertBackdropPhoto(canvas, width >= height ? LandscapePhoto : PortraitPhoto);

            PrepareState(controller, compact, state);

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = Mathf.Max(camera.nearClipPlane + 0.001f, 0.011f);
            Canvas.ForceUpdateCanvases();
            CaptureCamera(camera, width, height, Path.Combine(Folder, $"JawQuiz_{suffix}_{state}.png"));
        }

        private static void InsertBackdropPhoto(Canvas canvas, string photoPath)
        {
            var root = canvas.GetComponent<RectTransform>();
            var existing = root.Find("Screenshot Backdrop");
            RawImage image;
            if (existing != null)
            {
                image = existing.GetComponent<RawImage>();
            }
            else
            {
                var go = new GameObject("Screenshot Backdrop", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(root, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                image = go.AddComponent<RawImage>();
            }
            image.rectTransform.SetAsFirstSibling();
            var bytes = File.ReadAllBytes(photoPath);
            var tex = new Texture2D(2, 2);
            tex.LoadImage(bytes);
            image.texture = tex;
        }

        private static void PrepareState(JawQuizSceneController controller, JawQuizCompactPortraitUi compact, string state)
        {
            compact.CloseDrawer();
            controller.SetDiagnosticsVisible(false);

            // Set controller-side source text FIRST (stateText/personalizedText for the
            // offline/queued case), then run one real SynchronizeVisibleText pass so it correctly
            // flows into the drawer status chip. Everything set on the compact UI's OWN labels
            // (questionLabel/progressChipLabel/attemptChipLabel) must happen AFTER this call, since
            // SynchronizeVisibleText always re-pulls those three from the controller's real
            // (never-actually-started) underlying text and would otherwise clobber the overrides.
            if (state == "offline_queued")
            {
                var stateText = Field<Text>(controller, "stateText");
                if (stateText != null) stateText.text = "OFFLINE • QUEUED • 2";
                var personalizedText = Field<Text>(controller, "personalizedText");
                if (personalizedText != null) personalizedText.text = "Offline • Local anatomy feedback remains available.";
            }
            Invoke(compact, "SynchronizeVisibleText");

            var startState = state == "start";
            Field<TMP_Text>(compact, "questionLabel").text = startState
                ? "Press Start Quiz when you are ready."
                : "Find the Left Ramus";
            Field<TMP_Text>(compact, "progressChipLabel").text = startState ? "— / 23" : "9 / 23";
            Field<TMP_Text>(compact, "attemptChipLabel").text = startState ? "Attempt — / 3" : "Attempt 1 / 3";
            Field<Button>(compact, "startButton").gameObject.SetActive(startState);
            Field<Button>(compact, "repeatButton").gameObject.SetActive(!startState);
            Field<Button>(compact, "hintButton").gameObject.SetActive(!startState);
            Field<Button>(compact, "skipButton").gameObject.SetActive(!startState);
            var next = Field<Button>(compact, "nextButton");
            var showNext = state == "next_available";
            next.gameObject.SetActive(showNext);
            next.interactable = showNext;

            switch (state)
            {
                case "correct":
                    compact.ShowFeedbackPreview("Correct — great work identifying the ramus.", JawMaterialTheme.Success);
                    break;
                case "incorrect":
                    compact.ShowFeedbackPreview("Incorrect — try the painted region farther back on the jaw.", JawMaterialTheme.Error);
                    break;
                case "tracking_collecting":
                    SetTracking(compact, controller, locked: false, "HOLD STILL — COLLECTING 4/8");
                    break;
                case "tracking_locked":
                    SetTracking(compact, controller, locked: true, "JAW LOCKED IN PLACE — MOVE CAMERA AROUND IT");
                    break;
                case "drawer":
                    compact.OpenDrawer();
                    SnapDrawerOpen(compact);
                    break;
                case "offline_queued":
                    compact.OpenDrawer();
                    SnapDrawerOpen(compact);
                    break;
                case "diagnostics":
                    compact.OpenDiagnosticsFromDrawer();
                    break;
            }

            Invoke(compact, "SynchronizeActionRow");
            Invoke(compact, "UpdateTransientOverlays");
            // In Editor mode (no Player loop running) TMP's own deferred mesh generation, which
            // normally rides Canvas.willRenderCanvases during Play mode, doesn't reliably fire —
            // without this, TMP_Text components have correct layout/bounds but empty geometry, so
            // camera.Render() captures invisible text. Settle layout first (so labels inside
            // HorizontalLayoutGroups have their final rect), then force every TMP label to rebuild
            // its mesh against that settled rect, then do one more layout pass.
            Canvas.ForceUpdateCanvases();
            foreach (var text in controller.GetComponentInChildren<Canvas>(true).GetComponentsInChildren<TMP_Text>(true))
            {
                text.SetAllDirty();
                text.ForceMeshUpdate();
            }
            Canvas.ForceUpdateCanvases();
        }

        // OpenDrawer() kicks off a slide-in coroutine that only advances on real Update() ticks,
        // which never happen in this single-frame Editor-mode capture — without this, the drawer
        // would be captured mid-slide (its very first partial animation step) instead of fully
        // open. Snap it straight to its resting position.
        private static void SnapDrawerOpen(JawQuizCompactPortraitUi compact)
        {
            var drawer = Field<RectTransform>(compact, "drawer");
            if (drawer != null) drawer.anchoredPosition = Vector2.zero;
        }

        private static void SetTracking(JawQuizCompactPortraitUi compact, JawQuizSceneController controller, bool locked, string message)
        {
            var tracker = controller.jawTracker;
            if (tracker != null)
            {
                var backingField = tracker.GetType().GetField("<WorldPoseLocked>k__BackingField", PrivateInstance);
                backingField?.SetValue(tracker, locked);
            }
            var trackingPanel = Field<RectTransform>(compact, "trackingPanel");
            if (trackingPanel == null) return;
            trackingPanel.gameObject.SetActive(true);
            var text = trackingPanel.GetComponentInChildren<Text>(true);
            if (text != null) text.text = message;
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
    }
}
