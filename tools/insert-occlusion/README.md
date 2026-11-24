# insert-occlusion: synthetic green-screen clips with exact ground truth

The instrument behind the rigid-pose repair of tracked screen inserts
(`ChromaPlatePoseTracker`, docs/video-editing.md "Occluded corners and edges").
Nothing here runs in the product or in CI.

- `gen.py` renders 48 clips, 1280x720, 4 s, 24 fps: a device with a green plate
  (a flat rectangle, or a cylinder section) in front of a pinhole camera.
  Scene name = `{motion}-{surface}-{occlusion}`:
  - motion: `static`, `pan2d` (slide, zoom, roll), `tilt3d` (60 degree turn in
    depth), `handheld` (drift plus shake);
  - surface: `flat`, `curved`;
  - occlusion: `clean`, `cornertouch`, `cornercover`, `edgecover` (a whole edge
    and both its corners), `twocorners`, `split` (a bar crossing the plate).
  Each clip comes with `{name}.json`: per frame the plate's true corners and
  which corners and edges the occluder covers.
- `score.py` scores a run of `InsertTrackingQualityHarness` against that truth:
  corner error, corner error on the covered corners, content registration over a
  5x5 lattice of plate points, share of the visible plate left uncovered, and
  how much of the occluder was painted over.

```bash
pip install numpy opencv-python-headless
python3 tools/insert-occlusion/gen.py /tmp/synth
```

```bash
cd inference && REELBOLT_INSERT_CLIPS=/tmp/synth REELBOLT_INSERT_LABEL=run \
  REELBOLT_INSERT_SAVE_COMPOSITE=1 REELBOLT_INSERT_REFLECTIONS=0 \
  dotnet test tests/ReelBolt.WorkflowEngine.Tests -c Release \
  --filter "FullyQualifiedName~InsertTrackingQualityHarness"
```

```bash
python3 tools/insert-occlusion/score.py /tmp/synth run
```

`REELBOLT_INSERT_RIGID=0` on the harness run gives the behaviour before the
repair existed; `REELBOLT_INSERT_NO_COMPOSITE=1` skips the ffmpeg composite
(geometry only, about eight times faster).
