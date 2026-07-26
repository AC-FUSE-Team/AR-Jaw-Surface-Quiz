# Version Selection Evidence

## Selected release: v37 — "Post-Lock Drift Correction"

| Field | Value |
|---|---|
| APK file | `JawSurfaceQuiz_v37_PostLockDriftCorrection.apk` |
| Package ID | `com.omar.jawsurfacequiztest` |
| Version name | `1.8.0-post-lock-drift-correction` |
| Version code | `37` |
| Build timestamp | 2026-07-25 04:45 (local file mtime; confirmed by build log) |
| SHA-256 | `54c3a238a9f6818cda411c4c8753d4937d9b8f643c4a1c8329518006a91954dd` |
| Size | 56,586,122 bytes |
| Source path (original project, untouched) | `/home/omar/UnityProjects/BMC` |
| Build script | `Assets/JawAR/Quiz/Editor/JawQuizV37PostLockDriftCorrectionAndroidBuild.cs` |

## Why v37, not v36 or v35

- Sorting every `JawSurfaceQuiz_*.apk` in `/home/omar/JawRepair` by modification time, v37 (Jul 25,
  04:45) is the newest **production** build — newer than v36 (Jul 25, 00:28) and v35 (Jul 24, 22:47).
- Files with later timestamps than v37 (`JawFullPlaqueCalibrationDiagnostic_v1/v2/v3`,
  `JawAlignmentDiag_*`) are diagnostics, excluded per the "do not select a diagnostic" rule. Confirmed
  by reading their own `FINAL_REPORT.md`, which explicitly labels them
  "Diagnostic-only build. No production app... modified" and
  "`UNVERIFIED_DIAGNOSTIC_CANDIDATE`".
- v37 was built from the exact same, unmodified Android/proxy build pipeline as v34/v35/v36
  (`JawQuizAndroidTestBuild.BuildInternal`, reused by reflection) — it did not fork the game logic.

## What v37 includes (carried forward, verified by artifact trail)

- **Three learning modes** (Find It, What Is This?, Two-Player Challenge) — shipped in v35,
  confirmed via `Artifacts/ThreeLearningModes_v35/FINAL_REPORT.md`, 10 phone-realistic screenshots,
  and `LearningModesResults.xml`.
- **Input usability fixes** — shipped in v36, confirmed via `Artifacts/InputUsabilityFix_v36/FINAL_REPORT.md`:
  fixed the background timer that silently killed input outside Find It, and the stale
  physical-selection-leaking-into-a-new-question bug. 121/121 EditMode tests passed. v37's build log
  and preservation hashes show none of these v36 files were reverted or altered before the v37 build.
- **Post-lock drift correction** (v37's own addition) — a fix to `JawOpenCvArucoTracker.cs` for
  "jaw drifts away from the print" after ARCore lock. Per
  `Artifacts/JawFullPlaqueCalibrationDiagnostic_v2_PostLockFix/preservation/APPROVED_CHANGE_NOTE.txt`,
  this was an explicitly user-approved change, made **after Omar physically confirmed on his Note 9**
  that the fix visibly reduces the drift symptom — tested via the isolated
  `JawFullPlaqueCalibrationDiagnostic_v3` diagnostic app, then carried into the real quiz unchanged
  (same default-on tracker fields). 147/147 EditMode tests passed 13 minutes before the v37 APK was
  built (`EditMode_SmartCorrection2.xml`, 04:32) — including new
  `JawOpenCvArucoTrackerPostLockCorrectionTests.cs` cases.
- **Portrait lock** — confirmed directly on the v37 APK via `aapt2 dump badging`:
  `uses-implied-feature: android.hardware.screen.portrait`, no landscape activity.
- **No microphone permission** — confirmed via `aapt2`: only `INTERNET`, `CAMERA`,
  `ACCESS_NETWORK_STATE`, and the standard `DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`. No
  `RECORD_AUDIO`.
- **arm64-v8a only**, `minSdkVersion=25`, `targetSdkVersion=36`, `com.google.ar.core.depth` and
  `android.hardware.camera.ar` required features present (AR Foundation/ARCore configured).

## Physical confirmation (closes the gap below)

Omar installed this exact APK (`JawSurfaceQuiz_v37_PostLockDriftCorrection.apk`, SHA-256 above) on
his Galaxy Note 9 and confirmed it works correctly. v37 is treated as the verified production
release candidate for the GitHub export on that basis.

### Original known gap — now closed

v37's own artifact folder (`Artifacts/QuizV37PostLockDriftCorrection/`) contains only preservation
hashes — no dedicated FINAL_REPORT or screenshot set was produced during development, unlike v35
and v36. The underlying drift-correction *code* had been physically validated via the isolated
diagnostic app, and v37 reuses a pipeline + input/mode logic already phone-verified through v36's
EditMode suite — but this exact v37 APK had not yet been installed and driven end-to-end on a
physical device. That gap is now closed by the confirmation above.

## Excluded from consideration (with reason)

| Build | Reason excluded |
|---|---|
| `JawFullPlaqueCalibrationDiagnostic_v1/v2/v3.apk` | Diagnostic, `UNVERIFIED_DIAGNOSTIC_CANDIDATE`, own FINAL_REPORT says so explicitly |
| `JawAlignmentDiag_Good_NoNetwork_v17/v18.apk` | Diagnostic (`com.omar.jawgoodalignmentdiag`), observation-only |
| `JawAlignmentDiag_Quiz_NoNetwork_v30/v31.apk` | Diagnostic (`com.omar.jawsurfacequizalignmentdiag`), observation-only |
| `JawSurfaceQuiz_BackboardProxy_v26...v34*.apk` | Superseded intermediate builds pre-dating the three-mode/input/drift fixes |
| `JawArUcoAnatomy*.apk`, `JawSurfaceQuiz_Portrait_*_Test.apk`, `*_WorkingTrackingParity_Test.apk`, `*_HighQualityLock_Test.apk` | Older experiment/test builds, superseded |
| `BMC_5x5SmokeTest.apk` | Unrelated marker smoke test, not the jaw game |
