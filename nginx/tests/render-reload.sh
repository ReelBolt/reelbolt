#!/bin/sh
# Renders and reloads the nginx image's config for real (docker, nginx -t, curl inside the
# container). Covers: the entrypoint's behaviour WITHOUT the blue-green mounts (self-host and cloud
# image), WITH them (source dir, upstreams.env, private listener), a reload that ships a change, a
# reload that must refuse a broken change and leave nginx untouched, and the previous-slot fallback
# including that a POST body survives the retry. Skips (exit 0) when docker is unavailable.
set -eu
cd "$(dirname "$0")/../.."
command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1 || { echo "skip: docker not available"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "skip: python3 not available"; exit 0; }

work="$(mktemp -d)"
IMG="reelbolt-nginx-render-test:$$"
fail=0
containers=""
pids=""
cleanup() {
  for c in $containers; do docker rm -f "$c" >/dev/null 2>&1 || true; done
  for p in $pids; do kill "$p" 2>/dev/null || true; done
  docker rmi -f "$IMG" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
ok() { printf '  ok   %s\n' "$1"; }
ko() { printf '  FAIL %s\n' "$1"; fail=$((fail + 1)); }
check() { # description, command...
  d="$1"; shift
  if "$@" >"$work/out" 2>&1; then ok "$d"; else ko "$d"; sed 's/^/       /' "$work/out" | tail -15; fi
}

# The real Dockerfile needs the network (apk). Offline, reuse the tools layer of any locally built
# ReelBolt nginx image and overlay this tree's files on it (the COPY lines are what is under test).
if ! docker build -q -t "$IMG" nginx >/dev/null 2>&1; then
  base="$(docker images --format '{{.Repository}}:{{.Tag}}' | grep -- '-nginx:' | head -1)"
  [ -n "$base" ] || { echo "image build failed and no local nginx image to reuse"; exit 2; }
  echo "note: offline build, reusing $base for openssl/envsubst/curl"
  sed -e "s#^FROM .*#FROM $base#" -e '/apk add/d' nginx/Dockerfile >"$work/Dockerfile"
  docker build -q -t "$IMG" -f "$work/Dockerfile" nginx >/dev/null || { echo "image build failed"; exit 2; }
fi

start() { # name, extra docker args...
  n="$1"; shift
  containers="$containers $n"
  docker run -d --name "$n" "$@" "$IMG" >/dev/null
  i=0
  until docker exec "$n" nginx -t >/dev/null 2>&1 && docker exec "$n" test -s /var/run/nginx.pid >/dev/null 2>&1; do
    i=$((i + 1)); [ "$i" -gt 60 ] && { echo "$n did not start"; docker logs "$n" | tail -20; exit 2; }
    sleep 0.5
  done
}
x() { n="$1"; shift; docker exec "$n" "$@"; }

echo "== without the blue-green mounts (entrypoint unchanged)"
start rb-nginx-plain-$$ -e DOMAIN=
check "nginx -t passes" x rb-nginx-plain-$$ nginx -t
check "no slots-only file is rendered" sh -c "[ -z \"\$(docker exec rb-nginx-plain-$$ sh -c 'ls /etc/nginx/conf.d | grep ^slots-')\" ]"
check "no previous-slot variables or fallback locations in the effective config" sh -c "! docker exec rb-nginx-plain-$$ nginx -T 2>/dev/null | grep -v '^[[:space:]]*#' | grep -vF 'proxy_set_header X-ReelBolt-Slot-Fallback \"\";' | grep -Eq '_prev|worker_shutdown_timeout|Slot-Fallback'"
check "no listener on the private port" sh -c "! docker exec rb-nginx-plain-$$ curl -s -m 2 http://127.0.0.1:8081/ >/dev/null"

echo "== the OLD entrypoint (origin/master) with this tree's config bind-mounted as docker-compose.yml does"
if git show origin/master:nginx/docker-entrypoint.sh >"$work/old-entrypoint.sh" 2>/dev/null; then
  chmod +x "$work/old-entrypoint.sh"
  mounts=""
  for f in $(grep -E '^[[:space:]]+- \./nginx/[^:]+:/etc/nginx/[^:]+:ro' docker-compose.yml | sed -E 's#^[[:space:]]+- \./nginx/([^:]+):.*#\1#'); do
    mounts="$mounts -v $PWD/nginx/$f:/etc/nginx/$f:ro"
  done
  O=rb-nginx-old-$$
  # shellcheck disable=SC2086
  start "$O" -e DOMAIN= --entrypoint /old-entrypoint.sh -v "$work/old-entrypoint.sh:/old-entrypoint.sh:ro" $mounts
  check "nginx -t passes under the old entrypoint" x $O nginx -t
  check "the old entrypoint renders no slots file and the tree's config has none of its directives" sh -c "! docker exec $O nginx -T 2>/dev/null | grep -v '^[[:space:]]*#' | grep -vF 'proxy_set_header X-ReelBolt-Slot-Fallback \"\";' | grep -Eq '_prev|worker_shutdown_timeout|Slot-Fallback|listen 8081'"
  check "the old entrypoint still serves" sh -c "docker exec $O curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1/health | grep -Eq '^[0-9]{3}$'"
else
  ko "cannot read origin/master:nginx/docker-entrypoint.sh (is origin/master fetched?)"
fi

echo "== backends for the fallback test (host side)"
GW="$(docker network inspect bridge -f '{{(index .IPAM.Config 0).Gateway}}')"
cat >"$work/backend.py" <<'PY'
import hashlib, http.server, sys
mode, port = sys.argv[1], int(sys.argv[2])
class H(http.server.BaseHTTPRequestHandler):
    def _go(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n) if n else b""
        import re
        if self.path == "/whoami":
            out = ("xri=%s xff=%s hdr=%s" % (self.headers.get("X-Real-IP"), self.headers.get("X-Forwarded-For"), self.headers.get("X-ReelBolt-Slot-Fallback"))).encode()
            self.send_response(200); self.send_header("Content-Length", str(len(out))); self.end_headers(); self.wfile.write(out); return
        tm = re.match(r"^/api/v1/assistant/threads/([^/]+)/turn$", self.path)
        if tm:
            tid, h = tm.group(1), self.headers.get("X-ReelBolt-Slot-Fallback")
            if (tid == "livenew" and mode == "new") or (tid == "liveold" and mode == "old"):
                code, out = 200, ("%s live hdr=%s" % (mode, h)).encode()
            elif tid == "foreign":
                code, out = 404, ("%s foreign" % mode).encode()
            elif h == "1":
                code, out = 404, ("%s idle-404" % mode).encode()
            else:
                code, out = 200, ("%s idle hdr=%s" % (mode, h)).encode()
            self.send_response(code); self.send_header("Content-Length", str(len(out))); self.end_headers(); self.wfile.write(out); return
        if self.path.endswith("/assistant/chat") and mode == "new" and b"attach" not in body:
            # a normal turn: a chunked stream whose first chunk must reach the client before the second exists
            import time
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.send_header("Transfer-Encoding", "chunked"); self.end_headers()
            for part in (b"first\n", b"second\n"):
                self.wfile.write(b"%x\r\n%s\r\n" % (len(part), part)); self.wfile.flush(); time.sleep(2)
            self.wfile.write(b"0\r\n\r\n"); return
        if mode == "new" and ("approvals" in self.path or "assistant/chat" in self.path or self.path.endswith("old.js")):
            code, out = 404, b"new slot: unknown"
        else:
            code = 200
            out = ("%s auth=%s len=%d sha=%s hdr=%s" % (mode, self.headers.get("Authorization"), len(body), hashlib.sha256(body).hexdigest(), self.headers.get("X-ReelBolt-Slot-Fallback"))).encode()
        self.send_response(code); self.send_header("Content-Length", str(len(out))); self.end_headers(); self.wfile.write(out)
    do_GET = do_POST = _go
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(("0.0.0.0", port), H).serve_forever()
PY
NEWP=$((20000 + $$ % 20000)); OLDP=$((NEWP + 1))
python3 "$work/backend.py" new "$NEWP" & pids="$pids $!"
python3 "$work/backend.py" old "$OLDP" & pids="$pids $!"
sleep 1

echo "== with the mounts (source dir, upstreams.env, private listener)"
mkdir -p "$work/src" "$work/deploy"
cp -a nginx/. "$work/src/"
printf 'UPSTREAM_INFERENCE_API=http://%s:%s\nUPSTREAM_INFERENCE_PREV=http://%s:%s\nUPSTREAM_WEB=http://%s:%s\nUPSTREAM_WEB_PREV=http://%s:%s\nUPSTREAM_BAD=http://x;evil\n' \
  "$GW" "$NEWP" "$GW" "$OLDP" "$GW" "$NEWP" "$GW" "$OLDP" >"$work/deploy/upstreams.env"
N=rb-nginx-slots-$$
start "$N" -e NGINX_PRIVATE_PORT=8081 -v "$work/src:/etc/nginx/reelbolt-src:ro" -v "$work/deploy:/etc/nginx/reelbolt-deploy:ro"
check "nginx -t passes" x $N nginx -t
check "slots mode renders the slots-only files and worker_shutdown_timeout" sh -c "docker exec $N sh -c 'ls /etc/nginx/conf.d/slots-vars.inc /etc/nginx/conf.d/slots-locations.inc /etc/nginx/conf.d/slots-private-server.conf' && docker exec $N nginx -T 2>/dev/null | grep -q 'worker_shutdown_timeout 45m' && docker exec $N cat /etc/nginx/conf.d/slots-vars.inc | grep -q 'set \$inference_prev http://$GW:$OLDP;'"
check "upstreams.env wins; a non-URL value is ignored" sh -c "docker exec $N cat /etc/nginx/conf.d/common.inc | grep -q 'set \$inference_api http://$GW:$NEWP;' && ! docker exec $N cat /etc/nginx/conf.d/common.inc | grep -q evil"
check "private listener proxies and passes Authorization untouched" sh -c "docker exec $N curl -s -m 5 -H 'Authorization: Bearer engine-token' http://127.0.0.1:8081/anything | grep -q 'new auth=Bearer engine-token'"
# EDGE-10: whatever a caller on the private network sends, the API sees the socket peer as the client address
# (overwritten, not appended to) and never nginx's own slot signal.
check "private listener overwrites X-Real-IP and X-Forwarded-For and blanks the slot header" sh -c "docker exec $N curl -s -m 5 -H 'X-Real-IP: 6.6.6.6' -H 'X-Forwarded-For: 6.6.6.6' -H 'X-ReelBolt-Slot-Fallback: 1' http://127.0.0.1:8081/whoami | grep -qx 'xri=127.0.0.1 xff=127.0.0.1 hdr=None'"

head -c 300000 /dev/urandom >"$work/body.bin"
want="$(sha256sum "$work/body.bin" | cut -d' ' -f1)"
docker cp "$work/body.bin" $N:/tmp/body.bin
check "approval POST: 404 on the new slot falls back to the previous one with the body intact" sh -c "docker exec $N curl -s -m 10 -X POST --data-binary @/tmp/body.bin -H 'Authorization: Bearer user-jwt' http://127.0.0.1/api/v1/assistant/approvals/abc/decision | grep -q 'old auth=Bearer user-jwt len=300000 sha=$want'"
check "chat re-attach: 404 on the new slot falls back to the previous one with the body intact" sh -c "docker exec $N curl -s -m 10 -X POST -H 'Authorization: Bearer user-jwt' -d '{\"threadId\":\"t\",\"attach\":true}' http://127.0.0.1/api/v1/assistant/chat | grep -q 'old auth=Bearer user-jwt len=30 '"
check "chat: a normal 200 stream is not intercepted and arrives unbuffered (first chunk well before the second)" sh -c "docker exec $N sh -c 't0=\$(date +%s%N); first=\$(curl -sN -m 10 -X POST -d \"{}\" http://127.0.0.1/api/v1/assistant/chat | head -n1); t1=\$(date +%s%N); [ \"\$first\" = first ] && [ \$(( (t1 - t0) / 1000000 )) -lt 1500 ]'"
check "other API paths do NOT fall back" sh -c "docker exec $N curl -s -m 5 http://127.0.0.1/api/v1/projects | grep -q '^new '"
check "/app/_next/static 404 falls back to the previous web slot" sh -c "docker exec $N curl -s -m 5 http://127.0.0.1/app/_next/static/chunks/old.js | grep -q '^old '"
check "/app/_next/static of a chunk the new slot has stays on it" sh -c "docker exec $N curl -s -m 5 http://127.0.0.1/app/_next/static/chunks/new.js | grep -q '^new '"

# R7: the assistant turn route. nginx sets the header (overwriting the client's); the API answers an idle
# turn 404 when it sees it, and nginx then asks the previous slot, header cleared.
T=http://127.0.0.1/api/v1/assistant/threads

check "turn: idle on the new slot, live on the previous one returns live" sh -c "docker exec $N curl -s -m 5 -w ' [%{http_code}]' $T/liveold/turn | grep -qx 'old live hdr=None \[200\]'"
check "turn: idle on both slots returns 200 idle" sh -c "docker exec $N curl -s -m 5 -w ' [%{http_code}]' $T/idle/turn | grep -qx 'old idle hdr=None \[200\]'"
check "turn: live on the new slot stays there, with the header set" sh -c "docker exec $N curl -s -m 5 $T/livenew/turn | grep -qx 'new live hdr=1'"
check "turn: a client-sent header is overwritten, not trusted" sh -c "docker exec $N curl -s -m 5 -H 'X-ReelBolt-Slot-Fallback: 0' $T/livenew/turn | grep -qx 'new live hdr=1'"
check "turn: a foreign thread (404 on both) stays 404" sh -c "docker exec $N curl -s -m 5 -w ' [%{http_code}]' $T/foreign/turn | grep -qx 'old foreign \[404\]'"
# EDGE-11: on every other path the client's header is dropped (proxy-client-headers.conf blanks it), including a
# case variant of the turn path, which the case-sensitive turn location does not take.
check "turn: a client-sent header never reaches the API on other paths (dropped, no fallback)" sh -c "docker exec $N curl -s -m 5 -H 'X-ReelBolt-Slot-Fallback: 1' http://127.0.0.1/api/v1/projects | grep -q '^new .*hdr=None\$' && docker exec $N curl -s -m 5 -w ' [%{http_code}]' -H 'X-ReelBolt-Slot-Fallback: 1' $T/idle/turn/x | grep -q '^new .*hdr=None \[200\]\$' && docker exec $N curl -s -m 5 -w ' [%{http_code}]' -H 'X-ReelBolt-Slot-Fallback: 1' http://127.0.0.1/api/v1/Assistant/Threads/idle/Turn | grep -q '^new .*hdr=None \[200\]\$'"

# The previous slot is unreachable (stopped): the new slot's 404 must stay a 404 and an idle turn stays 200 idle,
# never a 502. Uses the second backend by pointing the previous slot at a closed port, then restores it.
DEADP=$((OLDP + 1000))
printf 'UPSTREAM_INFERENCE_API=http://%s:%s\nUPSTREAM_INFERENCE_PREV=http://%s:%s\nUPSTREAM_WEB=http://%s:%s\nUPSTREAM_WEB_PREV=http://%s:%s\n' \
  "$GW" "$NEWP" "$GW" "$DEADP" "$GW" "$NEWP" "$GW" "$DEADP" >"$work/deploy/upstreams.env"
x $N /etc/nginx/reload-config.sh >/dev/null 2>&1; sleep 1
check "prev down: approval 404 stays 404, not 502" sh -c "docker exec $N curl -s -m 10 -o /dev/null -w '%{http_code}' -X POST -d '{}' http://127.0.0.1/api/v1/assistant/approvals/abc/decision | grep -qx 404"
check "prev down: chat re-attach 404 stays 404, not 502" sh -c "docker exec $N curl -s -m 10 -o /dev/null -w '%{http_code}' -X POST -d '{\"attach\":true}' http://127.0.0.1/api/v1/assistant/chat | grep -qx 404"
check "prev down: stale web chunk 404 stays 404, not 502" sh -c "docker exec $N curl -s -m 10 -o /dev/null -w '%{http_code}' http://127.0.0.1/app/_next/static/chunks/old.js | grep -qx 404"
check "prev down: idle turn answers 200 with the idle body, not 502" sh -c "docker exec $N curl -s -m 10 -w ' [%{http_code}]' $T/idle/turn | grep -qx '{\"state\":\"idle\",\"pendingWatches\":0} \[200\]'"
check "prev down: a live turn on the new slot still returns live" sh -c "docker exec $N curl -s -m 5 $T/livenew/turn | grep -qx 'new live hdr=1'"
printf 'UPSTREAM_INFERENCE_API=http://%s:%s\nUPSTREAM_INFERENCE_PREV=http://%s:%s\nUPSTREAM_WEB=http://%s:%s\nUPSTREAM_WEB_PREV=http://%s:%s\n' \
  "$GW" "$NEWP" "$GW" "$OLDP" "$GW" "$NEWP" "$GW" "$OLDP" >"$work/deploy/upstreams.env"
x $N /etc/nginx/reload-config.sh >/dev/null 2>&1; sleep 1
# (A previous slot whose NAME no longer resolves ends the same way, but the test container sits on the default
# bridge, which has no embedded DNS to answer NXDOMAIN, so that case cannot be reproduced here.)

echo "== reload ships a change"
printf 'UPSTREAM_INFERENCE_API=http://%s:%s\nUPSTREAM_INFERENCE_PREV=http://%s:%s\n' "$GW" "$OLDP" "$GW" "$NEWP" >"$work/deploy/upstreams.env"
check "reload-config.sh succeeds" x $N /etc/nginx/reload-config.sh
sleep 1
check "traffic now follows the new upstream file" sh -c "docker exec $N curl -s -m 5 http://127.0.0.1/api/v1/projects | grep -q '^old '"

echo "== release-bound source: .deploy/nginx-src/current wins over the checkout mount"
mkdir -p "$work/deploy/nginx-src"
cp -a "$work/src" "$work/deploy/nginx-src/rel1"
printf '\nlocation = /rel-marker { return 200 "from-release"; }\n' >>"$work/deploy/nginx-src/rel1/locations.conf"
printf '\nlocation = /src-marker { return 200 "from-checkout"; }\n' >>"$work/src/locations.conf"
check "without current the checkout is the source" sh -c "docker exec $N /etc/nginx/reload-config.sh && sleep 1 && docker exec $N curl -s -m 5 http://127.0.0.1/src-marker | grep -q from-checkout"
ln -s rel1 "$work/deploy/nginx-src/current"
check "with current the release copy is the source and the checkout's change is not loaded" sh -c "docker exec $N /etc/nginx/reload-config.sh && sleep 1 && docker exec $N curl -s -m 5 http://127.0.0.1/rel-marker | grep -q from-release && ! docker exec $N curl -s -m 5 http://127.0.0.1/src-marker | grep -q from-checkout"
rm -f "$work/deploy/nginx-src/current"
check "dropping current returns to the checkout" sh -c "docker exec $N /etc/nginx/reload-config.sh && sleep 1 && docker exec $N curl -s -m 5 http://127.0.0.1/src-marker | grep -q from-checkout"
cp -a nginx/locations.conf "$work/src/locations.conf"

echo "== reload refuses a broken change"
printf 'this is not nginx;\n' >>"$work/src/locations.conf"
pid_before="$(x $N cat /var/run/nginx.pid)"
if x $N /etc/nginx/reload-config.sh >"$work/out" 2>&1; then ko "reload-config.sh should fail on a broken config"; else ok "reload-config.sh exits non-zero"; fi
check "nginx -t passes again (files restored)" x $N nginx -t
check "same master still serving the old config" sh -c "[ \"\$(docker exec $N cat /var/run/nginx.pid)\" = '$pid_before' ] && docker exec $N curl -s -m 5 http://127.0.0.1/api/v1/projects | grep -q '^old '"

[ "$fail" -eq 0 ] && echo "render-reload: all passed" || { echo "render-reload: $fail failed"; exit 1; }
