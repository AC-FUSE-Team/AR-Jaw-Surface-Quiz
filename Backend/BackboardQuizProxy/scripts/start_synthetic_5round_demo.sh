#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROXY_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEMO_DB="$PROXY_ROOT/data/jaw_quiz_SYNTHETIC_5ROUND_DEMO.sqlite3"
if [[ ! -f "$DEMO_DB" ]]; then
  echo "Five-round synthetic database is missing. Seed it first:"
  echo "  .venv/bin/python scripts/seed_synthetic_5round_demo.py"
  exit 2
fi
if [[ "${1:-}" == "--backboard" ]]; then
  set -a
  [[ -f /home/omar/.config/backboard/backboard.env ]] && source /home/omar/.config/backboard/backboard.env
  [[ -f /home/omar/.config/backboard/quiz_proxy.env ]] && source /home/omar/.config/backboard/quiz_proxy.env
  set +a
  export BACKBOARD_MOCK=0
  echo "BACKBOARD MODE AVAILABLE — no request occurs until dashboard confirmation"
else
  unset BACKBOARD_API_KEY QUIZ_ENABLE_TEACHER_AI
  export BACKBOARD_MOCK=1
  echo "OFFLINE MODE — Backboard disabled; local PDF generation remains available"
fi
export QUIZ_PROXY_DB="$DEMO_DB"
export QUIZ_PROXY_TOKEN="${QUIZ_PROXY_TOKEN:-}"
export PYTHONPATH="/usr/lib/python3/dist-packages"
cd "$PROXY_ROOT"
echo "Dashboard: http://127.0.0.1:8765/teacher"
exec .venv/bin/python run_proxy.py --port 8765
