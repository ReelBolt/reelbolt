#!/bin/sh
# The browser security header set of the nginx edge, pinned per path class (nginx/security-headers.conf,
# the maps in nginx.conf). Two halves, like forwarded-headers.sh:
#   1. static (--static-only, no docker): the snippet, the maps and the render step say what they should.
#   2. live: the built image behind an upstream that ALSO sends its own copies of the headers nginx owns
#      (proves the dedupe), probed over https on a stack with FORCE_HTTPS on and off. Every probed path
#      must answer EXACTLY the expected security headers, each once, nothing else of that family.
#
#   sh nginx/tests/security-headers.sh [--static-only]
set -u
cd "$(dirname "$0")/.." || exit 1
DIR=$(pwd)
FAIL=0
E=0

# --- 1. static --------------------------------------------------------------------------------
SNIP="$DIR/security-headers.conf"
NC="$DIR/nginx.conf"
RC="$DIR/render-config.sh"
for h in 'X-Content-Type-Options "nosniff"' 'X-Frame-Options "DENY"' 'Referrer-Policy $sh_referrer' \
         'Permissions-Policy $sh_permissions' 'Content-Security-Policy-Report-Only $sh_csp_report_only' \
         'Cross-Origin-Opener-Policy $sh_coop' 'Cross-Origin-Resource-Policy $sh_corp' \
         'Cross-Origin-Embedder-Policy-Report-Only $sh_coep_report_only' \
         'Reporting-Endpoints $sh_reporting_endpoints'; do
    grep -qF "add_header $h always;" "$SNIP" || { echo "FAIL security-headers.conf lacks [add_header $h always;]"; E=1; }
done
grep -qF 'include /etc/nginx/conf.d/hsts*.inc;' "$SNIP" || { echo "FAIL security-headers.conf does not include the FORCE_HTTPS-only hsts file"; E=1; }
# Nothing enforcing for the CSP or COEP, ever, in this snippet.
grep -Ev '^[[:space:]]*#' "$SNIP" | grep -Eq 'add_header[[:space:]]+(Content-Security-Policy|Cross-Origin-Embedder-Policy)[[:space:]]' \
    && { echo "FAIL security-headers.conf enforces the CSP or COEP (only the -Report-Only forms are allowed)"; E=1; }
# The upstream's copies of every header nginx owns are hidden (http level); the CSP itself is NOT hidden.
for h in X-Content-Type-Options X-Frame-Options Referrer-Policy Permissions-Policy Strict-Transport-Security \
         Content-Security-Policy-Report-Only Cross-Origin-Opener-Policy Cross-Origin-Resource-Policy \
         Cross-Origin-Embedder-Policy-Report-Only Reporting-Endpoints; do
    grep -qE "^[[:space:]]*proxy_hide_header[[:space:]]+$h;" "$NC" || { echo "FAIL nginx.conf does not hide the upstream's $h"; E=1; }
done
grep -Eq '^[[:space:]]*proxy_hide_header[[:space:]]+Content-Security-Policy;' "$NC" \
    && { echo "FAIL nginx.conf hides the upstream Content-Security-Policy (the API's own CSP on file responses must pass)"; E=1; }
# The policy: report-only strings only, the required directives, no upgrade-insecure-requests.
csp=$(grep -E 'default "default-src' "$NC" | head -1)
for d in "default-src 'self'" "object-src 'none'" "base-uri 'self'" "form-action 'self'" "frame-ancestors 'none'" \
         'report-uri /api/v1/telemetry/csp-report; report-to csp-endpoint' \
         'https://challenges.cloudflare.com' 'https://*.paddle.com' 'https://www.googletagmanager.com' 'worker-src'; do
    case "$csp" in *"$d"*) ;; *) echo "FAIL the CSP lacks [$d]"; E=1 ;; esac
done
case "$csp" in *upgrade-insecure-requests*) echo "FAIL the CSP has upgrade-insecure-requests (plain-http local and e2e stacks)"; E=1 ;; esac
# HSTS: includeSubDomains, no preload, only inside the FORCE_HTTPS branch; the preload caveat is written down.
grep -qF 'max-age=31536000; includeSubDomains" always;' "$RC" || { echo "FAIL render-config.sh HSTS is not max-age=31536000; includeSubDomains"; E=1; }
grep -v '^[[:space:]]*#' "$RC" | grep -qi 'preload' && { echo "FAIL render-config.sh sends preload"; E=1; }
grep -qi 'do NOT$' "$RC" || grep -qi 'do NOT submit' "$RC" || { echo "FAIL render-config.sh lost the comment that we do not submit to the preload list"; E=1; }
grep -qF 'security-headers.conf' "$DIR/Dockerfile" || { echo "FAIL the Dockerfile does not COPY security-headers.conf"; E=1; }
grep -qF 'security-headers.conf' "$RC" || { echo "FAIL render-config.sh does not copy security-headers.conf from the source mount"; E=1; }
[ "$E" -eq 0 ] && echo "ok   static: snippet, maps, hidden upstream copies, report-only CSP, HSTS with includeSubDomains and no preload"
[ "$E" -eq 0 ] || FAIL=1
[ "${1:-}" = "--static-only" ] && exit "$FAIL"

