#!/usr/bin/env bash
# Read-only readiness probes for desktop-runner pairing against a deployed ReelBolt.
#
#   scripts/check-runner-prod.sh https://reelbolt.ai
#
# Sends only unauthenticated requests that no handler can act on: an invalid pair/start body (400
# before anything is stored, so NO pairing request is created), a GET to the websocket route
# without an upgrade, a GET to the POST-only telemetry route, an unsigned device API call. No
# secrets, no cookies, no writes. Each line is one public, read-only check of the production runner endpoints.
#
# Environment (all optional):
#   RUNNER_HOST       runner.<domain> to check as well (only if RUNNER_HOSTNAME is configured)
#   DOWNLOADS_URL     default https://downloads.reelbolt.ai
#   TLS_WARN_DAYS     warn when a certificate expires within this many days (default 21)
#   CURL_OPTS         extra curl options (for a local mock: --resolve, -k; -k defeats the TLS check, never use it against production)
#
# Exit code: 0 when no FAIL (WARN allowed), 1 when any FAIL, 2 on usage error.
set -u

if [ $# -ne 1 ] || [ -z "${1:-}" ]; then
  echo "usage: $0 <base-url>   e.g. $0 https://reelbolt.ai" >&2
  exit 2
fi
BASE="${1%/}"
case "$BASE" in
  http://* | https://*) ;;
  *) echo "base-url must start with http:// or https://" >&2; exit 2 ;;
esac
HOSTPORT="${BASE#*://}"
HOSTPORT="${HOSTPORT%%/*}"
HOST="${HOSTPORT%%:*}"
PORT=443
case "$HOSTPORT" in *:*) PORT="${HOSTPORT##*:}" ;; esac
DOWNLOADS_URL="${DOWNLOADS_URL:-https://downloads.reelbolt.ai}"
TLS_WARN_DAYS="${TLS_WARN_DAYS:-21}"
RUNNER_HOST="${RUNNER_HOST:-}"

PASS=0
WARN=0
FAIL=0
pass() { PASS=$((PASS + 1)); printf 'PASS  %s\n' "$*"; }
warn() { WARN=$((WARN + 1)); printf 'WARN  %s\n' "$*"; }
fail() { FAIL=$((FAIL + 1)); printf 'FAIL  %s\n' "$*"; }

BODY=$(mktemp)
HDRS=$(mktemp)
trap 'rm -f "$BODY" "$HDRS"' EXIT

# req METHOD URL [curl args...] -> sets CODE (000 on transport failure); body in $BODY, headers in $HDRS.
req() {
  local method="$1" url="$2"
  shift 2
  : >"$BODY"
  : >"$HDRS"
  # shellcheck disable=SC2086
  CODE=$(curl -sS --max-time 15 ${CURL_OPTS:-} -X "$method" -o "$BODY" -D "$HDRS" -w '%{http_code}' "$@" "$url" 2>/dev/null) || CODE=000
  [ -n "$CODE" ] || CODE=000
}

is_proxy_error() { case "$1" in 000 | 404 | 502 | 503 | 504) return 0 ;; esac; return 1; }

# ---- TLS ----------------------------------------------------------------------------------
check_tls() {
  local host="$1" port="$2" label="$3" end epoch now days
  if ! command -v openssl >/dev/null 2>&1; then
    warn "tls $label: openssl not installed, certificate expiry not checked"
    return
  fi
  end=$(echo | timeout 15 openssl s_client -servername "$host" -connect "$host:$port" 2>/dev/null |
    openssl x509 -noout -enddate 2>/dev/null | sed 's/^notAfter=//')
  if [ -z "$end" ]; then
    fail "tls $label: no certificate from $host:$port"
    return
  fi
  epoch=$(date -d "$end" +%s 2>/dev/null || echo 0)
  now=$(date +%s)
  days=$(((epoch - now) / 86400))
  if [ "$epoch" -eq 0 ]; then
    warn "tls $label: could not parse expiry '$end'"
  elif [ "$days" -lt 0 ]; then
    fail "tls $label: certificate EXPIRED ($end)"
  elif [ "$days" -lt "$TLS_WARN_DAYS" ]; then
    warn "tls $label: certificate expires in $days days ($end)"
  else
    pass "tls $label: certificate valid for $days more days"
  fi
  # Chain/hostname verification is curl's job: a plain HEAD without -k fails on an untrusted chain.
  req GET "https://$host:$port/" --noproxy '*'
  if [ "$CODE" = 000 ]; then
    fail "tls $label: curl could not verify the chain or hostname for $host"
  else
    pass "tls $label: chain and hostname verified (HTTP $CODE)"
  fi
}

case "$BASE" in
  https://*) check_tls "$HOST" "$PORT" "$HOST" ;;
  *) warn "tls: base URL is http://, certificate not checked (prod must be https)" ;;
esac
if [ -n "$RUNNER_HOST" ]; then
  check_tls "$RUNNER_HOST" 443 "$RUNNER_HOST"
