#!/bin/sh
# Regression test: a client must not be able to choose the address an upstream sees.
#
# nginx drops EVERY inherited proxy_set_header as soon as a location sets one of its own, so a
# location that only blanks or translates Authorization used to forward a client-supplied
# X-Real-IP unchanged (and no X-Forwarded-* from nginx at all). The Inference API records
# X-Real-IP as marketing-consent evidence, so that was a spoofable audit trail.
#
# The fix must not change the Host any upstream receives: the shared snippet carries only the
# client-address headers, and Host stays exactly where it is pinned below (server blocks, the three
# marketing locations and the visit beacon set `Host $host`; every other location with its own
# headers sends nginx's default, the upstream's name). The visit beacon joined that list with the
# new-user funnel: the API recognises a self-referral by the public hostname it is called on.
#
#   1. Static: every location block that sets a proxy_set_header must also include
#      /etc/nginx/proxy-client-headers.conf (or set all three client-address headers itself);
#      the snippet must not set Host; the server blocks must set Host $host; in locations.conf
#      only the three marketing locations and the visit beacon may set Host.
#   2. Live (skip with --static-only): build the image, put a header-echo upstream behind it,
#      send `X-Real-IP: 6.6.6.6` to a set of locations and require the upstream to see the real
#      client address and the same Host as before the fix. Repeated with the basic-auth front
#      door on and with the Cloudflare real-IP ranges loaded. Also: case/trailing-slash variants
#      of the marketing paths share the auth_public limit (S-05); a peer inside a Cloudflare
#      range has CF-Connecting-IP honoured (N-09); per-IP zones key IPv6 on the /64 (N-04);
#      a tracked link /l/{code} reaches the API's /api/v1/l/{code} anonymously and never ends on
#      an error page; the API's own path /api/v1/l/{code} is the same location with the same limit,
#      and only a request carrying the right X-ReelBolt-Site-Token (the site's relay) uses the
#      looser link_relay zone.
#      Nothing is published on the host; every container, network and the image are removed on
#      exit. CI runs both halves (.github/workflows/ci.yml, job nginx-live).
#
# Usage: sh nginx/tests/forwarded-headers.sh [--static-only]
set -eu

DIR=$(cd "$(dirname "$0")/.." && pwd)
FAIL=0

# --- 1. static --------------------------------------------------------------------------------
check_file() {
    awk -v file="$1" '
        function flush() {
            if (inloc && sets && !inc && have != 3) {
                printf "FAIL %s:%d location %s sets proxy_set_header without the client-address headers\n", file, start, name
                bad = 1
            }
        }
        /^[ \t]*#/ { next }
        !inloc && /^[ \t]*location[ \t]/ {
            inloc = 1; depth = 0; sets = 0; inc = 0; have = 0; start = NR
            name = $0; sub(/^[ \t]*location[ \t]+/, "", name); sub(/[ \t]*\{.*$/, "", name)
            delete seen
        }
        inloc {
            if ($0 ~ /proxy_set_header/) sets = 1
            if ($0 ~ /include[ \t]+\/etc\/nginx\/proxy-client-headers\.conf;/) inc = 1
            if ($0 ~ /proxy_set_header[ \t]+X-Real-IP[ \t]+\$remote_addr;/ && !seen["r"]++) have++
            if ($0 ~ /proxy_set_header[ \t]+X-Forwarded-For[ \t]+\$proxy_add_x_forwarded_for;/ && !seen["f"]++) have++
            if ($0 ~ /proxy_set_header[ \t]+X-Forwarded-Proto[ \t]+\$scheme;/ && !seen["p"]++) have++
            n = gsub(/\{/, "{"); depth += n
            n = gsub(/\}/, "}"); depth -= n
            if (depth <= 0 && $0 ~ /\}/) { flush(); inloc = 0 }
        }
        END { exit bad }
    ' "$1"
}

for f in locations.conf runner-locations.conf.template runner-server.conf.template \
         http-server-redirect.conf http-server-dev.conf https-server.conf.template; do
    check_file "$DIR/$f" || FAIL=1
done
SNIP="$DIR/proxy-client-headers.conf"
if [ -f "$SNIP" ]; then
    for h in 'X-Real-IP $remote_addr;' 'X-Forwarded-For $proxy_add_x_forwarded_for;' \
             'X-Forwarded-Proto $scheme;'; do
        grep -qF "proxy_set_header $h" "$SNIP" \
            || { echo "FAIL proxy-client-headers.conf lacks: proxy_set_header $h"; FAIL=1; }
    done
    grep -Eq '^[[:space:]]*proxy_set_header[[:space:]]+Host' "$SNIP" \
        && { echo "FAIL proxy-client-headers.conf sets Host (it would change the Host upstreams see)"; FAIL=1; }
    grep -q 'proxy-client-headers.conf' "$DIR/Dockerfile" \
        || { echo "FAIL Dockerfile does not copy proxy-client-headers.conf into the image"; FAIL=1; }
    if [ -f "$DIR/../docker-compose.yml" ]; then
        grep -qF './nginx/proxy-client-headers.conf:/etc/nginx/proxy-client-headers.conf:ro' \
            "$DIR/../docker-compose.yml" \
            || { echo "FAIL docker-compose.yml does not bind-mount proxy-client-headers.conf"; FAIL=1; }
    fi
else
    echo "FAIL nginx/proxy-client-headers.conf is missing"; FAIL=1
fi
for f in http-server-dev.conf https-server.conf.template; do
    grep -Eq '^    proxy_set_header Host \$host;' "$DIR/$f" \
        || { echo "FAIL $f: the server block no longer sets Host \$host"; FAIL=1; }
done
# Host per location is pinned: only these locations set it. The three marketing locations always
# did; the visit beacon does since the new-user funnel (docs/design/new-user-funnel.md), because the
# API drops a self-referral by comparing against the hostname it was called on, and a location with
# headers of its own would otherwise send it the upstream's name.
hosts=$(awk '/^[ \t]*#/ { next } /^location[ \t]/ { name = $0 }
             /proxy_set_header[ \t]+Host[ \t]/ { print name }' "$DIR/locations.conf" | tr '\n' '|')
want='location ~* ^/api/v1/marketing/subscribe(/|$) {|location ~* ^/api/v1/marketing/confirm(/|$) {|location ~* ^/api/v1/marketing/unsubscribe(/|$) {|location ~* ^/api/v1/telemetry/visit(/|$) {|'
[ "$hosts" = "$want" ] || { echo "FAIL locations.conf: Host is set in [$hosts], expected only the three marketing locations and the visit beacon"; FAIL=1; }
[ "$FAIL" -eq 0 ] && echo "ok   static: every location that sets proxy_set_header forwards the client address; Host set only where pinned"

# Rate limits on the subscribe forward (F-1): a request carrying X-ReelBolt-Site-Token (the
# marketing site's server, few source addresses) uses the looser site_forward zone on
# /api/v1/marketing/subscribe only; everything else keeps auth_public exactly as before.
RL=0
NC="$DIR/nginx.conf"
grep -qF 'map $http_x_reelbolt_site_token $site_forward_request {' "$NC" \
    || { echo "FAIL nginx.conf: no map on \$http_x_reelbolt_site_token"; RL=1; }
grep -qF 'limit_req_zone $auth_public_key zone=auth_public:10m rate=5r/m;' "$NC" \
    || { echo "FAIL nginx.conf: auth_public must stay 5r/m, keyed on \$auth_public_key"; RL=1; }
grep -qF 'limit_req_zone $site_forward_key zone=site_forward:10m rate=60r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no site_forward zone (60r/m on \$site_forward_key)"; RL=1; }
# The marketing site's own form endpoints (site container, outside /api/v1/) carry their own
# per-address zones, keyed on $client_ip_key. site_forms is the closest expressible form of the
# 5-requests-per-600-seconds the Cloudflare rule specifies (limit_req takes an integer r/s or r/m
# only, and 1r/m is the slowest rate it can express); site_promotions keeps the generous rate
# docs/marketing-site.md step 9 gives the read-only proxy, which the promo banner fetches once per
# page load. The live half proves both limits fire.
grep -qF 'limit_req_zone $client_ip_key zone=site_forms:10m rate=1r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no site_forms zone (1r/m on \$client_ip_key)"; RL=1; }
grep -qF 'limit_req_zone $client_ip_key zone=site_promotions:10m rate=100r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no site_promotions zone (100r/m on \$client_ip_key)"; RL=1; }
# map_body <var>: the entries of `map ... $<var> {`, one per line, whitespace squeezed.
map_body() {
    awk -v v="$1" '/^[ \t]*#/ { next }
        $0 ~ "^[ \t]*map[ \t].*[ \t]\\$" v "[ \t]*\\{" { on = 1; next }
        on && /\}/ { on = 0 }
        on { $1 = $1; print }' "$NC" | tr '\n' '|'
}
want='"~*^1/api/v1/marketing/subscribe(/|$)" "";|default $client_ip_key;|'
[ "$(map_body auth_public_key)" = "$want" ] \
    || { echo "FAIL nginx.conf: \$auth_public_key may only drop the header-carrying subscribe request [$(map_body auth_public_key)]"; RL=1; }
want='"~*^1/api/v1/marketing/subscribe(/|$)" $client_ip_key;|"~*^1/api/v1/billing/promotions/(active|plan-prices)(/|$)" $client_ip_key;|default "";|'
[ "$(map_body site_forward_key)" = "$want" ] \
    || { echo "FAIL nginx.conf: \$site_forward_key must key only the header-carrying subscribe request [$(map_body site_forward_key)]"; RL=1; }
