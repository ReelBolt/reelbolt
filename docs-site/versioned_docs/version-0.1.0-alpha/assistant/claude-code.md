---
title: Using ReelBolt from Claude Code
sidebar_position: 2
description: Connecting the Claude Code command-line tool to ReelBolt so it can work with your projects, workflows and pictures — signing in, what it is allowed to do, the optional admin tools setting, what it costs, and how to disconnect.
---

# Using ReelBolt from Claude Code

Claude Code is a command-line AI assistant. You can connect it to ReelBolt so that, instead of
switching to the dashboard, you can ask it in your terminal to look at your projects, inspect a
workflow, check how a run went, or change a workflow for you. It uses the same tools and the same
permissions as the ReelBolt assistant that lives in the dashboard — it is another way to reach the
same machinery, not a second copy of it.

This page is for people who already use Claude Code and have a ReelBolt account. You do not need to
know how ReelBolt is built to follow it.

## What Claude Code can do once it is connected

Once connected, Claude Code gets a set of ReelBolt tools. Broadly, it can:

- **Look things up.** List your projects, read a project's files and the agent list, list a project's
  workflows, and read the steps of one workflow.
- **Check on runs.** List recent runs and open one to see each step's status, how long it took, how
  many credits it used, and the full reason a step failed. Claude Code can also **wait** for a run to
  finish (it checks back for you, up to about a minute and a half at a time, and shows how far the run
  has got) instead of asking again and again. If a run you asked for in 4K could only be made
  smaller, the run result says so in a plain sentence.
- **Check your credits.** Ask how many credits you have, how many are held by runs in progress, and
  when your plan renews.
- **Draft and change workflows.** Check a set of steps is valid, show you what a change would do, and
  apply it after you agree.
- **Add files to a project.** Upload an audio, video or image file from your computer (up to the
  storage your plan allows; large files are sent in parts), or import a file from a public web
  link (`https://`, up to 512 MB). Claude Code shows you what it is about to add and you approve it.
- **Get a link to a result.** For any finished video or file in a project, Claude Code can give you
  its size, length and shape, a short-lived link to download it without signing in again (the link
  works for about 15 minutes), and a few grids of frames from the video so it can describe what the
  video looks like. If the frames are not ready yet it tells you to ask again in a minute.
- **Keep a result as a project file.** Copy a finished video into the project's files so that later
  runs can use it, for example as music or as a clip to insert. The original stays where it is, and
  the copy counts toward your storage.
- **Put a screen recording on a green screen.** Say "put this screen recording onto the green screen in
  that clip". Claude Code checks the clip and the recording, shows you the workflow it would save (or update,
  if one with that name exists) and, once you agree, saves it. It uses no AI model (only processing time is charged), and it does
  not start the run: ask Claude Code to run the workflow afterwards.
