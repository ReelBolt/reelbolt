---
title: Desktop runner
sidebar_position: 4
description: The desktop runner — pairing a computer you own to render ReelBolt videos on it, the supported platforms, approving and revoking a device, the workspace compute preference, install and setup, settings, what runs where and what data leaves the machine, and troubleshooting.
---

# Desktop runner

A **desktop runner** is a computer you own — your laptop, a workstation, a spare box — that renders your ReelBolt videos instead of the cloud. You pair it to a workspace by approving a short code it shows, and from then on the work that can run on it runs on it. Rendering on your own machine uses **no ReelBolt credits**: the machine does the work, so you pay nothing to ReelBolt for that part. This page is about pairing, approving and revoking a device, the supported platforms, the workspace setting that decides when media work goes to a paired machine, installing and running the runner, its settings, and what data a runner ever sends anywhere.

## Installing and running a ReelBolt desktop runner

When a release is published, you download the installer from [reelbolt.ai/runner](https://reelbolt.ai/runner) and follow the [ReelBolt Runner](../runner/index.md) pages ([Windows](../runner/install-windows.md), [Linux](../runner/install-linux.md), [pairing](../runner/pairing.md)). Until a release is published, or if you prefer, you can build it from the ReelBolt source tree on the machine that will run it (this needs the Go toolchain and a copy of the source), then pair it and run it:

```sh
# Build the headless daemon (or the tray shell, which embeds it).
cd runner && go build -o reelbolt-runner ./cmd/reelbolt-runner
./reelbolt-runner pair --name "My workstation"
./reelbolt-runner run
```

`pair` starts the pairing flow described below and stores the result; `run` then serves that workspace's jobs until stopped. A runner you build yourself never installs updates on its own; to update it, build it again from the newer source. `doctor` is the one to run first: it checks ffmpeg, the container runtime, the pinned runtime image and the container's egress posture, and prints the exact capability document the gateway will route on. `status` shows the local configuration and device state, and names the reason the machine is not working if the governor has paused it.

**Where the runner lives on disk.** Configuration is a text file you can edit, `runner.json`, in your OS user config directory (for example `~/.config/ReelBolt/runner.json` on Linux, `%AppData%\ReelBolt\runner.json` on Windows), kept 0600. The device's private key lives in your OS keyring (or a 0600 file next to the configuration, with a warning, where the keyring is unreachable). Job scratch goes to your OS user cache directory and is deleted when the job ends. Nothing else of yours is read.

**The container runtime choice.** A machine that renders Remotion needs a container runtime: **Docker (24 or newer) or Podman (5 or newer)**. Which one to run on a laptop:

- **Podman** — no daemon, no GUI. The machine's own engine does the isolation; rootless and VM modes are both supported. For most personal machines this is the cleaner choice.
- **Docker Desktop** — a full GUI and a VM under the hood. On Windows it is the only choice; on Linux it is a matter of taste. Note the licensing: Docker Desktop is free for personal use and for organisations of up to 250 employees; a larger organisation needs a Docker Desktop subscription. Podman has no such licence.
- **WSL2** (Windows): run the runner and Podman inside the Linux distribution; the container's no-egress network is the isolation boundary, and WSL2's own networking is outside it.
- **No runtime at all** — the runner still works: it then offers only the ffmpeg work (video compiles and analysis extraction), as host processes, and cannot take Remotion renders.

The runtime's sandbox network is a `--internal` (no-egress) network the runner creates and verifies at startup: a live probe from inside a container to the public internet must fail. If it cannot be created, the machine degrades to the ffmpeg job types and says why, rather than failing to start.

**Running it unattended.** The runner drains on SIGTERM (waits `drainSeconds`, default 5, then closes), so a service manager is the normal supervisor: a Linux **user** systemd unit (so the keyring is reachable) or a Windows service. Run it under one if you want it to keep running unattended.

## Using the ReelBolt desktop app

The desktop app has four screens, in the menu on its left.

- **Home** says, in one line, what your computer is doing right now: ready, working on a job (with which project and how far along), paused, or what needs fixing. **Pause** stops it taking new jobs until you press **Resume**; a job already running finishes. Until the computer is fully set up, Home also shows the setup steps: **Connect to your workspace** (you approve a code in your browser), **Set up video rendering** (Docker or Podman, then a one-time download of the video toolkit, with its progress shown) and a one-time question about crash reports.
- **Activity** lists the jobs this computer ran since the app started, newest first, with how each one ended on this computer: **Done**, **Failed** (with the reason), **Took too long**, **Cancelled** or **Interrupted**. "Done" means the computer finished its part; whether the whole video came out right is shown on the run's page in ReelBolt, which the arrow next to a job opens.
- **Folders** keeps folders on your computer copied into projects. See the next section.
- **Settings** holds everything you can change, below.

## Desktop app settings

Each group saves on its own, and every change is checked before it is saved, so a mistake cannot leave the app half-configured.

- **This computer**: its name, the workspace it works for and who connected it. **Remove** takes it out of the workspace: it stops taking jobs, disappears from the workspace's list of computers, and its folders stop syncing. Files already in ReelBolt stay. Turn on "Also delete this computer's saved identity" when you give the computer to someone else.
- **When to work**: **Any time**, **When I'm away** (after the computer has been idle for a number of minutes you choose), **When plugged in**, or **During set hours** (days and times; an end time earlier than the start runs overnight). You can also add set hours on top of the other choices, and **Pause on battery**. Pausing from Home always wins over the schedule.
- **How much to use**: how many jobs run at the same time, the memory and processor cores each job may use, its **priority** (Lower keeps your own apps responsive while a job runs), and the disk space each job may fill. Values larger than the computer has are lowered to what it has.
- **Storage**: footage a job downloads is kept on the computer so the next job that uses the same file starts without downloading it again. **Keep up to** sets the limit (the oldest files go first when it is reached) and **Empty** clears it. **Clean up** removes files left behind by jobs that ended.
- **Startup and updates**: open the app quietly in the system tray when you sign in, install updates automatically (only while no job is running), and **Get early versions** to try new features first.
- **Privacy**: crash reports are off until you turn them on. **See what is sent** shows exactly what a report contains and what it never contains (files, folders, names, email addresses, projects). **Diagnostics file** saves a file you can attach when you contact support; it is never uploaded for you.

### For administrators: the settings file

The app writes its settings to `runner.json` in your user configuration folder (for example `~/.config/ReelBolt/runner.json` on Linux, `%AppData%\ReelBolt\runner.json` on Windows). Editing it by hand is possible but never needed; the headless `reelbolt-runner status` command prints it.

| Setting | Default | What it does |
| --- | --- | --- |
| `slots` | 1 | Jobs at the same time, up to 16. |
| `limits` | 4 GB memory, 2 cores, `below-normal` priority, 8 GB disk per job | The per-job caps shown under **How much to use**. |
| `schedule` | any time, on battery too | `mode` (`always`, `idle`, `ac`, `windows`), `idleMinutes`, `windows` (`{days, start, end}`), `onBattery` (`allow` or `pause`). |
| `mediaCache.maxBytes` | 10 GB | The **Keep up to** limit for downloaded footage; 0 turns the cache off. |
| `updateChannel` | `stable` | `stable` or `beta` (**Get early versions**). |
| `autoUpdate` | on | Install updates automatically. |

**Pausing from the command line.** `reelbolt-runner pause` and `resume` do the same as the app's buttons. A pause is never kept across a restart: restarting always comes back working.

**You cannot pause from the dashboard.** Nothing in the dashboard sends a pause to a paired machine. To stop a machine, remove it (see "Revoking a paired machine") or pause it on the machine itself.

## Copying a folder from your computer into a project

The desktop app can also keep a folder on your computer copied into a ReelBolt project — one-way, your
computer to ReelBolt. Open the app's **Folders** screen, choose **Add a folder**, pick a folder and the
project it should copy into, and ReelBolt keeps the project the same as the folder from then on. This is how
you get **iCloud Drive** into ReelBolt: Apple does not let a website read it, but iCloud for Windows shows it
as a normal folder, and ReelBolt copies from it like any other. OneDrive, Dropbox, a NAS share and a plain
folder work the same way. The full guide is [Copying a folder from your computer into a
project](../projects/local-folder-sync.md).

Three things to know:

- **Windows and Linux only.** There is no Mac version of the desktop app, so this feature is not
  available on a Mac.
- **It syncs while the app runs**, not only while a render is happening. Keep the app open and changes reach
  the project within seconds; anything changed while it was closed is copied when it starts again.
- **What leaves your machine is only the files in the folder you chose.** Nothing outside that folder is
  read, nothing is ever written back to your computer or your cloud storage, and files you uploaded yourself
  (or an agent wrote) are never touched.

**To stop it**, open the folder's **⋯** menu and choose **Stop syncing**: the files already copied stay in the
project, and nothing is deleted. **Pause syncing** in the same menu stops copying for a while without unlinking
the folder. Closing the app stops
folder sync entirely until you open it again.

## What runs where, and what data leaves your machine

The division is the same in every section of this page, so it is stated once and relied on:

**Runs on your machine** — the media work: Remotion renders, video compiles (ffmpeg), and video analysis extraction. A machine with no container runtime runs only the ffmpeg half, as host processes.

**Stays on the platform, never on the runner** — the model calls and all AI thinking, transcription (ASR), on-screen description (vision) generation, object tracking, the derush pre-pass, input screening, and billing. These need provider credentials, and no message the runner ever receives carries one.

**What leaves the machine, in total:**

- The outbound WebSocket to the ReelBolt gateway (the runner never listens on a port), carrying the work — the job spec and the operations — and carrying back the results.
- The files a job needs: project sources and voiceover WAVs are **staged to you from presigned object-store URLs** (a GET), and the rendered output is **published to a presigned URL** (a PUT) in the job's own key space. Binary data never travels over the socket.
- The machine's name, OS, architecture and runner version, advertised at pairing and in the capability document. There is no geolocation and no IP-address lookup. The approval screen shows the raw address the pairing request came from and nothing else about it.
- Crash reports, **only if you turn them on** in **Settings → Privacy**. A report names the kind of error and where in the app's own code it happened, and never a file, folder, name, address or project; **See what is sent** shows the exact report before you decide.

**What is checked before it is used.** A runner's output is **untrusted** by construction — the binary is controlled by its owner. Every runner-produced artifact is verified by the platform (size, duration, frame size, codec, frame rate, checksum) before it is promoted to the project's files; a rejected output fails the step. A step that ran on your machine is tagged in the run's step details, and the run page carries a chip when at least one of its steps did. The platform also re-checks the device's revocation state, so a revoked machine stops receiving work even before its next connection.

**What the platform never gets to see:** your other files, your keyring, anything outside the job's scratch directory (deleted when the job ends), and the container's network is a `--internal` no-egress network, so a job cannot phone home.

## Troubleshooting a paired machine

The full error-message catalogue is on the [Troubleshooting](../troubleshooting.md) page; the runner's own entries are there. The four situations that need no page:

- **The machine is not taking work, and `status` says why.** Read the line: `user-active` (you are at the keyboard — in `idle` mode), `on-battery`, `outside-window`, or `paused`. `resume` clears an explicit pause without editing a file; a schedule gate names when it next changes, and the same line of `status` says `resume` is a no-op against it. If nothing responds at all, kill the process: a pause is never persisted, so a restart always comes back working.
- **`NO_RUNNER_ONLINE` in a step's error.** Your workspace is set to **Only my machines** and no paired machine was online and able to take the step. That choice never falls back to the cloud; start (or leave on) the machine, make sure it is paired to this workspace on **Runners**, and rerun the step. See the [Troubleshooting](../troubleshooting.md) page for the full entry.
- **The machine shows nothing in the capability document for renders.** `doctor` names it: no container runtime (Docker or Podman), the runtime is older than the minimum (Docker 24, Podman 5), or the `--internal` no-egress network could not be created. An ffmpeg-only machine cannot take Remotion renders; fix the runtime and `doctor` again.
- **You revoked the machine and want it back.** Revoke removes it from the list; to use it again, connect it afresh from the app's **Home** screen (or `reelbolt-runner pair`). The device's private key is unchanged; only the workspace's record of it was deleted.
- **The app says "Set up video rendering".** Install Docker Desktop or Podman Desktop, open it once, then press **Check again**; the app then offers to download its video toolkit and shows the download's progress.

## Supported platforms for a ReelBolt desktop runner

A runner is available for **Windows (x64)** and **Linux (x64 or arm64)**. **macOS is not supported.** A device on any other platform is shown with the raw values it reported, not folded into a supported-looking label.

A machine gets the runner from the installer on [reelbolt.ai/runner](https://reelbolt.ai/runner) once a release is published (see [ReelBolt Runner](../runner/index.md)), or by building it from the source tree. A machine that has it shows a pairing code, and a workspace Owner or Admin approves it. How to build and run the binary, the container runtime choice (Docker Desktop's 250-employee licence, Podman, WSL2) and running it unattended are in [Installing and running a ReelBolt desktop runner](#installing-and-running-a-reelbolt-desktop-runner) above on this page.

## What a runner can do, and what it cannot

A paired machine runs the **media work** of a run: Remotion renders, video compiles, and video analysis extraction. Everything else — the model calls, the AI thinking, transcription, captioning, billing — stays on the platform. A machine that has no container runtime (Docker or Podman) can only do the ffmpeg work (compiling and extracting); a Remotion render needs a machine that reported a container runtime. Because a machine's own claims about what it can do are taken as given and then verified, the platform checks the output before it is used.

## Pairing a machine in ReelBolt

Pairing starts on the device, not in the dashboard.

1. On the machine, start the ReelBolt runner. It shows a short pairing code (eight characters, like `7HQD-4M2R`) and opens a link to ReelBolt's approval screen with that code in it.
2. In the workspace, open **Runners → Pair a device** (or type the code on the **Pair a runner** screen if you followed a bare link).
3. On the approval screen, check the code matches the one on the screen character for character, choose which workspace to pair the device into, and click **Approve this device** (or **Deny** to refuse it).

You can approve a device into a workspace where you are an **Owner or Admin**, or into your own **Personal** workspace. A device is paired into the workspace you pick, and only people in that workspace will see it.

**Only approve a code shown on a device you own.** Approving connects a computer to the workspace and lets it receive your media and render your videos. If someone sent you a code instead of a machine showing one, deny it — they are trying to attach their machine to your workspace. The screen also shows the machine's name, platform, version, when it was requested, its expiry, and the network address the request came from. It does not claim a location for that address: ReelBolt does not look one up.

When you approve, **every Owner of that workspace is emailed about it**, so a mistake is visible after the fact. A pairing code is short-lived; if it has expired, ask the device to show a fresh code.

## What the approval screen deliberately does not show

The approval screen shows the device's name, platform, runner version, the request time, the expiry, and the raw network address of the request. It does **not** claim a city or location for that address — ReelBolt has no geolocation in this product, so nothing here is ever labelled with a place. If a code is unknown, expired, already used, or denied, and a workspace you are not a member of, are all one and the same answer: **there is no matching pairing request.** ReelBolt does not distinguish them, so it never tells you that a code you typed is valid but in someone else's workspace.

## Revoking a paired machine in ReelBolt

On **Runners**, each paired machine has a **Revoke** action. Revoking disconnects it immediately and it receives no more work; it then disappears from the list — the workspace keeps no record of a revoked device here. To use the machine again, pair it afresh from the machine itself. A machine that is revoked does not show a "Revoked" badge; it is simply gone from the list, because the list is "what can run work right now".

## The compute preference in ReelBolt

On **Workspace → Settings**, under **Where your work runs**, an Owner or Admin chooses where media work runs. We use your paired machines for media work when they're online; everything else runs on ReelBolt.

The four choices, in display order:

- **Automatic (the default)** — use a paired machine when one is online; otherwise ReelBolt runs it.
- **Never use my machines** — media work always runs on ReelBolt, and pairing a machine changes nothing.
- **Prefer my machines** — use a paired machine whenever one is online and can take the step; otherwise ReelBolt runs it. A machine that is offline or busy is not waited for.
- **Only my machines** — media work runs only on your paired machines. If no paired machine is online and able to take the step, the step **fails with `NO_RUNNER_ONLINE`** and the run stops there — nothing is quietly run or billed on ReelBolt instead. Remotion renders need a machine that reported a container runtime, so an ffmpeg-only machine cannot take them; under this choice a render fails rather than running anywhere else. This is the only choice that turns an offline machine into a failed run instead of a queued one.

## What a runner does not pause from the dashboard

A paired machine can be paused and resumed from **the desktop app's Home screen or its command line**, but nothing in the dashboard sends a pause to it — there is no pause button on the Runners page. If you want a machine to stop taking work, revoke it (see above) or pause it on the machine itself. `reelbolt-runner status` names the reason it is not working, and `resume` clears an explicit hold without editing a file; the full setting names and defaults are in [Desktop app settings](#desktop-app-settings) above on this page.

## Related pages in ReelBolt

- [Workspaces and teams](../workspaces.md) — who can approve a device and the workspace it is paired into.
- [Copying a folder from your computer into a project](../projects/local-folder-sync.md) — the desktop app's folder sync: adding a folder, iCloud, pausing and what never happens.
- [Plans and credits](./plans-and-credits.md) — why work on your machine costs 0 credits, and the storage each plan includes.
- [Troubleshooting](../troubleshooting.md) — what `NO_RUNNER_ONLINE` means when a step fails, and the runner's other entries.
