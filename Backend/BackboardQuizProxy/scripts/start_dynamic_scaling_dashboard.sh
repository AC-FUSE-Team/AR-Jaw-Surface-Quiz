#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROXY_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEMO_DB="$PROXY_ROOT/data/skeleton_SYNTHETIC_DYNAMIC_SCALING.sqlite3"
if [[ ! -f "$DEMO_DB" ]]; then
  echo "Dynamic scaling database is missing. Initialize it first:"
  echo "  .venv/bin/python scripts/seed_dynamic_skeleton_scaling.py --initialize"
  exit 2
fi
if [[ "${1:-}" == "--backboard" ]]; then
  set -a
  [[ -f /home/omar/.config/backboard/backboard.env ]] && source /home/omar/.config/backboard/backboard.env
  [[ -f /home/omar/.config/backboard/quiz_proxy.env ]] && source /home/omar/.config/backboard/quiz_proxy.env
  set +a
  export BACKBOARD_MOCK=0
  echo "BACKBOARD MODE AVAILABLE — paid work still requires explicit per-student confirmation"
else
  unset BACKBOARD_API_KEY QUIZ_ENABLE_TEACHER_AI
  export BACKBOARD_MOCK=1
  echo "OFFLINE REVIEW MODE — all Backboard requests disabled"
fi
export QUIZ_PROXY_DB="$DEMO_DB"
export QUIZ_PROXY_TOKEN="${QUIZ_PROXY_TOKEN:-}"
export PYTHONPATH="/usr/lib/python3/dist-packages"
cd "$PROXY_ROOT"
echo "Dashboard: http://127.0.0.1:8765/teacher"
exec .venv/bin/python run_proxy.py --port 8765
