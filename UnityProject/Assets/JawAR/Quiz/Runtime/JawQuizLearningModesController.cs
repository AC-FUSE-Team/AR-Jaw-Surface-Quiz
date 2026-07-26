using System;
using System.Collections.Generic;
using BMC.JawAR.Quiz.Material3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz
{
    public enum JawQuizLearningMode
    {
        ModeSelection,
        FindIt,
        WhatIsThis,
        TwoPlayerChallenge
    }

    public enum JawTwoPlayerPhase
    {
        ChooseTargetPrivately,
        ConfirmTarget,
        TellPlayerTwo,
        PlayerTwoAnswer,
        Result
    }

    [Serializable]
    public sealed class JawTwoPlayerChallengeRecord
    {
        public int challengeNumber;
        public bool correctFirstAttempt;
        public bool correctAfterRetry;
        public bool unsuccessful;
        public int attempts;
        public float responseTimeSeconds;
        public string confusionPair;
        public int roleChangeCount;
    }

    /// <summary>
    /// Adds local learning modes around the proven Find It controller. It owns no tracking,
    /// calibration, region-map, grading, or network state. Every anatomical selection enters
    /// through JawQuizSurfaceSelectionAdapter and therefore resolves the saved triangle owner.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1100)]
    public sealed class JawQuizLearningModesController : MonoBehaviour
    {
        public const int DefaultTwoPlayerAttempts = 3;
        [Range(1, 5)] public int twoPlayerMaxAttempts = DefaultTwoPlayerAttempts;

        private JawQuizSceneController controller;
        private JawQuizCompactPortraitUi compactUi;
        private JawQuizSurfaceSelectionAdapter adapter;
        private JawQuizPaintedRegionPresenter presenter;
        private RectTransform safeArea;
        private RectTransform findItHud;
        private RectTransform modeSelectionRoot;
        private RectTransform modeSelectionCard;
        private RectTransform customHud;
        private TMP_Text modeChip;
        private TMP_Text customTitle;
        private TMP_Text customInstruction;
        private TMP_Text customSupporting;
        private TMP_Text scoreLabel;
        private readonly Button[] actionButtons = new Button[4];
        private readonly TMP_Text[] actionLabels = new TMP_Text[4];
        private readonly Dictionary<JawQuizLearningMode, JawPaintedRegionOverlayMode> overlayPreferences = new();
        private bool initialized;
        private string selectedRegionId = string.Empty;
        private string selectedRegionName = string.Empty;
        private string targetRegionId = string.Empty;
        private string targetRegionName = string.Empty;
        private int currentAttempts;
        private int completedChallenges;
        private int correctChallenges;
        private int correctFirstAttempt;
        private int correctAfterRetry;
        private int unsuccessfulChallenges;
        private int roleChangeCount;
        private int challengeNumber = 1;
        private float challengeStartedAt;
        private bool rolesSwitched;

        public JawQuizLearningMode CurrentMode { get; private set; } = JawQuizLearningMode.ModeSelection;
        public JawTwoPlayerPhase TwoPlayerPhase { get; private set; } = JawTwoPlayerPhase.ChooseTargetPrivately;
        public IReadOnlyList<JawTwoPlayerChallengeRecord> ChallengeRecords => challengeRecords;
        public string SelectedRegionId => selectedRegionId;
        public string TargetRegionId => targetRegionId;
        public int CurrentAttempts => currentAttempts;
        public int CorrectChallenges => correctChallenges;
        public int CompletedChallenges => completedChallenges;
        public int RoleChangeCount => roleChangeCount;
        public bool IsModeSelectionVisible => modeSelectionRoot != null && modeSelectionRoot.gameObject.activeSelf;
        public JawPaintedRegionOverlayMode CurrentOverlayMode => presenter != null
            ? presenter.OverlayMode : JawPaintedRegionOverlayMode.Hidden;

        private readonly List<JawTwoPlayerChallengeRecord> challengeRecords = new();

        public void Initialize(JawQuizSceneController sceneController, JawQuizCompactPortraitUi compact,
            RectTransform safe, RectTransform legacyFindItHud)
        {
            if (initialized) return;
            initialized = true;
            controller = sceneController;
            compactUi = compact;
            adapter = controller.selectionAdapter;
            presenter = controller.paintedRegions;
            safeArea = safe;
            findItHud = legacyFindItHud;
            overlayPreferences[JawQuizLearningMode.FindIt] = JawPaintedRegionOverlayMode.Hidden;
            overlayPreferences[JawQuizLearningMode.WhatIsThis] = JawPaintedRegionOverlayMode.AllRegions;
            overlayPreferences[JawQuizLearningMode.TwoPlayerChallenge] = JawPaintedRegionOverlayMode.AllRegions;
            BuildModeSelection();
            BuildCustomHud();
            if (adapter != null) adapter.DetailedSelectionReceived += OnDetailedSelection;
            ReturnToModeSelection();
        }

        private void OnDestroy()
        {
            if (adapter != null) adapter.DetailedSelectionReceived -= OnDetailedSelection;
        }

        private void LateUpdate()
        {
            if (!initialized || adapter == null) return;
            adapter.BlockingOverlayOpen = compactUi.DrawerOpen || compactUi.DiagnosticsOpen ||
                                          CurrentMode == JawQuizLearningMode.ModeSelection;
        }

        public void ReturnToModeSelection()
        {
            if (!initialized) return;
            SaveOverlayPreference();
            ClearTransientState();
            controller.ResetTemporaryStateForModeChange();
            compactUi.CloseDrawer();
            controller.SetDiagnosticsVisible(false);
            CurrentMode = JawQuizLearningMode.ModeSelection;
            JawQuizDiagnostics.CurrentMode = "ModeSelection";
            if (findItHud != null) findItHud.gameObject.SetActive(false);
            customHud.gameObject.SetActive(false);
            modeSelectionRoot.gameObject.SetActive(true);
            presenter?.SetOverlayMode(JawPaintedRegionOverlayMode.Hidden, string.Empty);
            ConfigureInput(false, false, false);
        }

        public void SelectMode(JawQuizLearningMode mode)
        {
            if (mode == JawQuizLearningMode.ModeSelection)
            {
                ReturnToModeSelection();
                return;
            }
            SaveOverlayPreference();
            ClearTransientState();
            controller.ResetTemporaryStateForModeChange();
            compactUi.CloseDrawer();
            controller.SetDiagnosticsVisible(false);
            CurrentMode = mode;
            JawQuizDiagnostics.CurrentMode = mode.ToString();
            modeSelectionRoot.gameObject.SetActive(false);
            var findIt = mode == JawQuizLearningMode.FindIt;
            if (findItHud != null) findItHud.gameObject.SetActive(findIt);
            customHud.gameObject.SetActive(!findIt);

            if (findIt)
            {
                presenter?.SetOverlayMode(overlayPreferences[mode], string.Empty);
                ConfigureInput(false, true, false);
                return;
            }
            if (mode == JawQuizLearningMode.WhatIsThis)
            {
                presenter?.SetOverlayMode(overlayPreferences[mode], string.Empty);
                ConfigureInput(true, true, true);
                RenderWhatIsThis();
                return;
            }
            ResetTwoPlayerSession();
            presenter?.SetOverlayMode(overlayPreferences[mode], string.Empty);
            ConfigureInput(true, false, true);
            RenderTwoPlayer();
        }

        public string OverlaySettingLabel()
        {
            if (CurrentMode == JawQuizLearningMode.ModeSelection) return "Painted Region Overlay: Hidden";
            var mode = overlayPreferences.TryGetValue(CurrentMode, out var saved)
                ? saved : JawPaintedRegionOverlayMode.Hidden;
            return "Painted Region Overlay: " + OverlayName(mode);
        }

        public void CycleOverlaySetting()
        {
            if (CurrentMode == JawQuizLearningMode.ModeSelection) return;
            var current = overlayPreferences.TryGetValue(CurrentMode, out var saved)
                ? saved : JawPaintedRegionOverlayMode.Hidden;
            var next = current == JawPaintedRegionOverlayMode.Hidden
                ? JawPaintedRegionOverlayMode.SelectedOnly
                : current == JawPaintedRegionOverlayMode.SelectedOnly
                    ? JawPaintedRegionOverlayMode.AllRegions
                    : JawPaintedRegionOverlayMode.Hidden;
            overlayPreferences[CurrentMode] = next;
            ApplyOverlayPreference();
            if (CurrentMode == JawQuizLearningMode.WhatIsThis) RenderWhatIsThis();
            else if (CurrentMode == JawQuizLearningMode.TwoPlayerChallenge) RenderTwoPlayer();
        }

        public void RepeatName()
        {
            if (CurrentMode != JawQuizLearningMode.WhatIsThis || string.IsNullOrEmpty(selectedRegionName)) return;
            controller.StopModeSpeech();
            controller.SpeakModeText(selectedRegionName);
        }

        public void ClearWhatIsThisSelection()
        {
            if (CurrentMode != JawQuizLearningMode.WhatIsThis) return;
            selectedRegionId = selectedRegionName = string.Empty;
            controller.StopModeSpeech();
            ApplyOverlayPreference();
            RenderWhatIsThis();
        }

        public void ConfirmPrivateTarget()
        {
            if (CurrentMode != JawQuizLearningMode.TwoPlayerChallenge ||
                TwoPlayerPhase != JawTwoPlayerPhase.ConfirmTarget || string.IsNullOrEmpty(targetRegionId)) return;
            TwoPlayerPhase = JawTwoPlayerPhase.TellPlayerTwo;
            presenter?.SetOverlayMode(JawPaintedRegionOverlayMode.AllRegions, string.Empty);
            ConfigureInput(false, false, false);
            RenderTwoPlayer();
        }

        public void ChooseTargetAgain()
        {
            if (CurrentMode != JawQuizLearningMode.TwoPlayerChallenge) return;
            targetRegionId = targetRegionName = string.Empty;
            TwoPlayerPhase = JawTwoPlayerPhase.ChooseTargetPrivately;
            presenter?.SetOverlayMode(overlayPreferences[CurrentMode], string.Empty);
            ConfigureInput(true, false, true);
            RenderTwoPlayer();
        }

        public void PlayerTwoReady()
        {
            if (CurrentMode != JawQuizLearningMode.TwoPlayerChallenge ||
                TwoPlayerPhase != JawTwoPlayerPhase.TellPlayerTwo || string.IsNullOrEmpty(targetRegionId)) return;
            TwoPlayerPhase = JawTwoPlayerPhase.PlayerTwoAnswer;
            currentAttempts = 0;
            challengeStartedAt = Time.unscaledTime;
            presenter?.SetOverlayMode(NeutralAnswerOverlay(), string.Empty);
            ConfigureInput(false, true, true);
            RenderTwoPlayer();
        }

        public void SamePlayerChoosesAgain()
        {
            if (TwoPlayerPhase != JawTwoPlayerPhase.Result) return;
            challengeNumber++;
            ChooseTargetAgain();
        }

        public void SwitchPlayers()
        {
            if (TwoPlayerPhase != JawTwoPlayerPhase.Result) return;
            roleChangeCount++;
            rolesSwitched = !rolesSwitched;
            challengeNumber++;
            ChooseTargetAgain();
        }

        private void OnDetailedSelection(JawQuizSurfaceSelection selection)
        {
            if (adapter == null || adapter.BlockingOverlayOpen) return;
            if (CurrentMode == JawQuizLearningMode.WhatIsThis)
            {
                HandleWhatIsThisSelection(selection);
                return;
            }
            if (CurrentMode != JawQuizLearningMode.TwoPlayerChallenge) return;
            if (TwoPlayerPhase == JawTwoPlayerPhase.ChooseTargetPrivately &&
                selection.Source == JawQuizSelectionSource.ScreenTap)
                HandlePrivateTargetSelection(selection);
            else if (TwoPlayerPhase == JawTwoPlayerPhase.PlayerTwoAnswer &&
                     selection.Source == JawQuizSelectionSource.PhysicalFingertip)
                EvaluatePlayerTwo(selection);
        }

        private void HandleWhatIsThisSelection(JawQuizSurfaceSelection selection)
        {
            if (selection.HitKind == JawQuizSurfaceHitKind.EmptySpace)
            {
                customSupporting.text = "Tap or point directly at a painted region.";
                return;
            }
            if (selection.HitKind == JawQuizSurfaceHitKind.UnlabelledTriangle)
            {
                selectedRegionId = selectedRegionName = string.Empty;
                customTitle.text = "This area has not been labelled yet.";
                customInstruction.text = "Tap or point to another region";
                customSupporting.text = string.Empty;
                ApplyOverlayPreference();
                return;
            }
            selectedRegionId = selection.StableId;
            selectedRegionName = selection.DisplayName;
            controller.StopModeSpeech();
            JawQuizDiagnostics.NoteTtsInvoked();
            controller.SpeakModeText(selectedRegionName);
            presenter?.BrieflyEmphasize(selectedRegionId);
            ApplyOverlayPreference();
            RenderWhatIsThis();
        }

        private void HandlePrivateTargetSelection(JawQuizSurfaceSelection selection)
        {
            if (selection.HitKind == JawQuizSurfaceHitKind.EmptySpace)
            {
                customSupporting.text = "Tap directly on a painted jaw region.";
                return;
            }
            if (selection.HitKind == JawQuizSurfaceHitKind.UnlabelledTriangle)
            {
                customSupporting.text = "That triangle has not been labelled and cannot be a target.";
                return;
            }
            targetRegionId = selection.StableId;
            targetRegionName = selection.DisplayName;
            TwoPlayerPhase = JawTwoPlayerPhase.ConfirmTarget;
            controller.StopModeSpeech();
            presenter?.BrieflyEmphasize(targetRegionId);
            ConfigureInput(false, false, false);
            RenderTwoPlayer();
        }

        private void EvaluatePlayerTwo(JawQuizSurfaceSelection selection)
        {
            if (selection.HitKind != JawQuizSurfaceHitKind.LabelledRegion) return;
            JawQuizDiagnostics.NoteGradingInvoked();
            currentAttempts++;
            var correct = string.Equals(selection.StableId, targetRegionId, StringComparison.Ordinal);
            if (correct)
            {
                completedChallenges++;
                correctChallenges++;
                if (currentAttempts == 1) correctFirstAttempt++; else correctAfterRetry++;
                AddRecord(correct, selection.StableId);
                TwoPlayerPhase = JawTwoPlayerPhase.Result;
                ConfigureInput(false, false, false);
                presenter?.SetOverlayMode(JawPaintedRegionOverlayMode.SelectedOnly, targetRegionId);
                controller.StopModeSpeech();
                JawQuizDiagnostics.NoteTtsInvoked();
                controller.SpeakModeText("Correct — " + targetRegionName);
                RenderTwoPlayer("Correct", JawMaterialTheme.Success,
                    "The requested region was " + targetRegionName + ".");
                return;
            }
            if (currentAttempts < MaxTwoPlayerAttempts)
            {
                presenter?.BrieflyEmphasize(selection.StableId);
                RenderTwoPlayer("Try Again", JawMaterialTheme.Warning,
                    "You pointed to " + selection.DisplayName + $". Attempt {currentAttempts + 1} of {MaxTwoPlayerAttempts}.");
                return;
            }
            completedChallenges++;
            unsuccessfulChallenges++;
            AddRecord(false, selection.StableId);
            TwoPlayerPhase = JawTwoPlayerPhase.Result;
            ConfigureInput(false, false, false);
            presenter?.SetOverlayMode(JawPaintedRegionOverlayMode.SelectedOnly, targetRegionId);
            controller.StopModeSpeech();
            JawQuizDiagnostics.NoteTtsInvoked();
            controller.SpeakModeText("The correct region was " + targetRegionName);
            RenderTwoPlayer("Maximum attempts reached", JawMaterialTheme.Error,
                "Correct answer: " + targetRegionName);
        }

        private void AddRecord(bool correct, string selectedId)
        {
            challengeRecords.Add(new JawTwoPlayerChallengeRecord
            {
                challengeNumber = challengeNumber,
                correctFirstAttempt = correct && currentAttempts == 1,
                correctAfterRetry = correct && currentAttempts > 1,
                unsuccessful = !correct,
                attempts = currentAttempts,
                responseTimeSeconds = Mathf.Max(0f, Time.unscaledTime - challengeStartedAt),
                confusionPair = correct ? string.Empty : targetRegionId + " -> " + selectedId,
                roleChangeCount = roleChangeCount
            });
        }

        private void RenderWhatIsThis()
        {
            JawQuizDiagnostics.CurrentModeState = string.IsNullOrEmpty(selectedRegionName)
                ? "AwaitingSelection" : "RegionIdentified";
            modeChip.text = "WHAT IS THIS?";
            customTitle.text = string.IsNullOrEmpty(selectedRegionName) ? "Select a painted region" : selectedRegionName;
            customInstruction.text = string.IsNullOrEmpty(selectedRegionName)
                ? "Point with your finger, or tap the visible painted jaw."
                : "Tap or point to another region";
            customSupporting.text = string.IsNullOrEmpty(selectedRegionName)
                ? "Only saved painted-triangle labels are identified."
                : controller.SpeechMuted ? "Name shown visually • audio muted" : "Name spoken through device media audio";
            scoreLabel.text = OverlaySettingLabel();
            SetActions(("Repeat Name", RepeatName), (OverlayActionName(), CycleOverlaySetting),
                ("Clear Selection", ClearWhatIsThisSelection), ("Back to Modes", ReturnToModeSelection));
        }

        private void RenderTwoPlayer(string resultTitle = null, Color? resultColor = null, string resultSupport = null)
        {
            JawQuizDiagnostics.CurrentModeState = TwoPlayerPhase.ToString();
            modeChip.text = "TWO-PLAYER CHALLENGE";
            customTitle.color = JawMaterialTheme.OnSurface;
            scoreLabel.text = $"Player 2: {correctChallenges} correct out of {completedChallenges}  •  Challenge {challengeNumber}";
            switch (TwoPlayerPhase)
            {
                case JawTwoPlayerPhase.ChooseTargetPrivately:
                    customTitle.text = "Choose a region for Player 2.";
                    customInstruction.text = "Player 1: tap a labelled painted region on the screen.";
                    customSupporting.text = rolesSwitched
                        ? "Roles switched • Player 1 now holds the phone • no target audio"
                        : "Private selection • no target audio";
                    SetActions((OverlayActionName(), CycleOverlaySetting), ("", null), ("", null),
                        ("Back to Modes", ReturnToModeSelection));
                    break;
                case JawTwoPlayerPhase.ConfirmTarget:
                    customTitle.text = "You selected: " + targetRegionName;
                    customInstruction.text = "Confirm this private target.";
                    customSupporting.text = "The name is shown privately. Audio is off so Player 2 cannot hear the answer.";
                    SetActions(("Use This Region", ConfirmPrivateTarget), ("Choose Again", ChooseTargetAgain),
                        ("", null), ("Back to Modes", ReturnToModeSelection));
                    break;
                case JawTwoPlayerPhase.TellPlayerTwo:
                    customTitle.text = "Tell Player 2: Find the " + targetRegionName + ".";
                    customInstruction.text = "Say the anatomical name aloud, then hide the answer.";
                    customSupporting.text = "The app does not speak during this private handoff.";
                    SetActions(("Player 2 Ready", PlayerTwoReady), ("Choose Again", ChooseTargetAgain),
                        ("", null), ("Back to Modes", ReturnToModeSelection));
                    break;
                case JawTwoPlayerPhase.PlayerTwoAnswer:
                    customTitle.text = resultTitle ?? "Player 2: point to the requested region";
                    customTitle.color = resultColor ?? JawMaterialTheme.OnSurface;
                    customInstruction.text = "Hold your finger over the physical jaw region.";
                    customSupporting.text = resultSupport ?? $"Attempt {currentAttempts + 1} of {MaxTwoPlayerAttempts} • target hidden";
                    SetActions(("", null), ("", null), ("", null), ("End Session", ReturnToModeSelection));
                    break;
                case JawTwoPlayerPhase.Result:
                    customTitle.text = resultTitle ?? "Challenge complete";
                    customTitle.color = resultColor ?? JawMaterialTheme.OnSurface;
                    customInstruction.text = resultSupport ?? ("Target: " + targetRegionName);
                    customSupporting.text = $"First try: {correctFirstAttempt} • after retry: {correctAfterRetry} • unsuccessful: {unsuccessfulChallenges}";
                    SetActions((unsuccessfulChallenges > 0 && challengeRecords.Count > 0 &&
                                challengeRecords[challengeRecords.Count - 1].unsuccessful
                            ? "Next Challenge • Same Chooser" : "Same Player Chooses Again", SamePlayerChoosesAgain),
                        ("Switch Players", SwitchPlayers), ("", null), ("End Session", ReturnToModeSelection));
                    break;
            }
        }

        private void ResetTwoPlayerSession()
        {
            challengeRecords.Clear();
            completedChallenges = correctChallenges = correctFirstAttempt = correctAfterRetry = 0;
            unsuccessfulChallenges = roleChangeCount = 0;
            challengeNumber = 1;
            rolesSwitched = false;
            targetRegionId = targetRegionName = string.Empty;
            currentAttempts = 0;
            TwoPlayerPhase = JawTwoPlayerPhase.ChooseTargetPrivately;
        }

        private void ClearTransientState()
        {
            controller.StopModeSpeech();
            selectedRegionId = selectedRegionName = string.Empty;
            targetRegionId = targetRegionName = string.Empty;
            currentAttempts = 0;
            presenter?.ClearHighlight();
            if (customTitle != null) customTitle.color = JawMaterialTheme.OnSurface;
        }

        /// <summary>
        /// Routes every mode/phase transition through the adapter's Arm()/Disarm() gate instead of
        /// flipping AcceptingSelections directly, so a physical selection completed just before the
        /// transition (previous phase's feedback, the drawer, a lingering finger) cannot be graded
        /// against the state that is about to be shown.
        /// </summary>
        private void ConfigureInput(bool screen, bool fingertip, bool accepting)
        {
            if (adapter == null) return;
            adapter.acceptScreenInput = screen;
            adapter.acceptFingertipInput = fingertip;
            if (accepting) adapter.Arm();
            else adapter.Disarm();
        }

        private void SaveOverlayPreference()
        {
            if (presenter == null || CurrentMode == JawQuizLearningMode.ModeSelection) return;
            if (CurrentMode == JawQuizLearningMode.TwoPlayerChallenge &&
                (TwoPlayerPhase == JawTwoPlayerPhase.PlayerTwoAnswer || TwoPlayerPhase == JawTwoPlayerPhase.Result))
                return;
            overlayPreferences[CurrentMode] = presenter.OverlayMode;
        }

        private void ApplyOverlayPreference()
        {
            if (presenter == null || !overlayPreferences.TryGetValue(CurrentMode, out var mode)) return;
            if (CurrentMode == JawQuizLearningMode.TwoPlayerChallenge &&
                TwoPlayerPhase == JawTwoPlayerPhase.PlayerTwoAnswer)
            {
                presenter.SetOverlayMode(NeutralAnswerOverlay(), string.Empty);
                return;
            }
            var selected = mode == JawPaintedRegionOverlayMode.SelectedOnly ? selectedRegionId : string.Empty;
            presenter.SetOverlayMode(mode, selected);
        }

        private JawPaintedRegionOverlayMode NeutralAnswerOverlay()
        {
            return overlayPreferences.TryGetValue(JawQuizLearningMode.TwoPlayerChallenge, out var preference) &&
                   preference == JawPaintedRegionOverlayMode.AllRegions
                ? JawPaintedRegionOverlayMode.AllRegions
                : JawPaintedRegionOverlayMode.Hidden;
        }

        private int MaxTwoPlayerAttempts => Mathf.Clamp(twoPlayerMaxAttempts, 1, 5);

        private string OverlayActionName() => "Overlay: " + OverlayName(
            overlayPreferences.TryGetValue(CurrentMode, out var mode) ? mode : JawPaintedRegionOverlayMode.Hidden);

        private static string OverlayName(JawPaintedRegionOverlayMode mode) => mode switch
        {
            JawPaintedRegionOverlayMode.SelectedOnly => "Selected Only",
            JawPaintedRegionOverlayMode.AllRegions => "All Regions",
            _ => "Hidden"
        };

        private void SetActions(params (string label, Action action)[] definitions)
        {
            for (var i = 0; i < actionButtons.Length; i++)
            {
                var enabled = i < definitions.Length && !string.IsNullOrEmpty(definitions[i].label) &&
                              definitions[i].action != null;
                actionButtons[i].gameObject.SetActive(enabled);
                actionButtons[i].onClick.RemoveAllListeners();
                if (!enabled) continue;
                actionLabels[i].text = definitions[i].label;
                var captured = definitions[i].action;
                actionButtons[i].onClick.AddListener(() => captured());
            }
        }

        private void BuildModeSelection()
        {
            modeSelectionRoot = JawMaterialWidgets.Card(safeArea, "Learning Mode Selection",
                new Color(JawMaterialTheme.Scrim.r, JawMaterialTheme.Scrim.g, JawMaterialTheme.Scrim.b, 0.72f), 0f);
            SetRect(modeSelectionRoot, Vector2.zero, Vector2.one);
            modeSelectionCard = JawMaterialWidgets.Card(modeSelectionRoot, "Learning Modes Card",
                JawMaterialTheme.Surface, JawMaterialTheme.RadiusLarge);
            SetRect(modeSelectionCard, new Vector2(0.055f, 0.12f), new Vector2(0.945f, 0.92f));
            var title = JawMaterialWidgets.Label(modeSelectionCard, "Choose a learning mode",
                JawMaterialTheme.TypeQuestionSize + 4, JawMaterialTheme.FontBold,
                JawMaterialTheme.OnSurface, TextAlignmentOptions.Center);
            SetRect(title.rectTransform, new Vector2(0.06f, 0.87f), new Vector2(0.94f, 0.97f));
            var subtitle = JawMaterialWidgets.Label(modeSelectionCard,
                "All modes use the same saved painted jaw regions.", JawMaterialTheme.TypeSupportingSize,
                JawMaterialTheme.FontRegular, JawMaterialTheme.OnSurfaceVariant, TextAlignmentOptions.Center);
            SetRect(subtitle.rectTransform, new Vector2(0.06f, 0.80f), new Vector2(0.94f, 0.87f));
            BuildModeCard(0, "Find It", "The app names a region. Point to it.", JawMaterialIcons.Lightbulb,
                JawQuizLearningMode.FindIt);
            BuildModeCard(1, "What Is This?", "Select a region to learn its name.", JawMaterialIcons.Visibility,
                JawQuizLearningMode.WhatIsThis);
            BuildModeCard(2, "Two-Player Challenge", "One player chooses. The other player finds it.",
                JawMaterialIcons.Person, JawQuizLearningMode.TwoPlayerChallenge);
        }

        private void BuildModeCard(int index, string title, string description, string icon,
            JawQuizLearningMode mode)
        {
            var top = 0.76f - index * 0.235f;
            var card = JawMaterialWidgets.Card(modeSelectionCard, title + " Mode Card",
                index == 0 ? new Color(JawMaterialTheme.Primary.r, JawMaterialTheme.Primary.g,
                    JawMaterialTheme.Primary.b, 0.16f) : JawMaterialTheme.SurfaceContainer,
                JawMaterialTheme.RadiusMedium);
            SetRect(card, new Vector2(0.055f, top - 0.19f), new Vector2(0.945f, top));
            var button = card.gameObject.AddComponent<Button>();
            button.targetGraphic = card.GetComponent<Image>();
            button.onClick.AddListener(() => SelectMode(mode));
            JawMaterialWidgets.EnsureTouchTarget(card.gameObject);
            var image = JawMaterialWidgets.Icon(card, icon, JawMaterialTheme.Primary, 54f);
            SetRect(image.rectTransform, new Vector2(0.04f, 0.25f), new Vector2(0.18f, 0.75f));
            var titleLabel = JawMaterialWidgets.Label(card, title, JawMaterialTheme.TypeQuestionSize - 5,
                JawMaterialTheme.FontBold, JawMaterialTheme.OnSurface, TextAlignmentOptions.MidlineLeft);
            SetRect(titleLabel.rectTransform, new Vector2(0.21f, 0.48f), new Vector2(0.92f, 0.86f));
            var descriptionLabel = JawMaterialWidgets.Label(card, description, JawMaterialTheme.TypeSupportingSize,
                JawMaterialTheme.FontRegular, JawMaterialTheme.OnSurfaceVariant, TextAlignmentOptions.MidlineLeft);
            SetRect(descriptionLabel.rectTransform, new Vector2(0.21f, 0.12f), new Vector2(0.92f, 0.50f));
        }

        private void BuildCustomHud()
        {
            customHud = JawMaterialWidgets.Card(safeArea, "Learning Mode HUD", JawMaterialTheme.Surface,
                JawMaterialTheme.RadiusLarge);
            SetRect(customHud, new Vector2(0.015f, 0.755f), new Vector2(0.985f, 0.995f));
            JawMaterialWidgets.NewButton(customHud, "Mode Hamburger Button", JawButtonStyle.IconOnly,
                string.Empty, JawMaterialIcons.Menu, compactUi.OpenDrawer);
            modeChip = JawMaterialWidgets.Chip(customHud, "MODE", JawMaterialIcons.Visibility,
                JawMaterialTheme.Primary, JawMaterialTheme.OnPrimary).label;
            customTitle = JawMaterialWidgets.Label(customHud, string.Empty, JawMaterialTheme.TypeQuestionSize,
                JawMaterialTheme.FontBold, JawMaterialTheme.OnSurface, TextAlignmentOptions.MidlineLeft);
            customInstruction = JawMaterialWidgets.Label(customHud, string.Empty, JawMaterialTheme.TypeSupportingSize,
                JawMaterialTheme.FontMedium, JawMaterialTheme.OnSurfaceVariant, TextAlignmentOptions.MidlineLeft);
            customSupporting = JawMaterialWidgets.Label(customHud, string.Empty,
                JawMaterialTheme.TypeProgressStatusSize, JawMaterialTheme.FontRegular,
                JawMaterialTheme.OnSurfaceVariant, TextAlignmentOptions.MidlineLeft);
            scoreLabel = JawMaterialWidgets.Label(customHud, string.Empty,
                JawMaterialTheme.TypeProgressStatusSize, JawMaterialTheme.FontMedium,
                JawMaterialTheme.Primary, TextAlignmentOptions.MidlineRight);
            for (var i = 0; i < actionButtons.Length; i++)
            {
                var action = JawMaterialWidgets.NewButton(customHud, "Mode Action " + (i + 1),
                    i == 0 ? JawButtonStyle.Filled : JawButtonStyle.Outlined, "Action", null, null);
                actionButtons[i] = action.button;
                actionLabels[i] = action.skin.Label;
            }
            LayoutCustomHud();
            customHud.gameObject.SetActive(false);
        }

        private void LayoutCustomHud()
        {
            SetRect(Find(customHud, "Mode Hamburger Button"), new Vector2(0.015f, 0.72f), new Vector2(0.12f, 0.97f));
            SetRect(modeChip.rectTransform.parent as RectTransform, new Vector2(0.14f, 0.75f), new Vector2(0.47f, 0.96f));
            SetRect(scoreLabel.rectTransform, new Vector2(0.48f, 0.75f), new Vector2(0.98f, 0.96f));
            SetRect(customTitle.rectTransform, new Vector2(0.03f, 0.49f), new Vector2(0.97f, 0.73f));
            SetRect(customInstruction.rectTransform, new Vector2(0.03f, 0.34f), new Vector2(0.97f, 0.51f));
            SetRect(customSupporting.rectTransform, new Vector2(0.03f, 0.22f), new Vector2(0.97f, 0.35f));
            const float gap = 0.012f;
            var width = (0.94f - gap * 3f) / 4f;
            for (var i = 0; i < actionButtons.Length; i++)
            {
                var left = 0.03f + i * (width + gap);
                SetRect(actionButtons[i].GetComponent<RectTransform>(), new Vector2(left, 0.025f),
                    new Vector2(left + width, 0.205f));
            }
        }

        private static RectTransform Find(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root as RectTransform;
            for (var i = 0; i < root.childCount; i++)
            {
                var result = Find(root.GetChild(i), name);
                if (result != null) return result;
            }
            return null;
        }

        private static void SetRect(RectTransform rect, Vector2 min, Vector2 max)
        {
            if (rect == null) return;
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
