#!/bin/sh
# The deploy upgrade path: release.sh RELOADS the running nginx (it is never recycled), so the container
# still holds the render-config.sh/reload-config.sh and snippets of the image it started from. A new
# nginx.conf (copied from nginx-src/current by the OLD renderer) that includes a file only the NEW renderer
# delivers fails `nginx -t`. #1450 hit this in production (open() "/etc/nginx/conf.d/security-storage.conf"
# failed); the switch rolled back safely. This test is the reproduction and the proof of the fix:
#   1. static: nginx.conf includes no rendered file the old renderer lacks except through a glob.
#   2. live: a container whose scripts are the pre-#1450 ones (fixtures/pre-1450/render-config.sh) and which
#      lacks the new snippet. The PLAIN reload fails and rolls back (the production failure), then
#      release.sh's deliver_nginx_runtime + reload-config.sh succeeds and serves the full header set.
# Usage: sh nginx/tests/upgrade-reload.sh [--static-only]
set -eu
DIR=$(cd "$(dirname "$0")/.." && pwd)
REL="$DIR/../infra/scripts/release.sh"
OLD="$DIR/tests/fixtures/pre-1450/render-config.sh"
E=0
fail() { echo "FAIL $*"; E=1; }

echo "-- static"
# Every include of a rendered conf.d file in nginx.conf must be a glob (tolerates the file being absent).
if grep -Ev '^[[:space:]]*#' "$DIR/nginx.conf" | grep -E 'include[[:space:]]+/etc/nginx/conf\.d/' | grep -v '\*' \
   | grep -vE 'conf\.d/(realip|http-server|https-server|runner-server)\.conf;' | grep -q .; then
    fail "nginx.conf includes a rendered conf.d file by exact name that the previous renderer does not write"
fi
grep -Eq 'include /etc/nginx/conf\.d/security-storage\*\.map;' "$DIR/nginx.conf" || fail "sh_csp_storage map lacks the glob include"
sed -n '/map \$uri \$sh_csp_storage {/,/}/p' "$DIR/nginx.conf" | grep -Eq '^[[:space:]]*default[[:space:]]' \
    && fail "nginx.conf gives sh_csp_storage a default (it comes from the rendered map)"
grep -q 'deliver_nginx_runtime' "$REL" || fail "release.sh has no deliver_nginx_runtime"
[ "$E" = 0 ] && echo "ok static"
[ "$E" = 0 ] || exit 1
[ "${1:-}" = "--static-only" ] && exit 0

W=$(mktemp -d); TAG="upgrade-reload-$$"; NET="upgrade-reload-net-$$"; ECHO="upgrade-echo-$$"; NGX="upgrade-ngx-$$"
cleanup() { docker rm -f "$NGX" "$ECHO" >/dev/null 2>&1 || true; docker network rm "$NET" >/dev/null 2>&1 || true
            docker rmi -f "$TAG" >/dev/null 2>&1 || true; rm -rf "$W"; }
trap cleanup EXIT INT TERM

docker build -q --network host -t "$TAG" "$DIR" >/dev/null
docker network create "$NET" >/dev/null
docker run -d --name "$ECHO" --network "$NET" nginx:1.31.9-alpine sh -c '
    printf "%s\n" "server { listen 8080; default_type text/plain; location / { return 200 ok; } }" > /etc/nginx/conf.d/default.conf
    exec nginx -g "daemon off;"' >/dev/null

# The deploy state exactly as release.sh lays it out: nginx-src/<sha> + current symlink, upstreams.env.
mkdir -p "$W/deploy/nginx-src/abc123"
cp -R "$DIR/." "$W/deploy/nginx-src/abc123/" && rm -rf "$W/deploy/nginx-src/abc123/tests"
ln -s abc123 "$W/deploy/nginx-src/current"
U="http://$ECHO:8080"
for k in INFERENCE_API GO_API MCP WEB SITE DOCS; do printf 'UPSTREAM_%s=%s\n' "$k" "$U"; done > "$W/deploy/upstreams.env"

