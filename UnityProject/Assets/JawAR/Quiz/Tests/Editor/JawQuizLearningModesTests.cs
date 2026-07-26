using System.Reflection;
using BMC.JawAR.Quiz.Learning;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.Quiz.Tests
{
    public sealed class JawQuizLearningModesTests
    {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private JawQuizSceneController controller;
        private JawQuizCompactPortraitUi compact;
        private JawQuizLearningModesController modes;
        private JawQuizSurfaceSelectionAdapter adapter;

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity", OpenSceneMode.Single);
            controller = Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            Assert.NotNull(controller);
            controller.EnsureInterface();
            compact = controller.GetComponent<JawQuizCompactPortraitUi>() ??
                      controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            if (compact.LearningModes == null)
                compact.GetType().GetMethod("Awake", PrivateInstance)?.Invoke(compact, null);
            modes = compact.LearningModes;
            adapter = controller.selectionAdapter;
            Assert.NotNull(modes);
            Assert.NotNull(adapter);
        }

        [Test]
        public void ModeSelection_ShowsThreeClearlyNamedModes()
        {
            Assert.True(modes.IsModeSelectionVisible);
            var root = controller.GetComponentInChildren<Canvas>(true).transform;
            Assert.NotNull(Find(root, "Find It Mode Card"));
            Assert.NotNull(Find(root, "What Is This? Mode Card"));
            Assert.NotNull(Find(root, "Two-Player Challenge Mode Card"));
        }

        [Test]
        public void FindIt_KeepsOriginalQuestionBankAndAcceptsOnlyPhysicalPointing()
        {
            modes.SelectMode(JawQuizLearningMode.FindIt);
            Assert.AreEqual(23, controller.questionBank.Questions.Count,
                "Find It remains wired to the unchanged starter question bank");
            Assert.True(adapter.acceptFingertipInput);
            Assert.False(adapter.acceptScreenInput, "screen taps cannot submit Find It answers");
        }

        [Test]
        public void SwitchingModes_ClearsTemporaryStateButKeepsTrackingAndMute()
        {
            var tracker = controller.jawTracker;
            var markerSize = tracker.blackSquareSizeMeters;
            controller.ToggleMute();
            var muted = controller.SpeechMuted;
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
            Assert.AreEqual("LeftRamus", modes.SelectedRegionId);
            modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
            Assert.IsEmpty(modes.SelectedRegionId);
            Assert.AreEqual(muted, controller.SpeechMuted);
            Assert.AreEqual(markerSize, tracker.blackSquareSizeMeters);
        }

        [Test]
        public void WhatIsThis_PhysicalAndTapUseSameCanonicalRegionAndRepeat()
        {
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            Assert.True(adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.PhysicalFingertip));
            Assert.AreEqual("LeftRamus", modes.SelectedRegionId);
            Assert.True(adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap));
            Assert.AreEqual("LeftRamus", modes.SelectedRegionId);
            Assert.True(adapter.SimulateDetailedSelection("RightRamus", JawQuizSelectionSource.ScreenTap));
            Assert.AreEqual("RightRamus", modes.SelectedRegionId, "identification remains immediately repeatable");
        }

        [Test]
        public void WhatIsThis_MuteSuppressesSpeechButNotVisualIdentification()
        {
            if (!controller.SpeechMuted) controller.ToggleMute();
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
            Assert.AreEqual("LeftRamus", modes.SelectedRegionId);
            Assert.True(controller.SpeechMuted);
        }

        [Test]
        public void WhatIsThis_UnlabelledAndEmptyAreNeverGuessed()
        {
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            adapter.SimulateUnlabelledSelection();
            Assert.IsEmpty(modes.SelectedRegionId);
            adapter.SimulateEmptySpaceTap();
            Assert.IsEmpty(modes.SelectedRegionId);
        }

        [Test]
        public void TwoPlayer_OnlyPrivateScreenTapSetsTargetAndProducesNoSpeech()
        {
            modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
            var speech = (QuizLoggingSpeechService)typeof(JawQuizSceneController)
                .GetField("speech", PrivateInstance)?.GetValue(controller);
            var before = speech?.LastText ?? string.Empty;
            adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.PhysicalFingertip);
            Assert.IsEmpty(modes.TargetRegionId);
            adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
            Assert.AreEqual("LeftRamus", modes.TargetRegionId);
            Assert.AreEqual(JawTwoPlayerPhase.ConfirmTarget, modes.TwoPlayerPhase);
            Assert.AreEqual(before, speech?.LastText ?? string.Empty, "private target selection must produce no TTS");
        }

        [Test]
        public void TwoPlayer_ReadyHidesTargetAndOnlyPhysicalAnswerCanEvaluate()
        {
            BeginPlayerTwoTurn("LeftRamus");
            Assert.AreEqual(JawTwoPlayerPhase.PlayerTwoAnswer, modes.TwoPlayerPhase);
            Assert.IsEmpty(controller.paintedRegions.HighlightedRegionId);
            adapter.SimulateDetailedSelection("RightRamus", JawQuizSelectionSource.ScreenTap);
            Assert.Zero(modes.CurrentAttempts, "screen tapping cannot change or evaluate the target during Player 2's turn");
            Assert.AreEqual("LeftRamus", modes.TargetRegionId);
            adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.PhysicalFingertip);
            Assert.AreEqual(JawTwoPlayerPhase.Result, modes.TwoPlayerPhase);
            Assert.AreEqual(1, modes.CorrectChallenges);
            Assert.AreEqual(1, modes.CompletedChallenges);
        }

        [Test]
        public void TwoPlayer_RetriesFinalRevealScoringAndRoleSwitchWork()
        {
            BeginPlayerTwoTurn("LeftRamus");
            adapter.SimulateDetailedSelection("RightRamus", JawQuizSelectionSource.PhysicalFingertip);
            Assert.AreEqual(JawTwoPlayerPhase.PlayerTwoAnswer, modes.TwoPlayerPhase);
            Assert.AreEqual(1, modes.CurrentAttempts);
            adapter.SimulateDetailedSelection("RightRamus", JawQuizSelectionSource.PhysicalFingertip);
            adapter.SimulateDetailedSelection("RightRamus", JawQuizSelectionSource.PhysicalFingertip);
            Assert.AreEqual(JawTwoPlayerPhase.Result, modes.TwoPlayerPhase);
            Assert.AreEqual("LeftRamus", controller.paintedRegions.HighlightedRegionId);
            Assert.AreEqual(1, modes.CompletedChallenges);
            Assert.Zero(modes.CorrectChallenges);
            Assert.True(modes.ChallengeRecords[0].unsuccessful);
            StringAssert.Contains("LeftRamus", modes.ChallengeRecords[0].confusionPair);
            modes.SwitchPlayers();
            Assert.AreEqual(1, modes.RoleChangeCount);
            Assert.AreEqual(JawTwoPlayerPhase.ChooseTargetPrivately, modes.TwoPlayerPhase);
        }

        [Test]
        public void DrawerAndModeSelection_BlockAllAnatomicalInput()
        {
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            compact.OpenDrawer();
            modes.GetType().GetMethod("LateUpdate", PrivateInstance)?.Invoke(modes, null);
            Assert.True(adapter.BlockingOverlayOpen);
            var before = modes.SelectedRegionId;
            adapter.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap);
            Assert.AreEqual(before, modes.SelectedRegionId);
            compact.CloseDrawer();
            modes.ReturnToModeSelection();
            Assert.False(adapter.AcceptingSelections);
        }

        [Test]
        public void OverlayMode_DoesNotChangeTriangleLookupOrMapData()
        {
            var map = controller.surfaceTarget.regionMap;
            var labelled = map.TotalLabelledTriangleCount;
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            modes.CycleOverlaySetting();
            modes.CycleOverlaySetting();
            modes.CycleOverlaySetting();
            Assert.AreEqual(labelled, map.TotalLabelledTriangleCount);
            Assert.AreSame(map.GetRegion("LeftRamus"), map.GetRegion("LeftRamus"));
        }

        private void BeginPlayerTwoTurn(string target)
        {
            modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
            adapter.SimulateDetailedSelection(target, JawQuizSelectionSource.ScreenTap);
            modes.ConfirmPrivateTarget();
            modes.PlayerTwoReady();
        }

        private static Transform Find(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var result = Find(root.GetChild(i), name);
                if (result != null) return result;
            }
            return null;
        }
    }
}
