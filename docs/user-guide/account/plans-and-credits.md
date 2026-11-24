---
title: Plans and credits
sidebar_position: 2
description: What a ReelBolt plan is, how credits are granted and spent, which plans exist and what each includes, how much storage a workspace gets and what counts toward it, and where to find the pages on changing a plan, promo codes, estimates, resolution and the assistant's costs.
---

# Plans and credits

:::note
Detail; the short version is in [Plan, credits and billing](../basics/plan-credits-and-billing.md).
:::

A ReelBolt **plan** is what a workspace subscribes to, and **credits** are what the work inside it costs. Every unit of AI work ReelBolt buys on your behalf, such as a model call, a transcription, a generated clip or a render, is recorded in a ledger and, on a paid plan, charged in credits. This page is the overview of the money side: what the plans are, how credits come in and go out, and where you see it all. Each topic has its own page, linked at the end of its section.

## What a plan is in ReelBolt

A plan sets what the workspace may do in a month: how many credits it gets, how much storage it includes, the highest export resolution, whether exports carry a watermark, how many runs can go at once, whether it includes the editing rooms and generated video, and how much generated video it can buy per day. The plan your workspace is on shows on **Settings → Plan and billing** as **Current plan**, with its status, its renewal date and its seat count.

Only the workspace **Owner** and members with the **Billing** role can buy, change or cancel a plan. Everyone else sees the page read-only. See [Managing your plan](./managing-your-plan.md) for the steps.

## How credits work in ReelBolt

A **credit** is one unit of spend. ReelBolt meters every billable unit it buys from a provider and prices it when it runs, so a later price change never rewrites what you were billed. The **Usage** page (**Settings → Usage**) shows your balance, how much is held for work that is running, and how much you have used, broken down by who did the work.

- **Work through ReelBolt's providers is charged.** When a step runs on a provider that the ReelBolt installation set up, its credits come out of your balance.
- **Your own work is free to you.** Work through a provider your workspace connected itself (see [Bring your own provider](./bring-your-own-provider.md)) and work on a computer your workspace paired itself (see [Desktop runner](./desktop-runner.md)) is still recorded, so the Usage page stays complete, but it is charged **0 credits**. You pay your own provider and your own machine, not ReelBolt.
- **A step can finish a little over its estimate.** A step's real usage cannot be predicted exactly, so it may finish slightly over what was held for it. The overage is charged, and the balance can go negative, which blocks further work until the workspace tops up.

**How a failure is treated.** If a step fails because of the platform, the credits it was holding are refunded. If it fails because of your input, because you stopped it, or because a provider your workspace connected itself failed, the credits it actually used are kept.

## What uses credits

| What you do | How it is charged |
|---|---|
| **Workflow runs** | Each step is charged for the work it does, as described above. |
| **The assistant** and **[Claude Code](../assistant/claude-code.md)** | Free up to your plan's monthly assistant allowance, then charged for what they use. The two share one allowance. |
| **Picture editor AI tools** (for example edit with words, erase, upscale or cut out a subject) | Charged each time you use one. |
| **Timeline suggestions** | Charged each time you ask for one. |
| **Designed graphics** | Charged each time you design one. |
| **File summaries**, **search indexing** and **storage** | Free. Storage is limited by the plan instead (see below). |

Work through a provider your workspace connected itself costs 0 credits, whichever of these it is. The amount of the monthly assistant allowance for your plan is shown on **Settings → Plan and billing**. For what the assistant and Claude Code cost in detail, see [What the assistant costs](./what-the-assistant-costs.md).

## The plans on sale in ReelBolt

These are the plans in the catalog. Prices exclude VAT and sales tax, which are added at checkout by the payment provider. The credit and limit figures below are the current published set.

| Plan | Price | Credits | Storage | What it adds |
|---|---|---|---|---|
| **Trial** | Free, one-time 100 credits | 100 (one-time) | 1 GB | 720p exports with a watermark, no card required. Cannot top up, no bring-your-own providers, no rooms, no generated video. |
| **Creator** | $29 / month | 1,000 / month | 15 GB | 1080p exports, no watermark, bring your own provider, top-up credits. |
| **Pro** | $79 / month | 3,500 / month | 50 GB | 4K exports, the editing rooms, generated b-roll clips, priority rendering, top-up credits. |
| **Team** | $49 / month per seat (3 seats minimum) | 1,500 per seat / month, pooled | 100 GB shared | Everything in Pro (including 4K exports), with a shared workspace and roles. Credits pool across seats. |
| **Enterprise** | Contact us | — | unlimited | 4K and above, single sign-on, dedicated capacity, self-hosting licence, custom terms and support. |

Top-up packs are available on paid plans (Creator, Pro and Team) in sizes of 500, 1,000 and 3,000 credits. They expire twelve months after purchase. The free Trial plan cannot top up, so choose a paid plan first.

## Export resolution on each ReelBolt plan