docker run -d --name "$NGX" --network "$NET" -v "$W/deploy:/etc/nginx/reelbolt-deploy:ro" \
    -e FORCE_HTTPS=true -e MINIO_PUBLIC_ENDPOINT=https://s3.example.com/bucket \
    -e UPSTREAM_WORKFLOW_ENGINE="$U" "$TAG" >/dev/null
i=0; until docker exec "$NGX" curl -sk -o /dev/null https://127.0.0.1/health 2>/dev/null; do
    i=$((i + 1)); [ "$i" -gt 40 ] && { docker logs "$NGX"; echo "FAIL nginx did not start"; exit 1; }; sleep 0.5; done

# Become the OLD container: pre-#1450 renderer, no snippet, no rendered storage file.
docker cp "$OLD" "$NGX:/etc/nginx/render-config.sh"
docker exec "$NGX" sh -c 'chmod +x /etc/nginx/render-config.sh; rm -f /etc/nginx/security-headers.conf /etc/nginx/conf.d/security-storage*'
BEFORE=$(docker exec "$NGX" sh -c 'cat /etc/nginx/nginx.conf | md5sum')

echo "-- reproduction: the plain reload with the old scripts fails and rolls back"
if docker exec "$NGX" /etc/nginx/reload-config.sh >"$W/plain.log" 2>&1; then fail "plain old-script reload unexpectedly succeeded"; cat "$W/plain.log"; fi
grep -q 'nginx -t failed' "$W/plain.log" || { fail "plain reload did not fail at nginx -t"; cat "$W/plain.log"; }
grep -q 'security-headers.conf' "$W/plain.log" || fail "failure is not the missing snippet"
[ "$(docker exec "$NGX" sh -c 'cat /etc/nginx/nginx.conf | md5sum')" = "$BEFORE" ] || fail "rollback did not restore nginx.conf"
docker exec "$NGX" curl -sk -o /dev/null -w '%{http_code}' https://127.0.0.1/health | grep -q 200 || fail "nginx stopped serving after the failed reload"

echo "-- fix: deliver_nginx_runtime, then reload-config.sh"
eval "$(sed -n '/^NGINX_RUNTIME_FILES=/p; /^deliver_nginx_runtime()/,/^}/p' "$REL")"
log() { echo "[release] $*"; }
# shellcheck disable=SC2034  # read by deliver_nginx_runtime, which is defined above by eval from release.sh
NGINX_SRC="$W/deploy/nginx-src"
deliver_nginx_runtime "$NGX" || fail "deliver_nginx_runtime failed"
docker exec "$NGX" /etc/nginx/reload-config.sh >"$W/fix.log" 2>&1 || { fail "reload after delivery failed"; cat "$W/fix.log"; }
docker exec "$NGX" nginx -t >/dev/null 2>&1 || fail "nginx -t fails after delivery"
H=$(docker exec "$NGX" curl -sk -o /dev/null -D - https://127.0.0.1/ | tr -d '\r' | tr 'A-Z' 'a-z')
for h in content-security-policy-report-only x-frame-options cross-origin-opener-policy; do
    printf '%s\n' "$H" | grep -q "^$h:" || fail "missing $h after the upgrade reload"
done
[ "$(printf '%s\n' "$H" | grep -c '^x-content-type-options: nosniff')" = 1 ] || fail "nosniff not exactly once"
printf '%s\n' "$H" | grep '^strict-transport-security:' | grep -q includesubdomains || fail "HSTS lacks includeSubDomains"
printf '%s\n' "$H" | grep '^content-security-policy-report-only:' | grep -q "media-src 'self' blob: https://cdn.reelbolt.ai https://s3.example.com;" \
    || fail "MINIO_PUBLIC_ENDPOINT not in media-src via the .map"
docker exec "$NGX" test -f /etc/nginx/conf.d/security-storage.map || fail "security-storage.map not rendered"

echo "-- tolerance: no rendered map at all still passes nginx -t (empty glob)"
docker exec "$NGX" rm -f /etc/nginx/conf.d/security-storage.map
docker exec "$NGX" nginx -t >/dev/null 2>&1 || fail "nginx -t fails without security-storage.map"

[ "$E" = 0 ] && echo "ok upgrade-reload" || exit 1
