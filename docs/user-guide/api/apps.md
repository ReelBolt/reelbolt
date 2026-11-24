---
title: Run an app
sidebar_position: 4.5
description: Find an app in a project, read what it asks for, upload a clip, estimate the cost, start a run with your inputs, wait for the result and download it, with curl and a complete JavaScript example.
---

# Run an app

An app is a workflow in one of your projects. This page walks through running one from your own code with the
same key, scopes and credits you already use for the rest of the API: find the app, read what it asks for,
upload a clip, check the cost, start the run, wait for it to finish and download the result.

The key needs these scopes: `workflows.read`, `files.read`, `files.write`, `runs.read`, `runs.execute` and
`outputs.read`. See [Keys](./keys.md) to create one.

All examples use these variables:

```bash
export REELBOLT="https://<your ReelBolt address>/api/public/v1"
export REELBOLT_KEY="rbk_..."
export PROJECT_ID="<project id>"
```

## 1. List the apps of a project

```bash
curl -s "$REELBOLT/projects/$PROJECT_ID/workflows" -H "Authorization: Bearer $REELBOLT_KEY"
```

The answer holds `data` and `nextCursor`; pass `?cursor=` to get the next page. Each entry in `data` has an
`id`, a `name`, a short `description`, its `steps`, a `costClass`, `appInputs` and `inputSchema`.

## 2. Read what the app asks for

Every app lists its fields in `appInputs`. Each field has a `kind`:

- `input`: a typed field with an `id` and a `type`. You send its value as `brief.inputs[<id>]` when you start
  the run.
- `brief`: a brief field with a `field` name. You send its value as `brief.<field>`.
- `files`: files with a `role` (such as `video`). You attach them in `brief.sourceFiles` with that role; `min`
  and `max` are the number of files.
- `notes`: free text. You send it as `brief.notes`.

Fields can also carry `label`, `required`, `options` (each with a `value` and a `label`), `unit`, `min`, `max`,
`default`, `help`, `example`, `maxLength` and `step`.

The `allowMediaEgress` field is a permission: it is never taken from its `default`. Send it yourself in
`brief.inputs` when you want to allow it; see [Runs](./runs.md).

The same answer has `inputSchema`: a JSON Schema that describes the `brief.inputs` object, so you can check a
value before you send it. It is built from the same fields. A field you do not find in `appInputs` is not
something the app asks for.

The schema lists only what it can state exactly. Model choices are not listed: leave a model field out to use the app's default. A field with a fractional step has no `multipleOf`, and the run still checks it.

## 3. Upload a clip

An upload has three calls. Send an `Idempotency-Key` on the first and the last.

1. Ask for an upload address. `sizeBytes` is required.

   ```bash
   curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/files/uploads" \
     -H "Authorization: Bearer $REELBOLT_KEY" -H "Content-Type: application/json" \
     -H "Idempotency-Key: $(uuidgen)" \
     -d '{"fileName":"clip.mp4","sizeBytes":1048576,"mimeType":"video/mp4"}'
   ```

   The `201` answer has `uploadId`, `mode`, `parts` (each with `partNumber`, `url` and `expiresAt`),
   `partSizeBytes` and `headers`.

2. Send the file to the address. When `mode` is `single`, make one `PUT` of the whole file to `parts[0].url` and
   include every header in `headers`.

   ```bash
   curl -s -X PUT "<parts[0].url>" -H "Content-Type: video/mp4" --data-binary @clip.mp4
   ```

   When `mode` is `multipart`, send each slice of `partSizeBytes` to its part `url` and keep the `ETag` header
   of each answer.

3. Finish the upload. For `single` the body is empty; for `multipart` list the parts with their `etag`.

   ```bash
   curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/files/uploads/$UPLOAD_ID/complete" \
     -H "Authorization: Bearer $REELBOLT_KEY" -H "Content-Type: application/json" \
     -H "Idempotency-Key: $(uuidgen)" \
     -d '{}'
   ```

   The answer is the file, with its `id`. Use that id in the run.

To bring in a file from a public address instead, use `POST /projects/{projectId}/files/import` with a `url`.

## 4. Estimate the cost