Each plan sets the highest resolution a run or an editor export can produce, and the plan table above lists the limit for each plan. Resolution is measured on the short side of the video, so one setting fits both landscape and vertical video. Choose the resolution for a run in its settings, or for an export in the editor's **Deliver** workspace (the video editor is an Early preview). A higher resolution costs more credits, because rendering is charged by the output's size.

For how to choose a resolution, what happens when you pick one your plan does not include, and what each resolution costs, see [Choosing a resolution](./choosing-resolution.md).

## Storage in a ReelBolt workspace

Each plan includes an amount of storage for the whole workspace, and Enterprise is unlimited. Storage is measured across everything in the workspace's projects, not just your uploads. It includes files you upload, files synced in from GitHub, a cloud drive or a folder on your own computer, files an agent writes during a run, and the videos, images and other outputs a run produces.

**What happens when the storage is full.** New files stop being added. An upload is refused and tells you the workspace is out of storage. A background sync copies what fits, leaves the rest, and says how many files did not fit. A workflow that is already running is not interrupted, and its outputs are still written, because the run has already been paid for. **Nothing is ever deleted automatically**, not even when you move to a plan with less storage than you use. The workspace simply cannot add more until you delete files or upgrade.

**Where to see it.** **Settings → Plan and billing** shows a storage bar with the amount used and the plan's limit, and the **Usage** page shows the same. Project pages show a warning when the workspace is at 90 % or more. If a deployment runs without billing, there is no storage limit and no bar.

## Changing your plan

Buy, upgrade, switch to a smaller plan, change seats, cancel and undo a cancellation on **Settings → Plan and billing**. A change to a bigger plan takes effect once the payment provider confirms it, and a change to a smaller plan starts with your next billing period. Only one change can wait at a time. Read [Managing your plan](./managing-your-plan.md) for each step, and what to do while a change is still being confirmed.

## Promo codes and free credits

Enter a promo code on **Settings → Plan and billing** to lower the price of a plan or a credit pack before you pay. Some codes give free credits instead, which you redeem on the same page. Promo credits expire, and they are spent before the credits you bought. Read [Promo codes and credits](./promo-codes-and-credits.md) for the steps, the refusals and when credits expire.

## What a run will cost before you start it

Before you run a workflow, ReelBolt shows what the run should cost, for example **This run should cost ≈ 40 credits (usually 30–52)**. It also says what the figure is based on, and what must be in your balance before the run can start. Read [Reading the estimate](./reading-the-estimate.md) for what each part of that line means.

## Invoices, payment method and the payment provider

The payment provider charges your card, adds tax and issues the invoices. Select **Manage in …** on the current-plan card (the button names your payment provider) to open your account page with the provider, where you can download invoices, change the payment method and cancel. The shortcuts **Update payment method** and **Cancel in …** (also naming the provider) open the matching page directly when the provider offers them.

## Topping up credits

Under **Buy credits** on **Settings → Plan and billing**, an Owner or a Billing member can buy a top-up pack. Top-up credits are added to the same balance as the plan's monthly credits. They are meant for a busy month, not as a replacement for a plan, and they expire twelve months after purchase. The payment provider shows the exact charge, in your currency, before you pay.

## What to do when the balance runs low

- **The work stops rather than overspending.** A step that needs more credits than are available is refused before it starts, so you are never charged past your balance without topping up.
- **Top up** from **Settings → Plan and billing → Buy credits**, or **move to a bigger plan** whose monthly credits fit your usage.
- **On a self-hosted install with billing off**, there is no balance to run out. The usage and billing pages are switched off.

## Where your credits are spent

The **Usage** page (**Settings → Usage**) is the running ledger. It shows **Credits available** (with the amount in the account and the amount held for running work), **Credits used**, and the split by who did the work: ReelBolt's providers, a provider your workspace connected itself, and the workspace's own runner. Every entry names the provider and model that did the work and the credits it was charged.

If a deployment runs without billing, this page says so and shows nothing. There is no plan to buy, no balance to top up and no invoice to open, and nothing you run is metered or charged.

## Related pages in ReelBolt

- [Managing your plan](./managing-your-plan.md): buying, upgrading, switching, changing seats and cancelling.
- [Promo codes and credits](./promo-codes-and-credits.md): promo codes, free-credit codes and when promo credits expire.
- [Reading the estimate](./reading-the-estimate.md): the cost line before a run, and what is free.
- [Choosing a resolution](./choosing-resolution.md): the resolution setting and what each resolution costs.
- [What the assistant costs](./what-the-assistant-costs.md): the assistant and Claude Code allowance and approval costs.
- [Workspaces and teams](../workspaces.md): who can buy the plan and how the workspace is organised.
- [Bring your own provider](./bring-your-own-provider.md): the free-to-you path for the model work.
- [Desktop runner](./desktop-runner.md): the free-to-you path for the rendering work.
- [Copying a folder from your computer into a project](../projects/local-folder-sync.md): how folder sync adds files to a workspace, which count toward its storage.
- [Troubleshooting](../troubleshooting.md): what to do when a run is refused for lack of credits, or when the workspace is out of storage.
