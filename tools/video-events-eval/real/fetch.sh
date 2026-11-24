#!/usr/bin/env bash
# Cut licence-clean excerpts listed in manifest.csv straight from their URLs.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${VEA_DATA:-$HOME/ve-data}/real"
UA='ReelBoltEval/1.0 (research; contact: hello@reelbolt.ai)'
mkdir -p "$OUT"; LOG="$OUT/fetch.log"; : >> "$LOG"
python3 - "$HERE/manifest.csv" > "$OUT/.rows.tsv" <<'PY'
import csv,sys
for r in csv.DictReader(open(sys.argv[1],encoding='utf-8')):
    print("\t".join(r[k] for k in ("id","url","startSec","durSec")))
PY
cut_one() { # id url start dur
  local id=$1 url=$2 ss=$3 dur=$4 tmp="$OUT/$1.part.mp4"
  local vf='scale=min(960\,iw):-2' wl=file
  case "$url" in https://*) wl=https,tls,tcp,crypto ;; esac
  ffmpeg -nostdin -y -loglevel error -protocol_whitelist "$wl" -user_agent "$UA" -ss "$ss" -i "$url" -t "$dur" \
    -map 0:v:0 -map '0:a:0?' -vf "$vf,format=yuv420p" -r "$fps" -fps_mode cfr \
    -c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k -movflags +faststart -f mp4 "$tmp" 2>>"$LOG"
}
while IFS=$'\t' read -r id url ss dur; do
  [[ "$id" =~ ^[a-z0-9-]+$ && "$ss" =~ ^[0-9]+$ && "$dur" =~ ^[0-9]+$ && "$url" =~ ^https://[A-Za-z0-9._~%/:?=\&+,@!*()-]+$ ]] || { echo "bad manifest row: $id" >&2; continue; }
  [ -s "$OUT/$id.mp4" ] && [ -n "$(ffprobe -v error -select_streams v -show_entries stream=codec_name -of csv=p=0 "$OUT/$id.mp4")" ] && { echo "skip $id"; continue; }
  echo "== $id $url" >> "$LOG"
  # source fps capped at 30
  fps=$(ffprobe -v error -protocol_whitelist https,tls,tcp,crypto -user_agent "$UA" -select_streams v:0 -show_entries stream=avg_frame_rate -of csv=p=0 "$url" 2>>"$LOG" | head -1)
  fps=$(python3 -c "
import sys
a=sys.argv[1].split('/')
try: f=float(a[0])/float(a[1]) if len(a)>1 else float(a[0])
except Exception: f=30
print(round(min(f if f>0 else 30,30),3))" "$fps")
  export fps
  ok=0
  src="$url"
  for try in 1 2 3; do
    if [ $try = 3 ]; then
      # direct seeking failed (e.g. Ogg over HTTP): download the whole file politely, cut locally
      full="$OUT/.full-$id"; curl -sSLf --proto =https --proto-redir =https --max-redirs 5 --max-filesize 2G --retry 3 --retry-delay 20 -A "$UA" -o "$full" "$url" 2>>"$LOG" && src="$full"
    fi
    rm -f "$OUT/$id.part.mp4"
    fps="$fps" OUT="$OUT" LOG="$LOG" UA="$UA" nice -n 10 bash -c "$(declare -f cut_one); cut_one \"\$@\"" _ "$id" "$src" "$ss" "$dur" || true
    if [ -s "$OUT/$id.part.mp4" ] && [ -n "$(ffprobe -v error -select_streams v -show_entries stream=codec_name -of csv=p=0 "$OUT/$id.part.mp4")" ]; then ok=1; break; fi
    echo "retry $id" >> "$LOG"; sleep 20
  done
  rm -f "$OUT/.full-$id"
  # apply fps via second pass only if needed is skipped: -r handled below
  if [ $ok = 1 ] && [ -s "$OUT/$id.part.mp4" ]; then mv "$OUT/$id.part.mp4" "$OUT/$id.mp4"; echo "ok $id"
  else rm -f "$OUT/$id.part.mp4"; echo "FAIL $id" | tee -a "$LOG"; fi
  sleep "${FETCH_PAUSE:-2}"
done < "$OUT/.rows.tsv"
python3 - "$HERE/manifest.csv" "$OUT/ATTRIBUTION.md" <<'PY'
import csv,sys
L=["# Attribution (internal measurement only, no redistribution)\n"]
for r in csv.DictReader(open(sys.argv[1],encoding='utf-8')):
    L.append(f"- {r['id']}: {r['title']} - {r['attribution']} - {r['licence']} - {r['url']} (excerpt {r['startSec']}s +{r['durSec']}s)")
open(sys.argv[2],'w',encoding='utf-8').write("\n".join(L)+"\n")
PY
