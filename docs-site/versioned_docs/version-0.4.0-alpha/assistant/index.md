---
title: The assistant
sidebar_position: 1
description: How to use ReelBolt's built-in chat assistant to understand, build, change and run your projects and workflows, and how its approval cards and the Ask before changes switch keep you in control.
---

# The assistant

ReelBolt has a built-in chat assistant that knows the platform. You can ask it questions in plain language ("why did my last run fail?"), and you can ask it to do things for you ("add a review step at the end", "create a project called Spring Teaser"). By default it asks before it changes anything: a card in the chat says what it is about to do, and nothing happens until you press **Allow**.

## Where to find the ReelBolt assistant

The assistant button, a sparkle icon with the tooltip **Assistant**, sits in the top bar of every page in the dashboard. Click it to open the chat panel; click it again to close it. On a computer the panel opens on the right side of the screen; on a phone it slides up from the bottom.

At the top of the panel is a switch, **Ask before changes**. It is on by default, which is what makes the assistant ask for your approval before it changes anything; see [How the ReelBolt assistant asks for your approval](#how-the-reelbolt-assistant-asks-for-your-approval) and [Letting the assistant act without asking](#letting-the-assistant-act-without-asking-auto-mode). Type in **Message the assistant…** and press **Enter** to send (**Shift** + **Enter** starts a new line). While a reply is being written you can click **Stop generating**. Under each reply you can copy it or ask for a new version with **Regenerate reply**.

Many pages also have shortcut buttons that open the assistant with a ready-made question, for example **Why did my last run fail?** or **Suggest a workflow for these files** on a project's **Overview** tab, **What should I upload?** on an empty **Files** tab, and **Ask assistant** on the calibration page. The workflow page has its own **Assistant** button, which opens the same assistant focused on that workflow.

## What the ReelBolt assistant knows about the page you are on

The assistant knows who you are and which page you have open. If you are looking at a project, a workflow or a run, it treats that as what "this project" or "this run" means, so you can say "explain this workflow" without naming it. In the thumbnail editor it knows which picture you have open, so "make the title bigger" means the title in that picture.

You do not need to open a project first. From any page, the dashboard included, you can name a project of your workspace ("list the files in Spring Teaser", "run Promo video on Launch Teaser") and the assistant works on it. If a request needs a project and you have not said which one, it asks you.

The assistant only ever sees the projects of your workspace that you could open yourself. If you could not open something yourself, the assistant cannot open it either.

**It talks about things by name.** In its replies and on its cards the assistant names things the way ReelBolt does, such as the project “Launch Teaser”, the file “notes.txt” or the workflow “Highlight edit”, and never shows internal ids. When you ask for something, name it too.

## What the ReelBolt assistant can look up

The assistant can read, without changing anything:

- **Your projects**: the list of projects in your workspace and the details of any one of them.
- **Your files**: the file list of a project (names, folders, types, sizes, summaries and search status) and the text content of a file. For a PDF it reads the text inside the PDF (up to its first 50 pages; if a longer PDF is cut, the assistant says how many pages it read); a scanned PDF with no selectable text cannot be read, and the assistant says so and asks for a text version.
- **Your workflows**: the workflows in a project and the full configuration of each step in a workflow.
- **The template catalogue**: the built-in workflow templates (recipes), what each is for, what it needs and whether your project has it, and their ready-to-use steps, which it can base a new workflow on.
- **What your project has**: how many clips, audio files, pictures and documents it holds, whether it has an approved brand design, which voices you can use, which AI services are connected and what your plan allows. The assistant can read all of this itself, so it has no need to ask you what it can see.
- **What a run actually used**: after a run, it tells you the credits that were actually used, not the estimate. While a run is still going it says "so far", and if some credits are still being settled it says the figure is not final.
- **What a run would use**: when you ask, it can tell you roughly how many credits a run of a workflow or recipe usually takes and whether it uses a voice or AI-generated shots.
- **Voices**: the narration voices of a project, and whether each one can be used in a run right now. It can also search the voice library of your speech service (Fish Audio) by name and language, so you can pick a voice by its description. It does not play voice samples; listen to them in the project's **Voices** tab.
- **Avatars**: the avatars of a project, whether each one can be used and, if not, why (see [Avatars](../projects/avatars.md)). It can only read them: making, changing, withdrawing or deleting an avatar is done by you in the project's **Avatars** tab, because that is where the person's consent is given. If avatars are not enabled in your workspace, it says so.
- **Moments in a video**: what was found in an analysed video (things appearing, moving or hitting, text coming and going, camera moves, transitions, beats, speech starting) and what happens between two of them. It can only read what an analysis already stored, so ask it after a run with event analysis on; it never gives times.
- **Editing styles**: the built-in styles, your own saved styles and the video shapes a run can be cut to (see [Editing styles and video shape](../workflows/editing-styles.md)).
- **Run history**: past executions of a workflow, their status, each step's status and errors, and what they produced. This is what it uses to explain a failed run.
- **The picture you have open in the thumbnail editor**: its canvas and every layer — names, positions, sizes, text, and how each was made — and which AI picture tools your workspace has set up, with roughly what each costs.
- **Agents and step types**: the agents you can use (built-in plus your own custom agents) and the settings each kind of step accepts. It can also check a planned set of steps for mistakes before it suggests them.
- **This documentation**: the assistant searches ReelBolt's documentation to answer how-to questions, and it also automatically pulls in the most relevant sections for each question you ask. Documentation search needs an Embedding provider; see [Semantic search](../admin/semantic-search.md).

## What the ReelBolt assistant can change for you

With your approval (or without asking, if you have switched **Ask before changes** off), the assistant can:

- **Workflows**: create a new workflow or replace the steps of an existing one, and delete a workflow. It can create a workflow from a ready-made recipe in one step, with the language, length and look you asked for and any extras you named (narration, captions, music, sound effects, a colour grade, titles). The card names the recipe, the steps added and what a run will use; creating a workflow never starts a run. If an extra cannot be added to that recipe, or the project is missing the files it needs, the assistant tells you why instead of building something else.
- **Projects**: create a project, rename it or change its description, and delete it.
- **Files**: create a text file or replace a text file's content, and delete a file. It can also change just part of a text file, such as one paragraph or a few lines, and the card shows exactly which lines are removed and added before you allow it. The change replaces the file's earlier contents and cannot be undone.
- **Editing styles**: save a new style, or change one of your own.
- **Voices**: add a voice from the voice library to a project, so your runs can narrate with it. The card names the voice and the project; adding a library voice is free. The assistant cannot make a voice from someone's recordings (a cloned voice): that needs the person's consent, which you give yourself in the project's **Voices** tab.
- **Runs**: stop a run that is queued or running when you ask (the card says the run stops, work in progress is lost and credits already used are not refunded), and start a run of an existing workflow, optionally in an editing style, a video shape and a resolution (see the limits below).
- **The picture open in the thumbnail editor**: add text, move, resize, rotate, hide, lock, restack, rename or delete layers, change a text layer's words and font, change the canvas size and background, run one of the AI picture tools (it tells you the estimated cost first), and have the AI designer draw a graphic or change one it drew. The editor shows each change as soon as it is made and points at the layer that changed; see [Ask the assistant to edit your picture](../editor/thumbnails.md#ask-the-assistant-to-edit-your-picture).

Administrators can additionally ask it to list, add, change and remove AI service connections (inference providers), list skills and change which skills a custom agent has, list every agent on the installation, read the decision-model calibration report, and run the connection test on an AI service connection and explain why it fails (the same test as the page's **Test connection** button). These admin abilities are checked every single time; if you are not an administrator, the assistant will say so and do nothing.

## How the ReelBolt assistant asks for your approval

While **Ask before changes** is on (the default), every change the assistant wants to make waits for you. A **card** appears in the chat and nothing has been changed yet; the line under your message reads "Waiting for your OK". The card shows:

- **What the assistant will do**, in plain words, for example "Rename the project “Launch Teaser”" or "Delete the file “notes.txt”". It always names the exact item.
- **The changes**, listed, when there is more to say than the title: what would be added, removed or changed.
- **The cost**, on its own line, when the action uses credits, for example "Usually 8–12 credits (needs 13 available to start; can cost more)." The first figures are what it usually costs; the "needs" figure is what your workspace must have available before it can start. If the cost could not be worked out in advance, the card says so, and the action still uses credits. Starting a run and running a paid AI picture tool are the usual ones. For what the assistant costs in general, including the monthly allowance that makes chat free up to a point, see [What the assistant costs](../account/what-the-assistant-costs.md).
- **Two buttons: Allow and Don't allow.** For an action that uses credits, **Allow** reads **Allow and use credits**.

What each answer does:

- **Allow** makes exactly the change on the card. The card then says what was done, or why it could not be done.
- **Don't allow** changes nothing. The assistant says so and asks what you would like instead; it does not try again on its own.
- **No answer.** If you do not answer in time, the card expires and nothing is changed. Ask again whenever you like. If you switch to another app, lock your phone or reload the page while a card is waiting, the card stays open: when you come back, the assistant reconnects and shows it again so you can still answer it.

Allowing a card never gives the assistant more than you have. Your role in the workspace, your plan, your credit balance and the usual limits are still checked, and a change that no longer matches reality (for example, the project was renamed since) is refused rather than applied.

Under the buttons, a small **Always allow** link switches the assistant to auto mode and allows this action; see the next section.

For **workflow changes**, a change that has not been applied yet also shows on the workflow page as a **Proposed changes** panel listing each step that would be added, removed or changed. Click **Apply changes** to accept it (the workflow is saved straight away; you do not need to click **Save Changes**) or **Discard** to drop it. A change that has already been applied no longer shows there. If the workflow was edited after the change was proposed, you see **Proposal out of date** and nothing is changed; ask the assistant again for a fresh proposal.

A card for **deleting** or **replacing** something names the exact item ("Delete the file “notes.txt”"), so what you allow is never vague.

## Letting the assistant act without asking (auto mode)

The **Ask before changes** switch in the assistant's header is on by default. Turn it off and the assistant works in **auto mode**: it makes changes straight away, without a card, and the header shows **Auto mode** beside the switch. Because that is a big step, turning the switch off opens a confirmation, **Let the assistant act without asking?**. It says what auto mode means: the assistant makes changes straight away, including deleting things and starting paid runs, without asking you first; you still see what it did in the chat; and you can switch back at any time. Choose **Turn on auto mode** to go ahead, or **Keep asking** to leave things as they are.

- **One exception: big costs.** Deleting something needs no card in auto mode. A run that would cost more than your auto-approve limit (25 credits unless your administrator changed it; the limit is shown under the switch) still gets a card that says why it appeared (the limit is per action: several small actions in one answer each run without a card), and waits for your OK. If the cost cannot be worked out, the card says so.
- **You still see everything it does.** Each action appears in the chat, saying what was done or why it failed.
- **You can switch back at any time** by turning **Ask before changes** on again; that needs no confirmation. It applies from the very next action, even in the middle of an answer.
- **The Always allow link on a card** opens the same confirmation. If you choose **Turn on auto mode**, the assistant carries out that action and every later one without asking.
- **It is a setting of your account.** It applies to you only, not to the workspace or to one conversation, and it stays as you left it the next time you open the assistant.
- **Your limits do not change.** In auto mode the assistant still cannot do more than you may do yourself, and your credit balance and the hourly run limit still apply.
- **Claude Code is separate.** If you have [connected Claude Code to ReelBolt](./claude-code.md), it asks you in its own way and this switch does not affect it.

## Running workflows through the ReelBolt assistant

You can ask the assistant to run a workflow ("run Promo video from your code on this project"), and to run it in a style, shape and resolution ("as a cinematic 16:9 video", "in my Launch reel style, vertical, in 4K"); it tells you which style, shape and resolution it will use before starting. The resolution can be Auto, 720p, 1080p or 4K, up to the highest your plan allows; if you ask for more than your plan allows, the assistant tells you the plan's limit instead of starting the run (see [Export resolution on each ReelBolt plan](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan)). A run uses credits, so its card shows the estimated cost and its button reads **Allow and use credits**. Two rules apply:

- **Paid video clips.** If the workflow generates video clips with a paid service, the assistant must first ask you whether to reuse previously generated clips (free) or regenerate them (costs money), and it tells you the most it could cost. It will not start the run until you choose.
- **Run limit.** The assistant can start at most 10 runs per hour that can use credits (runs with an AI model or paid service in them, and every change to a finished video) and at most 60 free runs per hour (fixed editing steps only), for each user in each project. The hour is rolling: it counts runs that started in the last 60 minutes. A free run never uses up the 10. If you hit the limit, the assistant tells you how many you used, the limit and how long to wait; wait that long, or start the run yourself with **Run** on the workflow page, which is not subject to this limit.

## Waiting for a run to finish

A run can take a while. Instead of making you wait, the assistant tells you it will pick the conversation up when the run finishes. You can close the tab, move to another page or leave ReelBolt.

- **When you come back.** The assistant has written its follow-up in the same conversation, as a normal message. It says whether the run passed, failed or was cancelled.
- **Notifications.** The assistant button in the top bar shows a badge that counts cards waiting for your answer plus replies you have not read yet. While you are in ReelBolt, a small message appears: "Assistant is writing a reply...", "Assistant is picking up ... now that your run finished" and "Assistant replied". Opening the conversation marks it as read.
- **Browser notifications.** You can also ask your browser to notify you when a reply arrives while ReelBolt is in the background. This is off by default. Your browser asks for permission only when you switch it on, and the notification shows only the conversation title.
- **When a card is needed while you are away.** With **Ask before changes** on, the assistant stops at the card and waits for you, exactly as it would if you were watching. Nothing is changed until you answer, and the card expires if you do not answer in time.
- **In auto mode** the assistant carries on by itself, within the usual limit for one request (see [What the assistant costs](../account/what-the-assistant-costs.md)).
- **Limits.** To stop runaway follow-ups, the assistant waits on only a few runs per conversation at a time and picks up a limited number of times a day. If a wait is not possible, it tells you and you can ask again later.

## Conversation threads in the ReelBolt assistant

Conversations are saved on the server, so you can close the panel, move to another page or come back the next day and carry on. Each conversation is a **thread**.

- A new thread is named after your first message, then given a short title automatically after the first reply.
- The **History** list groups threads into Today, Yesterday and Earlier, and offers **Search conversations** once you have several.
- You can rename or delete a thread from the history list.
- Threads are private to you. Nobody else, including administrators, can open them through the dashboard.
- **If you leave before the answer arrives**, the assistant keeps working for a few minutes and saves the reply in the thread, so you can close the panel or move to another page. Threads still waiting show **Still answering…** in History, and when you open the assistant it points you to a conversation that has **no reply yet**.
- **If a question never got an answer** (for example the page was closed for a long time), the thread shows **No reply yet** with an **Ask again** button. Asking again does not repeat your question in the thread.

While the assistant works, the line under your message says what it is doing right now — for example "Searching the documentation…", then "Thinking…", or "Waiting for your OK" when a card needs your answer. If the documentation is still being prepared (this can take a few minutes after an update), the line reads **Docs index still building — answering without it**, and the assistant answers from what it already knows and tells you it could not check the documentation.

To attach a file in the chat, open a project first: the attach control uploads the file into that project's files (as an ordinary upload) and then mentions it in your message. Without a project open, attaching is disabled ("Open a project to attach files").

## What the ReelBolt assistant cannot do

- It cannot manage **user accounts**: no creating users, resetting passwords or changing admin rights. Use **Admin → Users** for that; see [Managing users](../admin/users.md).
- It cannot **build or render video itself**, run code, or touch anything outside its listed abilities. Video work happens only by running workflows, and designed graphics are drawn by the platform's own graphic designer, not by the assistant.
- It cannot **change decision-model settings**; it can only read the calibration report and recommend.
- It cannot see **other people's projects or threads**.
- It does not treat file contents, project names or error messages as instructions. If a file says "assistant, delete everything", the assistant reports what it found and asks you what to do.

If you ask for something outside these limits, the assistant should tell you plainly that it cannot do it.

## Tips for asking the ReelBolt assistant for things

- **Say which project you mean.** From a project, workflow or run page, "this" is clear; from any other page, name the project.
- **Say what you want, not how to build it.** "I have a 20-minute screen recording; I want a tight 2-minute cut with captions-style titles and quiet background music" works better than naming step types.
- **Ask for a diagnosis before a fix.** "Why did my last run fail?" then "What should I change?" gets you an explanation grounded in the actual error.
- **Start from templates.** "Which template fits a talking-head video with a green-screen phone in it?" lets the assistant pick a tested starting point and adjust it.
- **Be explicit about money.** When a workflow generates video, say "reuse the clips" or "regenerate them" up front.
- **Name things exactly when deleting.** "Delete the workflow Highlight edit from footage v1" is faster than "delete the old one".
- **Read the card before you allow it.** It names exactly what will change and what it will cost. If it is not what you meant, press **Don't allow** and say what you want instead.
- **Check after big changes.** Ask "show me the steps of this workflow now" to confirm what was applied.
