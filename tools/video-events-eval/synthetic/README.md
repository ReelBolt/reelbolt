# Synthetic labelled clips

Sixteen small clips: twelve Remotion-rendered (4 `animation` with a synthetic voice-over, 4 `screen`
recordings, 4 `silent` long scenes) plus 2 `speech` (static picture, the voice-over, labels speech_start/end only, `expect.noBeats`)
and 2 `music` (static picture, steady kick plus chord pad, a `beat` label on every kick; ffmpeg-rendered, no Remotion) with exact event frames, for measuring a video-event detector.

Labels are exact by construction: `src/model.js` holds the pure timeline functions (opacity,
position, scale, camera, scene blend). The renderer (`src/Main.tsx`) draws from them and
`computeEvents` scans the very same functions for the label frames. Nothing is annotated by hand.

## Use

```
# node_modules: symlink an existing install of the sandbox template (same pins), do not commit it
ln -s <path-to>/sandbox/template/node_modules node_modules
node gen-specs.mjs                 # specs/*.json (hand-designed timelines)
node generate.mjs [--labels-only] [clip ...]   # labels + sidecars, then renders
node validate.mjs                  # frames in [0,frames), types in vocabulary, sidecar consistent
```

Videos go to `${VEA_DATA:-~/ve-data}/synthetic/<clip>.mp4` (never committed). Browser:
`VEA_BROWSER` (default `/usr/bin/google-chrome`). Render runs at `nice -n 10`, `--concurrency=2`,
960x540 at 30 fps. Labels are committed to `../labelled/synthetic/<clip>.labels.json`, plus the
`DeclareRenderEvents` sidecar `<clip>.events.json` (`{version, fps, events:[{name, frame, label}]}`,
names like `appear-ev1`).

## Vocabulary and label-frame definitions

appear/text_in: first frame with opacity >= half the element's peak (slide-ins: first frame fully in
place). disappear/text_out: first frame below half peak (slide-outs: fully off canvas).
move_start: first frame displaced >= 0.5 px from rest; move_stop: first frame within 0.5 px of the
final rest. hit: ball touching the floor, scale pop / shake / button press starting, or a cursor
click (ripple starts). camera_move_start/stop: same 0.5 px (or 0.00125 scale) rule on the global
pan/zoom. transition: first frame of the new scene (cut) or the blend midpoint (wipe, fade).
speech_start/end: onset/offset frame of each synthetic phrase.

## Caveats

- Elements can carry `skip` (suppress label types) or `quiet` (drawn, never labelled) to keep
  4..14 labelled events per clip; unlabelled motion (e.g. a bouncing decoration ball) still exists.
- Eased moves start slowly: the label is the first frame >= 0.5 px, a few frames after the nominal start.
- `silent` clips use a slowly shifting background gradient (no spatial motion) and are unlabelled.
- Voice-over is band-limited noise plus a hum, syllable-rate amplitude modulation; AAC adds a few ms of delay.
