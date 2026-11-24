# Video events evaluation harness

Measures how well a detector finds frame-accurate event anchors (see `docs/design/video-events-analysis.md`).

- `labelled/synthetic/` committed exact labels (+ DeclareRenderEvents sidecars) for the 12 synthetic clips; videos are regenerated into `${VEA_DATA:-~/ve-data}` by `synthetic/generate.mjs`.
- `real/` licence-clean real clips (manifest, fetch, labeller). Videos and real labels' source clips stay outside the repo.
- `baseline-runner/` runs today's VideoAnalyze anchor detectors (real engine classes, `--config default|best`) or the layer-1 event detector (`--config events`) and writes `<clip>.pred.json`.
- `score.py` precision/recall/F1 within ±N frames (default 2) per event type and per video type; `control.py` chance control.
- `results/` committed baseline scores.
- `candidates.md` survey of models and libraries for layers 2-3.

Pred schema: `{"clip","fps","anchors":[{"id","frame","type"?}]}`. Untyped anchors are scored on the `untyped` table.
Run: `dotnet run --project baseline-runner -- --videos ~/ve-data/synthetic --out /tmp/p --config best` then
`python3 score.py --labels labelled/synthetic --pred /tmp/p`.

## Layer-1 event detector and its regression gate

`--config events` runs the real `VideoEventDetector` (`inference/src/ReelBolt.WorkflowEngine/Services/Video/Events/`) and writes typed
predictions: `type` is the event name in snake case, matching the label vocabulary (`move_start`, `camera_move_stop`, ...). Beat events are
left out unless `--beats` is given (the recorded results use `--beats`). Beats are only reported inside music-present regions (a steady
onset pulse; speech, silence and noise give none). The `speech` clips carry `expect.noBeats` and score.py counts their predicted beats
(`noBeatClips`); gate.json requires 0 (`maxBeatsOnNoBeatClips`). The `music` clips are labelled with `beat` at each kick onset. The pred file also carries `seconds` and `videoSeconds` for timing.

```
dotnet run --project baseline-runner -- --videos ~/ve-data/synthetic --out /tmp/ve-ev --config events --beats
python3 score.py --labels labelled/synthetic --pred /tmp/ve-ev --name synthetic/events \
    --out results/synthetic/events.json --md results/synthetic/events.md
python3 gate.py                      # exits non-zero below a floor in gate.json
python3 gate.py --self-test          # tamper check: results scored at tolFrames != 2 are refused
```

`gate.json` holds floors for typed precision and recall at +/-2 frames: overall, per video type, per event type (plus the untyped overall
F1). Each floor is what the detector achieved on the 16 synthetic clips minus 0.05, so a real regression fails and run-to-run noise does not.
After an intended improvement, regenerate with `python3 gate.py --record results/synthetic/events.json`.

CI vs local: the videos stay outside the repository (see `synthetic/generate.mjs`), so the gate is a local/release check run on a machine
that has `~/ve-data`. What CI runs is the xUnit set `inference/tests/ReelBolt.WorkflowEngine.Tests/Video/Events/`: pure detector tests on
synthetic in-memory frames for every event type (a detection counts within exactly +/-2 frames), and mini clips that ffmpeg renders at test
time with known event frames (skipped when ffmpeg is absent), plus determinism tests.