# Which locations use which zone: site_forward only on subscribe, auth_public where it always was.
zones=$(awk '/^[ \t]*#/ { next } /^location[ \t]/ { name = $0 }
             /^[ \t]*limit_req[ \t]/ { $1 = $1; sub(/^location (= |~\* )/, "", name); sub(/ \{.*/, "", name)
                                       print name " " $0 }' "$DIR/locations.conf" | tr '\n' '|')
A='limit_req zone=auth_public burst=5 nodelay;'
S='limit_req zone=site_forward burst=20 nodelay;'
M='^/api/v1/marketing'
# Pricing-page promotions reads: promo_public without the site token, site_forward with it.
B='^/api/v1/billing/promotions/(active|plan-prices)(/|$)'
P='limit_req zone=promo_public burst=30 nodelay;'
# Anonymous output links: dedicated per-IP zone (a media player issues many Range requests).
O='^/api/v1/output-links/'
OL='limit_req zone=output_links burst=60 nodelay;'
# Anonymous guest review links: reads share a per-IP zone, POSTs also pass the write zone (keyed on POST only).
R='^/api/v1/review-links/'
RR='limit_req zone=review_links burst=60 nodelay;'
RW='limit_req zone=review_links_write burst=10 nodelay;'
# Anonymous recipe marketplace: media (preview/poster) first, then every other read, own per-IP zones.
MM='^/api/v1/public/marketplace/recipes/[^/]+/(preview|poster)/?$'
MMR='limit_req zone=marketplace_media burst=60 nodelay;'
ML='^/api/v1/public/marketplace/'
MLR='limit_req zone=marketplace_list burst=30 nodelay;'
# Authenticated client telemetry ingest: dedicated per-IP zone (Security finding 2), a
# case-insensitive regex like the marketing locations so no case variant skips it (S-05).
T='^/api/v1/telemetry/'
TL='limit_req zone=telemetry_ingest burst=60 nodelay;'
S2='limit_req zone=site_forward burst=20 nodelay;'
# The marketing site's own form endpoints (the `site` container, outside /api/v1/): their own
# per-address limit. This string is the WHOLE set of limit_req lines in locations.conf, so any
# other location gaining or losing one fails here.
F='limit_req zone=site_forms burst=4 nodelay;'
PR='limit_req zone=site_promotions burst=100 nodelay;'
CT='^/api/contact(/|$)'
SU='^/api/subscribe(/|$)'
PM='^/api/promotions(/|$)'
# Tracked links, /l/{code} AND the API's own /api/v1/l/{code}: one location, own per-IP zone, plus the
# verified-relay zone (exactly one applies per request). The regex is quoted in locations.conf (braces).
LK='"^/(?:api/v1/)?l/(?<link_code>[a-z0-9][a-z0-9-]{1,39})/?$"'
LKR='limit_req zone=link_redirect burst=40 nodelay;'
LKL='limit_req zone=link_relay burst=400 nodelay;'
want="/api/v1/auth/token $A|/api/v1/auth/signup $A|/api/v1/auth/forgot-password $A|/api/v1/auth/resend-verification $A|location ~ ^/api/v1/auth/email-change/(preview|confirm|cancel)\$ $A|$M/subscribe(/|\$) $A|$M/subscribe(/|\$) $S|$M/confirm(/|\$) $A|$M/unsubscribe(/|\$) $A|$M/webhooks/brevo(/|\$) $TL|$B $P|$B $S2|$O $OL|$R $RR|$R $RW|^/api/public/ limit_req zone=public_api burst=200 nodelay;|$MM $MMR|$ML $MLR|^/api/v1/telemetry/visit(/|\$) limit_req zone=telemetry_visit burst=20 nodelay;|$T $TL|location ^~ /app/review/g/ $RR|$LK $LKR|$LK $LKL|$CT $F|$SU $F|$PM $PR|location /.well-known/oauth-protected-resource limit_req zone=mcp burst=200 nodelay;|/oauth/register $A|location /mcp limit_req zone=mcp burst=200 nodelay;|"
[ "$zones" = "$want" ] || { echo "FAIL locations.conf: limit_req lines are [$zones], expected [$want]"; RL=1; }
# S-05: the Inference API routes case-insensitively and tolerates a trailing slash, so the marketing
# locations are case-insensitive regexes (the live half proves the variants share the limit). An
# exact-match marketing location would let /api/v1/marketing/Subscribe fall through to /api/v1/.
grep -Eq '^[[:space:]]*location[[:space:]]+=[[:space:]]+/api/v1/marketing/' "$DIR/locations.conf" \
    && { echo "FAIL locations.conf: exact-match marketing location (case/trailing-slash variants bypass its limit)"; RL=1; }
# The telemetry ingest is authenticated and best-effort: the location must translate the session
# cookie exactly as the generic /api/v1/ block does (Authorization $auth_header), keep the client
# address headers, and reach the Inference API -- and be a case-insensitive regex, because the
# Inference API routes case-insensitively, so an exact or case-sensitive match would let a case
# variant fall through to /api/v1/ and skip the zone (S-05).
tel="$(awk 'index($0, "location ~* ^/api/v1/telemetry/ {") { on = 1 }
            on { print } on && /\}/ { exit }' "$DIR/locations.conf")"
printf '%s\n' "$tel" | grep -qF 'proxy_set_header Authorization $auth_header;' \
    || { echo "FAIL locations.conf: telemetry location does not translate the session cookie"; RL=1; }
printf '%s\n' "$tel" | grep -qF 'include /etc/nginx/proxy-client-headers.conf;' \
    || { echo "FAIL locations.conf: telemetry location drops the client-address headers"; RL=1; }
printf '%s\n' "$tel" | grep -qF 'proxy_pass $inference_api;' \
    || { echo "FAIL locations.conf: telemetry location is not proxied to the Inference API"; RL=1; }
grep -Eq '^[[:space:]]*location[[:space:]]+=[[:space:]]+/api/v1/telemetry/' "$DIR/locations.conf" \
    && { echo "FAIL locations.conf: exact-match telemetry location (case/trailing-slash variants bypass its limit)"; RL=1; }
# Marketplace: zones on $client_ip_key; both locations anonymous (no basic auth, Authorization and Cookie blanked),
# GET/HEAD only, and the upstream Cache-Control: public must not be stripped or cached over.
grep -qF 'limit_req_zone $client_ip_key zone=marketplace_list:10m rate=60r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no marketplace_list zone (60r/m on \$client_ip_key)"; RL=1; }
grep -qF 'limit_req_zone $client_ip_key zone=marketplace_media:10m rate=120r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no marketplace_media zone (120r/m on \$client_ip_key)"; RL=1; }
for mp in 'location ~* ^/api/v1/public/marketplace/recipes/[^/]+/(preview|poster)/?$ {' 'location ~* ^/api/v1/public/marketplace/ {'; do
    mb="$(awk -v pat="$mp" 'index($0, pat) == 1 { on = 1 } on { print } on && /^\}/ { exit }' "$DIR/locations.conf")"
    [ -n "$mb" ] || { echo "FAIL locations.conf: no [$mp]"; RL=1; continue; }
    for need in 'auth_basic off;' 'proxy_set_header Authorization "";' 'proxy_set_header Cookie "";' \
                'limit_except GET HEAD { deny all; }' 'include /etc/nginx/proxy-client-headers.conf;' 'proxy_pass $inference_api;'; do
        printf '%s\n' "$mb" | grep -qF "$need" || { echo "FAIL locations.conf: [$mp] lacks [$need]"; RL=1; }
    done
    printf '%s\n' "$mb" | grep -Eq 'proxy_hide_header|proxy_ignore_headers|expires|add_header' \
        && { echo "FAIL locations.conf: [$mp] touches the upstream cache headers"; RL=1; }
done
# The anonymous prefix always carries /public/: the authenticated /api/v1/marketplace/ never matches it.
grep -E '^location .*marketplace' "$DIR/locations.conf" | grep -vq '/api/v1/public/marketplace/' \
    && { echo "FAIL locations.conf: a marketplace location outside the anonymous /public/ prefix"; RL=1; }
# The visit beacon stays anonymous and keeps the client address headers now that it also sets Host.
vis="$(awk 'index($0, "location ~* ^/api/v1/telemetry/visit(/|$) {") == 1 { on = 1 } on { print } on && /^\}/ { exit }' "$DIR/locations.conf")"
for need in 'proxy_set_header Host $host;' 'proxy_set_header Authorization "";' 'auth_basic off;' \
            'include /etc/nginx/proxy-client-headers.conf;' 'client_max_body_size 2k;' 'proxy_pass $inference_api;'; do
    printf '%s\n' "$vis" | grep -qF "$need" || { echo "FAIL locations.conf: the visit beacon location lacks [$need]"; RL=1; }
done
# Tracked links (docs/design/new-user-funnel.md): /l/{code} -> the Inference API's /api/v1/l/{code}. Own zone
# per address; anonymous (no basic auth, Authorization and Cookie blanked), GET/HEAD only, client address
# headers kept; the code is a NAMED capture (for an IPv6 client the zone key's regex map overwrites $1 before
# proxy_pass reads it, and the upstream path would be built from bytes of the address);
# an API that cannot answer (or answers too slowly), and a request over the limit, end on the home page with
# the link code as utm_content, and not on an error page.
#
# The API's own path is served by the SAME location. It used to fall to the generic /api/v1/ block, which has
# no limit, takes every method and forwards the session: every request there is one more click on a counter,
# so the longer address was a way around link_redirect. One location for both paths makes that impossible to
# reintroduce by editing one of them.
#
# The site's relay (the Cloudflare Pages Function, few egress addresses) uses link_relay instead, and only
# when its X-ReelBolt-Site-Token equals this container's SITE_FORWARD_TOKEN: checked in auth.js, because
# nothing behind this location limits per caller. An unchecked header would be the next way around.
grep -qF 'limit_req_zone $link_redirect_key zone=link_redirect:10m rate=120r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no link_redirect zone (120r/m on \$link_redirect_key)"; RL=1; }
grep -qF 'limit_req_zone $link_relay_key zone=link_relay:10m rate=1200r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no link_relay zone (1200r/m on \$link_relay_key)"; RL=1; }
grep -Eq '^env SITE_FORWARD_TOKEN;' "$NC" \
    || { echo "FAIL nginx.conf: SITE_FORWARD_TOKEN is not kept in nginx's environment (env directive, main context)"; RL=1; }
grep -qF 'js_set $site_relay_token_ok auth.siteRelayVerified;' "$NC" \
    || { echo "FAIL nginx.conf: the relay token is not checked by auth.siteRelayVerified"; RL=1; }
# No header: never the relay, and njs is not run. Any header: only what auth.js verified.
want='"" "";|default $site_relay_token_ok;|'
[ "$(map_body site_relay_verified)" = "$want" ] \
    || { echo "FAIL nginx.conf: \$site_relay_verified must be \"\" without the header and the checked result with it [$(map_body site_relay_verified)]"; RL=1; }
want='"1" "";|default $client_ip_key;|'
[ "$(map_body link_redirect_key)" = "$want" ] \
    || { echo "FAIL nginx.conf: \$link_redirect_key may only drop the verified relay [$(map_body link_redirect_key)]"; RL=1; }
want='"1" $client_ip_key;|default "";|'
[ "$(map_body link_relay_key)" = "$want" ] \
    || { echo "FAIL nginx.conf: \$link_relay_key must key only the verified relay [$(map_body link_relay_key)]"; RL=1; }
# The check itself: the secret comes from the environment, an unset one verifies nobody, and the
# comparison does not stop at the first different character.
AJ="$DIR/auth.js"
rv="$(awk '/^function siteRelayVerified\(/ { on = 1 } on { print } on && /^\}/ { exit }' "$AJ")"
[ -n "$rv" ] || { echo "FAIL auth.js: no siteRelayVerified"; RL=1; }
for need in 'process.env.SITE_FORWARD_TOKEN' "r.headersIn['X-ReelBolt-Site-Token']" "if (!expected || !sent) return '';" \
            "return sameSecret(sent, expected) ? '1' : '';"; do
    printf '%s\n' "$rv" | grep -qF "$need" || { echo "FAIL auth.js: siteRelayVerified lacks [$need]"; RL=1; }
done
printf '%s\n' "$rv" | grep -Eq '===[[:space:]]*expected|expected[[:space:]]*===|indexOf|startsWith' \
    && { echo "FAIL auth.js: siteRelayVerified compares the secret with an early-exit comparison"; RL=1; }
grep -Eq '^export default \{.*siteRelayVerified.*\};' "$AJ" \
    || { echo "FAIL auth.js: siteRelayVerified is not exported"; RL=1; }
# The token header is read by the two maps and by nothing else: a log format naming it would record the secret.
grep -v '^[[:space:]]*#' "$NC" | grep 'http_x_reelbolt_site_token' | grep -vq '^[[:space:]]*map ' \
    && { echo "FAIL nginx.conf: \$http_x_reelbolt_site_token is used outside the two maps (a log format would record the secret)"; RL=1; }

lkp='location ~* "^/(?:api/v1/)?l/(?<link_code>[a-z0-9][a-z0-9-]{1,39})/?$" {'
lk="$(awk -v pat="$lkp" 'index($0, pat) == 1 { on = 1 } on { print } on && /^\}/ { exit }' "$DIR/locations.conf")"
[ -n "$lk" ] || { echo "FAIL locations.conf: no [$lkp]"; RL=1; }
for need in 'limit_req zone=link_redirect burst=40 nodelay;' 'limit_req zone=link_relay burst=400 nodelay;' \
            'limit_except GET HEAD { deny all; }' 'auth_basic off;' 'proxy_set_header Authorization "";' \
            'proxy_set_header Cookie "";' 'proxy_set_header X-ReelBolt-Site-Token "";' \
            'include /etc/nginx/proxy-client-headers.conf;' 'proxy_intercept_errors on;' \
            'proxy_connect_timeout 2s;' 'proxy_send_timeout 5s;' 'proxy_read_timeout 5s;' \
            'access_log off;' 'error_page 429 500 502 503 504 = @link_home;' 'proxy_pass $inference_api/api/v1/l/$link_code;'; do
    printf '%s\n' "$lk" | grep -qF "$need" || { echo "FAIL locations.conf: [$lkp] lacks [$need]"; RL=1; }
done
printf '%s\n' "$lk" | grep -Eq 'proxy_set_header[[:space:]]+Host|\$1|access_log[[:space:]]+[^o]|\$is_args|\$args' \
    && { echo "FAIL locations.conf: [$lkp] sets Host, uses a positional capture, forwards the query or turns the access log on"; RL=1; }
# Everything else under /l/ and /api/v1/l/ redirects home, in any letter case. It is a regex AFTER the code
# location (nginx takes the first regex that matches); `^~` would stop nginx trying the code regex at all.
grep -Eq '^location[[:space:]]+\^~[[:space:]]+/(api/v1/)?l/' "$DIR/locations.conf" \
    && { echo "FAIL locations.conf: a ^~ location shadows the tracked-link regex"; RL=1; }
lj='location ~* ^/(?:api/v1/)?l/ {'
jb="$(awk -v pat="$lj" 'index($0, pat) == 1 { on = 1 } on { print } on && /^\}/ { exit }' "$DIR/locations.conf")"
[ -n "$jb" ] || { echo "FAIL locations.conf: no [$lj]"; RL=1; }
for need in 'auth_basic off;' 'absolute_redirect off;' 'return 302 /;'; do
    printf '%s\n' "$jb" | grep -qF "$need" || { echo "FAIL locations.conf: [$lj] lacks [$need]"; RL=1; }
done
printf '%s\n' "$jb" | grep -q 'proxy_pass' && { echo "FAIL locations.conf: [$lj] reaches an upstream"; RL=1; }
code_line=$(grep -nF "$lkp" "$DIR/locations.conf" | head -1 | cut -d: -f1)
junk_line=$(grep -nF "$lj" "$DIR/locations.conf" | head -1 | cut -d: -f1)
[ -n "$code_line" ] && [ -n "$junk_line" ] && [ "$code_line" -lt "$junk_line" ] \
    || { echo "FAIL locations.conf: the tracked-link code location must come before the catch-all (first matching regex wins)"; RL=1; }
# The fallback keeps the link: the code travels as utm_content, which joins the visit to the link without the API.
hb="$(awk -v pat='location @link_home {' 'index($0, pat) == 1 { on = 1 } on { print } on && /^\}/ { exit }' "$DIR/locations.conf")"
[ -n "$hb" ] || { echo "FAIL locations.conf: no [location @link_home {]"; RL=1; }
for need in 'auth_basic off;' 'absolute_redirect off;' 'add_header Cache-Control "no-store" always;' 'return 302 /?utm_content=$link_code;'; do
    printf '%s\n' "$hb" | grep -qF "$need" || { echo "FAIL locations.conf: [location @link_home {] lacks [$need]"; RL=1; }
done
# No other location may claim a tracked-link path: a second one is how the two paths drifted apart before.
others=$(grep -E '^location ' "$DIR/locations.conf" | grep -E '/l[/( ]' | grep -vF "$lkp" | grep -vF "$lj" || true)
[ -z "$others" ] || { echo "FAIL locations.conf: another location claims a tracked-link path: $others"; RL=1; }
# N-04: every per-IP zone keys on $client_ip_key (IPv4 address, IPv6 /64), never on the raw /128.
grep -qF 'map $binary_remote_addr $client_ip_key {' "$NC" \
    || { echo "FAIL nginx.conf: no \$client_ip_key map (IPv6 /64 per-IP key)"; RL=1; }
grep -qF 'limit_req_zone $client_ip_key zone=runner_telemetry:10m rate=20r/m;' "$NC" \
    || { echo "FAIL nginx.conf: runner_telemetry must key on \$client_ip_key"; RL=1; }
grep -qF 'limit_req_zone $client_ip_key zone=telemetry_ingest:10m rate=120r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no telemetry_ingest zone (120r/m on \$client_ip_key)"; RL=1; }
grep -Eq '^[[:space:]]*limit_req_zone[[:space:]]+\$(binary_)?remote_addr' "$NC" \
    && { echo "FAIL nginx.conf: a limit_req_zone keys on the full client address (an IPv6 /64 rotates past it)"; RL=1; }
[ "$RL" -eq 0 ] && echo "ok   static: subscribe forwards with X-ReelBolt-Site-Token use site_forward; auth_public unchanged elsewhere; tracked links (both paths) anonymous in their own zone, relay zone only for a verified token; per-IP keys on IPv6 /64"
[ "$RL" -eq 0 ] || FAIL=1

# T-004: the global SSE feed must have its own exact-match, unbuffered, long-timeout location;
# the origin must gzip the API's JSON but never the event stream. block_of <literal> prints one
# location block.
K=0
block_of() {
    awk -v pat="$1" 'index($0, pat) { on = 1 } on { print } on && /\}/ { exit }' "$DIR/locations.conf"
}
check_block() {
    block=$1; label=$2; shift 2
    for want in "$@"; do
        printf '%s\n' "$block" | grep -qF "$want" \
            || { echo "FAIL locations.conf: $label lacks: $want"; K=1; }
    done
}
SSE="$(block_of 'location = /api/v1/workflows/events {')"
check_block "$SSE" "the global SSE feed location" \
    'proxy_pass $go_api;' 'proxy_http_version 1.1;' 'proxy_buffering off;' 'proxy_cache off;' \
    'proxy_read_timeout 1h;' 'proxy_send_timeout 1h;' 'proxy_set_header Authorization $auth_header;' \
    'include /etc/nginx/proxy-client-headers.conf;'
printf '%s\n' "$SSE" | grep -qF 'auth_basic off;' \
    && { echo "FAIL locations.conf: the global SSE feed opts out of the staging basic-auth front door"; K=1; }
# gzip at the origin: JSON/css/js/plain/xml/svg, never the SSE stream.
GZ="$(grep -E '^[[:space:]]*gzip_types ' "$NC")"
for t in application/json text/css text/plain application/javascript application/xml image/svg+xml; do
    printf '%s\n' "$GZ" | grep -qF "$t" \
        || { echo "FAIL nginx.conf: gzip_types does not include $t"; K=1; }
done
printf '%s\n' "$GZ" | grep -qF 'text/event-stream' \
    && { echo "FAIL nginx.conf: gzip_types compresses text/event-stream"; K=1; }
grep -qF 'gzip on;' "$NC" || { echo "FAIL nginx.conf: gzip is off"; K=1; }
[ "$K" -eq 0 ] && echo "ok   static: global SSE feed is exact-match and unbuffered; origin gzips JSON but not the event stream"
[ "$K" -eq 0 ] || FAIL=1

# Security assessment, edge findings. E collects them.
E=0
LC="$DIR/locations.conf"
# loc_block <literal first line>: one top-level location block of locations.conf.
loc_block() { awk -v pat="$1" 'index($0, pat) == 1 { on = 1 } on { print } on && /^\}/ { exit }' "$LC"; }
# EDGE-03: the Inference API routes case-insensitively and tolerates a trailing slash, so its
# locations with a control of their own (body cap, zone, the Paddle source-IP allowlist) are
# case-insensitive regexes, never exact matches a variant falls past.
grep -Eq '^[[:space:]]*location[[:space:]]+=[[:space:]]+/api/v1/(billing/webhooks|github/webhook|telemetry)' "$LC" \
    && { echo "FAIL locations.conf: exact-match Inference location (case/trailing-slash variants bypass it)"; E=1; }
for spec in 'location ~* ^/api/v1/billing/webhooks/paddle(/|$) {=client_max_body_size 1m;' \
            'location ~* ^/api/v1/billing/webhooks/paddle(/|$) {=include /etc/nginx/conf.d/paddle-webhook-allow.conf;' \
            'location ~* ^/api/v1/billing/webhooks/stripe(/|$) {=client_max_body_size 1m;' \
            'location ~* ^/api/v1/github/webhook(/|$) {=client_max_body_size 2m;' \
            'location ~* ^/api/v1/telemetry/visit(/|$) {=client_max_body_size 2k;'; do
    pat=${spec%%=*}; need=${spec#*=}
    b="$(loc_block "$pat")"
    [ -n "$b" ] || { echo "FAIL locations.conf: no [$pat]"; E=1; continue; }
    for n in "$need" 'proxy_set_header Authorization "";' 'auth_basic off;' \
             'include /etc/nginx/proxy-client-headers.conf;' 'proxy_pass $inference_api;'; do
        printf '%s\n' "$b" | grep -qF "$n" || { echo "FAIL locations.conf: [$pat] lacks [$n]"; E=1; }
    done
done
# The visit beacon must stay ahead of the telemetry ingest regex: the first matching regex wins.
v_line=$(grep -nF 'location ~* ^/api/v1/telemetry/visit(/|$) {' "$LC" | head -1 | cut -d: -f1)
t_line=$(grep -nF 'location ~* ^/api/v1/telemetry/ {' "$LC" | head -1 | cut -d: -f1)
[ -n "$v_line" ] && [ -n "$t_line" ] && [ "$v_line" -lt "$t_line" ] \
    || { echo "FAIL locations.conf: the visit beacon location must come before the telemetry ingest regex"; E=1; }
# EDGE-07: a limit_req rejection logs the request line at `error` by default, and these three paths
# carry a capability token. `info` is below error_log's `warn` threshold.
grep -Eq '^error_log /dev/stderr warn;' "$NC" \
    || { echo "FAIL nginx.conf: error_log is no longer at warn (limit_req_log_level info relies on it)"; E=1; }
for pat in 'location ~* ^/api/v1/output-links/ {' 'location ~* ^/api/v1/review-links/ {' 'location ^~ /app/review/g/ {'; do
    loc_block "$pat" | grep -Eq '^[[:space:]]*limit_req_log_level info;' \
        || { echo "FAIL locations.conf: [$pat] lacks limit_req_log_level info (its token reaches the error log on a 429)"; E=1; }
done
# EDGE-11: nginx's own slot signal is blanked for every proxied request.
grep -qF 'proxy_set_header X-ReelBolt-Slot-Fallback "";' "$SNIP" \
    || { echo "FAIL proxy-client-headers.conf does not blank X-ReelBolt-Slot-Fallback"; E=1; }
# EDGE-10: the private engine-to-API listener overwrites the client-address headers with the socket
# peer (nothing is appended to a caller-supplied chain) and keeps Authorization untouched.
PV="$DIR/private-server.conf.template"
for n in 'proxy_set_header X-Real-IP $remote_addr;' 'proxy_set_header X-Forwarded-For $remote_addr;' \
         'proxy_set_header X-ReelBolt-Slot-Fallback "";'; do
    grep -qF "$n" "$PV" || { echo "FAIL private-server.conf.template lacks [$n]"; E=1; }
done
grep -v '^[[:space:]]*#' "$PV" | grep -Eq 'proxy_add_x_forwarded_for|proxy_set_header[[:space:]]+Authorization' \
    && { echo "FAIL private-server.conf.template appends to a caller-supplied X-Forwarded-For or touches Authorization"; E=1; }
# EDGE-04: nosniff for every upstream, HSTS only when FORCE_HTTPS=true. add_header is inherited
# only into a scope with no add_header of its own, so every scope (file level, location, `if`)
# that sets one must repeat both lines.
NS='add_header X-Content-Type-Options "nosniff" always;'
HS='include /etc/nginx/conf.d/hsts*.inc;'
for f in common.conf.template runner-server.conf.template; do
    grep -qF "$NS" "$DIR/$f" || { echo "FAIL $f lacks [$NS]"; E=1; }
    grep -qF "$HS" "$DIR/$f" || { echo "FAIL $f lacks [$HS]"; E=1; }
done
for f in locations.conf slots-locations.conf runner-locations.conf.template runner-server.conf.template \
         common.conf.template http-server-dev.conf https-server.conf.template; do
    awk -v file="$f" -v ns="$NS" -v hs="$HS" '
        function close_scope() {
            if (ah[d] && !(sn[d] && sh[d])) {
                printf "FAIL %s:%d a block sets add_header without repeating nosniff and the hsts include\n", file, st[d]
                bad = 1
            }
        }
        /^[ \t]*#/ { next }
        {
            line = $0
            o = gsub(/\{/, "{", line); c = gsub(/\}/, "}", line)
            if (o > c) { d++; ah[d] = 0; sn[d] = 0; sh[d] = 0; st[d] = NR; next }
            if (c > o) { close_scope(); d--; next }
            if ($0 ~ /^[ \t]*add_header[ \t]/) ah[d] = 1
            if (index($0, ns)) sn[d] = 1
            if (index($0, hs)) sh[d] = 1
        }
        END { st[0] = 1; close_scope(); exit bad }
    ' "$DIR/$f" || E=1
done
RC="$DIR/render-config.sh"
grep -qF "printf 'add_header Strict-Transport-Security \"max-age=31536000\" always;\n' > /etc/nginx/conf.d/hsts.inc" "$RC" \
    || { echo "FAIL render-config.sh does not render conf.d/hsts.inc"; E=1; }
grep -qF 'rm -f /etc/nginx/conf.d/hsts.inc' "$RC" \
    || { echo "FAIL render-config.sh does not remove a stale conf.d/hsts.inc (FORCE_HTTPS turned off)"; E=1; }
# The header is written only inside the FORCE_HTTPS=true branch.
awk '/^if \[ "\$FORCE_HTTPS" = "true" \]; then/ { on = 1 } on && /^else/ { on = 0 }
     /Strict-Transport-Security/ && !/^#/ && !on { bad = 1 } END { exit bad }' "$RC" \
    || { echo "FAIL render-config.sh writes HSTS outside the FORCE_HTTPS=true branch"; E=1; }
grep -Eq '^[[:space:]]*add_header[[:space:]]+(Content-Security-Policy|X-Frame-Options)' "$DIR"/*.conf "$DIR"/*.template \
    && { echo "FAIL a CSP or framing header is set at the edge (needs a product decision: it can break embeds)"; E=1; }
# EDGE-02: /mcp has its own per-address zone and a small body cap.
grep -qF 'limit_req_zone $client_ip_key zone=mcp:10m rate=600r/m;' "$NC" \
    || { echo "FAIL nginx.conf: no mcp zone (600r/m on \$client_ip_key)"; E=1; }
mcpb="$(loc_block 'location /mcp {')"
for n in 'limit_req zone=mcp burst=200 nodelay;' 'client_max_body_size 1m;' 'proxy_set_header Authorization $auth_header;' \
         'include /etc/nginx/proxy-client-headers.conf;' 'proxy_buffering off;' 'proxy_pass $mcp;'; do
    printf '%s\n' "$mcpb" | grep -qF "$n" || { echo "FAIL locations.conf: [location /mcp {] lacks [$n]"; E=1; }
done
# The metadata prefix reaches the same MCP server on GET: same zone, same body cap.
wkb="$(loc_block 'location /.well-known/oauth-protected-resource {')"
for n in 'limit_except GET { deny all; }' 'limit_req zone=mcp burst=200 nodelay;' 'client_max_body_size 1m;'; do
    printf '%s\n' "$wkb" | grep -qF "$n" \
        || { echo "FAIL locations.conf: [location /.well-known/oauth-protected-resource {] lacks [$n]"; E=1; }
done
[ "$E" -eq 0 ] && echo "ok   static: Inference webhook and beacon locations are case-insensitive regexes; token paths log limit_req at info; slot header blanked; private listener overwrites the client address; nosniff everywhere, HSTS only with FORCE_HTTPS; /mcp limited and capped"
[ "$E" -eq 0 ] || FAIL=1

[ "${1:-}" = "--static-only" ] && exit "$FAIL"

# --- 2. live ----------------------------------------------------------------------------------
TAG="t015-nginx-$$"
NET="t015-net-$$"
ECHO="t015-echo-$$"
NGX="t015-ngx-$$"
# A network INSIDE one of Cloudflare's published ranges (cloudflare-realip.conf), so a request the
# nginx container sends to its own address there arrives from a Cloudflare-range peer and the
# shipped realip config is exercised unmodified. A /29 at the top of 173.245.48.0/20: the host
# routes these 8 addresses to the bridge only while the test runs.
NETCF="t015-cf-$$"
CF_SUBNET=173.245.63.248/29
cleanup() {
    docker rm -f "$NGX" "$ECHO" >/dev/null 2>&1 || true
    docker network rm "$NET" "$NETCF" >/dev/null 2>&1 || true
    docker rmi -f "$TAG" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

docker build -q --network host -t "$TAG" "$DIR" >/dev/null
docker network create "$NET" >/dev/null
# No body cap of its own (client_max_body_size 0): a 413 in the body-cap checks below must come
# from the edge config under test, never from this server's 1m default.
# Answers every request with the forwarding headers it received (and the Authorization the
# location sent, so a cookie-translating location can be told from one that blanks it).
# A few /api/v1/l/ paths answer as the Inference API's tracked-link endpoint would: one echoes what
# it was sent, one redirects with a relative Location, two fail (502, 500), one is the "links switched
# off" 404.
docker run -d --name "$ECHO" --network "$NET" nginx:1.31.9-alpine sh -c '
    printf "%s\n" "server { listen 8080; default_type text/plain; absolute_redirect off; client_max_body_size 0;" \
      "location = /api/v1/l/echo-uri { return 200 \"uri=\$request_uri cookie=\$http_cookie auth=\$http_authorization token=\$http_x_reelbolt_site_token\"; }" \
      "location = /api/v1/l/hn-launch { add_header Cache-Control no-store always; return 302 /for/software-teams?utm_content=hn-launch; }" \
      "location = /api/v1/l/api-down { return 502; }" \
      "location = /api/v1/l/five-hundred { return 500; }" \
      "location = /api/v1/l/links-off { return 404; }" \
      "location / { return 200 \"xri=\$http_x_real_ip xff=\$http_x_forwarded_for host=\$http_host proto=\$http_x_forwarded_proto auth=\$http_authorization\"; } }" \
      > /etc/nginx/conf.d/default.conf && exec nginx -g "daemon off;"' >/dev/null

U="http://$ECHO:8080"
start_nginx() {
    docker rm -f "$NGX" >/dev/null 2>&1 || true
    docker run -d --name "$NGX" --network "$NET" \
        -e UPSTREAM_GO_API="$U" -e UPSTREAM_INFERENCE_API="$U" -e UPSTREAM_WORKFLOW_ENGINE="$U" \
        -e UPSTREAM_WEB="$U" -e UPSTREAM_SITE="$U" -e UPSTREAM_DOCS="$U" -e UPSTREAM_MCP="$U" \
        -e UPSTREAM_RUNNER_GATEWAY="$U" "$@" "$TAG" >/dev/null
    i=0
    until docker exec "$NGX" curl -s -o /dev/null http://127.0.0.1/health 2>/dev/null; do
        i=$((i + 1)); [ "$i" -gt 30 ] && { docker logs "$NGX"; echo "FAIL nginx did not start"; exit 1; }
        sleep 0.5
    done
}

# probe <label> <host> <curl args...>: the upstream must see X-Real-IP 127.0.0.1 (the real peer),
# never 6.6.6.6, X-Forwarded-For must end with the real peer, X-Forwarded-Proto must be $PROTO
# (default http) and Host must be <host>: what that location sent before the fix. Authorization
# reaches the upstream as `auth=`; a location that translates the cookie or blanks the header sends
# it empty, but $auth_header passes an incoming Authorization through (auth.js), so a probe that
# sends one -- the basic-auth front door -- sets WANT_AUTH to what the upstream should see.
probe() {
    label=$1; want_host=$2; shift 2
    out=$(docker exec "$NGX" curl -s -H 'X-Real-IP: 6.6.6.6' -H 'X-Forwarded-For: 6.6.6.6' \
        -H 'CF-Connecting-IP: 7.7.7.7' "$@")
    case "$out" in
        "xri=127.0.0.1 xff=6.6.6.6, 127.0.0.1 host=$want_host proto=${PROTO:-http} auth=${WANT_AUTH:-}") echo "ok   $label -> $out" ;;
        *) echo "FAIL $label -> $out"; FAIL=1 ;;
    esac
}

# Host each upstream received before the fix. CLIENT: the client's Host ($host), set by the server
# block (inherited by locations without headers of their own), the marketing locations and the
# runner locations. UPSTREAM: nginx's default $proxy_host, the upstream's name, for every other
# location with headers of its own -- including /app/ and /mcp.
CLIENT=127.0.0.1
UPSTREAM="$ECHO:8080"
PATHS="/api/v1/anything=U /api/v1/workflows/x=U /api/v1/workflows/events=U /api/v1/auth/token=C /api/v1/auth/me=U /api/v1/orgs=U
/api/v1/marketing/subscribe=C /api/v1/marketing/confirm=C /api/v1/marketing/unsubscribe=C
/api/v1/marketing/consent=U /api/v1/billing/webhooks/paddle=U /api/v1/billing/webhooks/stripe=U /api/v1/device/x=U
/api/v1/assistant/chat=U /api/v1/workflow-engine/x=U /app=U /app/=U /app/projects=U /docs/=C /=U
/api/v1/telemetry/events=U /api/v1/telemetry/errors=U /api/v1/telemetry/visit=C
/l/some-code=U /api/v1/l/some-code=U
/mcp=U /oauth/token=U /oauth/authorize=U /.well-known/oauth-protected-resource/mcp=U
/api/v1/billing/promotions/plan-prices=U /API/V1/Billing/Promotions/Active/=U
/runner/telemetry/v1/reports=C
/api/contact=U /api/subscribe=U /api/promotions=U"
host_for() { case "$1" in *=C) echo "$CLIENT" ;; *) echo "$UPSTREAM" ;; esac; }

echo "-- default"
start_nginx
docker exec "$NGX" nginx -t 2>&1 | tail -1
for e in $PATHS; do
    p=${e%=*}; h=$(host_for "$e")
    if [ "$p" = /runner/telemetry/v1/reports ]; then probe "POST $p" "$h" -X POST -d '{}' "http://127.0.0.1$p"
    else probe "$p" "$h" "http://127.0.0.1$p"; fi
done

echo "-- subscribe rate limits (auth_public 5r/m burst 5; with X-ReelBolt-Site-Token site_forward 60r/m burst 20)"
# codes <n> <curl args...>: the status codes of n back-to-back requests, space separated.
codes() {
    n=$1; shift
    docker exec "$NGX" sh -c 'i=0; while [ "$i" -lt "$0" ]; do
        curl -s -o /dev/null -w "%{http_code} " "$@"; i=$((i + 1)); done' "$n" "$@"
}
start_nginx   # fresh zones: the probes above already spent part of auth_public's burst
SUB=http://127.0.0.1/api/v1/marketing/subscribe
TOK='X-ReelBolt-Site-Token: anything'
# 15 token-carrying requests (more than auth_public's burst) all pass the site_forward zone, also
# on a case/trailing-slash variant of the path (S-05: the same API route, so the same zone) ...
out="$(codes 8 -X POST -H "$TOK" "$SUB")$(codes 7 -X POST -H "$TOK" "http://127.0.0.1/Api/V1/Marketing/SubScribe/")"
case "$out" in *429*) echo "FAIL subscribe with site token throttled early: $out"; FAIL=1 ;;
               *) echo "ok   15 subscribe forwards with the site token -> $out" ;; esac
# ... without touching auth_public: header-less requests still get its full 1 + burst 5, then 429 ...
out=$(codes 8 -X POST "$SUB")
case "$out" in "200 200 200 200 200 200 429 429 ") echo "ok   header-less subscribe keeps auth_public -> $out" ;;
               *) echo "FAIL header-less subscribe: want 6x200 then 429, got $out"; FAIL=1 ;; esac
# ... and the header is no way around auth_public on any other location (same address, same zone).
out=$(codes 1 -X POST -H "$TOK" http://127.0.0.1/api/v1/marketing/confirm)
case "$out" in "429 ") echo "ok   site token on /confirm stays in auth_public -> $out" ;;
               *) echo "FAIL site token on /confirm escaped auth_public: $out"; FAIL=1 ;; esac
# site_forward is still a limit: past its burst of 20 (15 used above) it answers 429 too.
out=$(codes 10 -X POST -H "$TOK" "$SUB")
case "$out" in *429*) echo "ok   site_forward still limits -> $out" ;;
               *) echo "FAIL site_forward never throttled: $out"; FAIL=1 ;; esac

echo "-- pricing-page promotions: promo_public (30r/m, burst 30) without the token, site_forward with it"
start_nginx
PROMO=http://127.0.0.1/api/v1/billing/promotions/plan-prices
# 36 header-less reads: the first 31 pass (burst 30 + 1), then 429. 0.5 r/s refill can add one pass.
out=$(codes 36 "$PROMO")
n200=$(printf '%s' "$out" | tr ' ' '\n' | grep -c '^200$' || true)
case "$out" in *429*) n429=1 ;; *) n429=0 ;; esac
if [ "$n200" -ge 31 ] && [ "$n200" -le 32 ] && [ "$n429" -eq 1 ]; then
    echo "ok   header-less promotions reads share promo_public (31 + 429s) -> $out"
else
    echo "FAIL promo_public: want 31 200s then 429, got $out"; FAIL=1
fi
# Still limited, and not through auth_public (the promotions location does not declare it).
out=$(codes 1 "$PROMO")
case "$out" in "429 ") echo "ok   promotions still limited without the token -> $out" ;;
               *) echo "FAIL promotions header-less not limited: $out"; FAIL=1 ;; esac
# With the token promo_public is skipped (empty key) and site_forward answers: its bucket is fresh.
out=$(codes 3 -H "$TOK" "$PROMO")
case "$out" in "200 200 200 ") echo "ok   token promotions reads skip promo_public, use site_forward -> $out" ;;
               *) echo "FAIL token promotions: want 200s from site_forward, got $out"; FAIL=1 ;; esac

echo "-- the marketing site's own form endpoints: site_forms (1r/m, burst 4), site_promotions (100r/m, burst 100)"
# The `site` container's routes (docs/marketing-site.md "Same-origin routes"), outside /api/v1/.
# nginx's limit_req takes an integer r/s or r/m only, so the Cloudflare rule's 5 requests per
# 600 s has no exact form; 1r/m burst=4 lets the same five through and answers the sixth 429,
# which is what the alpha gate's A23 row measured.
# Three on each POST path in ONE container: the two share a single site_forms bucket, as the single
# Cloudflare rule above counts them together (counting characteristic ip.src over both paths). Five
# pass and the sixth is 429, which is also the A23 outcome -- and a per-path zone would show six 200s.
start_nginx
out="$(codes 3 -X POST http://127.0.0.1/api/contact)$(codes 3 -X POST http://127.0.0.1/api/subscribe)"
case "$out" in "200 200 200 200 200 429 ") echo "ok   /api/contact and /api/subscribe share one site_forms bucket -> $out" ;;
               *) echo "FAIL site_forms: want three + three with the sixth 429, got $out"; FAIL=1 ;; esac
# The limit is path-only, never keyed on the Host. The SAME three-and-three split, across TWO
# hostnames, still exhausts one bucket. A zone keyed on $host (or on a domain) would answer six 200s
# here, so this fails if anyone domain-scopes the zone -- the alpha gate's bypassability concern.
start_nginx
out="$(codes 3 -X POST -H 'Host: a.example' http://127.0.0.1/api/contact)$(codes 3 -X POST -H 'Host: b.example' http://127.0.0.1/api/contact)"
case "$out" in "200 200 200 200 200 429 ") echo "ok   two hostnames share one site_forms bucket -> $out" ;;
               *) echo "FAIL site_forms is keyed on the Host, not the path: $out"; FAIL=1 ;; esac
# A case/trailing-slash variant is the same URI nginx limits, so it shares the bucket. The bucket is
# already spent above, so this needs no fresh container.
out=$(codes 1 -X POST http://127.0.0.1/API/Contact/)
case "$out" in "429 ") echo "ok   /API/Contact/ shares the site_forms bucket -> $out" ;;
               *) echo "FAIL /API/Contact/ escaped site_forms: $out"; FAIL=1 ;; esac
# The read-only promotions proxy is generous (100r/m, burst 100): the first 101 pass, then 429. Both
# ends are bounded, as the promo_public check above does: 1000r/m would answer all 110 with 200, and
# the upper bound allows for the refill (1.67 slots/s) over the seconds the 110 requests take.
start_nginx
out=$(codes 110 http://127.0.0.1/api/promotions)
n200=$(printf '%s' "$out" | tr ' ' '\n' | grep -c '^200$' || true)
case "$out" in *429*) n429=1 ;; *) n429=0 ;; esac
if [ "$n200" -ge 101 ] && [ "$n200" -le 105 ] && [ "$n429" -eq 1 ]; then
    echo "ok   /api/promotions reads share site_promotions (101-105 + 429s) -> $out"
else
    echo "FAIL site_promotions: want 101-105 200s then 429, got $out"; FAIL=1
fi

echo "-- client telemetry ingest: cookie translated, dedicated telemetry_ingest zone (120r/m, burst 60)"
# Authenticated route: the location must translate the session cookie to a bearer token for the
# Inference API, exactly as the generic /api/v1/ block does (the echo reports the Authorization it
# received as `auth=`), and it must be limited by its own per-address zone (Security finding 2).
start_nginx
out=$(docker exec "$NGX" curl -s -H 'Cookie: reelbolt_token=TESTTOKEN' http://127.0.0.1/api/v1/telemetry/events)
case "$out" in *" auth=Bearer TESTTOKEN") echo "ok   telemetry translates the session cookie -> $out" ;;
               *) echo "FAIL telemetry cookie translation -> $out"; FAIL=1 ;; esac
# 80 back-to-back POSTs: 1 + burst 60 pass, then 429. 2r/s refill can add a few over the run.
start_nginx
out=$(codes 80 -X POST http://127.0.0.1/api/v1/telemetry/events)
n200=$(printf '%s' "$out" | tr ' ' '\n' | grep -c '^200$' || true)
case "$out" in *429*) n429=1 ;; *) n429=0 ;; esac
if [ "$n200" -ge 61 ] && [ "$n200" -le 75 ] && [ "$n429" -eq 1 ]; then
    echo "ok   telemetry ingest shares telemetry_ingest (61-75 + 429s) -> $out"
else
    echo "FAIL telemetry_ingest: want ~61 200s then 429, got $out"; FAIL=1
fi
# A case variant is the same API action (S-05) and the bucket is spent, so it is mostly 429: a
# case-sensitive location would have fallen through to /api/v1/ and answered 200 to every one.
# The zone refills 2 a second, so on a loaded machine a token or two may come back between the
# flood and this burst; a single probe flaked on that. 10 back to back: at least 6 must be 429 (an
# escaped variant scores 0).
out=$(codes 10 -X POST http://127.0.0.1/API/V1/Telemetry/Errors)
n429=$(printf '%s' "$out" | tr ' ' '\n' | grep -c '^429$' || true)
if [ "$n429" -ge 6 ]; then echo "ok   /API/V1/Telemetry/Errors shares the telemetry_ingest bucket ($n429 of 10 -> 429) -> $out"
else echo "FAIL telemetry case variant escaped the zone: $n429 of 10 -> 429: $out"; FAIL=1; fi

echo "-- tracked links: /l/{code} and /api/v1/l/{code} -> the API, anonymous, link_redirect zone (120r/m, burst 40), never an error page"
RELAY_TOKEN=t015-relay-token-0123456789
start_nginx -e SITE_FORWARD_TOKEN="$RELAY_TOKEN"
# head_of <curl args...>: status, Location and Cache-Control of one response, on one line.
head_of() {
    docker exec "$NGX" curl -s -o /dev/null -D - "$@" | tr -d '\r' \
        | awk 'NR == 1 { printf "%s", $2 } tolower($1) == "location:" { printf " loc=%s", $2 } tolower($1) == "cache-control:" { printf " cc=%s", $2 } END { print "" }'
}
# count <codes> <code>: how many of the space-separated status codes are <code>.
count() { printf '%s' "$1" | tr ' ' '\n' | grep -c "^$2\$" || true; }
# The upstream path is /api/v1/l/{code} from BOTH addresses; the click's query string, cookies,
# Authorization and the relay token stay behind.
for p in /l/echo-uri /api/v1/l/echo-uri /l/echo-uri/ /api/v1/l/echo-uri/; do
    out=$(docker exec "$NGX" curl -s -H 'Cookie: s=1; reelbolt_token=TESTTOKEN' -H 'Authorization: Bearer leak' \
        -H "X-ReelBolt-Site-Token: $RELAY_TOKEN" "http://127.0.0.1$p?fbclid=abc")
    case "$out" in "uri=/api/v1/l/echo-uri cookie= auth= token=") echo "ok   $p reaches /api/v1/l/{code}, anonymous -> $out" ;;
                   *) echo "FAIL $p mapping -> $out"; FAIL=1 ;; esac
done
# The API's own redirect passes through untouched: relative Location, no-store.
for p in /l/hn-launch /api/v1/l/hn-launch; do
    out=$(head_of "http://127.0.0.1$p")
    case "$out" in "302 loc=/for/software-teams?utm_content=hn-launch cc=no-store") echo "ok   $p: the API's redirect passes through -> $out" ;;
                   *) echo "FAIL $p redirect -> $out"; FAIL=1 ;; esac
    out=$(head_of -I "http://127.0.0.1$p")
    case "$out" in "302 loc=/for/software-teams?utm_content=hn-launch cc=no-store") echo "ok   $p: HEAD is answered like GET -> $out" ;;
                   *) echo "FAIL $p HEAD -> $out"; FAIL=1 ;; esac
    for m in POST PUT DELETE; do
        code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' -X "$m" "http://127.0.0.1$p")
        [ "$code" = 403 ] && echo "ok   $m $p refused (403)" || { echo "FAIL $m $p answered $code, expected 403"; FAIL=1; }
    done
done
# Anything under /l/ or /api/v1/l/ that cannot be a code never reaches the API and never 404s: home page.
for p in /l/ /l/a /l/Bad_Code /l/has/slash /l/-lead /l/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa /L/Bad_Code \
         /api/v1/l/ /api/v1/l/a /api/v1/l/Bad_Code /api/v1/l/has/slash /api/v1/L/Bad_Code; do
    out=$(head_of -H 'Cookie: reelbolt_token=TESTTOKEN' "http://127.0.0.1$p")
    case "$out" in "302 loc=/") echo "ok   $p -> home" ;;
                   *) echo "FAIL $p -> $out, expected a 302 to /"; FAIL=1 ;; esac
done
# Neighbours that only look alike are not tracked-link paths: they still reach their upstream.
for p in /api/v1/l /api/v1/legal /api/v1/links /library; do
    out=$(docker exec "$NGX" curl -s "http://127.0.0.1$p")
    case "$out" in xri=*) echo "ok   $p is not a tracked-link path" ;;
                   *) echo "FAIL $p was taken by a tracked-link location -> $out"; FAIL=1 ;; esac
done
# An API that cannot answer sends the visitor to the home page WITH the link code as utm_content (the
# visit still joins the link; only the click count is lost); the API's own 404 (links switched off) is
# its answer. Same on both addresses.
for p in /l/api-down /api/v1/l/api-down /l/five-hundred; do
    out=$(head_of "http://127.0.0.1$p")
    case "$out" in "302 loc=/?utm_content=${p##*/} cc=no-store") echo "ok   $p: upstream error -> home with the link code, uncached -> $out" ;;
                   *) echo "FAIL $p with the API failing -> $out"; FAIL=1 ;; esac
done
for p in /l/links-off /api/v1/l/links-off; do
    code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1$p")
    [ "$code" = 404 ] && echo "ok   $p: the API's 404 passes through (tracked links switched off)" || { echo "FAIL $p: the API's 404 became $code"; FAIL=1; }
done
# 60 back-to-back clicks from one address: 1 + burst 40 are proxied (200 from the echo), the rest are sent
# home (302) instead of answered 429. 2r/s refill can add a few over the run.
start_nginx -e SITE_FORWARD_TOKEN="$RELAY_TOKEN"
out=$(codes 60 http://127.0.0.1/l/echo-uri)
n200=$(count "$out" 200); n302=$(count "$out" 302)
if [ "$n200" -ge 41 ] && [ "$n200" -le 50 ] && [ "$((n200 + n302))" -eq 60 ] && [ "$n302" -ge 1 ]; then
    echo "ok   tracked links share link_redirect (41-50 proxied, the rest sent home) -> $out"
else
    echo "FAIL link_redirect: want ~41 200s then 302s, got $out"; FAIL=1
fi
out=$(head_of http://127.0.0.1/l/echo-uri)
case "$out" in "302 loc=/?utm_content=echo-uri cc=no-store") echo "ok   over the limit -> home with the link code, uncached -> $out" ;;
               *) echo "FAIL /l/{code} over the limit -> $out"; FAIL=1 ;; esac
# The API's own path is the same bucket: it is not a way around the limit, in any letter case, and
# neither is a token header that is not exactly the token (wrong, one character longer, one shorter,
# empty). still_limited <label> <curl args...>: 5 more requests, most of them sent home (the bucket
# refills 2 a second, so one or two may pass on a slow machine; a way around would pass all five).
still_limited() {
    label=$1; shift
    out=$(codes 5 "$@")
    [ "$(count "$out" 302)" -ge 3 ] && echo "ok   still over the limit: $label -> $out" \
        || { echo "FAIL the link limit was walked around ($label): $out"; FAIL=1; }
}
still_limited "the API's own path" http://127.0.0.1/api/v1/l/echo-uri
still_limited "the API's own path, re-cased" http://127.0.0.1/API/V1/L/echo-uri/
still_limited "a wrong token" -H 'X-ReelBolt-Site-Token: wrong' http://127.0.0.1/api/v1/l/echo-uri
still_limited "the token plus one character" -H "X-ReelBolt-Site-Token: ${RELAY_TOKEN}x" http://127.0.0.1/api/v1/l/echo-uri
still_limited "the token minus one character" -H "X-ReelBolt-Site-Token: ${RELAY_TOKEN%?}" http://127.0.0.1/l/echo-uri
still_limited "an empty token header" -H 'X-ReelBolt-Site-Token;' http://127.0.0.1/api/v1/l/echo-uri
# The flood spent its own zone only: the visit beacon (telemetry_visit) still answers.
code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1/api/v1/telemetry/visit)
[ "$code" = 200 ] && echo "ok   link_redirect is its own zone (the visit beacon still answers)" || { echo "FAIL visit beacon answered $code after the link flood"; FAIL=1; }
# A fresh start, and the direct API path alone: 60 back-to-back requests are throttled exactly like the
# short path (this is the request loop that used to add 60 clicks).
start_nginx -e SITE_FORWARD_TOKEN="$RELAY_TOKEN"
out=$(codes 60 http://127.0.0.1/api/v1/l/echo-uri)
n200=$(count "$out" 200); n302=$(count "$out" 302)
if [ "$n200" -ge 41 ] && [ "$n200" -le 50 ] && [ "$((n200 + n302))" -eq 60 ]; then
    echo "ok   /api/v1/l/{code} is limited like /l/{code} ($n200 proxied, $n302 sent home)"
else
    echo "FAIL /api/v1/l/{code}: want ~41 200s then 302s, got $out"; FAIL=1
fi
# The site's relay, proving itself with the token, has its own zone: 100 more requests from the same
# (spent) address all pass. It is still a limit: past its burst of 400 it sends clicks home too (800
# more, so the 20-a-second refill cannot keep up even on a slow machine).
out=$(codes 100 -H "X-ReelBolt-Site-Token: $RELAY_TOKEN" http://127.0.0.1/api/v1/l/echo-uri)
[ "$(count "$out" 200)" -eq 100 ] && echo "ok   the verified relay uses link_relay (100 of 100 proxied from an address over link_redirect)" \
    || { echo "FAIL verified relay throttled with the visitors: $out"; FAIL=1; }
out=$(codes 800 -H "X-ReelBolt-Site-Token: $RELAY_TOKEN" http://127.0.0.1/api/v1/l/echo-uri)
[ "$(count "$out" 302)" -ge 1 ] && echo "ok   link_relay still limits ($(count "$out" 302) of the next 800 sent home)" \
    || { echo "FAIL link_relay never throttled"; FAIL=1; }
# Without SITE_FORWARD_TOKEN in nginx's environment (the default), or with an empty one, nobody is the
# relay, whatever is sent. no_relay <label>: 60 token-carrying requests are limited like anybody's.
no_relay() {
    out=$(codes 60 -H "X-ReelBolt-Site-Token: $RELAY_TOKEN" http://127.0.0.1/api/v1/l/echo-uri)
    n200=$(count "$out" 200)
    if [ "$n200" -ge 41 ] && [ "$n200" -le 50 ]; then echo "ok   SITE_FORWARD_TOKEN $1: the header buys nothing ($n200 of 60 proxied)"
    else echo "FAIL SITE_FORWARD_TOKEN $1: a token header left link_redirect: $out"; FAIL=1; fi
}
start_nginx
no_relay unset
start_nginx -e SITE_FORWARD_TOKEN=
no_relay empty

echo "-- EDGE-03: case and trailing-slash variants of the Inference webhook and beacon paths keep their location"
start_nginx
# The beacon location is the only one of these that sends the client's Host: a variant that fell to
# the telemetry ingest regex or to /api/v1/ would show the upstream's name.
probe "/API/V1/Telemetry/Visit/" "$CLIENT" -X POST http://127.0.0.1/API/V1/Telemetry/Visit/
# status_with_body <bytes> <url>: the status of a POST carrying a body of that many bytes.
status_with_body() {
    docker exec "$NGX" sh -c 'head -c "$0" /dev/zero | curl -s -o /dev/null -w "%{http_code}" -X POST \
        -H "Content-Type: application/octet-stream" --data-binary @- "$1"' "$1" "$2"
}
# Each variant is over its own location's body cap and far under the server's 100M: 413 proves the
# location took it. (The generic /api/v1/ block would have answered 200.)
for spec in "3000 /api/v1/telemetry/Visit/" "1500000 /api/v1/billing/webhooks/Paddle" \
            "1500000 /API/V1/Billing/Webhooks/Stripe/" "2500000 /api/v1/GitHub/Webhook/"; do
    n=${spec% *}; p=${spec#* }
    code=$(status_with_body "$n" "http://127.0.0.1$p")
    [ "$code" = 413 ] && echo "ok   $p keeps its body cap ($n bytes -> 413)" \
        || { echo "FAIL $p with $n bytes answered $code, expected 413 (the variant left its location)"; FAIL=1; }
done
# A body under the cap still reaches the API on the variant and on the plain path.
for p in /api/v1/billing/webhooks/Paddle/ /api/v1/billing/webhooks/paddle /api/v1/github/webhook; do
    code=$(status_with_body 500 "http://127.0.0.1$p")
    [ "$code" = 200 ] && echo "ok   $p with a small body is proxied (200)" \
        || { echo "FAIL $p with a small body answered $code, expected 200"; FAIL=1; }
done

echo "-- EDGE-04: nosniff on every response, no HSTS without FORCE_HTTPS"
# resp_headers <curl args...>: the response header lines of one request.
resp_headers() { docker exec "$NGX" curl -s -o /dev/null -D - "$@" | tr -d '\r'; }
# header_check <label> <want hsts lines> <curl args...>: exactly one nosniff line (also from a
# location with an add_header of its own, which inherits none) and the wanted number of HSTS lines.
header_check() {
    label=$1; want_hsts=$2; shift 2
    h=$(resp_headers "$@")
    ns=$(printf '%s\n' "$h" | grep -ci '^x-content-type-options: nosniff$' || true)
    hs=$(printf '%s\n' "$h" | grep -ci '^strict-transport-security: max-age=31536000$' || true)
    if [ "$ns" = 1 ] && [ "$hs" = "$want_hsts" ]; then echo "ok   $label: nosniff x$ns, HSTS x$hs"
    else echo "FAIL $label: nosniff x$ns (want 1), HSTS x$hs (want $want_hsts)"; FAIL=1; fi
}
for p in /api/v1/anything /app/projects / /it/pricing /app/review/g/x /l/api-down /mcp; do
    header_check "$p" 0 "http://127.0.0.1$p"
done
header_check "/ redirected by language" 0 -H 'Accept-Language: it' http://127.0.0.1/
header_check "https /api/v1/anything (FORCE_HTTPS off)" 0 -k https://127.0.0.1/api/v1/anything

echo "-- EDGE-04: FORCE_HTTPS=true adds HSTS on :443 only"
start_nginx -e FORCE_HTTPS=true
for p in /api/v1/anything /app/projects / /app/review/g/x /l/api-down /mcp; do
    header_check "https $p" 1 -k "https://127.0.0.1$p"
done
header_check "https / redirected by language" 1 -k -H 'Accept-Language: it' https://127.0.0.1/
h=$(resp_headers http://127.0.0.1/api/v1/anything)
case "$h" in *[Ss]trict-[Tt]ransport-[Ss]ecurity*) echo "FAIL the plain-http redirect carries HSTS"; FAIL=1 ;;
             *) echo "ok   the plain-http redirect carries no HSTS" ;; esac

echo "-- EDGE-07: a capability token on a path over its limit stays out of the container log"
start_nginx
for p in /api/v1/output-links/SECRETFLOODTOKEN1 /api/v1/Review-Links/SECRETFLOODTOKEN2/media /app/review/g/SECRETFLOODTOKEN3; do
    out=$(codes 90 "http://127.0.0.1$p")
    case "$out" in *429*) echo "ok   $p was limited ($(count "$out" 429) of 90 refused)" ;;
                   *) echo "FAIL $p was never limited, so the log check below proves nothing: $out"; FAIL=1 ;; esac
done
if docker logs "$NGX" 2>&1 | grep -q 'SECRETFLOODTOKEN'; then
    echo "FAIL a token on a rate-limited request reached the nginx logs:"; docker logs "$NGX" 2>&1 | grep 'SECRETFLOODTOKEN' | head -3; FAIL=1
else
    echo "ok   no token of a rate-limited request is in the nginx logs"
fi

echo "-- EDGE-02: /mcp has a body cap and its own zone (600r/m, burst 200)"
start_nginx
code=$(status_with_body 1500000 http://127.0.0.1/mcp)
[ "$code" = 413 ] && echo "ok   /mcp refuses a 1.5 MB body (413)" || { echo "FAIL /mcp with a 1.5 MB body answered $code, expected 413"; FAIL=1; }
code=$(status_with_body 200000 http://127.0.0.1/mcp)
[ "$code" = 200 ] && echo "ok   /mcp takes a 200 KB body (200)" || { echo "FAIL /mcp with a 200 KB body answered $code, expected 200"; FAIL=1; }
# 800 back-to-back requests: 1 + burst 200 pass, then 429 (10 a second refill, so 800 cannot all pass
# unless the run takes a minute). The first 200 untouched is the "legitimate client" half.
start_nginx
out=$(codes 800 http://127.0.0.1/mcp)
n200=$(count "$out" 200); n429=$(count "$out" 429)
case "$out" in "200 200 200 200 200 200 200 200 200 200 "*) first=1 ;; *) first=0 ;; esac
if [ "$n200" -ge 201 ] && [ "$n429" -ge 1 ] && [ "$first" -eq 1 ]; then echo "ok   /mcp is limited by its own zone ($n200 passed, $n429 refused)"
else echo "FAIL /mcp zone: want at least 201 200s and then 429s, got $n200 x200 and $n429 x429"; FAIL=1; fi
# ... and it is its own zone: a neighbouring API route still answers.
code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1/api/v1/anything)
[ "$code" = 200 ] && echo "ok   the /mcp flood spent only its own zone" || { echo "FAIL /api/v1/anything answered $code after the /mcp flood"; FAIL=1; }

echo "-- EDGE-02: the protected-resource metadata prefix is no second door to the MCP transport"
# The MCP server answers this path on GET only; every other method would reach its transport
# without the /mcp zone and body cap. 300 back-to-back POSTs: none may reach the upstream.
start_nginx
WK=http://127.0.0.1/.well-known/oauth-protected-resource
out=$(codes 300 -X POST -H 'Authorization: Bearer flood' -H 'Content-Type: application/json' -d '{}' "$WK/mcp")
n200=$(count "$out" 200); n403=$(count "$out" 403); n429=$(count "$out" 429)
# The location's limit_req runs before limit_except, so past the burst a POST is a 429, not a 403.
if [ "$n200" -eq 0 ] && [ $((n403 + n429)) -eq 300 ]; then echo "ok   a POST flood to $WK/mcp is refused at the edge ($n403 x403, $n429 x429, no 200)"
else echo "FAIL POST flood to $WK/mcp: want only 403/429 and no 200, got $n200 x200, $n403 x403, $n429 x429"; FAIL=1; fi
start_nginx
code=$(status_with_body 1500000 "$WK/mcp")
[ "$code" = 403 ] || [ "$code" = 413 ] && echo "ok   a 1.5 MB POST to $WK/mcp is refused ($code)" \
    || { echo "FAIL a 1.5 MB POST to $WK/mcp answered $code, expected 403 or 413"; FAIL=1; }
for m in PUT DELETE; do
    code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' -X "$m" "$WK")
    [ "$code" = 403 ] && echo "ok   $m $WK is refused (403)" || { echo "FAIL $m $WK answered $code, expected 403"; FAIL=1; }
done
# Discovery itself answers, on both forms, on a fresh zone (start_nginx above reset it); the
# GET flood below then shows a same-address flood is held to the zone.
for p in "" /mcp; do
    code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' "$WK$p")
    [ "$code" = 200 ] && echo "ok   GET $WK$p still answers (200)" || { echo "FAIL GET $WK$p answered $code, expected 200"; FAIL=1; }
done
code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' -I "$WK/mcp")
[ "$code" = 200 ] && echo "ok   HEAD $WK/mcp still answers (200)" || { echo "FAIL HEAD $WK/mcp answered $code, expected 200"; FAIL=1; }
# GET is let through to the MCP server, so it carries the /mcp body cap: a 1.5 MB GET body is 413.
code=$(docker exec "$NGX" sh -c 'head -c 1500000 /dev/zero | curl -s -o /dev/null -w "%{http_code}" -X GET \
    -H "Content-Type: application/octet-stream" --data-binary @- "$0"' "$WK/mcp")
[ "$code" = 413 ] && echo "ok   a 1.5 MB GET body to $WK/mcp is refused (413)" \
    || { echo "FAIL a 1.5 MB GET body to $WK/mcp answered $code, expected 413"; FAIL=1; }
# ... and the /mcp zone: a bearer GET flood on suffixed paths (each still under the prefix) is
# throttled. 300 per suffix, back to back: the first batch passes 1 + burst 200 then 429s, the
# later batches find the bucket empty. Every batch must see 429s.
start_nginx
for sfx in X %2Fmcp /./ /mcp; do
    out=$(codes 300 -H 'Authorization: Bearer flood' "$WK$sfx")
    n200=$(count "$out" 200); n429=$(count "$out" 429)
    if [ "$n429" -ge 1 ] && [ $((n200 + n429)) -eq 300 ]; then echo "ok   GET flood to $WK$sfx is held to the /mcp zone ($n200 passed, $n429 x429)"
    else echo "FAIL GET flood to $WK$sfx: want 429s, got $n200 x200 and $n429 x429"; FAIL=1; fi
done

echo "-- S-05: case and trailing-slash variants of the marketing paths share auth_public"
# ASP.NET routes these case-insensitively and accepts a trailing slash, so each variant reaches
# the same action. Before the fix they fell through to /api/v1/ (or /) and skipped the limit.
start_nginx
for p in /api/v1/marketing/Subscribe/ /API/V1/MARKETING/CONFIRM /api/v1/marketing/unsubscribe/; do
    probe "$p" "$CLIENT" -X POST "http://127.0.0.1$p"
done
out=""
for p in /api/v1/marketing/CONFIRM/ /Api/v1/Marketing/UnSubscribe; do
    out="$out$(codes 1 -X POST "http://127.0.0.1$p")"
done
out="$out$(codes 3 -X POST http://127.0.0.1/api/v1/marketing/subscribe/)"
case "$out" in "200 200 200 429 429 ") echo "ok   variants share one auth_public bucket (3 probes + 2 + 3) -> $out" ;;
               *) echo "FAIL marketing variants: want 200 200 200 429 429 after 3 probes, got $out"; FAIL=1 ;; esac

echo "-- basic-auth front door on"
start_nginx -e BASIC_AUTH_USER=t015 -e BASIC_AUTH_PASSWORD=t015pw
docker exec "$NGX" nginx -t 2>&1 | tail -1
# The basic-auth probe carries an Authorization header, which $auth_header passes through
# unchanged (auth.js), so the upstream sees it.
WANT_AUTH="Basic $(printf '%s' 't015:t015pw' | base64)" \
    probe "/api/v1/anything (authenticated)" "$UPSTREAM" -u t015:t015pw http://127.0.0.1/api/v1/anything
probe "/api/v1/marketing/unsubscribe (auth_basic off)" "$CLIENT" http://127.0.0.1/api/v1/marketing/unsubscribe
# Anonymous output links: reachable without the basic-auth front door, upstream sees the real
# client address, and the token never reaches the container's log output (stdout).
probe "/api/v1/output-links/{token} (auth_basic off)" "$UPSTREAM" http://127.0.0.1/api/v1/Output-Links/SECRETTOKEN123.sig
if docker logs "$NGX" 2>&1 | grep -q 'SECRETTOKEN123'; then
    echo "FAIL output-links token reached the nginx logs"; FAIL=1
else
    echo "ok   output-links token is not in the nginx logs"
fi
probe "/api/v1/review-links/{token} (auth_basic off)" "$UPSTREAM" http://127.0.0.1/api/v1/Review-Links/SECRETREVIEWTOKEN123/media
if docker logs "$NGX" 2>&1 | grep -q 'SECRETREVIEWTOKEN123'; then
    echo "FAIL review-links token reached the nginx logs"; FAIL=1
else
    echo "ok   review-links token is not in the nginx logs"
fi

# Tracked links and the visit beacon: reachable without the basic-auth front door (a link opened from a
# post has no credentials, and neither has the site's relay, which calls the API's own path), also on
# the paths that never reach the API. The beacon carries the client's Host. A credential sent anyway
# does not reach the API on either address.
probe "/l/{code} (auth_basic off)" "$UPSTREAM" -H 'Authorization: Bearer leak' http://127.0.0.1/l/some-code
probe "/api/v1/l/{code} (auth_basic off)" "$UPSTREAM" -H 'Authorization: Bearer leak' -H 'Cookie: reelbolt_token=TESTTOKEN' http://127.0.0.1/api/v1/l/some-code
probe "/api/v1/telemetry/visit (auth_basic off, client Host)" "$CLIENT" -X POST http://127.0.0.1/api/v1/telemetry/visit
for p in /l/Bad_Code /l/api-down /api/v1/l/Bad_Code /api/v1/l/api-down; do
    code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1$p")
    [ "$code" = 302 ] && echo "ok   $p needs no credentials (302)" || { echo "FAIL $p answered $code behind the front door, expected 302"; FAIL=1; }
done
# The front door itself is where it was for every other API route.
code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1/api/v1/links)
[ "$code" = 401 ] && echo "ok   a neighbouring API route is still behind the front door (401)" || { echo "FAIL /api/v1/links answered $code behind the front door, expected 401"; FAIL=1; }
# Anonymous marketplace: reachable without basic auth, Authorization blanked toward the upstream,
# case variants share the location; the authenticated /api/v1/marketplace/ stays on the /api/v1/ block
# (its Authorization passes through $auth_header), and non-GET methods are refused.
probe "/api/v1/public/marketplace/recipes (auth_basic off)" "$UPSTREAM" -H 'Authorization: Bearer leak' -H 'Cookie: s=1' http://127.0.0.1/api/v1/Public/Marketplace/recipes
probe "/api/v1/public/marketplace/recipes/{id}/preview (auth_basic off)" "$UPSTREAM" -H 'Authorization: Bearer leak' http://127.0.0.1/api/v1/public/marketplace/recipes/abc/preview
code=$(docker exec "$NGX" curl -s -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1/api/v1/public/marketplace/recipes)
[ "$code" = 403 ] && echo "ok   marketplace POST refused (403)" || { echo "FAIL marketplace POST answered $code, expected 403"; FAIL=1; }
WANT_AUTH="Basic $(printf '%s' 't015:t015pw' | base64)" \
    probe "/api/v1/marketplace/recipes (authenticated route untouched)" "$UPSTREAM" -u t015:t015pw http://127.0.0.1/api/v1/marketplace/recipes

