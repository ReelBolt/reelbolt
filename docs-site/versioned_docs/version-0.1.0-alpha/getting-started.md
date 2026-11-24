---
title: Getting started
sidebar_position: 2
description: Sign in, create your first project, upload files, run the starter workflow and find the finished video.
---

# Getting started

This page walks you through your first session in ReelBolt: signing in, making a project, adding files, running a workflow, watching it work and finding the finished video.

## Signing in to ReelBolt for the first time

How you get in depends on the kind of ReelBolt you are using.

**A self-hosted ReelBolt has no public sign-up.** An administrator creates your account and gives you a **temporary password** (it is emailed to you if the installation has email set up; otherwise the administrator passes it on to you directly).

1. Open the ReelBolt sign-in page and enter your **Email** and the temporary **Password**, then click **Sign in**.
2. Because the password is temporary, ReelBolt immediately takes you to the change-password screen. You cannot use the rest of the app until you finish this step.
3. Fill in **Current Password** (the temporary one), **New Password** (at least 8 characters) and **Confirm New Password**, then click **Change Password**.

**A cloud ReelBolt lets you sign up yourself** (when self-service sign-up is switched on for the installation; a self-hosted one is not).

1. Open the sign-up page, enter your **Email**, a **Password** (at least 8 characters) and your **Display name**, and accept the terms and privacy policy, then click **Create account**.
2. ReelBolt sends a confirmation link to that address. Open the link in the email to confirm the address — until you do, a yellow **Confirm your email address** bar stays at the top and the runs that cost money are disabled. **Resend the email** on that bar re-sends it. (Signing up from a team invitation link confirms the address for you, because the invitation proves it.)
3. Sign in with the address and password you chose. You start in the **personal workspace** your account was given.

In either case you land in the dashboard afterwards. Your sign-in lasts 24 hours; after that you are asked to sign in again. If you forget your password, ask an administrator to reset it, or use **Forgot password** on the sign-in page; see [Managing users](./admin/users.md).

## Finding your way around the ReelBolt dashboard

The left-hand menu has, at the top, the things you use every day:

- **Dashboard**: an overview of recent activity, runs in progress and anything that needs attention.
- **Projects**: all of your projects.
- **Editor**: a timeline view of edits that video workflows have produced (the video editor is switched off in this alpha; the picture editor is available).
- **Agents**: the AI workers available to your workflows.
- **Styles**: the editing styles a workflow can be run with.
- **Runners**: the computers paired to this workspace that can render its videos.

Everything else is grouped into sections you can open and close. A section opens by itself when the page you are on is inside it, so the menu never hides where you are:

- **Settings**: **Account** — your own page, with your details, your password, how the dashboard looks, and the accounts you have connected — then **Usage** and **Plan & billing**, which are about the workspace you are currently in.
- **Organization** (workspace Owners and Admins): the workspace's **Settings**, its **Members**, and its own **Providers**.
- **Platform** (platform administrators only): **Overview**, **Workflow Service**, **Inference**, **Billing**, **Calibration**, **Skills** and **Users**.

Administering the whole installation is the **Platform** section. It is not where you add somebody to a workspace: that is **Organization → Members**, and the two are deliberately separate.

The **assistant** button (a sparkle icon) sits in the top bar on every page. It opens a chat panel on the right (or from the bottom on a phone). See [The assistant](./assistant/index.md).

## Creating a project in ReelBolt

A project holds everything for one video: its files, its workflows and its renders.

1. Go to **Projects** and click **New project**.
2. Give it a **Name** (for example "Spring launch teaser") and, optionally, a **Description**.
3. Save. The new project opens on its **Overview** tab.

A project page has five tabs: **Overview**, **Files**, **Workflows**, **Renders** and **Voices** (the narration voices this project can use; see the [voiceover recipe](./recipes/voiceover.md)). A link ending in `?tab=voices` (or `files`, `workflows`, `renders`) opens that tab directly. To rename a project or delete it, use the **⋮** (Project actions) menu next to **Upload files** and choose **Edit name and description** or **Delete project**. Deleting a project permanently removes all of its files, workflows and renders.

Projects are private: only the person who created a project can see it.

## Uploading files to a ReelBolt project

Click **Upload files** at the top of the project page, or open the **Files** tab and use **Upload**, which offers **Choose files** or **Choose a folder**. You can also drag files or whole folders anywhere onto the project page. When you upload a folder, its structure is kept.

Upload whatever the workflow needs: source code for a promo built from your product, raw video for an edit, music and sound-effect files, images, or reference documents. Very large video files are supported (uploads of many gigabytes are allowed).

