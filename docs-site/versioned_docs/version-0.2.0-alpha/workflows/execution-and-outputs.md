---
title: Running workflows and finding the results
sidebar_position: 4
description: What happens when you run a ReelBolt workflow, how to follow and stop it, how retries and review loops work, and where videos and other results end up.
---

# Running workflows and finding the results

This page explains what happens after you press **Run** on a workflow: how the run is queued, what
the step statuses mean, how retries and review loops behave, how to stop a run, and where the
finished video and the other results end up.

## Starting a run

You start a workflow from its page in the project (Workflows tab, open the workflow, click
**Run**). A run never starts on a single click: a confirmation box opens first.

- The box lists every step in plain words ("Watches your footage", "Writes the narration") and
  roughly how long the run takes, based on your earlier runs of the same workflow.
- It always offers **Your brief**: free text passed to every AI agent in the run as extra
  instructions ("keep it under a minute", "show our pricing page in the phone screen"). It is
  optional unless the workflow has **Require a brief before each run** switched on.
- It also offers an **Editing style** (how the AI should cut the video) and a one-click **Video
  shape** (9:16, 1:1, 4:5 or 16:9) for this run only; see
  [Editing styles and video shape](./editing-styles.md).
- Next to the shape, **Resolution** sets how sharp the finished video is: **Auto**, **720p**,
  **1080p** or **4K**. Auto gives the usual size: up to 1080p, or your plan's limit if that is
  lower. Choices above your plan are shown disabled, and a higher resolution uses more render
  credits; see [Export resolution on each ReelBolt plan](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan).
- Click **Start run**. The run appears in the workflow's **Run history** immediately, numbered
  **Run 1**, **Run 2**… with the first line of its brief, and ReelBolt opens its live run page.
- If the workflow contains a `VideoGenerate` step (paid AI video clips), ReelBolt asks whether to
  **reuse previously generated clips** (free, the default) or **Regenerate** them (costs money). The
  dialog shows the most it could cost: the sum of the video steps' spending caps.

You can also ask the [assistant](../assistant/index.md) to run a workflow for you.

## The queue and run statuses

A new run is first **Queued**, then **Running**. The platform runs a limited number of workflows at
the same time (4 by default, set by the administrator); extra runs wait in the queue and start by
themselves when a slot frees up. A run can take from minutes (a short edit) to over an hour (a promo
whose agents write and render a lot of code).

A run ends in one of these statuses (the run page shows them as **Finished**, **Failed** and
**Stopped**):

| Status | Meaning |
|---|---|
| `Passed` | Every step finished. |
| `Failed` | A step failed (after any automatic retries), so the run stopped there. The error is shown on the run page. |
| `Cancelled` | The run was stopped, by you or by the system. The run page says who or why. |

### Needs attention

A finished run can show an amber **Needs attention** badge instead of **Passed**. An amber note on
the run page explains why, for example: "The review stopped after 3 rounds at 6/10; the target was
8/10." The film is ready but did not reach the quality bar. You can still preview and download it,
or ask for changes and run it again.

## Step statuses

Each step in a run shows its own status:

| Status | Meaning |
|---|---|
| `Pending` | Not reached yet. |
| `Running` | Working now. Long steps show live progress messages (for example "Tracking insert regions" or "Encoding"). |
| `Completed` | Finished. Click it to see what it produced. |
| `Failed` | Did not finish; the error details explain why. |
| `Skipped` | Not run because its agent could not be found — for example a step using a custom agent (custom agents cannot run in workflows). |