# The guest review PAGE carries the same token in its path: its own log format keeps it out of stdout.
curl -s -o /dev/null http://127.0.0.1/app/review/g/SECRETPAGETOKEN123 || true
if docker logs "$NGX" 2>&1 | grep -q 'SECRETPAGETOKEN123'; then
    echo "FAIL review page token reached the nginx logs"; FAIL=1
else
    echo "ok   review page token is not in the nginx logs"
fi

echo "-- REAL_IP_FROM_CLOUDFLARE=true (peer outside Cloudflare's ranges: CF-Connecting-IP ignored)"
start_nginx -e REAL_IP_FROM_CLOUDFLARE=true
probe "/api/v1/anything" "$UPSTREAM" http://127.0.0.1/api/v1/anything
probe "/api/v1/marketing/confirm" "$CLIENT" http://127.0.0.1/api/v1/marketing/confirm

echo "-- :443 server block (https-server.conf.template)"
PROTO=https probe "https /api/v1/anything" "$UPSTREAM" -k https://127.0.0.1/api/v1/anything
PROTO=https probe "https /api/v1/auth/token" "$CLIENT" -k https://127.0.0.1/api/v1/auth/token
PROTO=https probe "https /api/v1/marketing/subscribe" "$CLIENT" -k https://127.0.0.1/api/v1/marketing/subscribe

