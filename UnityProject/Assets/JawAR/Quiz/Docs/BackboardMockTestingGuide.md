# Backboard Quiz Mock Testing Guide

This phase is intentionally mock-only. It does not build Android and does not call Backboard.

## One-time laptop setup

Open Konsole and run:

```bash
cd /home/omar/UnityProjects/BMC
python3 -m venv Tools/BackboardQuizProxy/.venv
Tools/BackboardQuizProxy/.venv/bin/pip install -r Tools/BackboardQuizProxy/requirements.txt
```

## Start a clean mock demonstration

These commands explicitly remove `BACKBOARD_API_KEY` from the child processes, even if it is set
in the shell:

```bash
cd /home/omar/UnityProjects/BMC
rm -f /tmp/jaw_quiz_mock.sqlite3
env -u BACKBOARD_API_KEY BACKBOARD_MOCK=1 QUIZ_PROXY_DB=/tmp/jaw_quiz_mock.sqlite3 \
  PYTHONPATH=Tools/BackboardQuizProxy \
  Tools/BackboardQuizProxy/.venv/bin/python Tools/BackboardQuizProxy/generate_mock_data.py
cd Tools/BackboardQuizProxy
env -u BACKBOARD_API_KEY BACKBOARD_MOCK=1 QUIZ_PROXY_DB=/tmp/jaw_quiz_mock.sqlite3 \
  PYTHONPATH=. .venv/bin/python run_proxy.py
```

Leave that Konsole window open. In a browser on the laptop, visit:

```text
http://127.0.0.1:8765/teacher
```

Expected results:

- the health badge says `OK • MOCK`;
- all student IDs are anonymous (`student_001`, etc.);
- the numeric tables are labelled calculated and come from SQLite;
- the mock AI section is visually separate;
- CSV and JSON exports contain the stored rows.

Stop the proxy with `Ctrl+C`.

## Run tests

```bash
cd /home/omar/UnityProjects/BMC
env -u BACKBOARD_API_KEY BACKBOARD_MOCK=1 PYTHONPATH=Tools/BackboardQuizProxy \
  Tools/BackboardQuizProxy/.venv/bin/pytest -q Tools/BackboardQuizProxy/tests
```

Run Unity EditMode tests (Unity must be closed):

```bash
cd /home/omar/UnityProjects/BMC
/home/omar/Unity/Hub/Editor/6000.4.6f1/Editor/Unity \
  -batchmode -nographics -projectPath /home/omar/UnityProjects/BMC \
  -runTests -testPlatform EditMode \
  -testResults /tmp/jaw_backboard_editmode_results.xml \
  -logFile /tmp/jaw_backboard_editmode.log
```

## Local Unity persistence test for a beginner

1. Start the proxy in mock mode as above, or leave it stopped to test offline behavior.
2. Open Unity and load `Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity`.
3. Press Play and select an anonymous profile. Never enter a real name.
4. Start the quiz and use Developer Diagnostics to simulate a correct and incorrect selection.
5. Confirm the quiz responds immediately. Stopping the proxy must not freeze grading.
6. Exit Play mode, start again, and confirm the queued count is recovered.
7. Local authoritative files are under Unity's `Application.persistentDataPath`, inside
   `JawSurfaceQuizLearning/attempts.jsonl` and `attempt-sync.jsonl`.
8. Mute must stop speech; Repeat must speak/log the deterministic question again.

## Safe API-key storage (do not run real mode yet)

The owner-only file already created at `~/.config/backboard/backboard.env` is appropriate. It must
contain only an environment assignment, for example (do not paste the value into this project):

```bash
BACKBOARD_API_KEY='replace-with-the-key-in-your-private-file'
```

Keep permissions at `600`:

```bash
chmod 600 ~/.config/backboard/backboard.env
```

After explicit approval for one bounded real verification, a new Konsole can load it without
printing it:

```bash
set -a
source ~/.config/backboard/backboard.env
set +a
```

Do not run the real-mode proxy yet. Never use `cat`, `echo`, shell tracing (`set -x`), screenshots,
or command-history arguments to expose the key.

## Future phone/LAN plan (not executed in this phase)

1. Generate a separate local prototype token with `openssl rand -hex 24` and export it as
   `QUIZ_PROXY_TOKEN` in both the proxy process and a temporary phone-build scene copy.
2. Start the proxy with `run_proxy.py --lan`; the runner refuses LAN binding without the token.
3. Allow only the phone/laptop private Wi-Fi network in the firewall and configure explicit CORS
   origins. The Backboard key stays on the laptop.
4. Temporarily set the Unity proxy URL to the laptop's private Wi-Fi address in the isolated build
   scene, never in the student UI.
5. Re-check microphone permission absence, TTS-only behavior, v25 tracking configuration, and all
   Android settings; build a new filename/version without overwriting v25.
