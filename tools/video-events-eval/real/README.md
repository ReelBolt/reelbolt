# Real-clip evaluation set

41 licence-clean excerpts listed in `manifest.csv`. For internal measurement only; do not redistribute.

## Where videos live
Outside the repo: `${VEA_DATA:-$HOME/ve-data}/real/<id>.mp4`. Never commit video or frame files.

## Run
```
./fetch.sh                                   # sequential, nice'd, skips existing files, log in $VEA_DATA/real/fetch.log
python3 labeller/prepare.py $VEA_DATA/real/real-anim-01.mp4 [--candidates pred.json]
# open labeller/index.html in a browser, choose folder $VEA_DATA/real/frames/<id>/, label, press Export
```
Save the exported `<id>.labels.json` under `labelled/real/`. `--candidates` takes a baseline `<clip>.pred.json`
and shows its anchors as faint ticks; press `C` on a tick frame to accept one. Nothing is auto-accepted.

## Labelling protocol
- Event vocabulary and frame definitions: `../synthetic/README.md` (appear, disappear, move_start, move_stop, hit, text_in, text_out, camera_move_start, camera_move_stop, transition, speech_start, speech_end).
- Frame numbers are 0-based; precision target is +-1 frame.
- Label every visible event of the vocabulary, not only the salient ones.
- A different person re-labels a random 20% of clips; disagreements beyond 2 frames are discussed and resolved.
- Budget about 10 minutes per clip.

## Licence notes
- Tears of Steel is not used.
- Prelinger Archives items carry a "public domain mark" which is the uploader's claim, not verified.
- Per-clip attribution is generated into `$VEA_DATA/real/ATTRIBUTION.md` by `fetch.sh`.
