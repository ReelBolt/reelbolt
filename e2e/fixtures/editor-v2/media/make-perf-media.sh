#!/usr/bin/env bash
# 1080p media for the playback perf spec (tests/editor-v2/14-playback-perf). Never committed.
#
#   e2e/fixtures/editor-v2/media/make-perf-media.sh [outDir]      default /tmp/editor-remediation/media-perf
#
#   perf-a.mp4    1920x1080 30 fps 24 s, testsrc2, 440 Hz tone, H.264 CRF 20, GOP 30
#   perf-b.mp4    1920x1080 30 fps 24 s, mandelbrot (high-entropy picture), 660 Hz tone, H.264 CRF 20, GOP 30
#   perf-{a,b}-proxylike.mp4  720p GOP-15 re-encodes of the above, shaped like the derivative pipeline's proxy
#   perf-{a,b}-60.mp4  1920x1080 60 fps 24 s, GOP 60 (the 60 fps timeline case)
#   perf-music.wav  24 s 330 Hz, 48 kHz stereo
#   perf-{a,b}-4k.mp4, perf-{a,b}-4k-60.mp4  3840x2160 at 30 / 60 fps, 16 s (only when PERF_4K=1)
set -euo pipefail
OUT=${1:-/tmp/editor-remediation/media-perf}
mkdir -p "$OUT"
FF=(ffmpeg -nostdin -hide_banner -loglevel error -y)
ENC=(-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -g 30 -bf 0 -c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart)

[ -s "$OUT/perf-a.mp4" ] || "${FF[@]}" -f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=24" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=24" "${ENC[@]}" -t 24 "$OUT/perf-a.mp4"
[ -s "$OUT/perf-b.mp4" ] || "${FF[@]}" -f lavfi -i "mandelbrot=size=1920x1080:rate=30" \
  -f lavfi -i "sine=frequency=660:sample_rate=48000:duration=24" "${ENC[@]}" -t 24 "$OUT/perf-b.mp4"
[ -s "$OUT/perf-music.wav" ] || "${FF[@]}" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=24" -ac 2 -c:a pcm_s16le "$OUT/perf-music.wav"
for n in a b; do
  [ -s "$OUT/perf-$n-proxylike.mp4" ] || "${FF[@]}" -i "$OUT/perf-$n.mp4" -vf "scale=-2:720:flags=bicubic" \
    -c:v libx264 -preset veryfast -crf 23 -profile:v main -pix_fmt yuv420p -g 15 -keyint_min 15 -sc_threshold 0 \
    -c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart "$OUT/perf-$n-proxylike.mp4"
done
# 60 fps variants (GOP 60) for the 60 fps timeline case.
ENC60=(-c:v libx264 -preset veryfast -crf 24 -maxrate 14M -bufsize 28M -pix_fmt yuv420p -g 60 -bf 0 -c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart)
[ -s "$OUT/perf-a-60.mp4" ] || "${FF[@]}" -f lavfi -i "testsrc2=size=1920x1080:rate=60:duration=24" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=24" "${ENC60[@]}" -t 24 "$OUT/perf-a-60.mp4"
[ -s "$OUT/perf-b-60.mp4" ] || "${FF[@]}" -f lavfi -i "mandelbrot=size=1920x1080:rate=60" \
  -f lavfi -i "sine=frequency=660:sample_rate=48000:duration=24" "${ENC60[@]}" -t 24 "$OUT/perf-b-60.mp4"
if [ "${PERF_4K:-0}" = 1 ]; then
  # 3840x2160 sets, 16 s each (the spec plays 14 s; the picture-in-picture starts 2 s into its source). The b picture is
  # a 1080p mandelbrot scaled up: still a high-entropy 4K stream, but cheap to generate.
  ENC4K30=(-c:v libx264 -preset veryfast -crf 24 -maxrate 40M -bufsize 80M -pix_fmt yuv420p -g 30 -bf 0 -c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart)
  ENC4K60=(-c:v libx264 -preset veryfast -crf 30 -maxrate 30M -bufsize 60M -pix_fmt yuv420p -g 60 -bf 0 -c:a aac -b:a 128k -ar 48000 -ac 2 -movflags +faststart)
  for r in 30 60; do
    sfx=-4k; [ "$r" = 60 ] && sfx=-4k-60
    enc=("${ENC4K30[@]}"); [ "$r" = 60 ] && enc=("${ENC4K60[@]}")
    [ -s "$OUT/perf-a$sfx.mp4" ] || "${FF[@]}" -f lavfi -i "testsrc2=size=3840x2160:rate=$r:duration=16" \
      -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=16" "${enc[@]}" -t 16 "$OUT/perf-a$sfx.mp4"
    [ -s "$OUT/perf-b$sfx.mp4" ] || "${FF[@]}" -f lavfi -i "mandelbrot=size=1920x1080:rate=$r" \
      -f lavfi -i "sine=frequency=660:sample_rate=48000:duration=16" -vf "scale=3840:2160:flags=bicubic" "${enc[@]}" -t 16 "$OUT/perf-b$sfx.mp4"
  done
fi
echo "$OUT"
