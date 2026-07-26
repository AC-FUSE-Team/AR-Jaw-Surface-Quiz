using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BMC.JawAR.Quiz.Learning;
using BMC.JawAR.SurfaceRegions;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz
{
    [DisallowMultipleComponent]
    public sealed class JawQuizSceneController : MonoBehaviour
    {
        public JawQuizQuestionBank questionBank;
        public JawQuizSurfaceSelectionAdapter selectionAdapter;
        public JawQuizPaintedRegionPresenter paintedRegions;
        public JawSurfaceRegionTarget surfaceTarget;
        public JawOpenCvArucoTracker jawTracker;
        [Range(1, 5)] public int maxAttemptsPerQuestion = 3;
        public bool diagnosticMode = true;
        [Header("Optional local learning proxy (never a Backboard URL or key)")]
        public string learningProxyUrl = "http://192.168.2.244:8765";
        public string learningProxyPrototypeToken = "";
        public Text VisibleTrackingStatusText => trackingStatusText;
        public bool SpeechMuted => speech?.Muted == true;
        public bool FindItSessionRunning => engine?.CurrentQuestion != null &&
                                            engine.State != JawQuizState.SessionComplete;

        private JawQuizEngine engine;
        private Canvas canvas;
        private CanvasScaler canvasScaler;
        private RectTransform topBar;
        private RectTransform quizCard;
        private RectTransform trackingStatusPanel;
        private RectTransform controlsPanel;
        private GameObject diagnosticsPanel;
        private Text titleText;
        private Text studentText;
        private Text questionText;
        private Text attemptText;
        private Text hintText;
        private Text feedbackText;
        private Text selectedText;
        private Text personalizedText;
        private Text stateText;
        private Text trackingStatusText;
        private Text diagnosticLearningText;
        private Text proxyCurrentUrlText;
        private Text proxyTestResultText;
        private InputField proxyUrlInput;
        private Text overlayButtonText;
        private Text virtualJawButtonText;
        private Text diagnosticsButtonText;
        private Text startButtonText;
        private Text profileButtonText;
        private Text muteButtonText;
        private Button startButton;
        private Button repeatButton;
        private Button hintButton;
        private Button skipButton;
        private Button nextButton;
        private Button overlayButton;
        private Button diagnosticsButton;
        private Button profileButton;
        private Button muteButton;
        private JawQuizAttemptStore attemptStore;
        private JawQuizProxyClient proxyClient;
        private IQuizSpeechService speech;
        private string studentId = "student_001";
        private string sessionId;
        private JawQuizAttemptRecord latestAttempt;
        private float questionStartedAt;
        private Coroutine retryRoutine;
        private readonly HashSet<string> synchronizingEventIds = new(StringComparer.Ordinal);
        private bool statusCheckInFlight;
        private bool proxyConnected;
        private bool backboardAvailable;
        private bool syncing;
        private string proxyMode = string.Empty;
        private string lastSyncResult = "Local storage ready";
        private string proxyBuildDefaultUrl;
        private float nextProxyStatusCheck;
        private int lastLayoutWidth;
        private int lastLayoutHeight;
        private bool previewResolutionOverride;
        private float nextDiagnosticLogTime;
        private bool diagnosticBaselineCaptured;
        private Vector3 diagnosticJawBaselinePos;
        private Vector3 diagnosticCameraBaselinePos;
        private Coroutine readinessRoutine;

        // Low alpha (unlike the working app's plain-text UI, this app uses background panels) so
        // the live camera feed stays visible through the UI -- the student needs to see and frame
        // the physical marker while aiming/calibrating, not just the ~28% sliver of screen height
        // that was left between opaque panels before.
        private static readonly Color Navy = new(0.035f, 0.075f, 0.13f, 0.55f);
        private static readonly Color Card = new(0.075f, 0.12f, 0.19f, 0.5f);
        private static readonly Color Cyan = new(0.16f, 0.82f, 0.92f, 1f);
        private static readonly Color Green = new(0.25f, 0.92f, 0.52f, 1f);
        private static readonly Color Amber = new(1f, 0.72f, 0.18f, 1f);
        private static readonly Color Red = new(1f, 0.4f, 0.38f, 1f);

        private void Awake()
        {
            EnsureLearningServices();
            EnsureInterface();
            if (paintedRegions != null)
                paintedRegions.SetPaintedRegionsVisible(paintedRegions.visibleByDefault);
            if (selectionAdapter != null) selectionAdapter.DetailedSelectionReceived += OnDetailedSelection;
            RefreshUi();
        }

        private void EnsureLearningServices()
        {
            if (attemptStore != null) return;
            sessionId = "session_" + Guid.NewGuid().ToString("N");
            attemptStore = new JawQuizAttemptStore(Application.persistentDataPath);
            proxyBuildDefaultUrl = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUIZ_PROXY_URL"))
                ? learningProxyUrl : Environment.GetEnvironmentVariable("QUIZ_PROXY_URL");
            proxyClient = new JawQuizProxyClient
            {
                BaseUrl = JawQuizProxyConfiguration.Load(proxyBuildDefaultUrl),
                PrototypeToken = string.IsNullOrWhiteSpace(learningProxyPrototypeToken)
                    ? (Environment.GetEnvironmentVariable("QUIZ_PROXY_TOKEN") ?? string.Empty)
                    : learningProxyPrototypeToken
            };
            speech = QuizSpeechFactory.Create();
        }

        private void OnDestroy()
        {
            if (selectionAdapter != null) selectionAdapter.DetailedSelectionReceived -= OnDetailedSelection;
            speech?.Stop();
            speech?.Dispose();
        }

        private void Update()
        {
            if (selectionAdapter != null) selectionAdapter.TrackingReady = IsInputSystemReady();
            if (diagnosticMode) LogDiagnosticsIfDue();
            if (proxyClient != null && !statusCheckInFlight && Time.unscaledTime >= nextProxyStatusCheck)
            {
                nextProxyStatusCheck = Time.unscaledTime + 8f;
                StartCoroutine(CheckProxyStatus());
            }
            if (previewResolutionOverride || canvas == null) return;
            if (lastLayoutWidth != Screen.width || lastLayoutHeight != Screen.height)
                ApplyResponsiveLayout(Screen.width, Screen.height);
        }

        /// <summary>
        /// True only once the jaw is locked/ready and the selection lookup can actually resolve a
        /// triangle to a region. Anatomical input must never be accepted or graded before this is
        /// true (a missing tracker component is treated as "not gating", e.g. Editor preview scenes).
        /// </summary>
        private bool IsInputSystemReady()
        {
            return surfaceTarget != null && surfaceTarget.meshCollider != null &&
                   surfaceTarget.regionMap != null && (jawTracker == null || jawTracker.WorldPoseLocked);
        }

        // Bounded (~2 Hz) diagnostic trace: is the locked jaw anchor's own transform actually
        // static after lock (as the tracker's code implies), or is something moving it? Also
        // records the live AR camera pose so camera drift can be distinguished from anchor drift.
        private void LogDiagnosticsIfDue()
        {
            if (jawTracker == null || !jawTracker.WorldPoseLocked ||
                jawTracker.jawAnchorRoot == null || jawTracker.arCamera == null) return;
            if (Time.unscaledTime < nextDiagnosticLogTime) return;
            nextDiagnosticLogTime = Time.unscaledTime + 0.5f;

            var jaw = jawTracker.jawAnchorRoot;
            var cam = jawTracker.arCamera.transform;
            if (!diagnosticBaselineCaptured)
            {
                diagnosticJawBaselinePos = jaw.position;
                diagnosticCameraBaselinePos = cam.position;
                diagnosticBaselineCaptured = true;
            }

            var jawDriftMeters = Vector3.Distance(jaw.position, diagnosticJawBaselinePos);
            var camMovedMeters = Vector3.Distance(cam.position, diagnosticCameraBaselinePos);
            var jawEuler = jaw.rotation.eulerAngles;
            var camEuler = cam.rotation.eulerAngles;
            Debug.Log($"JAW_QUIZ_DIAG t={Time.unscaledTime:F2} " +
                      $"jawPos=({jaw.position.x:F4},{jaw.position.y:F4},{jaw.position.z:F4}) " +
                      $"jawRotEuler=({jawEuler.x:F1},{jawEuler.y:F1},{jawEuler.z:F1}) " +
                      $"jawDriftSinceLockMeters={jawDriftMeters:F5} " +
                      $"camPos=({cam.position.x:F4},{cam.position.y:F4},{cam.position.z:F4}) " +
                      $"camRotEuler=({camEuler.x:F1},{camEuler.y:F1},{camEuler.z:F1}) " +
                      $"camMovedSinceBaselineMeters={camMovedMeters:F4} " +
                      $"screen={Screen.width}x{Screen.height} orientation={Screen.orientation}");
        }

        public void EnsureInterface()
        {
            if (canvas == null)
                BuildInterface();
            if (jawTracker != null)
                jawTracker.statusText = trackingStatusText;
        }

        public void StartQuiz()
        {
            EnsureLearningServices();
            sessionId = "session_" + Guid.NewGuid().ToString("N");
            StartCoroutine(SynchronizePendingAttempts());
            StartCoroutine(CheckProxyStatus());
            EnsureInterface();
            if (selectionAdapter != null)
            {
                // Screen taps are still *detected* in Find It (so a jaw tap can show the
                // "use your finger" hint below) but JawQuizSceneController.OnDetailedSelection
                // never grades a ScreenTap-sourced selection while engine.State is AwaitingSelection.
                selectionAdapter.acceptScreenInput = true;
                selectionAdapter.acceptFingertipInput = true;
                selectionAdapter.BlockingOverlayOpen = false;
            }
            if (questionBank == null)
            {
                SetFeedback("Question bank is missing.", Red);
                return;
            }
            var scheduler = new JawQuizDeterministicScheduler();
            var studentHistory = attemptStore.Attempts.Where(item => item.studentId == studentId);
            engine = new JawQuizEngine(scheduler.Order(questionBank.Questions, studentHistory),
                maxAttemptsPerQuestion);
            if (!engine.StartQuiz())
            {
                SetFeedback("No enabled quiz questions are available.", Red);
                return;
            }
            PresentCurrentQuestion();
        }

        /// <summary>
        /// Clears only quiz-session/transient presentation state when another local mode is chosen.
        /// Tracking, calibration, anonymous profile, mute, overlay preference, and local history are
        /// deliberately left intact.
        /// </summary>
        public void ResetTemporaryStateForModeChange()
        {
            EnsureLearningServices();
            if (retryRoutine != null)
            {
                StopCoroutine(retryRoutine);
                retryRoutine = null;
            }
            engine = null;
            latestAttempt = null;
            questionStartedAt = 0f;
            speech?.Stop();
            if (readinessRoutine != null)
            {
                StopCoroutine(readinessRoutine);
                readinessRoutine = null;
            }
            selectionAdapter?.Disarm();
            JawQuizDiagnostics.CurrentQuestionId = "none";
            if (questionText != null) questionText.text = "Press Start Quiz when you are ready.";
            if (attemptText != null) attemptText.text = "Not started";
            if (hintText != null) hintText.text = "Questions are graded locally from the saved painted triangles.";
            if (feedbackText != null) feedbackText.text = string.Empty;
            if (selectedText != null) selectedText.text = string.Empty;
            RefreshUi();
        }

        public void StopModeSpeech() => speech?.Stop();

        public void SpeakModeText(string text)
        {
            EnsureLearningServices();
            speech?.Speak(text);
        }

        public void RepeatQuestion()
        {
            if (engine?.CurrentQuestion == null) return;
            questionText.text = engine.CurrentQuestion.DisplayPrompt;
            SetFeedback("Question repeated on screen.", Cyan);
            speech?.Speak(SpokenQuestion(engine.CurrentQuestion));
        }

        public void RequestHint()
        {
            if (engine?.CurrentQuestion == null) return;
            var hint = engine.RequestHint();
            hintText.text = string.IsNullOrWhiteSpace(hint) ? "No additional hint is available." : hint;
            speech?.Speak(hintText.text);
            if (latestAttempt != null && latestAttempt.questionId == engine.CurrentQuestion.QuestionId)
                StartCoroutine(proxyClient.RequestHint(latestAttempt, engine.HintLevel,
                    OnPersonalizedHint));
            engine.ResumeAfterHint();
            RefreshUi();
        }

        public void SkipQuestion()
        {
            if (engine?.CurrentQuestion == null) return;
            engine.SkipCurrentQuestion();
            SetFeedback("Question skipped. Select Next to continue.", Amber);
            RefreshUi();
        }

        public void NextQuestion()
        {
            if (engine == null) return;
            if (engine.NextQuestion()) PresentCurrentQuestion();
            else if (engine.State == JawQuizState.SessionComplete)
            {
                questionText.text = "Quiz complete";
                hintText.text = "You reached the end of the local diagnostic question bank.";
                SetFeedback("Session complete.", Green);
                speech?.Speak("Quiz complete. Your attempts are saved locally.");
                RefreshUi();
            }
        }

        public void TogglePaintedRegions()
        {
            if (paintedRegions == null) return;
            paintedRegions.TogglePaintedRegions();
            UpdateOverlayUi();
        }

        public void ToggleVirtualJaw()
        {
            if (paintedRegions == null) return;
            paintedRegions.ToggleVirtualJaw();
            UpdateOverlayUi();
        }

        public void SetDiagnosticsVisible(bool visible)
        {
            EnsureInterface();
            diagnosticsPanel.SetActive(visible);
            if (visible) diagnosticsPanel.transform.SetAsLastSibling();
            diagnosticsButtonText.text = "Developer Diagnostics";
        }

        public void ToggleDiagnostics() => SetDiagnosticsVisible(!diagnosticsPanel.activeSelf);

        public void HighlightRegion(string stableId)
        {
            if (paintedRegions == null || surfaceTarget?.regionMap == null) return;
            var region = surfaceTarget.regionMap.GetRegion(stableId);
            if (region == null || !paintedRegions.HighlightOnly(stableId)) return;
            selectedText.text = "Legend focus: " + region.DisplayName;
            UpdateOverlayUi();
        }

        public void ShowAllRegionColours()
        {
            paintedRegions?.ShowAllRegions();
            selectedText.text = "Legend: all painted regions";
            UpdateOverlayUi();
        }

        public void SimulateSelection(string stableId) => selectionAdapter?.SimulateRegionSelection(stableId);
        public void SimulateUnlabelledSelection() => selectionAdapter?.SimulateUnlabelledSelection();

        /// <summary>Used only by the Editor screenshot utility; it does not save scene changes.</summary>
        public void PrepareScreenshotPreview(bool showDiagnostics)
        {
            EnsureInterface();
            paintedRegions?.SetPaintedRegionsVisible(true);
            StartQuiz();
            SetFeedback("Tap the painted jaw surface, or open Developer Diagnostics to simulate a selection.", Cyan);
            SetDiagnosticsVisible(showDiagnostics);
        }

        /// <summary>Editor-only visual demonstration; it never contacts the proxy.</summary>
        public void PrepareLearningScreenshotPreview()
        {
            PrepareScreenshotPreview(false);
            personalizedText.text = "Personalized: Mock mode suggests reviewing nearby landmarks before retrying.";
            stateText.text = "PROXY CONNECTED • MOCK • 2 QUEUED";
        }

        public void PrepareLearningScreenshotPreview(string state)
        {
            PrepareLearningScreenshotPreview();
            if (state == "offline")
            {
                personalizedText.text = "Offline • Local anatomy feedback remains available.";
                stateText.text = "OFFLINE • QUEUED • 2";
            }
            else if (state == "synchronized")
            {
                personalizedText.text = "Personalized: Mock explanation synchronized successfully.";
                stateText.text = "PROXY CONNECTED • BACKBOARD AVAILABLE";
            }
        }

        /// <summary>Editor capture hook. Runtime layout continues to follow Screen dimensions.</summary>
        public void SetPreviewResolution(int width, int height)
        {
            EnsureInterface();
            previewResolutionOverride = true;
            ApplyResponsiveLayout(width, height);
        }

        public void PrepareScreenshotBeforeStart()
        {
            EnsureInterface();
            paintedRegions?.SetPaintedRegionsVisible(true);
            SetDiagnosticsVisible(false);
            RefreshUi();
        }

        public void PrepareScreenshotFeedback()
        {
            PrepareScreenshotPreview(false);
            OnDetailedSelection(new JawQuizSurfaceSelection(JawQuizSurfaceHitKind.LabelledRegion,
                JawQuizSelectionSource.Simulation, "LowerIncisors", "Lower Incisors", -1, Guid.NewGuid(),
                Time.unscaledTime));
        }

        private void PresentCurrentQuestion()
        {
            var question = engine.CurrentQuestion;
            questionText.text = question.DisplayPrompt;
            hintText.text = "Hint is optional.";
            selectedText.text = "Legend: all painted regions";
            feedbackText.text = "Point to the requested region on the physical jaw.";
            feedbackText.color = Color.white;
            personalizedText.text = "Personalized explanation: local feedback ready";
            engine.ConfirmQuestionPresented();
            questionStartedAt = Time.unscaledTime;
            latestAttempt = null;
            speech?.Stop();
            speech?.Speak(SpokenQuestion(question));
            JawQuizDiagnostics.CurrentQuestionId = question.QuestionId;
            ArmFindItInputWhenReady();
            RefreshUi();
        }

        /// <summary>
        /// Single arming entry point for Find It. Refuses to arm until tracking/collider/map are
        /// ready (polling cheaply each frame rather than a fixed delay), and otherwise arms with the
        /// adapter's built-in debounce + stale-selection + release/re-entry gate so a question never
        /// inherits a physical selection left over from before it was presented.
        /// </summary>
        private void ArmFindItInputWhenReady()
        {
            if (selectionAdapter == null) return;
            if (readinessRoutine != null)
            {
                StopCoroutine(readinessRoutine);
                readinessRoutine = null;
            }
            if (!IsInputSystemReady())
            {
                SetFeedback("Finish locking the jaw before selecting a region.", Amber);
                readinessRoutine = StartCoroutine(ArmWhenReadyRoutine());
                return;
            }
            selectionAdapter.Arm();
        }

        private IEnumerator ArmWhenReadyRoutine()
        {
            while (!IsInputSystemReady()) yield return null;
            readinessRoutine = null;
            if (engine == null || engine.State != JawQuizState.AwaitingSelection) yield break;
            selectionAdapter?.Arm();
            SetFeedback("Point to the requested region on the physical jaw.", Color.white);
            RefreshUi();
        }

        private void OnDetailedSelection(JawQuizSurfaceSelection selection)
        {
            if (engine == null || engine.State != JawQuizState.AwaitingSelection) return;
            if (selection.Source == JawQuizSelectionSource.ScreenTap)
            {
                // Find It is physical-pointing-only; a jaw screen tap is neither graded nor wrong.
                SetFeedback("Use your finger to point at the physical jaw in this mode.", Amber);
                RefreshUi();
                return;
            }
            var stableId = selection.StableId;
            var displayName = selection.DisplayName;
            selectionAdapter?.Disarm();
            var elapsed = Mathf.Max(0f, Time.unscaledTime - questionStartedAt);
            JawQuizDiagnostics.NoteGradingInvoked();
            var evaluation = engine.EvaluateSelection(stableId, elapsed);
            if (evaluation.Kind == JawQuizSelectionKind.Unlabelled)
            {
                SetFeedback("That part of the jaw is not labelled. Try a coloured anatomical region.", Amber);
                selectionAdapter?.Arm();
                RefreshUi();
                return;
            }

            latestAttempt = JawQuizAttemptRecord.Create(studentId, sessionId,
                engine.CurrentQuestion.QuestionId, RegionMapVersion(), evaluation.ExpectedRegionId,
                evaluation.SelectedRegionId, evaluation.Kind == JawQuizSelectionKind.Correct,
                evaluation.ResponseSeconds, evaluation.AttemptNumber, engine.HintLevel);
            attemptStore.Append(latestAttempt);
            StartCoroutine(SynchronizeAttempt(latestAttempt));

            var expected = DisplayName(evaluation.ExpectedRegionId);
            selectedText.text = $"Selected: {displayName}   •   Expected: {expected}";
            paintedRegions?.BrieflyEmphasize(stableId);
            if (evaluation.Kind == JawQuizSelectionKind.Correct)
            {
                SetFeedback(engine.CurrentQuestion.CorrectFeedback + " " +
                            engine.CurrentQuestion.EducationalExplanation, Green);
                JawQuizDiagnostics.NoteTtsInvoked();
                speech?.Speak(engine.CurrentQuestion.CorrectFeedback + " " +
                              engine.CurrentQuestion.EducationalExplanation);
                engine.CompleteCurrentQuestion();
            }
            else if (evaluation.Kind == JawQuizSelectionKind.Incorrect)
            {
                SetFeedback(engine.CurrentQuestion.IncorrectFeedback + $" You selected {displayName}.", Red);
                JawQuizDiagnostics.NoteTtsInvoked();
                speech?.Speak(engine.CurrentQuestion.IncorrectFeedback + " " + engine.CurrentQuestion.FirstHint);
                StartCoroutine(proxyClient.RequestHint(latestAttempt, engine.HintLevel, OnPersonalizedHint));
                if (engine.CanRetry)
                {
                    if (retryRoutine != null) StopCoroutine(retryRoutine);
                    retryRoutine = StartCoroutine(RetryAfterFeedback());
                }
                else
                {
                    engine.CompleteCurrentQuestion();
                    SetFeedback($"Maximum attempts reached. Expected: {expected}. " +
                                engine.CurrentQuestion.EducationalExplanation, Amber);
                    speech?.Speak($"The expected region was {expected}. " +
                                  engine.CurrentQuestion.EducationalExplanation);
                }
            }
            RefreshUi();
        }

        private IEnumerator RetryAfterFeedback()
        {
            yield return new WaitForSecondsRealtime(1.15f);
            engine.Retry();
            questionStartedAt = Time.unscaledTime;
            SetFeedback("Try again.", Amber);
            retryRoutine = null;
            ArmFindItInputWhenReady();
            RefreshUi();
        }

        public void SelectNextAnonymousProfile()
        {
            var suffix = studentId.Substring(studentId.Length - 3);
            var number = int.TryParse(suffix, out var parsed) ? parsed : 1;
            studentId = $"student_{number % 5 + 1:000}";
            RefreshUi();
        }

        public void ToggleMute()
        {
            if (speech == null) return;
            speech.Muted = !speech.Muted;
            RefreshUi();
        }

        private IEnumerator SynchronizePendingAttempts()
        {
            var pending = attemptStore.Pending(studentId).Take(20).ToArray();
            foreach (var attempt in pending)
                yield return SynchronizeAttempt(attempt);
        }

        private IEnumerator SynchronizeAttempt(JawQuizAttemptRecord attempt)
        {
            if (attempt == null || !synchronizingEventIds.Add(attempt.eventId)) yield break;
            syncing = true;
            lastSyncResult = "Synchronizing local attempt";
            RefreshUi();
            var success = false;
            yield return proxyClient.PostAttempt(attempt, (ok, _) => success = ok);
            if (success)
            {
                proxyConnected = true;
                var decision = JawQuizMemoryPolicy.Evaluate(attempt, attemptStore.Attempts);
                if (decision.ShouldWrite)
                {
                    var memorySuccess = false;
                    var reference = string.Empty;
                    yield return proxyClient.PostLearningEvent(attempt, (ok, _, remoteReference) =>
                    { memorySuccess = ok; reference = remoteReference; });
                    if (memorySuccess)
                    {
                        attempt.backboardResponseReference = reference;
                        attemptStore.MarkSynchronization(attempt.eventId, JawQuizSyncState.Synced, reference);
                        lastSyncResult = "Durable learning pattern synchronized";
                        backboardAvailable = true;
                    }
                    else lastSyncResult = "Offline • learning event queued";
                }
                else
                {
                    attemptStore.MarkSynchronization(attempt.eventId, JawQuizSyncState.Synced,
                        attempt.backboardResponseReference);
                    lastSyncResult = "Attempt synchronized";
                }
            }
            else
            {
                proxyConnected = false;
                lastSyncResult = "Offline • attempt queued";
            }
            synchronizingEventIds.Remove(attempt.eventId);
            syncing = synchronizingEventIds.Count > 0;
            RefreshUi();
        }

        private IEnumerator CheckProxyStatus()
        {
            if (statusCheckInFlight || proxyClient == null) yield break;
            statusCheckInFlight = true;
            yield return proxyClient.CheckStatus((connected, available, mode, _) =>
            {
                proxyConnected = connected;
                backboardAvailable = available;
                proxyMode = mode ?? string.Empty;
            });
            statusCheckInFlight = false;
            RefreshUi();
        }

        private void OnPersonalizedHint(bool success, string text, string reference)
        {
            var local = engine?.CurrentQuestion?.FirstHint ?? "Local feedback remains available.";
            text = JawQuizProxyClient.RemoteOrLocal(success, text, local);
            if (!success)
            {
                personalizedText.text = "Offline • " + text;
                proxyConnected = false;
                lastSyncResult = "Readonly hint unavailable • local fallback";
                RefreshUi();
                return;
            }
            personalizedText.text = "Personalized: " + text;
            proxyConnected = true;
            backboardAvailable = true;
            lastSyncResult = "Readonly personalized hint received";
            if (latestAttempt != null && !string.IsNullOrEmpty(reference))
            {
                latestAttempt.backboardResponseReference = reference;
                attemptStore.MarkSynchronization(latestAttempt.eventId,
                    latestAttempt.synchronizationState, reference);
            }
            speech?.Speak(text);
            RefreshUi();
        }

        private string RegionMapVersion()
        {
            var map = surfaceTarget?.regionMap;
            if (map == null) return "unknown";
            var signature = map.MeshSignatureSha256 ?? string.Empty;
            return $"data-v{map.DataVersion}:{(signature.Length > 12 ? signature.Substring(0, 12) : signature)}";
        }

        private static string SpokenQuestion(JawQuizQuestionDefinition question)
        {
            return string.IsNullOrWhiteSpace(question.SpokenPrompt)
                ? question.DisplayPrompt : question.SpokenPrompt;
        }

        private string DisplayName(string stableId)
        {
            return surfaceTarget?.regionMap?.GetRegion(stableId)?.DisplayName ?? stableId;
        }

        private void RefreshUi()
        {
            var running = engine?.CurrentQuestion != null && engine.State != JawQuizState.SessionComplete;
            startButton.interactable = engine == null || engine.State == JawQuizState.Idle ||
                                       engine.State == JawQuizState.SessionComplete;
            repeatButton.interactable = running;
            hintButton.interactable = running && engine.State == JawQuizState.AwaitingSelection;
            skipButton.interactable = running && engine.State != JawQuizState.QuestionComplete;
            nextButton.interactable = engine != null && engine.State == JawQuizState.QuestionComplete;
            var showStart = engine == null || engine.State == JawQuizState.Idle ||
                            engine.State == JawQuizState.SessionComplete;
            startButton.gameObject.SetActive(showStart);
            nextButton.gameObject.SetActive(!showStart);
            startButtonText.text = engine?.State == JawQuizState.SessionComplete ? "Restart Quiz" : "Start Quiz";
            // Arming/disarming the selection adapter is handled explicitly at each state
            // transition (ArmFindItInputWhenReady / Disarm), not implicitly here. RefreshUi runs
            // from a periodic background timer (proxy status polling) as well as real transitions,
            // and forcing AcceptingSelections off on every call previously broke input in the other
            // learning modes, which never populate `engine` at all.
            if (engine != null) JawQuizDiagnostics.CurrentModeState = engine.State.ToString();

            if (engine?.CurrentQuestion == null)
            {
                attemptText.text = "Not started";
            }
            else
            {
                var displayedAttempt = engine.State == JawQuizState.AwaitingSelection
                    ? Mathf.Min(engine.AttemptNumber + 1, engine.MaxAttempts)
                    : Mathf.Max(1, engine.AttemptNumber);
                attemptText.text = $"Question {engine.QuestionNumber}/{engine.QuestionCount}   •   Attempt {displayedAttempt}/{engine.MaxAttempts}";
            }
            var queued = attemptStore?.Pending(studentId).Count ?? 0;
            stateText.text = syncing ? "SYNCING" : proxyConnected
                ? (backboardAvailable ? "PROXY CONNECTED • BACKBOARD AVAILABLE" : "PROXY CONNECTED")
                : queued > 0 ? "OFFLINE • QUEUED" : "LOCAL";
            if (queued > 0) stateText.text += $" • {queued}";
            if (diagnosticLearningText != null)
                diagnosticLearningText.text = $"Connection: {(proxyConnected ? "connected" : "offline")}  •  " +
                    $"Mode: {(string.IsNullOrEmpty(proxyMode) ? "local" : proxyMode)}  •  Queue: {queued}\n" +
                    "Last sync: " + lastSyncResult;
            if (proxyCurrentUrlText != null && proxyClient != null)
                proxyCurrentUrlText.text = "Current Proxy URL: " + proxyClient.BaseUrl;
            if (profileButtonText != null)
                profileButtonText.text = studentId + " • " + sessionId.Substring(8, 6);
            if (muteButtonText != null)
                muteButtonText.text = speech != null && speech.Muted ? "Unmute" : "Mute";
            UpdateOverlayUi();
        }

        private void UpdateOverlayUi()
        {
            if (overlayButtonText == null || paintedRegions == null) return;
            overlayButtonText.text = paintedRegions.PaintedRegionsVisible
                ? "Hide Painted Regions"
                : "Show Painted Regions";
            if (virtualJawButtonText != null)
                virtualJawButtonText.text = paintedRegions.VirtualJawVisible
                    ? "Hide Virtual Jaw"
                    : "Show Virtual Jaw";
        }

        private void SetFeedback(string message, Color color)
        {
            feedbackText.text = message;
            feedbackText.color = color;
        }

        private void BuildInterface()
        {
            var canvasGo = new GameObject("Jaw Quiz Student UI");
            canvasGo.transform.SetParent(transform, false);
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 250;
            canvasScaler = canvasGo.AddComponent<CanvasScaler>();
            canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasScaler.referenceResolution = new Vector2(1080f, 2220f);
            canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            canvasScaler.matchWidthOrHeight = 0f;
            canvasGo.AddComponent<GraphicRaycaster>();

            var safe = Panel(canvasGo.transform, "Safe Area", Color.clear,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            safe.gameObject.AddComponent<JawQuizSafeArea>();

            topBar = Panel(safe, "Top Bar", Navy, new Vector2(0.02f, 0.875f),
                new Vector2(0.98f, 0.985f), Vector2.zero, Vector2.zero);
            topBar.GetComponent<Image>().raycastTarget = false;
            titleText = Label(topBar, "Jaw Landmark Quiz", 46, FontStyle.Bold, Color.white,
                new Vector2(0.035f, 0.52f), new Vector2(0.965f, 0.95f), TextAnchor.MiddleLeft);
            studentText = Label(topBar, "Profile", 25, FontStyle.Normal,
                new Color(0.78f, 0.86f, 0.92f), new Vector2(0.035f, 0.07f), new Vector2(0.18f, 0.49f), TextAnchor.MiddleLeft);
            profileButton = ActionButton(topBar, "student_001", SelectNextAnonymousProfile, out profileButtonText);
            muteButton = ActionButton(topBar, "Mute", ToggleMute, out muteButtonText);
            stateText = Label(topBar, "LOCAL • Idle", 24, FontStyle.Bold, Cyan,
                new Vector2(0.67f, 0.07f), new Vector2(0.965f, 0.49f), TextAnchor.MiddleRight);

            quizCard = Panel(safe, "Quiz Card", Card, new Vector2(0.02f, 0.64f),
                new Vector2(0.98f, 0.865f), Vector2.zero, Vector2.zero);
            quizCard.GetComponent<Image>().raycastTarget = false;
            attemptText = Label(quizCard, "Not started", 27, FontStyle.Bold, Cyan,
                new Vector2(0.035f, 0.86f), new Vector2(0.965f, 0.97f), TextAnchor.MiddleLeft);
            questionText = Label(quizCard, "Press Start Quiz when you are ready.", 42, FontStyle.Bold, Color.white,
                new Vector2(0.035f, 0.63f), new Vector2(0.965f, 0.86f), TextAnchor.MiddleLeft);
            hintText = Label(quizCard, "Questions are graded locally from the saved painted triangles.", 27,
                FontStyle.Italic, new Color(0.75f, 0.87f, 0.94f), new Vector2(0.035f, 0.47f),
                new Vector2(0.965f, 0.63f), TextAnchor.MiddleLeft);
            feedbackText = Label(quizCard, "", 28, FontStyle.Bold, Color.white,
                new Vector2(0.035f, 0.18f), new Vector2(0.965f, 0.47f), TextAnchor.MiddleLeft);
            selectedText = Label(quizCard, "Legend: all painted regions", 23, FontStyle.Normal,
                new Color(0.68f, 0.76f, 0.83f), new Vector2(0.035f, 0.03f),
                new Vector2(0.965f, 0.13f), TextAnchor.MiddleLeft);
            personalizedText = Label(quizCard, "Personalized explanation: local feedback ready", 22,
                FontStyle.Italic, Cyan, new Vector2(0.035f, 0.13f),
                new Vector2(0.965f, 0.25f), TextAnchor.MiddleLeft);


            trackingStatusPanel = Panel(safe, "Tracking Calibration Status", new Color(0.035f, 0.075f, 0.13f, 0.55f),
                new Vector2(0.02f, 0.575f), new Vector2(0.98f, 0.63f), Vector2.zero, Vector2.zero);
            trackingStatusPanel.GetComponent<Image>().raycastTarget = false;
            trackingStatusText = Label(trackingStatusPanel, "POINT CAMERA AT THE BLACK/WHITE JAW MARKER\nHold still while calibration locks", 27,
                FontStyle.Bold, Amber, new Vector2(0.025f, 0.08f), new Vector2(0.975f, 0.92f), TextAnchor.MiddleCenter);
            controlsPanel = Panel(safe, "Bottom Controls", Navy, new Vector2(0.02f, 0.015f),
                new Vector2(0.98f, 0.29f), Vector2.zero, Vector2.zero);
            controlsPanel.GetComponent<Image>().raycastTarget = false;
            startButton = ActionButton(controlsPanel, "Start Quiz", StartQuiz, out startButtonText);
            nextButton = ActionButton(controlsPanel, "Next", NextQuestion, out _);
            repeatButton = ActionButton(controlsPanel, "Repeat", RepeatQuestion, out _);
            hintButton = ActionButton(controlsPanel, "Hint", RequestHint, out _);
            skipButton = ActionButton(controlsPanel, "Skip", SkipQuestion, out _);
            overlayButton = ActionButton(controlsPanel, "Hide Painted Regions", TogglePaintedRegions, out overlayButtonText);
            diagnosticsButton = ActionButton(controlsPanel, "Developer Diagnostics", ToggleDiagnostics, out diagnosticsButtonText);

            BuildDiagnostics(safe);
            ApplyResponsiveLayout(Screen.width, Screen.height);
            SetDiagnosticsVisible(false);
        }

        private void BuildDiagnostics(Transform safe)
        {
            diagnosticsPanel = Panel(safe, "Developer Diagnostics Panel", new Color(0.025f, 0.05f, 0.08f, 0.995f),
                new Vector2(0.01f, 0.01f), new Vector2(0.99f, 0.99f), Vector2.zero, Vector2.zero).gameObject;
            var panel = diagnosticsPanel.transform;
            var blocker = diagnosticsPanel.AddComponent<CanvasGroup>();
            blocker.interactable = true;
            blocker.blocksRaycasts = true;

            Label(panel, "DEVELOPER DIAGNOSTICS", 38, FontStyle.Bold, Amber,
                new Vector2(0.04f, 0.92f), new Vector2(0.68f, 0.985f), TextAnchor.MiddleLeft);
            var close = CreateButton(panel, "Close", new Color(0.55f, 0.16f, 0.18f, 1f), 30,
                new Vector2(0.72f, 0.92f), new Vector2(0.96f, 0.985f));
            close.onClick.AddListener(() => SetDiagnosticsVisible(false));
            diagnosticLearningText = Label(panel, "Connection: local  •  Queue: 0", 22,
                FontStyle.Normal, Cyan, new Vector2(0.04f, 0.86f),
                new Vector2(0.70f, 0.915f), TextAnchor.MiddleLeft);

            proxyCurrentUrlText = Label(panel, "Current Proxy URL", 21, FontStyle.Bold, Color.white,
                new Vector2(0.04f, 0.815f), new Vector2(0.96f, 0.855f), TextAnchor.MiddleLeft);
            proxyUrlInput = CreateInputField(panel, new Vector2(0.04f, 0.755f), new Vector2(0.65f, 0.81f));
            proxyUrlInput.text = proxyClient?.BaseUrl ?? learningProxyUrl;
            proxyUrlInput.interactable = false;
            var editProxy = CreateButton(panel, "Edit", new Color(0.1f, 0.33f, 0.43f, 1f), 23,
                new Vector2(0.67f, 0.755f), new Vector2(0.80f, 0.81f));
            editProxy.onClick.AddListener(BeginProxyUrlEdit);
            var saveProxy = CreateButton(panel, "Save", Cyan, 23,
                new Vector2(0.82f, 0.755f), new Vector2(0.96f, 0.81f));
            saveProxy.onClick.AddListener(SaveProxyUrl);
            var resetProxy = CreateButton(panel, "Reset to Build Default", new Color(0.1f, 0.33f, 0.43f, 1f), 22,
                new Vector2(0.04f, 0.69f), new Vector2(0.58f, 0.745f));
            resetProxy.onClick.AddListener(ResetProxyUrl);
            var testProxy = CreateButton(panel, "Test Connection", Cyan, 22,
                new Vector2(0.60f, 0.69f), new Vector2(0.96f, 0.745f));
            testProxy.onClick.AddListener(TestProxyConnection);
            proxyTestResultText = Label(panel, "", 21, FontStyle.Bold, Amber,
                new Vector2(0.04f, 0.65f), new Vector2(0.96f, 0.685f), TextAnchor.MiddleLeft);

            var showAll = CreateButton(panel, "Show All Painted Regions", Cyan, 27,
                new Vector2(0.04f, 0.47f), new Vector2(0.61f, 0.53f));
            showAll.onClick.AddListener(ShowAllRegionColours);
            var unlabelled = CreateButton(panel, "Simulate Unlabelled", Amber, 26,
                new Vector2(0.63f, 0.47f), new Vector2(0.96f, 0.53f));
            unlabelled.onClick.AddListener(SimulateUnlabelledSelection);
            var virtualJaw = CreateButton(panel, "Hide Virtual Jaw", new Color(0.1f, 0.33f, 0.43f, 1f), 27,
                new Vector2(0.04f, 0.55f), new Vector2(0.96f, 0.62f));
            virtualJaw.onClick.AddListener(ToggleVirtualJaw);
            virtualJawButtonText = virtualJaw.GetComponentInChildren<Text>();

            var viewport = Panel(panel, "Region List Viewport", new Color(0.05f, 0.085f, 0.12f, 1f),
                new Vector2(0.04f, 0.035f), new Vector2(0.96f, 0.45f), Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<RectMask2D>();
            var contentGo = new GameObject("Region Rows");
            contentGo.transform.SetParent(viewport, false);
            var content = contentGo.AddComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            var rowIndex = 0;
            if (surfaceTarget?.regionMap != null)
            {
                foreach (var region in surfaceTarget.regionMap.Regions)
                {
                    var capturedId = region.StableId;
                    var row = Panel(content, "Row_" + capturedId, new Color(0.08f, 0.13f, 0.18f, 1f),
                        Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                    var rowLayout = row.gameObject.AddComponent<LayoutElement>();
                    rowLayout.preferredHeight = 82f;
                    row.anchorMin = new Vector2(0f, 1f);
                    row.anchorMax = new Vector2(1f, 1f);
                    row.pivot = new Vector2(0.5f, 1f);
                    row.sizeDelta = new Vector2(0f, 82f);
                    row.anchoredPosition = new Vector2(0f, -8f - rowIndex * 90f);
                    var highlight = CreateButton(row, region.DisplayName, new Color(0.12f, 0.2f, 0.27f, 1f), 27,
                        new Vector2(0.01f, 0.08f), new Vector2(0.68f, 0.92f));
                    highlight.onClick.AddListener(() => HighlightRegion(capturedId));
                    var simulate = CreateButton(row, "Simulate", new Color(0.12f, 0.55f, 0.62f, 1f), 26,
                        new Vector2(0.70f, 0.08f), new Vector2(0.99f, 0.92f));
                    simulate.onClick.AddListener(() => SimulateSelection(capturedId));
                    rowIndex++;
                }
            }
            content.sizeDelta = new Vector2(0f, 16f + rowIndex * 90f);

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 36f;
        }

        private void BeginProxyUrlEdit()
        {
            if (proxyUrlInput == null || proxyClient == null) return;
            proxyUrlInput.interactable = true;
            proxyUrlInput.text = proxyClient.BaseUrl;
            proxyUrlInput.Select();
            proxyUrlInput.ActivateInputField();
        }

        private void SaveProxyUrl()
        {
            if (proxyUrlInput == null) return;
            if (!JawQuizProxyConfiguration.Save(proxyUrlInput.text, out var normalized, out _))
            {
                SetProxyTestResult("Invalid private address", Red);
                return;
            }
            ApplyProxyUrl(normalized);
            proxyUrlInput.interactable = false;
            SetProxyTestResult("Saved", Green);
        }

        private void ResetProxyUrl()
        {
            ApplyProxyUrl(JawQuizProxyConfiguration.Reset(proxyBuildDefaultUrl));
            if (proxyUrlInput != null) proxyUrlInput.interactable = false;
            SetProxyTestResult("Reset to build default", Green);
        }

        private void ApplyProxyUrl(string url)
        {
            if (proxyClient == null) return;
            proxyClient.BaseUrl = url;
            if (proxyUrlInput != null) proxyUrlInput.text = url;
            proxyConnected = false;
            backboardAvailable = false;
            nextProxyStatusCheck = 0f;
            lastSyncResult = "Proxy address changed";
            RefreshUi();
        }

        private void TestProxyConnection()
        {
            if (proxyClient == null) return;
            if (!JawQuizProxyConfiguration.TryValidatePrivateBaseUrl(proxyClient.BaseUrl, true,
                    out _, out _))
            {
                SetProxyTestResult("Invalid private address", Red);
                return;
            }
            SetProxyTestResult("Testing…", Amber);
            StartCoroutine(proxyClient.CheckHealth(result =>
            {
                switch (result)
                {
                    case JawQuizProxyClient.HealthResult.Connected:
                        SetProxyTestResult("Connected", Green); break;
                    case JawQuizProxyClient.HealthResult.TimedOut:
                        SetProxyTestResult("Timed out", Amber); break;
                    case JawQuizProxyClient.HealthResult.InvalidPrivateAddress:
                        SetProxyTestResult("Invalid private address", Red); break;
                    case JawQuizProxyClient.HealthResult.Unauthorized:
                        SetProxyTestResult("Unauthorized", Red); break;
                    case JawQuizProxyClient.HealthResult.Cancelled:
                        SetProxyTestResult("Cancelled", Amber); break;
                    default:
                        SetProxyTestResult("Unavailable", Amber); break;
                }
            }));
        }

        private void SetProxyTestResult(string value, Color color)
        {
            if (proxyTestResultText == null) return;
            proxyTestResultText.text = value;
            proxyTestResultText.color = color;
        }

        private void ApplyResponsiveLayout(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            lastLayoutWidth = width;
            lastLayoutHeight = height;
            var portrait = height >= width;

            canvasScaler.referenceResolution = portrait ? new Vector2(1080f, 2220f) : new Vector2(2220f, 1080f);
            canvasScaler.matchWidthOrHeight = portrait ? 0f : 1f;
            SetAnchors(topBar, new Vector2(0.02f, portrait ? 0.875f : 0.84f), new Vector2(0.98f, 0.985f));
            SetAnchors(quizCard, new Vector2(0.02f, portrait ? 0.64f : 0.49f),
                new Vector2(0.98f, portrait ? 0.865f : 0.83f));
            SetAnchors(controlsPanel, new Vector2(0.02f, 0.015f),
                new Vector2(0.98f, portrait ? 0.29f : 0.25f));
            SetAnchors(trackingStatusPanel, new Vector2(0.02f, portrait ? 0.575f : 0.40f),
                new Vector2(0.98f, portrait ? 0.63f : 0.48f));

            SetTextLayout(titleText, 46, new Vector2(0.035f, 0.52f), new Vector2(0.965f, 0.95f), TextAnchor.MiddleLeft);
            SetTextLayout(studentText, portrait ? 25 : 27, new Vector2(0.035f, 0.07f),
                new Vector2(0.18f, 0.49f), TextAnchor.MiddleLeft);
            SetButtonLayout(profileButton, new Vector2(0.19f, 0.09f), new Vector2(0.46f, 0.47f), portrait ? 20 : 22);
            SetButtonLayout(muteButton, new Vector2(0.47f, 0.09f), new Vector2(0.61f, 0.47f), portrait ? 20 : 22);
            SetTextLayout(stateText, portrait ? 22 : 25, new Vector2(0.62f, 0.07f),
                new Vector2(0.965f, 0.49f), TextAnchor.MiddleRight);
            SetTextLayout(attemptText, 27, new Vector2(0.035f, 0.86f), new Vector2(0.965f, 0.97f), TextAnchor.MiddleLeft);
            SetTextLayout(questionText, portrait ? 42 : 48, new Vector2(0.035f, 0.63f),
                new Vector2(0.965f, 0.86f), TextAnchor.MiddleLeft);
            SetTextLayout(hintText, portrait ? 27 : 29, new Vector2(0.035f, 0.47f),
                new Vector2(0.965f, 0.63f), TextAnchor.MiddleLeft);
            SetTextLayout(feedbackText, portrait ? 27 : 29, new Vector2(0.035f, 0.25f),
                new Vector2(0.965f, 0.47f), TextAnchor.MiddleLeft);
            SetTextLayout(personalizedText, portrait ? 21 : 23, new Vector2(0.035f, 0.13f),
                new Vector2(0.965f, 0.25f), TextAnchor.MiddleLeft);
            SetTextLayout(selectedText, portrait ? 22 : 24, new Vector2(0.035f, 0.03f),
                new Vector2(0.965f, 0.13f), TextAnchor.MiddleLeft);

            SetButtonLayout(startButton, new Vector2(0.025f, 0.69f), new Vector2(0.975f, 0.96f), portrait ? 38 : 32);
            SetButtonLayout(nextButton, new Vector2(0.025f, 0.69f), new Vector2(0.975f, 0.96f), portrait ? 38 : 32);
            SetTextLayout(trackingStatusText, portrait ? 27 : 29, new Vector2(0.025f, 0.08f),
                new Vector2(0.975f, 0.92f), TextAnchor.MiddleCenter);
            SetButtonLayout(repeatButton, new Vector2(0.025f, 0.38f), new Vector2(0.33f, 0.64f), portrait ? 31 : 27);
            SetButtonLayout(hintButton, new Vector2(0.347f, 0.38f), new Vector2(0.653f, 0.64f), portrait ? 31 : 27);
            SetButtonLayout(skipButton, new Vector2(0.67f, 0.38f), new Vector2(0.975f, 0.64f), portrait ? 31 : 27);
            SetButtonLayout(overlayButton, new Vector2(0.025f, 0.07f), new Vector2(0.575f, 0.33f), portrait ? 27 : 25);
            SetButtonLayout(diagnosticsButton, new Vector2(0.592f, 0.07f), new Vector2(0.975f, 0.33f), portrait ? 27 : 25);
        }

        private static void SetAnchors(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetTextLayout(Text text, int size, Vector2 min, Vector2 max, TextAnchor alignment)
        {
            text.fontSize = size;
            text.alignment = alignment;
            SetAnchors(text.rectTransform, min, max);
        }

        private static void SetButtonLayout(Button button, Vector2 min, Vector2 max, int fontSize)
        {
            SetAnchors(button.GetComponent<RectTransform>(), min, max);
            button.GetComponentInChildren<Text>().fontSize = fontSize;
        }

        private static RectTransform Panel(Transform parent, string name, Color color,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = color.a > 0.01f;
            var rect = image.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return rect;
        }

        private static Text Label(Transform parent, string value, int fontSize, FontStyle style, Color color,
            Vector2 anchorMin, Vector2 anchorMax, TextAnchor alignment)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.text = value;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return text;
        }

        private static Button ActionButton(Transform parent, string label,
            UnityEngine.Events.UnityAction action, out Text buttonText)
        {
            var button = CreateButton(parent, label, new Color(0.1f, 0.33f, 0.43f, 1f), 24,
                Vector2.zero, Vector2.one);
            button.onClick.AddListener(action);
            buttonText = button.GetComponentInChildren<Text>();
            return button;
        }

        private static Button CreateButton(Transform parent, string label, Color color, int fontSize,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            var rect = Panel(parent, label + " Button", color, anchorMin, anchorMax, Vector2.zero, Vector2.zero);
            var button = rect.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.16f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.2f);
            colors.disabledColor = new Color(0.16f, 0.18f, 0.2f, 0.75f);
            button.colors = colors;
            Label(rect, label, fontSize, FontStyle.Bold, Color.white,
                new Vector2(0.03f, 0.04f), new Vector2(0.97f, 0.96f), TextAnchor.MiddleCenter);
            return button;
        }

        private static InputField CreateInputField(Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var rect = Panel(parent, "Proxy URL Input", new Color(0.08f, 0.13f, 0.18f, 1f),
                anchorMin, anchorMax, Vector2.zero, Vector2.zero);
            var input = rect.gameObject.AddComponent<InputField>();
            var text = Label(rect, string.Empty, 22, FontStyle.Normal, Color.white,
                new Vector2(0.035f, 0.08f), new Vector2(0.965f, 0.92f), TextAnchor.MiddleLeft);
            var placeholder = Label(rect, "http://192.168.x.x:8765", 22, FontStyle.Italic,
                new Color(0.55f, 0.62f, 0.68f), new Vector2(0.035f, 0.08f),
                new Vector2(0.965f, 0.92f), TextAnchor.MiddleLeft);
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = InputField.LineType.SingleLine;
            input.contentType = InputField.ContentType.Standard;
            return input;
        }
    }
}
