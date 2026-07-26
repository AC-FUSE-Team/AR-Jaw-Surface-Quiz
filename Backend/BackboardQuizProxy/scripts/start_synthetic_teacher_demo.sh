#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROXY_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEMO_DB="$PROXY_ROOT/data/jaw_quiz_SYNTHETIC_DEMO.sqlite3"
if [[ ! -f "$DEMO_DB" ]]; then
  echo "Synthetic demo database is missing. Seed it first:"
  echo "  python3 $SCRIPT_DIR/seed_synthetic_teacher_demo.py"
  exit 2
fi
unset BACKBOARD_API_KEY
unset QUIZ_ENABLE_TEACHER_AI
export BACKBOARD_MOCK=1
export QUIZ_PROXY_DB="$DEMO_DB"
export QUIZ_PROXY_TOKEN=""
export PYTHONPATH="/usr/lib/python3/dist-packages"
cd "$PROXY_ROOT"
echo "STARTING SYNTHETIC LOCAL DEMONSTRATION — external Backboard/model access disabled"
echo "Dashboard: http://127.0.0.1:8765/teacher"
exec .venv/bin/python run_proxy.py --port 8765
