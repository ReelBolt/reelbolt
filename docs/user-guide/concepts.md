---
title: Concepts
sidebar_position: 3
description: The ideas behind ReelBolt explained in plain words - projects, files, workflows, steps, agents, runs, outputs, caching, user requests and costs.
---

# Concepts

This page explains the words ReelBolt uses and how the pieces fit together. Each section stands on its own, so you can jump straight to the one you need.

:::note Plain words
In the app and in most of this guide, an **app** is what you run, a **video** is what it makes and a **run** is one attempt. The words workflow, step and template describe how apps are built; you only need them if you build your own (see [Workflow templates](./workflows/templates.md)). A **derush** is the first cut of raw footage with the dead air removed. **B-roll** is extra footage shown over the main shot.
:::

## Workspaces in ReelBolt

A **workspace** is the container that owns everything you do in ReelBolt: your projects, their files, apps, videos, custom agents and AI connections. It is also the unit that shares and bills: people inside one workspace work on the same projects, and people outside it cannot see them at all.

Every account gets a **personal workspace** of its own, always shown as **Personal**, holding just its owner. When you need to work with other people you create a **team workspace** and invite them into it, and you can belong to as many as you like and switch between them from the menu at the top left. Inside a workspace each member holds one of four roles — **Owner**, **Admin**, **Member** or **Billing** — and the role decides what they can do there.

Two things follow from this and are easy to get wrong:

- **A project you can see is a project your workspace can see**, not one only you can see. The exception is the personal workspace, which has just you in it.
- **Platform administration is not workspace membership.** The menu group called **Platform** belongs to whoever runs the whole installation, and it deliberately gives no view of any workspace's projects.

See [Workspaces and teams](./workspaces.md) for the roles in full, invitations, switching and leaving.

## Projects in ReelBolt

A **project** is a container for one video effort: a product launch video, a tutorial edit, a feature teaser. It holds three things:

- the **files** you upload and the files produced while working,
- the **apps** that describe how to turn those files into a video,
- the **videos** it has made.

A project belongs to the **workspace** it was created in. Everyone in that workspace whose role allows project work — Owner, Admin and Member — can see it and run its workflows; someone outside the workspace cannot see it at all, and neither can a platform administrator, who manages accounts and AI services rather than other people's projects. Deleting a project permanently deletes everything in it. See [Workspaces and teams](./workspaces.md).

## Project files and their three categories

Every file in a ReelBolt project has a **category** that says where it came from. You will see these names when talking to the assistant or reading a file's details:

| Category | Shown in the Files tab as | What it holds |
|---|---|---|
| `userFiles` | **Sources** ("Uploaded by you") | Everything you uploaded: code, footage, music, images, documents. |
| `agentFiles` | **Generated** ("Created by an agent") | Files agents wrote during runs, such as analysis results, generated clips and synthesized voiceover audio. |
| `outputFiles` | **Videos** | Finished videos produced by runs. |

Files can sit in **folders**. Folders are just a way of organising the list: they appear when a file is uploaded or moved into them, and an empty folder exists only until you put something in it. You can move files between folders, and even between categories, with **Move to...**.

Text files get an automatic **summary** (a few lines about what the file contains) and are added to the project's **search index** so they can be searched by meaning. Pictures (PNG, JPEG, GIF and WebP) are described by the installation's vision model instead: the description becomes the picture's summary and is what search matches against. When no vision model is set up, pictures are left without a summary. Every other kind of file (video, audio, PDFs, archives, fonts and other binary files) is skipped for both. The summary helps agents and the assistant understand your material without reading every file in full.