# --- 2. live ----------------------------------------------------------------------------------
TAG="sechdr-nginx-$$"
NET="sechdr-net-$$"
ECHO="sechdr-echo-$$"
NGX="sechdr-ngx-$$"
cleanup() {
    docker rm -f "$NGX" "$ECHO" >/dev/null 2>&1 || true
    docker network rm "$NET" >/dev/null 2>&1 || true
    docker rmi -f "$TAG" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

docker build -q --network host -t "$TAG" "$DIR" >/dev/null
docker network create "$NET" >/dev/null
# An upstream that sends its OWN copies of the headers nginx owns, with values nginx must replace, plus an
# enforced CSP of its own (the API does this on file responses) that must reach the client untouched.
docker run -d --name "$ECHO" --network "$NET" nginx:1.31.9-alpine sh -c '
    printf "%s\n" "server { listen 8080; default_type text/plain; absolute_redirect off; client_max_body_size 0;" \
      "add_header X-Content-Type-Options nosniff always;" \
      "add_header X-Frame-Options SAMEORIGIN always;" \
      "add_header Referrer-Policy origin always;" \
      "add_header Permissions-Policy camera=* always;" \
      "add_header Strict-Transport-Security max-age=1 always;" \
      "add_header Cross-Origin-Resource-Policy same-site always;" \
      "add_header Cross-Origin-Opener-Policy unsafe-none always;" \
      "add_header Content-Security-Policy-Report-Only default-src-upstream always;" \
      "add_header Content-Security-Policy frame-ancestors-upstream always;" \
      "add_header Reporting-Endpoints upstream=\"/x\" always;" \
      "location = /api/v1/telemetry/csp-report { add_header X-Echo \"auth=[\$http_authorization] cookie=[\$http_cookie]\" always; return 204; }" \
      "location / { return 200 ok; } }" \
      > /etc/nginx/conf.d/default.conf && exec nginx -g "daemon off;"' >/dev/null

U="http://$ECHO:8080"
start_nginx() {
    docker rm -f "$NGX" >/dev/null 2>&1 || true
    docker run -d --name "$NGX" --network "$NET" \
        -e UPSTREAM_GO_API="$U" -e UPSTREAM_INFERENCE_API="$U" -e UPSTREAM_WORKFLOW_ENGINE="$U" \
        -e UPSTREAM_WEB="$U" -e UPSTREAM_SITE="$U" -e UPSTREAM_DOCS="$U" -e UPSTREAM_MCP="$U" "$@" "$TAG" >/dev/null
    i=0
    until docker exec "$NGX" curl -s -o /dev/null http://127.0.0.1/health 2>/dev/null; do
        i=$((i + 1)); [ "$i" -gt 30 ] && { docker logs "$NGX"; echo "FAIL nginx did not start"; exit 1; }
        sleep 0.5
    done
}

# sec_headers <curl args...>: the security header family of the response, one "name: value" line per
# header line (a duplicated header shows twice), names lowercased, sorted.
sec_headers() {
    docker exec "$NGX" curl -s -o /dev/null -D - "$@" | tr -d '\r' \
        | grep -iE '^(x-content-type-options|x-frame-options|referrer-policy|permissions-policy|content-security-policy(-report-only)?|cross-origin-(opener|resource|embedder)-policy(-report-only)?|strict-transport-security|reporting-endpoints):' \
        | awk '{ i = index($0, ":"); print tolower(substr($0, 1, i - 1)) substr($0, i) }' | sort
}

PERM_DEFAULT='camera=(), microphone=(), geolocation=(), payment=()'
PERM_APP='camera=(), microphone=(self), geolocation=(), payment=(self "https://*.paddle.com")'
# $1 = storage source ("" or " https://host") after media-src and connect-src.
csp_for() {
    printf "default-src 'self'; script-src 'self' 'unsafe-inline' https://www.googletagmanager.com https://cdn.paddle.com https://challenges.cloudflare.com blob:; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' data: https://fonts.gstatic.com; img-src 'self' data: blob: https:; media-src 'self' blob: https://cdn.reelbolt.ai%s; connect-src 'self' blob:%s https://*.google-analytics.com https://*.analytics.google.com https://*.paddle.com https://challenges.cloudflare.com; frame-src https://*.paddle.com https://challenges.cloudflare.com; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; report-uri /api/v1/telemetry/csp-report; report-to csp-endpoint" "$1" "$1"
}

# expect <class> <hsts: 0|1> <storage>: the exact family for a path class.
expect() {
    class=$1; hsts=$2; stor=$3
    {
        echo 'x-content-type-options: nosniff'
        echo 'x-frame-options: DENY'
        # The upstream's enforced CSP is not nginx's to touch: it reaches the client as sent.
        # (/l/x is too short to be a link code: nginx redirects it itself, no upstream answers.)
        [ "$class" = link ] || echo 'content-security-policy: frame-ancestors-upstream'
        [ "$hsts" = 1 ] && echo 'strict-transport-security: max-age=31536000; includeSubDomains'
        case $class in
            review)  echo 'referrer-policy: no-referrer' ;;
            link_api) echo 'referrer-policy: no-referrer' ;;
            *)       echo 'referrer-policy: strict-origin-when-cross-origin' ;;
        esac
        case $class in app|review) echo "permissions-policy: $PERM_APP" ;; *) echo "permissions-policy: $PERM_DEFAULT" ;; esac
        case $class in
            site|doc|app|review) echo "content-security-policy-report-only: $(csp_for "$stor")"
                                 echo 'cross-origin-embedder-policy-report-only: credentialless; report-to="csp-endpoint"'
                                 echo 'reporting-endpoints: csp-endpoint="/api/v1/telemetry/csp-report"' ;;
        esac
        case $class in
            site|doc) echo 'cross-origin-opener-policy: same-origin' ;;
            app|review) echo 'cross-origin-opener-policy: same-origin-allow-popups' ;;
        esac
        case $class in
            link_api|link|public_asset|static) echo 'cross-origin-resource-policy: cross-origin' ;;
            *) echo 'cross-origin-resource-policy: same-origin' ;;
        esac
    } | sort
}

