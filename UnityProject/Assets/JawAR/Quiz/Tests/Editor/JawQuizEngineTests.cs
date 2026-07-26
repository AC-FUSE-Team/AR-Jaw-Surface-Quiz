using System.Collections.Generic;
using BMC.JawAR.SurfaceRegions;
using NUnit.Framework;
using UnityEngine;

namespace BMC.JawAR.Quiz.Tests
{
    public sealed class JawQuizEngineTests
    {
        private static JawQuizQuestionDefinition Question(string expected = "LowerIncisors")
        {
            return new JawQuizQuestionDefinition("q.lower.v1", expected, "Find it", "Find it",
                "Correct", "Incorrect", "Hint one", "Hint two", "Explanation",
                JawQuizDifficulty.Beginner);
        }

        [Test]
        public void StableRegionComparison_IsExactOrdinal()
        {
            var engine = Started(Question());
            Assert.AreEqual(JawQuizSelectionKind.Correct,
                engine.EvaluateSelection("LowerIncisors", 1f).Kind);

            engine = Started(Question());
            Assert.AreEqual(JawQuizSelectionKind.Incorrect,
                engine.EvaluateSelection("lowerincisors", 1f).Kind);
        }

        [Test]
        public void CorrectSelection_TransitionsThroughCompletion()
        {
            var engine = Started(Question());
            var result = engine.EvaluateSelection("LowerIncisors", 2.4f);
            Assert.AreEqual(JawQuizState.ShowingCorrectFeedback, engine.State);
            Assert.AreEqual(1, result.AttemptNumber);
            engine.CompleteCurrentQuestion();
            Assert.AreEqual(JawQuizState.QuestionComplete, engine.State);
        }

        [Test]
        public void IncorrectSelection_AllowsBoundedRetry()
        {
            var engine = Started(Question(), 2);
            Assert.AreEqual(JawQuizSelectionKind.Incorrect,
                engine.EvaluateSelection("LeftRamus", 1f).Kind);
            Assert.True(engine.CanRetry);
            Assert.True(engine.Retry());
            Assert.AreEqual(JawQuizState.AwaitingSelection, engine.State);
            engine.EvaluateSelection("RightRamus", 2f);
            Assert.False(engine.CanRetry);
            engine.CompleteCurrentQuestion();
            Assert.AreEqual(JawQuizState.QuestionComplete, engine.State);
        }

        [Test]
        public void UnlabelledSelection_DoesNotConsumeAttempt()
        {
            var engine = Started(Question());
            var result = engine.EvaluateSelection(string.Empty, 0.5f);
            Assert.AreEqual(JawQuizSelectionKind.Unlabelled, result.Kind);
            Assert.AreEqual(0, engine.AttemptNumber);
            Assert.AreEqual(JawQuizState.AwaitingSelection, engine.State);
        }

        [Test]
        public void HintLevels_AreBoundedAndReturnToAwaiting()
        {
            var engine = Started(Question());
            Assert.AreEqual("Hint one", engine.RequestHint());
            Assert.AreEqual(JawQuizState.ShowingHint, engine.State);
            engine.ResumeAfterHint();
            Assert.AreEqual("Hint two", engine.RequestHint());
            engine.ResumeAfterHint();
            Assert.AreEqual("Hint two", engine.RequestHint());
            Assert.AreEqual(2, engine.HintLevel);
        }

        [Test]
        public void QuestionTransitions_ReachSessionComplete()
        {
            var engine = Started(Question());
            engine.SkipCurrentQuestion();
            Assert.AreEqual(JawQuizState.QuestionComplete, engine.State);
            Assert.False(engine.NextQuestion());
            Assert.AreEqual(JawQuizState.SessionComplete, engine.State);
        }

