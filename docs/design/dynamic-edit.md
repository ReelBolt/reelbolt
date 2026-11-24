# Dynamic edit: pace, a closed effect catalogue, one default style preset

Status: design for initiative `dynamic-edit-style-preset` (issue #1253). Words and ids come from the agents; every number is computed by the compile from measured data (beat grid, scene boundaries, source fps). No model-supplied numbers anywhere (existing invariant tests stay green).

## What was wrong on the b-roll film (measured 2026-11-19)

Source `C3.mp4` (19.82 s) holds 7 real angle changes: 2 hard cuts (4.94, 13.25) and 5 soft ones (whip 4.9 blur, zoom 7.9, logo warp 10.9-11.6, whip to wheel 15.9, wheel spin 17.6). It is not one take.

- `VideoAnalyze` Standard uses ffmpeg `scene > 0.30`: it found only the 2 hard cuts (0.40, 0.58). Soft ones score 0.05-0.08. A 0.2 s lagged frame difference finds 7 of 7 (threshold 0.14).
- The 10 offered ids were content sub-shots of 3 shots. The story editor kept them abutting and `CoalesceAdjacent` merged them, erasing every boundary.
- A cut inside contiguous footage is invisible by itself. Visible dynamics need something at the seam: a framing change, a transition, a source switch, a flash.

Two fixes, both opt-in (byte-identical off):

1. Detection: `SceneDetectionMode.Soft` (appended). Standard shots plus a lagged frame-difference pass (`SoftTransitionDetector`: 24x42 thumbnails at source fps, difference between frames 0.2 s apart, local maxima above 0.14, merged within 0.6 s, snapped to the nearest frame). Its boundaries become real shot boundaries (`method: "soft"` on the shot).
2. Compile: `VideoCompileStepConfig.Dynamics` keeps offered shot/sub-shot boundaries as cut points (no coalescing across them) and runs the `DynamicsPlanner`.

## The closed catalogue (`DynamicEffect`, append-only, by name)

The story editor (and the edit room director) may list `dynamics[]` entries in its `VideoEditDecisionOutput` (additive, string-only, empty valid). Entry: `{ effect, anchorId, otherId?, moment, beats?, division?, winner?, intensity?, reason }`. All words except ids and `reason`.

| Effect | Words it takes | What the compile does (all numbers derived) |
|---|---|---|
| `SplitOnBeat` | anchor span, `moment` | Splits the anchor span into segments on grid lines inside it (the cuts a continuous take lacks); each segment is framed by the planner (alternating wide / punch). |
| `PunchIn` | anchor, `moment`, `intensity` Subtle/Normal/Strong | Reframe keyframes on the segment: zoom 1.0 to 1.08/1.15/1.30 by `intensity`, centre from the subject safe area (centre when unknown), easing Hold on the beat then EaseOut. Uses existing `Reframes`. |
| `PunchOut` | same | Zoom from the punch back to wide over the beat. |
| `BeatAlternate` | `anchorId` (A), `otherId` (B, a different source or span), `beats` Two/Four/Eight/Sixteen, `division` Beat/HalfBeat/Bar, `winner` A/B | Alternates A and B on every grid cell of the division for N beats, then holds the winner to the end of the anchor window. Cell length = grid spacing x division; frame-aligned. Each cell plays consecutive footage of its source (A continues where it left off) so the sources "mix". |
| `StrobeCut` | anchor, `otherId`, `moment`, `division` | `BeatAlternate` without a winner, for `moment` OnDrop: bounded to one bar. |
| `FlashFrame` | anchor, `moment` | White (or black via `intensity`) overlay for one frame at the grid line (xfade-free, `drawbox`-style fill on that frame). |
| `SpeedRamp` | anchor, `moment` OnDrop/OnAccent, `intensity` | Video-only retime (setpts) of the 1 beat before the grid line (0.6x/0.5x/0.4x by intensity then snap back to 1x); only on spans whose source audio is muted (`MuteSourceAudio`) so audio sync is never touched; otherwise dropped and reported. |
| `WhipOnBar` | anchor boundary, `intensity` | At a bar line cut, a whip: the last and first frames around the cut are slid and blurred horizontally (`WhipLeft`, `WhipRight`, one eighth of a beat per side, whole frames). Duration-neutral on purpose: no xfade overlap, so no later frame moves, the cut stays on its beat, and it survives beat sync (which turns every overlapping seam back into a hard cut). Counted as applied only when it is in the render graph (`whipsRealised`), otherwise reported dropped (`whip_not_rendered`). |

`moment` is one of `OnBeat`, `OnBar`, `OnDrop`, `OnAccent` (accent = a grid line where the music's onset strength is in the top decile; the planner measures it; the model never says when). Unknown word, unoffered id or unknown source: that entry is dropped and reported `dynamics_entry_dropped` with the reason; the compile never fails on a plan.

## How pace bounds the catalogue (`DynamicPace`: Calm, Medium, Fast; `StyleDynamics` axis)

| | Calm | Medium | Fast |
|---|---|---|---|
| Entries kept per 10 s of output | 1 | 3 | 6 |
| Effects allowed | PunchIn, WhipOnBar | + SplitOnBeat, PunchOut, FlashFrame | + BeatAlternate, StrobeCut, SpeedRamp |
| Max intensity | Subtle | Normal | Strong |
| Min gap between effects (start of one move to the start of the next) | 4 beats | 2 beats | 1 beat |
| `BeatAlternate` max beats / min division | n/a | n/a | 16 / HalfBeat |
| Auto fill: punches added around the agent's entries (and when it lists none) | none | every 4 beats on the bar and drop lines | every 2 beats, alternating normal and strong, framing cycling through a small table |

Excess entries are dropped lowest-priority-first (priority: BeatAlternate, SpeedRamp, SplitOnBeat, PunchIn, FlashFrame, WhipOnBar) and reported. Never one cut on every beat unless the agent explicitly asks for `BeatAlternate`/`StrobeCut`, and those are capped per pace.

The auto fill is a fill, not a plan: it runs after every entry of the agent, in time order, and only takes a slot that is outside the min gap of any other move (a flash, a ramp, a whip, an alternation) and while the pace budget lasts. A fill punch lasts one beat, so at Fast two beats apart they sit one beat clear. An agent punch lasts two beats, so at Fast (gap one beat, start to start) the next move can start inside it: a plain punch is therefore bounded by the next reframe's start on its span (cut short, never overlapping), and anything else that would overlap is a `min_gap` drop. The reframe set the planner returns always passes `VideoReframe.Validate` (tested on the real model's word lists and on random lists at every pace); if the compile still drops the dynamics reframes, the report lists the punches as dropped (`dynamics_reframes_dropped`, with the count) and not as applied. It never replaces an agent entry and is reported separately (`autoAdded`). The budget is "per 10 s of the film": the body the planner sees, or the compile's target length when that is longer (the cold open and the end card are part of the film). Fast over a 30 s target therefore allows 18 moves, and the per-source reframe cap (16) still holds.

A generated clip is an offered id too: the clip id from the generated-clips manifest (`v2t1`, after the dailies room's take selection) can be the `otherId` of `BeatAlternate` and `StrobeCut`. The compile downloads and normalises it like a seam bridge, offers it to the planner as pool footage, and plays it from its first frame, consecutive per side, filling the canvas; the winner hold continues the winner. An id that is not in the manifest, not selected or not delivered drops the entry as `unknown_other_id`.

## Where the control lives

- New `StyleConfig` axis `dynamics` (`StyleDynamics`: `Calm`, `Medium`, `Fast`; append-only, null = no preference, byte-identical for existing styles and presets).
- New built-in preset `dynamic-edit` ("Dynamic edit", Fast pacing, Mixed editing, dynamics Medium, graphics Moderate, suggested vertical shape) in `StylePresetCatalog`. A preset key is append-only; the preset's dynamics value is the default.
- The run brief gets a `pace` choice (`calm|medium|fast`) that overrides the style's dynamics; it is a visible input in the app's inputs (the style picker shows the preset, the pace is a segmented control beside it).
- `RunStyleOverrides.ApplyToCompile`: when the run's style carries dynamics and the compile has no explicit `Dynamics`, it enables `Dynamics` (pace from the style), `TransitionPolicy` Editor if off, `SceneDetection` is not changed on a finished analysis (analysis already ran) — templates that want soft boundaries set `SceneDetection: Soft`.
- `StyleNudgeRegistry`: editor-role and graphics-role lines for the new axis tell the agent the catalogue words and the pace bound; story editor prompt (both copies) gains a "Dynamics" section and loses "prefer fewer, longer runs" only when a pace is set (a conditional line, base prompt unchanged so golden prompts stay).

## Compile pipeline placement

After span normalisation and BeatSync (`VideoCompileStepExecutor` ~1151), before encode: `DynamicsPlanner.Plan(spans, grid, boundaries, plan, pace, fps)` returns `(segments[], reframes[], flashes[], retimes[], seams[], report)`. Pure and unit-tested. Each product maps to an existing stage: segments to spans, punches to `Reframes` (per-segment), flash to a one-frame overlay, retime to a `setpts` on the segment, whips to the seam planner. Report goes to `outputSummary.dynamics` (applied, dropped with reasons, counts, `maxBeatOffsetMs`). Step cache: `DeterministicStepRevisions` `+dynamics-1` on `VideoCompile`, `+soft-scenes-1` on `VideoAnalyze`. The run's effective dynamics (style axis or brief pace) key a `VideoCompile` only (`StepCacheKeyInputs.RunDynamics`), never the shared `RunStyle` component, so a pace-only change re-runs the compile and nothing else; a compile with generated clips also carries `+cutaway-min-length-1` (the `cutaway_too_short` skip).

## Relation to event moves, flash cues, speed ramps and motion

The compile already has event-driven motion: `EventMoves` (punches and pushes on a detected event), `FlashCues`, `SpeedRamps` (a retime of the finished program) and `EnableMotion` (a motion director's plan). They answer "this event happened, show it"; Dynamics answers "the music has a grid, cut and move on it". They stay independent and neither is rewritten.

- **When both are configured**, the config's own event moves, flash cues and speed ramps own the moments of the source events they name. Dynamics yields: a move (or a fill punch) that would fall within the pace's min gap of such a moment is dropped, reported `event_move_owns_moment` for the agent's entries and silent for the fill. The event is mapped onto the program through the same spans Dynamics lays out, so one moment never receives two moves.
- **Speed ramps** do not stack either way: the upstream `SpeedRamps` retime the finished program after everything, Dynamics' `SpeedRamp` retimes one beat of one muted span inside it. The yield rule keeps them a min gap apart.
- **`EnableMotion`** cues come from a plan resolved after Dynamics runs, so Dynamics does not see them; a motion cue and a dynamics move can coincide. That is the one overlap not covered.
- **Music.** `musicSpansProgram` is true only when the program has generated segments and a music bed exists; the bed then runs under the whole program (cold open, body, end card) instead of under the body alone, which is what the proof films need. Dynamics adds nothing of its own. Every grid fact the compile places something on (beat sync, the drop-aligned cut, Dynamics, the OnBar/OnDrop/OnAccent words, the planner view's music facts) is read where the music really plays (QA-DYN-54, `MusicFilmTime`): the bed starts with the FILM, so a beat at track time `t` plays at film time `t` and at body time `t - coldOpen`. The cold open's length is therefore resolved before the beat sync, by a segment pass of `ResolveGeneratedClipsAsync` (cold open and end card: download, normalise, probe; the full pass later reuses those clips and a segment keeps its slot under `MaxGeneratedClips`). Beat and bar lines that play inside the cold open are no lines of the body; the drop is the track's drop at body time, and when it plays inside the cold open the drop-aligned cut and OnDrop are not used and reported (`beatSync.dropReason` / `dynamics.dropReason` = `drop_in_cold_open`; `beatSync.dropBodySec` is null). The bed is no longer shifted by the cold open's remainder (that only moved the sub-beat remainder and left the body 22 beats into the track on f1e9be7a, with bars 2 beats off): `beatSync.bodyShiftMs` is `-coldOpen`, `coldOpenSec` and `coldOpenOffsetMs` (how far the body's start is from a beat line) are reports only, `musicShiftMs` and `coldOpenRemainderMs` are gone. The cold open's join and a cutaway or end card edge are measured against the film's grid in `fileCuts`; the join is off the beat by the cold open's own remainder by construction. The planner view takes the cold open's length from the manifest's requested `durationSeconds` (the compile measures the real clip), so its `holds the drop` and length words stay approximate. Cache: `+beat-grid-film-time-1` on a compile with generated clips that cuts to the beat or runs Dynamics. The Dynamics-off path with a cold open changes on purpose: before, the body cuts sat on the track's beats in body time (on f1e9be7a up to a bar and a drop off what the bed plays).
- **Soft detection** is not stored in the source-pass cache: the cached shot pass carries spans only, and its key does not name the Soft mode, so a cached result could be served for the wrong mode. It runs every analysis, which is cheap next to the pass it replaces.

## Proof

Run 35b5ac16 (hand re-edit, 12 beat-aligned punch-ins) is the bar. With hand-written words, the preset reaches at least 12 dynamic moments on the same five generated shots plus the static source, with at least one `BeatAlternate` (static vs generated night shot, N beats, winner = generated); the preset alone does not choose that alternation, the words do. The `DynamicsPlanner` agent of the follow-up makes the alternation agent-choosable, because it sees the compile-time pool ids the story editor cannot. Executed as a new run reusing the existing generated clips (clip reuse via a project-file path, no generation spend), and compared by frame grabs and the compile report.

## Out of scope

Lipsync-style retime of dialogue, audio time-stretch, any model-supplied number, any change to existing presets or to a run with no dynamics.

## A late planner that sees the generated clips (`AgentType.DynamicsPlanner`)

The follow-up builds option (b) below. `DynamicsPlanner` runs after generation (and, in the dailies template, after the take selection) and emits only `dynamics[]` (`DynamicsPlanOutput`: the same string-only `DynamicsEntryOutput` plus a rationale; tools `ProjectRead` and `WorkflowControl`). It is handed a view built by `DynamicsPlannerViewBuilder`, not every earlier output: the source's offered shot ids with caption words, the generated clips the compile would accept (delivered, a picture purpose, admitted by the same take filter) with clip id (`v{n}t{k}`), purpose and the planned shot's description, the selected take ids, and the music as words and counts (tempo, bpm, beats per bar, drop present). Never a storage key. `VideoCompileStepConfig.DynamicsPlan` (last member, null is byte-identical) merges its entries with the story editor's own list, the plan winning on an identical anchor and effect; unknown ids are dropped by the compile with a reason, as before. The story editor keeps its own path. The b-roll and dailies templates carry the step; the app manifest gains no input (the pace input exists). Detail and a sample view: `docs/video-editing.md` "The dynamics planner".

## The edit room and the planner step (follow-up)

The edit room passes the director's `dynamics` list through unchanged and the compile reads it. A run with dynamics tells the room, through `StyleNudgeRegistry.RoomDynamicsWords` only (charter and base prompts byte-identical, pinned by `BasePromptsUnchangedTests`), that the catalogue moves are allowed, that the final decision lists them as words and offered ids, how to read the music beat facts, and that seats propose at most a few moves with anchor ids. No template combines the room with b-roll. The planner's base prompt and the view note agree: an `anchorId` is a source shot id, a generated clip id is only usable as the `otherId` of `BeatAlternate`/`StrobeCut`. The `DynamicsPlanner` step is skipped (Skipped result, no model call, no charge) when the run has no effective pace; the compile then runs without a plan.

## Cutaways on an alternating shot (QA-DYN-33)

A cutaway clip is anchored on an offered shot id and, before this fix, took the FIRST kept piece of that shot's source window (`OutputTimeline.MapWindowToOutput`). After a `BeatAlternate` the first piece of the anchor is its first A cell, so the cutaway replaced that cell (the static shot was seen twice only in the first alternation of the real film: the first alternation starts on its shot's first line, the later ones start mid-shot, so their first kept piece is the untouched start of the shot). The planner now reports where the alternation's cells end (`applied[].cellsEndFrame`, the end of the last whole cell; what follows is the winner's hold). The compile maps the anchor's window to ALL its output pieces (`MapWindowToOutputAll`) and takes the first piece that is not inside a cell window: the stretch before the first cell, or the winner's hold when the winner is the anchor. A shot whose every piece is inside cells (winner B and the alternation from the shot's start, or a Bar division that fills the shot) gives `anchor_hosts_alternation` and the clip is skipped, never laid over a cell. Pieces of a shot without an alternation are untouched (same first-piece rule as before). Tested with a compile of three alternations (first, mid-shot, winner B) and green cutaway clips: A cells are the source colour, B cells the other source, the clip shows only where the alternation left footage.

## Why the file's cuts are off the grid when the report said they are not

Measured on the real Local film (run f1e9be7a, 140 bpm, 29.97 fps, Dynamics Fast, 3 `BeatAlternate`, 3 cutaways, cold open 9.44 s). Three different numbers were read as one:

| Field | What it covered | Real film |
|---|---|---|
| `dynamics.maxBeatOffsetMs` | only the moves the planner placed, frame rounding of its own lines (16.5 ms) | 16.5 |
| `beatSync.maxOffsetMs` | body cuts only, against the beat lines of the unshifted body grid; a half-beat alternation edge reads half a beat off (it predates the dynamic edit and stays as is) | 202.8 |
| `beatSync.fileMaxOffsetMs` (before) | body cuts and cutaway edges with Dynamics, against the shifted grid; no joins | 61 |
| `beatSync.fileMaxOffsetMs`, `fileP90OffsetMs`, `fileMedianOffsetMs`, `fileCuts[]` (now) | EVERY cut of the finished program: body cuts, split pieces, alternation cell edges, both edges of every cutaway, the cold open join, the end card join; each as time and offset | see the table below |

My own measurement of the file (frame-by-frame scene score of `film-local.mp4`, the cut's presentation time against the beat line `0.0372 + k x 0.42857 s`: the planned grid phase 26.4 ms plus the bed's 10.8 ms delay). Half-beat edges are against the half-beat grid. The file's timeline has a one-frame gap at the cold open join (frame 281 then 282, 66.7 ms apart), so every later time is a frame later than its frame number says; presentation time is what the audio sees.

| film time | type | offset ms |
|---|---|---|
| 9.443 | cold open join (clip length; the body starts 26 ms before its first beat line) | -23.0 |
| 9.910 / 10.310 / 10.744 / 11.178 | alternation 1 (p0, Beat) cell edges (the last one is weak) | +15.6 / -12.6 / -7.4 / -2.2 |
| 12.045 | body cut p0/p1 | +8.2 |
| 12.913 / 13.747 | punch-in onsets (reframe jumps, not cuts) | +18.6 / -4.4 |
| 14.181 | body cut p1/p2 and cutaway start | +0.8 |
| 14.648 | cutaway end, at alternation 2's first line (one frame late) | +39.3 |
| 14.815 | alternation 2 (p2, HalfBeat) cell edge, half-beat grid (206 ms against the beat grid, the coordinator's maximum) | -8.1 |
| 15.082 | body cut p2/p3 | +44.5 |
| 16.350 | body cut p3/p4 (the drop) | +26.8 |
| 17.184 / 17.618 / 18.051 / 18.485 | alternation 3 (p4, Beat) cell edges | +3.8 / +9.0 / +14.2 / +19.4 |
| 19.786 | body cut p4/p5 | +34.9 |
| 20.654 | punch-in onset | +45.3 |
| 21.088 | body cut p5/p6 | +50.5 |
| 23.690 | body cut p6/p7 | +81.7 |
| 24.024 | punch-in onset | -13.2 |
| 24.958 | body cut p7/p8 | +63.9 |
| 25.759 / 26.627 | punch-in onsets | +7.6 / +18.0 |
| 27.094 | end card join (the body ends 1.6 frames after its planned end) | +56.5 |

The 20 cuts (punch-in onsets excluded): median 17.5 ms, p90 56.5 ms, max 81.7 ms; the 8 body cuts alone: median 39.7, p90 63.9, max 81.7; the 9 alternation edges: median 9.0, max 19.4. Against the beat grid alone the half-beat edge reads 206 ms and the whole film's maximum is 212 ms (the coordinator's 37 / 163 / 212). The large offsets are not the music or the alternations: they are the body cuts, which sit 1 to 3 frames after the cut the beat sync planned (planned snapped ends 2.598, 4.741, 5.598, 6.884, 10.312, 11.598, 14.169, 15.455, 17.598 s; the file has them at 2.603, 4.738, 5.639, 6.907, 10.344, 11.645, 14.247, 15.516, 17.651 s after the join). Root cause: `DynamicsPlanner.Plan` lays each span on `round(end) - round(start)` frames of its own, so the rounding of every span adds up (the program is 17.651 s against the planned 17.598 s, 1.6 frames). QA-DYN-44 fixed it: `DynamicsPlanner.Layout` rounds each boundary from the exact cumulative time of the spans before it, so a cut is never more than half a frame from its planned time and the error never accumulates. A span may take one source frame past its window, but never past the next shot's first frame (`DynamicsSpan.LimitEndSec`); the shortfall goes to the next span, whose boundary is still the exact one. On an 8-shot synthetic film at 140 bpm the body cuts' maximum offset fell from 57.1 ms to 14.3 ms (seven body cuts, 14.3/28.6/33.3/19.0/28.6/42.9/57.1 before, 14.3/4.7/0.0/14.3/4.7/9.6/9.6 after). On the real C3 source (29.97 fps, 140 bpm, the proof films) the body cuts' maximum offset fell from 30.5 to 17.2 ms (median 10.8 to 4.7 ms) and the body from 528 to 527 frames (QA-DYN-59: re-measured on the proof films; the earlier 16.2 ms and 554 -> 552 figures were not this material). The film stays about one frame short of its plan with Dynamics off as well, so that residue is not this layout. The prod film itself was not recompiled (its analysis and plan are not on this machine); the 82 ms measured there is the same per-span rounding.

What the report now measures and what it leaves off the grid on purpose: `fileCuts[]` lists every cut above with its type and offset; `fileMaxOffsetMs` is the largest, `fileMedianOffsetMs` and `fileP90OffsetMs` (nearest rank) summarise them, and `dynamics.fileMaxOffsetMs` repeats the maximum next to the planner's own number. They are report-only and now also appear on a run without Dynamics that has generated clips (a cold open that is not a whole number of beats shows as its own offset, with no bed shift). Cutaway starts sit on an existing cut or on the winner hold's start, so they keep it: the clip's first frame is stamped on the window's first frame in whole-frame microseconds (a quarter frame early; stamped exactly on the frame it landed a tick after the base frame's own stamp and one frame of the anchor flashed in front of the cutaway, QA-DYN-43), and `fileCuts` reports the frame the picture changes. A cutaway's return edge is where its clip runs out unless an existing cut ends the window first, so on a dynamic edit it is snapped down to the nearest grid line (whole frames, never past the clip's own length, at least 3 frames long; `beatSync.cutawayEdgesSnapped` counts them). The cold open join is off the grid by construction (the bed is not shifted: it starts with the film, the body's cuts follow the music the film plays, so the join is off the beat by the cold open's own remainder, up to half a beat); the end card join is the body's end, which the beat sync never moves (the last span is never trimmed). `LoopedGrid` repeats the grid per track length, which is not a whole number of beats: it only matters when the program is longer than the track (it was not: 31.6 s of a 120.7 s track).

## Proof: every grid fact in film time (QA-DYN-54)

Measured from the rendered files of the prod film f1e9be7a (9.44 s cold open, 140 bpm, the track's drop at 6.884 s). Method: the music's lag in the film is the cross-correlation of the film audio with `music.mp3` (1 ms envelopes); cuts are the real frame pts of scene detection; the track's beat and bar phase come from the production analyzer run on `music.mp3`; `DynamicsProofTests.MeasureMusicGrid` (env `DYN_PROOF_DIR`, tool test `DYN_PROOF_FILM` + `DYN_PROOF_REPORT`).

| | BEFORE (film-local.mp4) | AFTER auto-cadence | AFTER agent-words | AFTER llm-words | AFTER llm2-words |
|---|---|---|---|---|---|
| bed lag in the film | +11 ms (remainder shift) | 0 ms | 0 ms | 0 ms | 0 ms |
| drop plays at film | 6.895 s (inside the cold open) | 6.884 s | 6.884 s | 6.884 s | 6.884 s |
| report on the drop | `dropAligned` true | false, `drop_in_cold_open` | same | same | same |
| a body cut at body 6.884 (film 16.33 s, no drop there) | yes (film 16.35 s) | no | no | no | no |
| body whole-beat cuts: median / max offset from a beat line | 45.2 / 81.6 ms (10 cuts) | 11.1 / 43.6 ms (11) | 14.6 / 22.8 ms (6) | 11.1 / 31.0 ms (13) | 11.1 / 43.6 ms (10) |
| bar-aligned (OnBar) moves, worst bar phase error | 1.993 beats (2 moves) | none placed | 0.043 beats (2) | 0.043 beats (2) | 0.034 beats (1) |

A frame is 33.4 ms; the one-frame measurement floor of the scene detector explains the larger maxima. llm-words keeps one cell edge 85.9 ms off the half-beat grid (film 21.755 s, two frames after the strobe's cell edge); it is reported, not gated, like llm2-words.

## Why words get dropped

For a 5 to 7 shot, 20 to 30 s film at Fast (140 bpm: beat 0.43 s, bar 1.71 s; budget 15 moves per 25 s; minimum gap one beat; shots of 3 to 5 s hold 7 to 12 beats):

| Drop | Verdict | Numbers |
|---|---|---|
| `StrobeCut` / `FlashFrame` `no_grid_line_in_window` | expected by design; defect in the guidance (fixed) | `moment` OnDrop has exactly one line (the drop). The beat sync puts the drop on a cut, so only the one shot that starts at it contains the line: 1 shot of 5 to 7 (14 to 20 %), 80 % or more of the guesses miss. Nothing in the view told the model which shot. OnBar (a line every 1.71 s) and OnBeat always have a line in any shot of a bar or more. |
| `BeatAlternate` `window_too_short` | expected by design; defect in the guidance (fixed) | An alternation needs two whole cells after its first grid line inside the shot: division Beat 0.86 s, HalfBeat 0.43 s, Bar 3.43 s, plus up to one cell to reach the first line (worst case 1.3 s, 0.9 s and 5.1 s of shot). A 3 to 5 s shot takes Beat and HalfBeat; Bar fails on any shot under about 5 s, and in the real film the p2 and p3 shots were 0.86 s and 1.28 s. |
| `PunchIn` `min_gap` | expected by design | The gap is one beat (0.43 s) from start to start, and a move that rewrites its shot (an alternation, from its first line to the shot's end) keeps every other move's start out of that range. A PunchIn on the shot of an alternation, or two agent punches within a beat, are dropped; the auto fill takes only free slots and is silent. |

Fixes: the planner view gives every source shot a `length` word (`short` under a bar, `medium` a bar up to three, `long` three or more, from the shot's extent and the music's beat) and says in `notes` what the words mean and that OnDrop works only on the shot that holds the drop; the Fast editor and planner nudge lines (`StyleNudgeRegistry`, so the base prompts and their two copies are unchanged) state the same rules in words (Bar needs a long shot, Beat or HalfBeat a medium one, OnDrop belongs on that one shot only, a PunchIn on an alternating shot is dropped, one move per shot). No number, time or duration is asked of the model. The compile itself is unchanged: these are the rules it enforces, now told to the agent that has to obey them. QA-DYN-45: when the story editor's cut is in the run, the view lays it on the program the way the compile will (one span per keep entry, the beat sync's own planner for the trim, the music from time 0): a shot's `length` word is its KEPT length, an unkept shot has none, and the one shot the drop falls in carries `"drop": "holds the drop"`; with no cut it falls back to the offered shot's extent and the generic sentence. Padding and a target-length fit are not modelled. The story editor's own view has no length word, so the nudge says it is the planner's view that gives it.

## Open

- **The story editor cannot see generated clip ids (known design gap; the story editor itself stays as is).** In the b-roll template the story editor step runs BEFORE generation, so its view holds only the source's shots (`s0..`); the generated clips exist only at compile time. A `BeatAlternate` of the static source against a generated night shot therefore needs an agent that sees those ids (the follow-up's `DynamicsPlanner`, see its section). Options for the owner: (a) reorder the template so generation runs before the story editor (the editor then sees the clips as offered sources, at the cost of generating before the cut is known); (b) keep the order and hand the compile-time pool ids (the generated clips' ids as the compile knows them) to a later agent, for example a dynamics-only pass after generation that emits only the `dynamics` list. The follow-up chose (b) (the `DynamicsPlanner`, see its section); the story editor itself still pairs only ids of the source video.
- Real-model proof (QA-DYN-8): the proof words so far were hand-written. The prompt, input and schema the story editor really gets are dumped by `DynamicsProofTests` (`DYN_PROOF_DIR/llm/`) so a different model can choose the words; the film from them is flagged as a different model's choice, not the product's configured provider.


Cap order (QA-DYN-66): when the segment pass ran, the cold open and the end card it resolved hold their `MaxGeneratedClips` slots before any cutaway takes one (segments first, then cutaways in manifest order), so the total never exceeds the cap whatever the manifest order; a surplus clip is dropped with `max_generated_clips_exceeded`. Without the segment pass the order is the manifest's, as before.
