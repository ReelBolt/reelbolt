---
title: Add AI-generated b-roll clips
sidebar_position: 10
description: Recipe for adding paid, AI-generated cutaways, a cold open or an end card to an edited video, with spending caps and privacy settings explained.
---

# Add AI-generated b-roll clips

**Goal:** add short AI-generated video clips ("b-roll") to an edited video: a **cutaway** shown
while someone keeps talking, a **cold open** before the video starts, or an **end card** after it
ends. A shot-director AI plans the clips, a generation step **pays a video-generation service** to
make them, and the compile step places them.

**Generated clips use credits.** Each clip is charged to your workspace's balance, and ReelBolt
checks the caps described below before it buys anything, so it never buys a clip that would go over
a cap. Generated video is available only on plans that include it (see [Plans and credits](../account/plans-and-credits.md)).

## Prerequisites for generated b-roll

- The video uploaded to the project.
- A chat AI provider.
- **A video-generation provider**, connected by an administrator for your workspace. If a step does
  not name one, the workspace's default video provider is used. See
  [Inference providers](../admin/inference-providers.md) for how an administrator connects one.
- Enough credits for the clips you plan to make. The run page shows the estimate before you start.

## What generated clips cost

Generated video is priced per clip and is shown as a ceiling before you start. When you start a run,
the cost line on the run page reads **Plus up to** followed by the most the generated clips can
cost, and a run that regenerates clips shows the same figure in its dialog. See
[Reading the estimate](../account/reading-the-estimate.md) for what the figures mean.

## Spending caps for generated clips

Three caps protect you; a purchase must fit under **all** of them:

1. **Per step:** `maxSpendCredits` on the `VideoGenerate` step (required, must be above 0). The whole
   plan's estimated cost is checked against it **before** anything is bought.
2. **Per project per day:** a daily limit set by your administrator.
3. **Whole platform per day:** a daily limit set by your administrator, across all projects.

If any cap would be exceeded, **nothing at all** is bought and the step fails with
`BUDGET_EXCEEDED`. There is no partial purchase. `maxClips` (1 to 4) also limits how many clips one
step can make; planned shots beyond it are skipped, not bought.

## The generated b-roll workflow

Add the **Footage edit with AI-generated b-roll** template (`video-derush-edit-broll`) and select your video
in step 1.

| # | Label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan generated b-roll shots | `Agent` (context `FullWorkflow`) | `ShotDirector` |
| 4 | Generate planned b-roll clips | `VideoGenerate` | `VideoTransform` (calls the paid service) |
| 5 | Compile edited video with generated clips | `VideoCompile` | `VideoTransform` (not an AI) |
| 6 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

**Step 4** as shipped. The model and resolution are left to their defaults:

```json
{"planSource": "StepRef", "plan": {"from": "Step", "stepOrder": 3},
 "analysis": {"from": "Step", "stepOrder": 1},
 "durationSeconds": 6, "ratio": "16:9",
 "takes": 1, "maxClips": 4, "maxSpendCredits": 5.0,
 "pollIntervalSeconds": 10, "timeoutSeconds": 1800}
```

- `"planSource": "StepRef"` makes one clip per planned shot from step 3.
- `takes` — versions per shot (1 to 4, never more than `maxClips`).
- `ratio` — one of `21:9`, `16:9`, `4:3`, `1:1`, `3:4`, `9:16`; match your footage.
- `timeoutSeconds` — how long to wait for each clip (60 to 7200).

**Step 5**:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableGeneratedClips": true, "generatedClips": {"from": "Step", "stepOrder": 4},
 "maxGeneratedClips": 4, "transitionPolicy": "Auto", "programFadeInMs": 500,
 "programFadeOutMs": 800, "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900,
 "minSegmentMs": 800}