        [Test]
        public void SimulatedAdapter_UsesActualMapStableId()
        {
            var map = ScriptableObject.CreateInstance<JawSurfaceRegionMap>();
            map.InitializeDefaultRegions();
            var targetGo = new GameObject("Target");
            var adapterGo = new GameObject("Adapter");
            try
            {
                var target = targetGo.AddComponent<JawSurfaceRegionTarget>();
                target.regionMap = map;
                var adapter = adapterGo.AddComponent<JawQuizSurfaceSelectionAdapter>();
                adapter.surfaceTarget = target;
                adapter.AcceptingSelections = true;
                string received = null;
                adapter.SelectionReceived += (id, _, _, _) => received = id;
                Assert.True(adapter.SimulateRegionSelection("LeftMentalForamen"));
                Assert.AreEqual("LeftMentalForamen", received);
                Assert.False(adapter.SimulateRegionSelection("BodyOfMandible"));
            }
            finally
            {
                Object.DestroyImmediate(adapterGo);
                Object.DestroyImmediate(targetGo);
                Object.DestroyImmediate(map);
            }
        }

        [Test]
        public void OverlayVisibility_DoesNotChangeLookupGateOrMap()
        {
            var map = ScriptableObject.CreateInstance<JawSurfaceRegionMap>();
            map.InitializeDefaultRegions();
            var targetGo = new GameObject("Target");
            var presenterGo = new GameObject("Presenter");
            try
            {
                var target = targetGo.AddComponent<JawSurfaceRegionTarget>();
                target.regionMap = map;
                target.surfaceLookupEnabled = true;
                var presenter = presenterGo.AddComponent<JawQuizPaintedRegionPresenter>();
                presenter.target = target;
                presenter.SetPaintedRegionsVisible(false);
                Assert.True(target.surfaceLookupEnabled);
                Assert.AreEqual(23, map.Regions.Count);
                presenter.SetPaintedRegionsVisible(true);
                Assert.True(target.surfaceLookupEnabled);
            }
            finally
            {
                Object.DestroyImmediate(presenterGo);
                Object.DestroyImmediate(targetGo);
                Object.DestroyImmediate(map);
            }
        }