- **Change a finished video.** For an animated video ReelBolt made, ask for a change ("make the title
  bigger in the last scene") and Claude Code shows you the scenes it can change and the estimated
  cost before you agree. A change starts a new run and counts as a paid run for the hourly limit
  below.
- **Start a run.** Kick off a workflow, subject to the same hourly limit the dashboard assistant has
  (see [Run limits](#run-limits)).
  You can ask for an editing style, a video shape and a resolution for the run ("run it as a vertical
  video in 4K"); the `ExecuteWorkflow` tool takes these as `styleId` or `stylePresetKey`,
  `outputFormat` and `outputResolution` (`Auto`, `720p`, `1080p` or `2160p` for 4K).
- **Work with the picture open in the picture editor.** Read what a picture is made of, look at what
  it actually shows, and propose changes such as adding text, moving or deleting layers, changing the
  canvas, or running a paid picture tool.

Every change is confirmed with you before it is written: Claude Code shows you what would change,
and nothing is written until you agree. Starting a run, deleting
something, and anything that costs money always come back to you first.

## Run limits

To protect your credits, there is a limit on how many runs Claude Code and the dashboard assistant
can start for you. It is counted for each person in each project over a rolling hour:

- **Runs that can use credits** (anything that uses AI models or paid services, and every change to a
  finished video): up to **10 per hour**.
- **Runs that are free** (only fixed editing steps, with no AI model or paid service in them): up to
  **60 per hour**. Free runs never use up your 10.

When you reach a limit, Claude Code tells you how many you used, what the limit is, and how long to
wait before trying again. Starting a run yourself with **Run** in the dashboard is not counted.

If you save a workflow change that makes a free run use paid services right after you asked for it,
the run stops before doing anything and is not charged. Claude Code says "This workflow changed
after the run started; start it again." Ask it to start the run again.

## Connecting

You connect by adding ReelBolt's MCP address to Claude Code. The address is your ReelBolt web address
followed by `/mcp` — for example `https://reelbolt.example.com/mcp`, or `http://localhost/mcp` on a
local installation.

ReelBolt ships a **Claude Code plugin** that does this for you: it registers the ReelBolt endpoint and
asks you for that one address and nothing else, so there is no token to create, paste or store. Add
its public marketplace and install it:

```bash
claude plugin marketplace add vecchiotom/reelbolt-claude-plugin
claude plugin install reelbolt@reelbolt --config server_url=https://reelbolt.ai/mcp
```

Use your own address instead of `https://reelbolt.ai/mcp` if you self-host, and approve in the
browser as described below. You can equally add the server yourself; the steps are the same either
way. The plugin's README has the full install guide, including how to change the address later and
how to load it from a checkout of the ReelBolt source.

Having set that address, Claude Code then:

1. Registers itself with ReelBolt automatically.
2. Opens your browser at ReelBolt's sign-in page if you are not already signed in.
3. Shows you a consent screen naming what is being asked for, and who is asking.
4. Once you approve, gives Claude Code a credential it stores and uses from then on.

You approve once. If the connection is later revoked, you connect again the same way.

The consent screen asks for **use of ReelBolt's assistant tools**. If admin tools have been enabled
for your account (see below), it also says so, because that is a wider permission. Denying the
request simply returns you to Claude Code with nothing connected.

## Admin tools over MCP are off by default

ReelBolt's assistant has some tools that only administrators may use — the ones that manage the
deployment's own AI provider settings. Those tools are **not** available over Claude Code unless they
have been enabled for your account:

- When they are off (the default), ReelBolt does not even tell Claude Code that they exist. They
  cannot be called by accident or by a cleverly worded request.
- When they are on, they appear, and they still check your own permissions on every single call. The
  setting grants nothing by itself: if you are not an administrator, the call is refused.

Turning this on is a setting on your ReelBolt account, and it is not something Claude Code can turn
on for itself. At the time of writing there is no switch for it in the portal, so ask whoever
administers your ReelBolt installation to enable it for you; then reconnect Claude Code.

## What it costs

ReelBolt records **every** call Claude Code makes — what was asked for, whether it was allowed to
run, and how long it took. That record exists so usage is visible and so unusual activity can be
seen. A record is not a charge.

**Credits are only drawn where the work itself costs ReelBolt money**, exactly as when you use the
dashboard:

- Looking around — projects, files, workflows, run history, your credit balance, links and frames
  for a result, a picture's layers — is free.
- Adding a file or keeping a result as a file is free of credits, but the file counts toward your
  storage.
- **Starting a run** spends credits as the run's steps use paid services.
- **Changing a finished video** spends credits, and Claude Code shows the estimate first.
- **Paid picture tools** (erasing, filling, expanding, relighting, upscaling, generating) and
  **designed graphics** spend credits when you apply them. Claude Code is told the estimated cost
  when it proposes one, so it can tell you before you agree.
- **AI-generated video clips** are the most expensive item. Claude Code refuses to start a run that
  would generate them until you have chosen whether to reuse the clips from last time (free) or
  generate new ones (paid).

See [Plans and credits](../account/plans-and-credits.md) for how credits work in general.

## Disconnecting

Removing the ReelBolt server from Claude Code stops it using your account.

A ReelBolt credential that has already been handed out cannot be withdrawn on the spot: it is a
signed token that stays valid until it expires, within the hour. What can be stopped is its
**renewal**. Revoking the connection ends the grant that keeps issuing new credentials, so the
connection lapses shortly instead of being cut off this second. ReelBolt's OAuth endpoint exists for
this; at the time of writing there is no control for it in the portal, so ask whoever administers
your ReelBolt installation.

After that, connecting again means approving a new connection from scratch.

## Running the server yourself (advanced)

Instead of connecting to a hosted ReelBolt, you can run the ReelBolt MCP server locally and use your
own sign-in token. **The plugin does not set this up** — it registers the hosted endpoint described
above. Running the server locally is a separate, manual setup, intended for self-hosted installations
and advanced users; it is documented in the plugin repository's README. The short version is that the
local server needs two things from you: the ReelBolt address to call, and a ReelBolt token for your
own account. It never needs, and should never be given, a platform signing key.

## If it does not work

- **Claude Code says it needs to sign in.** Run the connection again; the browser step has to be
  completed by a person. Nothing connects silently.
- **Claude Code cannot see ReelBolt's tools.** Check the server address ends in `/mcp` and that the
  session is signed in. Then reconnect the server.
- **Calls are refused with a permissions message.** The tool exists but your account may not use it,
  or admin tools are not enabled for you. Refusals like this are answers, not crashes — they say what
  to do next.
- **A run is refused because of too many runs.** The dashboard assistant and Claude Code share an
  hourly limit on how many runs one person can start in one project (see [Run limits](#run-limits)).
  The message says how many you used and how long to wait. Wait that long, or start the run yourself
  with **Run** in the dashboard.
- **Claude Code says the session was not found.** After ReelBolt restarts, a connection that was
  open before the restart is no longer known. Claude Code connects again by itself; if it does not,
  reconnect the server.
- **A link to a result stopped working.** Links last about 15 minutes. Ask Claude Code for a new one.
  A link also stops working if you are no longer a member of the workspace.
- **A file import was refused.** Only public `https://` links work, the file has to be 512 MB or
  smaller, and it has to fit in your storage. Links to private or internal addresses are refused.
- **A run is refused because of the resolution.** Your plan sets the highest resolution a run can
  use (720p on Trial, 1080p on Creator, 4K on Pro and Team). Ask for that resolution or lower, or
  leave it on Auto; see
  [Export resolution on each ReelBolt plan](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan).
