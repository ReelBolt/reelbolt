#!/usr/bin/env bash
# Serve THIS worktree's web build on 127.0.0.1:3780 (basePath /app) for the phone assistant spec.
# Never touches the shared docker stacks (:18080, :28080). Idempotent: reuses an already-listening server.
#   e2e/scripts/assistant-phone-web.sh          # build if needed, start in background, print URL
#   e2e/scripts/assistant-phone-web.sh stop     # kill it
#   REBUILD=1 e2e/scripts/assistant-phone-web.sh  # force `next build` (do this after changing web/ code)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SELF="$ROOT/e2e/scripts/assistant-phone-web.sh"
PORT=3780
URL="http://127.0.0.1:${PORT}"
PIDFILE=/tmp/assistant-drawer-mobile/web.pid
LOG=/tmp/assistant-drawer-mobile/web.log
mkdir -p /tmp/assistant-drawer-mobile

if [[ "${1:-}" == "stop" ]]; then
  [[ -f "$PIDFILE" ]] && kill "$(cat "$PIDFILE")" 2>/dev/null || true
  pkill -f "next start -H 127.0.0.1 -p ${PORT}" 2>/dev/null || true
  # `npm exec` dies on the wrapper's pid, not on the server it spawned, so reap whatever still holds the port:
  # otherwise a rebuild is served by the old build and the next run measures stale code.
  for pid in $(ss -ltnp 2>/dev/null | awk -v p=":${PORT} " '$0 ~ p' | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
    kill "$pid" 2>/dev/null || true
  done
  rm -f "$PIDFILE"; echo "stopped"; exit 0
fi

cd "$ROOT/web"
[[ -d node_modules ]] || npm ci --no-audit --no-fund
if [[ "${REBUILD:-}" == "1" || ! -f .next/BUILD_ID ]]; then
  # A running server must not serve a half-replaced .next.
  "$SELF" stop >/dev/null
  NEXT_TELEMETRY_DISABLED=1 npx next build
fi
if curl -fsS -o /dev/null "$URL/app/login" 2>/dev/null; then echo "already up: $URL"; exit 0; fi
NEXT_TELEMETRY_DISABLED=1 nohup npx next start -H 127.0.0.1 -p "$PORT" >"$LOG" 2>&1 &
echo $! >"$PIDFILE"
for _ in $(seq 1 60); do
  curl -fsS -o /dev/null "$URL/app/login" 2>/dev/null && { echo "up: $URL (pid $(cat "$PIDFILE"), log $LOG)"; exit 0; }
  sleep 1
done
echo "next start did not come up; see $LOG" >&2; exit 1
