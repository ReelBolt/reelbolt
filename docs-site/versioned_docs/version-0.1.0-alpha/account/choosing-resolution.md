---
title: Choosing a resolution
sidebar_position: 10
description: How to choose the output resolution for a workflow run or an editor export, how resolution is measured, what happens when you pick a resolution your plan does not include, and why a higher resolution uses more credits.
---

# Choosing a resolution

This page covers how to choose the resolution a video is made at, both for a workflow run and for an export in the editor. It explains how ReelBolt measures resolution, what happens when you pick a value your plan does not include, and why a higher resolution uses more credits. Your plan sets the highest resolution you can choose. The plan table in [Plans and credits](./plans-and-credits.md#the-plans-on-sale-in-reelbolt) lists the limit for each plan, and **Settings → Plan and billing** shows your current one.

## How resolution is measured

Resolution is measured on the **short side** of the video, so one setting fits a landscape video and a vertical video alike. 4K is 3840×2160 in landscape and 2160×3840 in vertical. A square video is measured the same way: a 2160×2160 square counts as 4K.

## Choose a resolution for a run

1. Open the workflow and start the run from the workflow page.
2. In the run settings, next to the editing style and the aspect ratio, open **Resolution**.
3. Pick a value. **Auto** keeps the output at up to 1080p, or at your plan's limit if that is lower, so a run renders in 4K only when you ask for it. On Enterprise, Auto keeps the size of your source.
4. Check the estimate, which already includes the resolution you picked (see [Reading the estimate](./reading-the-estimate.md)), and then start the run.

You can also ask the assistant for a resolution, for example "in 4K". It checks your plan first and tells you the limit if you ask for more than your plan allows.

Options above your plan's limit are shown disabled. If a run is sent with one anyway, for example through an API call, ReelBolt refuses it before it starts and says which resolution your plan allows.

## Choose a resolution for an export in the editor

:::note Video editor availability

The video editor, including its **Deliver** workspace, is switched off in ReelBolt 0.1.0-alpha and shows
**Coming soon**. Choose the resolution in the run's own settings, described above.

:::

In the editor, open the **Deliver** workspace and choose a format. If the format is bigger than your plan allows, ReelBolt scales it down to your plan's limit with the same shape, and the form shows the size the export will have. 4K is shown disabled when your plan does not include it. See [Rendering a finished video (Deliver)](../editor/index.md#rendering-a-finished-video-deliver).

## Which resolutions your plan allows

Each plan sets the highest resolution a run or an export can produce. If your workspace moves to a smaller plan while a run is waiting, the run finishes at the new plan's highest resolution. To see your plan's limit, open **Settings → Plan and billing**.

## What a higher resolution costs

Rendering and encoding are charged by the output's resolution, measured on its short side. A 4K render uses about four times the render credits of a 1080p render, and a 720p render uses less than half of them. A square or portrait video costs the same as a widescreen video with the same short side.

The model work in a run, such as writing, planning and transcribing, costs the same at any resolution, so only the rendering part of the cost changes. The estimate on the run page already includes the resolution you chose.

## Related pages

- [Plans and credits](./plans-and-credits.md): the plan table and how credits work.
- [Reading the estimate](./reading-the-estimate.md): what the cost line before a run means.
- [Managing your plan](./managing-your-plan.md): upgrading, switching to a smaller plan and cancelling.
