#!/bin/sh
# Renders every generated nginx file (TLS certificate choice, upstreams, server blocks) from the
# templates in /etc/nginx. Split out of docker-entrypoint.sh so the SAME code runs at container
# start and on reload-config.sh (a config change then ships by reload, not by recreating nginx,
# which would drop the front door for a few seconds). See docs/tls.md for the TLS picture.
#
# Without the two optional mounts below this behaves exactly as the entrypoint always did:
#   /etc/nginx/reelbolt-src     a read-only copy of the repo's nginx/ dir. When present, the config
#                               files are copied from it on EVERY render, so editing the repo and
#                               reloading is enough (self-host and the cloud image never have it).
#                               Either mount (or upstreams.env) turns SLOTS MODE on: only then are the
#                               conf.d/slots-* files rendered (previous-slot variables and fallbacks,
#                               private listener, worker_shutdown_timeout). nginx.conf, common and
#                               locations include them by glob, so without them nothing dangles.
#   /etc/nginx/reelbolt-deploy/nginx-src/current
#                               the activated release's nginx/ dir, installed by the operator release script at switch.
#                               When present it is the source INSTEAD of reelbolt-src, so a reload
#                               (a retire, the 12 h tick) can never load a config ahead of the switch.
#   /etc/nginx/reelbolt-deploy/upstreams.env
#                               KEY=VALUE lines for UPSTREAM_* written by the operator release script at a blue-green
#                               switch. Wins over the environment: it is the deploy's live state.
set -eu

SRC_DIR=/etc/nginx/reelbolt-src
UPSTREAMS_ENV=/etc/nginx/reelbolt-deploy/upstreams.env
RELEASE_SRC=/etc/nginx/reelbolt-deploy/nginx-src/current

SLOTS=0
if [ -d "$SRC_DIR" ] || [ -f "$UPSTREAMS_ENV" ]; then SLOTS=1; fi
if [ -d "$RELEASE_SRC" ]; then SRC_DIR="$RELEASE_SRC"; fi

if [ -d "$SRC_DIR" ]; then
    for f in nginx.conf auth.js i18n.js pick-locale.js locales.json locations.conf \
             proxy-client-headers.conf security-headers.conf cloudflare-realip.conf http-server-dev.conf \
             http-server-redirect.conf https-server.conf.template common.conf.template \
             runner-server.conf.template runner-locations.conf.template private-server.conf.template \
             slots-vars.conf.template slots-locations.conf fetch-paddle-ips.sh; do
        if [ -f "$SRC_DIR/$f" ]; then cp "$SRC_DIR/$f" "/etc/nginx/$f"; fi
    done
    chmod +x /etc/nginx/fetch-paddle-ips.sh 2>/dev/null || true
fi

# Parsed, never sourced: the file is state, not code, and a stray value must not become a command
# or break out of an nginx `set` line. Only UPSTREAM_* names and URL-shaped values are accepted.
if [ -f "$UPSTREAMS_ENV" ]; then
    while IFS='=' read -r k v || [ -n "$k" ]; do
        case "$k" in
            UPSTREAM_*[!A-Z_]*|UPSTREAM_) continue ;;
            UPSTREAM_*) ;;
            *) continue ;;
        esac
        case "$v" in
            ''|*[!A-Za-z0-9:/._-]*) echo "[nginx] ignoring $k in $UPSTREAMS_ENV: not a plain URL" >&2; continue ;;
        esac
        export "$k=$v"
    done < "$UPSTREAMS_ENV"
fi

DOMAIN="${DOMAIN:-}"
FORCE_HTTPS="${FORCE_HTTPS:-false}"
# Upstreams default to the compose service names; override for a multi-host deployment.
export UPSTREAM_GO_API="${UPSTREAM_GO_API:-http://go-api:8080}"
export UPSTREAM_INFERENCE_API="${UPSTREAM_INFERENCE_API:-http://inference:8080}"
export UPSTREAM_WORKFLOW_ENGINE="${UPSTREAM_WORKFLOW_ENGINE:-http://workflow-engine:8080}"
export UPSTREAM_WEB="${UPSTREAM_WEB:-http://web:3000}"
export UPSTREAM_SITE="${UPSTREAM_SITE:-http://site:3000}"
export UPSTREAM_DOCS="${UPSTREAM_DOCS:-http://docs:80}"
export UPSTREAM_CADDY="${UPSTREAM_CADDY:-http://caddy:80}"
# The MCP server's HTTP transport. It is now published at the public origin so an MCP client can
# authenticate with OAuth rather than being handed a pasted token (docs/design/mcp-oauth.md §8).
# In-network default, like every other upstream here.
export UPSTREAM_MCP="${UPSTREAM_MCP:-http://mcp:3002}"
# Previous-slot fallbacks (blue-green). They default to the ACTIVE value, so without a deploy
# state file every fallback location retries the very upstream that just answered 404 (harmless).
export UPSTREAM_INFERENCE_PREV="${UPSTREAM_INFERENCE_PREV:-$UPSTREAM_INFERENCE_API}"
export UPSTREAM_WEB_PREV="${UPSTREAM_WEB_PREV:-$UPSTREAM_WEB}"
export UPSTREAM_SITE_PREV="${UPSTREAM_SITE_PREV:-$UPSTREAM_SITE}"
export CLIENT_MAX_BODY_SIZE="${CLIENT_MAX_BODY_SIZE:-100M}"
REAL_IP_FROM_CLOUDFLARE="${REAL_IP_FROM_CLOUDFLARE:-false}"
UPSTREAM_RUNNER_GATEWAY="${UPSTREAM_RUNNER_GATEWAY:-}"
CERT_DIR=/etc/nginx/certs
CADDY_CERT_DIR="/caddy-data/caddy/certificates/acme-v02.api.letsencrypt.org-directory"

