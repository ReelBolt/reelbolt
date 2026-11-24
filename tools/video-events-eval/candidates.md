# Candidates for layers 1-3 (desk survey, 2026-11-10)

Sources are vendor pages, repositories and model cards; items marked (A) are engineering assumptions, (U) are
unverified. No paid or live call was made. Re-check every price on the live page before budgeting (several
vendors change rates on 1 Jan 2027). Costs are per video minute.

## Layer 1: local, shipped in the engine container (no egress)

| Rank | Candidate | Licence | Commercial | Cost | Note |
|---|---|---|---|---|---|
| 1 | ffmpeg `scdet`/`freezedetect`/`mestimate`/`select` + our own grid code (C#) | LGPL unless the build enables GPL (check our build) (A) | yes | CPU | already in image; no python needed |
| 2 | OpenCV DIS/Farneback optical flow | Apache-2.0 (A) | yes | CPU | needs an OpenCV sidecar; adopt only if the grid code misses the gate |
| 3 | PySceneDetect (adaptive) | BSD-3 | yes | CPU | cross-check on cuts only |
| 4 | PaddleOCR text detector (DBNet) | Apache-2.0 | yes | CPU, sampled frames | text in/out boxes; refine frame with energy signal |
| 5 | Tesseract | Apache-2.0 | yes | CPU | weak on stylised text |
| 1 (audio) | librosa onsets/beats, or first-party spectral flux | ISC | yes | CPU | first-party port avoids python |
| excluded | aubio (GPL), madmom models (CC BY-NC-SA), Essentia (AGPL + NC models) | | no | | |

## Layer 2: objects and points

| Rank | Candidate | Licence | Hosting | Cost | Egress |
|---|---|---|---|---|---|
| 1 | TAPNext / TAPIR / BootsTAPIR point tracking | Apache-2.0 | self-host, GPU | GPU time | none |
| 2 | SAM 3.1 video on fal (already integrated) | SAM License (royalty-free; military/ITAR, export, no reverse engineering, ends if we sue Meta; legal read needed) | fal | $0.01 per 16 frames ≈ $1.13 (SAM 3: $0.56) | video to fal; consent |
| 3 | SAM 2.1 | Apache-2.0 | self-host, GPU | GPU time | none |
| 4 | OWLv2 / Grounding DINO (weights licence U) | Apache-2.0 | self-host | GPU | none |
| 5 | Cutie | MIT code, weights U | self-host | GPU | none |
| excluded | CoTracker3 (CC BY-NC), YOLO-World/Ultralytics (AGPL-3.0, commercial licence needed) | | | | |

Layer 2 refines (which object moved, per-object appear/leave); it is not the primary detector.

## Layer 3: semantics

| Rank | Candidate | Licence/host | Cost | Time accuracy | Egress |
|---|---|---|---|---|---|
| 1 | Gemini 3 Flash class | Google API | ≈ $0.003 (low res, 100 tok/s) to $0.014 (high res) input plus output; rates change 1 Jan 2027 | 1 fps sampling, MM:SS; no published accuracy; use for windows only | frames/video to Google |
| 2 | Gemini 3.1 Pro | Google API | ≈ $0.012-0.036 input | same | same |
| 3 | Qwen3-VL 8B+ | Apache-2.0, self-host GPU | GPU time | text timestamps; no accuracy numbers found | none |
| 4 | TimePLE-8B (Qwen3-VL based) | Apache-2.0 (file U) | GPU | temporal grounding focus | none |
| 5 | TwelveLabs | hosted | ≈ $0.02-0.04 (secondary, conflicting) | search-oriented | video to vendor |
| 6 | Google Video Intelligence | hosted | $0.05 shots / $0.10 labels | shot level | video to Google |
| watch | TimeLens, Molmo2 | licence/release unclear | | | |

No model has published frame-level accuracy: layer 3 proposes windows and labels, layer 1 owns the frame.

## Datasets
No public dataset matches typed anchors on promo/animation/screen video (Kinetics-GEBD is CC BY-NC; TAP-Vid is
for point tracking). Our own labelled set is the benchmark; licence-clean real clips are listed in `real/manifest.csv`.