A step that was reused from an earlier identical run is marked as coming **from cache** (see "Reusing
earlier results" below).

## Following a run live

The run page updates live while the run is going: step statuses change, progress messages appear for
long steps (video analysis, encoding, clip generation), and room steps (`EditRoom`,
`GraphicsRoom`, `ColorGradeRoom`) show each panel member's turn as it happens. Progress messages are
for information only; a step is only done when its status says `Completed`.

The top of the run page names the run ("Run 3 · 4 Oct, 14:02"), says in plain words what is
happening now ("Writing the narration…"), and estimates the time left from how long the finished
steps took — an estimate, since rendering steps usually take longer than the rest. If any step
reported a problem you should know about (narration lines that could not be placed, no music
because the project has no audio files, a video much shorter than the length you asked for, a
narration voice that is not working), a yellow box at the very top says so, even when the run
finished. Token counts, the live-connection state and each step's raw data are under **Details**.

## Automatic retries

ReelBolt retries some failures by itself before giving up:

- **AI agent steps** (`Agent`, `ReviewLoop`, `ForEach`, `Parallel`) and `ColorGradeRoom`,
  `VideoGenerate` and `Voiceover` steps get **up to 3 attempts** by default (the administrator can
  change this). When an agent's answer has the wrong shape, the retry tells the agent what was
  wrong. Waits between attempts grow a little each time.
- **An agent that runs out of time is not retried**, because a retry would just repeat the same
  long work. The error says it timed out; an operator can raise that agent's time limit.
- `Extract`, `VideoAnalyze`, `VideoCompile`, `EditRoom` and `GraphicsRoom` steps are **not retried
  as a whole**: they would fail the same way again, or redo minutes of video processing for
  nothing. (Speech-to-text inside `VideoAnalyze`, and the director's final write-up inside the room
  steps, have their own small retries.)
- If an agent deliberately stops the run (its "stop the run" tool), there is no retry.

When a step still fails after its attempts, the whole run is marked `Failed`.

## Review loops

A `ReviewLoop` step is a quality check. A reviewing agent scores the work out of 10:

- At or above the step's passing score (`minScore`), the run continues.
- Below it, the run **jumps back** to an earlier step (`loopTargetStepOrder`) and redoes everything
  from there, with the reviewer's comments handed to the redone steps.
- After `maxIterations` rounds the run **continues anyway** with the latest result; a low final
  score does not fail the run.

### Needs attention

When the review stops below its passing score, the run still finishes as `Passed` and the film is
yours to use, but the run page shows **Needs attention** with this note: "The review stopped after
*N* rounds at *S*/10; the target was *M*/10. The film is ready but did not reach the quality bar."
The assistant reports such a run the same way instead of calling it a success. Credits are
unchanged: the run is billed exactly as any other finished run, and its outputs are all kept. If
several reviews stop short, the lowest score is shown. Retrying or resuming the run clears the note
and sets it again only if the new attempt also stops short.

In the footage templates the loop goes back to step 2 (the editing decision), so the analysis is not
repeated; in the promo templates it goes back to the `AuthorAgent` step, which re-renders.

## Stopping a run

Press **Stop** on the run page while it is running. The run stops at the next safe point and ends as
`Cancelled`, recorded as stopped by you. Work already finished (for example a clip already generated)
is kept.

## Editing or deleting a workflow never changes a run

When a run starts, ReelBolt keeps its own copy of the workflow as it was at that moment, and the run
uses that copy from start to finish. Editing the workflow while a run is going, or after it, does not
change that run, and neither do changes to the agents it uses. Resume and Run again also use the
run's own copy, never your later edits. To use a change, save it and start a new run.

You can delete a workflow while none of its runs is going. Its earlier runs stay in your history
under the workflow's name. If a run is still queued or running, deleting the workflow is refused
until the run ends.

## Resuming a run

When a run ended as `Failed` or `Cancelled`, **Resume** on the run page carries on from where it
stopped instead of starting over.

- **What is kept.** Steps that already finished are not run again, and they are not charged again.
  Resume picks up at the first step that did not finish, and everything from there on runs.
- **Groups of steps.** A loop, a group of parallel steps, a room or a review-loop round is kept only
  if all of it finished. Otherwise the whole group runs again.
- **A missing result.** If a finished step's saved file is no longer available, that step runs again.
- **Credits.** Resume checks your balance for the steps that still have to run, and refuses if it is
  too low. Your plan's limits apply as they do for a new run.
- **Paid video clips.** A clip that was already generated is kept. If an earlier attempt may have
  ordered a clip that never came back, Resume orders it again and you are charged for it once.
- **The same run.** The run keeps its start time and its place in your history.
- **The failed attempt.** The results of the attempt that did not finish, from the first step that
  runs again, are no longer shown on the run page. They stay in your records and credit history.
  Pressing **Resume** again later never charges you twice for the same step.
- **Pressed at the same time as someone else.** If another person on the project pressed **Resume** or
  **Run again** first, or deleted the workflow, you see a message saying what changed. Reload the page.
  Nothing is started or charged twice.

Resume is not available when:

- the run is still going, or is still stopping after you pressed Stop (try again in a moment);
- the run is older than this feature. Use **Run again** for those runs;
- you do not have edit access to the project, or your email is not verified.

If a run is lost because the system restarted, ReelBolt resumes it once on its own. It does not do
that while a video clip it ordered is still waiting to come back; those runs are marked `Failed` and wait
for you to press **Resume**, so you are never charged twice for the same clip.

## Retrying a run

When a run has ended (`Passed`, `Failed` or `Cancelled`), **Run again** on the run page starts the same
run again **from step 1**, with the same request text, style, shape and resolution, and the same copy
of the workflow the run used. Later edits to the workflow are not picked up. Steps whose inputs did not change may be
reused from cache, so a retry is usually faster than the first run. Steps that have to run again are
charged again; steps whose earlier result can be reused from cache are not charged again. If the workflow generates paid
clips, Run again asks again whether to reuse them or pay for new ones.

The results of the earlier attempt are no longer shown on the run page, but they are kept for your
records and your credit history. Run again checks your plan and your credit balance the same way
starting a new run does.

Stop cancels the step that is running; it does not let it finish. After you press Stop, Run again is refused until the
run has fully stopped, which usually takes under a minute. Until then it says the run is still stopping.

If the workflow was deleted and the run is too old to keep its own copy, Run again is not available;
start a new run from a workflow instead.

To change something before trying again, edit the workflow, save, and start a new run.

## Where the results end up

Different steps produce different kinds of results. Click any completed step on the run page to see
its result panel.

- **Videos.** A step that produced a video shows it in a **Video from this step** player on its
  result panel. All videos produced in the project also appear in the project's **Renders** tab (and in
  "Latest renders" on the Overview). These include the finished promo from the `AuthorAgent` step,
  the edited video from a `VideoCompile` step, generated clips from a `VideoGenerate` step, and the
  graphics or insert scenes a `MotionGraphicsPlanner` step rendered.
