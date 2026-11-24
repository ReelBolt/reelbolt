#!/usr/bin/env bash
# Synthetic fixtures for VideoCompileMediaOverlayRealFfmpegTests (ticket T-012).
# Deterministic and offline: ffmpeg only, no network, no customer media, no fonts.
# Re-running this script rewrites the two committed files.
set -euo pipefail

cd "$(dirname "$0")"

FFMPEG="${FFMPEG:-ffmpeg}"

# card.png: 800x1000. Dark slate base with a 400x400 GREEN centre block and a 200x200
# AMBER top-left corner block. The centre pixel (400,500) is green and (40,40) is
# amber, so the real-ffmpeg test can tell the card from the film and from its own
# black letterbox bars.
"$FFMPEG" -nostdin -hide_banner -y -loglevel error \
  -f lavfi -i "color=c=0x333A44:s=800x1000:r=1" \
  -f lavfi -i "color=c=0x00FF00:s=400x400:r=1" \
  -f lavfi -i "color=c=0xFFD200:s=200x200:r=1" \
  -filter_complex \
    "[0:v][1:v]overlay=200:300[base];[base][2:v]overlay=0:0,format=rgb24[out]" \
  -map "[out]" -frames:v 1 -update 1 card.png

# inset.mp4: 2 s, 320x240, 30 fps. The whole frame is flat RED (0xD02020) for the
# first second and flat BLUE (0x2050D0) for the second, so the 2 s cycle repeats when
# looped. An 4 kHz tone rides it: the Mixed-audio assertion can separate it from the
# program's 440 Hz tone with a high-pass.
"$FFMPEG" -nostdin -hide_banner -y -loglevel error \
  -f lavfi -i "color=c=0xD02020:s=320x240:r=30:d=1" \
  -f lavfi -i "color=c=0x2050D0:s=320x240:r=30:d=1" \
  -f lavfi -i "sine=frequency=4000:duration=2" \
  -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0,format=yuv420p[v]" \
  -map "[v]" -map 2:a \
  -c:v libx264 -preset veryfast -crf 28 -g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p \
  -c:a aac -b:a 64k -ar 48000 -ac 2 \
  -fflags +bitexact -flags:v +bitexact -flags:a +bitexact -map_metadata -1 -t 2 inset.mp4

ls -l card.png inset.mp4
