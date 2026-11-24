#!/bin/sh
# Renders nginx's :80/:443 server blocks and picks a TLS certificate before
# starting nginx. See docs/tls.md for the full picture; short version:
#
#   - No DOMAIN set (default): self-signed cert, generated once and reused,
#     so https://localhost works out of the box alongside http://.
#   - DOMAIN set and the `caddy` sidecar (compose `tls` profile) has already
#     obtained a Let's Encrypt cert for it: use that cert instead.
#
# Switching from "no domain" to "has domain" needs one manual container
# restart (this script re-runs and picks up the new cert path). After that,
# renewals are picked up automatically by the periodic `nginx -s reload`
# loop below — Caddy renews the file in place, nginx just needs to re-open it.
set -eu

# All rendering lives in render-config.sh so reload-config.sh can run the same code.
/etc/nginx/render-config.sh


# Persistent access logs (volume nginx_logs). Workers open the daily files, so the directory must
# be writable by them; retention is a daily busybox crond job. Fail-safe: never blocks startup.
LOG_DIR=/var/log/nginx
NGINX_LOG_RETENTION_DAYS="${NGINX_LOG_RETENTION_DAYS:-30}"
mkdir -p "$LOG_DIR"
# Narrowest grant: owned by the worker user, mode 0750. If chown fails, warn rather than widen.
if chown nginx:nginx "$LOG_DIR" 2>/dev/null; then chmod 750 "$LOG_DIR" 2>/dev/null || true
else echo "[nginx] WARN: cannot chown $LOG_DIR to nginx; access logging may fail" >&2; fi
case "$NGINX_LOG_RETENTION_DAYS" in
    ''|*[!0-9]*) echo "[nginx] NGINX_LOG_RETENTION_DAYS='$NGINX_LOG_RETENTION_DAYS' is not a number, using 30" >&2; NGINX_LOG_RETENTION_DAYS=30 ;;
esac
if command -v crond >/dev/null 2>&1; then
    mkdir -p /etc/crontabs
    echo "17 3 * * * find $LOG_DIR -name 'access-*.log' -type f -mtime +$NGINX_LOG_RETENTION_DAYS -delete" > /etc/crontabs/root
    crond -b -L /dev/stderr 2>/dev/null || true
    sleep 1
    if ! pgrep -x crond >/dev/null 2>&1 && ! ps 2>/dev/null | grep -q '[c]rond'; then
        echo "[nginx] WARN: crond is not running; access logs will NOT be pruned (see infra/README.md for the manual find)" >&2
    fi
else
    echo "[nginx] WARN: crond not available; access logs will NOT be pruned (see infra/README.md for the manual find)" >&2
fi

# Pick up a freshly (re)issued/renewed certificate without a full restart.
# Caddy renews roughly a month before expiry; checking twice a day is more
# than enough, and a reload is a cheap no-op when the file hasn't changed.
# The same tick re-fetches Paddle's published webhook IPs, so a rotated range
# takes effect without a container restart (docs/billing.md).
(
    while true; do
        sleep 43200
        /etc/nginx/fetch-paddle-ips.sh /etc/nginx/conf.d/paddle-webhook-allow.conf
        # With the blue-green source mount the reload also re-renders (a failed render or test
        # keeps the running config); without it, the plain reload the entrypoint always did.
        if [ -d /etc/nginx/reelbolt-src ]; then
            /etc/nginx/reload-config.sh || true
        else
            nginx -s reload 2>/dev/null || true
        fi
    done
) &

exec nginx -g 'daemon off;'
