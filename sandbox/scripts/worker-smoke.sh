#!/usr/bin/env bash
#
# worker-smoke.sh — the D13 acceptance check that can actually be run here.
#
# It stands up a real runner gateway, a real CloudPool worker, and a real
# throwaway Postgres, then drives one video.compile job through it. Nothing in
# the render path is faked:
#
#   1. an unenrolled pool worker is refused 4401 — the gap D5a found;
#   2. enrolling it (reelbolt-worker enroll) creates the runner_devices row and
#      the same worker now handshakes, with one idempotent row;
#   3. the gateway assigns it a real job and the worker accepts it;
#   4. an ffmpeg op runs INSIDE A REAL DOCKER CONTAINER (the sandbox-runtime
#      image), encodes a real MP4, and ffprobe reads its dimensions back through
#      the session;
#   5. closing the session completes the job;
#   6. the scaling hook reads D5c's /internal/v1/scaling and runs its command;
#   7. SIGTERM drains the worker and it exits.
#
# What it does NOT prove, stated plainly because the difference matters:
#   - remotion.workspace-session is not exercised. It needs a runtime image
#     pinned by DIGEST, and a locally built tag has none — which is correct
#     behaviour, not a gap: an unpinned runtime must not be advertised.
#   - presigned stage/publish are not exercised. They need an object store with
#     a public endpoint; D8's compose Garage is where that belongs.
#   - nothing here touches a cloud provider. See docs/cloud-worker.md for what
#     F12 must supply.
#
# Usage:  sandbox/scripts/worker-smoke.sh
# Env:    SANDBOX_IMAGE (default reelbolt-sandbox-runtime:local)
#         REELBOLT_SMOKE_PG_PORT / REELBOLT_SMOKE_GW_PORT to avoid collisions
#
# Requirements: docker with a working daemon, curl, jq, uuidgen, and the golang
# and postgres images available locally (the build and the database run in
# containers; no host Go toolchain is used).

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "$0")/../.." && pwd)
SANDBOX="$REPO_ROOT/sandbox"
GO_IMAGE=${REELBOLT_SMOKE_GO_IMAGE:-golang:1.26-alpine}
GO_MODCACHE=${REELBOLT_SMOKE_GO_MODCACHE:-reelforge-go-modcache}
PG_IMAGE=${REELBOLT_SMOKE_PG_IMAGE:-postgres:17-alpine}
RUNTIME_IMAGE=${SANDBOX_IMAGE:-reelbolt-sandbox-runtime:local}
# Unique per run, so a leftover process from an earlier (or interrupted) run can
# never "docker rm -f" THIS run's database out from under it. Seen in practice:
# an interrupted run whose EXIT trap fired late took the next run's postgres down
# at the moment it finished initialising.
PG_NAME="reelbolt-d13-pg-$RANDOM$(date +%s)"
PG_PORT=${REELBOLT_SMOKE_PG_PORT:-55439}
GW_PORT=${REELBOLT_SMOKE_GW_PORT:-18099}
SANDBOX_NETWORK="reelbolt-d13-smoke-net"
WORKER_NAME="pool-smoke"
POOL_CREDENTIAL="smoke-pool-credential"
INTERNAL_TOKEN="smoke-internal-token"

TMP=$(mktemp -d /tmp/reelbolt-d13-smoke.XXXXXX)
BIN="$TMP/bin"
GW_PID=""
WORKER_PID=""
PASSES=0

log()  { printf '\n=== %s ===\n' "$*"; }
pass() { PASSES=$((PASSES + 1)); printf 'PASS: %s\n' "$*"; }
fail() { printf '\nFAIL: %s\n' "$*" >&2; exit 1; }

# stop_pid sends SIGTERM and WAITS. Not waiting is not a cosmetic problem here:
# a worker that survives its own smoke run keeps reconnecting to the same port,
# and because the gateway supersedes per identity, it would steal the socket from
# the next run's worker before that one could do anything.
stop_pid() {
  local pid="$1"
  [ -n "$pid" ] || return 0
  kill -TERM "$pid" 2>/dev/null || return 0
  for _ in $(seq 1 20); do
    kill -0 "$pid" 2>/dev/null || return 0
    sleep 0.5
  done
  kill -KILL "$pid" 2>/dev/null || true
}

