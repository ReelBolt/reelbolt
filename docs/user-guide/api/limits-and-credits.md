---
title: Limits and credits
sidebar_position: 3
description: How API runs use credits, the per-key daily, weekly and monthly caps, the 402 answer, the rate limits and their headers, the 429 answer, and idempotency keys.
---

# Limits and credits

## Credits and prices

Runs started through the API cost the same credits at the same prices as runs started in the dashboard.
They draw from your workspace's balance.

## Caps per key

Every new key has a daily credit cap, 200 credits by default. You can change it, and you can also add a
weekly or monthly cap. The weekly and monthly caps are optional. Raising the daily cap above the default,
or removing it, needs a confirmation (see [API keys](./keys.md)). Keys made before the daily default
existed have no daily cap until you set one.

- The daily cap resets at 00:00 UTC.
- The weekly cap resets on Monday at 00:00 UTC.
- The monthly cap resets on the first of the month at 00:00 UTC.

A spend counts in the window in which it was reserved. ReelBolt checks the caps before a run starts and
again before every step. A run that reaches a cap stops at that step, and earlier steps keep their charge.
A step that costs a little more than its estimate can push the next check over the cap.

Caps apply only when billing is on for your workspace. When billing is off, no credits are reserved and
caps are not enforced.

## When a cap or the balance blocks a run

A start that would exceed a cap answers `402`:

```json
{
  "error": {
    "code": "API_KEY_BUDGET_EXCEEDED",
    "message": "...",
    "window": "daily",
    "capCredits": 500,
    "usedCredits": 480,
    "resetsAt": "2026-11-03T00:00:00Z"
  }
}
```

`window` is the cap that blocked the run. Wait until `resetsAt`, raise the cap, or start a smaller run.
Running out of workspace credits answers `402` with `INSUFFICIENT_CREDITS` instead.

## Rate limits

Each key can make 300 requests a minute, and 60 of them can be writes (`POST`, `PUT`, `PATCH` and `DELETE`). Authenticated responses that reach the rate limiter
carry:

| Header | Meaning |
|---|---|
| `RateLimit-Limit` | Requests allowed in the window |
| `RateLimit-Remaining` | Requests left in the window |
| `RateLimit-Reset` | Seconds until the window resets |

`401`, `402` and `403` refusals, and a `429` from the edge limit, may not carry these headers.

Over the limit, the answer is `429` with `error.code` `rate_limited`, a `Retry-After` header in seconds and
`error.resetsAt` as a UTC time. Wait that long before retrying. Starting runs has a separate hourly limit: a start over it is queued, and a resume or retry answers `429` `RUN_RATE_LIMITED`.
See [Runs](./runs.md).

## Idempotency keys

Send an `Idempotency-Key` header on a `POST` so that a retry after a network error does not repeat the work.
ReelBolt remembers the key for 24 hours for each API key.

- The same key with the same body returns the stored answer.
- The same key with a different body returns `422` `idempotency_key_reused`.
- The same key while the first request is still running returns `409` `idempotency_in_progress`.
- A key that is not a valid value returns `invalid_idempotency_key`.

Use a new random value, such as a UUID, for each new action.
