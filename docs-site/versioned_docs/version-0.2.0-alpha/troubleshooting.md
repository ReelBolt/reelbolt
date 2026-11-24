---
title: Troubleshooting
sidebar_position: 14
description: Common ReelBolt problems and error messages, what causes them and how to fix them.
---

# Troubleshooting

Each section below covers one problem: what you see, why it happens and how to fix it. Error codes in capitals (such as `BUDGET_EXCEEDED`) are shown in the failed step's details on the execution page; click the red step in the **Steps** diagram to read them (open **Details** on the run page to see each step's raw data). You can also ask the [assistant](./assistant/index.md) "Why did my last run fail?" on the run's page.

## "Content search is not available yet" when searching project files

**Symptom.** On a project's **Files** tab, **Search inside files for "…"** shows **Content search is not available yet**. Through the API, the search answers with `indexNotReady: true` and no results.

**Cause.** Either no Embedding provider is set up for the installation, or the project's files have not been indexed (made searchable) yet. Files uploaded before a provider existed are marked `Failed` in their **Search index** status.

**Fix.** Ask an administrator to add an Embedding provider (see [Semantic search](./admin/semantic-search.md) and [Inference providers](./admin/inference-providers.md)). Then, on the **Files** tab, open **⋮ → Reindex for search** and **Queue reindex** (25 files per batch; repeat for more). Name filtering keeps working in the meantime.

## Search results missing after switching embedding models

**Symptom.** Search-by-meaning worked, an administrator changed the default Embedding provider or its model, and now searches return little or nothing even though files show `Indexed`.

**Cause.** Each embedding model has its own separate index, and the new one starts empty. Files indexed with the old model still say `Indexed`, so the bulk **Reindex for search** action skips them.

**Fix.** Use **Reindex** on each file you need, or have someone with API access reindex the whole project with `includeIndexed: true`. Details are in [Semantic search](./admin/semantic-search.md).

## TRANSCRIPTION_UNAVAILABLE or TRANSCRIPTION_FAILED in a video analysis step

**Symptom.** A `VideoAnalyze` step fails with `TRANSCRIPTION_UNAVAILABLE` ("Transcription is Required but no transcription-capable inference provider is configured.") or `TRANSCRIPTION_FAILED`.

**Cause.** The step's transcription setting (`transcription`) is `Required`, which tells ReelBolt to stop rather than edit without a transcript. Either no speech-to-text (`Transcription`) provider is set as default, or the one that exists returned an error.

**Fix.** Either:

- ask an administrator to add a `Transcription` provider and make it the default (the bundled local `whisper` service works; see [Inference providers](./admin/inference-providers.md)), and click **Test connection** to confirm it works; or
- change the step's `transcription` setting to `Optional` (the default), which carries on without a transcript when none is available, or `Off`.

`VISION_UNAVAILABLE` is the same situation for on-screen descriptions: the step's `vision` setting is `Required` but no `Vision` provider is the default.

## Anthropic, Gemini or DeepSeek cannot be used for transcription

**Symptom.** When saving or testing a provider, ReelBolt refuses with a message such as "Anthropic providers cannot serve the Transcription capability — Anthropic exposes no speech-to-text API", and the **Capability** list does not offer **Transcription (ASR)** for these kinds.

**Cause.** Anthropic (Claude), Google Gemini and DeepSeek do not offer a speech-to-text service that ReelBolt can use. The same applies to some other pairings: video generation is MiniMax only, and embeddings need Azure OpenAI or an OpenAI-compatible service.

**Fix.** Keep your Anthropic, Gemini or DeepSeek connection for `Chat` (and `Vision`), and add a separate `Transcription` provider of kind **Azure OpenAI**, **OpenAI-compatible** (such as the bundled `whisper` service), or **Fish Audio**. See the table in [Inference providers](./admin/inference-providers.md).

## BUDGET_EXCEEDED in a video generation step

**Symptom.** A `VideoGenerate` step fails with `BUDGET_EXCEEDED`, and no clips were created.

**Cause.** Before buying any clip, ReelBolt checks three spending limits, and at least one would have been exceeded:

- the step's own **Max Spend** cap (`maxSpendCredits`), compared with the estimated cost of every clip the step would create;
- the project's daily budget (the credit equivalent of 20 US dollars per UTC day by default);
- the installation-wide daily budget (the credit equivalent of 50 US dollars per UTC day by default).

Nothing is bought when this happens, so you have not been charged.

