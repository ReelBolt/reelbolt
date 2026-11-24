# Video events analysis: anchors from pixels, acting on them, and the apps that use them

Status: DESIGN, awaiting approval. Nothing here is built. This phase delivers the
design, a measurement harness, a labelled set and a baseline of today's analyzer.
Related: [`docs/video-editing.md`](../video-editing.md), [`docs/editor.md`](../editor.md),
[`docs/design/workflow-apps.md`](workflow-apps.md), [`docs/fal-ai-models.md`](../fal-ai-models.md).

## 1. Problem

On a finished animation with a voice-over, a screen recording, or a long silent scene, the editor (an agent or
a person) is offered very few places to act. Inside a long shot the only anchors are shot bounds `s{n}`, speech
`t{n}`, silence gaps `g{n}`, prompted segmentation tracks `g{n}`, and (only for our own renders) render events
`e{n}`. The sound designer therefore places effects at arbitrary points, and nothing understands what moves,
appears, changes or says something on screen. The product bar: replace a human editor.

Three rules carry over unchanged from the existing system and are not up for change here:

1. Agents never write numbers or timestamps. Every action names an id; first-party code maps the id to a frame.
2. Anything computed from pixels is deterministic first; a model only adds meaning, and its times are snapped to
   frames found by code.
3. Third-party footage is analysed from pixels. Scene manifests and render sidecars are the exception (own renders).

## 2. The event model

One typed, ordered list per source, ids `e{n}` in frame order (the existing sidecar namespace; render-declared
events are merged into the same list with `origin: "declared"`, so one id space serves own and foreign video).

```
Event { id, type, frame, endFrame?, region?: [x,y,w,h] 0..1, strength: 0..1, track?: "g3"|"r2",
        origin: "pixels"|"audio"|"declared"|"model", label?: ≤60 chars, confidence }
```

Closed type vocabulary (append-only, persisted by name):
`appear, disappear, move_start, move_stop, hit, text_in, text_out, camera_move_start, camera_move_stop,
transition, change (large region change not otherwise typed), beat (audio onset/beat), speech_start, speech_end`.
Frame definitions (also the labelling rules) are in `tools/video-events-eval/README.md`.

`frame` is an integer in the source's own frame grid. The artifact keeps frames; the agent-facing view keeps ids,
types, a strength word (`weak|clear|strong`), a region word (3x3 pad: `top-left`..`bottom-right`) and no numbers.

## 3. The three layers

**Layer 1: local, deterministic, free (engine host).** One low-resolution decode pass (the existing grid sampler
family, extended in time resolution to every frame at 160x90 luma, plus a chroma plane), then pure C#:
per-cell motion energy and temporal difference, change-point detection on per-region energy (appear, disappear,
start/stop, hit = impulse-shaped energy), global camera motion by phase correlation or block-matching of the
grid (pan/zoom/shake versus object motion), text in/out from a text detector on sampled frames refined to the
exact frame by the energy signal in the detected box, transitions from dissolve/wipe/flash signatures beyond what
`scdet`-style cut detection finds, and audio onsets/beats (spectral flux, a first-party implementation: the
permissive libraries are listed in section 9; GPL/NC audio libraries are excluded).
Whether OpenCV is needed is decided by the measurement, not assumed: the engine image has no python or OpenCV
today (`apk add ffmpeg` only), so a C# port of grid motion/flow is the default, and an OpenCV sidecar is a
measured upgrade (adds an image dependency, Apache-2.0).
Cost: CPU only, no egress, no credits. Target: faster than real time on the engine host for 1080p sources via the
analysis proxy copy (shares the existing heavy-job slot).

**Layer 2: objects (optional, paid or self-hosted).** SAM 3.1 video segmentation on fal (existing client,
`AllowSourceMediaEgress` consent) seeded by Layer-1 regions ("what is in this moving region") or in an automatic
"everything" mode, yielding tracks `g{n}` with per-object events (object appears, leaves, starts/stops moving,
touches another). Dense point tracking (TAPNext/TAPIR, Apache-2.0) is the self-hosted option when a GPU exists.
CoTracker3 (CC-BY-NC) and YOLO-World/Ultralytics (AGPL) are excluded.

