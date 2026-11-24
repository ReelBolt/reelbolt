#!/bin/sh
# Re-renders the config, tests it and reloads nginx: how a config or upstream change ships
# without recreating the container. If the render or `nginx -t` fails, the previous files are put
# back and the running nginx is never signalled, so a bad change costs nothing. Exit 0 only when
# the new config is live.
set -u

ETC=/etc/nginx
BK="$(mktemp -d)"
trap 'rm -rf "$BK"' EXIT

cp -a "$ETC/." "$BK/"

restore() {
    echo "[nginx] reload-config: $1; restoring the previous files, nginx keeps running untouched" >&2
    cp -a "$BK/." "$ETC/"
}

if ! "$ETC/render-config.sh"; then restore "render failed"; exit 1; fi
if ! nginx -t; then restore "nginx -t failed"; exit 1; fi
if ! nginx -s reload; then restore "reload signal failed"; exit 1; fi
echo "[nginx] reload-config: new config is live"