mkdir -p "$CERT_DIR" /etc/nginx/conf.d

if [ -n "${TLS_CERT_FILE:-}" ] && [ -n "${TLS_KEY_FILE:-}" ]; then
    # Operator-supplied certificate, e.g. a Cloudflare Origin CA cert covering
    # <domain> and *.<domain> (needed for the runner. hostname; docs/tls.md).
    echo "[nginx] Using the operator-supplied certificate $TLS_CERT_FILE"
    SSL_CERT="$TLS_CERT_FILE"
    SSL_KEY="$TLS_KEY_FILE"
elif [ -n "$DOMAIN" ] && [ -f "$CADDY_CERT_DIR/$DOMAIN/$DOMAIN.crt" ]; then
    echo "[nginx] Using the Let's Encrypt certificate Caddy obtained for $DOMAIN"
    SSL_CERT="$CADDY_CERT_DIR/$DOMAIN/$DOMAIN.crt"
    SSL_KEY="$CADDY_CERT_DIR/$DOMAIN/$DOMAIN.key"
else
    if [ -n "$DOMAIN" ]; then
        echo "[nginx] DOMAIN=$DOMAIN is set but no certificate found yet at $CADDY_CERT_DIR/$DOMAIN/ — has the 'caddy' service (compose 'tls' profile) been started and finished issuing one? Falling back to a self-signed certificate for now."
    fi
    if [ ! -f "$CERT_DIR/selfsigned.crt" ]; then
        echo "[nginx] Generating a self-signed certificate for local development (this is expected to trigger a browser warning)"
        openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
            -keyout "$CERT_DIR/selfsigned.key" -out "$CERT_DIR/selfsigned.crt" \
            -subj "/CN=${DOMAIN:-localhost}" \
            -addext "subjectAltName=DNS:${DOMAIN:-localhost}"
    fi
    SSL_CERT="$CERT_DIR/selfsigned.crt"
    SSL_KEY="$CERT_DIR/selfsigned.key"
fi

export SSL_CERT SSL_KEY
envsubst '${SSL_CERT} ${SSL_KEY}' < /etc/nginx/https-server.conf.template > /etc/nginx/conf.d/https-server.conf
envsubst '${CLIENT_MAX_BODY_SIZE} ${UPSTREAM_GO_API} ${UPSTREAM_INFERENCE_API} ${UPSTREAM_WORKFLOW_ENGINE} ${UPSTREAM_WEB} ${UPSTREAM_SITE} ${UPSTREAM_DOCS} ${UPSTREAM_CADDY} ${UPSTREAM_MCP}' \
    < /etc/nginx/common.conf.template > /etc/nginx/conf.d/common.inc

if [ "$REAL_IP_FROM_CLOUDFLARE" = "true" ]; then
    echo "[nginx] REAL_IP_FROM_CLOUDFLARE=true — trusting CF-Connecting-IP from Cloudflare ranges"
    cp /etc/nginx/cloudflare-realip.conf /etc/nginx/conf.d/realip.conf
else
    : > /etc/nginx/conf.d/realip.conf
fi

# Paddle webhook source-IP allowlist (docs/billing.md). Rendered unconditionally on
# every start so the `include` in locations.conf can never dangle: the script writes an
# empty file when PADDLE_WEBHOOK_IP_ALLOWLIST is off (the default) and fails open to the
# same empty file when the fetch fails. The list is fetched from Paddle at runtime and
# never checked in; the reload loop below re-fetches it so a rotated range is picked up
# without a restart. Refreshing never fails the container — the script always exits 0.
/etc/nginx/fetch-paddle-ips.sh /etc/nginx/conf.d/paddle-webhook-allow.conf

