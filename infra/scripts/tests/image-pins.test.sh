#!/usr/bin/env bash
# The pins in infra/image-pins.env and the ARG defaults in the Dockerfiles must be the same strings,
# the Dockerfiles must copy the licence files, and no code of ours may name the non-free fdk encoder
# (our audio paths use ffmpeg's native `aac`).
#
#   infra/scripts/tests/image-pins.test.sh
set -euo pipefail
tests_dir="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$tests_dir/../../.." && pwd)"
passed=0; failed=0
ok() { printf '  ok   %s\n' "$1"; passed=$((passed + 1)); }
ko() { printf '  FAIL %s\n' "$1"; failed=$((failed + 1)); }
# shellcheck disable=SC1091
. "$repo/infra/image-pins.env"

arg_default() { sed -n "s/^ARG $2=//p" "$repo/$1" | head -n 1; }
check_arg() { # <Dockerfile> <ARG> <expected>
  local v; v="$(arg_default "$1" "$2")"
  if [ "$v" = "$3" ]; then ok "$1 $2 = $3"; else ko "$1 $2 is '$v', infra/image-pins.env says '$3'"; fi
}
check_arg sandbox/Dockerfile FFMPEG_DEBIAN_VERSION "$FFMPEG_DEBIAN_VERSION"
check_arg sandbox/Dockerfile FFMPEG_ALPINE_VERSION "$FFMPEG_ALPINE_VERSION"
check_arg inference/src/ReelBolt.WorkflowEngine/Dockerfile FFMPEG_ALPINE_VERSION "$FFMPEG_ALPINE_VERSION"
check_arg inference/src/ReelBolt.Inference.Api/Dockerfile FFMPEG_ALPINE_VERSION "$FFMPEG_ALPINE_VERSION"
check_arg inference/src/ReelBolt.ImportWorker/Dockerfile POPPLER_ALPINE_VERSION "$POPPLER_ALPINE_VERSION"
check_arg sandbox/Dockerfile COMPOSITOR_SHA256_LINUX_X64_GNU '"'"$COMPOSITOR_SHA256_LINUX_X64_GNU"'"'
check_arg sandbox/Dockerfile COMPOSITOR_SHA256_LINUX_ARM64_GNU '"'"$COMPOSITOR_SHA256_LINUX_ARM64_GNU"'"'
rv="$(sed -n 's/.*"remotion": "\([^"]*\)".*/\1/p' "$repo/sandbox/template/package.json" | head -n 1)"
[ "$rv" = "$REMOTION_VERSION" ] && ok "REMOTION_VERSION equals sandbox/template/package.json" || ko "REMOTION_VERSION $REMOTION_VERSION != template $rv"

# every Dockerfile of a published image (the CI docker matrix) copies the licence files
for f in api/Dockerfile mcp/Dockerfile web/Dockerfile site/Dockerfile docs-site/Dockerfile nginx/Dockerfile sandbox/Dockerfile \
         inference/src/ReelBolt.Inference.Api/Dockerfile inference/src/ReelBolt.WorkflowEngine/Dockerfile inference/src/ReelBolt.ImportWorker/Dockerfile; do
  n="$(grep -c '^COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/$' "$repo/$f" || true)"
  want=1; [ "$f" = sandbox/Dockerfile ] && want=4   # sandbox-runtime (control-plane inherits), runner-gateway, pool-waker, worker
  [ "$n" = "$want" ] && ok "$f copies /licenses ($n)" || ko "$f has $n licence COPY lines, expected $want"
done

# the pins gate the matrix images too: every matrix image's Dockerfile is in the list above
for img in $(sed -n 's/^ *file: \.\/\(.*Dockerfile\)$/\1/p' "$repo/.github/workflows/ci.yml" | sort -u); do
  grep -q "COPY --from=legal" "$repo/$img" && ok "matrix image $img carries /licenses" || ko "matrix image $img has no licence COPY"
done

# no code of ours selects the non-free fdk encoder (docs and the guards may name it)
hits="$(grep -rIl --include='*.cs' --include='*.go' --include='*.ts' --include='*.tsx' --include='*.mjs' --include='*.js' 'libfdk_aac' "$repo/inference/src" "$repo/api" "$repo/sandbox/cmd" "$repo/sandbox/pkg" "$repo/sandbox/template/reelbolt" "$repo/runner/internal" "$repo/web/src" 2>/dev/null | grep -v '_test\.\|/tests\?/' || true)"
[ -z "$hits" ] && ok "no source file of ours names libfdk_aac" || ko "libfdk_aac named in: $hits"

printf '\n%d passed, %d failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
