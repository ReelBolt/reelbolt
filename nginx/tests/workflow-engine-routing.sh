#!/bin/sh
# Static check: the engine's routes are reached through the Inference API, never directly.
#
# The workflow engine on a render host does not hold the session signing key, so it cannot
# validate a browser session. /api/v1/workflow-engine/* must therefore go to the Inference API
# (which authenticates the caller and calls the engine itself); only the anonymous liveness
# probe may go to the engine, and it must not carry the caller's credential.
#
#   sh nginx/tests/workflow-engine-routing.sh
set -eu
conf="$(cd "$(dirname "$0")/.." && pwd)/locations.conf"
fail=0
ok() { echo "ok   $1"; }
ko() { echo "FAIL $1"; fail=1; }

# block HEADER: the body of the location block whose opening line is exactly HEADER.
block() {
  awk -v h="$1" '$0 == h { on = 1; next } on && /^}/ { exit } on { print }' "$conf"
}

prefix="$(block 'location /api/v1/workflow-engine/ {')"
health="$(block 'location = /api/v1/workflow-engine/health {')"

[ -n "$prefix" ] && ok "the prefix location exists" || ko "location /api/v1/workflow-engine/ is missing"
[ -n "$health" ] && ok "the exact health location exists" || ko "location = /api/v1/workflow-engine/health is missing"

echo "$prefix" | grep -q 'proxy_pass \$inference_api;' && ok "the prefix goes to the Inference API" || ko "the prefix must proxy to \$inference_api"
echo "$prefix" | grep -q 'workflow_engine' && ko "the prefix must not name the engine upstream" || ok "the prefix never names the engine upstream"
echo "$prefix" | grep -q 'proxy_set_header Authorization \$auth_header;' && ok "the prefix translates the session cookie" || ko "the prefix must set Authorization \$auth_header"

echo "$health" | grep -q 'proxy_pass \$workflow_engine;' && ok "health goes to the engine" || ko "health must proxy to \$workflow_engine"
echo "$health" | grep -q 'Authorization' && ko "health must not forward a credential to the engine" || ok "health forwards no credential to the engine"

# Nothing else may send a request to the engine.
n="$(grep -c 'proxy_pass \$workflow_engine;' "$conf")"
[ "$n" -eq 1 ] && ok "exactly one location proxies to the engine" || ko "$n locations proxy to the engine, expected 1"

[ "$fail" -eq 0 ]