# The allowlist compares the source address nginx sees against Paddle's published
# ranges. With REAL_IP_FROM_CLOUDFLARE not 'true' nginx sees the proxy/Cloudflare
# address instead of Paddle's, so the trailing `deny all` rejects every Paddle
# webhook and silently stops billing. Warn loudly rather than refuse: a
# deployment may legitimately have no Cloudflare in front.
if [ "${PADDLE_WEBHOOK_IP_ALLOWLIST:-false}" = "true" ] && [ "$REAL_IP_FROM_CLOUDFLARE" != "true" ]; then
    echo "[nginx] WARNING: PADDLE_WEBHOOK_IP_ALLOWLIST=true but REAL_IP_FROM_CLOUDFLARE is not 'true' — nginx will see the proxy/Cloudflare address, not Paddle's, so 'deny all' rejects every Paddle webhook and billing stops. Set REAL_IP_FROM_CLOUDFLARE=true (behind Cloudflare) or leave PADDLE_WEBHOOK_IP_ALLOWLIST=false." >&2
fi

# The runner gateway. It is published as paths on the app's own host
# (/runner/v1/connect, /runner/telemetry/v1/reports), because every ReelBolt address
# lives under one host. A separate runner.<domain> server is rendered only when
# RUNNER_HOSTNAME names one explicitly; it is never derived from DOMAIN, since a
# hostname nobody created in DNS is a server block nothing can reach.
UPSTREAM_RUNNER_GATEWAY="${UPSTREAM_RUNNER_GATEWAY:-}"
UPSTREAM_RUNNER_GATEWAY="${UPSTREAM_RUNNER_GATEWAY%/}"
if [ -n "$UPSTREAM_RUNNER_GATEWAY" ]; then
    echo "[nginx] Routing /runner/v1/connect and /runner/telemetry/v1/reports to $UPSTREAM_RUNNER_GATEWAY"
    envsubst '${UPSTREAM_RUNNER_GATEWAY}' \
        < /etc/nginx/runner-locations.conf.template > /etc/nginx/conf.d/runner-locations.inc
else
    : > /etc/nginx/conf.d/runner-locations.inc
fi
if [ -n "$UPSTREAM_RUNNER_GATEWAY" ] && [ -n "${RUNNER_HOSTNAME:-}" ]; then
    echo "[nginx] Also routing wss://$RUNNER_HOSTNAME/v1/connect to $UPSTREAM_RUNNER_GATEWAY"
    envsubst '${SSL_CERT} ${SSL_KEY} ${RUNNER_HOSTNAME} ${UPSTREAM_RUNNER_GATEWAY}' \
        < /etc/nginx/runner-server.conf.template > /etc/nginx/conf.d/runner-server.conf
else
    : > /etc/nginx/conf.d/runner-server.conf
fi

# Staging front door (WP F14, docs/staging.md). One shared credential in front of the
# whole origin, so an unreleased environment is not public. Both variables empty (the
# default, and every self-host install) renders an empty include, i.e. no basic auth.
# Two locations opt out in locations.conf: `= /health`, which deploy.yml probes from
# the VM, and the Paddle webhook, whose only credential is the signature HMAC.
if [ -n "${BASIC_AUTH_USER:-}" ] && [ -n "${BASIC_AUTH_PASSWORD:-}" ]; then
    printf '%s:%s\n' "$BASIC_AUTH_USER" "$(openssl passwd -apr1 "$BASIC_AUTH_PASSWORD")" \
        > /etc/nginx/conf.d/basic-auth.htpasswd
    # 0644: the nginx worker (user nginx, not root) re-reads this file on EVERY request,
    # so a 0600/0640 file turns every authenticated request into a 500. It holds one apr1
    # hash per user, which is the format's whole point.
    chmod 644 /etc/nginx/conf.d/basic-auth.htpasswd
    printf 'auth_basic "%s";\nauth_basic_user_file /etc/nginx/conf.d/basic-auth.htpasswd;\n' \
        "${BASIC_AUTH_REALM:-ReelBolt}" > /etc/nginx/conf.d/basic-auth.inc
    echo "[nginx] Basic-auth front door on for user '$BASIC_AUTH_USER' (/health and the Paddle webhook stay open)"
elif [ -n "${BASIC_AUTH_USER:-}" ] || [ -n "${BASIC_AUTH_PASSWORD:-}" ]; then
    echo "[nginx] BASIC_AUTH_USER and BASIC_AUTH_PASSWORD must be set together, refusing to start" >&2
    exit 1
