---
title: API keys
sidebar_position: 2
description: Creating an API key, what its scopes allow, restricting it to projects, setting an expiry, the warning mails and badges, and revoking it.
---

# API keys

An API key is a secret that your script sends with every request. You manage keys in the dashboard.
Only Owners and Admins of a workspace create, see, change and revoke keys.

## Create a key

Choose **Create key** on the API keys page and fill in the name, the scopes, the optional project list,
the optional expiry and the optional credit caps. The secret looks like `rbk_<id>_<secret>`.

The secret is shown once, when you create the key. ReelBolt stores only a fingerprint and the last four
characters, so it cannot show the secret again. If you lose it, create a new key and revoke the old one. Keep the key on your server (environment variable or secret manager), never in browser or mobile app code or a repository; if it leaks, revoke it at once and create a new one.

Every new key gets a daily cap of 200 credits unless you set another value. Raising the daily cap above
200 (or above the key's current limit), or removing it, asks you to confirm. Keeping the same value,
lowering it (for example from 1000 to 500) or setting any value up to 200 needs no confirmation. In an API
call, send `confirmCaps: true` with the raise; without it the call answers `400` with
`cap_confirmation_required`. To remove the daily cap, send `noDailyCap: true` together with
`confirmCaps: true`. Sending `dailyCredits: null` on an edit leaves the daily cap unchanged.

Keys made before this change keep working with no daily limit. The key list shows a warning on each of
them until you set a daily cap.

You can later change only the name and the caps. To change scopes, projects or expiry, create a new key.

## Scopes

A key can do only what its scopes allow. Give a script the smallest set it needs.

| Scope | What it allows |
|---|---|
| `projects.read` | List and read projects |
| `projects.write` | Create projects (not for a key restricted to projects) |
| `files.read` | List files and get a file download link |
| `files.write` | Upload, import and delete files |
| `workflows.read` | List templates, list and read workflows |
| `workflows.write` | Create a workflow from a template, delete a workflow |
| `runs.read` | Estimate a run, list and read runs and run requests |
| `runs.execute` | Start, stop, resume and retry runs, cancel a queued start |
| `outputs.read` | List a run's outputs and create output links |
| `webhooks.manage` | Create, read, test and delete webhooks, rotate a secret |
| `media.egress` | Lets a run this key starts allow sending your media to AI services (`inputs.allowMediaEgress`). Off unless you tick it; no endpoint needs it |

A key never gets administrator rights, and it cannot reach workspace settings, billing or members.
Using the secret on a dashboard route is refused.

## Restrict a key to projects

Pick one or more projects when you create the key. The key then sees only those projects: any other project
answers "not found", and lists show only the allowed ones. A key restricted to projects cannot create new
projects.

## Expiry

Setting an expiry is optional. When set, it must be more than an hour away and at most 365 days away. For a
key with an expiry, ReelBolt mails a warning 7 days and again 1 day before it expires. An expired key is
refused with `api_key_expired`.

## Badges

The key list marks keys that need a look:

- **No expiry**: the key never expires. Consider setting a date on a new key.
- **Not used for 30 days**: nothing has called the API with this key for 30 days. Revoke it if you no
  longer need it.

The list also shows when each key was last used and how much of each cap is used.

## Who the key acts as

Each key acts as its own member of the workspace. It appears in "who did this" places as
"API key: <name>", for example as the person who started a run or uploaded a file. The key does not use a
seat, does not sign in, and gets no mail.

Because the key is its own member, it keeps working when the person who created it leaves the workspace.

## Revoke a key

Choose **Revoke** on the key. It stops working immediately. A run already in progress finishes its current
work, but the key cannot reserve credits for its next step. Revoked keys stay in the list so the history
keeps the key name.
