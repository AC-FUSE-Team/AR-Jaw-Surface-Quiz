using System.Reflection;
using BMC.JawAR;
using BMC.JawAR.SurfaceRegions;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.Quiz.Tests
{
    /// <summary>
    /// Bounded EditMode tests for the v36 input-usability fix. These target the two proven v35
    /// root causes directly:
    /// (A) a periodic background UI refresh was force-disabling anatomical input in every mode
    ///     except Find It (What Is This / Two-Player stopped responding to taps after ~8s), and
    /// (B) the physical fingertip router keeps completing dwell selections even while the quiz has
    ///     input disabled, so the moment a new question/phase armed input it immediately graded a
    ///     stale selection the student never intended for the new state.
    /// </summary>
    public sealed class JawQuizInputArbitrationTests
    {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class FakeClock
        {
            public float Now;
            public FakeClock(float start) => Now = start;
            public float Get() => Now;
        }

        private static void PumpUpdate(JawQuizSurfaceSelectionAdapter adapter)
        {
            typeof(JawQuizSurfaceSelectionAdapter).GetMethod("Update", PrivateInstance)?.Invoke(adapter, null);
        }

        // ---- Pure adapter-level tests (standalone GameObjects, no scene) ----

        private JawSurfaceRegionMap map;
        private GameObject targetGo;
        private GameObject adapterGo;
        private GameObject routerGo;
        private JawSurfaceRegionTarget target;
        private JawQuizSurfaceSelectionAdapter adapter;
        private JawSurfaceFingertipRouter router;

        [SetUp]
        public void SetUp()
        {
            map = ScriptableObject.CreateInstance<JawSurfaceRegionMap>();
            map.InitializeDefaultRegions();
            targetGo = new GameObject("Target");
            target = targetGo.AddComponent<JawSurfaceRegionTarget>();
            target.regionMap = map;
            adapterGo = new GameObject("Adapter");
            adapter = adapterGo.AddComponent<JawQuizSurfaceSelectionAdapter>();
            adapter.surfaceTarget = target;
            routerGo = new GameObject("Router");
            router = routerGo.AddComponent<JawSurfaceFingertipRouter>();
            router.mode = JawSurfaceFingertipRouter.FingertipSelectionMode.SurfaceRegionsOnly;
            router.dwellSeconds = 0.05f;
            adapter.fingertipRouter = router;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(adapterGo);
            Object.DestroyImmediate(routerGo);
            Object.DestroyImmediate(targetGo);
            Object.DestroyImmediate(map);
        }

        [Test]
        public void Arm_SnapshotsAlreadyCompletedDwell_SoItIsNeverReplayed()
        {
            var clock = new FakeClock(0f);
            router.TimeProvider = clock.Get;
            adapter.TimeProvider = clock.Get;
            var region = map.GetRegion("LeftRamus");

            // A dwell selection completes *before* anything arms input (e.g. left over from the
            // previous question's feedback window), then the finger lifts.
            router.ProcessSurfaceHit(true, region, 3);
            clock.Now += 0.1f;
            router.ProcessSurfaceHit(true, region, 3);
            Assert.NotNull(router.LastSelectedRegion);
            router.ProcessSurfaceHit(false, null, -1);

            string received = null;
            adapter.DetailedSelectionReceived += selection => received = selection.StableId;
            Assert.True(adapter.Arm(0f), "arming should succeed while tracking is ready");
            PumpUpdate(adapter);

            Assert.IsNull(received, "a dwell completed before Arm() must not be replayed after arming");
        }

        [Test]
        public void Arm_WithFingerStillResting_RequiresLiftBeforeAnyNewSelectionCounts()
        {
            var clock = new FakeClock(0f);
            router.TimeProvider = clock.Get;
            adapter.TimeProvider = clock.Get;
            var region = map.GetRegion("LeftRamus");

            router.ProcessSurfaceHit(true, region, 3); // finger currently resting, dwell not done yet
            string received = null;
            adapter.DetailedSelectionReceived += selection => received = selection.StableId;
            adapter.Arm(0f);

            // Dwell completes *after* arming, but the finger never left the surface first.
            clock.Now += 0.1f;
            router.ProcessSurfaceHit(true, region, 3);
            PumpUpdate(adapter);
            Assert.IsNull(received, "a selection must not count until the pre-arm finger has lifted at least once");

            // Now the finger lifts and dwells fresh -- this is the first deliberate selection.
            router.ProcessSurfaceHit(false, null, -1);
            PumpUpdate(adapter);
            clock.Now += 0.1f;
            router.ProcessSurfaceHit(true, region, 3);
            clock.Now += 0.1f;
            router.ProcessSurfaceHit(true, region, 3);
            PumpUpdate(adapter);
            Assert.AreEqual("LeftRamus", received, "a fresh dwell after release must be graded");
        }

        [Test]
        public void Arm_DebounceWindow_DelaysButDoesNotDropASelection()
        {
            var clock = new FakeClock(10f);
            router.TimeProvider = clock.Get;
            adapter.TimeProvider = clock.Get;
            var region = map.GetRegion("LeftRamus");

            string received = null;
            adapter.DetailedSelectionReceived += selection => received = selection.StableId;
            adapter.Arm(0.2f); // armedAt = 10.2

            clock.Now = 10.05f; // still inside the debounce window
            router.ProcessSurfaceHit(true, region, 3);
            // The router clamps its effective dwell divisor to a 0.1s floor regardless of the
            // configured dwellSeconds, so the next sample must be >=0.1s after candidateSince.
            clock.Now = 10.16f;
            router.ProcessSurfaceHit(true, region, 3); // dwell completes at 10.16, inside the window
            PumpUpdate(adapter);
            Assert.IsNull(received, "nothing should be graded before the debounce interval elapses");

            clock.Now = 10.25f; // past armedAt
            PumpUpdate(adapter);
            Assert.AreEqual("LeftRamus", received, "the same completed dwell should be accepted once debounce elapses");
        }

        [Test]
        public void HeldFingertipSelection_IsNotRepeatedlySubmitted()
        {
            var clock = new FakeClock(0f);
            router.TimeProvider = clock.Get;
            adapter.TimeProvider = clock.Get;
            var region = map.GetRegion("LeftRamus");
            var count = 0;
            adapter.DetailedSelectionReceived += _ => count++;
            adapter.Arm(0f);

            clock.Now += 0.1f;
            router.ProcessSurfaceHit(true, region, 3);
            clock.Now += 0.1f;
            router.ProcessSurfaceHit(true, region, 3); // dwell completes once
            PumpUpdate(adapter);
            Assert.AreEqual(1, count);

            // Finger stays on the exact same region for several more frames without lifting.
            for (var i = 0; i < 5; i++)
            {
                clock.Now += 0.05f;
                router.ProcessSurfaceHit(true, region, 3);
                PumpUpdate(adapter);
            }
            Assert.AreEqual(1, count, "a finger held in place must not be graded again");
        }

        [Test]
        public void TrackingNotReady_NeverAcceptsOrGradesInput()
        {
            adapter.TrackingReady = false;
            adapter.Arm(0f);
            Assert.False(adapter.AcceptingSelections, "Arm() must refuse to arm while tracking is not ready");
        }

        // ---- Scene-level regression tests (proves the two confirmed v35 bugs are fixed) ----

        private JawQuizSceneController controller;
        private JawQuizCompactPortraitUi compact;
        private JawQuizLearningModesController modes;

        private void LoadQuizScene()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity", OpenSceneMode.Single);
            controller = Object.FindFirstObjectByType<JawQuizSceneController>(FindObjectsInactive.Include);
            Assert.NotNull(controller);
            // Edit Mode never invokes MonoBehaviour.Awake() automatically (no [ExecuteAlways], no
            // Play Mode), so the scene controller's own DetailedSelectionReceived subscription
            // (Find It's grading path) would otherwise never exist in this test harness.
            typeof(JawQuizSceneController).GetMethod("Awake", PrivateInstance)?.Invoke(controller, null);
            controller.EnsureInterface();
            compact = controller.GetComponent<JawQuizCompactPortraitUi>() ??
                      controller.gameObject.AddComponent<JawQuizCompactPortraitUi>();
            if (compact.LearningModes == null)
                compact.GetType().GetMethod("Awake", PrivateInstance)?.Invoke(compact, null);
            modes = compact.LearningModes;
        }

        [Test]
        public void PeriodicBackgroundRefresh_DoesNotDisableWhatIsThisInput()
        {
            LoadQuizScene();
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            var adapterUnderTest = controller.selectionAdapter;
            Assert.True(adapterUnderTest.AcceptingSelections);

            // This reproduces exactly what the periodic proxy-status-check coroutine used to do
            // every ~8 seconds regardless of learning mode: call the scene controller's own
            // RefreshUi(). Before the fix this unconditionally set AcceptingSelections = false
            // whenever `engine` was null, which is always true outside Find It.
            typeof(JawQuizSceneController).GetMethod("RefreshUi", PrivateInstance)?.Invoke(controller, null);

            Assert.True(adapterUnderTest.AcceptingSelections,
                "a background UI refresh must never silently disable input in What Is This mode");
            Assert.True(adapterUnderTest.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap));
            Assert.AreEqual("LeftRamus", modes.SelectedRegionId);
        }

        [Test]
        public void PeriodicBackgroundRefresh_DoesNotDisableTwoPlayerInput()
        {
            LoadQuizScene();
            modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
            var adapterUnderTest = controller.selectionAdapter;

            typeof(JawQuizSceneController).GetMethod("RefreshUi", PrivateInstance)?.Invoke(controller, null);

            Assert.True(adapterUnderTest.AcceptingSelections);
            Assert.True(adapterUnderTest.SimulateDetailedSelection("LeftRamus", JawQuizSelectionSource.ScreenTap));
            Assert.AreEqual("LeftRamus", modes.TargetRegionId);
        }

        [Test]
        public void FindIt_FingerRestingBeforeStart_DoesNotGradeUntilReleaseAndFreshDwell()
        {
            LoadQuizScene();
            modes.SelectMode(JawQuizLearningMode.FindIt);
            // Bypass the AR tracking-lock gate for this EditMode test: WorldPoseLocked only ever
            // becomes true from a real ARCore session, and the arming coroutine that waits for it
            // does not tick outside Play Mode. IsInputSystemReady() treats a missing tracker as
            // "not gated", which is exactly this Editor/preview fallback path.
            controller.jawTracker = null;
            var clock = new FakeClock(0f);
            var routerUnderTest = controller.selectionAdapter.fingertipRouter;
            Assert.NotNull(routerUnderTest, "the quiz scene must wire a real fingertip router to the adapter");
            routerUnderTest.mode = JawSurfaceFingertipRouter.FingertipSelectionMode.SurfaceRegionsOnly;
            routerUnderTest.dwellSeconds = 0.05f;
            routerUnderTest.TimeProvider = clock.Get;
            controller.selectionAdapter.TimeProvider = clock.Get;

            var region = controller.surfaceTarget.regionMap.Regions[0];
            routerUnderTest.ProcessSurfaceHit(true, region, 1);
            clock.Now += 0.1f;
            routerUnderTest.ProcessSurfaceHit(true, region, 1);
            Assert.NotNull(routerUnderTest.LastSelectedRegion, "dwell completes before Start Quiz is pressed");

            // Manually present the first question the way StartQuiz() would, without its
            // network-touching coroutines (SynchronizePendingAttempts / CheckProxyStatus), which
            // this offline Editor test cannot safely execute against a live proxy.
            var engine = new JawQuizEngine(controller.questionBank.Questions, 3);
            typeof(JawQuizSceneController).GetField("engine", PrivateInstance)?.SetValue(controller, engine);
            Assert.True(engine.StartQuiz());
            engine.ConfirmQuestionPresented();
            typeof(JawQuizSceneController).GetMethod("ArmFindItInputWhenReady", PrivateInstance)
                ?.Invoke(controller, null);
            PumpUpdate(controller.selectionAdapter);

            Assert.AreEqual(JawQuizState.AwaitingSelection, engine.State,
                "a selection completed before Start Quiz must not be graded against the first question");
            Assert.AreEqual(0, engine.AttemptNumber);

            // Finger still resting (never left) -- a second poll must still not grade anything.
            PumpUpdate(controller.selectionAdapter);
            Assert.AreEqual(JawQuizState.AwaitingSelection, engine.State);
            Assert.AreEqual(0, engine.AttemptNumber);
        }

        [Test]
        public void DetailedSelectionReceived_HasExactlyOneSubscriberPerController()
        {
            LoadQuizScene();
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);
            modes.SelectMode(JawQuizLearningMode.TwoPlayerChallenge);
            modes.SelectMode(JawQuizLearningMode.FindIt);
            modes.SelectMode(JawQuizLearningMode.WhatIsThis);

            var field = typeof(JawQuizSurfaceSelectionAdapter).GetField("DetailedSelectionReceived", PrivateInstance);
            var del = field?.GetValue(controller.selectionAdapter) as System.MulticastDelegate;
            Assert.NotNull(del, "expected the event's backing delegate to exist");
            Assert.AreEqual(2, del.GetInvocationList().Length,
                "repeatedly switching modes must not accumulate duplicate subscriptions " +
                "(exactly the scene controller and the learning-modes controller, each subscribed once)");
        }
    }
}
