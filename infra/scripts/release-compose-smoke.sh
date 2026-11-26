#!/usr/bin/env bash
# Smoke check for docker-compose.release.yml (the published-image install path).
#   release-compose-smoke.sh [config]  cheap: resolve the merged config and assert every built-here service
#                                      uses ghcr.io/reelbolt/reelbolt-*:<tag> with no build section left.
#   release-compose-smoke.sh up        manual: pull, start the stack, poll http://localhost/health, stop it.
# `up` needs a real .env and network access; it is never run by CI.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

mode="${1:-config}"
export REELBOLT_IMAGE_TAG="${REELBOLT_IMAGE_TAG:-0.4.0-alpha}"
files=(-f docker-compose.yml -f docker-compose.release.yml)

# Compose service -> published image name (the ci.yml docker-publish matrix).
declare -A image_of=(
  [go-api]=go-api [inference]=inference-api [workflow-engine]=workflow-engine [import-worker]=import-worker
  [mcp]=mcp [web]=web [site]=site [docs]=docs-site [nginx]=nginx [sandbox-runtime]=sandbox-runtime
  [sandbox-executor]=sandbox-executor [runner-gateway]=runner-gateway
)

config_mode() {
  local out
  out="$(mktemp)"; trap 'rm -f "$out"' RETURN
  # Dummy values for the `:?` required variables, only when the caller has none.
  RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-dummy-rabbit}" SANDBOX_API_TOKEN="${SANDBOX_API_TOKEN:-dummy-sandbox-token}" \
  IMPORT_WORKER_TOKEN="${IMPORT_WORKER_TOKEN:-dummy-import-token}" \
    docker compose --profile '*' "${files[@]}" config --format json >"$out"
  python3 - "$out" "$REELBOLT_IMAGE_TAG" "$(declare -p image_of)" <<'PY'
import json, re, sys
cfg = json.load(open(sys.argv[1])); tag = sys.argv[2]
names = dict(re.findall(r"\[([\w-]+)\]=\"?([\w-]+)\"?", sys.argv[3]))
svc = cfg["services"]; bad = []
for s, img in names.items():
    d = svc.get(s)
    if d is None: bad.append(f"{s}: missing from merged config"); continue
    want = f"ghcr.io/reelbolt/reelbolt-{img}:{tag}"
    if d.get("image") != want: bad.append(f"{s}: image {d.get('image')!r}, want {want!r}")
for s, d in svc.items():
    if "build" in d: bad.append(f"{s}: still has a build section")
if svc["sandbox-executor"]["environment"].get("SANDBOX_IMAGE") != f"ghcr.io/reelbolt/reelbolt-sandbox-runtime:{tag}":
    bad.append("sandbox-executor: SANDBOX_IMAGE does not name the pulled runtime image")
if bad: print("\n".join(bad), file=sys.stderr); sys.exit(1)
print(f"release compose ok: {len(names)} services on tag {tag}, no build sections")
PY
}

case "$mode" in
  config) config_mode ;;
  up)
    docker compose "${files[@]}" pull
    trap 'docker compose "${files[@]}" down' EXIT
    docker compose "${files[@]}" up -d
    for _ in $(seq 1 60); do
      curl -fsS http://localhost/health >/dev/null 2>&1 && { echo "health ok"; exit 0; }
      sleep 5
    done
    echo "http://localhost/health did not answer in 5 minutes" >&2; exit 1 ;;
  *) echo "usage: $0 [config|up]" >&2; exit 2 ;;
esac