cleanup() {
  stop_pid "$WORKER_PID"
  stop_pid "$GW_PID"
  docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
  docker network rm "$SANDBOX_NETWORK" >/dev/null 2>&1 || true
  printf '\nartifacts: %s\n' "$TMP"
}
trap cleanup EXIT

command -v docker >/dev/null || fail "docker is required"
docker info >/dev/null 2>&1 || fail "the docker daemon is not reachable"
for tool in curl jq uuidgen; do command -v "$tool" >/dev/null || fail "$tool is required"; done
docker image inspect "$RUNTIME_IMAGE" >/dev/null 2>&1 || fail "the runtime image $RUNTIME_IMAGE is not present locally (build it, or set SANDBOX_IMAGE)"

# A worker left over from an EARLIER smoke run is worse than untidy: it
# reconnects to the same gateway port and, since both processes use the same pool
# worker name, supersedes the worker this run starts. The pattern only matches
# this script's own temp directories, so a real worker on the host is untouched.
if pgrep -f "reelbolt-d13-smoke.*reelbolt-worker" >/dev/null 2>&1; then
  log "killing stale smoke workers from an earlier run"
  pkill -f "reelbolt-d13-smoke.*reelbolt-worker" || true
  sleep 1
fi

# ── build ───────────────────────────────────────────────────────────────────
log "building the gateway, the worker and the scaler"
mkdir -p "$BIN"
docker run --rm -v "$SANDBOX":/w -v "$GO_MODCACHE":/go/pkg/mod -v "$BIN":/out -w /w -e CGO_ENABLED=0 "$GO_IMAGE" sh -euc '
	go build -o /out/runner-gateway ./cmd/runner-gateway
	go build -o /out/reelbolt-worker ./cmd/reelbolt-worker
	go build -o /out/reelbolt-scaler ./cmd/reelbolt-scaler
'
pass "three static binaries built (they run on the host, not in a container)"