```bash
curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/workflows/$WORKFLOW_ID/estimate" \
  -H "Authorization: Bearer $REELBOLT_KEY" -H "Content-Type: application/json" \
  -d '{"videoFileIds":["<file id>"]}'
```

The answer has `low`, `high`, `typical`, `required`, `balance`, `affordable` and `keyBudget` (what is left of
the key's caps). See [Limits and credits](./limits-and-credits.md).

## 5. Start the run

```bash
curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/workflows/$WORKFLOW_ID/runs" \
  -H "Authorization: Bearer $REELBOLT_KEY" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{
    "acceptEstimateCredits": 12.5,
    "brief": {
      "sourceFiles": [{ "role": "Video", "fileIds": ["<file id>"] }],
      "notes": "Keep it short.",
      "inputs": { "<input id>": "<value>" }
    }
  }'
```

- `acceptEstimateCredits` is the cost you saw in the estimate. Send the `required` value. Only an app that
  builds a design profile checks it; other apps ignore it.
- `brief.sourceFiles` attaches files. `role` is one of `Video`, `Code`, `Music`, `Sfx`, `Image`, `Document`,
  `InsertContent` or `Overlay`.
- `brief.inputs` holds the typed inputs of the app, by `id`. Other brief fields, such as `language`, `lengthSec`
  or `notes`, sit directly in `brief`.

The answer is `201` with the run and its `id`. Over the hourly limit it is `202` and the start is queued; see
[Runs](./runs.md). Use the same `Idempotency-Key` to retry a start safely: a lost answer never starts a second
run.

## 6. Wait for the result

```bash
curl -s "$REELBOLT/projects/$PROJECT_ID/runs/$RUN_ID" -H "Authorization: Bearer $REELBOLT_KEY"
```

Ask every few seconds until `status` is `succeeded`, `failed` or `cancelled`. On a failure, `failure.code` and
`failure.message` say why. To avoid polling, subscribe to the `run.succeeded` and `run.failed` events; see
[Webhooks](./webhooks.md).

## 7. Download the output

```bash
curl -s "$REELBOLT/projects/$PROJECT_ID/runs/$RUN_ID/outputs" -H "Authorization: Bearer $REELBOLT_KEY"

curl -s -X POST "$REELBOLT/projects/$PROJECT_ID/outputs/$STEP_RESULT_ID/link" \
  -H "Authorization: Bearer $REELBOLT_KEY"
```

Each output has a `stepResultId`, a `fileName` and a `kind`. The link answer is `{ "url", "expiresAt" }`. The
link works for 15 minutes; ask again when it expires. While a content check is still running, the answer is
`202` with a `Retry-After` header; try again after that many seconds.

## Complete JavaScript example

This script runs on Node 20 or later and needs no packages. It finds the app by name, uploads a clip, checks the
cost, starts the run, waits and prints the download link. Set `REELBOLT`, `REELBOLT_KEY` and `PROJECT_ID`, then run
`node run-app.mjs ./clip.mp4 "My app name"`.

```javascript
import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { basename } from "node:path";

const base = process.env.REELBOLT;
const key = process.env.REELBOLT_KEY;
const projectId = process.env.PROJECT_ID;
const [filePath, appName] = process.argv.slice(2);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class ApiError extends Error {
  constructor(status, error, retryAfter) {
    super(error?.message ?? `HTTP ${status}`);
    this.status = status;
    this.code = error?.code;
    this.body = error;
    this.retryAfter = retryAfter;
  }
}

// Calls the API, returns the JSON answer and throws an ApiError on any error.
// On 429 it waits for Retry-After and tries again, up to three times.
async function api(method, path, body, headers = {}, attempt = 0) {
  const res = await fetch(`${base}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${key}`,
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...headers,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (res.ok) return { status: res.status, headers: res.headers, json };

  const retryAfter = Number(res.headers.get("Retry-After")) || undefined;
  const error = json?.error;
  if (res.status === 429 && attempt < 3) {
    await sleep((retryAfter ?? 5) * 1000);
    return api(method, path, body, headers, attempt + 1);
  }
  if (res.status === 402 && error?.code === "INSUFFICIENT_CREDITS") {
    throw new ApiError(res.status, { ...error, message: "Not enough credits. Add credits and run again." });
  }
  if (res.status === 402 && error?.code === "API_KEY_BUDGET_EXCEEDED") {
    throw new ApiError(res.status, { ...error, message: `Key cap reached. Try again after ${error.resetsAt}.` });
  }
  throw new ApiError(res.status, error, retryAfter);
}

// 1. Find the app by name.
const apps = (await api("GET", `/projects/${projectId}/workflows`)).json;
const app = (apps.data ?? apps).find((w) => w.name === appName);
if (!app) throw new Error(`No app named "${appName}" in this project.`);
console.log("Inputs:", app.appInputs.map((i) => i.id ?? i.field ?? i.role ?? i.kind));

// 2. Upload the clip.
const bytes = await readFile(filePath);
const upload = (
  await api(
    "POST",
    `/projects/${projectId}/files/uploads`,
    { fileName: basename(filePath), sizeBytes: bytes.length, mimeType: "video/mp4" },
    { "Idempotency-Key": randomUUID() },
  )
).json;
if (upload.mode !== "single") throw new Error("This clip needs a multipart upload; see the steps above.");
const put = await fetch(upload.parts[0].url, { method: "PUT", headers: upload.headers, body: bytes });
if (!put.ok) throw new Error(`Upload failed with HTTP ${put.status}`);
const file = (
  await api("POST", `/projects/${projectId}/files/uploads/${upload.uploadId}/complete`, {}, {
    "Idempotency-Key": randomUUID(),
  })
).json;

// 3. Estimate.
const estimate = (
  await api("POST", `/projects/${projectId}/workflows/${app.id}/estimate`, { videoFileIds: [file.id] })
).json;
console.log("Estimate:", estimate);
if (!estimate.affordable) throw new Error("Not enough credits for this run.");

// 4. Start the run. The Idempotency-Key makes a retry safe.
const started = await api(
  "POST",
  `/projects/${projectId}/workflows/${app.id}/runs`,
  {
    acceptEstimateCredits: estimate.required,
    brief: {
      sourceFiles: [{ role: "Video", fileIds: [file.id] }],
      // inputs: { "<input id>": "<value>" },
    },
  },
  { "Idempotency-Key": randomUUID() },
);
if (started.status === 202) throw new Error("The start is queued; see the run-requests endpoints.");
const runId = started.json.id;

// 5. Poll until the run ends.
let run;
do {
  await sleep(5000);
  run = (await api("GET", `/projects/${projectId}/runs/${runId}`)).json;
  console.log("Status:", run.status);
} while (!["succeeded", "failed", "cancelled"].includes(run.status));
if (run.status !== "succeeded") throw new Error(`Run ${run.status}: ${run.failure?.code} ${run.failure?.message}`);

// 6. List the outputs and get a 15 minute link for each.
const outputs = (await api("GET", `/projects/${projectId}/runs/${runId}/outputs`)).json;
for (const out of outputs.data ?? outputs) {
  let link;
  for (;;) {
    const res = await api("POST", `/projects/${projectId}/outputs/${out.stepResultId}/link`);
    if (res.status !== 202) { link = res.json; break; }
    await sleep((Number(res.headers.get("Retry-After")) || 5) * 1000);
  }
  console.log(out.fileName, link.url, "expires", link.expiresAt);
}
```

To handle errors in your own code, catch `ApiError` and branch on `error.code`; see [Errors](./errors.md) for
the codes. The script above waits and retries on `429`, and explains `INSUFFICIENT_CREDITS` and
`API_KEY_BUDGET_EXCEEDED`, which a retry does not fix.

## What is not offered yet

- There is no list of apps across all projects. List the apps of one project at a time.
- There are no keys for a single app. A key covers the projects on its project list.
- There is no call that waits for a run to finish and returns the result. Poll the run, or use a
  [webhook](./webhooks.md).

## Related pages

- [Keys](./keys.md), for scopes and project lists.
- [Runs](./runs.md), for queued starts and run control.
- [Limits and credits](./limits-and-credits.md), for caps and the estimate.
- [Errors](./errors.md), for the error format and codes.
- [Webhooks](./webhooks.md), to be told when a run ends.
