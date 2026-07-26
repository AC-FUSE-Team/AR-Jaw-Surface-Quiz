using System.Reflection;
using BMC.JawAR.SurfaceRegions;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz.Tests
{
    public sealed class JawQuizCompactPortraitUiTests
    {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private JawQuizSceneController controller;
        private JawQuizCompactPortraitUi compact;

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity", OpenSceneMode.Single);
            controller = Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            Assert.NotNull(controller);
            controller.EnsureInterface();
            controller.GetType().GetMethod("EnsureLearningServices", PrivateInstance)?.Invoke(controller, null);
            compact = controller.GetComponent<JawQuizCompactPortraitUi>() ??
                      controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            EnsureBuilt();
        }

        [Test]
        public void Drawer_StartsClosed_AndOpensCloses()
        {
            Assert.False(compact.DrawerOpen);
            compact.OpenDrawer();
            Assert.True(compact.DrawerOpen);
            compact.CloseDrawer();
            Assert.False(compact.DrawerOpen);
        }

        [Test]
        public void DrawerAndHud_FitStandardNote9PortraitViewport()
        {
            compact.SetPreviewResolution(1080, 2220);
            compact.OpenDrawer();
            var hud = Field<RectTransform>(compact, "hud");
            var drawer = Field<RectTransform>(compact, "drawer");
            Assert.NotNull(hud);
            Assert.NotNull(drawer);
            Assert.GreaterOrEqual(hud.anchorMin.x, 0f);
            Assert.LessOrEqual(hud.anchorMax.x, 1f);
            Assert.GreaterOrEqual(hud.anchorMin.y, 0f);
            Assert.LessOrEqual(hud.anchorMax.y, 1f);
            Assert.AreEqual(Vector2.zero, drawer.anchorMin);
            Assert.AreEqual(1f, drawer.anchorMax.y);
            Assert.Greater(drawer.anchorMax.x, 0f);
            Assert.Less(drawer.anchorMax.x, 1f);
        }

        [Test]
        public void Drawer_BackdropCloses_AndBlocksJawRaycastsWhileOpen()
        {
            compact.OpenDrawer();
            var backdrop = Field<RectTransform>(compact, "drawerBackdrop");
            Assert.NotNull(backdrop);
            Assert.True(backdrop.GetComponent<Image>().raycastTarget);
            Assert.True(backdrop.GetComponent<Button>().interactable);
            compact.CloseDrawerFromBackdrop();
            Assert.False(compact.DrawerOpen);
        }

        [Test]
        public void HiddenProfileControl_RemainsFunctionalAfterReopening()
        {
            compact.OpenDrawer();
            var profile = Field<Button>(compact, "profileButton");
            var before = profile.GetComponentInChildren<Text>().text;
            profile.onClick.Invoke();
            var afterFirstOpen = profile.GetComponentInChildren<Text>().text;
            compact.CloseDrawer();
            compact.OpenDrawer();
            profile.onClick.Invoke();
            var afterReopen = profile.GetComponentInChildren<Text>().text;
            Assert.AreNotEqual(before, afterFirstOpen);
            Assert.AreNotEqual(afterFirstOpen, afterReopen);
        }

        [Test]
        public void Gameplay_HasNoPermanentBottomPanel_AndKeepsEssentials()
        {
            Assert.False(compact.HasPermanentBottomPanel);
            Assert.True(compact.EssentialControlsAvailable);
            var bottom = Field<RectTransform>(controller, "controlsPanel");
            Assert.NotNull(bottom);
            Assert.False(bottom.gameObject.activeSelf);
            // questionLabel moved from legacy Text to TMP_Text as part of the Material 3-inspired
            // redesign (Assets/JawAR/Quiz/Runtime/Material) — the NotNull check's intent (compact
            // UI exposes its own question label, independent of the controller's bottom panel) is
            // unchanged, only the concrete text component type it now resolves to.
            Assert.NotNull(Field<TMP_Text>(compact, "questionLabel"));
            Assert.NotNull(Field<Button>(compact, "repeatButton"));
            Assert.NotNull(Field<Button>(compact, "hintButton"));
            Assert.NotNull(Field<Button>(compact, "skipButton"));
            Assert.NotNull(Field<Button>(compact, "nextButton"));
        }

        [Test]
        public void Diagnostics_RequiresDeliberateDrawerAction()
        {
            Assert.False(compact.DiagnosticsOpen);
            Assert.False(compact.DiagnosticsEntryVisible);
            compact.OpenDrawer();
            Assert.True(compact.DiagnosticsEntryVisible);
            compact.OpenDiagnosticsFromDrawer();
            Assert.False(compact.DrawerOpen);
            Assert.True(compact.DiagnosticsOpen);
        }

        [Test]
        public void OverlayVisibility_AndTriangleLookupRemainIndependent()
        {
            var target = Object.FindFirstObjectByType<JawSurfaceRegionTarget>(FindObjectsInactive.Include);
            var presenter = Object.FindFirstObjectByType<JawQuizPaintedRegionPresenter>(FindObjectsInactive.Include);
            Assert.NotNull(target);
            Assert.NotNull(presenter);
            var lookupBefore = target.surfaceLookupEnabled;
            var visibleBefore = presenter.PaintedRegionsVisible;
            Field<Button>(compact, "overlayButton").onClick.Invoke();
            Assert.AreEqual(lookupBefore, target.surfaceLookupEnabled);
            Assert.AreNotEqual(visibleBefore, presenter.PaintedRegionsVisible);
            Field<Button>(compact, "overlayButton").onClick.Invoke();
            Assert.AreEqual(visibleBefore, presenter.PaintedRegionsVisible);
        }

        [Test]
        public void TrackingAndGradingConfiguration_AreUntouched()
        {
            var tracker = Object.FindFirstObjectByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include);
            var target = Object.FindFirstObjectByType<JawSurfaceRegionTarget>(FindObjectsInactive.Include);
            Assert.AreSame(tracker, controller.jawTracker);
            Assert.AreEqual(1280, tracker.detectionLongEdge);
            Assert.AreEqual(6f, tracker.detectionsPerSecond);
            Assert.AreEqual(24, tracker.stableDetectionsRequired);
            Assert.AreEqual(30, tracker.lockSampleWindowSize);
            Assert.AreEqual(0.001f, tracker.maxPositionSpreadMeters);
            Assert.AreEqual(1f, tracker.maxRotationSpreadDegrees);
            Assert.AreEqual(3, controller.maxAttemptsPerQuestion);
            Assert.True(target.surfaceLookupEnabled);
            Assert.AreEqual(23, controller.questionBank.Questions.Count);
        }

        private void EnsureBuilt()
        {
            if (Field<RectTransform>(compact, "hud") != null) return;
            compact.GetType().GetMethod("Awake", PrivateInstance)?.Invoke(compact, null);
        }

        private static T Field<T>(object target, string name) where T : class =>
            target.GetType().GetField(name, PrivateInstance)?.GetValue(target) as T;
    }
}