```

## What the shot director plans

The `ShotDirector` plans zero or more shots. For each it chooses:

- a **purpose**: `Cutaway` (replaces the picture for a moment while the sound continues), `ColdOpen`
  (added before the start), `EndCard` (added after the end), `SeamBridge` (a generated clip between
  two cuts, only when the compile step's `transitionPolicy` is `"Generated"`), `Extend` (lengthens a
  shot that is shorter than its narration) or `ScreenContent` (put **onto a tracked green screen** in
  your footage — needs `"enableInserts": true` on the compile step and a green screen found by the
  analysis step; without those the clip is reported as `inserts_disabled` or
  `no_insert_region_at_anchor` and skipped);
- **where** it goes: one of the offered shot, pause or phrase ids;
- a **camera** word and a **length** word (`Short`, `Medium`, `Long`), which ReelBolt turns into
  real settings;
- a written **prompt** describing the clip. This is the only text sent to the provider, after
  cleaning.

The shot director cannot spend money; only step 4 can, and only within the caps. Steer it through
the run's brief (the **Your brief** box when you click **Run**), for example "One cutaway of
a busy coffee shop when I mention the café, and an end card with a sunset over the city".

## Running and re-running generated b-roll

When you run or retry this workflow, ReelBolt asks:

- **Run and reuse previous clips** (default, free): any clip with exactly the same prompt and
  settings that was already generated in this project, and still exists, is reused.
- **Regenerate (costs money)**: pay for fresh clips. The dialog shows the most it could cost.

When the review step sends the run back to step 2, unchanged clips are reused too; set step 4's
`"loopBackBehavior": "Regenerate"` to pay for new ones on every round instead.

**What to check:** step 4's result lists every clip, whether it was delivered, reused or skipped,
and its cost. Its detailed record (data view) is the full clip list. Step 5's result reports which
clips were placed and any dropped with a reason.

## Privacy: sending your own footage to the provider

The shot director may ask for a clip to start or end on a frame from your own footage, so the clip
matches visually. That frame **is your footage**, and once sent to a provider it cannot be called
back. So:

- `allowSourceMediaEgress` on step 4 is `false` by default. **Adding this template yourself from the
  template picker sets it to `true`** — that is the template's own permission for the shot director
  to key a generated shot off a frame of your footage, which is the whole point of b-roll that cuts
  together with the edit. **A workflow the assistant creates for you starts with it off**: the
  permission to send your footage is yours to give, so the assistant tells you to switch it on in the
  step before running. On a
  step of your own, leaving it `false` means any planned shot that asks for a frame stops the
  **whole step** with `EGRESS_REFUSED` before anything is bought: either set it to `true` (your
  explicit permission) or plan shots without frames.
- With permission, the frame **is sent**: ReelBolt cuts one frame out of the shot the plan named (the
  shot's first moment for a starting frame, its last for an ending one) and sends it to the
  video-generation service as part of that clip's request. Each clip's record says whether the frame
  went with it. A still image you added to the project can be used the same way — it is analysed as a
  source, so its beats are ordinary shot ids the plan can name.
- A service that cannot accept an image at all is refused up front, with `KEYFRAME_NOT_SUPPORTED`,
  before anything is bought.

## Variation: two takes per clip and a dailies room

Add the **Footage edit with AI b-roll, best take chosen by a panel** template (`video-derush-edit-dailies`) instead.
Its generation step makes **two takes** of every planned clip and switches on **look matching** (each
clip is asked to match the colour and contrast of the real shot beside it) and **camera continuity**
(a blank camera word copies the real shot's still or handheld feel). A second analysis step
measures every take (`"source": {"kind": "GeneratedClips", "stepOrder": 4}`), a `DailiesRoom` panel
picks the better take of each shot by its id, and the compile step's
`"takeSelection": {"from": "Step", "stepOrder": 6}` places only those. A shot the panel leaves
undecided keeps its first take. Expect roughly double the generation cost.

## Variation: one clip from your own prompt

For a single clip described by you, without the shot director, use a `VideoGenerate` step on its
own with `"planSource": "Inline"`:

```json
{"planSource": "Inline", "inlinePrompt": "Slow dolly shot through a sunlit greenhouse full of ferns",
 "durationSeconds": 6, "ratio": "16:9",
 "takes": 1, "maxClips": 1, "maxSpendCredits": 1.0}
```

The first clip becomes the step's output video (visible in Renders). A later `VideoAnalyze` step can
use it as its source with `{"kind": "PreviousStepOutput"}` or
`{"kind": "StepOutput", "stepOrder": 1}` (the generation step's number), so it can be edited like
any other footage.

## Pitfalls with generated b-roll

- **`CONFIG_INVALID` before anything happens:** `maxSpendCredits` missing or 0, `takes` above
  `maxClips`, an unsupported resolution or ratio for the model, or `planSource` `"StepRef"` without
  `plan`.
- **`BUDGET_EXCEEDED`:** the plan costs more than `maxSpendCredits`, or a daily cap is reached. Nothing
  was bought. Raise the step cap, ask for fewer or shorter clips, or wait for the next UTC day.
- **`EGRESS_REFUSED`:** see the privacy section above.
- **`INVALID_PROMPT`:** an empty prompt or one over 7000 characters (inline mode).
- **`POLL_TIMEOUT`:** the provider took longer than `timeoutSeconds`. The clip may still be charged.
- **Clips not in the video:** step 5 needs `"enableGeneratedClips": true`, `generatedClips` pointing
  at step 4, and `"mode": "Reencode"` (otherwise `GENERATED_CLIPS_REQUIRE_REENCODE`). A cutaway
  whose moment was cut out of the edit is dropped.
- **Unexpected re-spend:** choosing **Regenerate**, setting `loopBackBehavior` to `"Regenerate"`,
  or changing a clip's prompt or settings (including the shot director planning different shots)
  all buy new clips.

## Recipe: Footage edit with AI-generated b-roll

Template: **Footage edit with AI-generated b-roll**. A highlight edit plus extra shots generated
by AI: a cold open, cutaways or an end card.

**Set before you run it.** Your clips, spoken language, length, shape, style, and in the notes
the extra shots you want, in plain words. Setting meanings are in [Run settings](run-settings.md).

**What it costs.** It uses credits and AI shots. Generation is real spend and is capped for each
run. The run page shows the price and the generation allowance before you start, and your plan
must allow AI shots.

**What you get.** One edited video with the generated shots placed where the shot plan puts them.

**Common problems.**

- The run stops at the generation step: no video-generation provider is connected, or your plan
  does not include AI shots. Ask your admin.
- A shot is not what you described: name subject, setting and mood in the notes, then use the
  re-generate option on the run.
- People or private footage in the clips: read the privacy section on this page before you run.
