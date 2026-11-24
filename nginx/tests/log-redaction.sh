#!/bin/sh
# Asserts the access-log redaction maps in nginx.conf cover every route that carries a capability
# token in its PATH. No stack needed: pulls the regexes out of nginx.conf and tests them with grep -P.
# ROUTES is the single source list; a route found in source but missing here fails the test.
set -eu
cd "$(dirname "$0")/../.."
CONF=nginx/nginx.conf
fail=0

# path|sample-token-position (token replaced by SECRET)
ROUTES='
/invite/SECRET
/app/invite/SECRET
/app/invite/SECRET/anything
/api/v1/auth/invitations/SECRET
/api/v1/auth/invitations/SECRET/accept
/api/v1/output-links/SECRET
/api/v1/review-links/SECRET
/api/v1/review-links/SECRET/media
/app/review/g/SECRET
/api/v1/runners/pair/SECRET
/api/v1/runners/pair/SECRET/approve
/api/v1/runners/pair/SECRET/deny
'
# Not tokens: must pass through unchanged.
PLAIN='
/api/v1/runners/pair/start
/api/v1/runners/pair/poll
/pricing
'

# Regexes of a map block, one per line (pattern only).
patterns() { # $1 = map variable name
  awk -v v="$1" '$0 ~ "map .* \\$" v " \\{" {on=1; next} on&&/^ *}/ {on=0} on&&/^ *~\*/ {sub(/^ *~\*/,""); sub(/ "\$1-\$2";$/,""); print}' "$CONF"
}
redact() { # $1 = map var, $2 = input
  printf '%s\n' "$2" | perl -e '
    my ($pats)=@ARGV; my $in=<STDIN>; chomp $in;
    open(my $f,"<",$pats); while(my $p=<$f>){chomp $p; if($in=~/$p/i){print "$1-$2\n"; exit}}
    print "$in\n"' "$3"
}
U=$(mktemp); R=$(mktemp); trap 'rm -f "$U" "$R"' EXIT
patterns log_uri_safe > "$U"; patterns log_referer_safe > "$R"
[ -s "$U" ] && [ -s "$R" ] || { echo "FAIL: could not extract redaction maps from $CONF"; exit 1; }
[ "$(wc -l < "$U")" -eq "$(wc -l < "$R")" ] || { echo "FAIL: uri and referrer maps differ in size"; fail=1; }

for r in $ROUTES; do
  out=$(redact u "$r" "$U")
  case "$out" in *SECRET*) echo "FAIL uri not redacted: $r -> $out"; fail=1;; esac
  out=$(redact r "app.example.com$r" "$R")
  case "$out" in *SECRET*) echo "FAIL referrer not redacted: $r -> $out"; fail=1;; esac
done
for r in $PLAIN; do
  [ "$(redact u "$r" "$U")" = "$r" ] || { echo "FAIL over-redacted: $r"; fail=1; }
done
grep -q 'log_uri_safe";' "$CONF" || true
grep -q '"uri":"\$log_uri_safe"' "$CONF" || { echo "FAIL: log_format does not use \$log_uri_safe"; fail=1; }
grep -q '"referrer":"\$log_referer_safe"' "$CONF" || { echo "FAIL: log_format does not use \$log_referer_safe"; fail=1; }
if grep -E 'log_format analytics' -A4 "$CONF" | grep -qE '\$request[^_]|\$args|\$query_string|\$request_uri|\$http_referer'; then
  echo "FAIL: analytics log_format logs a query-bearing variable"; fail=1; fi

grep -qE '^    root /usr/share/nginx/html;' "$CONF" || { echo "FAIL: http-level root missing (variable access_log path needs it)"; fail=1; }

# Coverage: every path-token route declared in source must have its prefix in ROUTES.
src=$( { grep -rhoE '"/api/v1/[^"]*\{(token|userCode)\}[^"]*"' api/handlers 2>/dev/null | tr -d '"' | sed -E 's/\{[a-zA-Z]+\}/SECRET/'
         find web/app site/app -type d -name '\[token\]' 2>/dev/null | sed -E 's#^web/app#/app#; s#^site/app##; s#/\([^)/]+\)##g; s#\[token\]#SECRET#'
         grep -l 'Route("api/v1/output-links")' inference/src/*/Controllers/*.cs >/dev/null 2>&1 && echo /api/v1/output-links/SECRET
         grep -l 'Route("api/v1/review-links/{token}")' inference/src/*/Controllers/*.cs >/dev/null 2>&1 && echo /api/v1/review-links/SECRET
       } | sort -u)
for r in $src; do
  echo "$ROUTES" | grep -qxF "$r" || { echo "FAIL: source route not in ROUTES (and so not proven redacted): $r"; fail=1; }
done
# public_api_log must never carry the Authorization header or a query-bearing variable.
pal=$(grep -A2 'log_format public_api_log' "$CONF")
[ -n "$pal" ] || { echo "FAIL: public_api_log format missing"; fail=1; }
if printf '%s\n' "$pal" | grep -qE '\$http_authorization|\$request([^_]|$)|\$args|\$query_string|\$request_uri'; then
  echo "FAIL: public_api_log logs a query-bearing or Authorization variable"; fail=1; fi

# Edge 429 on /api/public/ must be JSON with Retry-After.
L=nginx/locations.conf
grep -q 'error_page 429 = @public_api_rate_limited;' "$L" || { echo "FAIL: public api 429 error_page missing"; fail=1; }
blk=$(awk '/^location @public_api_rate_limited/ {on=1} on {print} on && /^}/ {exit}' "$L")
for k in 'Retry-After 60 always' 'RateLimit-Reset 60 always' 'application/json' '"code":"rate_limited"' '"resetsAt":null'; do
  printf '%s\n' "$blk" | grep -qF "$k" || { echo "FAIL: public api 429 block lacks $k"; fail=1; }
done

[ "$fail" -eq 0 ] && echo "log-redaction: ok ($(echo "$ROUTES" | grep -c SECRET) routes)"
exit "$fail"
