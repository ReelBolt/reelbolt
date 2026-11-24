#!/usr/bin/env bash
# Compare the HLG->SDR tone-map BEFORE (mobius knee 0.5) and AFTER (mobius knee 0.2, the current
# HdrToneMap.Filter("hlg")) on one of your own clips. ffmpeg only: it reads the local file, writes
# local PNGs, and never uploads, downloads or calls the network.
#
# Usage:
#   inference/tools/hdr-tonemap-compare.sh [--out DIR] [--time SECONDS] [--ffmpeg PATH] CLIP [CLIP...]
#
#   --out DIR        output directory (created if needed). Default: a fresh mktemp directory.
#   --time SECONDS   frame to grab from each clip (default: 1.0). Clamped to the clip by ffmpeg.
#   --ffmpeg PATH    ffmpeg binary (default: $FFMPEG, else `ffmpeg` on PATH). ffprobe is expected
#                    next to it; set FFMPEG to override both when they live in different places.
#
# For each clip it writes, into DIR/<clip-name>/:
#   before.png   the old chain (HLG: mobius param=0.5; PQ/SDR: unchanged)
#   after.png    the current chain (HLG: mobius param=0.2; PQ/SDR: unchanged)
#   side-by-side.png  before on the left, after on the right
# Pixels come from a single decoded frame, so lighting/motion cannot hide the tone-map difference.
#
# Safety: every input must be an existing regular file; URLs, `concat:` and other option-like
# arguments are refused. A path is canonicalized to an absolute path, so it can never be read as an
# ffmpeg flag or a protocol prefix, and ffmpeg/ffprobe are limited to the `file` protocol so a local
# playlist cannot fetch remote segments. Nothing is written outside DIR.

set -euo pipefail

usage() {
    sed -n '2,23p' "$0" | sed 's/^# \{0,1\}//'
}

OUT=""
SEEK="1.0"
FFMPEG_BIN="${FFMPEG:-ffmpeg}"
FFPROBE_BIN="${FFPROBE:-}"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --out)    OUT="${2:?--out needs a directory}"; shift 2 ;;
        --time)   SEEK="${2:?--time needs seconds}"; shift 2 ;;
        --ffmpeg) FFMPEG_BIN="${2:?--ffmpeg needs a path}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        --) shift; break ;;
        -*) echo "error: unknown option $1 (use -- before a path that starts with '-')" >&2; exit 2 ;;
        *) break ;;
    esac
done

if [ "$#" -eq 0 ]; then
    echo "error: give one or more clip paths" >&2
    usage >&2
    exit 2
fi

if ! command -v "$FFMPEG_BIN" >/dev/null 2>&1; then
    echo "error: ffmpeg not found ($FFMPEG_BIN)" >&2
    exit 2
fi
if [ -z "$FFPROBE_BIN" ]; then
    FFPROBE_BIN="$(dirname "$(command -v "$FFMPEG_BIN")")/ffprobe"
    [ -x "$FFPROBE_BIN" ] || FFPROBE_BIN="ffprobe"
fi

case "$SEEK" in
    ''|*[!0-9.]*) echo "error: --time must be a number of seconds" >&2; exit 2 ;;
esac

if [ -z "$OUT" ]; then
    OUT="$(mktemp -d "${TMPDIR:-/tmp}/hdr-tonemap-compare.XXXXXX")"
    echo "output: $OUT"
else
    mkdir -p "$OUT"
fi
# Physical path, so the containment check below compares like with like even when OUT is a symlink.
OUT="$(cd "$OUT" && pwd -P)"

# The two delivery chains under comparison. HLG_BEFORE is the chain shipped before hdr-tonemap-v3;
# HLG_AFTER is the current HdrToneMap.Filter("hlg"). They differ only in the mobius knee.
HLG_BEFORE="zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:param=0.5:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"
HLG_AFTER="zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:param=0.2:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"
SDR="format=yuv420p"

for CLIP in "$@"; do
    # A URL, a pipe or an option is not a local file; refuse it rather than let ffmpeg open it.
    case "$CLIP" in
        -*|*://*|concat:*|subfile:*|cache:*|data:*)
            echo "error: not a local file path: $CLIP" >&2
            exit 2 ;;
    esac
    if [ ! -f "$CLIP" ]; then
        echo "error: no such regular file: $CLIP" >&2
        exit 2
    fi
    CLIP="$(cd "$(dirname "$CLIP")" && pwd)/$(basename "$CLIP")"

    TRANSFER="$("$FFPROBE_BIN" -v error -select_streams v:0 -protocol_whitelist file \
        -show_entries stream=color_transfer -of default=nw=1:nk=1 "$CLIP" 2>/dev/null || true)"
    case "$TRANSFER" in
        arib-std-b67) BEFORE="$HLG_BEFORE"; AFTER="$HLG_AFTER"; KIND="HLG" ;;
        smpte2084)    BEFORE="$HLG_BEFORE"; AFTER="$HLG_BEFORE"; KIND="PQ (unchanged: mobius param=0.5)" ;;
        *)            BEFORE="$SDR";       AFTER="$SDR";       KIND="SDR/unknown (not tone-mapped)" ;;
    esac

    NAME="$(basename "$CLIP")"
    NAME="$(printf '%s' "$NAME" | sed 's/\.[^.]*$//; s/[^A-Za-z0-9._-]/-/g')"
    # "." and ".." are directory references, not child names: a clip called "...x" strips to "..".
    # Never let the sanitized name address anything but a new child of OUT.
    case "$NAME" in
        ''|.|..) NAME="clip" ;;
    esac
    DIR="$OUT/$NAME"
    mkdir -p "$DIR"
    # Defence in depth: whatever the name resolved to, nothing may be written outside OUT.
    RESOLVED_DIR="$(cd "$DIR" && pwd -P)"
    case "$RESOLVED_DIR" in
        "$OUT"/*) ;;
        *) echo "error: refusing to write outside $OUT: $RESOLVED_DIR" >&2; exit 2 ;;
    esac

    echo "== $CLIP"
    echo "   transfer=${TRANSFER:-none} -> $KIND"

    # `-ss` before `-i` seeks cheaply; one frame, PNG (lossless), full source resolution. The input
    # is restricted to the `file` protocol so a local playlist cannot make ffmpeg fetch remote URIs.
    "$FFMPEG_BIN" -hide_banner -loglevel error -nostdin -y -protocol_whitelist file -ss "$SEEK" -i "$CLIP" \
        -map 0:v:0 -frames:v 1 -vf "$BEFORE" "$DIR/before.png"
    "$FFMPEG_BIN" -hide_banner -loglevel error -nostdin -y -protocol_whitelist file -ss "$SEEK" -i "$CLIP" \
        -map 0:v:0 -frames:v 1 -vf "$AFTER" "$DIR/after.png"

    "$FFMPEG_BIN" -hide_banner -loglevel error -nostdin -y \
        -protocol_whitelist file -i "$DIR/before.png" -protocol_whitelist file -i "$DIR/after.png" \
        -filter_complex "[0:v][1:v]hstack=inputs=2" -frames:v 1 "$DIR/side-by-side.png"

    echo "   wrote $DIR/before.png, after.png, side-by-side.png"
done

echo "done: $OUT"
