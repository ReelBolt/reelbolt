#!/usr/bin/env bash
# Run the admin-campaigns browser spec for one phase against an isolated stack and the local fake Brevo.
#   E2E_PROJECT=reelbolt-brevo-qa e2e/scripts/up-isolated.sh   # once, stack must already be up
#   e2e/brevo-mock/run-phase.sh <A|B|C|D|F|R> [playwright args...]
# Phases: A campaigns off | B on, list send off | C on, list send on | D as C without API key | E as C (api-contract)
#         F as B without footer identity | R as C with short sync-stale window (restart spec).
# Loopback only. No real key: BREVO_CAMPAIGN_API_KEY is the fake's own constant; recipients are @example.invalid.
# AIFORGE_BIN: unused here; run this script under the governor, e.g.
#   "$AIFORGE_BIN" testrun --class e2e -- e2e/brevo-mock/run-phase.sh E
# Guards: BrevoApiBase points at the fake, api.brevo.com resolves to 127.0.0.1 in the container, and
# Playwright aborts any brevo host. The fake listens on 127.0.0.1:${FAKE_PORT:-19741}.
set -euo pipefail
PH=${1:?phase A|B|C|D|E|F|R}; shift
HERE=$(cd "$(dirname "$0")" && pwd); E2E=$(dirname "$HERE"); ROOT=$(dirname "$E2E")
PROJECT=${E2E_PROJECT:-reelbolt-brevo-qa}
FAKE_PORT=${FAKE_PORT:-19741}
OUT=${QA_OUT:-$E2E/test-results/admin-campaigns-qa}  # test-results/ is git-ignored
mkdir -p "$OUT"
export BREVO_WEBHOOK_TOKEN=qa-fake-webhook-token BREVO_LIST_ID=10 BREVO_TEST_LIST_ID=11 BREVO_CAMPAIGN_API_KEY=qa-fake-not-a-key
export MARKETING_TEST_RECIPIENT=qa-test@example.invalid MARKETING_CONTROLLER_NAME="QA Example Ltd"
export MARKETING_FOOTER_ADDRESS="1 Example Street, Testville" MARKETING_FOOTER_VAT_NUMBER=QA0000000
EXTRA=()
case $PH in
  A) export MARKETING_CAMPAIGNS_ENABLED=false MARKETING_LIST_SEND_ENABLED=false QA_EXPECT=disabled;;
  B) export MARKETING_CAMPAIGNS_ENABLED=true MARKETING_LIST_SEND_ENABLED=false;;
  C|E) export MARKETING_CAMPAIGNS_ENABLED=true MARKETING_LIST_SEND_ENABLED=true;;
  D) export MARKETING_CAMPAIGNS_ENABLED=true MARKETING_LIST_SEND_ENABLED=true BREVO_CAMPAIGN_API_KEY=;;
  F) unset MARKETING_CONTROLLER_NAME MARKETING_FOOTER_ADDRESS MARKETING_FOOTER_VAT_NUMBER
     export MARKETING_CAMPAIGNS_ENABLED=true MARKETING_LIST_SEND_ENABLED=false;;
  R) export MARKETING_CAMPAIGNS_ENABLED=true MARKETING_LIST_SEND_ENABLED=true
     EXTRA=(-f "$HERE/compose.restart.yml");;
  *) echo "unknown phase $PH" >&2; exit 2;;
esac
# The fake, started here and stopped on exit.
# Bind loopback plus the docker bridge gateway (what host-gateway resolves to), never 0.0.0.0.
FAKE_BIND_ADDRS=${FAKE_BIND_ADDRS:-$(docker network inspect bridge -f '{{(index .IPAM.Config 0).Gateway}}' 2>/dev/null || true)}
export FAKE_BIND_ADDRS FAKE_PORT
FAKE_PID=
trap '[ -n "$FAKE_PID" ] && kill "$FAKE_PID" 2>/dev/null || true' EXIT
PORT=$FAKE_PORT node "$HERE/server.mjs" > "$OUT/fake.log" 2>&1 & FAKE_PID=$!
for _ in $(seq 50); do curl -fs "http://127.0.0.1:$FAKE_PORT/__qa/state" >/dev/null && break; sleep 0.1; done
docker compose -p "$PROJECT" --project-directory "$ROOT" --env-file "$E2E/.stack/$PROJECT/.env" \
  -f "$ROOT/docker-compose.yml" -f "$ROOT/docker-compose.e2e.yml" \
  -f "$HERE/compose.override.yml" -f "$HERE/compose.fast.yml" ${EXTRA[@]+"${EXTRA[@]}"} \
  up -d --force-recreate --wait --wait-timeout 240 inference
# What restart.spec.ts needs: a script running `docker compose` for this stack, and the inference container id.
QA_COMPOSE="$OUT/compose.sh"
cat > "$QA_COMPOSE" <<SH
#!/usr/bin/env bash
FAKE_PORT=$FAKE_PORT exec docker compose -p "$PROJECT" --project-directory "$ROOT" --env-file "$E2E/.stack/$PROJECT/.env" -f "$ROOT/docker-compose.yml" -f "$ROOT/docker-compose.e2e.yml" -f "$HERE/compose.override.yml" -f "$HERE/compose.fast.yml" ${EXTRA[*]+"${EXTRA[*]}"} "\$@"
SH
chmod +x "$QA_COMPOSE"
QA_INFERENCE_CONTAINER=$("$QA_COMPOSE" ps -q inference)
export QA_COMPOSE QA_INFERENCE_CONTAINER
# shellcheck disable=SC1090
source "$E2E/.stack/$PROJECT/env.sh"
export QA_OUT=$OUT QA_PHASE=$PH FAKE=http://127.0.0.1:$FAKE_PORT
cd "$E2E"
# No exec: the EXIT trap must run to stop the fake this script started. set -e would skip the cleanup of the code, so capture it.
rc=0
npx playwright test -c playwright.admin-campaigns.config.ts "$@" || rc=$?
exit "$rc"