else
    : > /etc/nginx/conf.d/basic-auth.inc
fi

# Slots mode only (blue-green): the files nginx.conf, common.conf.template and locations.conf include
# by glob. Without them (self-host, the cloud image, a legacy deploy) none of this exists.
rm -f /etc/nginx/conf.d/slots-*
if [ "$SLOTS" = 1 ]; then
    printf 'worker_shutdown_timeout 45m;\n' > /etc/nginx/conf.d/slots-main.conf
    envsubst '${UPSTREAM_INFERENCE_PREV} ${UPSTREAM_WEB_PREV} ${UPSTREAM_SITE_PREV}' \
        < /etc/nginx/slots-vars.conf.template > /etc/nginx/conf.d/slots-vars.inc
    cp /etc/nginx/slots-locations.conf /etc/nginx/conf.d/slots-locations.inc
    # recursive_error_pages must be on HERE, in the location that first redirects: nginx marks the request as
    # "already redirected" at that first error_page, using this location's setting, so the 502/503/504 handling
    # inside the @*_prev locations never runs if it is only set there.
    for n in chat approvals web_static site_static; do
        printf 'proxy_intercept_errors on;\nrecursive_error_pages on;\nerror_page 404 = @%s_prev;\n' "$n" > "/etc/nginx/conf.d/slots-err-$n.inc"
    done
    # Private listener for the engine-to-API path: the media box's engine calls the inference API on
    # CONTROL_PRIVATE_IP:8080, which nginx owns so the API behind it can switch slots without the
    # engine noticing. Only when NGINX_PRIVATE_PORT is set.
    if [ -n "${NGINX_PRIVATE_PORT:-}" ]; then
        echo "[nginx] Private engine-to-API listener on :$NGINX_PRIVATE_PORT"
        export NGINX_PRIVATE_PORT NGINX_PRIVATE_MAX_BODY="${NGINX_PRIVATE_MAX_BODY:-2g}"
        envsubst '${NGINX_PRIVATE_PORT} ${NGINX_PRIVATE_MAX_BODY} ${UPSTREAM_INFERENCE_API}' \
            < /etc/nginx/private-server.conf.template > /etc/nginx/conf.d/slots-private-server.conf
    fi
fi

# The deployment's storage origin (R2/S3) for the report-only Content-Security-Policy: the browser fetches
# presigned media and uploads from it (media-src, connect-src). Taken from MINIO_PUBLIC_ENDPOINT, reduced
# to scheme://host[:port]; anything that is not a plain http(s) origin (or empty) renders no source, so a
# stray value can never inject a directive. $sh_csp_storage is empty or " https://host" (nginx.conf).
SH_STORAGE=""
case "${MINIO_PUBLIC_ENDPOINT:-}" in
    http://*|https://*)
        SH_ORIGIN=$(printf '%s' "$MINIO_PUBLIC_ENDPOINT" | sed -n 's#^\(https\{0,1\}://[A-Za-z0-9.-]*\(:[0-9]\{1,5\}\)\{0,1\}\)\(/.*\)\{0,1\}$#\1#p')
        if [ -n "$SH_ORIGIN" ]; then SH_STORAGE=" $SH_ORIGIN"; else echo "[nginx] MINIO_PUBLIC_ENDPOINT is not a plain http(s) origin; the CSP will not name it" >&2; fi ;;
esac
rm -f /etc/nginx/conf.d/security-storage.conf
printf 'default "%s";\n' "$SH_STORAGE" > /etc/nginx/conf.d/security-storage.map

# HSTS only when FORCE_HTTPS=true, i.e. once the operator has confirmed https:// works: a browser
# that has seen the header refuses plain http for the host for max-age, so it must never be sent by
# an install that still serves http://. includeSubDomains is on: every hostname under the domain is
# then https-only for a year, which holds for the hosts this stack serves. NO preload, and we do NOT
# submit the domain to the HSTS preload list: preload is a near-permanent, browser-shipped commitment
# and may only be considered once EVERY subdomain (downloads., cdn., runner., test. and any other,
# including ones outside this stack) is served over https. Included by glob (hsts*.inc) from
# security-headers.conf and runner-server.conf.template.
rm -f /etc/nginx/conf.d/hsts.inc
if [ "$FORCE_HTTPS" = "true" ]; then
    echo "[nginx] FORCE_HTTPS=true — redirecting http:// to https://"
    cp /etc/nginx/http-server-redirect.conf /etc/nginx/conf.d/http-server.conf
    printf 'add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;\n' > /etc/nginx/conf.d/hsts.inc
else
    cp /etc/nginx/http-server-dev.conf /etc/nginx/conf.d/http-server.conf
fi
