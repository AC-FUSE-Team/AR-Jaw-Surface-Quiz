using BMC.JawAR.SurfaceRegions;
using NUnit.Framework;
using BMC.JawAR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.Quiz.Tests
{
    public sealed class JawQuizSceneConfigurationTests
    {
        [Test]
        public void StarterBank_UsesAllActualIdsAndOmitsMissingBodyId()
        {
            var bank = AssetDatabase.LoadAssetAtPath<JawQuizQuestionBank>(
                "Assets/JawAR/Quiz/Data/JawQuizStarterBank.asset");
            Assert.NotNull(bank);
            Assert.AreEqual(23, bank.Questions.Count);
            foreach (var question in bank.Questions)
            {
                Assert.IsNotEmpty(question.QuestionId);
                Assert.IsNotEmpty(question.ExpectedRegionId);
                Assert.AreNotEqual("BodyOfMandible", question.ExpectedRegionId);
            }
        }

        [Test]
        public void QuizScene_HasIsolatedEnabledLookupAndVisibleOverlay()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity", OpenSceneMode.Single);
            var target = Object.FindFirstObjectByType<JawSurfaceRegionTarget>(FindObjectsInactive.Include);
            var presenter = Object.FindFirstObjectByType<JawQuizPaintedRegionPresenter>(FindObjectsInactive.Include);
            var adapter = Object.FindFirstObjectByType<JawQuizSurfaceSelectionAdapter>(FindObjectsInactive.Include);
            var tracker = Object.FindFirstObjectByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include);
            var controller = Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            var coordinator = Object.FindFirstObjectByType<JawSurfaceRegionSelectionCoordinator>(FindObjectsInactive.Include);

            Assert.NotNull(target);
            Assert.True(target.surfaceLookupEnabled);
            Assert.AreEqual("Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap_CodexDraft.asset",
                AssetDatabase.GetAssetPath(target.regionMap));
            Assert.NotNull(presenter);
            Assert.True(presenter.visibleByDefault);
            Assert.NotNull(adapter);
            Assert.NotNull(controller);
            Assert.NotNull(tracker);
            Assert.AreSame(tracker, controller.jawTracker);
            controller.EnsureInterface();
            Assert.NotNull(controller.VisibleTrackingStatusText);
            Assert.AreSame(controller.VisibleTrackingStatusText, tracker.statusText);
            StringAssert.Contains("POINT CAMERA", controller.VisibleTrackingStatusText.text);
            Assert.AreEqual(1280, tracker.detectionLongEdge);
            Assert.AreEqual(6f, tracker.detectionsPerSecond);
            Assert.AreEqual(2f, tracker.trackingSettleSeconds);
            Assert.AreEqual(24, tracker.stableDetectionsRequired);
            Assert.AreEqual(30, tracker.lockSampleWindowSize);
            Assert.AreEqual(0.001f, tracker.maxPositionSpreadMeters);
            Assert.AreEqual(1f, tracker.maxRotationSpreadDegrees);
            Assert.AreEqual(4, tracker.stableWindowsRequired);
            Assert.AreEqual(0.015f, tracker.maxSampleDeviationMeters);
            Assert.AreEqual(7f, tracker.maxSampleAngularDeviationDegrees);
            Assert.False(controller.diagnosticMode,
                "Continuous pose logging should be off after the static-anchor diagnosis.");
            Assert.False(coordinator.enabled, "The legacy coordinator must remain disabled in the quiz scene.");
        }
    }
}
