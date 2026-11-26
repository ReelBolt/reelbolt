---
title: Getting started
sidebar_position: 2
description: Sign in, create your first project, upload files, run your first app and find the finished video.
---

# Getting started

:::note
Detail; the short version is in [The basics](./basics/index.md).
:::

:::tip New here?

The short, illustrated guides in [The basics](./basics/index.md) take you from sign-up to your first video in ten minutes.

:::

This page walks you through your first session in ReelBolt: signing in, making a project, adding files, running an app, watching the run and finding the finished video.

## Make your first film with a sample clip

If you have not run anything yet, the dashboard shows a **Make your first film** card (the empty Apps page offers the same button). One click creates a project called "My first film", adds a short sample clip to it and opens the highlight edit app with that clip already chosen. Nothing starts by itself: press **Run** when you are ready. You can close the card with its **X**, and it disappears once you have a run.

## Signing in to ReelBolt for the first time

How you get in depends on the kind of ReelBolt you are using.

**A self-hosted ReelBolt has no public sign-up.** An administrator creates your account and gives you a **temporary password** (it is emailed to you if the installation has email set up; otherwise the administrator passes it on to you directly).

1. Open the ReelBolt sign-in page and enter your **Email** and the temporary **Password**, then click **Sign in**.
2. Because the password is temporary, ReelBolt immediately takes you to the change-password screen. You cannot use the rest of the app until you finish this step.
3. Fill in **Current Password** (the temporary one), **New Password** (at least 10 characters) and **Confirm New Password**, then click **Change Password**.

**A cloud ReelBolt lets you sign up yourself** (when self-service sign-up is switched on for the installation; a self-hosted one is not).

