---
title: Step types
sidebar_position: 2
description: What each kind of workflow step does, whether it uses AI, its important settings with defaults, and how steps refer to each other.
---

# Step types

A workflow is a numbered list of **steps**. Each step has a **step type** that decides what kind of
work it does. Some step types run an AI agent; others are plain, predictable processing that gives
the same result every time for the same input (this guide calls those **non-AI** steps).

There are eighteen step types in this table. Their exact names (used by the assistant and in saved workflows)
and the labels shown in the workflow builder are:

| Step type | Builder label | Uses AI? | In one sentence |
|---|---|---|---|
| `Agent` | Agent | Yes | Runs one AI agent. |
| `ReviewLoop` | Review Loop | Yes | Scores earlier work and sends it back for another try if the score is too low. |
| `Conditional` | Conditional Branch | No (optional AI mode) | Jumps to one step or another depending on a condition. |
| `ForEach` | For Each | Yes | Runs one agent once per item of a list. |
| `Parallel` | Parallel | Yes | Runs several agents at the same time. |
| `Extract` | Extract | No | Trims or reshapes an earlier step's result so later steps read less. |
| `VideoAnalyze` | Analyze Video | No (optional speech and vision AI) | Studies a video: shots, pauses, speech, colour, green screens. |
| `VideoCompile` | Compile Video | No | Cuts the video and adds graphics, inserts, music, effects, grade, voiceover. |
| `EditRoom` | Edit Room | Yes | A panel of AI editors decides what to keep. |
| `GraphicsRoom` | Graphics Room | Yes | A panel of AI artists plans graphics and screen inserts. |
| `ColorGradeRoom` | Color Grade Room | Yes | A panel of AI colourists picks one colour look. |
| `DailiesRoom` | Dailies Room | Yes | A panel of AI judges picks one AI-generated take per planned shot. |
| `VideoGenerate` | Generate Video | No (calls a paid video service) | Pays a video-generation service to make new clips. |
| `Voiceover` | Voiceover | No (calls a speech service) | Turns written lines into spoken narration audio. |
| `ThumbnailGenerate` | Thumbnail | No | Picks one frame of your video as a cover image, optionally with a caption burned in. |
| `TimelineRender` | Render Timeline | No | Turns one of the project's editor timelines into a finished video file. |
| `ExtractFrame` | Grab a frame | No | Saves one frame of a video as an image. |
| `AudioGenerate` | Generate music or sound | No (calls a paid audio service) | Makes music or a sound effect from a written description. |
| `PinInsert` | Put on screens | No | Puts a video or image on the green, blue or magenta screens an Analyze Video step tracked. |

**There is no "render Remotion" step type.** Remotion animations (the code-based animated videos
ReelBolt produces) are written and rendered by certain AI agents **inside `Agent` steps** (and
inside the `GraphicsRoom` step's final director call). The `AuthorAgent` renders a whole promo
video; the `MotionGraphicsPlanner` and `MotionGraphicsDirector` render small graphics and the
scenes shown inside tracked screen inserts. See [Agents](agents.md).

## Editing steps in the workflow builder

On a computer the workflow page shows the steps as cards on a canvas, one under the other with
arrows between them. Each card says what the step does in plain words ("Watches your footage",
"Picks the best clips", "Writes the narration") and a few chips summarising its main choices (how
many clips, the output shape, whether music or narration is on). A card never grows into a form:

- **Click a card, or its Settings button,** to open that step's settings in a panel on the right.
  The everyday choices come first; technical settings (codecs, limits, thresholds, decision-model
  options, the exact data format a step hands on) sit under **Advanced**, closed by default. It
  opens by itself when the assistant proposes a change there or a save was rejected because of it.
- **Scroll with the mouse wheel or trackpad to move around the canvas.** To zoom, hold Ctrl (Cmd on
  a Mac) while scrolling, pinch on a trackpad, or use the + and − buttons in the corner. The canvas
  fills the window, and the whole workflow is framed when it opens, so the last steps of a long
  workflow are easy to reach. **Auto Layout** puts dragged cards back in a neat column.
- A step card warns you about a missing choice, for example a music step in a project that has no
  audio files, or a footage step with no video chosen.

On a phone the steps are a simple list instead, with the same settings.

## Settings every step has

Every step, whatever its type, has these settings (the names in backticks are how the assistant
and saved workflows spell them):

- `stepOrder` — the step's number, starting at 1. Other steps refer to it by this number.
- `label` — a human-readable name shown in the builder and on run pages.
- `stepType` — one of the fifteen types above. Defaults to `Agent`.
- An **agent**. `Agent`, `ReviewLoop` and `ForEach` steps run the agent you pick. Non-AI steps and
  the three room steps still need an agent slot filled, and use a built-in placeholder for it:
  `ExtractTransform` for `Extract`, and `VideoTransform` for `VideoAnalyze`, `VideoCompile`,
  `VideoGenerate`, `Voiceover`, `ThumbnailGenerate`, `TimelineRender`, `EditRoom`, `GraphicsRoom`
  and `ColorGradeRoom`. The placeholder is
  never run as an AI; a room's real AI members come from the room's own settings.
- `cacheMode` — whether a later run may reuse this step's earlier result instead of redoing the
  work: `Default` (ReelBolt decides), `Always`, or `Never`. See
  [Execution and outputs](execution-and-outputs.md).
- A **settings block** for the types that need one, stored as JSON text in a field named after the
  type: `videoAnalyzeConfigJson`, `videoCompileConfigJson`, `extractConfigJson`,
  `editRoomConfigJson`, `graphicsRoomConfigJson`, `colorGradeRoomConfigJson`,
  `videoGenerateConfigJson`, `voiceoverConfigJson`, `thumbnailGenerateConfigJson`,
  `conditionalConfigJson`, `reviewLoopConfigJson`.

Inside every settings block, names are written in camelCase (`detectInsertRegions`, not
`DetectInsertRegions`) and choices are written as words in quotes (`"mode": "Reencode"`). Anything
you leave out takes its default value.

## How steps refer to each other

ReelBolt steps pass work forward. Most settings blocks point at an earlier step in one of these
shapes.

**Pointing at an earlier step's result** (used by `VideoCompile`, the rooms, `VideoGenerate` and
`Extract`):

```json
{"from": "Previous"}
```

means "the step that ran just before this one", and

