---
title: Errors
sidebar_position: 6
description: The API error format, the request id header, and a table of error codes with what each means and what to do.
---

# Errors

## Format

Every error has the same shape:

```json
{ "error": { "code": "rate_limited", "message": "Too many requests.", "resetsAt": "2026-11-02T15:31:00Z" } }
```

Some errors add fields. Each one is optional and appears only on the errors it belongs to. The list is not closed: an error can also carry fields from the check that refused the call (for example `required`, `balance` or `plan` on a `402`), so ignore members you do not know:

| Field | Type | On |
|---|---|---|
| `resetsAt` | ISO date-time | `429`, and `402` cap refusals: when you can try again |
| `retryAfterSeconds` | integer | `429`: the same wait as `Retry-After` |
| `window` | string | `402` cap refusals: `daily`, `weekly` or `monthly` |
| `capCredits`, `usedCredits`, `requiredCredits` | number | `402` cap refusals |
| `requiredScopes` | array of strings | `403 insufficient_scope` |
| `limit`, `used`, `bucket`, `windowMinutes`, `busy` | integer, string, boolean | run-limit `429` |
| `details` | object of field name to messages | `400 invalid_request`: what was wrong, per field |

A missing or unusable key on any `/api/public/v1` route answers `401` with `api_key_required` in this shape,
with a `WWW-Authenticate: Bearer` header. A body or query value that cannot be read (an empty body, bad JSON,
`?limit=abc`) answers `400 invalid_request` with `details`. Every response, successful or not, has an
`X-Request-Id` header. Include it when you contact support.

## Codes

| Code | Meaning | What to do |
|---|---|---|
| `api_key_required` | No API key was sent, or a dashboard session was used on the API | Send `Authorization: Bearer rbk_...` |
| `invalid_api_key` | The key is unknown or wrong | Check the secret; create a new key |
| `api_key_revoked` | The key was revoked | Create a new key |
| `api_key_expired` | The key passed its expiry | Create a new key |
| `api_key_inactive` | The key's service account or membership is gone, or a queued request was refused because the key is no longer active | Create a new key and retry |
| `API_KEY_INACTIVE` | Run failure code: the key was revoked or expired while its run was going | Create a new key and start the run again |
| `insufficient_scope` | The key lacks the scope this call needs | Create a key with the scope |
| `scope_not_declared` | The call has no scope set up and is refused | Contact support with the `X-Request-Id` |
| `api_key_scope` | A key was used on a dashboard route | Use the `/api/public/v1` routes |
| `rate_limited` | Too many requests | Wait for `Retry-After` |
| `run_queue_full` | The key already has 20 queued starts | Wait or cancel a request |
| `idempotency_key_reused` | Same `Idempotency-Key`, different body | Use a new key for a new action |
| `idempotency_in_progress` | The first request with this key is still running | Retry shortly |
| `invalid_idempotency_key` | The key value is not valid | Use a UUID |
| `INSUFFICIENT_CREDITS` | The workspace lacks credits | Add credits |
| `API_KEY_BUDGET_EXCEEDED` | A daily, weekly or monthly cap is reached | Wait for `resetsAt` or raise the cap |
| `PLAN_NOT_ENTITLED` | The plan does not include the API | Upgrade to Creator or higher |
| `api_disabled` | The public API is switched off | Contact support |
| `webhook_limit_reached` | The workspace has 10 webhook endpoints | Delete one you no longer use |
| `idempotency_response_unavailable` | `409`: the first request with this `Idempotency-Key` finished but its answer can no longer be replayed | Check the result with a GET, or use a new key |
| `invalid_request` | The body or a parameter is not valid | Fix the request using the error message |
| `run_not_owned_by_api_key` | `403`: a key can retry or resume only runs it started | Use the key that started the run |
| `webhook_test_rate_limited` | Test deliveries are limited to 5 per minute, one pending at a time | Wait for `Retry-After` |
| `PLAN_NOT_ENTITLED` (after a downgrade) | `402` on any request: the plan is re-checked on every request | Upgrade the plan |
| `RUN_RATE_LIMITED` | The hourly run limit is full for a resume or retry | Wait for `Retry-After`; a start is queued instead, see [Runs](./runs.md) |
| `run_request_not_cancellable` | The run request is no longer queued | Read its status |
| `CONTENT_MODERATION_PENDING` | The output is still being checked (`202`) | Retry after `Retry-After` |
| `CONTENT_BLOCKED` | The output was blocked by a content check (`422`) | The output cannot be shared |
| `webhook_disabled` | The webhook endpoint is disabled (`409`) | Set `enabled` to `true` |

Run starts and run control calls (stop, resume, retry) also return the same codes and messages as the
dashboard. Read `error.message` for the reason.