echo "-- REAL_IP_FROM_CLOUDFLARE=true (peer INSIDE a Cloudflare range: CF-Connecting-IP honoured)"
docker network create --subnet "$CF_SUBNET" "$NETCF" >/dev/null
start_nginx -e REAL_IP_FROM_CLOUDFLARE=true
docker network connect "$NETCF" "$NGX"
CFIP=$(docker inspect -f "{{(index .NetworkSettings.Networks \"$NETCF\").IPAddress}}" "$NGX")
CFU="http://$CFIP"
# cfget <CF-Connecting-IP> <path> [curl args...]: one request from the Cloudflare-range peer.
cfget() {
    ip=$1; p=$2; shift 2
    docker exec "$NGX" curl -s -H "CF-Connecting-IP: $ip" -H 'X-Real-IP: 6.6.6.6' \
        -H 'X-Forwarded-For: 6.6.6.6' "$@" "$CFU$p"
}
out=$(cfget 7.7.7.7 /api/v1/anything)
case "$out" in "xri=7.7.7.7 xff=6.6.6.6, 7.7.7.7 host=$UPSTREAM proto=http auth=") echo "ok   peer $CFIP: visitor from CF-Connecting-IP -> $out" ;;
               *) echo "FAIL peer $CFIP: CF-Connecting-IP not honoured -> $out"; FAIL=1 ;; esac
