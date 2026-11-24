#!/usr/bin/env bash
# Flips the two "signup switch" halves on an ISOLATED e2e stack for the soft-open rehearsal
# (e2e/tests/soft-open/signup-to-checkout.spec.ts). See e2e/README.md "Soft-open rehearsal".
#
#   E2E_PROJECT=reelbolt-soft-open e2e/scripts/soft-open-switches.sh off   # negative control
#   E2E_PROJECT=reelbolt-soft-open e2e/scripts/soft-open-switches.sh on    # the stack's normal state
#   E2E_PROJECT=reelbolt-soft-open e2e/scripts/soft-open-switches.sh down  # remove the off-site container
#
# off: recreates go-api and web with SIGNUP_ENABLED=false (an extra compose override in /tmp, the stack's
#      own compose files untouched) and builds + runs a SECOND marketing-site image with
#      NEXT_PUBLIC_SIGNUP_ENABLED=false on 127.0.0.1:${SITE_OFF_PORT:-8131}. The site flag is inlined at
#      BUILD time, so the only way to see the "off" CTA is a separate build; the stack's own site (built by
#      up-isolated.sh with NEXT_PUBLIC_SIGNUP_ENABLED=true) is left alone.
# on:  recreates go-api and web from the stack's compose files (SIGNUP_ENABLED=true again).
#
# Needs the stack up (up-isolated.sh --provider stripe, E2E_PROFILES=e2e-site[,e2e-engine]). It only ever
# touches the compose project named by E2E_PROJECT and the container/image reelbolt-soft-open-site-off.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
E2E_PROJECT="${E2E_PROJECT:?set E2E_PROJECT to the compose project of the stack}"
refuse_project() {
  local name="$1" lower main_root forbidden
  lower="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
  if [ -z "$lower" ]; then
    echo "E2E_PROJECT is empty; refusing (an empty project name means the default stack)." >&2
    exit 2
  fi
  if ! printf '%s' "$lower" | grep -Eq '^[a-z0-9][a-z0-9_-]*$'; then
    echo "E2E_PROJECT='$name' is not a valid compose project name ([a-z0-9][a-z0-9_-]*)." >&2
    exit 2
  fi
  # Compose names the default project after the checkout directory; refuse both this checkout's and
  # the main checkout's, plus the names the developer's own stack has used.
  main_root="$(git -C "$REPO_ROOT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null | sed 's#/\.git$##' || true)"
  for forbidden in reelforge reelbolt "$(basename "$REPO_ROOT")" "${main_root:+$(basename "$main_root")}"; do
    forbidden="$(printf '%s' "$forbidden" | tr '[:upper:]' '[:lower:]')"
    if [ -n "$forbidden" ] && [ "$lower" = "$forbidden" ]; then
      echo "E2E_PROJECT='$name' would address the developer's default stack; refusing. Use e.g. reelbolt-<initiative>." >&2
      exit 2
    fi
  done
}
refuse_project "$E2E_PROJECT"
STACK_DIR="$REPO_ROOT/e2e/.stack/$E2E_PROJECT"
SITE_OFF_PORT="${SITE_OFF_PORT:-8131}"
SITE_OFF_NAME="${E2E_PROJECT}-site-off"
[ -f "$STACK_DIR/.env" ] || { echo "No stack at $STACK_DIR; run up-isolated.sh first." >&2; exit 2; }
APP_PORT="$(grep -E '^APP_PORT=' "$STACK_DIR/.env" | cut -d= -f2)"

compose() {
  docker compose -p "$E2E_PROJECT" --project-directory "$REPO_ROOT" --env-file "$STACK_DIR/.env" \
    -f "$REPO_ROOT/docker-compose.yml" -f "$REPO_ROOT/docker-compose.e2e.yml" \
    --profile e2e-site --profile e2e-engine --profile stripe "$@"
}

wait_api() {
  for _ in $(seq 1 60); do
    if curl -fs -o /dev/null "http://localhost:$APP_PORT/api/v1/legal/documents"; then return 0; fi
    sleep 2
  done
  echo "go-api did not come back on http://localhost:$APP_PORT" >&2
  exit 1
}

case "${1:-}" in
  off)
    OVERRIDE="$(mktemp /tmp/soft-open-signup-off.XXXXXX.yml)"
    cat >"$OVERRIDE" <<'EOF'
services:
  go-api:
    environment:
      SIGNUP_ENABLED: "false"
  web:
    environment:
      - SIGNUP_ENABLED=false
EOF
    compose -f "$OVERRIDE" up -d --no-deps --no-build --force-recreate go-api web
    rm -f "$OVERRIDE"
    wait_api
    docker build --network host --target production \
      --build-arg "NEXT_PUBLIC_SITE_URL=http://localhost:$APP_PORT" \
      --build-arg NEXT_PUBLIC_SIGNUP_ENABLED=false \
      -t "$SITE_OFF_NAME" "$REPO_ROOT/site" >/tmp/soft-open-site-off-build.log 2>&1
    docker rm -f "$SITE_OFF_NAME" >/dev/null 2>&1 || true
    docker run -d --name "$SITE_OFF_NAME" -p "127.0.0.1:$SITE_OFF_PORT:3000" "$SITE_OFF_NAME" >/dev/null
    for _ in $(seq 1 30); do
      if curl -fs -o /dev/null "http://127.0.0.1:$SITE_OFF_PORT/"; then break; fi
      sleep 1
    done
    echo "switches OFF: go-api SIGNUP_ENABLED=false; off-site on http://127.0.0.1:$SITE_OFF_PORT"
    ;;
  on)
    compose up -d --no-deps --no-build --force-recreate go-api web
    wait_api
    echo "switches ON: go-api SIGNUP_ENABLED=true (stack default)"
    ;;
  down)
    docker rm -f "$SITE_OFF_NAME" >/dev/null 2>&1 || true
    docker rmi "$SITE_OFF_NAME" >/dev/null 2>&1 || true
    echo "off-site container and image removed"
    ;;
  *)
    sed -n '2,20p' "$0"
    exit 2
    ;;
esac
