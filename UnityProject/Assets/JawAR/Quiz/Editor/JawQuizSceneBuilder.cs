using System;
using System.Collections.Generic;
using System.IO;
using BMC.JawAR.SurfaceRegions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    public static class JawQuizSceneBuilder
    {
        public const string SourceScenePath = "Assets/Scenes/JawArUcoAnatomy_SurfacePaint_AR.unity";
        public const string QuizScenePath = "Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity";
        public const string DraftMapPath = "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap_CodexDraft.asset";
        public const string QuestionBankPath = "Assets/JawAR/Quiz/Data/JawQuizStarterBank.asset";

        [MenuItem("Tools/Jaw Anatomy Quiz/Create or Refresh Experimental Quiz Scene")]
        public static void Build()
        {
            if (!File.Exists(SourceScenePath))
                throw new FileNotFoundException("The accepted surface-paint source scene is missing.", SourceScenePath);

            var draftMap = AssetDatabase.LoadAssetAtPath<JawSurfaceRegionMap>(DraftMapPath);
            if (draftMap == null || draftMap.Regions.Count != 23)
                throw new InvalidOperationException("The editable 23-region draft map is missing or unexpected.");

            if (!File.Exists(QuizScenePath) && !AssetDatabase.CopyAsset(SourceScenePath, QuizScenePath))
                throw new IOException("Unity could not duplicate the experimental surface-paint scene.");

            var bank = CreateOrUpdateQuestionBank(draftMap);
            var scene = EditorSceneManager.OpenScene(QuizScenePath, OpenSceneMode.Single);
            if (scene.path == SourceScenePath)
                throw new InvalidOperationException("Safety stop: refusing to configure the source scene.");

            var surfaceTarget = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionTarget>(FindObjectsInactive.Include);
            var surfaceFeedback = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionFeedback>(FindObjectsInactive.Include);
            var fingertipRouter = UnityEngine.Object.FindFirstObjectByType<JawSurfaceFingertipRouter>(FindObjectsInactive.Include);
            var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
            var tracker = UnityEngine.Object.FindFirstObjectByType<JawOpenCvArucoTracker>(FindObjectsInactive.Include);
            if (surfaceTarget == null || surfaceFeedback == null || fingertipRouter == null || camera == null || tracker == null)
                throw new InvalidOperationException("The duplicated scene is missing required surface-region components.");

            if (surfaceTarget.regionMap != draftMap)
                throw new InvalidOperationException("Safety stop: duplicated scene is not referencing the editable draft map.");

            surfaceTarget.surfaceLookupEnabled = true;
            var legacyCoordinator = UnityEngine.Object.FindFirstObjectByType<JawSurfaceRegionSelectionCoordinator>(FindObjectsInactive.Include);
            if (legacyCoordinator != null) legacyCoordinator.enabled = false;
            var legacyTap = UnityEngine.Object.FindFirstObjectByType<JawAnatomyTapController>(FindObjectsInactive.Include);
            if (legacyTap != null) legacyTap.enabled = false;
            var legacyVoice = UnityEngine.Object.FindFirstObjectByType<JawVoiceQuestionController>(FindObjectsInactive.Include);
            if (legacyVoice != null) legacyVoice.enabled = false;

            fingertipRouter.mode = JawSurfaceFingertipRouter.FingertipSelectionMode.SurfaceRegionsOnly;
            fingertipRouter.surfaceTarget = surfaceTarget;
            fingertipRouter.surfaceFeedback = surfaceFeedback;

            var oldUi = GameObject.Find("Jaw AR UI");
            if (oldUi != null) oldUi.SetActive(false);

            var workflow = GameObject.Find("JawQuiz_EXPERIMENTAL");
            if (workflow == null) workflow = new GameObject("JawQuiz_EXPERIMENTAL");
            var adapter = workflow.GetComponent<JawQuizSurfaceSelectionAdapter>() ??
                          workflow.AddComponent<JawQuizSurfaceSelectionAdapter>();
            adapter.targetCamera = camera;
            adapter.surfaceTarget = surfaceTarget;
            adapter.surfaceFeedback = surfaceFeedback;
            adapter.fingertipRouter = fingertipRouter;
            adapter.acceptScreenInput = true;
            adapter.acceptFingertipInput = true;

            var presenter = workflow.GetComponent<JawQuizPaintedRegionPresenter>() ??
                            workflow.AddComponent<JawQuizPaintedRegionPresenter>();
            presenter.target = surfaceTarget;
            presenter.opacity = 0.58f;
            presenter.visibleByDefault = true;

            // Match the stability profile proven by the successful physical-phone surface build.
            // This keeps the established OpenCV pose/calibration path and prevents an early,
            // noisy ARCore pose from being permanently accepted by the world-lock average.
            tracker.detectionLongEdge = 1280;
            tracker.detectionsPerSecond = 6f;
            tracker.trackingSettleSeconds = 2f;
            tracker.stableDetectionsRequired = 24;
            tracker.lockSampleWindowSize = 30;
            // Phone evidence: all five visibly offset screenshots came from 1.50-1.54 mm
            // lock windows, while this same quiz achieved 0.55 mm and the captured good-app
            // lock was 0.80 mm. Require the clean quality both apps have demonstrated.
            tracker.maxPositionSpreadMeters = 0.001f;
            tracker.maxRotationSpreadDegrees = 1f;
            tracker.stableWindowsRequired = 4;
            tracker.maxSampleDeviationMeters = 0.015f;
            tracker.maxSampleAngularDeviationDegrees = 7f;

            var controller = workflow.GetComponent<JawQuizSceneController>() ??
                             workflow.AddComponent<JawQuizSceneController>();
            controller.questionBank = bank;
            controller.selectionAdapter = adapter;
            controller.paintedRegions = presenter;
            controller.surfaceTarget = surfaceTarget;
            controller.jawTracker = tracker;
            controller.maxAttemptsPerQuestion = 3;
            // Developer Diagnostics remains available; this only disables the continuous 2 Hz
            // pose log after the movement diagnosis proved the locked anchor stays static.
            controller.diagnosticMode = false;

            EditorUtility.SetDirty(surfaceTarget);
            EditorUtility.SetDirty(fingertipRouter);
            EditorUtility.SetDirty(adapter);
            EditorUtility.SetDirty(presenter);
            EditorUtility.SetDirty(tracker);
            EditorUtility.SetDirty(controller);
            if (legacyCoordinator != null) EditorUtility.SetDirty(legacyCoordinator);
            if (legacyTap != null) EditorUtility.SetDirty(legacyTap);
            if (legacyVoice != null) EditorUtility.SetDirty(legacyVoice);
            if (oldUi != null) EditorUtility.SetDirty(oldUi);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, QuizScenePath))
                throw new IOException("Unity failed to save the new quiz scene.");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"JAW_QUIZ_SCENE_READY scene={QuizScenePath} questions={bank.Questions.Count} " +
                      $"map={DraftMapPath} surfaceLookupEnabled={surfaceTarget.surfaceLookupEnabled} " +
                      "paintedVisibleByDefault=true legacyCoordinatorEnabled=false");
        }

        private static JawQuizQuestionBank CreateOrUpdateQuestionBank(JawSurfaceRegionMap map)
        {
            var bank = AssetDatabase.LoadAssetAtPath<JawQuizQuestionBank>(QuestionBankPath);
            if (bank == null)
            {
                bank = ScriptableObject.CreateInstance<JawQuizQuestionBank>();
                AssetDatabase.CreateAsset(bank, QuestionBankPath);
            }

            var questions = new List<JawQuizQuestionDefinition>();
            foreach (var region in map.Regions)
            {
                var display = region.DisplayName;
                var explanation = Explanation(region.StableId, display);
                questions.Add(new JawQuizQuestionDefinition(
                    "jaw.region." + ToKebab(region.StableId) + ".v1",
                    region.StableId,
                    "Identify the " + display + ".",
                    "Identify the " + display + ".",
                    "Correct — you identified the " + display + ".",
                    "Not quite. Compare the selected colour with the landmark location.",
                    FirstHint(region.StableId),
                    explanation,
                    explanation,
                    Difficulty(region.StableId)));
            }
            bank.SetEditorData("jaw-surface-starter-v1", questions);
            EditorUtility.SetDirty(bank);
            return bank;
        }

        private static string ToKebab(string value)
        {
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                if (i > 0 && char.IsUpper(value[i])) result.Append('-');
                result.Append(char.ToLowerInvariant(value[i]));
            }
            return result.ToString();
        }

        private static JawQuizDifficulty Difficulty(string id)
        {
            return id.Contains("Origin", StringComparison.Ordinal) || id.Contains("Insertion", StringComparison.Ordinal)
                ? JawQuizDifficulty.Advanced
                : id.Contains("Foramen", StringComparison.Ordinal) || id.Contains("Process", StringComparison.Ordinal)
                    ? JawQuizDifficulty.Intermediate
                    : JawQuizDifficulty.Beginner;
        }

        private static string FirstHint(string id)
        {
            if (id.StartsWith("Left", StringComparison.Ordinal)) return "Look on the anatomical left side of the mandible.";
            if (id.StartsWith("Right", StringComparison.Ordinal)) return "Look on the anatomical right side of the mandible.";
            if (id.Contains("Mental", StringComparison.Ordinal) || id == "MentalisOrigin") return "Look near the anterior chin region.";
            if (id == "LowerIncisors" || id == "AlveolarProcess") return "Look along the tooth-bearing upper border.";
            return "Use the coloured surface boundaries and the anatomical legend.";
        }

        private static string Explanation(string id, string display)
        {
            return id switch
            {
                "LowerIncisors" => "The lower incisors occupy the anterior midline of the mandibular dental arch.",
                "LeftRamus" or "RightRamus" => "The ramus is the broad ascending posterior part of the mandible.",
                "LeftCondylarProcess" or "RightCondylarProcess" => "The condylar process is the posterior superior projection that articulates at the temporomandibular joint.",
                "LeftCoronoidProcess" or "RightCoronoidProcess" => "The coronoid process is the anterior superior projection of the ramus.",
                "LeftMentalForamen" or "RightMentalForamen" => "The mental foramen is an opening on the anterolateral mandibular body, commonly near the premolars.",
                "MentalProtuberance" => "The mental protuberance forms the central prominence of the chin.",
                "AlveolarProcess" => "The alveolar process is the tooth-bearing superior portion of the mandibular body.",
                "LeftMasseterInsertion" or "RightMasseterInsertion" => "The masseter inserts on the lateral ramus and mandibular angle.",
                "LeftTemporalisInsertion" or "RightTemporalisInsertion" => "The temporalis inserts mainly on the coronoid process and anterior ramus.",
                "LeftBuccinatorOrigin" or "RightBuccinatorOrigin" => "The buccinator has mandibular attachment near the posterior alveolar region.",
                "LeftDepressorAnguliOrisOrigin" or "RightDepressorAnguliOrisOrigin" => "Depressor anguli oris originates from the oblique line of the mandibular body.",
                "LeftDepressorLabiiInferiorisOrigin" or "RightDepressorLabiiInferiorisOrigin" => "Depressor labii inferioris originates from the anterior mandibular body between the midline and oblique line.",
                "MentalisOrigin" => "The mentalis originates from the incisive region of the anterior mandible.",
                "OrbicularisOrisReference" => "This painted area is a reference for the perioral orbicularis oris relationship.",
                _ => "This painted surface identifies the saved " + display + " region."
            };
        }

        public static void BuildAndExit()
        {
            try { Build(); }
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
