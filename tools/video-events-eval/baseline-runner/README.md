# BaselineRunner

Runs today's VideoAnalyze anchor detectors, using the real `ReelBolt.WorkflowEngine` classes
(project reference), on local mp4 files and writes `<out>/<clip>.pred.json` for `../score.py`.
Needs `ffmpeg`/`ffprobe` on PATH. No network, no paid calls.

    dotnet run --project tools/video-events-eval/baseline-runner -- \
      --videos <dir> --out <dir> --config default|best [--clip name]

Output: `{"clip","config","fps","anchors":[{"id","frame","kind","timeSec"}]}`; frame = round(timeSec*fps)
with fps from ffprobe `r_frame_rate`; anchors on frame 0 or the last frame are dropped; no `type` (untyped).

## Configs (what VideoAnalyzeStepExecutor offers an editor today)

- `default`: SceneDetection Standard, SceneThreshold 0.30, `ShotSpanConsolidator.AbsorbTransients` (MinShotMs 200),
  SubShotMode Off. Anchors: interior shot boundaries (`shotStart`) + silence edges (`silenceStart`/`silenceEnd`,
  -34 dB, 350 ms, only if the clip has audio).
- `best`: SceneDetection Sensitive (`SceneCutFinder`), SubShotMode Content (`SubShotBounds.Clamp` of 1.2/4.0/8,
  `ShotSplitter.SplitByContent`, `SubShotSignal` from a 32x18 grid at `SubShotPlanner.SampleFps`). Anchors: all
  span boundaries (`subShotCut` for content-split ones), silence edges, and `motionEdge` where the media-analysis
  motion class (`MotionSignalParser`, 1 s windows) changes.

## Config `events`

Runs the layer-1 `VideoEventDetector` instead of the anchor detectors and writes typed anchors (`type` in snake case, plus
`strength`/`origin`). `--beats` includes Beat events (left out by default: the labelled vocabulary has none).

## Limits

- Transcript word/segment anchors (`t{n}`) are not produced: ASR is paid.
- Vision captions are not run.
- Config defaults are mirrored as constants in `Program.cs` (the config record has required positional args);
  re-sync if `VideoAnalyzeStepConfig` defaults change.
- Only `*.mp4` files; silence is skipped for clips without an audio stream.
