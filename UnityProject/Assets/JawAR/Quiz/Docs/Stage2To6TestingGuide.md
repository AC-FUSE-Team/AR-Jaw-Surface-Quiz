# Jaw Surface Quiz: Editor Testing Guide (Stages 2–6)

This experimental workflow uses the saved painted triangles in
`JawSurfaceRegionMap_CodexDraft.asset`. It does not use Backboard, networking, local attempt
storage, Android text-to-speech, or an Android build.

## Open the project and quiz scene

In Konsole:

```bash
cd /home/omar/UnityProjects/BMC
/home/omar/Unity/Hub/Editor/6000.4.6f1/Editor/Unity -projectPath /home/omar/UnityProjects/BMC
```

In Unity:

1. In the Project window, open `Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity`.
2. Open the **Game** tab.
3. Choose a landscape aspect such as **2220 × 1080** or approximately **18.5:9**.
4. Press **Play**. No AR camera or phone is required for the diagnostic buttons.

## Start and simulate the quiz

1. Press **Start Quiz**.
2. Read the large prompt. The first starter question is **Lower Incisors**.
3. Press **Developer Diagnostics**.
4. Every row has two separate controls:
   - Press the anatomical name to highlight only that painted region.
   - Press **Simulate** to submit that stable region ID to the deterministic quiz grader.
5. Scroll the list vertically to reach all 23 saved regions.

## Confirm incorrect and correct feedback

For the first **Lower Incisors** question:

1. Press **Simulate** beside **Left Ramus**. Red incorrect feedback should appear and name the
   selected region. After a short delay, the same question accepts another attempt.
2. Press **Simulate** beside **Lower Incisors**. Green correct feedback should appear and show
   both selected and expected anatomy names.
3. Press **Next** to continue.

An unlabelled hit can be tested with **Simulate Unlabelled**. It does not consume an attempt.

## Show, hide, and inspect painted regions

- Painted colours are visible by default.
- Press **Hide Painted Regions** to hide only the coloured renderer overlays. Triangle lookup
  and simulation remain enabled.
- The button changes to **Show Painted Regions**; press it to restore the colours.
- In Developer Diagnostics, press an anatomical name to show only that region.
- Press **Show All Painted Regions** to return to the complete colour overlay.

## Other controls

- **Repeat** restores the current prompt on screen. Android speech is intentionally not included
  in these stages.
- **Hint** shows deterministic hint level one and then level two.
- **Skip** completes the current question without grading a selection.
- **Next** advances after a correct answer, the maximum attempt count, or Skip.

## Run the isolated EditMode tests

Close the interactive Unity Editor first, then run:

```bash
cd /home/omar/UnityProjects/BMC
/home/omar/Unity/Hub/Editor/6000.4.6f1/Editor/Unity \
  -batchmode -nographics \
  -projectPath /home/omar/UnityProjects/BMC \
  -runTests -testPlatform EditMode \
  -testFilter BMC.JawAR.Quiz.Tests \
  -testResults /tmp/jaw_quiz_editmode_results.xml \
  -logFile /tmp/jaw_quiz_editmode_tests.log
```

This command runs Editor tests only. It does not build or install an APK.
