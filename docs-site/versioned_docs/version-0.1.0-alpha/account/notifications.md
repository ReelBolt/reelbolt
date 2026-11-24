---
title: Notifications
sidebar_position: 5
description: The four email notifications ReelBolt can send you, the switches that turn each one on, who receives each kind, and how delivery works.
---

# Notifications

ReelBolt can send you an **email when something happens in a workspace**: a video job finishes or fails, a file is added, or a thumbnail graphic is edited. Each kind has its own switch, so you choose which ones to receive. You change these in **Settings → Notifications** in the left menu.

## Where to change them

Go to **Settings → Notifications** in the left menu. The page has one switch per kind of email, and a **Recent activity** list at the bottom that shows the notifications delivered most recently.

- **Job finished** — an email when a video job completes successfully.
- **Job failed** — an email when a video job fails, including what went wrong.
- **File added** — an email when a file is added to a project.
- **Thumbnail graphic edited** — an email when a thumbnail or graphic in a project is edited.

All four are **on by default**. Flipping a switch off stops that kind of email for you, and the change takes effect straight away — there is no save button. If a switch does not respond, the page shows why (for example, if it could not load your settings), and turning it back on simply flips the switch back.

## Who receives each kind

- **Job finished** and **Job failed** go to **the person who started the job**. If a job was started by the system rather than by a person, it goes to the workspace **Owner** instead, so a failure is never lost.
- **File added** and **Thumbnail graphic edited** go to **everyone in the workspace except the person who made the change** — the person who did the work is not emailed about their own action. If the change was made by the system (not a person), it reaches **everyone** in the workspace.
- A change you make in a workspace where you are the **only member** does not email you: the person who acted is always left out, and when you are the only one, that leaves no one to tell.

## How delivery works

Notifications are sent through the same email setup the installation already uses for account emails (password resets and invitations). If that email setup is not configured, the notification is still **recorded** and logged to the server instead of being sent, so it is never lost — it just does not leave the installation.

Every delivered (or attempted) notification is recorded, and the most recent ones appear in the **Recent activity** list on the page, with a marker for whether each one was delivered or could not be sent.

**A notification never gets in the way of the work.** A job that finishes, a file that uploads, and a graphic that edits all complete normally even if the email cannot be sent — the email is a side note, not part of the operation.
