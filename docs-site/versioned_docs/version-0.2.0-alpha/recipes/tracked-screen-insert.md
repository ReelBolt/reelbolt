---
title: Put an animation on a green screen (tracked screen insert)
sidebar_position: 12
description: Step-by-step recipe for filling a green-screen phone or laptop screen in your footage with an animation ReelBolt builds, following the screen as it moves.
---

# Put an animation on a green screen (tracked screen insert)

A **tracked screen insert** fills a coloured screen in your footage — typically a phone or laptop
showing a plain green screen while someone holds or uses it — with an animation, so that the
animation moves, turns and tilts with the device as if it were really on the screen. Rounded
corners, the camera notch and a hand passing in front of the screen are kept.

**Yes, ReelBolt can do this. The agents build the animation and ReelBolt renders it itself.** There
is no separate "render" step: the motion-graphics AI (`MotionGraphicsPlanner`, or the director of a
`GraphicsRoom` panel) builds the animation, renders it, and names it in its answer. The
`VideoCompile` step then pins it into the tracked screen.

## Put one of your own videos on the screen (no AI needed)

When you already have the clip that should play on the screen — a promo, a screen recording, an
image — you don't need any AI step at all. Upload both videos to the project and build a
two-step workflow:

1. **`VideoAnalyze`** with the green-screen footage as its source and **Detect tracked
   screen-insert regions** switched on. Pick only the green-screen footage here — **not** the clip
   you want on the screen. Every source of this step is footage for the edit; a clip listed here
   ends up in the finished video as a scene of its own.
2. **`VideoCompile`** with **Keep every source whole (no edit)** on, **Enable tracked screen
   inserts** on, and your clip picked under **Insert content (your file)**.

In JSON, step 2 looks like this:

```json
{
  "version": 1,
  "decision": {"from": "Previous"},
  "analysisStepOrder": 1,
  "keepWholeSources": true,
  "enableInserts": true,
  "insertContentProjectFileId": "PUT-YOUR-CLIP-FILE-ID-HERE",
  "insertFit": "Auto"
}
```

- `keepWholeSources` keeps the footage uncut, so no editor agent is needed (`decision` is then
  ignored).
- `insertContentProjectFileId` is the clip shown on the screen. It must be a video or image in this
  project.
- `insertFit` decides what happens when the clip's shape differs from the screen's: `"Auto"`
  (default) keeps your clip's shape and adds black bars when it is very different (a landscape
  video on an upright phone), `"Cover"` crops to fill the screen, `"Stretch"` distorts it to fit.

The tracker tunes itself to each video: it tries several settings on your footage and keeps the
one whose outline sits exactly on the screen's edge from frame to frame. Open the result in the
**Editor** to see the tracked screen drawn over the video, its confidence, whether it was cut to
the screen's exact shape ("matted"), and which settings the tracker chose.

## The ready-made template, or ask the assistant

The two steps above are also a template, **Green-screen insert** (`green-screen-insert`; see
[Workflow templates](../workflows/templates.md)). It uses no AI model; only processing time is charged. The quickest way
to use it is to ask the assistant in plain words, in the app or from Claude Code:

> Put the screen recording `app-demo.mp4` onto the green screen in `cat-and-phone.mp4`.

The assistant checks both files, shows you what it is about to save, and creates the workflow (or
updates the one with the same name) with the right files already chosen. It does not start the run:
ask it to run the workflow when you are ready. To show a render from an earlier run instead of an
uploaded file, say so: it uses that run's result (it must be a run of the same project).

For the JSON, step 1 is `{"version":1,"source":{"kind":"ProjectFile","projectFileId":"THE-GREEN-SCREEN-CLIP-ID"},"detectInsertRegions":true,"transcription":"Off","vision":"Off"}`
and step 2 is the JSON above with `insertContentRef` in place of `insertContentProjectFileId`:
`"insertContentRef": {"from": "ProjectFile", "projectFileId": "THE-CONTENT-FILE-ID"}`
(`{"from": "StepResult", "stepResultId": "…"}` names a render of an earlier run).

## Different videos on different screens

