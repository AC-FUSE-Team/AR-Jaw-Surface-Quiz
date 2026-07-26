# Jaw-only ArUco anatomy scene

## Ready-to-run assets

- Scene: `Assets/Scenes/JawArUcoAnatomy_AR.unity`
- Android build: `build/JawArUcoAnatomy.apk`, shipped copy at `/home/omar/JawRepair/JawArUcoAnatomy.apk`
- Menu rebuild: `Tools > Jaw AR > Build Jaw ArUco Anatomy Scene`
- Menu APK build: `Tools > Jaw AR > Build Android APK`
- Current version: **1.3.0** (versionCode 16)
- Full chronological history of every phone test and fix: `/home/omar/JawRepair/Jaw_Unity_ChatGPT_Summary.txt`

This scene contains the printed mandible/stand only. It does not instantiate the old full skeleton, bottle, or bottle proxy.

## Tracking

The physical marker is OpenCV `DICT_5X5_50`, ID `1`. The black square is CAD-verified at exactly `56 mm` wide — ground truth lives in `/home/omar/JawRepair/HumanSkull_Jaw_ArUco_Printable/ArUco_pose_metadata.json`, which also has the marker's exact corner coordinates in the original CAD design. `JawOpenCvArucoTracker` reads grayscale AR camera CPU frames, decodes ID 1 with OpenCV 4.12 (sub-pixel corner refinement enabled), and estimates the marker pose from calibrated AR camera intrinsics.

Automatic use:

1. Put the print on a stable surface under diffuse, even light.
2. Point the camera at the complete black square and its white border.
3. Begin roughly 30-60 cm away and avoid a grazing viewing angle.
4. **Hold the phone still.** The status text will show a live stability readout (position/rotation spread in mm/degrees) while it collects samples, then locks once several consecutive windows are stable. The jaw stays hidden until the lock is accepted, so nothing bad-looking flashes on screen.
5. Once locked, tap a jaw anatomy zone. The collider meshes stay hidden until a selected zone briefly flashes orange.

Manual fallback:

1. Wait for AR Foundation to detect the stand's flat supporting plane.
2. Tap the marker center.
3. Tap a second point on the stand in the direction of the jaw.

The manual mode uses the physical AR plane for position and normal and the two taps for jaw direction. It is less exact than automatic pose estimation but does not require marker decoding.

## Why locking used to be inconsistent (fixed in 1.1.3)

Earlier versions locked onto the marker after a fixed count of detections, averaged together with no check on whether those detections actually agreed with each other. If the phone moved even slightly during that ~1 second window, the average could land noticeably off — same build, same setup, but a good lock one launch and a bad one the next.

`JawOpenCvArucoTracker` now keeps a sliding window of recent detections and only accepts a lock once:
- the position spread across the window is under `maxPositionSpreadMeters` (3.5mm default),
- the rotation spread is under `maxRotationSpreadDegrees` (1.5° default),
- that stability holds for `stableWindowsRequired` consecutive windows (3 default),
- and no individual sample jumped too far from the running pose (`maxSampleDeviationMeters` / `maxSampleAngularDeviationDegrees`) or arrived after too long a gap (`maxSampleGapSeconds`) — either resets the window instead of quietly absorbing a bad reading.

If a lock still looks wrong after this, it's worth checking lighting and holding noticeably steadier during the "HOLD STILL" phase before assuming it's a code bug again.

## Calibration diagnostic (board overlay)

`Assets/JawAR/Models/JawArUcoBoardCalibration.obj` is a marker-aligned virtual replica of the *entire* printed board (not just the jaw), generated directly from the original CAD file (`HumanSkull_Jaw_ArUco_WHITE.stl`) with a plain Python STL parser — no Blender needed, see the conversion approach in `export_unity_marker_aligned_jaw.py` for the coordinate math it mirrors. It renders as a translucent orange overlay.

It's normally excluded from builds (`IncludeCalibrationBoardOverlay = false` in `JawArUcoSceneBuilder.cs`, ~30MB of extra geometry). If a future placement bug shows up, flip that flag to `true` and rebuild: a flat rectangle is far easier to judge alignment against than an organic jaw shape. If the orange board outline visibly doesn't match the real board, it's a real regression; if the board lines up fine and only the jaw looks slightly off, it's normal AR jitter, not a bug to chase.

## Editable anatomy hierarchy

All colliders are below:

`JawMarkerAlignedRoot/MarkerContent_Jawward/AnatomyHitboxes_EDITABLE`

Groups:

- `Masseter_Insertion` (two sides)
- `Temporalis_Insertion` (two sides)
- `Buccinator_Origin` (two sides)
- `Depressor_Anguli_Oris_Origin` (two sides)
- `Depressor_Labii_Inferioris_Origin` (two sides)
- `Mentalis_Origin/Midline`

The boxes were placed from the supplied screenshots and are intentionally marked `approximatePlacement`. They are editable cube children, so move/scale them in the Scene view after comparing them to the final physical print. Model-side `+X/-X` naming is provisional until anatomical left/right is confirmed on the print. The incorrect `Orbicularis_Oris` box was intentionally removed.

## Troubleshooting

- **Jaw appears far above/away from the real one:** this was the original bug (XR Origin's VR-only "standing eye height" offset, ~1.1m) — fixed in 1.0.7. If it recurs, check `origin.CameraYOffset` is still forced to `0f` in `CreateXROrigin()`.
- **Jaw floats slightly above the real one:** a 1.391mm CAD-verified correction is already applied (`Mandible` local Y offset in `InstantiateJawModel()`) — don't re-add another one on top of it.
- **Alignment is inconsistent between launches:** covered above — the 1.1.3 stability-window system should catch this; if it's still happening, try more even lighting and holding stiller during the "HOLD STILL" phase before assuming a new bug.
- **Overlay rotated 180°:** change `MarkerContent_Jawward` local Y rotation between `0` and `180`.
- **Overlay wrong scale:** measure only the black square (not the quiet zone) and update `blackSquareSizeMeters` — though this has been CAD-verified at 56mm and checked against a physical photo, so treat a scale problem as unlikely before assuming it's the cause.

## Fingertip pointer (1.2.0)

After the jaw locks, show one hand and keep the index fingernail visible to the camera. A yellow `+` follows MediaPipe hand landmark 8 (the index fingertip). Move the `+` over an anatomy region and hold for 0.65 seconds. It turns green and invokes the same anatomy flash/text feedback as a touch.

The detector runs on-device at up to 6 FPS using MediaPipe Tasks Vision 0.10.35 and the official float16 Hand Landmarker model. Frames are processed on a background Android thread and dropped while inference is busy. The pointer starts only after `WorldPoseLocked`, so ArUco acquisition is unchanged.

Important limitation: this first version uses the fingertip's 2D camera position to cast into the registered 3D hitboxes. The finger and target must both be visible. MediaPipe's hand-relative depth is not yet fused with ARCore scene depth, so overlapping objects along the same camera ray can still be ambiguous.

## Voice questions (1.3.0)

On first launch, allow microphone access while using the app. Voice listening begins automatically after the jaw world lock. Point at a region until it selects, then say **“What is that?”**. The app also accepts “What's that?”, “What is this?”, and “What's this?”. Android speech recognition supplies the phrase and Android TextToSpeech says “That is the [anatomy name].”

The controller answers the currently hovered region, or the last completed fingertip selection for up to 8 seconds. Speech recognition pauses during the spoken answer and then restarts, preventing the app from answering its own voice. Recognition availability and offline/network behavior depend on the speech service installed on the phone.