The error now says which limit stopped the step. For example: "This needs about 120 credits of video generation but the project's daily video budget is 833.33 credits and 790 are already used today; it resets at 00:00 UTC (in 5 h 12 min). Generate fewer clips or wait." It names the limit ("this step's spending limit", "the project's daily video budget", "your plan's daily video budget" or "the platform's daily video budget"), what is already used today and when the day resets. If the platform's daily limit was reached, it only tells you that and when it resets. When you ask the assistant about the run, it can also report how much video budget is left for the project today.

**Fix.**

- If the error says the plan's estimated cost exceeds the step limit, raise `maxSpendCredits` on the step, or ask for fewer or shorter clips (fewer takes, a shorter duration, or a lower `maxClips`).
- If the message says a daily video budget is reached, wait until it resets at 00:00 UTC (the message says how long that is), or ask whoever runs the installation to raise the daily budgets.
- When rerunning, choose **reuse previous clips** instead of **Regenerate (costs money)**, so clips that were already made are reused for free.

## A run is refused because the workspace is out of credits

**Symptom.** A run is refused before it starts — or a step that needs more credits than are available is refused — and the message names a plan or credit limit. The run did not start, and nothing was charged.

**Cause.** The workspace's plan does not have enough available credits for the work, or the plan does not include the kind of step the run wants (an editing "room" or generated video on a plan without it). ReelBolt refuses before the step runs rather than overspend, so a run that cannot be afforded never starts and nothing is charged.

When the message reads "This run needs up to 788 credits to start (it usually costs about 286); the workspace has 500 available, so 288 more are needed. Credits that aren't used are returned when the run finishes.", the first figure is the most the run can set aside before it starts, the figure in brackets is what it usually costs, and the last figure is how many more credits you need to start it. A run often costs less than the amount it needs to start, and any credits it does not use are returned when it finishes. This applies to a run you start from the dashboard, from the assistant and from Claude Code, and to a change to a finished video.

**Fix.**

- On a paid plan, top up credits from **Settings → Plan and billing → Buy credits**, or move to a bigger plan whose monthly credits fit your usage (see [Plans and credits](./account/plans-and-credits.md)).
- If the refusal names a feature the plan does not include (rooms or generated video), upgrade to a plan that has it, or change the workflow to a step type the plan does allow.
- On a self-hosted install with billing off there is no balance to run out — the billing pages are simply switched off, so this error would not appear there.

## A step fails with NO_RUNNER_ONLINE

**Symptom.** A run stops at a media step with the error `NO_RUNNER_ONLINE`, and the step does not run.

**Cause.** The workspace's compute preference is **Only my machines** (the "Only my machines" choice in **Organization → Settings → Where your work runs**), and no paired machine was online and able to take the step. That choice never falls back to the cloud: the run stops there instead of quietly billing the cloud. A **Prefer my machines** or **Automatic** workspace would instead have sent the step to the cloud pool, which has **no workers**, so such a step would sit in a queue that nothing picks up, rather than fail this way. See [Desktop runner](./account/desktop-runner.md).

**Fix.**

- Start (or leave on) the paired machine you want to run the work, make sure it is paired to this workspace on **Runners**, and run the step again.
- If you do not need to force it onto your machine, change the preference to **Prefer my machines** or **Automatic** so a step can run on the platform when your machine is offline.
- If the step is a Remotion render, the machine must have reported a container runtime (Docker or Podman); an ffmpeg-only machine cannot take renders.

## EGRESS_REFUSED in a video generation step

**Symptom.** A `VideoGenerate` step that works from a shot plan fails with `EGRESS_REFUSED` before anything is generated.

**Cause.** The shot plan asked for a generated clip to start or end on a frame taken from your own footage (a `firstFrame` or `lastFrame`). Doing that would mean sending part of your footage to the video-generation service, and the step's `allowSourceMediaEgress` setting is `false` (the default), which forbids it. The whole step is refused so that nothing is bought.

**Fix.** Either set `allowSourceMediaEgress` to `true` on the step if you are happy for frames of your footage to leave your installation, or change the plan so shots do not ask for a starting or ending frame. With permission granted, ReelBolt really does cut that one frame out of the shot the plan named and send it with the clip's request — the clip's record in the step result says whether it went, and the **Footage edit with AI-generated b-roll** template already grants this permission for you.

