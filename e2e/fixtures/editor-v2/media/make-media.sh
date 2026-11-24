#!/usr/bin/env bash
# Deterministic seed media for the editor-v2 suite (same specs as the audit's setup.md §4).
#
#   e2e/fixtures/editor-v2/media/make-media.sh [outDir]      default /tmp/editor-remediation/media
#
# Writes (never committed; regenerated when missing):
#   clipA.mp4     1280x720 30 fps 12 s, testsrc2 picture, 440 Hz tone, silent 3.0-4.5 s and 8.0-9.5 s
#   clipB.mp4     1280x720 30 fps  8 s, mandelbrot picture, 660 Hz tone
#   vertical.mp4   720x1280 30 fps 6 s, SMPTE HD bars, 880 Hz tone
#   still.png     1920x1080 rgb test pattern
#   tone.wav      5 s 550 Hz, 48 kHz stereo
# The pictures differ on purpose: the viewer pixel guards tell the sources apart by comparing the program
# monitor against reference frames of these files. Bit-exact output is not required (ffmpeg builds differ);
# the generators and parameters are fixed.
set -euo pipefail
OUT=${1:-/tmp/editor-remediation/media}
mkdir -p "$OUT"
FF=(ffmpeg -nostdin -hide_banner -loglevel error -y)
X264=(-c:v libx264 -preset veryfast -pix_fmt yuv420p -g 30 -bf 0 -c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart)

[ -s "$OUT/clipA.mp4" ] || "${FF[@]}" \
  -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=12" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=12" \
  -filter_complex "[1:a]volume=enable='between(t,3.0,4.5)+between(t,8.0,9.5)':volume=0[a]" \
  -map 0:v -map "[a]" "${X264[@]}" -t 12 "$OUT/clipA.mp4"

[ -s "$OUT/clipB.mp4" ] || "${FF[@]}" \
  -f lavfi -i "mandelbrot=size=1280x720:rate=30" \
  -f lavfi -i "sine=frequency=660:sample_rate=48000:duration=8" \
  -map 0:v -map 1:a "${X264[@]}" -t 8 "$OUT/clipB.mp4"

[ -s "$OUT/vertical.mp4" ] || "${FF[@]}" \
  -f lavfi -i "smptehdbars=size=720x1280:rate=30:duration=6" \
  -f lavfi -i "sine=frequency=880:sample_rate=48000:duration=6" \
  -map 0:v -map 1:a "${X264[@]}" -t 6 "$OUT/vertical.mp4"

[ -s "$OUT/still.png" ] || "${FF[@]}" \
  -f lavfi -i "rgbtestsrc=size=1920x1080:rate=1" -frames:v 1 "$OUT/still.png"

[ -s "$OUT/tone.wav" ] || "${FF[@]}" \
  -f lavfi -i "sine=frequency=550:sample_rate=48000:duration=5" -ac 2 -c:a pcm_s16le "$OUT/tone.wav"

echo "$OUT"
