#!/usr/bin/env bash
#
# T-200 — deterministic proxy fixture generator.  REMOVABLE (see README.md).
#
# Produces the ONE input both demuxers are measured against:
#   web/lib/editing/preview/spike/fixtures/proxy-1080p-12s.mp4
#
# 1080p H.264 (High, yuv420p) + AAC-LC, 12 s @ 30 fps, CONSTANT GOP of 30
# frames (one IDR per second), 2 B-frames, faststart.
#
# Determinism: the video source is a synthetic lavfi pattern (no camera, no
# clock, no randomness), the encoder is pinned to a single thread, and
# `-fflags +bitexact -flags +bitexact -map_metadata -1` strip the encoder
# version string, the creation timestamp and all container metadata.  Running
# this twice on the same ffmpeg build yields byte-identical output; running it
# on a different libx264 build may not, which is why the fixture is COMMITTED
# and this script exists only to regenerate it.
#
# Usage:  web/lib/editing/preview/spike/make-fixture.sh
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${HERE}/fixtures"
OUT="${OUT_DIR}/proxy-1080p-12s.mp4"

mkdir -p "${OUT_DIR}"

# ── The encoding itself ──────────────────────────────────────────────────────
# -g 30 -keyint_min 30 -sc_threshold 0  -> an IDR every 30 frames, no scene-cut
#                                          IDRs, i.e. a strictly regular GOP.
# -bf 2                                  -> real B-frames, so the seek planner
#                                          has to walk the sample table rather
#                                          than assume decode order == PTS order.
# -fflags/-flags +bitexact               -> reproducible bytes.
ffmpeg -hide_banner -nostdin -y \
  -f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=12" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=12" \
  -map 0:v:0 -map 1:a:0 \
  -c:v libx264 -preset veryfast -threads 1 \
  -profile:v high -level 4.0 -pix_fmt yuv420p \
  -g 30 -keyint_min 30 -sc_threshold 0 -bf 2 \
  -crf 30 -maxrate 6000k -bufsize 12000k \
  -c:a aac -b:a 128k -ac 2 -ar 48000 \
  -movflags +faststart \
  -fflags +bitexact -flags +bitexact -map_metadata -1 \
  "${OUT}"

# ── Evidence for the report (never hand-wave the input) ──────────────────────
echo "### ffprobe -show_streams -show_format"
ffprobe -hide_banner -v error -show_streams -show_format "${OUT}"

echo
echo "### size / sha256"
stat -c '%s bytes  %n' "${OUT}"
sha256sum "${OUT}"

echo
echo "### keyframe timestamps + GOP regularity (ffprobe -show_frames, JSON)"
# `-of csv=p=0` is unusable here: x264 writes an unregistered-SEI side_data blob
# whose own commas corrupt the CSV columns, so the pict_type field shifts. The
# JSON output is exact.  (ffprobe 8 names the field `pts_time`; `pkt_pts_time`
# was removed, and asking for it silently yields nothing.)
ffprobe -hide_banner -v error -select_streams v:0 -show_frames \
  -show_entries frame=key_frame,pts_time,pict_type \
  -of json "${OUT}" | node -e '
    let raw = "";
    process.stdin.on("data", (c) => (raw += c));
    process.stdin.on("end", () => {
      const frames = JSON.parse(raw).frames ?? [];
      const keys = frames.filter((f) => f.key_frame === 1);
      const times = keys.map((f) => Number(f.pts_time));
      keys.forEach((f, i) =>
        console.log(
          `  keyframe ${String(i + 1).padStart(2)}  t=${Number(f.pts_time).toFixed(3)}s  pict_type=${f.pict_type}`
        )
      );
      const deltas = times.slice(1).map((t, i) => +(t - times[i]).toFixed(3));
      console.log(`  TOTAL video frames=${frames.length}  keyframes=${keys.length}`);
      console.log(`  keyframe interval (s) = ${JSON.stringify(deltas)}`);
      console.log(
        `  regular GOP: ${new Set(deltas).size === 1 ? `YES, one IDR every ${deltas[0]}s` : "NO"}`
      );
    });'

echo
echo "### packets per stream"
for s in v a; do
  n=$(ffprobe -hide_banner -v error -select_streams "${s}:0" -show_packets -show_entries packet=pts_time -of csv=p=0 "${OUT}" | wc -l)
  echo "  ${s}:0 packets=${n}"
done