:::note Advanced
The next sections describe how apps are built: workflows, steps and agents. You do not need them to run an app. Skip to [Executions, runs and step results](#executions-runs-and-step-results) if you only run ready-made apps.
:::

## Workflows in ReelBolt

A **workflow** is the ordered list of steps behind an app that turns your project's files into a video. Workflows belong to one project.

You can get a workflow in three ways:

- **Automatically**: every new project gets one ready-made workflow, chosen by what you said the video is made from. A project that starts from clips gets **Highlight edit from footage v1**. A project that starts from an app or website you built gets **Promo video from your code v1** (called **Quick Win Promo v1** in projects created before October 2026).
- **From a template**: on the project's **Workflows** tab, click **New workflow**, pick one of the cards under **Start from a template** and click **Use this template**. See [Workflow templates](./workflows/templates.md).
- **By building or describing one**: add steps yourself in the visual builder, or ask the [assistant](./assistant/index.md) to draft one and allow it.

You can rename a workflow, change its steps, switch **Require a brief before each run** on or off, and click **Save Changes**. Running a workflow is done with **Run**.

## Steps and step types in a ReelBolt workflow

A **step** is one job in a workflow. Steps run in order, and each step can use what earlier steps produced. Every step has a **step type**, which decides what kind of job it is:

- **Agent**: an AI agent does the work (writes a script, decides which shots to keep, builds an animation).
- **ReviewLoop**: an agent scores the result and, if it falls below a minimum score, sends the workflow back to an earlier step to try again, up to a set number of times.
- **Conditional**, **ForEach** and **Parallel**: control the flow, by choosing a branch, repeating over a list, or running several agents at once.
- **Extract**: trims a large earlier result down to just the parts later steps need, which makes runs cheaper and faster.
- **VideoAnalyze**: examines footage (pauses, scene changes, speech, what is on screen) without any AI decision-making of its own.
- **VideoCompile**: cuts and assembles the final video from the decisions made by earlier steps, adding graphics, music, sound effects, colour and inserts if asked.
- **EditRoom**, **GraphicsRoom** and **ColorGradeRoom**: several agents discuss a decision together, like a small editing team, and a director agent settles it.
- **VideoGenerate**: creates new video clips with a paid video-generation service.
- **Voiceover**: turns script text into narration audio.

Each step type is described in detail in [Step types](./workflows/step-types.md).

## Agents: built-in and custom

An **agent** is an AI worker with a specific role and set of instructions. A workflow's Agent steps each name the agent that should do the work.

- **Built-in agents** ship with ReelBolt: code analysers, a scene translator that turns your interface into animated scenes, an animation strategist, a scriptwriter, a director, an author that builds and renders the video, reviewers, and video-editing specialists such as the story editor, motion-graphics planner, music supervisor, sound designer, colorist and shot director. Their instructions cannot be edited, because the rest of the system relies on what they produce.
- **Custom agents** are ones you create on the **Agents** page with **New Agent**: you give it a **Name**, a **Description**, a **System Prompt** (its instructions in plain language) and a **Color**. Custom agents can be edited at any time.

Each agent uses an AI service that the workspace has available: the connections the ReelBolt installation was set up with, plus — in a team workspace whose plan allows it — the workspace's own connections, which take precedence there. A platform administrator can point every agent everywhere at a different AI service; a workspace's Owner or Admin can do the same for that workspace alone — for a built-in agent type, and for the workspace's custom agents (for example a stronger model for the director). See [Inference providers](./admin/inference-providers.md). The full list is in [Agents](./workflows/agents.md).

## Executions, runs and step results

An **execution** (or **run**) is one go of a workflow from start to finish. Each execution has a status:

| Status | Meaning |
|---|---|
| `Queued` | Waiting for a free slot; only a limited number of runs work at the same time. |
| `Running` | Working through its steps. |
| `Passed` | Finished successfully. |
| `Failed` | A step could not complete; the error is shown on the execution page. |
| `Cancelled` | Stopped by you pressing **Stop**. A run that is interrupted by an update or restart is not cancelled: it is picked up again automatically, once. |

Every step that runs leaves a **step result**: what went in, what came out, its own status (`Pending`, `Running`, `Completed`, `Failed` or `Skipped`), any error, how many tokens it used, and which attempt it was (a review loop can run the same step several times). You can read all of this by clicking a step on the execution page. A step that fails is usually retried automatically before the whole run is marked `Failed`.

## Outputs and videos {#outputs-and-renders}

An **output** is anything a run produces. The most important outputs are **videos**: playable video files. Videos appear on the project's **Videos** tab, under **Videos** on the **Files** tab, and on the **Overview** tab under **Latest videos**. You can play, **Download** and **Share** them.

Some steps also produce non-video results that you can inspect on the execution page, such as a full footage analysis, the list of cuts, or a manifest of generated clips. Video-editing runs also produce a timeline you can browse in the **Editor** section (the video editor is available as an Early preview).

## The step cache: when a rerun reuses earlier results

When you run a workflow again, ReelBolt can reuse a step's earlier result instead of doing the work again, if **nothing that step depends on has changed**. That includes the step's own settings, the agent and its instructions, the AI service used, everything earlier steps produced for it, your user request, and the project's files. Change any of those and the step runs fresh. Reused results are kept for up to 7 days by default.

A reused step is marked **reused from cache** on the execution page, with how many tokens it saved. This makes reruns faster and cheaper, and for paid video generation it means an identical clip is not paid for twice.

Some steps are never reused by default, because they change things outside their own result or because a fresh judgement is the whole point: steps whose agent writes project files or builds and renders video, review loops, the graphics room, and the creative steps in the built-in templates. Analysis and cutting steps are reused by default.

Each step has a **Cache Mode** setting (the JSON key is `cacheMode`):

| Cache Mode | Meaning |
|---|---|
| `Default` | Use the built-in rules above. |
| `Always` | Always allow reuse for this step, even where the built-in rules would not. Use with care. |
| `Never` | Always run this step fresh. Use it when you want a new creative take every time. |

## Your brief for a run (Require a brief before each run)

Every run can carry a short brief: when you click **Run**, the confirmation box has a **Your brief** field for free text (apps without fields of their own) such as "keep it under 45 seconds and end on the pricing page". An app that has fields of its own (see [App fields](workflows/app-inputs.md)) asks for those instead of a free brief. Leaving it empty is fine unless the workflow has **Require a brief before each run** switched on (on the workflow page; the setting is called `requiresUserInput`), in which case the run cannot start without one.

The request is passed to every agent in the run as extra context, and it is remembered with the execution, so **Retry** uses the same request. Because the request is part of what the step cache compares, changing it makes affected steps run fresh.

## Costs and spending limits in ReelBolt

Most of ReelBolt's work uses AI services connected to your workspace (its own, or the installation's). Those services usually charge by usage; ReelBolt shows how many tokens each step used on the execution page, but the bill itself comes from the service provider, not from ReelBolt.

**Paid video generation is the one place ReelBolt actively guards your spending.** A `VideoGenerate` step must have a **Max Spend** cap (`maxSpendCredits`, in **credits** — the same unit as the rest of your billing). Before buying anything, ReelBolt checks these limits and refuses the whole step, buying nothing, if any would be exceeded:

- the step's own **Max Spend** cap, compared with the estimated cost of every clip it would create,
- **your own daily video limit**, if you set one (**Settings → Usage → Daily video limit**; by default there is none, so only your credits and the step's cap apply),
- your plan must include video generation (a plan without it refuses the step); no plan limits how much you may spend per day.

Days are counted in UTC. The daily limit is yours to set, change or remove; no plan sets a daily video budget. See [Your daily video limit](./account/daily-video-limit.md). (Whoever runs an installation also keeps a very high safety ceiling against runaway spend; a normal day never reaches it.)

When you run or retry a workflow that generates video, ReelBolt asks whether to **reuse previous clips** (free: an identical earlier clip is used again) or **Regenerate (costs money)**. The upper bound shown is the sum of the video steps' Max Spend caps; the real cost is usually lower. See [Troubleshooting](./troubleshooting.md) for what a `BUDGET_EXCEEDED` error means.
