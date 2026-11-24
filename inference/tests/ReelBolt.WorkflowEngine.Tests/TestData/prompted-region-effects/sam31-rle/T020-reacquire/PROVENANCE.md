# Provenance — T-020 slot-merged selection replay

`response.json` is a **vendor output replayed offline**. It is data, never code: nothing here is
executed, imported or trusted to be well-formed. The test that reads it treats every field as
untrusted input and feeds it through the shipping `Sam31VideoRleResponseReader`.

Ticket: `prompted-region-effects` T-020 (slot-merged selection); the empirical answer is
`trials/fal/T020/analysis.md`.

## Where it came from

| what | value |
|---|---|
| initiative | `prompted-region-effects`, trial `T020` |
| endpoint | `fal-ai/sam-3-1/video-rle` |
| date | 2026-10-19 (fal request history) |
| prompt | `"speedometer display"` (the shipping client's exact input) |
| other inputs | `max_num_objects: 4`, `detection_threshold: 0.5`, `return_object_rles: true`, `apply_mask: false`, `boundingbox_zip: false` |
| trim request id | `01a154bf-7784-77b0-b9b6-4700be993e5a` (`fal-ai/workflow-utilities/trim-video`, first 3.75 s of the source) |
| segmentation request id | `01a154bf-fc4a-7cb3-8110-2bdeb579c5f7` |
| cost | ≈ $0.67 total (trim + segmentation; authorised) |
| source | the initiative's fal trial directory (private, not part of this repository) |
| sha256 | `76c694e08789f6c9bd6487501c45e690ac5ec67c340c20e6f7388e3fedd09fa9` |

This file is the `result` object only (709 489 bytes); the full transport envelope is left out
(see the sibling trial directory for `run-model-envelope.json`). Nothing was re-encoded: this is
the response exactly as it came back.

## What the answer shows (measured, not assumed)

1080x1920, 225 frames at ~60 fps. The vendor **renumbers the same object three times**:

| object_id | frames | first | last |
|---|---|---:|---:|
| 0 | 11 | 7 | 17 |
| 1 | 182 | 18 | 199 |
| 2 | 14 | 211 | 224 |

Frames 0–6 genuinely carry no object. The merged mask is non-empty on 207 of 225 frames; the
re-acquisition gap between id 1 and id 2 is 11 frames (0.18 s at 60 fps) and the id 0 → id 1 join
is 0 frames — both well inside T-020's `ReacquireGapSeconds = 0.5` window. The one-slot selector
picks id 1 and renders `empty` for frames 0–17 and 200–224, which is the reported symptom.