out=$(cfget 2001:db8:7::7 /api/v1/marketing/confirm -X POST)
case "$out" in "xri=2001:db8:7::7 xff=6.6.6.6, 2001:db8:7::7 host=$CFIP proto=http auth=") echo "ok   peer $CFIP: IPv6 visitor -> $out" ;;
               *) echo "FAIL peer $CFIP: IPv6 CF-Connecting-IP not honoured -> $out"; FAIL=1 ;; esac

# A tracked link clicked by an IPv6 visitor: the /64 arm of the $client_ip_key map matches for this
# address, which is exactly the case where a positional capture in the link location would be
# overwritten. The code must still be what reaches the API.
for p in /l/echo-uri /api/v1/l/echo-uri; do
    out=$(cfget 2001:db8:7::7 "$p")
    case "$out" in "uri=/api/v1/l/echo-uri cookie= auth= token=") echo "ok   peer $CFIP: an IPv6 visitor's tracked link ($p) keeps its code -> $out" ;;
                   *) echo "FAIL peer $CFIP: tracked link $p from an IPv6 visitor -> $out"; FAIL=1 ;; esac
done
# ... and the fallback redirect names the same code (the capture survives the error_page jump).
out=$(cfget 2001:db8:7::7 /l/api-down -o /dev/null -w '%{http_code} %{redirect_url}')
case "$out" in "302 $CFU/?utm_content=api-down") echo "ok   peer $CFIP: an IPv6 visitor's fallback keeps the code -> $out" ;;
               *) echo "FAIL peer $CFIP: fallback for an IPv6 visitor -> $out"; FAIL=1 ;; esac

