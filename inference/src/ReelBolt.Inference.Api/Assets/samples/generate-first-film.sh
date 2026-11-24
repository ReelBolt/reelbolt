#!/usr/bin/env bash
# Regenerates first-film.mp4: 24 s, 640x360, 25 fps, h264/aac, entirely synthetic (ffmpeg lavfi sources).
set -euo pipefail
cd "$(dirname "$0")"
ffmpeg -y -loglevel error \
  -f lavfi -i "color=c=0x1e3a8a:s=640x360:r=25:d=6,drawtext=text='Scene 1':fontcolor=white:fontsize=48:x=(w-text_w)/2:y=(h-text_h)/2" \
  -f lavfi -i "testsrc2=s=640x360:r=25:d=6" \
  -f lavfi -i "color=c=0x047857:s=640x360:r=25:d=6,drawtext=text='Scene 3':fontcolor=white:fontsize=48:x=(w-text_w)/2:y=(h-text_h)/2" \
  -f lavfi -i "smptebars=s=640x360:r=25:d=6" \
  -f lavfi -i "sine=frequency=330:sample_rate=44100:duration=24" \
  -filter_complex "[0:v][1:v][2:v][3:v]concat=n=4:v=1:a=0,format=yuv420p[v];[4:a]volume='if(lt(mod(t,6),4.2),0.4,0)':eval=frame[a]" \
  -map "[v]" -map "[a]" -c:v libx264 -preset medium -crf 30 -c:a aac -b:a 64k -movflags +faststart -t 24 first-film.mp4