# check <label> <class> <hsts> <storage> <curl args...>
check() {
    label=$1; class=$2; hsts=$3; stor=$4; shift 4
    got=$(sec_headers "$@"); want=$(expect "$class" "$hsts" "$stor")
    if [ "$got" = "$want" ]; then echo "ok   $label"
    else
        echo "FAIL $label"; printf '%s\n' "$want" > /tmp/sechdr-want.$$; printf '%s\n' "$got" > /tmp/sechdr-got.$$
        diff /tmp/sechdr-want.$$ /tmp/sechdr-got.$$ | sed 's/^/     /'; rm -f /tmp/sechdr-want.$$ /tmp/sechdr-got.$$
        FAIL=1
    fi
}

# probe_all <hsts> <storage>: the path table, over https.
probe_all() {
    h=$1; s=$2
    check "/ (site)"                         site      "$h" "$s" -k https://127.0.0.1/
    check "/it/ (site, localised)"           site      "$h" "$s" -k https://127.0.0.1/it/
    check "/blog (site)"                     site      "$h" "$s" -k https://127.0.0.1/blog
    check "/docs/ (docs)"                    doc       "$h" "$s" -k https://127.0.0.1/docs/
    check "/app/login (dashboard)"           app       "$h" "$s" -k https://127.0.0.1/app/login
    check "/app/review/g/x (guest review)"  review    "$h" "$s" -k https://127.0.0.1/app/review/g/x
    check "/api/v1/output-links/x"           link_api  "$h" "$s" -k https://127.0.0.1/api/v1/output-links/x
    check "/api/v1/l/x"                      link      "$h" "$s" -k https://127.0.0.1/api/v1/l/x
    check "/l/x"                             link      "$h" "$s" -k https://127.0.0.1/l/x
    check "/brand/x"                         public_asset "$h" "$s" -k https://127.0.0.1/brand/x
    check "/_next/static/x"                  static    "$h" "$s" -k https://127.0.0.1/_next/static/x
    check "/health"                          machine   "$h" "$s" -k https://127.0.0.1/health
}

echo "-- FORCE_HTTPS=true, storage endpoint set (path and query are dropped, only the origin is named)"
start_nginx -e FORCE_HTTPS=true -e MINIO_PUBLIC_ENDPOINT='https://acct123.eu.r2.cloudflarestorage.com/bucket?x=1'
docker exec "$NGX" nginx -t 2>&1 | tail -1
probe_all 1 ' https://acct123.eu.r2.cloudflarestorage.com'