**Layer 3: semantics (optional, paid).** A vision/video model labels event windows (contact sheets or short
clips): "a button is pressed", "the logo lands", "the speaker laughs", ranks importance, and writes the label
text. Its returned times are never used: each label attaches to the nearest Layer-1 event inside a window, or
is dropped. Gemini Flash is the ranked first candidate; Qwen3-VL/TimePLE is the no-egress candidate.

Each layer is independently switchable per run; Layer 1 is on by default whenever VideoAnalyze runs
`AnalyzeEvents` (new, default off for byte-identical existing runs, on in the new apps).

## 4. Surfaces

| Surface | Change |
|---|---|
| `VideoAnalyze` | new `view.events` (`e{n}`), artifact `Events` with frames; `meta.events {count, byType, digest}`; budget pass like placements (suppression warnings, `+events-1` revision tag, step-cache schema bump). `BuildIdTimeIndex` resolves `e{n}` point events as zero-length anchors only for action planners, never as `Keep` spans (a cut on an event is a new, explicit action, section 5). |
| Consumers | `SoundDesigner` (already accepts `e{n}`), `MotionGraphicsPlanner`, `VideoStoryEditor`/rooms, new `MotionDirector` (section 5). |
| Editor | **Events lane** under the ruler: ticks by type/colour, strength as height, snapping target for the playhead and for clip edges (`S` toggle), context menu "Add sound here", "Add title here", "Zoom here", "Cut here". Lane is read-only data (not in the document); analysis result cached by file digest like the other kinds. |
| Media analysis | new kind `events` beside silence/shots/motion/transcript (`POST/GET …/analysis`, `…/analysis/events`, estimate). Layer 1 = free; Layers 2-3 listed as separate paid kinds `objects`, `meaning` with their own estimate and consent. |
| Assistant / MCP | read tools `ListVideoEvents(source, section?, types?, limit)` and `DescribeVideoWindow(fromEventId, toEventId)` (section 6); chat count 115 → 117 and MCP catalog 77 → 79 (both free, read-only, in `AssistantApprovalCatalog` as not gated, annotations `readOnlyHint`). Action tools go through the existing Propose/Apply pairs. |

## 5. Acting on events: from "sound designer" to "editor"

Every action anchors to an event id (or a shot/speech id); first-party code resolves frames. Mapping to what exists
(from the scout of `VideoCompileStepConfig`, `OutputSchemas`, editor `Item`):

| Action | Exists today | Anchored today | Gap to close |
|---|---|---|---|
| SFX | `EnableSfx`, `SfxPlan`; `SoundDesigner` | s/g/t and `e{n}` | Only works on own renders; fixed by section 3 |
| Zoom / punch-in / push / pan | `Reframes` (keyframed zoom/centre/easing), editor `MediaClip.transform` | numeric source seconds | New word plan `MotionPlan` (agent writes `anchorId`, `Move` word: PunchIn/PushIn/PullOut/PanTo/…, `Intensity`, `Hold`); code expands to `Reframes` keyframes from the event frame and region. No new filter. |
| Follow | `FollowSubject` (track `r/g{n}`) | track id | Offer to the planner; no engine gap |
| Shake | none (editor `Stabilize` is the inverse) | – | New `ShakeCue` as a deterministic crop-offset keyframe set (seeded noise) on the Reframe path; render-parity item |
| Speed ramp / retime | editor `playbackRate`, `retimeQuality`; `EnableSpanMotion` reserved no-op in `VideoCompile` | – | Implement ramp in `VideoCompile` (`setpts` curve, audio `atempo` chain or muted), anchored to an event with `RampIn/Out` words; editor path already exists |
| Transitions | `TransitionPolicy` (Auto/Expressive/Editor words per seam) | seam of keep spans | Seams on events need cut-on-event; policy Editor words reused |
| Flash / glow / VFX | `FlashWhite` only as a seam transition; `RegionEffects` (Blur/Pixelate/Highlight/Spotlight/ColorPop/Outline), no time window | track id | Add `Flash`, `Glow` effects and an optional `[fromEvent,toEvent]` window to `RegionEffects`; standalone `FlashCue` on an event |
| Graphics on an event | `MotionGraphicsPlanner` over `p{n}` placements; `ScreenInsert` | `p{n}` | `Placement` gains `anchorEventId` so a graphic starts on an event; `Kind` (LowerThird/Callout/Title) becomes real |
| Cut on an event | `VideoEditDecisionOutput.Keep` over s/g/t only; `e{n}` refused as Keep | s/g/t | New `CutPoint` anchors: a keep span may begin/end at an event (`FromId`/`ToId` accept `e{n}`), snapped to a frame, still no numbers |
| Music duck / beat cut | `MusicDucking`, `BeatSync` (config only) | config | Planner may choose `BeatSync` per run; beat events `beat` feed it |
| Colour | `ColorGradeRoom` | whole programme | none for this initiative |