```json
{"from": "Step", "stepOrder": 2}
```

means "step number 2". Prefer the numbered form whenever there is more than one step in between.
The footage templates always use it, because "Previous" from a compile step would usually mean the
graphics, music or effects step, not the editing decision. (`Extract` steps additionally accept
`{"from": "Accumulated"}` and `{"from": "ProjectFiles"}`.)

**Pointing at a video** (used by `VideoAnalyze`'s `source`):

```json
{"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"}
```

uses a video you uploaded;

```json
{"kind": "StepOutput", "stepOrder": 3}
```

uses the video produced by step 3 (for example a render or a generated clip); and

```json
{"kind": "PreviousStepOutput"}
```

uses the most recent video any earlier step in this run produced.

```json
{"kind": "GeneratedClips", "stepOrder": 4}
```

analyses **every clip** a `VideoGenerate` step (here step 4) delivered, as separate sources, each
tagged with its clip id (`v1t1`, `v1t2`, ...). This is how a `DailiesRoom` step gets something to
choose between.

**What an AI agent sees.** An `Agent` step's `agentInputContextMode` decides which earlier results
the agent reads:

- `FullWorkflow` — all earlier steps' results (older ones may be shortened to keep the prompt a
  sensible size).
- `PreviousStepOnly` — only the step just before.
- `SelectedPriorSteps` — only the steps you list (for example steps 1, 4 and 5).
- `CustomMappedSubset` — a custom mapping you write.

An agent that needs two earlier results (for example a graphics planner that needs both the video
analysis and the editing decision) must use `FullWorkflow` or `SelectedPriorSteps`.

**Your brief.** The text you type in **Your brief** when you click **Run** is passed to every AI agent
as extra instructions. It is optional unless the workflow has **Require a brief before each run** on.

## Agent step (`Agent`)

An `Agent` step runs one built-in AI agent (such as `VideoStoryEditor` or `AuthorAgent`). The
agent reads earlier results (see `agentInputContextMode` above), may use its tools (reading project
files, writing code, rendering), and returns its answer. Built-in agents return a fixed answer
shape that later steps rely on.

