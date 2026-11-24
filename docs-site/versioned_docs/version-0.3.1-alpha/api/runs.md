---
title: Runs
sidebar_position: 4
description: The run lifecycle in the API - estimate, start, status, stop, resume and retry - the hourly run limit, queued starts, outputs, and short-lived download links.
---

# Runs

## Lifecycle

1. **Estimate.** `POST /projects/{projectId}/workflows/{workflowId}/estimate` returns what a run would cost
   (scope `runs.read`).
2. **Start.** `POST /projects/{projectId}/workflows/{workflowId}/runs` starts a run (scope `runs.execute`).
   The body is the same as the dashboard's run request. Send `acceptEstimateCredits` to confirm the cost you
   saw in the estimate.
3. **Status.** `GET /projects/{projectId}/runs/{runId}` returns the status (`queued`, `running`, `succeeded`, `failed` or `cancelled`), the steps, the credits and a
   failure code if the run failed. `GET /projects/{projectId}/runs` lists runs and filters by `workflowId`
   and `status`.
4. **Stop, resume, retry.** `POST .../runs/{runId}/stop`, `/resume` and `/retry` (scope `runs.execute`).
   A key can retry or resume only runs it started. Resume and retry start paid work again: they charge the key that calls them, count against the hourly
   run limit, and are never queued. When the limit is full they answer `429` `RUN_RATE_LIMITED` with
   `Retry-After`, `error.retryAfterSeconds` and `error.resetsAt`. Try again after that time.

Results are paged with `?limit=` and `?cursor=`; the answer holds `data` and `nextCursor`.

A start under the hourly limit answers `201` with the run. The API never turns on sending your source
media to outside services; a run started through the API keeps that off.

## The hourly run limit

Each key has a limit on how many runs it can start per hour. A start over the limit is not refused. It is
queued.

## Queued starts

A queued start answers `202`:

```json
{
  "id": "<run request id>",
  "status": "queued",
  "estimatedStartAt": "2026-11-02T15:30:00Z",
  "workflowId": "...",
  "projectId": "..."
}
```

The `id` is a run request id, not a run id. ReelBolt starts the request when the hourly limit allows, in
the order you sent them. A key can have 20 waiting requests (queued or starting); one more answers `429` `run_queue_full` with `Retry-After` and the `limit`.

- `GET /projects/{projectId}/run-requests` and `GET .../run-requests/{id}` show the request: its status
  (`queued`, `starting`, `started`, `cancelled` or `refused`), `estimatedStartAt` (while queued), the `runId`
  once started and `error` with a `code` and `message` if it was refused.
- `POST .../run-requests/{id}/cancel` cancels a request, only while it is `queued`; otherwise it answers `409` `run_request_not_cancellable`.

ReelBolt runs the credit and cap checks again when the request actually starts. A request can therefore end
as `refused`, with the same code and message a direct start would have given, for example
`API_KEY_BUDGET_EXCEEDED` or `INSUFFICIENT_CREDITS`. You can also get the `run_request.started` and
`run_request.refused` [webhook events](./webhooks.md).

## Outputs and download links

- `GET /projects/{projectId}/runs/{runId}/outputs` lists the outputs of a run (scope `outputs.read`).
- `POST /projects/{projectId}/outputs/{stepResultId}/link` returns `{ "url": "...", "expiresAt": "..." }`.
  The link works for 15 minutes. Ask for a new one when it expires.

Content checks run before an output can be shared. While a check is still running, the link request answers
`202` with `CONTENT_MODERATION_PENDING` and a `Retry-After` header. Try again after that many seconds. A
blocked output answers `422` `CONTENT_BLOCKED`; other check results answer `409`.