Planning agent: a new `MotionDirector` (one agent, one output schema `MotionPlanOutput`, all strings/words,
invariant test forbids numeric members) for zoom/shake/ramp/flash on events, run beside `SoundDesigner`,
`MotionGraphicsPlanner` and the story editor. Compile applies every plan through the same drop-with-reason
mechanism (`unknown_anchor_id`, `anchor_cut_away`, …). Review loop `ReviewAdjustableSettings` gains the intensity caps.
Editor parity: the same actions are `setProperty`/`transform` keyframes on the timeline (no new op for zoom, shake;
speed ramp uses existing `playbackRate` keys); "Zoom here" on the Events lane writes those.

## 6. Full context, only as much as needed (progressive disclosure)

The agent must know the whole video without reading all of it. Four levels; every level is built deterministically,
a model summary is optional and cannot add events.

| Level | Content | Budget (tokens) | Built by |
|---|---|---|---|
| L0 Outline | duration class, sections (3-12, from shot bounds + speech paragraphs + long idles + big transitions), per section: id `c{n}`, 1-line deterministic descriptor (motion class, speech density, text presence, dominant event types), counts by type, top-3 events by strength; global facts (fps class, has audio/speech, camera-move share) | ≤ 1,500 for ≤10 min; +120 per extra 5 min, hard cap 3,000 | `OutlineBuilder` over the artifact; optional one Vision/Chat call fills `summary` (≤25 words/section), cached by digest |
| L1 Events per section | all `e{n}` of one section ranked by strength, capped 25 per section, with type, strength word, region word, nearest speech `t{n}`/shot `s{n}` | ≤ 1,800 per section; the agent requests sections, default = all sections within a 8,000 total, trimmed lowest strength first with a suppression warning | view builder (same trimming rule as `MaxOutputChars`) |
| L2 Window detail (read tool) | `DescribeVideoWindow(from,to)`: all events between two ids incl. weak ones, objects/tracks present, motion curve summary, text strings seen, speech lines | ≤ 2,000 per call, ≤ 6 calls per agent turn | tool over the artifact; stills via existing `LookAtOutput`/Stills derivative at most 6 images, one Vision call (paid, metered) |
| L3 Pixels | contact sheet of a window | via existing `LookAtOutput` | existing |

Initial prompt for a planner = L0 + L1 for the sections it works on, ≤ 8,000 tokens, versus ≤ 24,000 chars (~6,000
tokens) of view today; long videos stay bounded because L1 is per section and L2 is on demand. Ids only, no times;
sections are ordered and carry "position words" (opening, middle, closing). Outline and L1 are included in the
step-cache key through `meta.events.digest`.

## 7. Replacing the template zoo with a few apps

Inventory (26 templates; 19 video-editing): `video-derush-edit` (+ `testimonial-reel`, `-graphics`, `-music`, `-room`,
`-graphics-room`, `-grade-room`, `-sfx`, `-broll`, `-voiceover`, `-pickups`, `-dub`, `-dailies`), `narrated-story`,
`video-region-effect`, `green-screen-insert`, `video-subtitles`, `video-subtitles-translated`. Only ~9 of the 19 have
a typed manifest; the rest are derived from steps or free-text brief, and every variant is a separate step list.

Proposal: three video apps built on the dynamic inputs (typed, `showWhen`, lite/full, mapped bindings). A choice
reveals the steps; the engine's full range stays reachable through Advanced inputs, so nobody guesses which app.