**Where files show up.** The **Files** tab can show **Sources** (what you uploaded), **Generated** (files agents created during runs), **Renders** (finished videos) or **All**. You can also filter by type (video, audio, image, text), sort, switch between list and grid view, and organise files into folders with **New folder**, **Move to...**, rename and delete. To rename a single file, open its **⋮** menu (or its preview) and choose **Rename**; workflows that use the file keep working. A name cannot be empty, contain `/` or `\`, be longer than 255 characters, or match another file in the same folder — ReelBolt tells you which.

**Summaries.** After you upload a text file (code, documents), ReelBolt writes a short summary of it automatically. Click a file to open its preview and look at the **Summary** tab; while it is still being written you will see "A summary is being written". A picture's summary is a short description written by the vision model, when one is set up. Video, audio and other non-text files are not summarised. Uploads never wait for summaries: a large folder uploads at full speed and its summaries are written afterwards, a few at a time, pausing briefly whenever you are chatting with the assistant so it answers you first.

**Search indexing.** Text files are also added to the project's search index so you (and the agents) can search inside them by meaning, not just by file name. This only works if your administrator has set up an Embedding provider; see [Semantic search](./admin/semantic-search.md). Video and audio files are never indexed. A file's **Details** tab shows its search index status, with a **Reindex** button.

## The starter workflow every new ReelBolt project gets

Every new project automatically gets one workflow called **Promo video from your code v1** (older projects have it as **Quick Win Promo v1**). It is the "promo from your code" pipeline: it analyses the code you uploaded (structure, dependencies, components, routes and styling), turns it into animated scenes, plans the animation, writes narration and captions, directs the shots, builds and renders the video, and finally runs a quality review that can send the video back for one more attempt if it scores below 8 out of 10. When you run it, it asks for your brief: who the video is for, what to show, how long it should be and the format (a "15 s vertical reel" comes out 1080x1920). It has no narration step, so the video is silent unless you add one.

It is designed for projects whose files are source code. If your project is about real footage, create a different workflow instead: go to the **Workflows** tab, click **New workflow**, pick one of the cards under **Start from a template** (each shows the template's name and what it makes) and click **Use this template**. The available templates are described in [Workflow templates](./workflows/templates.md). You can also ask the assistant to suggest or draft a workflow for your files.

## Running a workflow in ReelBolt

1. Open the project, go to the **Workflows** tab and click the workflow.
2. Click **Run** at the top of the workflow page. Nothing starts yet: a confirmation box opens first.
3. The box lists what the run will do, step by step, in plain words, and roughly how long it takes (from your earlier runs of the same workflow, or a warning that long workflows can take up to an hour).
4. Write **Your brief**: what video you want, for example "a 30-second teaser focused on the new dashboard, vertical, upbeat". Every agent in the run sees it. The brief is always offered; it is required only when the workflow has **Require a brief before each run** switched on.
5. Click **Start run**. If the workflow generates paid video clips, a second box titled **This workflow generates paid video clips** asks whether to reuse clips generated earlier (free) or regenerate them (costs money), and shows an upper bound on what regenerating could cost.
6. ReelBolt starts the run, adds it to the workflow's **Run history** straight away, and takes you to its live run page.

You can run the same workflow again at any time. Steps whose inputs have not changed may be reused from the previous run instead of being redone; see [Concepts](./concepts.md).

## Watching a ReelBolt workflow run

The run page updates live while the run is going. Its title names the run the way you will see it everywhere else, for example **Run 3 · 4 Oct, 14:02** (the third run of that workflow, started on 4 October at 14:02).

- **Warnings first.** If anything in the run did not turn out as asked — narration lines that could not be placed, no music because the project has no audio files, a video that came out much shorter than the length you asked for, a narration voice that is not working — a yellow box at the top says so in plain words. A run can finish and still have a warning, so read it before you share the video.
- The status badge shows **Waiting to start**, **Working**, **Finished**, **Failed** or **Stopped**, with a progress bar underneath.
- While it works, a line says what is happening right now in plain words, for example "Writing the narration…" or "Watching your footage…", with the step's name.
- Four tiles show **Steps done**, when the run **Started**, how long it has been going (**Elapsed**, or **Took** once it ends) and **Time left**. Time left is an estimate from how long the finished steps took; steps that render video usually take longer than the rest, so treat it as a rough guide.
- The **Steps** diagram shows every step with its plain-language job. Click a step to see its **Step details**: what it produced, how long it took, any problem, and whether it was reused from an earlier run.
- **Activity** lists what happened, newest first, with each agent named by its job.
- **Details** (under the tiles) shows the technical information: the live-connection state, token counts (a measure of how much AI text was read and written), review rounds, the run's internal id, each step's raw data and every event's raw payload. You do not need any of it to follow a run.
- **Stop** (shown while the run is going) cancels the run. **Run again** (shown once the run has finished, failed or been stopped) runs it again.

Runs can take a while, from minutes for small jobs to much longer for big code bases or long footage. You can leave the page; the run carries on, and the dashboard shows it under runs in progress.

## Finding the finished video in ReelBolt

When a run renders a video, it appears in the project's **Renders** tab (and in **Latest renders** on the **Overview** tab). Each render is named after the workflow and run that made it (for example "60s product story — Run 2") and shows when it was made, its length and its shape and size (for example "Vertical 9:16 · 1080×1920").

- **Final** marks the finished video of a run that completed. **Draft** marks an earlier attempt — a review step that sent the work back produces one draft per round — or a render from a run that did not finish. **In progress** marks a render from a run that is still going. The tab opens on the newest final video.
- Any warnings from that render's run are shown under the player.
- Click **Download** to save the video; the file is named after the workflow and run.
- Click **Share** to open your device's share sheet. Where the browser has no share sheet, the link is copied instead and a "Link copied" message confirms it; if copying is blocked too, the message shows the link so you can copy it yourself. **The link only opens for people signed in to this project** (today that is the project's owner), so it is for your own use, not for sending to a client. Public or expiring links are not available.
- Click **Run** to jump to the run that produced it.

Renders also appear as files under **Renders** on the **Files** tab. Video-editing workflows additionally let you open the edit on a timeline in the **Editor** section: it lists the project's edited videos by workflow and run, and if there are none yet its **Run a workflow** button takes you to the project's workflows. The video editor is switched off in ReelBolt 0.1.0-alpha; this describes the section for a release where it is on.

## Next steps after your first ReelBolt run

- Learn the vocabulary in [Concepts](./concepts.md).
- Pick a workflow that fits your material in [Workflow templates](./workflows/templates.md).
- Follow a recipe, such as [a promo from your code](./recipes/promo-from-code.md) or [derushing and editing footage](./recipes/derush-edit.md).
- Ask the [assistant](./assistant/index.md) "Why did my last run fail?" if something goes wrong, or read [Troubleshooting](./troubleshooting.md).
