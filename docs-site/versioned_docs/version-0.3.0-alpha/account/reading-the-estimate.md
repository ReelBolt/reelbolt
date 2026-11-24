---
title: Reading the estimate
sidebar_position: 8
description: What the cost line before a workflow run means, where its figures come from, how generated video and a wide range are shown, what must be available to start a run, what is free, and what happens when the balance runs low.
---

# Reading the estimate

This page explains the cost line that ReelBolt shows before you start a workflow run: what the typical cost and the range mean, where the figures come from, how generated video is shown, and what must be in your balance before the run can start. Read it before you start a run to decide whether to top up or to trim the workflow. For how credits are granted and spent, see [Plans and credits](./plans-and-credits.md).

## Read the estimate

Before you run a workflow, ReelBolt shows what the run should cost, for example **This run should cost ≈ 40 credits (usually 30–52)**.

- **≈ 40 credits** is the typical cost: what a run like this usually comes to.
- **usually 30–52** is the range most runs land in. Some runs cost less or more than that, because a model can write more or less, or a review step can ask for another pass.

If the lowest and highest figures are the same, only the typical figure is shown.

## Where the figures come from

A line under the estimate says what it is based on:

- **"First run, so this is a rough estimate from typical figures for these steps."** Your workspace does not yet have enough finished runs of this workflow or these steps, so ReelBolt uses what each kind of step usually costs. Treat it as a guide.
- **"Based on what these steps have cost in this workspace."** Steps like these have run in your workspace before, and their actual cost is used. For steps that render the video, runs at each resolution are counted separately, so a 4K estimate uses earlier 4K renders once there are a few of them.
- **"Based on N past runs of this workflow."** Once the workflow has finished successfully a few times at the resolution you chose since you last changed it, the estimate comes from what those runs actually cost. Changing the workflow starts the count again, because the older runs no longer describe it.

## Generated video is shown separately

A workflow that generates video clips adds a line such as **Plus up to 300 credits for generated video, priced at the most clips it can order.** Generated video has a fixed price per clip, so ReelBolt states the most it can cost rather than guessing. If a review step can send the work back for another pass, the line also says the video can be ordered again, and the amount already includes those extra passes.

## Why a range can be wide

When the top of the range is at least twice the bottom, the estimate lists the reasons under **Why the range is this wide:**. The reasons are:

- the review step can send the edit back for another pass;
- analysing and cutting footage costs more for longer clips;
- a repeating step runs once per item, and the number of items is known only during the run;
- steps that use your own AI provider are not charged.

## What must be available to start a run

Starting a run needs enough credits available to cover what ReelBolt holds for each step before that step runs. This amount is worked out separately from the estimate, so it can be higher or lower. On a first run it is at least the low end of the usual range, and often more, because each step holds at least a standard minimum for its kind.

A higher resolution raises this amount for the steps that render the video. A lower resolution never reduces it. The estimate shows it too, for example **Your workspace has 500 credits available, which covers the 120 this run holds before it starts.**

If the workspace does not have enough, you see **Not enough credits for this run** with the amount needed. Then either top up, or trim the workflow.

## When the run may need more credits than it has

If your workspace has enough credits to start the run but fewer than the run typically costs, including anything listed as "Plus up to", you see **You may run out of credits partway through this run.** Runs often cost more than the amount needed to start, so top up first to avoid the run stopping halfway.

## What is free

Some work costs nothing, and some work uses credits. You are charged for what a run actually uses, not for what was held before it started.

- **Free:** file summaries, indexing and search, storage (up to your plan's storage limit), work through an AI provider your workspace connected itself, and work on a computer your workspace paired itself.
- **Uses credits:** workflow steps that use ReelBolt's providers, AI picture tools, timeline suggestions, the assistant once your monthly allowance is used up, designed graphics, and generated video.

## If a run fails

If a step fails because of the platform, the credits it was holding are refunded, so you are not charged for a step that was ReelBolt's fault. If it fails because of your input, because you stopped it, or because a provider your workspace connected itself failed, the credits it actually used are kept. ReelBolt decides this from what it observed.

## What happens when the balance runs low

- **The work stops rather than overspending.** A step that needs more credits than are available is refused before it starts, so you are never charged past your balance without topping up.
- **Top up** from **Settings → Plan and billing**, under **Buy credits**. Only the workspace Owner and members with the Billing role can buy credits.
- **Move to a bigger plan** whose monthly credits fit your usage. See [Managing your plan](./managing-your-plan.md).

## Related pages

- [Plans and credits](./plans-and-credits.md): how credits are granted, spent and topped up.
- [Choosing a resolution](./choosing-resolution.md): how the resolution you choose changes the estimate.
- [What the assistant costs](./what-the-assistant-costs.md): the cost of chat and Claude Code work.
