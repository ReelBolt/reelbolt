#!/usr/bin/env bash
# The unlisted-file part of sandbox/Dockerfile's compositor guard, extracted from the Dockerfile and run
# against a fake node_modules: pinned files only pass; a versioned libavcodec.so.N, an extra ffmpeg-like
# file, or an unknown compositor package fails.
#
#   infra/scripts/tests/sandbox-compositor-guard.test.sh
set -euo pipefail
tests_dir="$(cd "$(dirname "$0")" && pwd)"
dockerfile="$tests_dir/../../../sandbox/Dockerfile"
pins="$tests_dir/../../image-pins.env"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
passed=0; failed=0
ok() { printf '  ok   %s\n' "$1"; passed=$((passed + 1)); }
ko() { printf '  FAIL %s\n' "$1"; failed=$((failed + 1)); }

# the first RUN loop: from "for d in node_modules/@remotion/compositor-*; do" to the "done; \" that closes it
awk '/^\tfor d in node_modules\/@remotion\/compositor-\*; do/ {on=1} on {print} on && /^\tdone; \\$/ {exit}' "$dockerfile" \
  | sed 's/\\$//' > "$work/guard.sh"
[ -s "$work/guard.sh" ] && ok "guard extracted from sandbox/Dockerfile" || { ko "guard not found in sandbox/Dockerfile"; exit 1; }

# shellcheck disable=SC1090
. "$pins"
export COMPOSITOR_SHA256_LINUX_X64_GNU COMPOSITOR_SHA256_LINUX_ARM64_GNU
mk() { # <case> <package dir name> [extra file...]
  local c="$1" pkg="$2"; shift 2
  local d="$work/$c/node_modules/@remotion/$pkg"; mkdir -p "$d"
  local e; for e in $COMPOSITOR_SHA256_LINUX_X64_GNU; do : > "$d/${e%%=*}"; done
  local f; for f in "$@"; do : > "$d/$f"; done
}
run() { ( cd "$work/$1" && sh -c "set -eu; $(cat "$work/guard.sh")" ) >"$work/$1.out" 2>&1; }

mk good compositor-linux-x64-gnu index.js package.json
run good && ok "pinned files only: passes" || ko "pinned files only failed: $(cat "$work/good.out")"
mk soname compositor-linux-x64-gnu libavcodec.so.60
run soname && ko "versioned libavcodec.so.60 accepted" || { grep -q UNLISTED "$work/soname.out" && ok "versioned libavcodec.so.N next to pinned files fails" || ko "soname: $(cat "$work/soname.out")"; }
mk extra compositor-linux-x64-gnu ffmpeg-static
run extra && ko "extra ffmpeg-like file accepted" || ok "an extra ffmpeg-like file fails"
mk fdk compositor-linux-x64-gnu libfdk-aac.so.2
run fdk && ko "libfdk file accepted" || ok "a libfdk file fails"
mk other compositor-linux-x64-musl
run other && ko "unknown compositor package with ffmpeg accepted" || ok "an unlisted compositor package with ffmpeg files fails"

printf '\n%d passed, %d failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