echo "-- N-04: per-IP zones key an IPv6 visitor on its /64, an IPv4 visitor on its address"
# cfcodes <path> <CF-Connecting-IP>...: status codes, one POST per address, from the Cloudflare peer.
cfcodes() {
    p=$1; shift
    for ip in "$@"; do cfget "$ip" "$p" -X POST -o /dev/null -w '%{http_code} '; done
}
S=/api/v1/auth/signup
# Seven different /128s inside 2001:db8:1:2::/64: one bucket, so 1 + burst 5 pass and the 7th is 429.
out=$(cfcodes $S 2001:db8:1:2::1 2001:db8:1:2::2 2001:db8:1:2:ffff:ffff:ffff:ffff 2001:db8:1:2:a:b:c:d \
                 2001:db8:1:2:1:: 2001:db8:1:2:0:0:0:3 2001:db8:1:2:dead:beef::)
case "$out" in "200 200 200 200 200 200 429 ") echo "ok   seven addresses in one IPv6 /64 share a bucket -> $out" ;;
               *) echo "FAIL IPv6 /64 rotation: want 6x200 then 429, got $out"; FAIL=1 ;; esac
# The neighbouring /64s, below (::/64 boundary) and above, are separate buckets.
out=$(cfcodes $S 2001:db8:1:3::1 2001:db8:1:1:ffff:ffff:ffff:ffff 2001:db8::1)
case "$out" in "200 200 200 ") echo "ok   other IPv6 /64s keep their own bucket -> $out" ;;
               *) echo "FAIL other IPv6 /64s throttled with 2001:db8:1:2::/64: $out"; FAIL=1 ;; esac