else
  warn "tls runner.: RUNNER_HOST not set, dedicated runner hostname not checked (fine when the gateway is under the app host)"
fi

# ---- downloads -------------------------------------------------------------------------------
# publish writes runner/<channel>/latest.json; the site reads the stable one. Before the first promotion
# only beta exists, which is a WARN, not a FAIL.
req GET "$DOWNLOADS_URL/runner/stable/latest.json" -H 'Origin: https://reelbolt.ai'
if [ "$CODE" = 200 ]; then
  if grep -q '[{]' "$BODY"; then
    pass "downloads: $DOWNLOADS_URL/runner/stable/latest.json reachable (200, JSON)"
  else
    fail "downloads: stable/latest.json answered 200 but is not JSON"
  fi
  if grep -qi '^access-control-allow-origin:' "$HDRS"; then
    pass "downloads: CORS header present on stable/latest.json"
  else
    warn "downloads: no Access-Control-Allow-Origin on stable/latest.json (the site fetches it at build time; needed only for a runtime fetch)"
  fi
else
  STABLE_CODE=$CODE
  req GET "$DOWNLOADS_URL/runner/beta/latest.json" -H 'Origin: https://reelbolt.ai'
  if [ "$CODE" = 200 ]; then
    warn "downloads: stable/latest.json answered HTTP $STABLE_CODE but beta/latest.json exists (not promoted to stable yet)"
  else
    fail "downloads: $DOWNLOADS_URL/runner/stable/latest.json answered HTTP $STABLE_CODE and beta answered $CODE (nothing published yet?)"
  fi
fi

# ---- pairing ---------------------------------------------------------------------------------
# An empty JSON object fails validation (invalid_name / invalid_public_key) before any row is written.
req POST "$BASE/api/v1/runners/pair/start" -H 'Content-Type: application/json' --data '{}'
case "$CODE" in
  400 | 401 | 403 | 422 | 429)
    if grep -q '[{]' "$BODY"; then
      pass "pair/start: invalid body refused with HTTP $CODE JSON (route reaches the Go API)"
    else
      warn "pair/start: HTTP $CODE but body is not JSON (a proxy or WAF answered, not the API?)"
    fi
    ;;
  2*) fail "pair/start: an EMPTY body returned HTTP $CODE; pairing may have created a request" ;;
  *) fail "pair/start: HTTP $CODE (404/502 means nginx does not route /api/v1/runners to the Go API)" ;;
esac

# ---- gateway -----------------------------------------------------------------------------------
probe_connect() {
  local base="$1" path="$2" label="$3"
  req GET "$base$path"
  case "$CODE" in
    400 | 401 | 403 | 405 | 426) pass "gateway $label: plain GET refused with HTTP $CODE (route reaches the gateway)" ;;
    404) fail "gateway $label: HTTP 404, the route is not published (UPSTREAM_RUNNER_GATEWAY unset?)" ;;
    502 | 503 | 504 | 000) fail "gateway $label: HTTP $CODE, nginx cannot reach the gateway (is the fabric profile up?)" ;;
    *) warn "gateway $label: unexpected HTTP $CODE" ;;
  esac
}
probe_connect "$BASE" /runner/v1/connect "$HOST/runner/v1/connect"
if [ -n "$RUNNER_HOST" ]; then
  probe_connect "https://$RUNNER_HOST" /v1/connect "$RUNNER_HOST/v1/connect"
fi

# Telemetry is POST only: nginx denies GET (403) or the gateway answers 405. 404/502 is the failure.
req GET "$BASE/runner/telemetry/v1/reports"
case "$CODE" in
  403 | 405) pass "telemetry: GET refused with HTTP $CODE (POST-only route is published)" ;;
  404) fail "telemetry: HTTP 404, route not published (UPSTREAM_RUNNER_GATEWAY unset?)" ;;
  502 | 503 | 504 | 000) fail "telemetry: HTTP $CODE, nginx cannot reach the gateway" ;;
  *) warn "telemetry: unexpected HTTP $CODE for GET" ;;
esac

# ---- device API ----------------------------------------------------------------------------------
req GET "$BASE/api/v1/device/me"
if [ "$CODE" = 401 ] && grep -q 'device_unauthorized' "$BODY"; then
  pass "device API: unsigned request answered 401 device_unauthorized"
elif is_proxy_error "$CODE"; then
  fail "device API: HTTP $CODE (404/502 means /api/v1/device/ does not reach the Inference API)"
else
  warn "device API: unsigned request answered HTTP $CODE, expected 401 device_unauthorized"
fi

printf '\nSummary: %d pass, %d warn, %d fail\n' "$PASS" "$WARN" "$FAIL"
if [ "$FAIL" -gt 0 ]; then
  echo "RESULT: FAIL"
  exit 1
fi
echo "RESULT: OK (review WARN lines)"
exit 0
