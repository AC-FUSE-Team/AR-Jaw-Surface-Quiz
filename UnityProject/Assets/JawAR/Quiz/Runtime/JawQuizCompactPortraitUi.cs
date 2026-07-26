using System;
using System.Collections.Generic;
using System.Reflection;
using BMC.JawAR.Quiz.Material3;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz
{
    /// <summary>
    /// Quiz-only presentation layer. It reuses the controller's existing controls/callbacks and
    /// changes no tracking, selection, grading, persistence, or network behaviour. Visually this is
    /// a Unity-native, Material 3-inspired interpretation (Unity UI + TextMeshPro + the
    /// <see cref="Material3"/> token/widget layer) — not the official Google Material
    /// Components/Compose library.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class JawQuizCompactPortraitUi : MonoBehaviour
    {
        private const float ToastSeconds = 2.6f;
        private const float LockedBannerSeconds = 2.6f;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private JawQuizSceneController controller;
        private JawQuizLearningModesController learningModes;
        private RectTransform safeArea;

        // ---- HUD ----
        private RectTransform hud;
        private TMP_Text questionLabel;
        private TMP_Text progressChipLabel;
        private TMP_Text attemptChipLabel;
        private RectTransform progressTrack;
        private RectTransform progressFill;

        // ---- Drawer ----
        private RectTransform drawerBackdrop;
        private RectTransform drawer;
        private RectTransform drawerViewport;
        private RectTransform drawerContent;
        private readonly List<RectTransform> drawerRows = new();
        private TMP_Text drawerStatusChipLabel;
        private Image drawerStatusChipBg;
        private TMP_Text drawerPersonalizedLabel;
        private JawSwitchSkin muteSwitch;
        private JawSwitchSkin overlaySwitch;
        private JawSwitchSkin virtualJawSwitch;
        private TMP_Text modesDrawerLabel;
        private TMP_Text overlayModeDrawerLabel;

        // The controller re-applies its own layout (including Text.fontSize) to every button it
        // owns whenever the screen size changes (JawQuizSceneController.ApplyResponsiveLayout runs
        // from its own Update()) — it doesn't know we've re-skinned these labels, so each frame we
        // cheaply reassert our color/size over its plain values rather than fight for setup order.
        private readonly List<(Text label, Color color, int fontSize)> legacyRowStyles = new();

        // ---- Snackbar ----
        private JawSnackbarSkin snackbar;

        // ---- Tracking status ----
        private RectTransform trackingPanel;
        private Image trackingPanelBg;

        // ---- Controller-sourced content (read-only mirrors) ----
        private Text questionSource;
        private Text attemptSource;
        private Text hintSource;
        private Text feedbackSource;
        private Text personalizedSource;
        private Text stateSource;
        private Button startButton;
        private Button repeatButton;
        private Button hintButton;
        private Button skipButton;
        private Button nextButton;
        private Button profileButton;
        private Button muteButton;
        private Button overlayButton;
        private Button diagnosticsButton;
        private Button virtualJawButton;

        private string lastFeedback = string.Empty;
        private string lastHint = string.Empty;
        private float toastUntil;
        private bool sawLocked;
        private float hideLockedBannerAt = float.PositiveInfinity;
        private int layoutWidth;
        private int layoutHeight;
        private bool previewResolution;

        public bool DrawerOpen => drawer != null && drawer.gameObject.activeSelf;
        public bool DiagnosticsOpen => GetField<GameObject>("diagnosticsPanel")?.activeSelf == true;
        public bool HasPermanentBottomPanel => false;
        public bool EssentialControlsAvailable => questionLabel != null && repeatButton != null &&
                                                  hintButton != null && skipButton != null && nextButton != null;
        public bool DiagnosticsEntryVisible => DrawerOpen && diagnosticsButton != null &&
                                               diagnosticsButton.gameObject.activeInHierarchy;
        public JawQuizLearningModesController LearningModes => learningModes;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallForLoadedQuiz()
        {
            foreach (var quiz in FindObjectsByType<JawQuizSceneController>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
                if (quiz.GetComponent<JawQuizCompactPortraitUi>() == null)
                    quiz.gameObject.AddComponent<JawQuizCompactPortraitUi>();
        }

        private void Awake()
        {
            controller = GetComponent<JawQuizSceneController>();
            if (controller == null) return;
            controller.EnsureInterface();
            BuildCompactInterface();
        }

        private void LateUpdate()
        {
            if (hud == null) return;
            if (!previewResolution && (layoutWidth != Screen.width || layoutHeight != Screen.height))
                ApplyLayout(Screen.width, Screen.height);
            ReassertLegacyRowStyles();
            SynchronizeVisibleText();
            SynchronizeActionRow();
            UpdateTransientOverlays();
        }

        // Cheap scalar reassertion (no allocation, no sprite/material rebuild) against the
        // controller's own ApplyResponsiveLayout, which unconditionally resets every button's
        // Text.fontSize on resize/rotation.
        private void ReassertLegacyRowStyles()
        {
            foreach (var (label, color, fontSize) in legacyRowStyles)
            {
                if (label == null) continue;
                if (label.fontSize != fontSize) label.fontSize = fontSize;
                if (label.color != color) label.color = color;
            }
        }

        public void OpenDrawer()
        {
            if (drawer == null) return;
            controller.SetDiagnosticsVisible(false);
            drawerBackdrop.gameObject.SetActive(true);
            drawer.gameObject.SetActive(true);
            drawerBackdrop.SetAsLastSibling();
            drawer.SetAsLastSibling();
            StopAllCoroutines();
            StartCoroutine(JawMaterialMotion.SlideX(drawer, -drawer.rect.width, 0f, JawMaterialTheme.MotionMedium));
        }

        public void CloseDrawer()
        {
            if (drawer == null) return;
            drawer.gameObject.SetActive(false);
            drawerBackdrop.gameObject.SetActive(false);
        }

        public void CloseDrawerFromBackdrop() => CloseDrawer();

        public void OpenDiagnosticsFromDrawer()
        {
            CloseDrawer();
            controller.SetDiagnosticsVisible(true);
        }

        public void SetPreviewResolution(int width, int height)
        {
            previewResolution = true;
            ApplyLayout(width, height);
        }

        public void RefreshPreviewNow()
        {
            SynchronizeVisibleText();
            SynchronizeActionRow();
            UpdateTransientOverlays();
            Canvas.ForceUpdateCanvases();
        }

        public void ShowFeedbackPreview(string message, Color color)
        {
            ShowSnackbar(message, color);
            toastUntil = float.PositiveInfinity;
        }

        private void BuildCompactInterface()
        {
            var canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null) return;
            safeArea = FindRect(canvas.transform, "Safe Area") ?? canvas.GetComponent<RectTransform>();

            questionSource = GetField<Text>("questionText");
            attemptSource = GetField<Text>("attemptText");
            hintSource = GetField<Text>("hintText");
            feedbackSource = GetField<Text>("feedbackText");
            personalizedSource = GetField<Text>("personalizedText");
            stateSource = GetField<Text>("stateText");
            trackingPanel = GetField<RectTransform>("trackingStatusPanel");
            startButton = GetField<Button>("startButton");
            repeatButton = GetField<Button>("repeatButton");
            hintButton = GetField<Button>("hintButton");
            skipButton = GetField<Button>("skipButton");
            nextButton = GetField<Button>("nextButton");
            profileButton = GetField<Button>("profileButton");
            muteButton = GetField<Button>("muteButton");
            overlayButton = GetField<Button>("overlayButton");
            diagnosticsButton = GetField<Button>("diagnosticsButton");
            virtualJawButton = FindButton(GetField<GameObject>("diagnosticsPanel")?.transform, "Hide Virtual Jaw Button");

            DisableLegacyPanel("topBar");
            DisableLegacyPanel("quizCard");
            DisableLegacyPanel("controlsPanel");

            BuildHud();
            BuildActionRow();
            BuildSnackbar();
            BuildDrawer();
            SkinTrackingPanel();

            CloseDrawer();
            controller.SetDiagnosticsVisible(false);
            ApplyLayout(Screen.width, Screen.height);
            SynchronizeVisibleText();
            SynchronizeActionRow();
            learningModes = GetComponent<JawQuizLearningModesController>() ??
                            gameObject.AddComponent<JawQuizLearningModesController>();
            learningModes.Initialize(controller, this, safeArea, hud);
        }

        private void BuildHud()
        {
            hud = JawMaterialWidgets.Card(safeArea, "Compact Quiz HUD", JawMaterialTheme.Surface, JawMaterialTheme.RadiusLarge);
            var outline = hud.gameObject.AddComponent<Outline>();
            outline.effectColor = JawMaterialTheme.OutlineFaint;
            outline.effectDistance = new Vector2(1f, -1f);

            JawMaterialWidgets.NewButton(hud, "Hamburger Button", JawButtonStyle.IconOnly, string.Empty,
                JawMaterialIcons.Menu, OpenDrawer);

            progressChipLabel = JawMaterialWidgets.Chip(hud, "— / 23", null, JawMaterialTheme.Primary,
                JawMaterialTheme.OnPrimary).label;
            attemptChipLabel = JawMaterialWidgets.Chip(hud, "Attempt — / 3", null,
                new Color(JawMaterialTheme.Tertiary.r, JawMaterialTheme.Tertiary.g, JawMaterialTheme.Tertiary.b, 0.22f),
                JawMaterialTheme.Tertiary).label;

            progressTrack = JawMaterialWidgets.Card(hud, "Progress Track", JawMaterialTheme.OutlineFaint, 3f);
            progressFill = JawMaterialWidgets.Card(progressTrack, "Progress Fill", JawMaterialTheme.Primary, 3f);
            progressFill.anchorMin = new Vector2(0f, 0f);
            progressFill.anchorMax = new Vector2(0f, 1f);
            progressFill.offsetMin = Vector2.zero;
            progressFill.offsetMax = Vector2.zero;

            questionLabel = JawMaterialWidgets.Label(hud, "Press Start Quiz when you are ready.",
                JawMaterialTheme.TypeQuestionSize, JawMaterialTheme.FontMedium, JawMaterialTheme.OnSurface,
                TextAlignmentOptions.MidlineLeft);
        }

        private void BuildActionRow()
        {
            // These four are controller-owned Buttons whose labels the controller never rewrites
            // dynamically, but whose Text CHILD and font size the controller's own responsive
            // layout still touches on every resize/rotation (ApplyResponsiveLayout -> SetButtonLayout
            // calls button.GetComponentInChildren<Text>().fontSize unconditionally for every button
            // it owns). Destroying that Text component would NullReferenceException the controller
            // the next time the screen size changes, so — like every other controller-owned button
            // — these are skinned in place, never rebuilt from scratch.
            if (repeatButton != null)
                TrackRow(JawMaterialWidgets.SkinLegacyRow(repeatButton, JawButtonStyle.Outlined, JawMaterialIcons.Repeat,
                    asSwitch: false, TextAnchor.MiddleCenter));
            if (hintButton != null)
                TrackRow(JawMaterialWidgets.SkinLegacyRow(hintButton, JawButtonStyle.Tonal, JawMaterialIcons.Lightbulb,
                    asSwitch: false, TextAnchor.MiddleCenter));
            if (skipButton != null)
                TrackRow(JawMaterialWidgets.SkinLegacyRow(skipButton, JawButtonStyle.Text, null,
                    asSwitch: false, TextAnchor.MiddleCenter));
            if (nextButton != null)
                TrackRow(JawMaterialWidgets.SkinLegacyRow(nextButton, JawButtonStyle.Filled, JawMaterialIcons.ArrowForward,
                    asSwitch: false, TextAnchor.MiddleCenter));
            if (startButton != null)
                TrackRow(JawMaterialWidgets.SkinLegacyRow(startButton, JawButtonStyle.Filled, null,
                    asSwitch: false, TextAnchor.MiddleCenter));

            repeatButton?.transform.SetParent(hud, false);
            hintButton?.transform.SetParent(hud, false);
            skipButton?.transform.SetParent(hud, false);
            nextButton?.transform.SetParent(hud, false);
            startButton?.transform.SetParent(hud, false);
        }

        private void TrackRow(JawLegacyRowSkin skin)
        {
            if (skin.Label != null) legacyRowStyles.Add((skin.Label, skin.LabelColor, skin.LabelFontSize));
        }

        private void BuildSnackbar()
        {
            snackbar = JawMaterialWidgets.Snackbar(safeArea);
            snackbar.Group.alpha = 0f;
            snackbar.Root.gameObject.SetActive(false);
        }

        private void BuildDrawer()
        {
            drawerBackdrop = JawMaterialWidgets.Card(safeArea, "Compact Drawer Backdrop", JawMaterialTheme.Scrim, 0f);
            var backdropButton = drawerBackdrop.gameObject.AddComponent<Button>();
            backdropButton.transition = Selectable.Transition.None;
            backdropButton.onClick.AddListener(CloseDrawerFromBackdrop);

            drawer = JawMaterialWidgets.Card(safeArea, "Quiz Menu Drawer", JawMaterialTheme.SurfaceContainer, JawMaterialTheme.RadiusLarge);

            JawMaterialWidgets.Label(drawer, "Jaw Landmark Quiz", JawMaterialTheme.TypeQuestionSize - 4,
                JawMaterialTheme.FontBold, JawMaterialTheme.OnSurface, TextAlignmentOptions.MidlineLeft);
            JawMaterialWidgets.NewButton(drawer, "Close Drawer Button", JawButtonStyle.IconOnly, string.Empty,
                JawMaterialIcons.Close, CloseDrawer);

            drawerViewport = JawMaterialWidgets.Card(drawer, "Menu Scroll Viewport", Color.clear, 0f);
            drawerViewport.gameObject.AddComponent<RectMask2D>();
            drawerContent = JawMaterialWidgets.Card(drawerViewport, "Menu Scroll Content", Color.clear, 0f);
            var scroll = drawerViewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = drawerViewport;
            scroll.content = drawerContent;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 44f;

            AddDrawerHeading("LEARNING MODE");
            var modesRow = JawMaterialWidgets.NewButton(drawerContent, "Back to Mode Selection Row",
                JawButtonStyle.Outlined, "Back to Mode Selection", JawMaterialIcons.ArrowForward,
                () => learningModes?.ReturnToModeSelection());
            modesDrawerLabel = modesRow.skin.Label;
            drawerRows.Add(modesRow.skin.Root);
            var overlayModeRow = JawMaterialWidgets.NewButton(drawerContent, "Painted Region Overlay Row",
                JawButtonStyle.Outlined, "Painted Region Overlay: Hidden", JawMaterialIcons.Visibility,
                () => learningModes?.CycleOverlaySetting());
            overlayModeDrawerLabel = overlayModeRow.skin.Label;
            drawerRows.Add(overlayModeRow.skin.Root);

            AddDrawerHeading("STUDENT");
            if (profileButton != null)
            {
                TrackRow(JawMaterialWidgets.SkinLegacyRow(profileButton, JawButtonStyle.Outlined, JawMaterialIcons.Person, asSwitch: false));
                AddDrawerRow(profileButton);
            }
            if (muteButton != null)
            {
                var skin = JawMaterialWidgets.SkinLegacyRow(muteButton, JawButtonStyle.Outlined, JawMaterialIcons.VolumeUp, asSwitch: true);
                TrackRow(skin);
                muteSwitch = skin.Switch;
                AddDrawerRow(muteButton);
            }

            AddDrawerHeading("CONNECTION & LEARNING");
            var statusChip = JawMaterialWidgets.Chip(drawerContent, "Local status", JawMaterialIcons.Wifi,
                JawMaterialTheme.SurfaceElevated, JawMaterialTheme.OnSurfaceVariant);
            drawerStatusChipLabel = statusChip.label;
            drawerStatusChipBg = statusChip.background;
            drawerRows.Add(statusChip.root);
            drawerPersonalizedLabel = JawMaterialWidgets.Label(drawerContent, "Personalized explanation status",
                JawMaterialTheme.TypeSupportingSize, JawMaterialTheme.FontRegular, JawMaterialTheme.OnSurfaceVariant,
                TextAlignmentOptions.TopLeft);
            drawerRows.Add(drawerPersonalizedLabel.rectTransform);

            AddDrawerHeading("DISPLAY");
            if (overlayButton != null)
            {
                var skin = JawMaterialWidgets.SkinLegacyRow(overlayButton, JawButtonStyle.Outlined, JawMaterialIcons.Visibility, asSwitch: true);
                TrackRow(skin);
                overlaySwitch = skin.Switch;
                AddDrawerRow(overlayButton);
            }
            if (virtualJawButton != null)
            {
                var skin = JawMaterialWidgets.SkinLegacyRow(virtualJawButton, JawButtonStyle.Outlined, JawMaterialIcons.Visibility, asSwitch: true);
                TrackRow(skin);
                virtualJawSwitch = skin.Switch;
                AddDrawerRow(virtualJawButton);
            }

            AddDrawerHeading("ADVANCED");
            if (diagnosticsButton != null)
            {
                TrackRow(JawMaterialWidgets.SkinLegacyRow(diagnosticsButton, JawButtonStyle.Outlined, JawMaterialIcons.Build, asSwitch: false));
                AddDrawerRow(diagnosticsButton);
                diagnosticsButton.onClick.RemoveListener(controller.ToggleDiagnostics);
                diagnosticsButton.onClick.AddListener(OpenDiagnosticsFromDrawer);
            }
        }

        private void SkinTrackingPanel()
        {
            if (trackingPanel == null) return;
            trackingPanelBg = trackingPanel.GetComponent<Image>();
            if (trackingPanelBg == null) trackingPanelBg = trackingPanel.gameObject.AddComponent<Image>();
            trackingPanelBg.sprite = JawMaterialSprites.RoundedRect(JawMaterialTheme.RadiusPill);
            trackingPanelBg.type = Image.Type.Sliced;
            var text = trackingPanel.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
            }
        }

        private void DisableLegacyPanel(string fieldName)
        {
            var panel = GetField<RectTransform>(fieldName);
            if (panel != null) panel.gameObject.SetActive(false);
        }

        private void AddDrawerHeading(string value)
        {
            var label = JawMaterialWidgets.Label(drawerContent, value, JawMaterialTheme.TypeDrawerSectionTitleSize,
                JawMaterialTheme.FontBold, JawMaterialTheme.Primary, TextAlignmentOptions.MidlineLeft);
            drawerRows.Add(label.rectTransform);
        }

        private void AddDrawerRow(Button button)
        {
            if (button == null) return;
            button.transform.SetParent(drawerContent, false);
            drawerRows.Add(button.GetComponent<RectTransform>());
        }

        private void SynchronizeVisibleText()
        {
            if (questionSource != null) questionLabel.text = questionSource.text;
            ParseProgress(attemptSource != null ? attemptSource.text : string.Empty);
            if (drawerStatusChipLabel != null && stateSource != null) SynchronizeStatusChip(stateSource.text);
            if (drawerPersonalizedLabel != null && personalizedSource != null)
                drawerPersonalizedLabel.text = personalizedSource.text;

            JawMaterialWidgets.SetSwitchState(muteSwitch, ParseOn(GetField<Text>("muteButtonText")?.text, "Unmute"));
            JawMaterialWidgets.SetSwitchState(overlaySwitch, ParseOn(GetField<Text>("overlayButtonText")?.text, "Hide"));
            JawMaterialWidgets.SetSwitchState(virtualJawSwitch, ParseOn(GetField<Text>("virtualJawButtonText")?.text, "Hide"));
            if (learningModes != null)
            {
                if (modesDrawerLabel != null)
                    modesDrawerLabel.text = learningModes.CurrentMode == JawQuizLearningMode.ModeSelection
                        ? "Choose Learning Mode"
                        : "Back to Modes • " + ModeName(learningModes.CurrentMode);
                if (overlayModeDrawerLabel != null)
                    overlayModeDrawerLabel.text = learningModes.OverlaySettingLabel();
            }

            var feedback = feedbackSource != null ? feedbackSource.text : string.Empty;
            if (feedback != lastFeedback)
            {
                lastFeedback = feedback;
                if (ShouldToast(feedback)) ShowSnackbar(feedback, feedbackSource.color);
            }
            var hint = hintSource != null ? hintSource.text : string.Empty;
            if (hint != lastHint)
            {
                lastHint = hint;
                if (ShouldToastHint(hint)) ShowSnackbar(hint, JawMaterialTheme.Warning);
            }
        }

        // A row's live label starts with the "on" word when the toggle is in its active state
        // (e.g. muteButtonText reads "Unmute" while muted, "Mute" while not; overlay/virtualJaw
        // text reads "Hide ..." while currently shown, "Show ..." while hidden).
        private static bool ParseOn(string label, string onPrefix) =>
            !string.IsNullOrEmpty(label) && label.StartsWith(onPrefix, StringComparison.OrdinalIgnoreCase);

        private static string ModeName(JawQuizLearningMode mode) => mode switch
        {
            JawQuizLearningMode.FindIt => "Find It",
            JawQuizLearningMode.WhatIsThis => "What Is This?",
            JawQuizLearningMode.TwoPlayerChallenge => "Two-Player Challenge",
            _ => "Modes"
        };

        private void SynchronizeStatusChip(string state)
        {
            drawerStatusChipLabel.text = state;
            var upper = state.ToUpperInvariant();
            if (upper.Contains("OFFLINE"))
                drawerStatusChipBg.color = new Color(JawMaterialTheme.Warning.r, JawMaterialTheme.Warning.g, JawMaterialTheme.Warning.b, 0.22f);
            else if (upper.Contains("SYNCING") || upper.Contains("QUEUED"))
                drawerStatusChipBg.color = new Color(JawMaterialTheme.Info.r, JawMaterialTheme.Info.g, JawMaterialTheme.Info.b, 0.22f);
            else
                drawerStatusChipBg.color = new Color(JawMaterialTheme.Success.r, JawMaterialTheme.Success.g, JawMaterialTheme.Success.b, 0.20f);
        }

        private void SynchronizeActionRow()
        {
            if (startButton == null) return;
            var showStart = startButton.gameObject.activeSelf;
            if (showStart)
            {
                SetRect(startButton.GetComponent<RectTransform>(), new Vector2(0.02f, 0.04f), new Vector2(0.98f, 0.31f));
                repeatButton.gameObject.SetActive(false);
                hintButton.gameObject.SetActive(false);
                skipButton.gameObject.SetActive(false);
                nextButton.gameObject.SetActive(false);
                return;
            }

            repeatButton.gameObject.SetActive(true);
            hintButton.gameObject.SetActive(true);
            skipButton.gameObject.SetActive(true);
            var showNext = nextButton.interactable;
            nextButton.gameObject.SetActive(showNext);
            var count = showNext ? 4 : 3;
            LayoutAction(repeatButton, 0, count);
            LayoutAction(hintButton, 1, count);
            LayoutAction(skipButton, 2, count);
            if (showNext) LayoutAction(nextButton, 3, count);
        }

        private static void LayoutAction(Button button, int index, int count)
        {
            const float gap = 0.012f;
            var width = (0.96f - gap * (count - 1)) / count;
            var min = 0.02f + index * (width + gap);
            SetRect(button.GetComponent<RectTransform>(), new Vector2(min, 0.04f), new Vector2(min + width, 0.31f));
        }

        private void UpdateTransientOverlays()
        {
            if (snackbar.Root != null && snackbar.Root.gameObject.activeSelf && Time.unscaledTime >= toastUntil)
            {
                StopAllCoroutines();
                StartCoroutine(HideSnackbarRoutine());
            }

            var locked = controller.jawTracker != null && controller.jawTracker.WorldPoseLocked;
            if (locked && !sawLocked)
            {
                sawLocked = true;
                hideLockedBannerAt = Time.unscaledTime + LockedBannerSeconds;
            }
            else if (!locked)
            {
                sawLocked = false;
                hideLockedBannerAt = float.PositiveInfinity;
            }
            if (trackingPanel != null)
            {
                var visible = !locked || Time.unscaledTime < hideLockedBannerAt;
                trackingPanel.gameObject.SetActive(visible);
                if (visible) ApplyTrackingTone();
            }
        }

        private void ApplyTrackingTone()
        {
            if (trackingPanelBg == null) return;
            var text = trackingPanel.GetComponentInChildren<Text>(true)?.text ?? string.Empty;
            var upper = text.ToUpperInvariant();
            Color tone;
            if (upper.Contains("UNSTABLE") || upper.Contains("LOST") || upper.Contains("FAILED") ||
                upper.Contains("UNAVAILABLE") || upper.Contains("INTERRUPTED"))
                tone = JawMaterialTheme.Warning;
            else if (upper.Contains("LOCKED"))
                tone = JawMaterialTheme.Success;
            else
                tone = JawMaterialTheme.Info;
            trackingPanelBg.color = new Color(tone.r, tone.g, tone.b, 0.85f);
        }

        private void ShowSnackbar(string message, Color accent)
        {
            if (snackbar.Root == null || string.IsNullOrWhiteSpace(message)) return;
            snackbar.Label.text = message;
            // The icon glyph itself always stays white so it reads clearly against any accent
            // circle; only the circle's fill and the specific glyph change with the feedback kind
            // so correctness is never communicated by color alone.
            snackbar.IconImage.sprite = JawMaterialIcons.Get(ClassifyFeedbackIcon(accent));
            snackbar.IconImage.color = Color.white;
            snackbar.IconImage.transform.parent.GetComponent<Image>().color = accent;
            snackbar.Root.gameObject.SetActive(true);
            snackbar.Root.SetAsLastSibling();
            StopAllCoroutines();
            StartCoroutine(JawMaterialMotion.FadeTo(snackbar.Group, 1f, JawMaterialTheme.MotionFast));
            toastUntil = Time.unscaledTime + ToastSeconds;
        }

        // The controller sets feedbackText.color to one of a small fixed palette (its own Red /
        // Green / Amber / Cyan constants); classify by nearest match rather than duplicating those
        // literals here, so the right icon (not just a color) always pairs with the message.
        private static string ClassifyFeedbackIcon(Color accent)
        {
            var candidates = new (Color color, string icon)[]
            {
                (JawMaterialTheme.Success, JawMaterialIcons.CheckCircle),
                (JawMaterialTheme.Error, JawMaterialIcons.Error),
                (JawMaterialTheme.Warning, JawMaterialIcons.Warning),
                (JawMaterialTheme.Info, JawMaterialIcons.Visibility),
            };
            var best = candidates[0];
            var bestDist = float.MaxValue;
            foreach (var candidate in candidates)
            {
                var d = SqrDistance(candidate.color, accent);
                if (d < bestDist) { bestDist = d; best = candidate; }
            }
            return best.icon;
        }

        private static float SqrDistance(Color a, Color b)
        {
            var dr = a.r - b.r;
            var dg = a.g - b.g;
            var db = a.b - b.b;
            return dr * dr + dg * dg + db * db;
        }

        private System.Collections.IEnumerator HideSnackbarRoutine()
        {
            yield return JawMaterialMotion.FadeTo(snackbar.Group, 0f, JawMaterialTheme.MotionFast);
            snackbar.Root.gameObject.SetActive(false);
        }

        private void ParseProgress(string value)
        {
            progressChipLabel.text = "— / 23";
            attemptChipLabel.text = "Attempt — / 3";
            SetProgressFraction(0f);
            if (string.IsNullOrEmpty(value) || !value.StartsWith("Question ", StringComparison.Ordinal)) return;
            var parts = value.Split(new[] { '•' }, 2);
            var progressText = parts[0].Replace("Question ", string.Empty).Trim();
            progressChipLabel.text = progressText.Replace("/", " / ");
            if (parts.Length > 1) attemptChipLabel.text = parts[1].Trim().Replace("/", " / ");

            var nums = progressText.Split('/');
            if (nums.Length == 2 && int.TryParse(nums[0].Trim(), out var cur) && int.TryParse(nums[1].Trim(), out var total) && total > 0)
                SetProgressFraction(Mathf.Clamp01((float)cur / total));
        }

        private void SetProgressFraction(float fraction)
        {
            if (progressFill == null) return;
            progressFill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
        }

        private void ApplyLayout(int width, int height)
        {
            layoutWidth = Mathf.Max(1, width);
            layoutHeight = Mathf.Max(1, height);
            var portrait = layoutHeight >= layoutWidth;
            SetRect(hud, new Vector2(0.015f, portrait ? 0.842f : 0.69f), new Vector2(0.985f, 0.995f));
            SetRect(FindRect(hud, "Hamburger Button"), new Vector2(0.018f, 0.70f), new Vector2(0.13f, 0.97f));
            SetRect(progressChipLabel.rectTransform.parent as RectTransform, new Vector2(0.15f, 0.72f), new Vector2(0.42f, 0.97f));
            SetRect(attemptChipLabel.rectTransform.parent as RectTransform, new Vector2(0.44f, 0.72f), new Vector2(0.72f, 0.97f));
            SetRect(progressTrack, new Vector2(0.15f, 0.655f), new Vector2(0.98f, 0.685f));
            SetRect(questionLabel.rectTransform, new Vector2(0.025f, 0.33f), new Vector2(0.975f, 0.65f));
            questionLabel.fontSize = portrait ? JawMaterialTheme.TypeQuestionSize : JawMaterialTheme.TypeQuestionSize - 5;

            if (snackbar.Root != null)
            {
                SetRect(snackbar.Root, new Vector2(0.04f, portrait ? 0.72f : 0.56f), new Vector2(0.96f, portrait ? 0.815f : 0.67f));
            }
            if (trackingPanel != null)
                SetRect(trackingPanel, new Vector2(0.08f, portrait ? 0.785f : 0.50f), new Vector2(0.92f, portrait ? 0.835f : 0.61f));

            SetRect(drawerBackdrop, Vector2.zero, Vector2.one);
            SetRect(drawer, Vector2.zero, new Vector2(portrait ? 0.84f : 0.58f, 1f));
            var title = drawer.GetChild(0) as RectTransform;
            SetRect(title, new Vector2(0.05f, 0.91f), new Vector2(0.76f, 0.985f));
            SetRect(FindRect(drawer, "Close Drawer Button"), new Vector2(0.80f, 0.915f), new Vector2(0.96f, 0.982f));
            SetRect(drawerViewport, new Vector2(0.04f, 0.035f), new Vector2(0.96f, 0.90f));
            LayoutDrawerRows(portrait ? 94f : 82f);
        }

        private void LayoutDrawerRows(float buttonHeight)
        {
            var y = -8f;
            foreach (var row in drawerRows)
            {
                var isButton = row.GetComponent<Button>() != null;
                var isChip = row.GetComponent<Image>() != null && !isButton &&
                             row != drawerPersonalizedLabel?.rectTransform;
                var height = isButton ? buttonHeight : isChip ? 56f :
                    (row == drawerPersonalizedLabel?.rectTransform ? 78f : 44f);
                row.anchorMin = new Vector2(0f, 1f);
                row.anchorMax = new Vector2(1f, 1f);
                row.pivot = new Vector2(0.5f, 1f);
                row.anchoredPosition = new Vector2(0f, y);
                row.sizeDelta = new Vector2(0f, height);
                y -= height + 12f;
            }
            drawerContent.anchorMin = new Vector2(0f, 1f);
            drawerContent.anchorMax = new Vector2(1f, 1f);
            drawerContent.pivot = new Vector2(0.5f, 1f);
            drawerContent.anchoredPosition = Vector2.zero;
            drawerContent.sizeDelta = new Vector2(0f, -y + 8f);
        }

        private static bool ShouldToast(string text) => !string.IsNullOrWhiteSpace(text) &&
            !text.StartsWith("Tap the corresponding", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("Point to the requested region", StringComparison.OrdinalIgnoreCase);

        private static bool ShouldToastHint(string text) => !string.IsNullOrWhiteSpace(text) &&
            !text.StartsWith("Hint is optional", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("Questions are graded", StringComparison.OrdinalIgnoreCase);

        private T GetField<T>(string name) where T : class
        {
            return typeof(JawQuizSceneController).GetField(name, PrivateInstance)?.GetValue(controller) as T;
        }

        private static Button FindButton(Transform root, string name)
        {
            return FindRect(root, name)?.GetComponent<Button>();
        }

        private static RectTransform FindRect(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root as RectTransform;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindRect(root.GetChild(i), name);
                if (found != null) return found;
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
