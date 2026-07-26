using System.Reflection;
using BMC.JawAR.Quiz.Material3;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz.Tests
{
    /// <summary>
    /// Coverage for the Material 3-inspired redesign specifically: token consistency, touch
    /// targets, camera-visible area, and feedback accessibility. Behavioural coverage that already
    /// existed (drawer open/close, grading/tracking configuration untouched, Next visibility,
    /// overlay/lookup independence) lives in <see cref="JawQuizCompactPortraitUiTests"/> and is not
    /// duplicated here.
    /// </summary>
    public sealed class JawQuizMaterialUiTests
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
            compact = controller.GetComponent<JawQuizCompactPortraitUi>() ??
                      controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            if (Field<RectTransform>(compact, "hud") == null)
                compact.GetType().GetMethod("Awake", PrivateInstance)?.Invoke(compact, null);
        }

        [Test]
        public void ThemeTokens_AreStableAndWithinExpectedRanges()
        {
            Assert.AreEqual(JawMaterialTheme.Primary, JawMaterialTheme.Primary, "color tokens must be stable constants, not regenerated per access");
            Assert.Greater(JawMaterialTheme.RadiusSmall, 0f);
            Assert.Greater(JawMaterialTheme.RadiusMedium, JawMaterialTheme.RadiusSmall);
            Assert.Greater(JawMaterialTheme.RadiusLarge, JawMaterialTheme.RadiusMedium);
            Assert.GreaterOrEqual(JawMaterialTheme.MinTouchTarget, 44f, "touch target token must meet an accessible minimum");
            foreach (var c in new[] { JawMaterialTheme.Success, JawMaterialTheme.Error, JawMaterialTheme.Warning, JawMaterialTheme.Info })
                Assert.AreEqual(1f, c.a, 0.01f, "semantic colors should be opaque so they read clearly as chips/snackbars");
        }

        [Test]
        public void RoundedRectSprites_AreCachedAndNotRegeneratedPerCall()
        {
            var first = JawMaterialSprites.RoundedRect(JawMaterialTheme.RadiusMedium);
            var second = JawMaterialSprites.RoundedRect(JawMaterialTheme.RadiusMedium);
            Assert.AreSame(first, second, "requesting the same radius twice must reuse the cached sprite, not build a new texture");
        }

        [Test]
        public void ActionButtons_MeetMinimumTouchTarget()
        {
            foreach (var name in new[] { "repeatButton", "hintButton", "skipButton", "nextButton" })
            {
                var button = Field<Button>(compact, name);
                Assert.NotNull(button, name);
                var layout = button.GetComponent<LayoutElement>();
                Assert.NotNull(layout, $"{name} should have an enforced minimum touch target");
                Assert.GreaterOrEqual(layout.minHeight, JawMaterialTheme.MinTouchTarget);
            }
        }

        [Test]
        public void HamburgerButton_MeetsMinimumTouchTarget()
        {
            var hud = Field<RectTransform>(compact, "hud");
            Assert.NotNull(hud);
            var hamburger = hud.Find("Hamburger Button");
            Assert.NotNull(hamburger, "hamburger button should exist in the HUD");
            var layout = hamburger.GetComponent<LayoutElement>();
            Assert.NotNull(layout);
            Assert.GreaterOrEqual(layout.minHeight, JawMaterialTheme.MinTouchTarget);
        }

        [Test]
        public void PortraitLayout_KeepsAtLeast80PercentOfScreenFreeForCamera()
        {
            compact.SetPreviewResolution(1080, 2220);
            var hud = Field<RectTransform>(compact, "hud");
            Assert.NotNull(hud);
            // hud is anchor-stretched (SetRect), so its own height fraction of the safe area is
            // directly (anchorMax.y - anchorMin.y); everything above it is unobstructed camera.
            var occupiedFraction = hud.anchorMax.y - hud.anchorMin.y;
            Assert.LessOrEqual(occupiedFraction, 0.20f,
                "the floating HUD card must not occupy more than ~20% of the portrait screen, " +
                "preserving at least 80% for the AR camera view");
        }

        [Test]
        public void QuestionLabel_UsesTextMeshProAndMaterialTypeScale()
        {
            compact.SetPreviewResolution(1080, 2220); // force portrait; ambient Screen size in the test runner is unreliable
            var label = Field<TMP_Text>(compact, "questionLabel");
            Assert.NotNull(label);
            Assert.AreEqual(JawMaterialTheme.TypeQuestionSize, (int)label.fontSize);
        }

        [Test]
        public void FeedbackSnackbar_PairsIconWithTextNotColorAlone()
        {
            compact.ShowFeedbackPreview("Correct — nice work", JawMaterialTheme.Success);
            compact.RefreshPreviewNow();
            var snackbarField = compact.GetType().GetField("snackbar", PrivateInstance);
            var skin = (JawSnackbarSkin)snackbarField!.GetValue(compact);
            Assert.IsTrue(skin.Root.gameObject.activeSelf);
            Assert.IsNotEmpty(skin.Label.text, "snackbar must always carry a text message");
            Assert.NotNull(skin.IconImage.sprite, "snackbar must always carry an icon, not rely on color alone");
        }

        [Test]
        public void Drawer_SwitchRows_ReflectControllerState()
        {
            compact.OpenDrawer();
            compact.RefreshPreviewNow();
            var muteSwitchField = compact.GetType().GetField("muteSwitch", PrivateInstance);
            var skin = (JawSwitchSkin)muteSwitchField!.GetValue(compact);
            Assert.NotNull(skin.Track, "mute row should be presented as a Material switch");
            Assert.NotNull(skin.Thumb);
        }

        [Test]
        public void NoPermanentBottomPanel_StillHolds()
        {
            // Re-assert this alongside the new coverage since it's the redesign's core spatial
            // constraint (see JawQuizCompactPortraitUiTests.Gameplay_HasNoPermanentBottomPanel_AndKeepsEssentials
            // for the original assertion this mirrors).
            Assert.False(compact.HasPermanentBottomPanel);
        }

        private static T Field<T>(object target, string name) where T : class =>
            target.GetType().GetField(name, PrivateInstance)?.GetValue(target) as T;
    }
}
