#!/bin/sh
# Renders nginx's Paddle webhook source-IP allowlist.
#
# Paddle publishes the source addresses its webhooks are delivered from at
# https://api.paddle.com/ips (unauthenticated). This script fetches that list at
# runtime and writes it as nginx access-control directives, so the list is never
# checked into the repository and a rotated range is picked up without a code
# change. The file named by $1 (default /etc/nginx/conf.d/paddle-webhook-allow.conf)
# is the one locations.conf `include`s inside the Paddle webhook location.
#
# It is FAIL-OPEN. If the fetch fails, the body is not the expected JSON, or
# `data.ipv4_cidrs` is empty or absent, it writes an EMPTY file and exits 0: the
# allowlist simply stays out of the way and the Paddle-Signature HMAC — the real
# credential — keeps guarding the route. A network blip must never stop payments.
#
# The feature is off by default. It fetches only when
# PADDLE_WEBHOOK_IP_ALLOWLIST=true; in every other case it empties the file.
#
# POSIX sh only (the nginx image is nginx:1.31.9-alpine); no jq, no bashisms.
set -eu

OUT="${1:-/etc/nginx/conf.d/paddle-webhook-allow.conf}"
URL="${PADDLE_IPS_URL:-https://api.paddle.com/ips}"
TMP="${OUT}.tmp.$$"

# Always leave a valid file behind, whatever happens next.
trap 'rm -f "$TMP"' EXIT HUP INT TERM

# True when $1 is exactly four dot-separated decimal octets, each 0-255,
# followed by '/' and a decimal mask 0-32 — the only shape nginx accepts in an
# `allow` directive. The charset filter below is not enough on its own:
# `1.2.3.4/999`, `1.2.3.4/`, `1.2.3`, `999.999.999.999` and `1.2.3.4/33` are
# all charset-valid, but a persisted one makes nginx refuse the whole config
# and the NEXT container start exits 1 before serving — crash-looping the
# origin under `restart: unless-stopped`. Anything that fails is skipped.
# POSIX sh only (no `local`), so the helpers are underscore-prefixed.
is_valid_cidr() {
    _c=$1
    case "$_c" in
        *[!0-9./]*) return 1 ;;   # digits, dots and slashes only
    esac
    case "$_c" in
        */*/*) return 1 ;;        # at most one slash...
        */*) ;;                   # ...and at least one
        *) return 1 ;;
    esac
    _net=${_c%/*}
    _mask=${_c##*/}
    case "$_net" in
        *.*.*.*) ;;               # at least three dots...
        *) return 1 ;;
    esac
    case "$_net" in
        *.*.*.*.*) return 1 ;;    # ...and not a fourth
        *..*|.*|*.) return 1 ;;   # no empty octet
    esac
    _o1=${_net%%.*}; _rest=${_net#*.}
    _o2=${_rest%%.*}; _rest=${_rest#*.}
    _o3=${_rest%%.*}; _o4=${_rest#*.}
    for _o in "$_o1" "$_o2" "$_o3" "$_o4"; do
        case "$_o" in
            ''|*[!0-9]*) return 1 ;;
        esac
        [ ${#_o} -le 3 ] || return 1   # bounds the numeric compare below
        [ "$_o" -le 255 ] || return 1
    done
    case "$_mask" in
        ''|*[!0-9]*) return 1 ;;
    esac
    [ ${#_mask} -le 2 ] || return 1
    [ "$_mask" -le 32 ] || return 1
    return 0
}

if [ "${PADDLE_WEBHOOK_IP_ALLOWLIST:-false}" != "true" ]; then
    : > "$OUT"
    echo "[paddle-ips] PADDLE_WEBHOOK_IP_ALLOWLIST is not 'true'; wrote empty $OUT" >&2
    exit 0
fi

if ! body=$(curl -fsS --connect-timeout 5 --max-time 10 "$URL"); then
    : > "$OUT"
    echo "[paddle-ips] fetch of $URL failed; wrote empty $OUT (fail open)" >&2
    exit 0
fi

# Pull the contents of the "ipv4_cidrs" array out of the JSON. Joining lines
# first means a pretty-printed body parses the same as a compact one; the array
# holds plain CIDR strings, so the first "]" after the "[" closes it.
cidrs=$(printf '%s' "$body" | tr '\n' ' ' \
    | sed -n 's/.*"ipv4_cidrs"[[:space:]]*:[[:space:]]*\[\([^]]*\)\].*/\1/p')

# One `allow <cidr>;` per entry. The charset filter keeps a hostile or
# malformed body from injecting nginx directives (a `;`, brace, quote, space or
# newline is rejected outright), and is_valid_cidr then requires the full
# CIDR shape, so a charset-valid but syntactically invalid token never reaches
# the generated file. The \r is stripped here too: a CRLF response would
# otherwise leave a carriage return on every entry, fail the filter and
# silently empty the allowlist.
# The trailing \n after $cidrs keeps `read` from dropping a last entry that has
# no comma after it (a line with no terminating newline is not read).
: > "$TMP"
printf '%s\n' "$cidrs" | tr ',' '\n' | while IFS= read -r entry; do
    cidr=$(printf '%s' "$entry" | tr -d ' \t"\r')
    [ -n "$cidr" ] || continue
    case "$cidr" in
        *[!0-9./]*) continue ;;
    esac
    is_valid_cidr "$cidr" || continue
    printf 'allow %s;\n' "$cidr"
done > "$TMP"

if [ ! -s "$TMP" ]; then
    : > "$OUT"
    echo "[paddle-ips] no usable data.ipv4_cidrs in the response from $URL; wrote empty $OUT (fail open)" >&2
    exit 0
fi

printf 'deny all;\n' >> "$TMP"
mv "$TMP" "$OUT"
echo "[paddle-ips] wrote $(wc -l < "$OUT" | tr -d ' ') lines (Paddle source addresses + deny all) to $OUT" >&2
exit 0
