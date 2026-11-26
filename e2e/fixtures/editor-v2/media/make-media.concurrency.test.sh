#!/usr/bin/env bash
# Proves make-media.sh is safe under concurrent callers and heals corrupt files.
#   e2e/fixtures/editor-v2/media/make-media.concurrency.test.sh
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
check() {
  for f in clipA.mp4 clipB.mp4 vertical.mp4 tone.wav still.png; do
    ffmpeg -nostdin -v error -xerror -i "$OUT/$f" -f null - || { echo "FAIL: $f does not decode ($1)" >&2; exit 1; }
  done
}
# 1. four simultaneous runs on an empty dir
pids=()
for _ in 1 2 3 4; do bash "$HERE/make-media.sh" "$OUT" >/dev/null & pids+=($!); done
for p in "${pids[@]}"; do wait "$p" || { echo "FAIL: a concurrent run exited non-zero" >&2; exit 1; }; done
check concurrent
# 2. a corrupt existing file is regenerated, even with concurrent callers
head -c 4096 "$OUT/clipA.mp4" > "$OUT/clipA.tmp" && mv "$OUT/clipA.tmp" "$OUT/clipA.mp4"
pids=()
for _ in 1 2; do bash "$HERE/make-media.sh" "$OUT" >/dev/null & pids+=($!); done
for p in "${pids[@]}"; do wait "$p" || { echo "FAIL: heal run failed" >&2; exit 1; }; done
check healed
# 3. no temp leftovers
for f in "$OUT"/.tmp.*; do
  [ -e "$f" ] && { echo "FAIL: temp dir left behind" >&2; exit 1; }
done
echo "ok"
