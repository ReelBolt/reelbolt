---
title: What is ReelBolt
sidebar_position: 1
description: Pick what you want to make with ReelBolt, from a promo video to cutting your footage, subtitles, a voice, a dub or a thumbnail, and find the page that shows you how.
---

:::note Alpha release

ReelBolt 0.4.0-alpha — public alpha. Expect rough edges; things may change.

Early access means features may change, there is no service-level guarantee, and your feedback is welcome.

:::

# What is ReelBolt

## What do you want to make?

Pick the goal that fits. Each link opens the page that shows you how.

- **A promo video for my product or app:** [Promo video from your code](./recipes/promo-from-code.md), or [Product promo, your way](./recipes/product-promo-your-way.md).
- **Cut my footage:** [Edit my video](./recipes/edit-my-video.md) removes dead air and retakes from a clip you filmed.
- **Subtitles:** [Add subtitles to a video](./recipes/subtitles.md).
- **A voice or narration:** [Voiceover](./recipes/voiceover.md), or [Read my text aloud](./recipes/read-text-aloud.md).
- **Dub into another language:** [Dub a video into another language](./recipes/dub-a-video.md).
- **Blur or highlight something:** [Blur or highlight one object](./recipes/prompted-region-effects.md).
- **Green screen:** [A tracked screen insert](./recipes/tracked-screen-insert.md) puts your content on a green screen in a clip.
- **A thumbnail:** [Thumbnail and cover text](./recipes/thumbnail-and-cover-text.md).
- **Change a finished video:** [Ask for changes](./basics/ask-for-changes.md).
- **Not supported yet:** talking avatars longer than about 15 seconds, image carousels, and reformatting a finished video. See [What ReelBolt cannot do yet](./recipes/choose-a-recipe.md#what-reelbolt-cannot-do-yet).
- **Something else:** ask the [assistant](./basics/assistant-basics.md). It tells you which app fits, or that none does.

All the apps in one list, with what each needs: [Choose an app](./recipes/choose-a-recipe.md).

## New here? Start with The basics

[The basics](./basics/index.md) are short illustrated guides that take you from sign-up to your first video in about ten minutes. Guides 1 to 4 are all you need.

## What ReelBolt is

ReelBolt is a web app for making videos with the help of AI. You put your material into a project, pick an app, press **Run**, and ReelBolt does the work and gives you a finished video. A **run** is one attempt.

You open ReelBolt in your browser. Everything described in this guide lives under the dashboard at `/app` (for example `/app/projects`).

Two words you will meet: a **derush** is the first cut of raw footage, with the dead air and retakes removed. **B-roll** is extra footage shown over the main shot.

## What ReelBolt can make for you

**Videos from your code.** Upload the source code of a web app or product. ReelBolt works out what the product does and how it looks, writes a script, plans the shots and builds an animated video that shows the product off, so the result looks like your actual product.

**Videos from your own footage.** Upload raw video, such as a long screen recording or a talking-head take. ReelBolt can:

- **Cut it**: remove the pauses and retakes and keep the good parts.
- **Add titles and callouts**: titles, lower-thirds and callouts at sensible moments.
- **Put content on a green screen**: follow a phone or laptop screen as it moves and place your own content inside it.
- **Blur or highlight one object**: say "blur the speedometer" and ReelBolt follows that object through the shot. See [Blur or highlight one object](./recipes/prompted-region-effects.md).
- **Add music and sound effects**, and give the video one consistent colour look.
- **Add extra clips**: generate short cutaways, an opening shot or an end card, inside the spending limits your team has set.
- **Add a voiceover** from your text, or **dub** the video into another language.

Some of these need an AI service that an administrator sets up first (for example speech-to-text for subtitles, or video generation for extra clips). See [Inference providers](./admin/inference-providers.md).

## Who ReelBolt is for

- **Founders, marketers and product teams** who want a launch or feature video without hiring an animator or editor for every version.
- **Creators and developers** who record demos or tutorials and want the dead air cut out and the edit tidied up quickly.
- **Administrators** who run a ReelBolt installation for a team: they create accounts and connect the AI services that the apps use.

You do not need to know how to code to use ReelBolt. Ready-made apps do the work, and the built-in assistant can explain, suggest and change things for you in plain conversation.

## The main building blocks

These are the pieces you will meet everywhere in ReelBolt. Each is explained in more depth in [Concepts](./concepts.md).

- **Workspace**: the container that owns your projects and the people you share them with. You get a personal one, and you can create team ones. See [Workspaces and teams](./workspaces.md).
- **Project**: a folder for one video effort. It holds your files, your apps' settings and every video they make.
- **Files**: what you upload (code, footage, music, images, documents) plus what ReelBolt creates along the way and the finished videos.
- **App**: a ready-made way to make one kind of video, such as editing your footage or adding subtitles. You pick it, fill in a few settings and press **Run**.
- **Run**: one attempt of an app, from start to finish. You can watch it live, stop it and retry it.
- **Video**: what a run makes. You can play, download and share it from the project's **Videos** tab.
- **Assistant**: a chat helper available on every page that can answer questions and make changes for you after you approve them.

## How this guide is organised

- [The basics](./basics/index.md): short illustrated guides, from sign-up to your first video.
- [Getting started](./getting-started.md): sign in, create a project, upload files, run your first app and find the video.
- [Concepts](./concepts.md): what workspaces, projects, files, apps, runs, videos, caching and costs mean in practice.
- [Workspaces and teams](./workspaces.md): your personal workspace, team workspaces, the four member roles, invitations and switching.
- [Your data, exports and closing your account](./your-data.md): download everything ReelBolt holds about you, close your account, and what is kept afterwards.
- [Syncing files from GitHub](./projects/github-repository.md): link a project to a GitHub repository, choose which files and folders to sync, and keep them up to date on every push.
- [Syncing files from Google Drive, OneDrive or Dropbox](./projects/cloud-drive-sync.md): link a project to a cloud folder, choose which files to sync, and keep them up to date automatically or with Sync now.
- [Copying a folder from your computer into a project](./projects/local-folder-sync.md): keep a folder on your own computer (including iCloud Drive) copied into a project with the desktop app, and what never happens.
- [The assistant](./assistant/index.md): what the built-in assistant can do, how approvals work, and how to ask it for things.
- Advanced, for building your own: the built-in [templates](./workflows/templates.md), the [step types](./workflows/step-types.md), the [agents](./workflows/agents.md) and [rooms](./workflows/rooms.md), where a panel of AI specialists makes one decision together.
- Plans, credits and billing: [Plans and credits](./account/plans-and-credits.md), with [Managing your plan](./account/managing-your-plan.md), [Promo codes and credits](./account/promo-codes-and-credits.md), [Reading the estimate](./account/reading-the-estimate.md), [What the assistant costs](./account/what-the-assistant-costs.md) and [Choosing a resolution](./account/choosing-resolution.md).
- [The timeline editor](./editor/index.md): open an edit as a timeline, play it, cut and move clips, add markers, undo and go back to earlier versions. The video editor and the [picture editor](./editor/thumbnails.md) are available as an Early preview.
- Apps: step-by-step guides such as [choosing an app](./recipes/choose-a-recipe.md), [a promo from your code](./recipes/promo-from-code.md), [derushing and editing footage](./recipes/derush-edit.md), [a tracked screen insert](./recipes/tracked-screen-insert.md) and [subtitles for a video](./recipes/subtitles.md).
- Administration: [users](./admin/users.md), [inference providers](./admin/inference-providers.md), [semantic search](./admin/semantic-search.md) and [decision models](./admin/decision-models.md).
- [Desktop runner](./account/desktop-runner.md): pairing a computer you own to make ReelBolt videos on it, installing and running it, its settings, and what runs where and what data leaves the machine.
- [Troubleshooting](./troubleshooting.md): common error messages and what to do about them.