If the step fails with `KEYFRAME_NOT_SUPPORTED` instead, the reason is different: the video-generation service the step resolved to cannot take an image at all, so ReelBolt refuses before buying anything rather than paying for a clip that would silently drop the frame. Point the step at a provider that accepts an input image.

## INSERTS_REQUIRE_REENCODE, GENERATED_CLIPS_REQUIRE_REENCODE and similar compile errors

**Symptom.** A `VideoCompile` step fails immediately with `INSERTS_REQUIRE_REENCODE` or `GENERATED_CLIPS_REQUIRE_REENCODE`. Related codes with the same cause are `GRAPHICS_REQUIRE_REENCODE`, `MUSIC_REQUIRES_REENCODE`, `SFX_REQUIRES_REENCODE`, `COLOR_GRADE_REQUIRES_REENCODE`, `TRANSITIONS_REQUIRE_REENCODE` and `MULTI_SOURCE_REQUIRES_REENCODE`.

**Cause.** The compile step's `mode` is set to `StreamCopy`, a fast mode that only joins cut pieces without re-rendering the picture. Tracked screen inserts, generated b-roll clips, graphics, music, sound effects, colour grading, transitions and combining several source videos all change the picture or sound, so they need the video to be re-rendered.

**Fix.** Set the compile step's `mode` to `Reencode` (the default), or switch off the feature named in the error (for example `enableInserts` or `enableGeneratedClips`). See [Step types](./workflows/step-types.md) and the [tracked screen insert recipe](./recipes/tracked-screen-insert.md).

## A workflow run seems stuck in Running or Queued

**Symptom.** The execution page shows **RUNNING** for a very long time with no step finishing, or stays **QUEUED**.

**Cause.** Several possibilities:

- **It is just slow.** Agent steps that build and render animation, and analysis of long footage, can take a long time, especially on slower AI services. Each agent step has a time limit (50 minutes by default) after which it fails with "Agent '…' timed out after … seconds".
- **It is waiting for a slot.** Only a limited number of runs work at the same time (4 by default); extra runs wait as `Queued` until one finishes.
- **The system restarted mid-run.** After an update or restart, a run that was working is picked up again automatically within a minute or two. If the workflow service was stopped abruptly, the run can briefly show `Running` even though nothing is working on it until that happens.

**Fix.** Check the **Steps** diagram and the "what is happening now" line (open **Details** for the live token counters): if steps are progressing or tokens are increasing, let it run. If nothing has changed for a long time, click **Stop**, which marks the run `Cancelled`, then click **Resume** to carry on from the step that did not finish (or **Run again** to start over). If agent steps keep timing out on a slow AI service, ask whoever runs the installation to raise that agent's time limit (the `Agents:<AgentName>:RunTimeoutSeconds` setting).

## A media step stays Queued and never starts

**Symptom.** A media step (a Remotion render, a video compile or a video analysis extraction) sits in `Queued` for a long time and never starts, under a workspace whose compute preference would send it to the ReelBolt cloud pool (**Automatic** or **Prefer my machines** with no machine online, or **Never use my machines**).

**Cause.** The ReelBolt cloud pool has **no workers**, so a step that falls back to the pool moves to a queue that nothing picks up. Nothing errors, which is exactly why this is written down here. Work on a paired machine, and work the platform runs itself, are not affected.

**Fix.** Pair a machine to the workspace (see [Desktop runner](./account/desktop-runner.md)) and leave it online, or change the workspace's compute preference so the step runs on the platform. Work only finishes where a paired machine (or the platform itself) can run it — do not rely on a fallback to the pool finishing your work.

## A paired machine is not taking work

**Symptom.** A machine you paired is not receiving media work, and a step that would have run on it fails with `NO_RUNNER_ONLINE` (under the **Only my machines** preference) or waits in a queue the pool cannot pick up (under the other preferences).

**Cause.** The machine's governor has stopped it for a reason it can evaluate, or the machine is simply offline. Run `reelbolt-runner status` on the machine: the line names the reason — `user-active` (you are at the keyboard, in `idle` schedule mode), `on-battery`, `outside-window` (a time window) or `paused` (an explicit pause). A machine whose schedule has stopped it has dropped its connection to the gateway entirely while it is idle, so it is not even holding a connection open.

**Fix.** Press **Resume** on the desktop app's **Home** screen (or run `reelbolt-runner resume`) to clear a pause. If the schedule is what is holding it, Home says so and when it next changes ("work resumes Mon 09:00"); change it under **Settings → When to work** in the app. If nothing responds at all, stop and restart the runner process: a pause is never persisted, so a restart always comes back working. The full settings and defaults are in [Desktop app settings](./account/desktop-runner.md#desktop-app-settings) on the [Desktop runner](./account/desktop-runner.md) page.