One edit can show different files on different screens, for example a phone recording on the
phones and a desktop recording on the laptop. In the compile step, under **Screen inserts**, click
**Add content for other screens** and, for each extra file, say which screens it is for:

- **Only on screens in clip number**: the clip's place in the first step (1 = the first clip).
- **Only on screen**: one screen's id (such as `r2`), shown in the first step's result.
- Leave both empty to use the file on every screen not covered otherwise.

A screen that matches nothing extra shows the file picked under **Insert content (your file)**. In
JSON this is `"insertContents": [{"projectFileId": "…", "sourceIndex": 1, "fit": "Contain"}]`
(`sourceIndex` counts from 0). Each extra file can have its own fit.

## Put an earlier step's video on the screens

To show something the workflow made itself (for example an animation an Author agent rendered
earlier in the run, or the output of an earlier run), add a **Put on screens** step after the
analyze step and the step that makes the content. Point **Screens found by** at the analyze step,
pick the content under **Content**, and in the compile step set the graphics plan to the **Put on
screens** step with screen inserts turned on. No AI is involved in choosing what goes where. See
[Put on screens step](../workflows/step-types.md#put-on-screens-step-pininsert).

## When your screen recording fades to black

Screen recordings often go black for a moment between screens. On a filmed phone that looks as if
the phone switched off. ReelBolt finds these black moments in your file and, by default, keeps
showing the last picture until the recording continues, so the screen never goes dark and the
timing of the recording stays the same. Under **When your video fades to black** you can instead
**Skip the black part** (the recording gets a little shorter) or **Leave it as it is**
(`"insertContentDips": "Freeze"`, `"Cut"` or `"Keep"`). The compile step's result lists the black
moments it found under `inserts.appliedInserts[].contentDips`. Videos made by the AI for the
screen, and pictures, are never changed.

## How a tracked screen insert works, in one picture

A tracked screen insert takes four steps, each with one job:

1. **`VideoAnalyze`** finds the coloured screen and follows its four corners through every frame
   (`"detectInsertRegions": true`). Each screen it follows is offered to the AI as a short id:
   `r0`, `r1`, ... with plain hints (how confident, how big, moving or still, its shape, flat or
   curved).
2. **`VideoStoryEditor`** (an `Agent` step) decides which parts of the footage to keep. The compile
   step cannot run without this decision. To keep the whole clip, say so in the request text.
3. **`MotionGraphicsPlanner`** (an `Agent` step) — or a **`GraphicsRoom`** step — chooses which
   offered screen to fill, **builds an animation shaped like that screen and renders it**, and
   returns it in its answer's `inserts` list: `regionId` (for example `r0`), the rendered animation
   file (saved by the agent itself) and `reason`.
4. **`VideoCompile`** reads that answer through its `graphicsPlan` setting and, with
   `"enableInserts": true` and `"mode": "Reencode"`, warps the animation onto the moving screen
   frame by frame and cuts it to the screen's exact shape.

The part that says **what fills the screen** is the entry in the plan's `inserts` list, produced by
step 3: an animation the agent built and rendered, or, when your request names one of your own
project files, that file. To skip the AI entirely, see [Put one of your own videos on the
screen](#put-one-of-your-own-videos-on-the-screen-no-ai-needed).

## Prerequisites for a tracked screen insert

Before building a tracked screen insert workflow, you need:

- **Footage with a plain, evenly coloured screen**, uploaded to the project. Green works best;
  blue and magenta are also supported. The screen should be at least a small part of the frame and
  visible for at least 1 second. A real phone or laptop showing a full-screen green image is ideal.
- **A working chat AI provider** (for the editor and the motion-graphics AI). The motion-graphics AI
  plans and builds each animation, so a capable model helps.
- Optional: a transcription provider, if the footage has speech the edit should follow.

**This recipe uses credits.** Running it is charged like any workflow run: each step is charged for
the work it does, and the cost line on the run page shows the expected cost before you start. Work
through a provider your workspace connected itself is charged 0 credits. See
[Reading the estimate](../account/reading-the-estimate.md) and [Plans and credits](../account/plans-and-credits.md).

## Build the tracked screen insert workflow

The quickest way is to start from the **Footage edit with titles and callouts** template
(`video-derush-edit-graphics`), which already has the four steps in the right order plus a review
step, and change three settings. You can also ask the assistant: "Set up a tracked screen insert
workflow for my green-screen phone video".

The full workflow, step by step:

| # | Label | Step type | Agent | Context |
|---|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` | — |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` | `PreviousStepOnly` |
| 3 | Plan graphics and screen inserts | `Agent` | `MotionGraphicsPlanner` | `FullWorkflow` |
| 4 | Compile edited video with insert | `VideoCompile` | `VideoTransform` | — |
| 5 (optional) | Review edit quality | `ReviewLoop` | `VideoReviewAgent` | `FullWorkflow`, back to step 2, `minScore` 8, `maxIterations` 3 |

Set steps 2, 3 and 5 to `"cacheMode": "Never"` so each run makes fresh decisions (step 3 is never
reused anyway, because it renders).

**Step 1 settings** (`videoAnalyzeConfigJson`) — pick your uploaded video as the source:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "detectInsertRegions": true,
  "insertRegionColor": "green",
  "maxInsertPlatesPerFrame": 1,
  "measureInsertCurvature": true,
  "solveInsertChromaKey": true
}
```

- `detectInsertRegions` is the switch that matters; it is off by default.
- `insertRegionColor` — `"green"` (default), `"blue"` or `"magenta"`.
- `maxInsertPlatesPerFrame` — raise to 2 or more when two screens appear at the same time.
- `measureInsertCurvature` and `solveInsertChromaKey` are already on by default; they make the
  corners precise, handle curved screens, and keep rounded corners, notches and hands in front.
- Add `"emitOverlayPlacements": true` only if you also want titles and lower-thirds.

**Step 4 settings** (`videoCompileConfigJson`):

```json
{
  "version": 1,
  "decision": {"from": "Step", "stepOrder": 2},
  "analysisStepOrder": 1,
  "mode": "Reencode",
  "graphicsPlan": {"from": "Step", "stepOrder": 3},
  "enableInserts": true,
  "insertSurface": "Auto",
  "enableInsertMatte": true,
  "transitionPolicy": "Off"
}
```

- `graphicsPlan` **must** point at the planner (or graphics room) step. Inserts are read from that
  same plan; without it nothing is inserted.
- `enableInserts` turns the feature on. `enableGraphics` is **not** needed for inserts; add
  `"enableGraphics": true` only if you also want the planner's titles and lower-thirds.
- `mode` must be `"Reencode"` (the default). `"StreamCopy"` makes the step fail with
  `INSERTS_REQUIRE_REENCODE`.
- `insertSurface` — `"Auto"` (default) chooses a flat or curved warp from what was measured;
  `"Planar"` always flat; `"Mesh"` always curved (for testing only).
- `enableInsertMatte` (default `true`) cuts the animation to the screen's exact shape.
- `"transitionPolicy": "Off"` keeps plain cuts. The template uses `"Auto"`, which can add dissolves
  between cuts; an insert is trimmed away from any dissolve, so with `"Auto"` the insert can start
  late or disappear near a cut.

If you start from the template: in step 1 add `"detectInsertRegions": true` (and keep
`"emitOverlayPlacements": true` only if you want titles too); in step 4 add `"enableInserts": true`,
optionally remove `"enableGraphics": true`, and consider `"transitionPolicy": "Off"`. Its step 4
already has `"graphicsPlan": {"from": "Step", "stepOrder": 3}`.

## Tell the AI what to show on the tracked screen

The motion-graphics AI decides what the screen shows, so tell it. When you click **Run**, type a
brief such as:

> Keep the whole clip. In the phone screen (r0), show an animated mockup of our app's home screen:
> dark background, our logo fading in at the top, then three cards sliding up. Brand colour #6C5CE7.
> No titles or lower-thirds.

Good requests:

- Say **keep the whole clip** (or what to cut). This instruction is for the editor in step 2.
- Describe the **content** of the screen: what appears, colours, text, motion.
- Say whether you also want titles or lower-thirds.
- Mention the screen by its role ("the phone screen") if there may be several.

What the AI does with it: it builds a small animation in a sandbox, shaped to the screen's
measured proportions (for example a tall portrait animation for a phone held upright), renders it as
a normal solid video, and puts the result in its answer's `inserts` list. It can also read your
project files, so a brief uploaded to the project can be referred to by name.

## Run the tracked screen insert workflow and check the result

Run the workflow and follow it on the run page. What to check, step by step:

- **Step 1** — the progress messages say "Tracking insert regions" and, per screen found, something
  like "Insert region r0: flat, silhouette key ... (agreement 92%)". In the step result, the
  `meta.insertTracking.regions` count should be at least 1 and `meta.offeredInsertRegionIdCount`
  should be the same number. The `view.insertRegions` list shows each screen's `conf`
  (`high`/`medium`/`low`), `size`, `motion`, `aspect` and `surface` (`Flat`/`Curved`).
- **Step 2** — the editor's `keep` list should cover the part of the clip where the screen is
  visible.
- **Step 3** — the result's `inserts` list should contain an entry with `regionId` and a rendered
  animation file. The step's **Output Video** player shows the animation the agent rendered.
  This step is slow: writing, checking and rendering the animation can take many minutes.
- **Step 4** — the result's `inserts` section reports `appliedInsertCount` and, for each insert,
  whether it was flat or curved (`surface`, mesh size) and `matted`. Anything skipped is listed in
  `droppedInserts` with a reason. Watch the **Output Video**.

## Why a tracked screen insert was not applied

When the screen still shows plain green in the final video, the `VideoCompile` step's result says
why. The reasons, in plain words:

| Reason shown | What it means | What to do |
|---|---|---|
| "No GraphicsPlan configured" | Step 4 has no `graphicsPlan` and no `insertContentProjectFileId`. | Set `"graphicsPlan": {"from": "Step", "stepOrder": 3}`, or pick your own file as insert content. |
| `content_not_in_project` / `content_not_video_or_image` | The chosen insert content is not a video or image of this project. | Pick a video or image uploaded to this project. |
| empty `inserts`, no dropped entries | The planner returned no inserts (no screen offered, or it chose none). | Check step 1 found a screen; ask for the insert explicitly in the request. |
| `unknown_region_id` | The planner named a screen id that step 1 did not offer in this run. | Check step 1's `offeredInsertRegionIdCount`; see the next section about large views. |
| `confidence_below_threshold` | The screen was tracked with low confidence (below `minInsertConfidence`, default 0.5). | Use footage with a more even, better-lit screen, or lower `minInsertConfidence` carefully. |
| `invalid_asset_storage_key` | The planner's animation is missing, invented, or from another run. | Run again; the animation must be rendered in the same run. |
| `asset_download_or_probe_failed` | The rendered animation could not be read. | Run again. |
| `cut_away` | The part of the clip with the screen was cut out by the edit. | Tell the editor to keep that part. |
| `window_consumed_by_transition_overlap` | A dissolve covered the whole insert. | Use `"transitionPolicy": "Off"`. |
| `max_inserts_exceeded` | More inserts than `maxInserts` (default 3). | Raise `maxInserts`. |
| `duplicate_region_id` | The same screen was chosen twice. | Only the first is used; nothing to do. |
| `unavailable: true` | The video tools on the server lack a required feature. | Ask your administrator. |

When a **Review Loop** checks a compile that was asked for screen inserts and applied none, the
review score is capped at 6, so the loop goes back for another try. The feedback names each screen
that was left out and the reason from the table above.

## Pitfalls with tracked screen inserts

Common reasons a tracked screen insert goes wrong:

- **Step 1's source is empty.** Templates leave the file unselected; the run fails with
  `SOURCE_UNRESOLVED` until you pick the video.
- **The screen list was left out because the description got too big.** Step 1 keeps the
  description it hands to the AI under `maxOutputChars` (24000 characters). On long or busy footage
  it may leave out the whole `insertRegions` list to fit; then step 3 cannot see any screen. Check
  that `meta.offeredInsertRegionIdCount` is above 0. If it is 0 while `meta.insertTracking.regions`
  is above 0, raise `maxOutputChars` (for example to 40000) or set `"visualDetail": "None"` in
  step 1.
- **The editor cut the screen out.** Ask it to keep the whole clip, or the part with the screen.
- **No brief.** Without a brief in the run's confirmation box, the planner guesses what to
  show (or shows nothing). Describe the content.
- **Uneven or reflective screens.** Strong glare, a very dark green, or green objects elsewhere in
  the shot lower tracking confidence. A brighter, evenly lit screen tracks best.
- **Colour grade and matte together.** The screen's exact colour is measured on the original
  footage, but a colour grade (`enableColorGrade`) is applied before the insert. A strong grade can
  shift the screen's colour so the cut-out shape is less exact. Leave colour grading off for insert
  shots, or use a subtle look.
- **One insert per stretch of screen.** If the edit cuts the screen's appearance into two separate
  pieces, the insert appears only in the first piece.
- **What is not handled:** relighting the scene to match the room, motion blur on fast movement, and
  screens that are not a plain colour (a screen showing real content cannot be tracked).

## Variations of the tracked screen insert workflow

Ways to adapt a tracked screen insert workflow:

- **Panel of artists instead of one planner.** Replace step 3 with a `GraphicsRoom` step:
  `{"version": 1, "view": {"from": "Step", "stepOrder": 1}}`. Its `InsertArtist` seat argues about
  which screens to fill and what to show; the director renders the animations in its final call. Step
  4's `graphicsPlan` then points at the room step. Point the room's `view` at step 1, not at
  `Previous`.
- **Titles too.** Add `"emitOverlayPlacements": true` to step 1 and `"enableGraphics": true` to
  step 4. The same plan carries both titles (`overlays`) and screen inserts (`inserts`).
- **Two screens at once.** Set `"maxInsertPlatesPerFrame": 2` in step 1; each screen gets its own
  id (`r0`, `r1`) and its own animation.
- **Blue or magenta screen.** Set `"insertRegionColor": "blue"` or `"magenta"`.
- **Several clips.** `VideoAnalyze` can analyse several videos together (`sources` list); inserts
  work when the edit combines clips.

## Checking green-screen tracking only (no insert)

To see whether ReelBolt can track your screen before building the whole workflow, make a workflow
with just the `VideoAnalyze` step from above and run it. The step uses no chat AI; speech-to-text
still runs if a transcription provider exists (add `"transcription": "Off"` to skip it). On the
step's result:

- Progress messages list each screen found, whether it reads flat or curved, and how well its exact
  colour was found ("agreement" percentage).
- `meta.insertTracking` shows whether tracking ran (`applied`), whether it hit a problem
  (`degraded`), and how many screens were found (`regions`).
- `view.insertRegions` shows each screen's id, confidence, size, motion, shape (`aspect`) and
  surface.
- The full corner-by-corner tracking is in the step's detailed record (data view).

To see a composite without any AI, add the two-step `VideoCompile` from [Put one of your own
videos on the screen](#put-one-of-your-own-videos-on-the-screen-no-ai-needed) with any uploaded
clip as the insert content. For troubleshooting, `insertSurface`
`"Planar"` or `"Mesh"` on the compile step forces the flat or curved warp, and
`"enableInsertMatte": false` turns off the exact-shape cut-out.

## Recipe: Green-screen insert

Template: **Green-screen insert**. It finds the green screen in your clip and puts a screen
recording, video or picture on it, following the screen as it moves, even when a hand passes in
front.

**Set before you run it.** Choose the clip that has the green screen and the content to place on
it, in the two steps of the recipe. Nothing else is needed: there is no language, length, style or
narration to set. Setting meanings for other recipes are in [Run settings](run-settings.md).

**What it costs.** No AI model is used and no credits are spent on a model. You are charged for
processing time only. The run page shows the amount.

**What you get.** Your clip with the content fixed to the screen, moving and turning with it.

**Common problems.**

- The insert is not applied: the clip has no clear green screen, or the screen is mostly hidden.
  Use a clip where the screen is plainly visible and evenly lit.
- The run stops with no clip chosen: set the clip and the content before you run.
- The recording fades to black at the end: see the section on that on this page.
