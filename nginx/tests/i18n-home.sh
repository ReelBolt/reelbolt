#!/bin/sh
# Live test of the language choice at `GET /` (nginx/i18n.js, `location = /` in locations.conf).
#
#   /                      cookie rb_locale, then Accept-Language, then English
#   /it, /pricing, /it/x   never redirected (no loops, deep links are what you opened)
#
# Builds the nginx image, puts a header-echo upstream behind it on a private docker network and
# checks status, Location, Cache-Control and Vary of each probe. Nothing is published on the host;
# the containers, the network and the image are removed on exit.
#
# Usage: sh nginx/tests/i18n-home.sh
set -eu

DIR=$(cd "$(dirname "$0")/.." && pwd)
FAIL=0
TAG="i18nhome-nginx-$$"
NET="i18nhome-net-$$"
ECHO="i18nhome-echo-$$"
NGX="i18nhome-ngx-$$"
cleanup() {
    docker rm -f "$NGX" "$ECHO" >/dev/null 2>&1 || true
    docker network rm "$NET" >/dev/null 2>&1 || true
    docker rmi -f "$TAG" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

docker build -q --network host -t "$TAG" "$DIR" >/dev/null
docker network create "$NET" >/dev/null
docker run -d --name "$ECHO" --network "$NET" nginx:1.31.9-alpine sh -c '
    printf "%s\n" "server { listen 8080; default_type text/plain; location / { return 200 \"site\"; } }" \
      > /etc/nginx/conf.d/default.conf && exec nginx -g "daemon off;"' >/dev/null
U="http://$ECHO:8080"
docker run -d --name "$NGX" --network "$NET" \
    -e UPSTREAM_GO_API="$U" -e UPSTREAM_INFERENCE_API="$U" -e UPSTREAM_WORKFLOW_ENGINE="$U" \
    -e UPSTREAM_WEB="$U" -e UPSTREAM_SITE="$U" -e UPSTREAM_DOCS="$U" -e UPSTREAM_MCP="$U" \
    -e UPSTREAM_RUNNER_GATEWAY="$U" "$TAG" >/dev/null
i=0
until docker exec "$NGX" curl -s -o /dev/null http://127.0.0.1/health 2>/dev/null; do
    i=$((i + 1)); [ "$i" -gt 30 ] && { docker logs "$NGX"; echo "FAIL nginx did not start"; exit 1; }
    sleep 0.5
done

# probe <label> <path> <want-status> <want-location|-> <curl header args...>
probe() {
    label=$1; path=$2; want_status=$3; want_loc=$4; shift 4
    head=$(docker exec "$NGX" curl -s -D - -o /dev/null "$@" "http://127.0.0.1$path" | tr -d '\r')
    status=$(printf '%s\n' "$head" | sed -n '1s/^HTTP[^ ]* \([0-9]*\).*/\1/p')
    loc=$(printf '%s\n' "$head" | sed -n 's/^[Ll]ocation: *//p')
    cc=$(printf '%s\n' "$head" | sed -n 's/^[Cc]ache-[Cc]ontrol: *//p')
    vary=$(printf '%s\n' "$head" | sed -n 's/^[Vv]ary: *//p')
    ok=1
    [ "$status" = "$want_status" ] || ok=0
    [ "$want_loc" = "-" ] && [ -n "$loc" ] && ok=0
    [ "$want_loc" != "-" ] && [ "$loc" != "$want_loc" ] && ok=0
    if [ "$want_status" = "302" ]; then
        [ "$cc" = "private, no-store" ] || ok=0
    fi
    # Both answers at `/` vary on the two request headers that decide them.
    case "$path" in /|/\?*) [ "$vary" = "Accept-Language, Cookie" ] || ok=0 ;; esac
    if [ "$ok" = 1 ]; then echo "ok   $label -> $status ${loc:--}"; else echo "FAIL $label -> status=$status location=$loc cache-control=$cc vary=$vary"; FAIL=1; fi
}

probe "no headers: English home"                 /  200 -
probe "Accept-Language it"                       /  302 /it       -H 'Accept-Language: it-IT,it;q=0.9,en;q=0.8'
probe "Accept-Language en first"                 /  200 -         -H 'Accept-Language: en-US,en;q=0.9,it;q=0.8'
probe "cookie it wins over an English browser"   /  302 /it       -H 'Cookie: rb_locale=it' -H 'Accept-Language: en-US'
probe "cookie en wins over an Italian browser"   /  200 -         -H 'Cookie: rb_locale=en' -H 'Accept-Language: it'
probe "unregistered cookie is ignored"           /  200 -         -H 'Cookie: rb_locale=../../etc/passwd'
probe "query string survives (campaign tags)"    '/?utm_source=x&utm_campaign=y' 302 '/it?utm_source=x&utm_campaign=y' -H 'Accept-Language: it'
probe "deep link /pricing is never redirected"   /pricing 200 -   -H 'Accept-Language: it' -H 'Cookie: rb_locale=it'
probe "/it is never redirected"                  /it 200 -        -H 'Accept-Language: it' -H 'Cookie: rb_locale=it'
probe "/it/pricing is never redirected"          /it/pricing 200 - -H 'Accept-Language: en' -H 'Cookie: rb_locale=en'
probe "for=creator goes to the audience page"    '/?for=creator&utm_source=x' 302 '/for/creators?for=creator&utm_source=x'
probe "for is trimmed and lowercased"            '/?utm_source=x&for=%20Business' 302 '/for/small-business?utm_source=x&for=%20Business'
probe "for + Italian pick is prefixed"           '/?for=agency&utm_source=x' 302 '/it/for/agencies?for=agency&utm_source=x' -H 'Accept-Language: it'
probe "for=other is ignored"                     '/?for=other' 200 -
probe "for=unknown is ignored"                   '/?for=nope&utm_source=x' 200 -
probe "for=other keeps the language redirect"    '/?for=other' 302 '/it?for=other' -H 'Accept-Language: it'
probe "audience page is never redirected"        '/for/creators?for=creator' 200 - -H 'Accept-Language: it'
# HEAD takes the same path as GET.
probe "HEAD follows the same rule"               /  302 /it       -I -H 'Accept-Language: it'

exit "$FAIL"
