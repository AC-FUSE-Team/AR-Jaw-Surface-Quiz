# AR Jaw Surface Quiz

An augmented-reality anatomy learning tool for AC-FUSE-Team's hackathon submission. A virtual
mandible (jaw) overlay is registered onto a physical 3D-printed jaw/plaque using an ArUco marker,
and players learn jaw anatomy through three modes: **Find It**, **What Is This?**, and
**Two-Player Challenge**.

Released version: **v37 "Post-Lock Drift Correction"** (`versionCode 37`,
`1.8.0-post-lock-drift-correction`). See [`VERSION_SELECTION.md`](VERSION_SELECTION.md) for why
this build was selected and what evidence backs it, including physical device confirmation.

## What's in this repository

| Folder | Contents |
|---|---|
| `APK/` | The ready-to-install production APK (`JawSurfaceQuiz_v37_PostLockDriftCorrection.apk`). |
| `UnityProject/` | Full Unity source for the quiz: scenes, scripts, tracking, surface-region data, AR/XR settings, and native Android plugins needed to build it. |
| `Backend/BackboardQuizProxy/` | The local Python (FastAPI) service the app talks to for adaptive question generation and attempt logging. |
| `Printable/` | The 3D-printable jaw + ArUco marker board STL files, print guide, and CAD metadata. |
| `VERSION_SELECTION.md` | Evidence trail for why v37 was chosen as the release build. |

## Running the game

1. **Print the physical jaw + marker board** — see `Printable/HumanSkull_Jaw_ArUco_printing_guide.md`.
2. **Start the backend proxy** (on a laptop on the same Wi-Fi as the phone):
   ```
   cd Backend/BackboardQuizProxy
   pip install -r requirements.txt
   python run_proxy.py
   ```
   By default it binds to `127.0.0.1:8765`. To let a phone on the same LAN reach it, run with
   `--lan --host <your-laptop's-private-IPv4>` and set a `QUIZ_PROXY_TOKEN` environment variable
   first (see `Backend/BackboardQuizProxy/README.md`).
3. **Install `APK/JawSurfaceQuiz_v37_PostLockDriftCorrection.apk`** on an Android phone (ARCore-capable,
   `minSdkVersion 25`, arm64-v8a).
4. **On first launch**, the app shows a proxy-URL text field — type in `http://<laptop-IP>:8765`
   (the value used during development, `192.168.2.244`, is specific to that network and will not
   work elsewhere; this field is exactly how you point the app at a new laptop/network without any
   rebuild).
5. Point the camera at the printed ArUco marker to lock the virtual jaw onto the physical one, then
   pick a mode.

To open/modify the game itself: open `UnityProject/` in Unity 6000.4.6f1 (AR Foundation/ARCore
6.5). `Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity` is the quiz scene.

## Disclosures

- **The painted anatomical regions are prototype annotations.** They were authored for this
  hackathon prototype and were **not created or clinically validated using authoritative anatomical
  references**. Do not treat region boundaries as medically/anatomically authoritative.
- **This project was developed with substantial AI assistance from Claude and Codex** (AI coding
  assistants), used throughout implementation, debugging, and testing.

## Licensing

- **Code** (Unity C#, Editor tooling, backend Python service) is licensed under the [MIT
  License](LICENSE) — see that file for the full text.
- **The 3D-printable jaw/marker model** in `Printable/` is a derivative of a third-party model and
  is licensed **CC BY-SA 4.0**, with attribution required — see
  [`Printable/ATTRIBUTION_AND_LICENSE.md`](Printable/ATTRIBUTION_AND_LICENSE.md) before
  redistributing or modifying those files.
