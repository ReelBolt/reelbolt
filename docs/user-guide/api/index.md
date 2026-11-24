---
title: The ReelBolt API
sidebar_position: 1
description: What the ReelBolt public API is for, its base URL, which plans include it, and a curl quick start that creates a key, starts a run from a template, receives a webhook and fetches the output link.
---

# The ReelBolt API

The ReelBolt API lets your own scripts and services do what you do in the dashboard: upload files,
create a workflow from a template, start a run, follow it, and download the result. It uses the same
credits and prices as the dashboard.

## Base URL and availability

All requests go to:

```text
https://<your ReelBolt address>/api/public/v1
```

Requests and responses are JSON with camelCase names. Running and uploading are
available on paid plans (Creator and up). A trial workspace can create a key and read with it (list apps,
read an app's input schema, estimate a run, list runs and files). Running, uploading, changing webhooks,
deleting and creating output links answer `402` with `API_PLAN_READ_ONLY`.

## Quick start

The examples use two shell variables. Set them first:

```bash
export REELBOLT=https://<your ReelBolt address>/api/public/v1
export REELBOLT_KEY=rbk_...   # the secret you copy in step 1
```

### 1. Create a key

An Owner or Admin of the workspace opens the API keys page in the dashboard, chooses **Create key**,
picks the scopes the script needs (for this quick start: `projects.read`, `workflows.read`,
`workflows.write`, `runs.read`, `runs.execute`, `outputs.read`, `webhooks.manage`) and copies the secret.
The secret is shown once. See [Keys](./keys.md).

### 2. Check the key with `/me`

```bash
curl -s "$REELBOLT/me" -H "Authorization: Bearer $REELBOLT_KEY"
```

The answer names the key, the workspace, its scopes, its project list, its expiry, its caps and how much of
each cap is used.

### 3. Start a run from a template

List the templates, create a workflow in one of your projects, then start it:

```bash
curl -s "$REELBOLT/templates" -H "Authorization: Bearer $REELBOLT_KEY"

curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/workflows" \
  -H "Authorization: Bearer $REELBOLT_KEY" -H "Content-Type: application/json" \
  -d '{"templateKey":"<a key from /templates>","name":"From the API"}'

curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/workflows/$WORKFLOW_ID/runs" \
  -H "Authorization: Bearer $REELBOLT_KEY" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{}'
```

Send an `Idempotency-Key` on every `POST` that creates or starts something (projects, uploads, upload complete, import, workflows, runs, run control, and webhook create, test and redeliver) so a retry never starts a second run. See
[Limits and credits](./limits-and-credits.md). If you started too many runs this hour, the start is
queued instead of refused; see [Runs](./runs.md).

### 4. Get a webhook when it finishes

```bash
curl -s -X POST "$REELBOLT/webhooks" \
  -H "Authorization: Bearer $REELBOLT_KEY" -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/reelbolt-hook","events":["run.succeeded","run.failed"]}'
```

The answer contains the signing secret, shown once. ReelBolt then calls your URL when a run ends. See
[Webhooks](./webhooks.md).

### 5. Fetch the output link

```bash
curl -s "$REELBOLT/projects/$PROJECT_ID/runs/$RUN_ID/outputs" -H "Authorization: Bearer $REELBOLT_KEY"

curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/outputs/$STEP_RESULT_ID/link" \
  -H "Authorization: Bearer $REELBOLT_KEY"
```

The link answer holds a `url` and an `expiresAt`. The link works for 15 minutes.

## Where to go next

- [Keys](./keys.md): scopes, project restriction, expiry, revoking.
- [Limits and credits](./limits-and-credits.md): caps, rate limits, idempotency.
- To run an app with your own inputs and files, see [Run an app](./apps.md).
- [Runs](./runs.md), [Webhooks](./webhooks.md), [Errors](./errors.md) and the [Reference](./reference.md).