1. Open the sign-up page. Where the installation offers it, **Continue with Google** or **Continue with GitHub** creates your account with nothing to type and no email to confirm. Otherwise enter your **Name**, your **Email** and a **Password** (at least 10 characters; you type it once), accept the terms and privacy policy, then click **Create account**.
2. If you signed up with your email, ReelBolt sends a confirmation link to that address. Opening the link confirms the address and signs you in. Until you do, a yellow **Confirm your email address** bar stays at the top and runs do not start. **Resend the email** on that bar re-sends it. (Signing up from a team invitation link confirms the address for you, because the invitation proves it.)
3. The first time you arrive, ReelBolt asks two short questions and sets up your first project from your answers; see [Sign up and take a first look](./basics/sign-up-and-first-look.md#answer-two-questions). You start in the **personal workspace** your account was given.

Afterwards you land in the dashboard whenever you sign in. Your sign-in lasts 24 hours; after that you are asked to sign in again. If you forget your password, ask an administrator to reset it, or use **Forgot password** on the sign-in page; see [Managing users](./admin/users.md).

## Finding your way around the ReelBolt dashboard

The left-hand menu has, at the top, the things you use every day:

- **Dashboard**: an overview of recent activity, runs in progress and anything that needs attention.
- **Projects**: all of your projects.
- **Editor**: a timeline view of edits that video workflows have produced (the video editor and the picture editor are available as an Early preview).

Under **More** you will find:

- **Styles**: the editing styles an app can be run with.
- **Runners**: the computers paired to this workspace that can make its videos.
- **Agents (advanced)**: the AI workers behind the apps (you rarely need this page).

Everything else is grouped into sections you can open and close. A section opens by itself when the page you are on is inside it, so the menu never hides where you are:

- **Settings**: **Account** — your own page, with your details, your password, how the dashboard looks, and the accounts you have connected — then **Usage** and **Plan & billing**, which are about the workspace you are currently in.
- **Workspace** (workspace Owners and Admins): the workspace's **Settings**, its **Members**, and its own **Providers**.
- **Platform** (platform administrators only): **Overview**, **Workflow Service**, **Inference**, **Billing**, **Calibration**, **Skills** and **Users**.

Administering the whole installation is the **Platform** section. It is not where you add somebody to a workspace: that is **Workspace → Members**, and the two are deliberately separate.

The **assistant** button (a sparkle icon) sits in the top bar on every page. It opens a chat panel on the right (or from the bottom on a phone). See [The assistant](./assistant/index.md).

## Creating a project in ReelBolt

A project holds everything for one video: its files, its apps' settings and the videos they make.

1. Go to **Projects** and click **New project**.
2. Give it a **Name** (for example "Spring launch teaser") and, optionally, a **Description**.
3. Save. The new project opens on its **Overview** tab.

A project page has tabs including **Overview**, **Files**, **Apps** (called **Workflows** where apps are switched off), **Videos** and **Voices** (the narration voices this project can use; see the [voiceover recipe](./recipes/voiceover.md)), plus tabs for settings such as **Brand & product** and **Repository**. A link ending in `?tab=voices` (or `files`, `workflows`, `renders`) opens that tab directly. To rename a project or delete it, use the **⋮** (Project actions) menu next to **Upload files** and choose **Edit name and description** or **Delete project**. Deleting a project permanently removes all of its files, apps and videos.

Projects are private: only the person who created a project can see it.

## Uploading files to a ReelBolt project

Click **Upload files** at the top of the project page, or open the **Files** tab and use **Upload**, which offers **Choose files** or **Choose a folder**. You can also drag files or whole folders anywhere onto the project page. When you upload a folder, its structure is kept.

Upload whatever the app needs: source code for a promo built from your product, raw video for an edit, music and sound-effect files, images, or reference documents. Very large video files are supported (uploads of many gigabytes are allowed).

**Where files show up.** The **Files** tab can show **Sources** (what you uploaded), **Generated** (files created during runs), **Videos** (finished videos) or **All**. You can also filter by type (video, audio, image, text), sort, switch between list and grid view, and organise files into folders with **New folder**, **Move to...**, rename and delete. To rename a single file, open its **⋮** menu (or its preview) and choose **Rename**; workflows that use the file keep working. A name cannot be empty, contain `/` or `\`, be longer than 255 characters, or match another file in the same folder — ReelBolt tells you which.

**Summaries.** After you upload a text file (code, documents), ReelBolt writes a short summary of it automatically. Click a file to open its preview and look at the **Summary** tab; while it is still being written you will see "A summary is being written". A picture's summary is a short description written by the vision model, when one is set up. A PDF with selectable text is summarised from that text; a scanned PDF is not. Video, audio and other non-text files are not summarised. Uploads never wait for summaries: a large folder uploads at full speed and its summaries are written afterwards, a few at a time, pausing briefly whenever you are chatting with the assistant so it answers you first.

**Search indexing.** Text files are also added to the project's search index so you (and the agents) can search inside them by meaning, not just by file name. This only works if your administrator has set up an Embedding provider; see [Semantic search](./admin/semantic-search.md). Video and audio files are never indexed. A file's **Details** tab shows its search index status, with a **Reindex** button.

## The starter app every new ReelBolt project gets {#the-starter-workflow-every-new-reelbolt-project-gets}

A new project starts with one ready-made app, chosen by what you said the video is made from when you created it. A project that starts from clips gets **Highlight edit from footage v1**, which cuts the dead air, false starts and retakes out of your footage; see [Highlight edit from footage](./recipes/derush-edit.md#recipe-highlight-edit-from-footage). A project that starts from an app or website you built gets **Promo video from your code v1** (older projects have it as **Quick Win Promo v1**), and so does a project created without that choice, for example through the [API](./api/index.md). The rest of this section is about that one. It is the "promo from your code" pipeline: it analyses the code you uploaded (structure, dependencies, components, routes and styling), turns it into animated scenes, plans the animation, writes narration and captions, directs the shots, builds and renders the video, and finally runs a quality review that can send the video back for one more attempt if it scores below 8 out of 10. When you run it, it asks for your brief: who the video is for, what to show, how long it should be and the format (a "15 s vertical reel" comes out 1080x1920). It has no narration step, so the video is silent unless you add one.

It is designed for projects whose files are source code. If your project is about real footage, pick a different app instead: go to the **Apps** (or **Workflows**) tab, click **New workflow**, pick one of the cards under **Start from a template** (each shows the template's name and what it makes) and click **Use this template**. The available templates are described in [Workflow templates](./workflows/templates.md). You can also ask the assistant to suggest or draft a workflow for your files.

## Running an app in ReelBolt {#running-a-workflow-in-reelbolt}

1. Open the project, go to the **Apps** tab (called **Workflows** where apps are switched off) and click the app.
2. Click **Run** at the top of the app page. Nothing starts yet: a confirmation box opens first.
3. The box lists what the run will do, in plain words, and roughly how long it takes (from your earlier runs of the same app, or a warning that long runs can take up to an hour).
4. Write **Your brief**: what video you want, for example "a 30-second teaser focused on the new dashboard, vertical, upbeat". Every part of the run sees it. The brief is always offered; it is required only when the workflow has **Require a brief before each run** switched on.
5. Click **Start run**. If the workflow generates paid video clips, a second box titled **This workflow generates paid video clips** asks whether to reuse clips generated earlier (free) or regenerate them (costs money), and shows an upper bound on what regenerating could cost.
6. ReelBolt starts the run, adds it to the workflow's **Run history** straight away, and takes you to its live run page.

You can run the same app again at any time. Work whose inputs have not changed may be reused from the previous run instead of being redone; see [Concepts](./concepts.md).

## Watching a ReelBolt run {#watching-a-reelbolt-workflow-run}

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

When a run renders a video, it appears in the project's **Videos** tab (and in **Latest videos** on the **Overview** tab). Each video is named after the app and run that made it (for example "60s product story — Run 2") and shows when it was made, its length and its shape and size (for example "Vertical 9:16 · 1080×1920").

- **Final** marks the finished video of a run that completed. **Draft** marks an earlier attempt — a review step that sent the work back produces one draft per round — or a video from a run that did not finish. **In progress** marks a video from a run that is still going. The tab opens on the newest final video.
- Any warnings from that video's run are shown under the player.
- Click **Download** to save the video; the file is named after the app and run.
- Click **Share** to open your device's share sheet. Where the browser has no share sheet, the link is copied instead and a "Link copied" message confirms it; if copying is blocked too, the message shows the link so you can copy it yourself. **The link only opens for people signed in to this project** (today that is the project's owner), so it is for your own use, not for sending to a client. Public or expiring links are not available.
- Click **Run** to jump to the run that produced it.

Videos also appear as files under **Videos** on the **Files** tab. Video-editing apps additionally let you open the edit on a timeline in the **Editor** section: it lists the project's edited videos by app and run, and if there are none yet its **Run an app** button takes you to the project's apps. The video editor is available as an Early preview, and every editor page says so.

## Next steps after your first ReelBolt run

- Learn the vocabulary in [Concepts](./concepts.md).
- Not sure what to make? Start from [What do you want to make?](./index.md#what-do-you-want-to-make) or [Choose an app](./recipes/choose-a-recipe.md).
- Follow a guide, such as [a promo from your code](./recipes/promo-from-code.md) or [derushing and editing footage](./recipes/derush-edit.md).
- Ask the [assistant](./assistant/index.md) "Why did my last run fail?" if something goes wrong, or read [Troubleshooting](./troubleshooting.md).