# ── database ────────────────────────────────────────────────────────────────
log "starting a throwaway postgres"
#
# Readiness comes from the DAEMON's health check rather than from a host-side
# "docker exec ... pg_isready" loop. That is not merely tidier: on this machine
# an exec-based probe intermittently left the official entrypoint stuck between
# its temporary server and the real one ("PostgreSQL init process complete" never
# printed, the container stayed running, and every probe failed for a minute), so
# the probe is kept inside the container and the start is retried. A flaky
# database would otherwise look like a flaky worker.
pg_ready=0
for attempt in 1 2 3; do
  PG_NAME="reelbolt-d13-pg-$RANDOM$attempt"
  docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
  docker run -d --rm --name "$PG_NAME" -e POSTGRES_PASSWORD=postgres \
    -p "127.0.0.1:$PG_PORT:5432" \
    --health-cmd "pg_isready -U postgres" --health-interval 2s --health-timeout 2s --health-retries 30 \
    "$PG_IMAGE" >/dev/null
  for _ in $(seq 1 45); do
    state=$(docker inspect "$PG_NAME" --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' 2>/dev/null || echo gone)
    [ "$state" = "healthy" ] && { pg_ready=1; break; }
    [ "$state" = "gone" ] && break
    sleep 1
  done
  [ "$pg_ready" = "1" ] && break
  printf 'postgres attempt %s did not become healthy\n' "$attempt"
  docker logs "$PG_NAME" 2>&1 | tail -6 || true
  docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
done
[ "$pg_ready" = "1" ] || fail "postgres never became healthy"
DB_URL="postgres://postgres:postgres@127.0.0.1:$PG_PORT/postgres?sslmode=disable"
docker exec -i "$PG_NAME" psql -U postgres -v ON_ERROR_STOP=1 -q < "$SANDBOX/scripts/runner-smoke-schema.sql"
pass "runner_devices, runner_jobs and runner_sessions created from the migration-faithful schema"

# ── gateway ─────────────────────────────────────────────────────────────────
log "starting the runner gateway"
PORT="$GW_PORT" \
DATABASE_URL="$DB_URL" \
RUNNER_POOL_CREDENTIALS="$POOL_CREDENTIAL" \
RUNNER_INTERNAL_TOKEN="$INTERNAL_TOKEN" \
GATEWAY_INSTANCE="smoke-gateway" \
"$BIN/runner-gateway" > "$TMP/gateway.log" 2>&1 &
GW_PID=$!
for _ in $(seq 1 60); do
  curl -sf "http://127.0.0.1:$GW_PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.5
done
curl -sf "http://127.0.0.1:$GW_PORT/healthz" >/dev/null 2>&1 || { tail -20 "$TMP/gateway.log"; fail "the gateway never became healthy"; }
pass "gateway healthy on 127.0.0.1:$GW_PORT"

# ── the worker's environment, in one place ──────────────────────────────────
#
# These are EXPORTED rather than wrapped in a function that runs the command, and
# that is not a style choice: "$!" after `f cmd &` is the PID of the subshell
# bash forks for the function, not of cmd, so killing "the worker" killed the
# subshell and left the real worker running — which then reconnected and stole
# the next run's socket. Exporting keeps $! pointing at the binary itself.
worker_exports() {
  export REELBOLT_GATEWAY_URL="ws://127.0.0.1:$GW_PORT/v1/connect"
  export RUNNER_POOL_CREDENTIAL="$POOL_CREDENTIAL"
  export REELBOLT_WORKER_NAME="$WORKER_NAME"
  export REELBOLT_WORKER_CPUS=4
  export SANDBOX_CPU_LIMIT=2
  export SANDBOX_IMAGE="$RUNTIME_IMAGE"
  export SANDBOX_ROOT="$TMP/sandboxes"
  export SANDBOX_NETWORK="$SANDBOX_NETWORK"
  export REELBOLT_WORKER_SCRATCH="$TMP/scratch"
  export SANDBOX_CONTAINER_CLI=docker
}
worker_exports

# ── 1. the gap ──────────────────────────────────────────────────────────────
log "1/7 an UNENROLLED pool worker is refused"
mkdir -p "$TMP/sandboxes" "$TMP/scratch"
set +e
timeout 25 "$BIN/reelbolt-worker" run > "$TMP/refused.log" 2>&1
REFUSED=$?
set -e
grep -q "unauthenticated" "$TMP/refused.log" || { tail -25 "$TMP/refused.log"; fail "the worker was not refused with a 4401"; }
grep -q "reelbolt-worker enroll" "$TMP/refused.log" || { tail -25 "$TMP/refused.log"; fail "the 4401 did not tell the operator how to fix it"; }
[ "$REFUSED" -eq 4 ] || fail "exit code $REFUSED, want 4 for a terminal 4401"
pass "refused 4401 with the enrollment hint, exit 4 (fails closed, does not loop)"

# ── 2. enroll ───────────────────────────────────────────────────────────────
log "2/7 enrolling the pool worker"
DATABASE_URL="$DB_URL" REELBOLT_WORKER_NAME="$WORKER_NAME" "$BIN/reelbolt-worker" enroll > "$TMP/enroll.log" 2>&1 || { cat "$TMP/enroll.log"; fail "enroll failed"; }
grep -q "enrolled pool worker" "$TMP/enroll.log" || { cat "$TMP/enroll.log"; fail "enroll did not report a new row"; }
DATABASE_URL="$DB_URL" REELBOLT_WORKER_NAME="$WORKER_NAME" "$BIN/reelbolt-worker" enroll >> "$TMP/enroll.log" 2>&1 || { cat "$TMP/enroll.log"; fail "the second enroll failed"; }
grep -q "already enrolled" "$TMP/enroll.log" || { cat "$TMP/enroll.log"; fail "the second enroll did not land on the same row"; }
ROWS=$(docker exec "$PG_NAME" psql -U postgres -tAc "select count(*) from runner_devices where kind='CloudPool' and name='$WORKER_NAME'")
[ "$ROWS" = "1" ] || fail "$ROWS rows are named $WORKER_NAME, want exactly 1"
pass "one idempotent runner_devices row (kind=CloudPool, organization_id null)"

# ── 3. the worker connects ──────────────────────────────────────────────────
log "3/7 starting the enrolled worker"
"$BIN/reelbolt-worker" run > "$TMP/worker.log" 2>&1 &
WORKER_PID=$!
CONNECTED=0
for _ in $(seq 1 60); do
  if curl -s "http://127.0.0.1:$GW_PORT/metrics" | grep -q 'reelbolt_runner_connected{kind="CloudPool"} 1'; then CONNECTED=1; break; fi
  sleep 0.5
done
[ "$CONNECTED" = "1" ] || { tail -30 "$TMP/worker.log"; fail "the worker never showed up as a connected CloudPool runner"; }
grep -q "video.compile" "$TMP/worker.log" || { tail -30 "$TMP/worker.log"; fail "the worker did not advertise video.compile"; }
pass "handshake accepted; the pool now has a runner (the same binary that was refused above)"

# ── 4. a job ────────────────────────────────────────────────────────────────
log "4/7 queueing a video.compile job for the pool"
ORG=$(uuidgen | tr 'A-Z' 'a-z')
EXEC=$(uuidgen | tr 'A-Z' 'a-z')
JOB_BODY='{"orgId":"'"$ORG"'","executionId":"'"$EXEC"'","jobType":"video.compile","target":"CloudPool","purpose":"d13-smoke","spec":{"note":"D13 smoke"}}'
CREATED=$(curl -sS -X POST "http://127.0.0.1:$GW_PORT/internal/v1/jobs" -H "Authorization: Bearer $INTERNAL_TOKEN" -H 'Content-Type: application/json' -d "$JOB_BODY")
JOB=$(printf '%s' "$CREATED" | jq -r '.jobId // empty')
[ -n "$JOB" ] || { printf '%s\n' "$CREATED"; fail "no jobId came back"; }

SESSION=""
STATUS=""
for _ in $(seq 1 60); do
  VIEW=$(curl -sS "http://127.0.0.1:$GW_PORT/internal/v1/jobs/$JOB" -H "Authorization: Bearer $INTERNAL_TOKEN")
  SESSION=$(printf '%s' "$VIEW" | jq -r '.sessionId // empty')
  STATUS=$(printf '%s' "$VIEW" | jq -r '.status // empty')
  [ -n "$SESSION" ] && break
  sleep 0.5
done
[ -n "$SESSION" ] || { tail -30 "$TMP/worker.log"; fail "the job was never assigned (status $STATUS)"; }
pass "job $JOB assigned and session $SESSION opened (status $STATUS)"

# ── 5. the ops, with a real container underneath ────────────────────────────
log "5/7 running ffmpeg inside a real container, through the session"
op() {
  curl -sS -X POST "http://127.0.0.1:$GW_PORT/internal/v1/sessions/$SESSION/ops" \
    -H "Authorization: Bearer $INTERNAL_TOKEN" -H 'Content-Type: application/json' \
    -d '{"op":"'"$1"'","args":'"$2"'}'
}

FFMPEG_ARGS='{"argv":["-hide_banner","-y","-f","lavfi","-i","testsrc=duration=1:size=320x180:rate=10","-pix_fmt","yuv420p","out.mp4"],"timeoutSec":180}'
RESULT=$(op ffmpeg "$FFMPEG_ARGS")
if [ "$(printf '%s' "$RESULT" | jq -r '.ok')" != "true" ]; then
  printf '%s\n' "$RESULT" | jq .
  tail -30 "$TMP/worker.log"
  fail "the ffmpeg op failed"
fi
EXIT_CODE=$(printf '%s' "$RESULT" | jq -r '.result.exitCode')
[ "$EXIT_CODE" = "0" ] || { printf '%s\n' "$RESULT" | jq -r '.result.stderrTail'; fail "ffmpeg exited $EXIT_CODE"; }
pass "ffmpeg encoded 1s of testsrc to out.mp4 inside $RUNTIME_IMAGE (exit 0)"

STAT=$(op stat '{"path":"out.mp4"}')
BYTES=$(printf '%s' "$STAT" | jq -r '.result.bytes // 0')
[ "$(printf '%s' "$STAT" | jq -r '.result.exists')" = "true" ] && [ "$BYTES" -gt 0 ] || { printf '%s\n' "$STAT"; fail "the rendered file is not in the session directory"; }
pass "the container wrote $BYTES bytes into the session's scratch directory"

PROBE=$(op ffprobe '{"argv":["-v","error","-show_entries","stream=width,height","-of","json","out.mp4"],"timeoutSec":60}')
if [ "$(printf '%s' "$PROBE" | jq -r '.ok')" != "true" ] || [ "$(printf '%s' "$PROBE" | jq -r '.result.exitCode')" != "0" ]; then
  printf '%s\n' "$PROBE" | jq .
  fail "the ffprobe op failed"
fi
WIDTH=$(printf '%s' "$PROBE" | jq -r '.result.stdout' | jq -r '.streams[0].width')
HEIGHT=$(printf '%s' "$PROBE" | jq -r '.result.stdout' | jq -r '.streams[0].height')
[ "$WIDTH" = "320" ] && [ "$HEIGHT" = "180" ] || fail "ffprobe read the wrong size: $WIDTH x $HEIGHT, want 320x180"
pass "ffprobe read the encoded video back as $WIDTH x $HEIGHT"

CLOSE=$(op close '{}')
[ "$(printf '%s' "$CLOSE" | jq -r '.ok')" = "true" ] || { printf '%s\n' "$CLOSE" | jq .; fail "the close op failed"; }

STATUS=""
for _ in $(seq 1 60); do
  STATUS=$(curl -sS "http://127.0.0.1:$GW_PORT/internal/v1/jobs/$JOB" -H "Authorization: Bearer $INTERNAL_TOKEN" | jq -r '.status // empty')
  [ "$STATUS" = "Succeeded" ] && break
  sleep 0.5
done
[ "$STATUS" = "Succeeded" ] || { tail -30 "$TMP/worker.log"; fail "the job ended as $STATUS, want Succeeded"; }
pass "the job completed as Succeeded (gateway to worker to container and back)"

# The worker recorded what it advertised: proof the enrollment row is the one
# the connection touched.
LAST_SEEN=$(docker exec "$PG_NAME" psql -U postgres -tAc "select coalesce(to_char(last_seen_at,'YYYY-MM-DD'),'') from runner_devices where name='$WORKER_NAME'")
SLOTS=$(docker exec "$PG_NAME" psql -U postgres -tAc "select coalesce(capabilities_json->>'slots','') from runner_devices where name='$WORKER_NAME'")
[ -n "$LAST_SEEN" ] || fail "the gateway never wrote last_seen_at on the enrolled row"
[ "$SLOTS" = "2" ] || fail "capabilities_json advertises $SLOTS slots, want 2 (4 vCPU / SANDBOX_CPU_LIMIT 2)"
pass "the enrolled row carries last_seen_at and the advertised 2 slots"

# ── 6. the scaling hook ─────────────────────────────────────────────────────
log "6/7 the scaling hook reads D5c's endpoint"
SCALING=$(RUNNER_GATEWAY_URL="http://127.0.0.1:$GW_PORT" RUNNER_INTERNAL_TOKEN="$INTERNAL_TOKEN" \
  POOL_SLOTS_PER_NODE=2 POOL_MAX_NODES=8 "$BIN/reelbolt-scaler" -json)
NODES=$(printf '%s' "$SCALING" | jq -r '.decision.nodes')
[ "$NODES" -ge 1 ] || { printf '%s\n' "$SCALING"; fail "the scaler decided $NODES nodes"; }
printf '%s\n' "$SCALING" | jq -c '.decision'
pass "dry run decided $NODES node(s) from D5c's desiredCloudPoolSlots (nothing was scaled)"

HOOK="$TMP/hook.out"
HOOK_CMD='echo "nodes=$POOL_NODES desired=$POOL_DESIRED_SLOTS cappedBy=$POOL_CAPPED_BY" > '"$HOOK"
RUNNER_GATEWAY_URL="http://127.0.0.1:$GW_PORT" RUNNER_INTERNAL_TOKEN="$INTERNAL_TOKEN" \
  POOL_SLOTS_PER_NODE=2 POOL_SCALE_COMMAND="$HOOK_CMD" "$BIN/reelbolt-scaler" -apply > "$TMP/scaler.log" 2>&1 \
  || { cat "$TMP/scaler.log"; fail "-apply failed"; }
grep -q "nodes=" "$HOOK" || { cat "$TMP/scaler.log"; fail "the scale command did not run"; }
pass "apply ran the hook with the decision in its environment: $(cat "$HOOK")"

# ── 7. drain ────────────────────────────────────────────────────────────────
log "7/7 SIGTERM drains the worker"
kill -TERM "$WORKER_PID"
STOPPED=0
for _ in $(seq 1 60); do
  if ! kill -0 "$WORKER_PID" 2>/dev/null; then STOPPED=1; break; fi
  sleep 0.5
done
[ "$STOPPED" = "1" ] || { tail -20 "$TMP/worker.log"; fail "the worker did not stop within 30s of SIGTERM"; }
WORKER_PID=""
grep -q "signal received: draining" "$TMP/worker.log" || { tail -20 "$TMP/worker.log"; fail "the worker did not drain on SIGTERM"; }
pass "drained for the spot-reclaim window and exited"

printf '\n%s checks passed.\n' "$PASSES"