| App | Inputs (lite → full) | Replaces |
|---|---|---|
| **Edit my video** (raw footage OR a finished video) | video files; "what is it" (raw footage / finished animation / screen recording); length; format; style; **Add**: sound effects (off/subtle/strong, file picker), music (file/none, beat sync), zooms and camera moves (off/subtle/dynamic), graphics (off/titles/callouts), transitions (auto/expressive), colour look, b-roll (off + spend cap), voice-over (off + language + voice), captions style; notes; review depth | derush-edit, testimonial-reel, -graphics, -music, -room, -graphics-room, -grade-room, -sfx, -broll, -dailies, -voiceover, -pickups (pickups = "fix flubbed lines" toggle), narrated-story |
| **Languages** | video; captions (off/burned/file); target languages; dub (off/voice) | -dub, video-subtitles, video-subtitles-translated |
| **Effects on a clip** | video; effect (blur object / put content on a screen / follow subject); target text; content file | video-region-effect, green-screen-insert |

`quick-win-promo*` and `product-profile-from-code` are not video editing and stay. Step lists inside "Edit my video"
are conditional (existing `Conditional`/disabled-step bindings from app inputs), so a run only holds and charges for
what the customer switched on (cost badge stays derived).
Order of work and fit with the sibling work: **making the typed app inputs the only implementation** (one implementation) is a hard
prerequisite; **the preset-recipes effort** reviews and ships preset recipes on the same inputs and overlaps these three
apps (both reshape the footage templates). Recommendation: merge the video-editing portion of the preset-recipes effort into
this rebuild (one owner, one set of templates), leaving the non-video presets (promo from code, avatar, thumbnail
pack) in the preset-recipes effort. Open for decision.
Migration: old template keys stay resolvable as aliases that open the new app with the matching toggles preset
(existing workflows keep running from their own snapshot; nothing is deleted from customer projects).

## 8. Costs, egress, consent

| Item | Cost per video minute | Egress |
|---|---|---|
| Layer 1 | CPU only (target ≤ 0.3 core-min per video minute; measured later) | none |
| Layer 2 SAM 3.1 on fal | ≈ $1.13 (1,800 frames at 30 fps, $0.01 per 16) ≈ 190 credits at the current credit value before markup; SAM 3 ≈ $0.56 | video to fal; `AllowSourceMediaEgress` required, never set by inputs |
| Layer 3 Gemini Flash | ≈ $0.003-0.014 input + output; cents | frames/clips to Google; same consent; Qwen3-VL self-host = none |
| Outline model summary | one chat call, ≤ 2k tokens | text only |

Hold/charge follow the existing ledger rules; billing off means free paths only. Prices are from vendor pages on
2026-11-10 and must be re-checked on the live page before budgeting (several vendors change rates on 1 Jan 2027).

## 9. Candidates

See `tools/video-events-eval/candidates.md` (ranked, with licence and cost). Summary in section 11.

## 10. Measurement

