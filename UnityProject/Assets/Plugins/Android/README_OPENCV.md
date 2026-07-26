# OpenCV Android AAR — not included in this repo

`opencv-4.12.0.aar` is required in this folder for the project to build, but it is **not
committed here** — at 112 MB it exceeds GitHub's 100 MB per-file limit, and it's a third-party
vendor library rather than project source.

To build the project:

1. Download the **OpenCV Android SDK, version 4.12.0** from the official releases page:
   <https://opencv.org/releases/>
2. Take `opencv-4.12.0.aar` from the SDK's `sdk/` folder.
3. Place it directly in this folder (`UnityProject/Assets/Plugins/Android/`), alongside
   `JawArucoBridge.java`.

This is a standard AAR (Android Archive) plugin; Unity picks it up automatically once it's in
`Assets/Plugins/Android/`. No other configuration is required.