        [Test]
        public void HidingPaintedRegions_HidesOnlyOverlays()
        {
            var fixture = CreateVisibilityFixture();
            try
            {
                fixture.presenter.SetPaintedRegionsVisible(false);

                Assert.True(fixture.baseRenderer.enabled);
                Assert.True(fixture.target.meshCollider.enabled);
                Assert.True(fixture.target.surfaceLookupEnabled);
                Assert.True(AllOverlayRenderers(fixture.target).TrueForAll(renderer => !renderer.enabled));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void HidingVirtualJaw_HidesBaseAndOverlaysButNotColliderOrObjects()
        {
            var fixture = CreateVisibilityFixture();
            try
            {
                fixture.presenter.SetVirtualJawVisible(false);

                Assert.False(fixture.baseRenderer.enabled);
                Assert.True(AllOverlayRenderers(fixture.target).TrueForAll(renderer => !renderer.enabled));
                Assert.True(fixture.target.meshCollider.enabled);
                Assert.True(fixture.target.gameObject.activeInHierarchy);
                Assert.True(fixture.presenter.gameObject.activeInHierarchy);
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void SurfaceLookup_StillWorksWhenAllGeometryIsVisuallyHidden()
        {
            var fixture = CreateVisibilityFixture();
            try
            {
                fixture.presenter.SetVirtualJawVisible(false);
                Physics.SyncTransforms();

                Assert.True(fixture.target.TryRaycast(
                    new Ray(new Vector3(0.2f, 0.2f, 1f), Vector3.back), 2f,
                    out _, out var region));
                Assert.AreEqual("LowerIncisors", region.StableId);
            }
            finally
            {
                fixture.Dispose();
            }
        }

        [Test]
        public void SimulatedGrading_StillWorksWhenAllGeometryIsVisuallyHidden()
        {
            var fixture = CreateVisibilityFixture();
            var adapterGo = new GameObject("Adapter");
            try
            {
                fixture.presenter.SetVirtualJawVisible(false);
                var engine = Started(Question());
                var adapter = adapterGo.AddComponent<JawQuizSurfaceSelectionAdapter>();
                adapter.surfaceTarget = fixture.target;
                adapter.AcceptingSelections = true;
                JawQuizEvaluation evaluation = default;
                adapter.SelectionReceived += (id, _, _, _) => evaluation = engine.EvaluateSelection(id, 0.5f);

                Assert.True(adapter.SimulateRegionSelection("LowerIncisors"));
                Assert.AreEqual(JawQuizSelectionKind.Correct, evaluation.Kind);
                Assert.AreEqual(JawQuizState.ShowingCorrectFeedback, engine.State);
            }
            finally
            {
                Object.DestroyImmediate(adapterGo);
                fixture.Dispose();
            }
        }

        [Test]
        public void ShowingVirtualJaw_RestoresPriorPaintedVisibilityPreference()
        {
            var fixture = CreateVisibilityFixture();
            try
            {
                fixture.presenter.SetPaintedRegionsVisible(false);
                fixture.presenter.SetVirtualJawVisible(false);
                fixture.presenter.SetVirtualJawVisible(true);
                Assert.True(fixture.baseRenderer.enabled);
                Assert.True(AllOverlayRenderers(fixture.target).TrueForAll(renderer => !renderer.enabled));

                fixture.presenter.SetPaintedRegionsVisible(true);
                fixture.presenter.SetVirtualJawVisible(false);
                fixture.presenter.SetVirtualJawVisible(true);
                Assert.True(AllOverlayRenderers(fixture.target).TrueForAll(renderer => renderer.enabled));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        private static VisibilityFixture CreateVisibilityFixture()
        {
            var mesh = new Mesh { name = "VisibilityTestTriangle" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();

            var map = ScriptableObject.CreateInstance<JawSurfaceRegionMap>();
            map.InitializeDefaultRegions();
            map.SetSourceMeshMetadata(mesh, string.Empty, string.Empty, new[] { 3 });
            Assert.True(map.AssignTriangle("LowerIncisors", 0, false, out _));
            map.SetBakedOverlayMesh("LowerIncisors", mesh);

            var targetGo = new GameObject("Visibility Target");
            var filter = targetGo.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var baseRenderer = targetGo.AddComponent<MeshRenderer>();
            var collider = targetGo.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            var target = targetGo.AddComponent<JawSurfaceRegionTarget>();
            target.meshFilter = filter;
            target.meshCollider = collider;
            target.regionMap = map;
            target.surfaceLookupEnabled = true;
            target.RebuildLookup();

            var presenterGo = new GameObject("Visibility Presenter");
            var presenter = presenterGo.AddComponent<JawQuizPaintedRegionPresenter>();
            presenter.target = target;
            presenter.BuildIfNeeded();
            presenter.SetPaintedRegionsVisible(true);
            presenter.SetVirtualJawVisible(true);
            return new VisibilityFixture(targetGo, presenterGo, mesh, map, target, presenter, baseRenderer);
        }

        private static List<MeshRenderer> AllOverlayRenderers(JawSurfaceRegionTarget target)
        {
            var result = new List<MeshRenderer>();
            foreach (var renderer in target.meshCollider.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.gameObject.name.StartsWith("QuizPaint_")) result.Add(renderer);
            Assert.IsNotEmpty(result);
            return result;
        }

        private sealed class VisibilityFixture
        {
            private readonly GameObject targetGo;
            private readonly GameObject presenterGo;
            private readonly Mesh mesh;
            private readonly JawSurfaceRegionMap map;
            public readonly JawSurfaceRegionTarget target;
            public readonly JawQuizPaintedRegionPresenter presenter;
            public readonly MeshRenderer baseRenderer;

            public VisibilityFixture(GameObject targetGo, GameObject presenterGo, Mesh mesh,
                JawSurfaceRegionMap map, JawSurfaceRegionTarget target,
                JawQuizPaintedRegionPresenter presenter, MeshRenderer baseRenderer)
            {
                this.targetGo = targetGo;
                this.presenterGo = presenterGo;
                this.mesh = mesh;
                this.map = map;
                this.target = target;
                this.presenter = presenter;
                this.baseRenderer = baseRenderer;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(presenterGo);
                Object.DestroyImmediate(targetGo);
                Object.DestroyImmediate(map);
                Object.DestroyImmediate(mesh);
            }
        }

        private static JawQuizEngine Started(JawQuizQuestionDefinition question, int attempts = 3)
        {
            var engine = new JawQuizEngine(new List<JawQuizQuestionDefinition> { question }, attempts);
            Assert.True(engine.StartQuiz());
            Assert.AreEqual(JawQuizState.QuestionPresented, engine.State);
            engine.ConfirmQuestionPresented();
            Assert.AreEqual(JawQuizState.AwaitingSelection, engine.State);
            return engine;
        }
    }
}