- Harness: `tools/video-events-eval/` (`score.py`, `baseline-runner/`, `synthetic/`, `labelled/`).
- Metric: one-to-one match within ±2 frames; precision, recall, F1 per event type and per video type; `untyped`
  (any anchor near a real event; the only fair score for today's untyped anchors) and `typed`.
- Sets: synthetic (12 Remotion clips, labels exact by construction, `DeclareRenderEvents` sidecars) and real
  (licence-clean, kept outside the repo; see `tools/video-events-eval/real/README.md`).
- Regression gate (after approval): committed synthetic labels + a small deterministic synthetic subset rendered in CI;
  real-set scores run locally and are recorded in an operator document.
- No paid calls anywhere; Layer 2-3 candidates are evaluated offline from vendor documentation until a paid run is
  approved (cost per run to be estimated before approval).

### 10.1 Baseline of today's analyzer (synthetic set, 12 clips, 126 labelled events, ±2 frames)

Produced by `tools/video-events-eval/baseline-runner` (real engine classes) and `score.py`. "default" is the shipped
configuration (shots at 0.30, silence gaps, `SubShotMode` Off); "best" is the strongest setting reachable today
(Sensitive scene detection, Content sub-shots, motion-class edges from the media-analysis motion kind). Today's
anchors carry no type, so the untyped score is the fair one; the typed score is 0 by construction.
Cells are precision / recall.

| scope | today default | today best | chance control (same count as best) |
|---|---|---|---|
| all clips | 1.00 / 0.14 | 0.34 / 0.37 | 0.09 / 0.10 |
| animation | 1.00 / 0.32 | 0.47 / 0.52 | 0.08 / 0.09 |
| screen | n/a / 0.00 | 0.28 / 0.22 | 0.18 / 0.14 |
| silent | n/a / 0.00 | 0.17 / 0.32 | 0.00 / 0.00 |

Recall per event type (untyped; the chance control shows how much of "best" is luck):

| event type | n | default recall | best recall | control recall |
|---|---|---|---|---|
| appear | 17 | 0.00 | 0.18 | 0.12 |
| camera_move_start | 3 | 0.00 | 0.33 | 0.00 |
| camera_move_stop | 3 | 0.00 | 0.33 | 0.00 |
| disappear | 6 | 0.00 | 0.00 | 0.00 |
| hit | 19 | 0.00 | 0.26 | 0.16 |
| move_start | 21 | 0.00 | 0.19 | 0.05 |
| move_stop | 7 | 0.00 | 0.57 | 0.14 |
| speech_end | 8 | 1.00 | 1.00 | 0.00 |
| speech_start | 8 | 0.88 | 0.88 | 0.12 |
| text_in | 19 | 0.00 | 0.16 | 0.16 |
| text_out | 3 | 0.00 | 0.00 | 0.00 |
| transition | 12 | 0.25 | 0.83 | 0.08 |

Reading:
- Default finds only cuts and speech edges: speech_start/end 0.88-1.00 (silence gaps; the synthetic voice has hard
  silence, so real speech will score lower), hard cuts yes, and nothing else. Screen recordings and silent scenes score 0.00 recall.
- "Best" lifts recall to 0.37 only by emitting 91 unmatched anchors (0.34 precision); on screen recordings its
  recall (0.22) is near the chance control (0.14). On silent scenes its recall comes from motion-class edges.
- appear/disappear, text in/out, hit, move start/stop and camera moves are essentially unseen today. That is the gap
  the sound designer sees: nothing inside a shot.
- Caveat: the synthetic clips contain unlabelled decoration motion (a few bouncing balls, a drifting background gradient), so
  precision of any detector is a lower bound. Real-set baseline (41 licence-clean clips) is pending hand labels.
Real set status: 35 of 41 manifest clips were fetched (45-60 s excerpts, kept in `~/ve-data/real`, outside the repo);
6 failed (4 Ogg sources that cannot be seeked over HTTP and Wikimedia rate limiting: `real-anim-12`, `real-scr-02..06`; the
fetcher has a whole-file fallback, rerun later). They are NOT yet labelled (about 10 minutes per clip, a human task).
What today's analyzer offers on them, anchors per excerpt (mean): default 11.7 on animations, 11.5 on screen recordings,
0.6 on silent scenes; best 31.8, 34.7 and 12.1. On a silent long scene the shipped default hands an editor almost nothing to anchor to.
- The regression target after approval is a typed score: layer-1 must beat these numbers per type and per video type
  with a margin fixed on the real set.

## 11. Phasing after approval

1. Layer 1 events + `view.events` + `events` analysis kind + regression gate (tickets: sampler, detectors, text, audio, artifact/view, tests).
2. Events lane + snapping + "add sound/title/zoom here"; `ListVideoEvents`/`DescribeVideoWindow`; outline builder.
3. Cut-on-event, `MotionDirector`, speed ramp, shake/flash/glow, graphics `anchorEventId`.
4. The three apps on app-inputs (after the typed-inputs consolidation).
5. Layers 2 and 3 behind consent, measured on the same set before they ship.

## 12. Risks

- Layer 1 may not reach ±2 frames on soft or low-contrast events (the harness measures; the gate is the baseline plus a margin, not a promise).
- Text detection and hit detection are the hardest event types; they are scored separately and can ship later.
- Replacing 19 templates changes what customers see in the gallery; aliases keep old links working.
- Licence: SAM License needs a legal read; GPL/NC audio and tracking libraries are excluded.
