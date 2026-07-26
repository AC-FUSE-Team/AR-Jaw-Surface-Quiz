# Jaw Surface Quiz proxy

The proxy is local-first. It stores deterministic Unity attempt results in SQLite and runs in mock
mode whenever `BACKBOARD_MOCK=1` or no `BACKBOARD_API_KEY` is present. It never changes grading.

See `../../UnityProject/Assets/JawAR/Quiz/Docs/BackboardMockTestingGuide.md` for exact beginner commands.
