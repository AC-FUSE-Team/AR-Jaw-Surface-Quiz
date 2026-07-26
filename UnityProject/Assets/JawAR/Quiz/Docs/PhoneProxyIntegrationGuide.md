# Jaw Surface Quiz phone-to-proxy integration

## Integration map

`JawQuizSceneController.OnSelection` remains the deterministic grading authority. It creates a
stable UUID attempt, writes and flushes JSONL through `JawQuizAttemptStore`, and immediately lets
the quiz continue. Pending records are submitted asynchronously by `JawQuizProxyClient`. The proxy
deduplicates each UUID while inserting the unchanged result into SQLite. Its deterministic
`DeterministicMemoryPolicy` may route one durable learning event to Backboard Auto; ordinary
attempts never do. Hints use Readonly in a fresh thread. Returned text updates the compact student
explanation and Android TextToSpeech, while any failure uses local text and leaves replayable JSONL.
The teacher dashboard calculates all numbers from SQLite and labels AI/mock prose separately.

## Exact memory policy

Every attempt is stored locally and in SQLite. Backboard Auto is requested only once per policy
key when one of these deterministic conditions is met:

- the same incorrect expected/selected confusion pair occurs at least twice;
- the same expected region has at least two incorrect answers;
- the region has at least two attempts using hints;
- the region has at least three attempts and accuracy is at most 50 percent.

The recurring confusion-pair rule has priority. A unique `(student_id, policy_key)` prevents
duplicate Auto calls. The Auto prompt explicitly states the locally counted number of separate
attempts. Correct answers, first errors, connectivity checks, and generic explanations use Off.
Personalized hints use Readonly and omit `thread_id`, so they cannot write memory and do not depend
on one conversation's history. All thresholds, grades, response times, and dashboard numbers come
from local deterministic data.

## Security boundaries

- The Backboard key exists only in `~/.config/backboard/backboard.env` on the laptop.
- Never open or source `~/.config/backboard/tech_crackers.env`.
- The replaceable prototype token is in `~/.config/backboard/quiz_proxy.env`, mode 600.
- `/health` reveals only `{\"status\":\"ok\"}`. All student, attempt, hint, summary, teacher,
  and export API endpoints require the token.
- The prototype limits bodies to 32 KiB and each client to 60 protected requests per minute.
- LAN mode requires `--lan` and an explicit private IPv4 address. Wildcard, loopback, public,
  HTTPS-mismatched, and wrong-port phone build URLs are rejected.
- CORS is restricted to the configured dashboard origin. The server is never exposed publicly.
- The test APK will necessarily contain the replaceable LAN token. It never contains the
  Backboard key. Rotate the token after testing by creating a new high-entropy value in the
  protected token file and rebuilding the test APK.

## Mock commands

Loopback Editor/mock mode:

```bash
cd /home/omar/UnityProjects/BMC
set -a
source /home/omar/.config/backboard/quiz_proxy.env
set +a
BACKBOARD_MOCK=1 QUIZ_PROXY_DB="$PWD/Tools/BackboardQuizProxy/data/mock_editor.sqlite3" \
  Tools/BackboardQuizProxy/.venv/bin/python Tools/BackboardQuizProxy/run_proxy.py --port 8765
```

Mock LAN preflight:

```bash
cd /home/omar/UnityProjects/BMC
set -a
source /home/omar/.config/backboard/quiz_proxy.env
set +a
BACKBOARD_MOCK=1 QUIZ_PROXY_DB="$PWD/Tools/BackboardQuizProxy/data/mock_phone_preflight.sqlite3" \
  Tools/BackboardQuizProxy/.venv/bin/python Tools/BackboardQuizProxy/run_proxy.py \
  --lan --host 10.70.221.178 --port 8765
```

Health: `http://10.70.221.178:8765/health`

Dashboard: `http://10.70.221.178:8765/teacher`

The dashboard asks for the prototype token and keeps it only in browser session storage.

## Real laptop command

Use a fresh database. This command sources only the active personal key and the prototype token:

```bash
cd /home/omar/UnityProjects/BMC
set -a
source /home/omar/.config/backboard/backboard.env
source /home/omar/.config/backboard/quiz_proxy.env
set +a
BACKBOARD_MOCK=0 BACKBOARD_MAX_ATTEMPTS=1 QUIZ_ENABLE_TEACHER_AI=0 \
  QUIZ_PROXY_DB="$PWD/Tools/BackboardQuizProxy/data/phone_real.sqlite3" \
  Tools/BackboardQuizProxy/.venv/bin/python Tools/BackboardQuizProxy/run_proxy.py \
  --lan --host 10.70.221.178 --port 8765
```

Press Ctrl+C to stop. Do not replay any earlier verification database.

## Isolated phone build

`JawQuizAndroidTestBuild.BuildPhoneProxy` reads `QUIZ_PROXY_URL` and `QUIZ_PROXY_TOKEN` from the
build process, validates a private IPv4 URL on port 8765, writes them only into a temporary scene
copy, builds that one launch scene, deletes the copy, and restores the manifest and all temporary
settings even on failure. The protected serialized quiz scene remains unchanged. Proposed output:
`/home/omar/JawRepair/JawSurfaceQuiz_BackboardProxy_Test.apk`, version code 27, with display name
`Jaw Surface Quiz Backboard Test`.
The build temporarily enables cleartext HTTP for the approved private LAN endpoint and removes the
unused speech-recognition manifest query and microphone permission. The shared manifest is restored
byte-for-byte afterward. The Galaxy Note 9 browser health check succeeded before this build ran.

## Firewall

No firewall setting is changed automatically. If the phone cannot connect and UFW is active, the
narrow proposed rule is:

```bash
sudo ufw allow from 10.70.192.0/19 to 10.70.221.178 port 8765 proto tcp
```

Ask for approval before executing it, and remove the rule after testing.