echo "-- FORCE_HTTPS off (the default), no storage endpoint: no HSTS, no storage source"
start_nginx -e FORCE_HTTPS=false
probe_all 0 ''
check "http:// /app/login on the plain-http server" app 0 '' http://127.0.0.1/app/login

echo "-- POST /api/v1/telemetry/csp-report: always an empty 204, anonymous, own rate limit"
start_nginx -e FORCE_HTTPS=false
CR=http://127.0.0.1/api/v1/telemetry/csp-report
# crpost <label> <expected echo header or ->  <curl args...>: status 204, empty body, no Set-Cookie.
crpost() {
    label=$1; want_echo=$2; shift 2
    out=$(docker exec "$NGX" sh -c 'curl -s -o /tmp/crbody -D /tmp/crhdr -w "%{http_code}" "$@"; echo " size=$(wc -c < /tmp/crbody | tr -d " ")"; tr -d "\r" < /tmp/crhdr' sh "$@")
    ok=1
    case "$out" in "204 size=0"*) ;; *) ok=0 ;; esac
    printf '%s\n' "$out" | grep -qi '^set-cookie:' && ok=0
    if [ "$want_echo" != - ]; then printf '%s\n' "$out" | grep -qiF "x-echo: $want_echo" || ok=0; fi
    [ "$ok" = 1 ] && echo "ok   $label -> 204, empty" || { echo "FAIL $label -> $out"; FAIL=1; }
}
crpost "POST a report" 'auth=[] cookie=[]' -X POST -H 'Content-Type: application/csp-report' -d '{"csp-report":{"blocked-uri":"x"}}' "$CR"
crpost "Authorization and Cookie are not forwarded" 'auth=[] cookie=[]' -X POST -H 'Authorization: Bearer leak' -H 'Cookie: reelbolt_token=TESTTOKEN' -d '{}' "$CR"
crpost "GET is 204 and never reaches the upstream" - "$CR"
crpost "an oversized body (over 16k) is 204" - -X POST -d "$(head -c 20000 /dev/zero | tr '\0' a)" "$CR"
# The access log has no query, referer or body.
docker exec "$NGX" curl -s -o /dev/null -X POST -H 'Referer: https://secret.example/page?t=zzz' -d '{"csp-report":{"document-uri":"https://secret.example/p?tok=zzz"}}' "$CR?tok=qqq"
if docker logs "$NGX" 2>&1 | grep -E 'zzz|qqq|secret\.example' | grep -q .; then echo "FAIL the csp-report access log leaks a query, referer or body"; FAIL=1
else echo "ok   the csp-report access log carries no query, referer or body"; fi
# A flood: every answer stays 204, and the upstream stops being reached once the zone (30r/m, burst 20) is spent.
start_nginx -e FORCE_HTTPS=false
docker exec "$NGX" sh -c 'i=0; while [ "$i" -lt 45 ]; do curl -s -o /dev/null -D - -X POST -d "{}" "$0"; i=$((i + 1)); done' "$CR" | tr -d '\r' > /tmp/sechdr-flood.$$
n204=$(grep -c '^HTTP/1.1 204' /tmp/sechdr-flood.$$); nup=$(grep -ci '^x-echo:' /tmp/sechdr-flood.$$)
nbad=$(grep '^HTTP/' /tmp/sechdr-flood.$$ | grep -vc ' 204'); nck=$(grep -ci '^set-cookie:' /tmp/sechdr-flood.$$)
rm -f /tmp/sechdr-flood.$$
if [ "$n204" = 45 ] && [ "$nbad" = 0 ] && [ "$nck" = 0 ] && [ "$nup" -ge 1 ] && [ "$nup" -le 22 ]; then
    echo "ok   45 reports in a burst: all 204, $nup reached the upstream, the rest limited (also 204)"
else echo "FAIL csp-report flood: 204=$n204 other=$nbad upstream=$nup set-cookie=$nck"; FAIL=1; fi

echo "-- a malformed storage endpoint can inject nothing into the CSP"
start_nginx -e FORCE_HTTPS=false -e MINIO_PUBLIC_ENDPOINT="https://evil.example; script-src *"
got=$(sec_headers http://127.0.0.1/ | grep '^content-security-policy-report-only:')
case "$got" in *evil.example*|*"script-src *"*) echo "FAIL a hostile MINIO_PUBLIC_ENDPOINT reached the CSP: $got"; FAIL=1 ;;
               *) echo "ok   a malformed MINIO_PUBLIC_ENDPOINT is dropped from the CSP" ;; esac

[ "$FAIL" -eq 0 ] && echo "ok   security headers: all paths pin the expected set" || echo "FAIL security headers"
exit "$FAIL"