## A workflow run was cancelled and you did not stop it

**Symptom.** A run shows **CANCELLED** with the message "Interrupted: the workflow engine shut down while this execution was running." (older runs), or **FAILED** with "Interrupted: engine instance lost".

**Cause.** The workflow service was restarted or shut down (for example during maintenance or an update) while your run was working. Today, ReelBolt puts such a run back in the queue once, automatically, and carries on from the step that did not finish. A run that was already resumed once, or that was in the middle of buying a generated video clip, is not resumed automatically, so that nothing is paid for twice; it ends as shown above. A run you stopped yourself says "Stopped by user" followed by your user id instead.

**Fix.** Click **Resume** on the execution page to carry on from the step that did not finish, without paying again for the steps that did. If Resume is not offered, click **Retry**; steps that already finished may then be reused from the cache.

## Retry is refused with "This run is already running or queued."

**Symptom.** Retrying a run fails with that message.

**Cause.** A run can only be retried once it has finished, failed or been cancelled.

**Fix.** Wait for it to finish, or click **Stop** first and then **Retry**.

## Retry is refused with "This run is still stopping"

**Symptom.** Pressing **Run again** on a run you stopped shows "This run is still stopping. Try again in a moment."

**Cause.** Stop cancels the step that was running; it does not let that step finish. ReelBolt refuses **Run again** until the stopped run has fully stopped, so the new run never overlaps the cancelled one.

**Fix.** Wait for the run to finish stopping, which usually takes under a minute, then press **Run again**. The new run starts again from the beginning. Steps that have to run again are charged again. Steps whose earlier result can be reused from cache are not charged again.

## Resume is refused with "Only a failed or stopped run can be resumed"

**Symptom.** Pressing **Resume** shows "Only a failed or stopped run can be resumed."

**Cause.** Only a run that ended as `Failed` or `Cancelled` can be resumed. A run that is `Queued`, `Running` or `Passed` cannot.

**Fix.** Wait for the run to end, or press **Stop** and wait until it has stopped. A run that passed has nothing to resume; start a new run instead.

## Resume is not available for an older run

**Symptom.** **Resume** is missing or refused on a run, with a message that resume is unavailable.

**Cause.** The run started before ReelBolt kept a copy of the workflow for each run, so there is nothing to carry on from.

**Fix.** Press **Run again**, which starts the run again from step 1. Steps whose earlier results can be reused from cache are not charged again.

## Resume is refused with "This run is still stopping"

**Symptom.** Pressing **Resume** on a run shows that the run is still stopping.

**Cause.** The run's last step has not finished stopping yet. ReelBolt waits for it so the resumed run never overlaps the old one.

**Fix.** Wait a moment, usually under a minute, then press **Resume** again.

## Run again or Resume says the run changed while you asked

**Symptom.** Pressing **Run again** or **Resume** shows a message such as "This run changed while you asked. Reload it and try again", "This run was already queued again", "This run no longer exists" or "This run's workflow was deleted while you asked".

**Cause.** The run changed between the moment you opened the page and the moment you pressed the button. Someone else on the project may have pressed **Run again** or **Resume** first, or deleted the workflow or the run. Nothing was started and nothing was charged.

**Fix.** Reload the page and look at the run's current state. If it is already running, let it finish. If it ended, press **Run again** or **Resume** once more. If the message says the workflow or the run is gone, there is nothing left to run again.

## Resume is refused because of credits or plan

**Symptom.** **Resume** or **Run again** is refused with an out-of-credits message, a message that your plan does not include something the workflow uses, or a message that your plan allows only a number of runs at once.

**Cause.** Both check your plan and then your balance before they start. If the plan's limit on runs at the same time is reached, the message says how many runs your plan allows, and that check comes before the balance check. Resume checks the balance only for the steps that still have to run. That check is an estimate: it can differ a little from what the steps cost, and each step is checked again as it starts.