A custom agent can be put in an `Agent` step, but the step is marked **Skipped**
when the run reaches it, because custom agents cannot run inside workflows (see
[Agents](agents.md#custom-agents)).

- **Uses AI:** yes.
- **Retries:** if the agent fails or returns an answer of the wrong shape, ReelBolt tries again,
  telling the agent what went wrong, up to 3 attempts by default. An agent that runs out of time is
  not retried.
- **Reuse on later runs:** agents that only read (analysis agents, `VideoStoryEditor`,
  `MusicSupervisor`, `SoundDesigner`, `ShotDirector`, `Colorist`, `VideoReviewAgent`) may be reused
  from an identical earlier run unless the step is set to `"cacheMode": "Never"` (the templates set
  every creative step to `Never`). Agents that write code, write files or render
  (`RemotionComponentTranslator`, `AuthorAgent`, `MotionGraphicsPlanner`) are never reused.

Minimal step (as the assistant would write it):

```json
{"stepOrder": 2, "label": "Decide which spans to keep", "stepType": "Agent",
 "agentDefinitionId": "ID-OF-THE-VideoStoryEditor-AGENT",
 "agentInputContextMode": "PreviousStepOnly", "cacheMode": "Never"}
```

## Review loop step (`ReviewLoop`)

A `ReviewLoop` step runs a reviewing agent (usually `ReviewAgent` for promos or `VideoReviewAgent`
for edited footage), reads its score out of 10, and decides whether to go back and redo work.

- `minScore` — the passing score. Below it, the run jumps back. Default 9 when not set (the
  templates use 8 or 9).
- `loopTargetStepOrder` — the step to jump back to (for footage templates, the editing decision,
  step 2; for promos, the `AuthorAgent` step).
- `maxIterations` — how many review rounds at most (templates use 2 or 3). When the last round
  still scores too low, the run **continues and finishes** with what it has; it does not fail.

When the run jumps back, the reviewer's comments are handed to the steps being redone, so they can
fix what was criticised.

An optional `reviewLoopConfigJson` (`{"enabled": true, ...}`) lets a fast "decision model" skip the
reviewer when it is very confident; it is off by default and needs an admin-configured decision
provider.

**Visual check.** Under **Advanced: visual check**, switch on **Check the picture before each
review** and the loop looks at frames of the video it is reviewing before each round. It flags text
that is cut off, runs past its box, sits too close to the edge or is too small to read, and hands
the reviewer a list such as "Frame 3 at 12.4 s: text overflows its box". If any problem is marked
serious, the round's score is held down to **Highest score when a serious problem is found**
(default 6), so the loop goes back and the steps being redone receive the same frame-and-time
complaint. The other settings are **Frames to look at** (2 to 16, default 8), **Safe margin (% of
the frame)** (0 to 20, default 5), **Smallest readable text (% of the frame height)** (0.5 to 20,
default 2.5) and **Vision model** (blank uses your default). The check is off by default, and when
it is off nothing changes. It needs a vision model set up for your account; without one, or if the
check fails for any reason, the review goes ahead without it. Each round adds one small vision
call, and the findings are advice to the reviewer, not a verdict.

**The reviewer can fix things itself.** With `"allowAdjustments": true` in `reviewLoopConfigJson`,
the reviewer may change a short list of settings of earlier steps for this run only (the size of
titles, how long sound effects play, the music level, the target length within a quarter of your
brief, how much the footage analysis keeps, and similar) and ask for the same changes you can ask
for on a finished run: speak a narration line again with new words, another speed or a corrected
pronunciation, make a generated shot, take or music take again, change a thumbnail's text, or
rewrite one scene of the script. Your saved workflow never changes. The run goes back to the
earliest changed step and redoes what depends on it, inside the same run; every step it redoes is
charged like any other step (a narration line or a generated shot costs what it costs to make).
The reviewer never edits your prompts, never picks another model or provider, never raises a
spending limit or the number of takes, and never lets your footage be sent to an outside service;
a voice it names must be one you are allowed to use. Every change, applied or refused, is listed on
the run page with the reason and what the redo cost. If your credits run out during a redo, the
review stops there and the run finishes with its best version, marked **Needs attention**. The
cost estimate includes the most these redos can add in its "up to" figure.

Every template ReelBolt ships turns this on, together with the visual check, the timeline and sound
facts the platform measures on the film, and closer looks at frames the reviewer asks for.

- **Uses AI:** yes. **Reuse on later runs:** never.

## Conditional step (`Conditional`)

A `Conditional` step looks at the previous step's result and jumps to one of two steps.

- `conditionExpression` — a test written against the previous result's fields, for example
  `$.overallScore >= 8` after a `ReviewAgent` step. Field names must match the earlier result
  exactly as it appears on the run page. You can combine tests with `&&` and `||`.
- `trueBranchStepOrder` / `falseBranchStepOrder` — the step number to go to in each case. If the
  branch is not set, the run simply continues with the next step.
- An expression that cannot be worked out counts as **false**.

An optional `conditionalConfigJson` with `"mode": "Decision"` asks a decision model a yes/no
question instead; the default is `"mode": "Expression"`.

- **Uses AI:** no (unless Decision mode). **Reuse on later runs:** never.

## For-each step (`ForEach`)

A `ForEach` step takes a list from the previous step's result and runs the chosen agent once for
each item, several at a time.

- `loopSourceExpression` — where the list is, for example `$.components` (or `$` when the whole
  result is a list).
- `maxIterations` — the most items to process; 0 means all.

The step's result is `{"results": [...], "sourceCount": N}`. If the list is empty the step
completes without running the agent.

- **Uses AI:** yes. **Reuse on later runs:** never.

## Parallel step (`Parallel`)

A `Parallel` step runs several agents at the same time on the same input and collects all their
answers into one list, each tagged with the agent's name. List the agents in `parallelAgentIdsJson`
(a JSON list of agent ids). The five code-analysis agents are good candidates, since they do not
depend on each other.

A `Conditional` step after a `Parallel` step can test one agent's answer with
`$.AgentName.field`, for example `$.Review.overallScore > 8`.

- **Uses AI:** yes. **Reuse on later runs:** never.

## Extract step (`Extract`)

An `Extract` step is a non-AI step that **shrinks** an earlier result so later AI steps read less
(and cost less). It can pick out a list and keep only certain fields (`"operation": "Project"`),
look up chosen ids in an earlier list (`"operation": "Resolve"`), or list project files
(`"operation": "Files"`).

Example from the `lean-context-promo` template (keep at most 40 components, three fields each):

```json
{"version": 1, "operation": "Project",
 "inputs": {"source": {"from": "Step", "stepOrder": 3}},
 "path": "$.components", "fields": ["name", "filePath", "responsibility"],
 "take": 40, "maxOutputChars": 8000, "expect": {"minItems": 1}}
```

- **Uses AI:** no. **Retries:** none (it would fail the same way again). **Reuse on later runs:**
  yes.

## Analyze video step (`VideoAnalyze`)

A `VideoAnalyze` step studies a video and produces a compact description that AI agents can read,
plus a full detailed record that the compile step uses later. It finds:

- **shots** (camera cuts), given ids `s0`, `s1`, ...;
- **pauses / silences**, ids `g0`, `g1`, ...;
- **speech** as text, ids `t0`, `t1`, ... (needs a transcription provider);
- per-shot **look and sound** facts (movement, brightness, colour, loudness, repeated takes);
- optionally, good **places for graphics** (ids `p0`, ...), **music choices** (`m0`, ...),
  **sound-effect choices** (`x0`, ...) and tracked **green-screen regions** (`r0`, ...).

It never cuts anything. It is non-AI, except for the optional speech-to-text and the optional
picture descriptions, which call providers an admin configured.

Important settings (defaults in brackets):

| Setting | Default | What it does |
|---|---|---|
| `source` | required | Which video to analyse (see "Pointing at a video" above). |
| `sources` | none | A list of several videos (takes, angles) analysed together into one set of ids. When set, `source` is ignored. |
| `transcription` | `"Optional"` | Speech-to-text: `"Off"`, `"Optional"` (use it if a provider exists), `"Required"` (fail without it). |
| `language` | auto | Language hint for speech-to-text, for example `"en"`. |
| `vision` | `"Off"` | AI descriptions of what is in each shot: `"Off"`, `"Optional"`, `"Required"`. Costs one AI call per described shot, up to `maxCaptionedShots` (50). |
| `locateSubjects` | `false` | Finds where each shot's main subject is, so a vertical or square compile with `outputFit: "Subject"` keeps it in the frame. Uses the image AI (two calls per shot, up to `maxSubjectShots`, 40). `subjectLabel` names what to look for, e.g. `"the phone"`; blank finds the main subject. Shots with a tracked green screen don't need it. |
| `emitOverlayPlacements` | `false` | Find good spots for titles and lower-thirds (`p` ids). Needed for graphics. |
| `offerMusicTracks` | `false` | Offer every uploaded audio file as a music choice (`m` ids). |
| `offerSfxClips` | `false` | Offer every uploaded audio file as a sound-effect choice (`x` ids). |
| `detectInsertRegions` | `false` | Find and follow coloured screens (green screen) in the footage (`r` ids). Needed for tracked screen inserts. |
| `insertRegionColor` | `"green"` | Screen colour to look for: `"green"`, `"blue"` or `"magenta"`. |
| `maxInsertPlatesPerFrame` | `1` | How many coloured screens can be followed at the same time (1 to 16). Raise it for two phones in one shot. |
| `measureInsertCurvature` | `true` | Measure corners and curvature of each screen more precisely. |
| `solveInsertChromaKey` | `true` | Work out the exact screen colour so the insert can be cut to the screen's true shape (rounded corners, notch, a hand in front). |
| `subShotMode` | `"Off"` | Cut points inside long shots: `"Off"` (none), `"Even"` (at equal intervals) or `"Content"` (where the picture pauses or changes). The editor can keep only part of a cut shot, or ignore the cut points. |
| `silenceThresholdDb` / `minSilenceMs` | `-34` / `350` | How quiet and how long a pause must be to count. |
| `sceneThreshold` | `0.30` | How different two frames must be to count as a cut. |
| `maxOutputChars` | `24000` | Size limit of the description AI agents read. |
| `maxDurationSeconds` | platform limit (1 hour) | Videos longer than this are refused before anything is charged. You can only set it lower. |
| `derushPrePass` | `"Off"` | Optional decision-model hint per shot; `"Fast"` behaves exactly like `"Prior"`. |

Minimal example (the template default; you must add the file id):

```json
{"version": 1, "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"}}
```

**Photos work as clips.** A PNG, JPEG, WebP, BMP, TIFF or AVIF image picked as a source is turned
into a short moving shot: a slow push in, a pull back out and a pan, each in two short beats. The
editor picks which move fits and whether it holds for one beat or two, so photos from a website or
an Instagram account can be cut into a reel alongside video.

**Choosing clips in the builder.** In the step's settings, under **Your footage**, a first step
offers your uploaded videos directly (there is no earlier step to take a video from). To cut
several clips into one video, switch on **Use several clips** and pick them all at once in
**Choose clips…**; they are used in the order listed, which you can adjust row by row, and
**Add another clip** adds a row for one more uploaded file. Switching on **Offer your music tracks**
or **Offer your sound-effect clips** in a project with no audio files shows a reminder to upload
some first.

If the description grows past `maxOutputChars`, ReelBolt first makes each shot's details shorter,
then leaves out whole optional lists (music choices, effect choices, green-screen regions), and only
then leaves out shots. A left-out list cannot be used by later steps in that run, so for
green-screen work check that the run summary shows the regions were offered (see the
[tracked screen insert recipe](../recipes/tracked-screen-insert.md)).

- **Retries:** none (re-running gives the same result; speech-to-text has its own small retry).
- **Reuse on later runs:** yes, an identical analysis is reused.
- **Common errors:** `SOURCE_UNRESOLVED` (no file selected, or the step you pointed at made no
  video), `SOURCE_TOO_LONG`, `SOURCE_TOO_LARGE` (over the 30 GB platform limit), `SCRATCH_BUSY` (machines busy, nothing charged, run again), `SOURCE_TOO_DEMANDING` (a very large clip could not be analysed in the time allowed; nothing charged; try a shorter clip or a smaller export, for example 1080p), `TRANSCRIPTION_UNAVAILABLE` or
  `TRANSCRIPTION_FAILED` (only with `"Required"`), `VISION_UNAVAILABLE` / `VISION_FAILED` (only with
  `"Required"`).

## Compile video step (`VideoCompile`)

A `VideoCompile` step produces the finished edited video file. It takes the editing decision (which
ids to keep), looks up their exact times in the analysis, cuts the video frame-accurately and, in
the same pass, adds whatever extras you switched on. It is non-AI: every number comes from
ReelBolt, never from an AI.

It always needs:

- `decision` — the step whose answer says what to keep: a `VideoStoryEditor` agent step or an
  `EditRoom` step, for example `{"from": "Step", "stepOrder": 2}`.
- `analysisStepOrder` — the number of the `VideoAnalyze` step, for example `1`.

Main settings (defaults in brackets):

| Setting | Default | What it does |
|---|---|---|
| `mode` | `"Reencode"` | `"Reencode"` cuts exactly and allows every extra. `"StreamCopy"` copies a single continuous cut losslessly and fast, cutting only on keyframes; a cut with more than one part is re-encoded. It needs `"allowKeyframeSnapping": true`, and allows **no** extras. |
| `outputFileName` | `"edited.mp4"` | Name of the result. Left at `"edited.mp4"`, the file added to your project is named after the workflow and the run instead, for example `Spring teaser - run 3.mp4`. |
| `outputFormat` | `"Source"` | Shape of the video: `"Source"` (the size of your largest clip), `"Vertical"` (1080x1920, for Reels, Shorts and TikTok), `"Square"` (1080x1080), `"Landscape"` (1920x1080) or `"Portrait"` (1080x1350). Needs `"Reencode"`. These sizes are for a run at Auto or 1080p: a run started at 720p makes them 720x1280, 720x720, 1280x720 and 720x900, and one started at 4K makes them 2160x3840, 2160x2160, 3840x2160 and 2160x2700. Every size stays within your plan's limit (see [Export resolution on each ReelBolt plan](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan)). |
| `outputFit` | `"Crop"` | When a clip has a different shape from `outputFormat`: `"Crop"` fills the frame and trims the sides (a wide shot keeps its middle), `"Subject"` fills the frame but keeps the subject in shot (a tracked screen, or the subject the analysis step found with `locateSubjects`) — a clip whose subject is too wide for any crop, like an end card's headline, is shown whole instead, `"Pad"` shows the whole shot with bars, filled per `canvasFill` (`"Black"` or `"Blur"`). |
| `targetDurationSec` | `0` | The length you want, in seconds (`0` = no target). A longer cut is shortened to it, trimming the ends of the longest shots and never the last one or the narration. ReelBolt does not invent footage; if the video comes out more than 20% shorter or longer, the run tells you. |
| `loudnessTarget` | `"Off"` | How loud the finished video is: `"Social"` matches Instagram, TikTok and YouTube, `"Web"` websites, `"Broadcast"` TV. The balance between voice and music stays the same. Under **Loudness** in the builder. |
| `registerProjectFile` | `true` | Also add the result to the project's files, so it can be edited again. |
| `rateControl` + `videoBitrateKbps` | off / none | Set the video bitrate: `"Capped"` (stays close to or under it) or `"Average"` (aims for it), 200–100000 kbps. `"Quality"` takes no bitrate. Left out, the encode is the plain quality one. |
| `minSegmentMs` | `250` | Drop kept pieces shorter than this. |
| `transitionPolicy` | `"Off"` | Joins between cuts: `"Off"` (plain cuts), `"AudioOnly"`, `"Auto"`, `"Expressive"` (visible dissolves and dips chosen automatically), or `"Editor"`: the editing AI picks each join from 56 transitions (slides, wipes, zooms, flashes, dips, iris and more) and how fast it plays. |
| `programFadeInMs` / `programFadeOutMs` | `0` | Fade the picture in at the start / out at the end. |
| `enableGraphics` + `graphicsPlan` | off / none | Add titles and lower-thirds from a `MotionGraphicsPlanner` or `GraphicsRoom` step. |
| `enableInserts` (+ `graphicsPlan`) | off | Put rendered scenes into tracked green screens. Reads the **same** `graphicsPlan`. |
| `enableMusic` + `musicPlan` or `musicTrackProjectFileId` | off | Add a music bed chosen by `MusicSupervisor`, or a specific file you name. |
| `enableSfx` + `sfxPlan` | off | Add sound effects planned by `SoundDesigner`. |
| `enableColorGrade` + `colorGradePlan` | off | Apply a colour look from `Colorist` or a `ColorGradeRoom` step. |
| `enableGeneratedClips` + `generatedClips` | off | Place AI-generated clips from a `VideoGenerate` step. A clip planned as `ScreenContent` is put onto a tracked green screen instead, which also needs `enableInserts`. |
| `reframes` | none | Zoom and pan over a stretch of a clip, easing between keyframes (for example a tight shot on the keyboard that eases out to the whole screen). Each reframe names a clip and a start and end time; each keyframe gives a time, a zoom (1 = whole frame, up to 8), a centre point and an easing. Up to 32 keyframes per reframe. Outside the stretch the clip is shown whole. Entered as JSON under **Advanced** (**Reframes (JSON)**); the step checks it when you save and says what to fix. |
| `sourceFits` | none | Choose a different fit for one clip than the rest (**Clip number** and **Fit**, under **Advanced**). |
| `subjectSafeArea` | `0.05` | With **Fill the frame around the subject**: a clip that carries on-screen text is shown whole with bars instead of cropped, and text is kept this far inside the edge (0 to 0.25; 0 switches it off). |
| `insertContentRef` | none | Puts an earlier step's video or image, or an earlier run's output, into a tracked screen, instead of a file you uploaded (**Insert content by reference**). |
| `musicGainDb` | none | The exact level of the music, from -40 to +6 dB. `0` plays your track at its own full volume; left empty, the music sits quietly under the picture. Speech still lowers it when ducking is on. |
| `generatedClipAudio` | `Auto` | What happens to the sound that comes with a generated clip: `Auto` (it plays, like speech), `Keep` (it plays as background and the music is not lowered under it) or `Mute`. The result says how each clip's sound was treated. |
| `takeSelection` | none | When the generation step made several takes per shot, place only the take a `DailiesRoom` step chose (otherwise each shot's first delivered take). |
| `enableVoiceover` + `voiceoverStepOrder` | off / none | Mix in narration from a `Voiceover` step (a step number, not a `from` reference). |
| `voiceoverMode` | `OverMusic` | What happens to the original speech under each narration line: leave it (`OverMusic`), turn it down (`DuckDialogue`, by `voiceoverDuckDb`, default -12) or remove it (`ReplaceDialogue`, for dubs). |
| `enableCaptions` + `captionStyle` | off / `Subtitle` | Burn the narration into the picture as it is spoken: `Subtitle` (a few words at a time), `Karaoke` (spoken words highlighted), `Punch` (one word at a time) or `Bold` (big words two or three at a time, the Reels/TikTok look). Karaoke and Punch need a transcription provider for word timing. |
| `enablePickups` + `pickupVoiceoverStepOrder` | off / none | Replace flubbed spoken phrases with re-recorded lines from a `Voiceover` step whose source is `PickupPlan`. The picture is not changed (no lip sync). |
| `expect.minRetainedRatio` | `0.15` | Refuse an edit that keeps less than 15% of the footage. |

**In the builder** these appear under **Format and length** near the top of the step's settings:
**Shape** (**Original size**, **Vertical 9:16 — Reels/TikTok/Shorts**, **Square 1:1 — feed**,
**Landscape 16:9 — YouTube**), **When a clip has a different shape** (**Fill the frame (crop the
middle)**, **Fill the frame, keep the subject in shot** or **Fit whole clip (bars)**, shown only for a
fixed shape) and **Target length (seconds)**. The extras
read **Add background music**, **Add sound effects**, **Add titles and graphics** and so on; switching
music or sound effects on in a project without audio files shows a reminder to upload some. Codec,
preset, quality (CRF), cut padding, transition timing and the before-encoding checks are under
**Advanced**.

Every "enable" switch defaults to off, and when off the result is exactly what it would be without
that feature. Every extra **requires `"mode": "Reencode"`**. When an extra cannot be applied (a
missing plan, an unknown id, a bad file), it is skipped and the reason is written in the step's
result; the edit itself still completes.

When the finished video is not quite what you asked for, the step's result lists **warnings** in
plain words, and the run page shows them: some or most of the narration could not be placed, music
or sound effects are switched on but the project has no audio files to choose from (upload some to
the project's files), or the video came out much shorter or longer than `targetDurationSec`. If
your clips have no sound of their own, narration, music and sound effects are still heard: ReelBolt
adds a silent sound track for them to play on.

Minimal example (from `video-derush-edit`):

```json
{"version": 1, "decision": {"from": "Previous"}, "analysisStepOrder": 1}
```

- **Retries:** none. **Reuse on later runs:** yes, when everything it reads is identical.
- **Common errors:** `DECISION_UNRESOLVED` (the decision step is wrong or missing),
  `ANALYSIS_NOT_FOUND` (wrong `analysisStepOrder`), `EMPTY_KEEP` (the editor kept nothing),
  `UNKNOWN_ID`, `EXPECT_FAILED` (too little kept), `GRAPHICS_REQUIRE_REENCODE`,
  `INSERTS_REQUIRE_REENCODE`, `MUSIC_REQUIRES_REENCODE`, `SFX_REQUIRES_REENCODE`,
  `COLOR_GRADE_REQUIRES_REENCODE`, `GENERATED_CLIPS_REQUIRE_REENCODE`,
  `TRANSITIONS_REQUIRE_REENCODE`, `OUTPUT_FORMAT_REQUIRES_REENCODE` (an extra or a fixed
  `outputFormat` was switched on with `"StreamCopy"`), `CODEC_NOT_ALLOWED`,
  `ENCODE_FAILED`. `reframes` also fails with `REFRAME_REQUIRES_REENCODE` under `"StreamCopy"`.

## Edit room step (`EditRoom`)

An `EditRoom` step replaces a single `VideoStoryEditor` with a **panel of AI editors**. By default
three seats — `PacingEditor`, `StoryEditor` and `CraftEditor` — take turns discussing the analysed
footage, a `VideoEditDirector` moderates, and the director then writes one final "what to keep"
decision in exactly the same shape a single editor would, so the compile step treats it the same.

| Setting | Default | What it does |
|---|---|---|
| `view` | none | The analysis to discuss, for example `{"from": "Previous"}` or `{"from": "Step", "stepOrder": 1}`. |
| `seats` | the three above | Your own seats: a list of `{"name": ..., "persona": ...}`. |
| `rounds` | `2` | Full passes round the table before the director speaks. |
| `maxTurns` | `8` | Hard turn limit (2 to 20). |
| `fallbackToSoloEditor` | `true` | If the panel fails, let one `VideoStoryEditor` decide instead. |
| `persistTranscript` | `true` | Keep the discussion so you can read it (for information only). |

```json
{"version": 1, "view": {"from": "Previous"}}
```

- **Uses AI:** yes, many calls. **Retries:** not retried as a whole (the director's final write-up
  has its own retries). **Reuse on later runs:** possible by default; the template sets `Never`.

## Graphics room step (`GraphicsRoom`)

A `GraphicsRoom` step replaces a single `MotionGraphicsPlanner` with a **panel of AI artists**. By
default four seats — `LayoutArtist`, `TimingArtist`, `CopyArtist` and `InsertArtist` — discuss the
offered graphics spots and green-screen regions; a `MotionGraphicsDirector` moderates and then, in
one final call, writes the graphics plan **and renders the graphics and insert scenes with
Remotion**. During the discussion itself nobody can render; only the director's final call can.
Its plan has the same shape as the single planner's, so the compile step's `graphicsPlan` can point
at it.

Settings are the same as the edit room (`view`, `seats`, `rounds`, `maxTurns` default `10`, ...),
with `fallbackToSoloPlanner` (default `true`) instead of `fallbackToSoloEditor`. Point `view` at the
**analysis step**, not at the editing decision:

```json
{"version": 1, "view": {"from": "Step", "stepOrder": 1}}
```

An empty plan (no graphics) is a valid result. **Reuse on later runs:** never (it may render).
**Retries:** not retried as a whole.

## Color grade room step (`ColorGradeRoom`)

A `ColorGradeRoom` step has a **panel of AI colourists** — `ToneArtist`, `PaletteArtist` and
`ContinuityArtist` by default — discuss the measured colour of each shot; a `ColorGradeDirector`
then picks one look for the whole video as words only: `look` (`None`, `Warm`, `Cool`, `Filmic`,
`Vibrant`, `Muted`, `Mono`), `strength` (`Subtle`, `Normal`, `Strong`), `shadowTone` and
`highlightTone`. ReelBolt turns the words into the actual colour change at compile time. A single
`Colorist` agent step produces the same shape and can be used instead.

```json
{"version": 1, "view": {"from": "Step", "stepOrder": 1}}
```

`fallbackToSoloColorist` defaults to `true`. **Retries:** up to the normal agent retry count.
**Reuse on later runs:** possible by default; the template sets `Never`.

## Dailies room step (`DailiesRoom`)

A `DailiesRoom` step is for workflows where the generation step made **several versions ("takes")
of each AI-generated clip** (`"takes": 2` or more). A panel of AI judges — `ContinuityJudge`,
`MotionJudge` and `ImageJudge` by default — compare the takes of each planned shot using the facts
a `VideoAnalyze` step measured about them; a `DailiesDirector` then writes the final choice: at most
one take per shot, named only by its id (`v1t2`). It never writes a score or a number.

Point `view` at a `VideoAnalyze` step whose source is the generation step's clips:

```json
{"version": 1, "view": {"from": "Step", "stepOrder": 5}}
```

where step 5 is `{"version": 1, "source": {"kind": "GeneratedClips", "stepOrder": 4}, "transcription": "Off"}`.
Then give the `VideoCompile` step `"takeSelection": {"from": "Step", "stepOrder": 6}` so it places
only the chosen takes. Leaving a shot unselected is a valid answer: that shot keeps its first
delivered take. Other settings are the edit room's (`seats`, `rounds`, `maxTurns`,
`persistTranscript`), with `fallbackToSoloSelector` (default `true`).

- **Uses AI:** yes, many calls. **Reuse on later runs:** possible by default; the template sets `Never`.

## Generate video step (`VideoGenerate`)

A `VideoGenerate` step **pays a video-generation service** (MiniMax or Higgsfield, configured by an
admin) — or uses a **ComfyUI server you run yourself**, which costs no money but GPU time — to
create new video clips. It is non-AI in the sense that it does not think; it sends prompts and
collects clips. It works in two ways:

- `"planSource": "Inline"` (default) — you write one prompt in `inlinePrompt` (up to 7000
  characters) and it makes `takes` clips from it. The first clip becomes the step's output video,
  so a later `VideoAnalyze` step can use it with `{"kind": "PreviousStepOutput"}`.
- `"planSource": "StepRef"` — it makes one clip per shot planned by a `ShotDirector` step
  (`plan`), for the compile step to place (`generatedClips`).

| Setting | Default | What it does |
|---|---|---|
| `maxSpendCredits` | `0` (must be set) | Hard spending cap for this step, in credits. Must be above 0 for a paid service. |
| `maxGpuMinutes` | `0` | Hard cap on GPU time for a self-hosted ComfyUI provider, which bills nothing. Must be above 0 for ComfyUI; ignored by paid services. |
| `model` | `"MiniMax-H3"` | Or `"MiniMax-H3-Max"`; for ComfyUI exactly `"wan2.2-ti2v-5b-t2v"` or `"wan2.2-ti2v-5b-i2v"`. |
| `resolution` | `"768P"` | `MiniMax-H3`: `768P` or `2K`. `MiniMax-H3-Max`: `480P` or `768P`. |
| `durationSeconds` | `6` | Clip length for inline prompts. |
| `ratio` | `"16:9"` | One of `21:9`, `16:9`, `4:3`, `1:1`, `3:4`, `9:16`. |
| `takes` / `maxClips` | `1` / `1` | Clips per prompt or shot / most clips in total (both 1 to 4). |
| `allowSourceMediaEgress` | `false` | Permission to send frames of your own footage to the service. A shot plan that names a `firstFrame`/`lastFrame` is refused (`EGRESS_REFUSED`) without it; with it, ReelBolt cuts that one frame out of the shot and sends it with the clip's request. |
| `loopBackBehavior` | `"Reuse"` | On review-loop retries, reuse clips (`"Reuse"`) or pay for new ones (`"Regenerate"`). |
| `lookMatching` | `false` | Plan mode only: tell the service to match the colour and contrast ReelBolt measured in the real shot each clip sits next to. |
| `cameraContinuity` | `false` | Plan mode only: when the shot director left the camera word blank, copy the real shot's measured camera move (still or handheld). |
| `firstFrame` / `lastFrame` | none | Start (and optionally end) the clip from a picture made by an earlier step, such as **Grab a frame** (**First frame from step** / **Last frame from step**, under **Advanced**). Typed prompts only, only for models that accept a picture, and only with `allowSourceMediaEgress` on. |
| `conformFps` / `conformHeight` | `0` / `0` | Bring a slower or smaller clip up to your programme's frame rate / height after delivery. `0` keeps the clip as delivered; a clip already at or above the target is left alone. |

```json
{"planSource": "Inline", "inlinePrompt": "Slow aerial shot over a misty pine forest at sunrise",
 "durationSeconds": 6, "takes": 1, "maxClips": 1, "maxSpendCredits": 1.0}
```

Before anything is bought, ReelBolt checks this cap plus daily caps per project and across the
whole platform; if any would be exceeded, nothing is bought and the step fails with
`BUDGET_EXCEEDED`. Other errors: `CONFIG_INVALID`, `INVALID_PROMPT`, `EGRESS_REFUSED`,
`POLL_TIMEOUT`. Identical earlier clips are reused for free unless you choose to regenerate. See
the [generated b-roll recipe](../recipes/generated-b-roll.md).

## Grab a frame step (`ExtractFrame`)

A **Grab a frame** step saves one frame of a video as an image. Use it to keep the last frame of a
clip as the start of the next one, to make a cover picture, or to reuse a still elsewhere in the
workflow. It does not use AI and it is free.

In the builder:

- **Take the frame from** — **An uploaded video file**, **The output of the previous step**, **The
  output of a specific step** or **The output of an earlier run (step result ID)**. The ID is shown
  on the run page and must belong to this project.
- **Frame** — **The last frame**, **The first frame** or **A moment I choose** (then **Moment
  (seconds)**, counted from the start of the video).
- **Format** — **PNG** (sharp, larger file) or **JPEG** (smaller file).
- **Longest side (pixels)** — 16 to 8192; empty keeps the video's own size. A smaller number only
  shrinks the picture, never enlarges it.
- **Save to project files** — on by default; lists the image under Files so you can download it and
  reuse it in later runs.

The image is the step's output, so a later `VideoGenerate` step can start from it (see
`firstFrame` above). Errors: `FRAME_OUT_OF_RANGE` (the moment is past the end of the video),
`SOURCE_NOT_FOUND` or `SOURCE_UNRESOLVED` (the video cannot be found), `FRAME_EXTRACTION_FAILED`.

- **Uses AI:** no. **Retries:** none. **Reuse on later runs:** yes, when the video is the same.

## Generate music or sound step (`AudioGenerate`)

A **Generate music or sound** step makes new music or a sound effect from a written description and
adds each result to the project's files as an audio file. The compile step then offers it like any
audio file you uploaded (see [Add music, sound effects and a colour grade](../recipes/music-sfx-and-grade.md)).
It does not think for itself; it sends your description to an audio service and collects the
result. Where your plan includes generated media, it is paid per result and counts towards the same
daily limits as **Generate Video**.

In the builder:

- **Kind** — **Music** or **Sound effect**.
- **Description** — up to 4000 characters. For music: style, mood, instruments and pacing. For a
  sound effect: the sound itself, for example "heavy wooden door creaking open, then a soft thud".
- **Length (seconds)** — music 3 to 300, sound effects 0.5 to 30. The provider you choose may allow
  a narrower range, and the step says so before anything is spent.
- **Instrumental only (no vocals)** for music; **Make it a seamless loop** and **How closely to
  follow the description (0 to 1)** for sound effects, where the provider supports them.
- **Provider** — blank uses your default music or sound-effect provider.
- **Takes** — 1 to 4 separate versions to choose from; each one is paid for.
- **Spending limit (credits)** — stops the step before it spends more than this. Empty applies only
  your daily limits.
- **Save to project files** — on by default, so you can listen to, download and reuse each take.
- Under **Advanced**: a specific **Model**, a music **Composition plan (JSON)** instead of a
  description, and a **File name prefix**.

Before anything is bought, ReelBolt prices the request and checks your limit and the daily limits;
if one would be exceeded, nothing is bought and the step fails with `BUDGET_EXCEEDED`. Other
errors: `NO_AUDIO_PROVIDER` (no provider is set up for this kind of audio), `CONFIG_INVALID`,
`PLAN_NOT_ENTITLED`, and `AUDIO_BAD_REQUEST` (the provider refused the description; its suggestion
is in the step's result).

- **Uses AI:** no. **Retries:** a failed step never buys the same take twice. **Reuse on later
  runs:** an identical request in the same project is served from the earlier result for free.

## Put on screens step (`PinInsert`)

A **Put on screens** step decides what appears on the tracked screens of your footage (a phone or
laptop showing a green, blue or magenta screen), without asking an AI to do it. It reads the
screens an **Analyze Video** step found, and hands the plan to a later **Compile Video** step, which
draws the content onto the screens. It does not use AI and it is free.

In the builder:

- **Screens found by** — the **Analyze Video** step that tracked the screens, or **The previous
  step**.
- For each piece of content:
  - **Screen** — the id of one screen, such as `r0`, as listed in the analyze step's result. Leave it
    empty to use the content on every screen that no other entry names.
  - **Content** — **The video or image of an earlier step** (for example the animation an Author
    agent rendered), **An uploaded video or image**, or **The output of an earlier run (step result
    ID)**, which must belong to this project.
- **Add content for another screen** — up to 16 entries. Only one entry may leave **Screen** empty.
- **Stop if something cannot be put on a screen** — on by default: the step stops with
  `PIN_INSERT_UNRESOLVED` and says which entry failed and why (a screen the analysis did not find,
  content that cannot be found or is not a video or image). Turn it off to use what works; the step's
  result lists the rest under `pinInsert.skipped`.

Then, in the **Compile Video** step, turn on screen inserts and set its graphics plan to this step
(`"graphicsPlan": {"from": "Step", "stepOrder": N}`, `"enableInserts": true`). If the analysis found
no screens the step stops with `PIN_INSERT_NO_REGIONS`: turn on green-screen detection in the
analyze step and check the screen is clearly visible.

- **Uses AI:** no. **Retries:** none. **Reuse on later runs:** yes, when its inputs are the same.

## Voiceover step (`Voiceover`)

A `Voiceover` step turns written lines into spoken audio with a speech service (Fish Audio, or a
local OpenAI-compatible speech server, configured by an admin). It does not decide what to say.

- `source` — one of four kinds:
  - `{"kind": "Inline", "lines": [...]}` with lines you write, each with `text`, `startSec`
    (seconds on the **original** video's clock) and an optional `voiceId`;
  - `{"kind": "ScriptScenes", "scriptwriterStepOrder": 8}` to speak the narration a
    `ScriptwriterAgent` step wrote, scene by scene;
  - `{"kind": "NarrationPlan", "narrationWriterStepOrder": 3}` to speak the lines a
    `NarrationWriter` (or `NarrationTranslator`) step planned against the edit;
  - `{"kind": "PickupPlan", "pickupPlannerStepOrder": 3}` to re-record the sentences a
    `PickupPlanner` step named.
- `defaultVoiceId` — the provider's voice to use when a line has none.
- `projectVoiceId` — a voice from the project's **Voices** tab instead. A cloned voice is usable
  only while its recorded consent is in place; otherwise the step **fails** rather than speaking
  in another voice.
- `voiceCasting` — `Off` (default) or `Decision`: let the decision model pick one of the project's
  usable voices from their descriptions.
- `pronunciationDictionary` — `Off` (default) or `Auto`: tell Fish Audio how to say the product
  and library names the code analysis found (from a fixed list ReelBolt knows how to spell out).
- `language` — a note recorded on the result (the speech service hears the language from the
  text).
- `providerId`, `model` — optional overrides.

```json
{"source": {"kind": "Inline", "lines": [{"text": "Welcome to the tour.", "startSec": 0}]},
 "defaultVoiceId": "YOUR-FISH-AUDIO-VOICE-ID"}
```

**Warnings in the builder.** The step's settings check the speech service a run would use right
now. If it failed its last connection test, a red box says **Narration will probably fail**; if a
working backup will be used instead, a yellow box says **Using a backup voice service**. Fixing the
service is an admin task (see [Inference providers](../admin/inference-providers.md)). Under
**Voice**, pick one of the project's voices, or follow the link to the project's **Voices** tab to
add one; the model, voice ID, size limit, pronunciation help and automatic casting are under
**Advanced**.

To hear it in an edited video, switch on `enableVoiceover` and set `voiceoverStepOrder` on the
`VideoCompile` step; add `enableCaptions` to see it too. In a promo, the `AuthorAgent` picks the
audio up by itself and sizes each narrated scene to the measured length of its line. See the
[voiceover recipe](../recipes/voiceover.md).

- **Retries:** up to the normal retry count. **Reuse on later runs:** yes.

## Render timeline step (`TimelineRender`)

A `TimelineRender` step turns one of the project's **editor timelines** (see
[The timeline editor](../editor/index.md)) into a finished video file. It is plain processing: the cut,
the layers, the text and the audio levels all come from the timeline document, and the step only
chooses the delivery format. Nothing about the timeline changes. The editor's **Deliver** workspace
creates and runs this step for you; you add it to a workflow of your own when a render should be part
of a longer pipeline. The video editor is available as an Early preview, so a project's editor timelines can be rendered with
this step.

- `editTimelineId` — the timeline to render. Required. The builder offers a picker over the
  project's timelines.
- `revision` — which saved version to render; blank renders the latest when the step starts, and the
  run records the version it used.
- `range` — `{"inFrame": 0, "outFrame": 300}` to render part of the timeline, in frames; blank
  renders all of it.
- `preset` — `Hd1080p16x9` (default), `Uhd4k16x9`, `Vertical9x16`, `Square1x1`, `Portrait4x5`,
  `Master` (the timeline's own size at the highest quality) or `AudioOnly`. The render stays within
  your plan's highest resolution: a preset bigger than your plan allows comes out scaled down to the
  limit, with the same shape and quality. In the Deliver workspace, `Uhd4k16x9` (or an explicit
  `width`/`height` above your plan) is refused when your plan does not include that size.
- `encode` — optional refinements over the preset: `codec` (`H264`, `H265`), `crf` (0 best – 51
  smallest), `speed` (`Fast`, `Medium`, `Slow`), `audioBitrateKbps` (64–320), an even
  `width`/`height` pair, and a rate control (below). Anything outside these ranges is refused when you save.
  - `rateControl` — `Quality` (default, a steady look; the file size follows your video), `Capped` (the
    same look, staying close to or under `videoBitrateKbps`) or `Average` (aims for `videoBitrateKbps`, so the file
    lands close to a size you can predict). `videoBitrateKbps` is 200–100000 (100 Mbps) and is required by
    `Capped` and `Average`; `Quality` and an audio-only render refuse it. Left out, the render is the same
    plain quality encode as before. The price of a render does not change with the bitrate.
- `outputFileName` — a plain file name (letters, digits, spaces, dots, dashes, underscores).
- `registerProjectFile` — also list the file under the project's Files (default on).
- `loudness` — `{"targetLufs": -16, "truePeakDbtp": -1.5, "loudnessRange": 11}` to normalise the
  programme loudness; blank measures it without changing it.

```json
{"editTimelineId": "1b2c3d4e-5f60-4711-8a92-3c4d5e6f7001", "preset": "Vertical9x16",
 "outputFileName": "reel.mp4", "loudness": {"targetLufs": -16, "truePeakDbtp": -1.5, "loudnessRange": 11}}
```

The step renders everything the editor can express: cuts and layers, titles, each clip's position,
scale, rotation, opacity, crop, fades, speed, reverse and freeze-frame, animated values, transitions,
colour/bars/test generators, and the mixer's faders, pan and equaliser — exactly as the editor shows
them. The step renders in parts and remembers each part by what is in it, so rendering the same timeline
again after a small change redoes only the parts that changed. Its result lists how many parts were
reused, the measured loudness, and the finished file, which appears under **Outputs** like a compiled
video.

- **Retries:** at most once. **Reuse on later runs:** no — the step keeps its own cache of rendered
  parts instead, which is finer than reusing the whole result.
