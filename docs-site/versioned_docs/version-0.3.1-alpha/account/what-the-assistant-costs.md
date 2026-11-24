---
title: What the assistant costs
sidebar_position: 9
description: Which things the ReelBolt assistant and Claude Code do for free, which use credits, how the read-only cost on an approval card works, how the monthly assistant allowance is shared, and what happens when the allowance runs out.
---

# What the assistant costs

This page explains which things the ReelBolt assistant and Claude Code do for free and which use credits, how the cost on an approval card reads, how the monthly assistant allowance works, and what happens when the allowance runs out. It applies to the chat assistant on every page and to Claude Code when it is connected to your ReelBolt account. For the general credit rules, see [Plans and credits](./plans-and-credits.md).

## What is free

- **Looking things up.** Asking about your projects, files, workflows, run history or a picture's layers is free.
- **Chat and tool use within your allowance.** Messages to the assistant and Claude Code tool calls are free until your plan's monthly assistant allowance is used up. Your plan's amount is shown on **Settings → Plan and billing**.
- **A timeline suggestion made with your own AI provider** costs no credits.
- **Work through a provider your workspace connected itself** costs no credits, whichever feature it is for.

## What uses credits

- **Starting a run.** A run spends credits as its steps use paid services. The cost is shown on the approval card before the run starts (see [Reading the estimate](./reading-the-estimate.md)).
- **Paid picture tools.** Erasing, filling, expanding, relighting, upscaling and generating a picture spend credits each time you use one. The assistant tells you the estimated cost first.
- **Designed graphics.** Each design uses credits, and the cost is shown before you start it.
- **Generated video clips.** These are the most expensive item. Before the assistant starts a run that would generate clips, it asks whether to reuse clips from earlier runs (free) or generate new ones (paid), and it tells you the most they could cost.

## When the assistant picks up after a run

If you ask the assistant to wait for a run, the follow-up message it writes when the run finishes is a normal assistant message. It follows the same rules as any other message here: it uses your monthly allowance first, then your credit balance.

Each request, including a follow-up, has a limit of 100 credits. The amount set aside while it works stays at up to 5 credits. In auto mode a follow-up can start work that uses credits without asking, still within that limit and your balance.

## Read the cost on an approval card

By default the assistant asks before it changes anything. Each card shows what will happen and, when the action uses credits, a cost line such as **Usually 8–12 credits (needs 13 available to start; can cost more).**

- The first figures are what the action usually costs.
- The **needs** figure is what your workspace must have available before the action can start.
- If the cost could not be worked out in advance, the card says so, and the action still uses credits.

For an action that uses credits, the button reads **Allow and use credits**. Select **Don't allow** to decline the card and tell the assistant what you want instead.

In auto mode, runs that use credits start without a card. Your credit balance and the hourly run limit still apply.

## What the monthly allowance covers

Each plan includes a monthly assistant allowance. The amount for your plan is shown on **Settings → Plan and billing**.

- Chat messages and Claude Code tool calls share one allowance.
- The allowance starts again with each billing period. On the Trial plan it starts again each calendar month.
- On a plan priced per seat, the allowance is counted per seat and shared across the workspace.
- To see how much you have used, open **Settings → Usage**. The **Assistant allowance** section shows the amount used, and **By source** shows what your credits were spent on.

## What happens when the allowance runs out

When the allowance is used up, the assistant and Claude Code keep working from your credit balance. Each message and each Claude Code tool call sets aside up to 5 credits while it runs. It uses the allowance first, then your balance, and is charged only for what it actually used.

The assistant stops only when the allowance is used up and your balance has no credits left. It then says so in the chat.

A paid picture tool or a timeline suggestion checks your balance before it starts. If there are not enough credits, it tells you how many it needs.

## Claude Code and the allowance

Claude Code draws on the same allowance as the chat assistant, and it follows the same rules for what is free and what uses credits. See [What it costs](../assistant/claude-code.md#what-it-costs) in the Claude Code guide.

## Related pages

- [Plans and credits](./plans-and-credits.md): the overview of plans, credits and the Usage page.
- [Reading the estimate](./reading-the-estimate.md): the cost line before a run.
- [The assistant](../assistant/index.md): what the assistant can do and how approvals work.
