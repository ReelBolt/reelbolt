---
title: What is ReelBolt
sidebar_position: 1
description: ReelBolt turns your code and your footage into finished promotional videos using teams of AI agents that you arrange into workflows.
---

:::note Alpha release

ReelBolt 0.2.0-alpha — public alpha. Expect rough edges; things may change.

Early access means features may change, there is no service-level guarantee, and your feedback is welcome.

:::

# What is ReelBolt

ReelBolt is a web app for making promotional videos with the help of AI agents. You put your material into a project, pick or build a workflow (a recipe of steps), press **Run**, and ReelBolt runs the steps one after another until a finished video comes out the other end.

You open ReelBolt in your browser. Everything described in this guide lives under the dashboard at `/app` (for example `/app/projects`).

## What ReelBolt can make for you

ReelBolt covers two broad kinds of video work, and you can mix them.

**Promo videos built from a codebase.** Upload the source code of a web app or product. A chain of agents reads the code, works out what the product does and how it looks (its components, pages, colours and fonts), writes a script, plans the shots, and then builds and renders an animated video that shows the product off. The animation is written as code and rendered into a real video file, so the result looks like your actual product rather than a stock template. A review agent scores the result and can send it back for another pass if the score is too low.

**Editing real footage.** Upload raw video (for example a long screen recording or a talking-head take). ReelBolt can:

- **Derush and cut it**: find the pauses, the scene changes and what is being said, then decide which parts to keep and cut them together accurately.
- **Add motion graphics**: titles, lower-thirds and callouts placed at sensible moments and in empty parts of the frame.
- **Place tracked screen inserts**: if your footage shows a phone or laptop with a green (or other single-colour) screen, ReelBolt can follow that screen as it moves and put your own content inside it, keeping hands and bezels in front.
- **Blur or highlight one object**: say "blur the speedometer" and ReelBolt follows that object through the shot and applies the effect to just that part of the picture. See [Blur or highlight one object](./recipes/prompted-region-effects.md).
- **Add background music and sound effects**: pick a track from your uploads, keep it quieter under speech, and add whooshes or clicks on cuts.
- **Colour-grade the edit**: apply one consistent look (warm, cool, filmic, and so on) to the whole video.
- **Generate b-roll**: create short extra clips (cutaways, an opening shot, an end card) with a video-generation service, inside the spending limits your team has set.
- **Add a voiceover**: turn script text into narration with a text-to-speech service.

Some of these features need a matching AI service to be set up by an administrator first (for example speech-to-text for transcripts, or a video-generation service for b-roll). See [Inference providers](./admin/inference-providers.md).

## Who ReelBolt is for

- **Founders, marketers and product teams** who want a launch or feature video without hiring an animator or editor for every version.
- **Creators and developers** who record demos or tutorials and want the dead air cut out and the edit tidied up quickly.
- **Administrators** who run a ReelBolt installation for a team: they create accounts and connect the AI services that the agents use.

You do not need to know how to code to use ReelBolt. Workflows can be built from ready-made templates, and the built-in assistant can explain, draft and change workflows for you in plain conversation.

## The main building blocks

These are the pieces you will meet everywhere in ReelBolt. Each is explained in more depth in [Concepts](./concepts.md).

- **Workspace**: the container that owns your projects and the people you share them with. You get a personal one, and you can create team ones. See [Workspaces and teams](./workspaces.md).
- **Project**: a folder for one video effort. It holds your files, your workflows and every video those workflows produce.
- **Files**: what you upload (code, footage, music, images, documents) plus what the agents create along the way and the finished renders.
- **Workflow**: an ordered list of steps that turns your files into a video. Every new project starts with one ready-made workflow called **Promo video from your code v1**.
- **Step**: one job in a workflow, such as "analyse the code", "decide which shots to keep" or "cut the final video".
- **Agent**: an AI worker with a specific role (scriptwriter, director, story editor, reviewer, and so on). ReelBolt ships many built-in agents, and you can create your own.
- **Execution** (also called a run): one go of a workflow from start to finish. You can watch it live, stop it and retry it.
- **Render**: a finished video produced by a run, which you can play, download and share from the project's **Renders** tab.
- **Assistant**: a chat helper available on every page that can answer questions and make changes for you after you approve them.

## How this guide is organised

- [Getting started](./getting-started.md): sign in, create a project, upload files, run your first workflow and find the video.
- [Concepts](./concepts.md): what workspaces, projects, files, workflows, steps, agents, runs, outputs, caching and costs mean in practice.
- [Workspaces and teams](./workspaces.md): your personal workspace, team workspaces, the four member roles, invitations and switching.
- [Your data, exports and closing your account](./your-data.md): download everything ReelBolt holds about you, close your account, and what is kept afterwards.
- [Syncing files from GitHub](./projects/github-repository.md): link a project to a GitHub repository, choose which files and folders to sync, and keep them up to date on every push.
- [Syncing files from Google Drive, OneDrive or Dropbox](./projects/cloud-drive-sync.md): link a project to a cloud folder, choose which files to sync, and keep them up to date automatically or with Sync now.
- [Copying a folder from your computer into a project](./projects/local-folder-sync.md): keep a folder on your own computer (including iCloud Drive) copied into a project with the desktop app, and what never happens.
- [The assistant](./assistant/index.md): what the built-in assistant can do, how approvals work, and how to ask it for things.
- Workflows: the built-in [templates](./workflows/templates.md), the [step types](./workflows/step-types.md), the [agents](./workflows/agents.md) and [rooms](./workflows/rooms.md), where a panel of AI specialists makes one decision together.
- Plans, credits and billing: [Plans and credits](./account/plans-and-credits.md), with [Managing your plan](./account/managing-your-plan.md), [Promo codes and credits](./account/promo-codes-and-credits.md), [Reading the estimate](./account/reading-the-estimate.md), [What the assistant costs](./account/what-the-assistant-costs.md) and [Choosing a resolution](./account/choosing-resolution.md).
- [The timeline editor](./editor/index.md): open an edit as a timeline, play it, cut and move clips, add markers, undo and go back to earlier versions. The video editor and the [picture editor](./editor/thumbnails.md) are available as an Early preview.
- Recipes: step-by-step guides such as [a promo from your code](./recipes/promo-from-code.md), [derushing and editing footage](./recipes/derush-edit.md), [a tracked screen insert](./recipes/tracked-screen-insert.md) and [subtitles for a video](./recipes/subtitles.md).
- Administration: [users](./admin/users.md), [inference providers](./admin/inference-providers.md), [semantic search](./admin/semantic-search.md) and [decision models](./admin/decision-models.md).
- [Desktop runner](./account/desktop-runner.md): pairing a computer you own to render ReelBolt videos on it, installing and running it, its settings, and what runs where and what data leaves the machine.
- [Troubleshooting](./troubleshooting.md): common error messages and what to do about them.
