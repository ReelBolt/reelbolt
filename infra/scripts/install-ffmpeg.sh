#!/usr/bin/env bash
# Install a pinned, checksum-verified static ffmpeg/ffprobe without sudo.
#
# GitHub-hosted runners already ship ffmpeg and ffprobe, so on them this script does nothing.
# Where a runner image has neither binary and no passwordless sudo, the jobs that need one
# (media fixtures, pixel probes, the worker's `ffmpeg -version` capability probe) cannot
# `apt-get install` it. For that case, fetch the johnvansickle static build the repository
# already trusts and put it on PATH. A no-op where both binaries are already present.
#
#   bash infra/scripts/install-ffmpeg.sh
#
# Linux x86_64 only: the pinned artifacts are `-linux-x64` gzip files. Any download,
# checksum or extraction failure exits non-zero, so a job never runs against a
# half-installed tool.
set -euo pipefail

FFMPEG_STATIC_RELEASE=${FFMPEG_STATIC_RELEASE:-b6.1.1} # eugeneware/ffmpeg-static; ffmpeg 7.0.2 (johnvansickle static build)
FFMPEG_GZ_SHA256=${FFMPEG_GZ_SHA256:-bfe8a8fc511530457b528c48d77b5737527b504a3797a9bc4866aeca69c2dffa}
FFPROBE_GZ_SHA256=${FFPROBE_GZ_SHA256:-25d9b6ccb05e3d9de9e04e31e2506d8dd7f9f0418981965ac6df12e8d3afd067}

if [ "$(uname -s)" != "Linux" ] || [ "$(uname -m)" != "x86_64" ]; then
  echo "install-ffmpeg: the pinned artifacts are linux-x64 only (this is $(uname -s)/$(uname -m))" >&2
  exit 1
fi

if command -v ffmpeg >/dev/null 2>&1 && command -v ffprobe >/dev/null 2>&1; then
  echo "install-ffmpeg: ffmpeg and ffprobe already on PATH"
else
  dir="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/ffmpeg-static"
  mkdir -p "$dir"
  for tool in ffmpeg ffprobe; do
    curl -fsSL -o "$dir/$tool.gz" \
      "https://github.com/eugeneware/ffmpeg-static/releases/download/${FFMPEG_STATIC_RELEASE}/${tool}-linux-x64.gz"
  done
  printf '%s  %s\n' "$FFMPEG_GZ_SHA256" "$dir/ffmpeg.gz" "$FFPROBE_GZ_SHA256" "$dir/ffprobe.gz" | sha256sum -c -
  gunzip -f "$dir/ffmpeg.gz" "$dir/ffprobe.gz"
  chmod 0755 "$dir/ffmpeg" "$dir/ffprobe"
  # GITHUB_PATH carries the directory into later steps; the export covers a local
  # (non-Actions) run of this script and the checks below.
  if [ -n "${GITHUB_PATH:-}" ]; then
    echo "$dir" >> "$GITHUB_PATH"
  fi
  export PATH="$dir:$PATH"
fi

# sed, not `head -n 1`: head closes the pipe early and `pipefail` would read the
# resulting SIGPIPE as a failed install.
ffmpeg -hide_banner -version | sed -n '1p'
ffprobe -hide_banner -version | sed -n '1p'
# One synthetic frame pair: proves the binary actually decodes, not merely that a
# file of the right name exists.
ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=320x180:rate=30 -t 1 -f null -
echo "install-ffmpeg: ok"
