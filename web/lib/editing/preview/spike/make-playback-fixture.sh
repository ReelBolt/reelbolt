#!/usr/bin/env bash
#
# T-201 — deterministic PLAYBACK fixture generator.  REMOVABLE (see README.md).
#
# Produces the inputs the compositor budget is measured against:
#
#   fixtures/playback/proxy-1080p-36s.mp4   the 4-video-layer source
#   fixtures/playback/stem-1.m4a .. stem-6.m4a   the 6 audio tracks (WebAudio)
#
# WHY THIS SHAPE (read before "improving" it):
#
#  * 36 s of source, one continuous run of >=30 s of playout, so the budget is
#    measured with NO loop discontinuity per layer.  A loop would inject a seek
#    into the middle of the decode-latency sample and flatter or wreck it
#    depending on where it landed.
#  * ONE proxy file, read by all four layers.  At a fixed resolution / profile /
#    level / GOP, a decoder's cost is a function of the bitstream parameters, not
#    of which bytes the bitstream happens to encode — and each layer owns an
#    INDEPENDENT VideoDecoder instance, so "4 concurrent 1080p30 H.264 decoders"
#    is exactly what is measured.  The four layers are composited with distinct
#    tint and position, so a layer that silently failed to decode would change
#    the composited image, and the harness asserts the composite is a real
#    4-layer blend.  Keeping one 6 MB fixture instead of four 6 MB fixtures is
#    deliberate: this whole directory is deleted by a later wave.
#  * The proxy carries NO audio track.  Audio is six separate stems, so the six
#    WebAudio tracks are six real decodes and six real mix inputs rather than one
#    muxed pair repeated.
#  * Stems are 10 s and loop via AudioBufferSourceNode.loop — buffer looping is
#    gapless and costs no decode, so the audio side runs the full >=30 s without
#    a seam.  Six distinct fundamentals plus tremolo make the mix non-trivial.
#  * AAC-LC at 96 kbps stereo, matching how the product's own muxed audio arrives.
#    If a browser build without proprietary codecs is ever used, decodeAudioData
#    fails loudly in the harness rather than silently measuring nothing.
#
# Determinism: synthetic lavfi sources only (no camera, no clock, no randomness),
# the encoder pinned to a single thread, and -fflags/-flags +bitexact plus
# -map_metadata -1 stripping the encoder version string and the creation
# timestamp.  Same ffmpeg build + same command => same bytes.  The fixtures are
# COMMITTED; this script exists only to regenerate them.
#
# Usage:  web/lib/editing/preview/spike/make-playback-fixture.sh
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${HERE}/fixtures/playback"
PROXY="${OUT_DIR}/proxy-1080p-36s.mp4"

DURATION=36
FPS=30

mkdir -p "${OUT_DIR}"

# ── The 4-layer video source ─────────────────────────────────────────────────
# -g 30 -keyint_min 30 -sc_threshold 0  -> one IDR per second, strictly regular,
#                                          so a seek is never a full-GOP re-decode
#                                          surprise inside the measured window.
# -bf 2                                  -> real B-frames: decode order != PTS
#                                          order, which is what a real proxy has.
# -threads 1                             -> deterministic bytes.
# -crf 34 -preset veryfast               -> ~205 kB/s at 1080p30, i.e. a
#                                          plausible proxy bitrate rather than a
#                                          synthetic worst case.
echo "### encoding ${PROXY} (${DURATION}s, 1920x1080@${FPS}, H.264 High, no audio)"
ffmpeg -hide_banner -nostdin -y \
  -f lavfi -i "testsrc2=size=1920x1080:rate=${FPS}:duration=${DURATION}" \
  -map 0:v:0 -an \
  -c:v libx264 -preset veryfast -threads 1 \
  -profile:v high -level 4.0 -pix_fmt yuv420p \
  -g 30 -keyint_min 30 -sc_threshold 0 -bf 2 \
  -crf 34 -maxrate 4000k -bufsize 8000k \
  -movflags +faststart \
  -fflags +bitexact -flags +bitexact -map_metadata -1 \
  "${PROXY}"

# ── The six audio stems ──────────────────────────────────────────────────────
# Distinct fundamentals, each amplitude-modulated at a different rate so the
# six-way sum is genuinely time-varying and a dropped or silent stem is
# detectable in the harness's per-stem RMS meter.
STEM_FREQS=(220 277 330 440 554 660)
STEM_TREM=(2.0 2.5 3.0 3.5 4.0 4.5)
for i in 0 1 2 3 4 5; do
  n=$((i + 1))
  f="${STEM_FREQS[$i]}"
  t="${STEM_TREM[$i]}"
  out="${OUT_DIR}/stem-${n}.m4a"
  echo "### encoding ${out} (10s, ${f} Hz, tremolo ${t} Hz, stereo AAC-LC 96k)"
  ffmpeg -hide_banner -nostdin -y \
    -f lavfi -i "sine=frequency=${f}:sample_rate=48000:duration=10,tremolo=f=${t}:d=0.6" \
    -map 0:a:0 -ac 2 -ar 48000 \
    -c:a aac -b:a 96k \
    -movflags +faststart \
    -fflags +bitexact -flags +bitexact -map_metadata -1 \
    "${out}"
done

# ── Evidence for the report (never hand-wave the input) ──────────────────────
echo
echo "### ffprobe -show_streams -show_format (proxy)"
ffprobe -hide_banner -v error -show_streams -show_format "${PROXY}"

echo
echo "### size / sha256 (all playback fixtures)"
stat -c '%s bytes  %n' "${OUT_DIR}"/*.mp4 "${OUT_DIR}"/*.m4a
sha256sum "${OUT_DIR}"/*.mp4 "${OUT_DIR}"/*.m4a

echo
echo "### GOP regularity (ffprobe -show_frames, JSON)"
# JSON, not CSV: x264 writes an unregistered-SEI side_data blob whose own commas
# corrupt the CSV columns (same trap T-200's make-fixture.sh records).
ffprobe -hide_banner -v error -select_streams v:0 -show_frames \
  -show_entries frame=key_frame,pts_time,pict_type \
  -of json "${PROXY}" | node -e '
    let raw = "";
    process.stdin.on("data", (c) => (raw += c));
    process.stdin.on("end", () => {
      const frames = JSON.parse(raw).frames ?? [];
      const keys = frames.filter((f) => f.key_frame === 1);
      const times = keys.map((f) => Number(f.pts_time));
      const deltas = times.slice(1).map((t, i) => +(t - times[i]).toFixed(3));
      console.log(`  video frames=${frames.length}  keyframes=${keys.length}`);
      console.log(`  first keyframe t=${times[0]}  last keyframe t=${times[times.length - 1]}`);
      console.log(
        `  keyframe interval (s) = ${JSON.stringify([...new Set(deltas)])}` +
          `  regular GOP: ${new Set(deltas).size === 1 ? `YES, one IDR every ${deltas[0]}s` : "NO"}`
      );
      const p = frames.map((f) => f.pict_type);
      const counts = p.reduce((a, x) => ((a[x] = (a[x] ?? 0) + 1), a), {});
      console.log(`  pict_type histogram = ${JSON.stringify(counts)}`);
    });'

echo
echo "### audio stem stream summary"
for i in 1 2 3 4 5 6; do
  ffprobe -hide_banner -v error -select_streams a:0 \
    -show_entries stream=codec_name,sample_rate,channels,bit_rate \
    -show_entries format=duration \
    -of default=noprint_wrappers=1 "${OUT_DIR}/stem-${i}.m4a" | tr '\n' ' '
  echo " <- stem-${i}.m4a"
done