# IPv4 is unchanged: 192.0.2.1 exhausts its own bucket, 192.0.2.2 is untouched.
out=$(cfcodes $S 192.0.2.1 192.0.2.1 192.0.2.1 192.0.2.1 192.0.2.1 192.0.2.1 192.0.2.1 192.0.2.2)
case "$out" in "200 200 200 200 200 200 429 200 ") echo "ok   IPv4 keeps a per-address bucket -> $out" ;;
               *) echo "FAIL IPv4 per-address key: want 6x200, 429, 200, got $out"; FAIL=1 ;; esac
# IPv4-mapped IPv6 addresses all sit in ::ffff:0:0/96 (one /64); they keep a per-address key.
out=$(cfcodes $S ::ffff:198.51.100.1 ::ffff:198.51.100.1 ::ffff:198.51.100.1 ::ffff:198.51.100.1 \
                 ::ffff:198.51.100.1 ::ffff:198.51.100.1 ::ffff:198.51.100.1 ::ffff:198.51.100.2)
case "$out" in "200 200 200 200 200 200 429 200 ") echo "ok   IPv4-mapped IPv6 keeps a per-address bucket -> $out" ;;
               *) echo "FAIL IPv4-mapped per-address key: want 6x200, 429, 200, got $out"; FAIL=1 ;; esac

exit "$FAIL"