**Fix.** If your plan allows only a number of runs at once, wait for one of them to finish or change plan. If you are out of credits, add credits. Then try again. See [A run is refused because the workspace is out of credits](#a-run-is-refused-because-the-workspace-is-out-of-credits).

## A video clip step stops with "reservation orphaned"

**Symptom.** A video generation step fails for a clip with `RESERVATION_ORPHANED`.

**Cause.** An earlier attempt may have ordered the clip, but ReelBolt never learned whether the order went through. When you resume or retry, ReelBolt orders such a clip again once. If the same clip is orphaned again after that, it stops to avoid paying for it repeatedly.

**Fix.** Ask whoever runs the installation to check the video service's account for an order near the time of the clip, then resume.

## The assistant says the run limit was reached

**Symptom.** When you ask the assistant (or Claude Code) to run a workflow, it reports "run limit reached — 10 of 10 paid runs in the last hour on this project; try again in 13 minutes".

**Cause.** The assistant limits how many runs it starts for each user in each project over a rolling hour: up to 10 runs that can use credits (a run with an AI model or paid service in it, and every change to a finished video), and up to 60 free runs (only fixed editing steps). The message says which kind you hit, how many you used, the limit, and how long until the oldest run in the hour is old enough to free a place. A free run never uses up the 10. This protects against runaway spending. Whoever runs the installation can change the numbers with the `Assistant:MaxPaidExecutionsPerHour` and `Assistant:MaxFreeExecutionsPerHour` settings.

**Fix.** Wait for the time in the message, then ask again, or start the run yourself with **Run** on the workflow page, which is not subject to this limit. If the message says "This workflow changed after the run started; start it again.", the workflow was edited between the check and the start. Nothing was charged; ask for the run again.

## The assistant's workflow proposal says "Proposal out of date"

**Symptom.** Clicking **Apply changes** on a workflow proposal shows **Proposal out of date** and nothing changes.

**Cause.** The workflow was edited (by you, or in another tab) after the assistant made its proposal. ReelBolt refuses to apply a proposal to something that no longer matches what you were shown.

**Fix.** Ask the assistant to propose the change again, then apply the new proposal.

## No video generation provider configured (PROVIDER_NOT_FOUND)

**Symptom.** A `VideoGenerate` step fails with `PROVIDER_NOT_FOUND` ("No video generation provider configured."). Related codes from the service itself are `PROVIDER_AUTH_FAILED` (the key was rejected) and `PROVIDER_INSUFFICIENT_BALANCE` (the account with the video service has run out of credit).

**Cause.** No `VideoGeneration` provider is set as default and the step does not name one, or the service refused the request.

**Fix.** Ask an administrator to add a MiniMax provider with the **Video Generation** capability, set it as default and test it. For an authentication error, check the API key; for insufficient balance, top up the account with the video service.

## A provider endpoint is rejected as a private address

**Symptom.** Saving a provider fails with "Endpoint must be a public https/http URL; internal/private/loopback addresses are not allowed."

**Cause.** Cloud-only kinds (Azure OpenAI, Anthropic, Gemini, DeepSeek, MiniMax, TypeSafe) must point at a public internet address. Only the self-hostable kinds (OpenAI-compatible, Fish Audio and the self-hosted decision server) may point at a server on your own network or inside the installation.

**Fix.** For a service you host yourself, choose **OpenAI-compatible** (or **Fish Audio** for voiceover) as the kind. For a cloud vendor, use its public address.

## An Anthropic, Gemini or DeepSeek provider fails with no API key

**Symptom.** A provider of one of these kinds fails its test or fails during runs, and its API key field was left blank.

**Cause.** For these kinds ReelBolt never silently uses a key from the server's environment; a blank key is an error.

**Fix.** Enter the API key, or enter the literal value `env:` to use the key from the server's environment variables (`ANTHROPIC_API_KEY`, `GEMINI_API_KEY` or `DEEPSEEK_API_KEY`), which whoever runs the installation must set. See [Inference providers](./admin/inference-providers.md).

## A provider cannot be deleted

**Symptom.** Deleting a provider is refused.

**Cause.** It is the default provider for its capability, and ReelBolt will not leave a capability without its default by accident.

**Fix.** Make another provider the default for that capability first, then delete the old one. Agents that were specifically pointed at a deleted provider go back to using the default automatically.

## A user never received their reset password

**Symptom.** An administrator reset a user's password, but the user did not get the new temporary password.

**Cause.** A reset password is only ever sent by email; it is not shown on screen. If the installation has no email (SMTP) set up, the new password cannot be delivered.

**Fix.** Ask whoever runs the installation to configure email (the `SMTP_*` settings), then reset the password again. New accounts are different: their temporary password is shown on screen in the **User Created** box. See [Managing users](./admin/users.md).