- **Edited video as a project file.** By default a `VideoCompile` step also adds its result to the
  project's **Files** (`registerProjectFile`, on by default), so you can use it as the source of a
  new edit. Each run keeps one file there: when a review loop re-edits the video, or a step is
  retried, the newer video replaces the older one in **Files** (a workflow that always uses the same
  file name also replaces the previous run's file). The older videos are not deleted; you still find
  them on their run's page and in **Renders**.
- **Detailed records.** `VideoAnalyze`, `VideoCompile` and `VideoGenerate` steps also keep a large
  detailed record (the full analysis, the full edit list, or the list of generated clips), shown
  on the step's **Data** tab, which appears once you open **Details** on the run page. These are information, not videos.
- **Timeline.** A `VideoCompile` step's result offers **Open in Editor**, an interactive timeline
  that shows the cuts, graphics and audio of the edit.
- **Summaries.** Each step's text result is shown in its panel. For a `VideoCompile` step it says
  what was applied and what was skipped and why (for example a graphic or insert that could not be
  used), so check it when something you expected is missing from the video.
- **Room discussions.** The room steps keep their panel's discussion so you can read how the
  decision was reached. It is for information only.
- **Changes to an animated video.** A finished run that made an animated video has a **Request
  changes** panel on its page: describe what should change and ReelBolt makes a new version without
  re-running the workflow. See [Request changes to a finished video](request-changes.md).

## Where earlier takes of a video went

Every time a workflow runs again, or a review loop asks for another edit, the video is made again.
Each version is a **take** of the same video. So that the newest video is easy to find, the
project's **Renders** tab and the **Latest renders** on the Overview show each video only once, by
its latest take: the newest version from a run that has finished. A run that is still going never
replaces it until it finishes.

The earlier takes are not lost:

- On the **Renders** tab, under the video you are watching, click **N earlier takes**. Each earlier
  take can be played and downloaded. "In-progress take" marks a version the run replaced on its
  way to the final one (for example a review-loop pass).
- In **Files**, earlier takes of your videos are hidden; click **Show N earlier takes of your
  videos** to list them too.

**Cleaning up.** Click the pin next to a take to keep it. **Delete earlier takes** deletes every
earlier take of that video at once (after you confirm); the latest take and pinned takes stay. You
can also delete one earlier take with its bin icon. Deleting a take removes the video file and its
entry in Files for good; the run itself, and what each step reported, stay in the run's history.
The one exception is a video file that a version of your composition still uses. ReelBolt keeps that
file, and its entry in Files, so the composition keeps playing.

## Reusing earlier results (the step cache)

ReelBolt can **reuse a step's earlier result** instead of redoing it, when the step and everything
it reads are exactly the same as in an earlier successful run (same settings, same earlier results,
same request text, same project files). This saves time and AI cost, and for `VideoGenerate` steps
it saves money.

- Reused by default: `Extract`, `VideoAnalyze`, `VideoCompile`, `Voiceover`, `EditRoom`,
  `ColorGradeRoom`, `VideoGenerate`, and `Agent` steps whose agent only reads.
- Never reused: agents that write code, write files or render (`RemotionComponentTranslator`,
  `AuthorAgent`, `MotionGraphicsPlanner`), `GraphicsRoom`, `ReviewLoop`, `Conditional`, `ForEach`,
  `Parallel`.
- Each step's `cacheMode` overrides this: `Never` always redoes the step, `Always` always allows
  reuse. The templates set every creative AI step to `Never`, so a re-run gets a fresh decision.
- A reused step shows as coming from cache on the run page.

Because one changed input changes everything after it, editing an early step's settings makes the
later steps redo their work automatically.

## Paid video clips: reuse or regenerate

`VideoGenerate` steps cost money, so ReelBolt avoids paying twice:

- **Reuse is the default.** When a clip with the same prompt and settings was already generated
  successfully (in this run or an earlier one in the project) and still exists, it is reused at no
  cost. This also applies when a review loop sends the run back.
- **Regenerate** when you run or retry the workflow to pay for fresh clips. The dialog shows the most
  it could cost.
- A step's `loopBackBehavior` set to `"Regenerate"` pays for fresh clips on every review-loop round
  after the first; the default `"Reuse"` does not.
- A step's `cacheMode` set to `"Never"` only stops the whole step from being replayed; individual
  identical clips are still reused unless you choose **Regenerate**.

Spending is always capped: each step's `maxSpendCredits`, plus daily limits per project and for the
whole platform set by the administrator. If a cap would be exceeded, **nothing** is bought and the
step fails with `BUDGET_EXCEEDED`. See the [generated b-roll recipe](../recipes/generated-b-roll.md).
