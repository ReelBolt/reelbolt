# Review loop adjustments and stronger review evidence

Status: implemented behind opt-in flags (`ReviewLoopStepConfig.AllowAdjustments`, `VisualCheckConfig.TimelineFacts` /
`AudioFacts` / `TargetedLooks`). Off is byte-identical. Every shipped template turns them on
(`WorkflowTemplateCatalog.ShippedReviewLoopConfigJson`, product owner's decision of 2026-11-04). Since that decision a
review may also re-run paid steps and ask for the changes a customer can request (see "Change requests inside the run").

## Why

Run `30a2792e-18b5-4e5d-90bd-e0bea3d852aa` ("Caffè Lume ad (rooms)": AudioGenerate x2 -> VideoAnalyze -> EditRoom ->
GraphicsRoom -> ColorGradeRoom -> SoundDesigner -> VideoCompile -> ReviewLoop to step 4) spent three rounds being told
"the title is below the 2.5% cap-height floor" and "SFX cues overlap". Both are compile settings
(`overlayFontSizePct`, `maxSfxCueSeconds`) that no agent in the loop can change, so the loop burned its retries and
stopped at 7/10. An earlier run (`a60d5aae`, issue 3 of the 2026-11-03 list) needed a VideoAnalyze setting
(`maxOutputChars`) upstream of the loop target. The reviewer could also only see a few evenly spaced frames.

## Design

1. **Closed allowlist** (`ReelBolt.Shared/Workflows/Review/ReviewAdjustableSettings.cs`, the only list). VideoCompile:
   `overlayFontSizePct` 3..12 (drawtext overlays only; a title rendered as a picture is the graphics step's work),
   `overlayBoxHeightPct` 10..30, `maxSfxCueSeconds` 0.5..4, `targetDurationSec` within ±25% of the run brief's length
   (else the saved value, else the current value; no base = refused), `minSegmentMs` 300..3000, `outputFit`
   Crop|Pad|Subject, `subjectSafeArea` 0..0.15, `captionFontSizePct` 3..12, `musicGainDb` -20..0, `transitionPolicy`
   Off|AudioOnly|Auto|Expressive. VideoAnalyze: `maxOutputChars` 8000..120000, `maxSubShotsPerShot` 1..8.
   `minOverlayCapHeightPct` 1..12 (the legibility floor of overlay text and rendered graphics, from the merged
   graphics lane; the graphics step reads it from the compile step's config in the run snapshot, which is where an
   adjustment is written, and it re-runs when the loop target is at or before it).
2. **Never adjustable** as a plain setting: any name containing egress, provider, model, spend, credit, budget, take,
   prompt, voice, maxClips, transcription, vision or segmentation; every config setting of VideoGenerate,
   AudioGenerate, Voiceover, ThumbnailGenerate, DesignGraphic, AudioCleanup and Revise; anything not on the list.
   The first four of those step types (and the scriptwriter) are changed through a change request instead (below).
3. **Snapshot only.** Applied changes are written to the in-memory steps and to the run's engine-taken snapshot rows
   (`execution.SnapshotDefinitionId`, `WorkflowDefinitionKind.ExecutionSnapshot`). A run without its own snapshot
   (a hidden system workflow, a composition replay) refuses every change. The saved workflow is never read for
   writing. Resume reloads the snapshot and keeps the change; Retry re-runs the same snapshot and also keeps it
   (open question below).
4. **Re-entry.** The loop re-runs from `min(loopTarget, earliest changed step)`. Since 2026-11-04 any step may be
   re-entered, paid ones included (the `RunRateClassifier.IsStepFree` restriction is gone): the changed step and every
   step between it and the loop target that depends on it (`WorkflowStepDependencyGraph.DownstreamOf`) run again.
   Steps in that stretch that do not depend on a change are reused: a copied row (`reused_from_step_result_id`), no
   hold, no charge, no cache write, exactly like a replay copy. A transcribing analysis's `maxOutputChars` change is
   accepted (one more ASR charge, on the run).
5. **Billing.** No new hold type: every re-run step takes its ordinary one hold per step result at dispatch and is
   settled at what it metered, like every loop pass (allowlisted settings do not enter any step's cost profile).
   Bounded by `MaxIterations` (at most one re-run per step per loop-back) and the credit gate: a hold the gate refuses
   inside the loop window of a review with adjustments on (`CreditRefusalException`) stops the LOOP, never the run:
   the refused step's row is recorded failed (`INSUFFICIENT_CREDITS`, nothing charged), the history of the
   best-scoring pass so far is restored, the run continues after the review and finishes `Passed` with the review
   attention (as an exhausted loop). With adjustments off a refusal fails the run as before. A changed config changes
   The same holds for a paid re-run refused by a spend cap rather than the gate (VideoGenerate or AudioGenerate
   `BUDGET_EXCEEDED` on a daily, global or step cap, `PLAN_NOT_ENTITLED`, the run rate): the finished film is kept and
   the kept review records `adjustmentRerun.refused {stepOrder, stepType, code, message}` plus a `review_rerun_refused`
   warning.
   A changed config changes
   that step's cache key, which is correct; a step carrying out a change request neither reads nor writes the cache.
6. **Visibility.** The reviewer sees `[ADJUSTABLE SETTINGS]` (≤4,500 chars): each eligible step's settings with
   current value and range, and each step that takes a change request marked `CHANGES` with its items (id and a few
   words, at most 12) and its keys with the customer catalogue's limits. Its `adjustments` answer is replaced in the
   review output by the engine's record `[{stepOrder, setting, from, to, reason, status: applied|refused, why}]` (a
   change-request entry adds `kind: "change"` and `item`), plus `adjustmentRerun {fromStepOrder, reusedStepOrders,
   scope, stepChanges?}` (`stepChanges` carries each validated request as canonical JSON, so a resumed run carries out
   the same changes) and one run-page warning (`review_adjusted_setting`) per applied change. The review that follows
   an adjusted re-run records `previousAdjustmentRerun {fromStepOrder, steps: [{stepOrder, stepType, outcome:
   changed|rerun|reused|stopped, creditsCharged}], creditsCharged}`: what the re-run did and what each re-run step was
   charged. That record is the data a later "apply to my workflow" button needs. At most 6 proposals per round
   (settings and change entries together).
7. **Default off; on in every shipped template.** `AllowAdjustments` defaults to false (no block, proposals ignored,
   output unchanged; `QaByteIdenticalFallbackTests` pins the record defaults). Every shipped template's review loop
   carries `WorkflowTemplateCatalog.ShippedReviewLoopConfigJson` =
   `{"version":1,"allowAdjustments":true,"visualCheck":{"enabled":true,"timelineFacts":true,"audioFacts":true,"targetedLooks":true}}`
   (frames default 8; the decision early stop `enabled` of the loop stays false; QA pins exactly this shape). The
   recipe estimate tests price the vision calls at the fixture chat model's token prices.

## Change requests inside the run

Product owner's decision of 2026-11-04: the reviewer may re-run any step and ask for the same changes a customer can
request (`docs/change-requests.md`), carried out INSIDE the current run as a review-loop re-run, never a new linked run.

- **One catalogue.** On a step whose `StepChangeCapabilities.For(type, agent)` is Supported (Voiceover lines,
  VideoGenerate shots and takes, AudioGenerate takes, ThumbnailGenerate, the scriptwriter's scenes), each reviewer
  entry `{stepOrder, item, setting, value, reason}` is one override (`setting` = catalogue key, `item` = item id or
  `*`) or the script's `instruction` (needs an `item`). The entries for one step become one `ChangeRequestPayload`
  validated by the customer `ChangeRequestValidator` against the step's latest output in this run: same keys, same
  limits, same messages. Values arrive as text and are converted per key type (number, flag, pronunciation list as
  JSON or `term=spelling; ...`).
- **Never.** A key containing prompt, egress, provider, model, spend, credit, budget, maxclips or takes is refused
  (`ReviewAdjustableSettings.ForbiddenChangeKeyReason`): a review never writes a prompt (the VideoGenerate/AudioGenerate
  `prompt` override a customer may use is refused for a reviewer), never turns on `allowSourceMediaEgress`, never
  changes a provider or model, never raises a spend cap or the take count. A reviewer cannot change the voice
  (`voiceId` is refused; a customer's voice change stays consent-guarded by the Voiceover executor). Agent definitions and system prompts are never touched.
- **Same executor path.** At the loop-back the engine builds a `StepChangeRequestScope` (previous output = the step's
  latest output in this run, artifact key only under the project's own `agentFiles/` prefix) and, on the step's next
  dispatch, leases it in `ReviewStepChangeRegistry` for that dispatch only; `StepChangeRequestContext.FindAsync`
  returns it before looking for a customer request, so every `IChangeRequestAwareStepExecutor` applies it exactly as
  it applies a customer request (carry-over, `CHANGE_NOT_APPLIED`, consent checks). The hold is the step's normal
  full hold (no `scope_fraction` scaling: that belongs to a customer replay), settled at what it metered. The
  dispatch bypasses the step cache.
- **Later rounds.** A step that carried out a review change keeps that result in a later round of the same loop
  (a copied row, free) while none of the steps it reads ran live in that round; when one did, the step runs normally
  (its inputs changed, so the earlier change no longer applies) and the reviewer may ask again.
- **Retry/Resume** keep adjusted values (snapshot), and a resume re-reads `adjustmentRerun.stepChanges` to carry out a
  change whose dispatch had not run yet.

## What it can cost (estimate and hold)

Choice: the engine does NOT hold the worst case up front; the shown estimate includes it as "up to".

- Engine: each re-run is one more dispatch with its own ordinary hold (p90 / cold-start plan figure for that step),
  reserved at dispatch and settled at what it metered. `RunCreditHolds` / `RequiredMillis` are unchanged, so the
  API/engine hold parity tests (`ResolutionHoldParityTests`, the template estimate tests) stay as they were, and the
  credit gate bounds the run at every dispatch (a refusal stops the loop, above).
- API estimate: `ReviewAdjustmentCeiling.ExtraRunsByStepOrder` counts, for every review loop with adjustments on, the
  steps BEFORE its loop target a review may change (allowlisted settings or a change request) plus every step between
  them and the loop target that depends on them, each `MaxIterations - 1` times (steps inside the loop window already
  repeat with the loop model). `WorkflowCostEstimator` adds `Σ extraRuns × max(hold, fixed price)` to
  `FixedMaxMillis` (the "plus up to" ceiling) and the reason `review_step_reruns`; the web says "Plus up to N credits
  if the review re-makes earlier steps (a narration line, a generated shot, the script) and for any generated video".
  Typical/low/high and the 402 preflight are unchanged.

## Seeing the film

New `visualCheck` members, each off by default and independent of `enabled`:

- `timelineFacts`: `[TIMELINE FACTS]` (≤6,000 chars) from the compile's timeline manifest (cuts with output in/out,
  source range, shot, look, transition; overlays with text, style and placement; SFX cues with start, length, gain;
  music bed and ducking; inserts), the compile's settings (fit, overlay size, caption size, SFX cap, music gain,
  transitions, target length) and its output nodes (output, length, loudness, programFade, transitions, music,
  audio, region effects, reframes). First-party only; quoted text is marked as content.
- `audioFacts`: `[AUDIO FACTS]` (≤2,500 chars) from one ffmpeg `ebur128=peak=true` pass in the engine: integrated
  loudness, loudness range, true peak and probable clipping, silences (momentary < -50 LUFS for ≥0.8 s), loudness
  at each SFX cue against the second before, loudness jumps ≥6 LU at cuts. No audio leaves the host. Narration is not
  transcribed: no configured capability lets a review model listen to audio (Transcription would be a new paid call),
  so the block says so and points at the timeline facts.
- `targetedLooks` (`maxExtraRounds` 0..3, default 2; `maxFramesPerRound` 1..8, default 6): the reviewer may answer with
  `looks: [{atSec: [...], region?: {x,y,w,h}, why}]`; the engine grabs those frames (cropped and enlarged when a region
  is given) with the media workspace, asks the same vision checklist with the reviewer's request quoted, appends
  `[TARGETED LOOK n]` and asks the reviewer again. Findings join the `visualCheck` node, so a high finding caps the
  score at `failScoreCap` exactly like the sampled check; each round is recorded under `targetedLooks`. Before each
  round the step's metered spend plus one average pass must fit `context.CreditHoldMillis`, else the round is skipped
  with a recorded reason. The Stills derivative was not reused: it is dispatched asynchronously by the API and cached
  per source, while a review needs the frames inside the step; the review's own pinned-local workspace is used instead.
- Cold-start hold (`StepUsageProfiles.For`): unchanged when off; with looks on it adds, per round, 2 vision calls ×
  (600 + frames × 4,000) input and 2 × 3,000 output on the vision target, plus one more copy of the reviewer's own chat
  quantities.

## Open questions

- Resolved 2026-11-04: a review may re-run a transcribing analysis (one more ASR charge) for its budget. Re-deriving
  the view from the stored artifact would still be cheaper (a follow-up in VideoAnalyze).
- Should Retry start from the saved settings instead of the adjusted snapshot? (Kept: Retry and Resume keep them.)
- Should an applied adjustment also add a run-level note (not only the step warning)?
- The credit-stop restores the best pass in the run's history; rows of the stopped partial pass stay on the run page
  (the refused step shows `INSUFFICIENT_CREDITS`). Superseding them is not done.
