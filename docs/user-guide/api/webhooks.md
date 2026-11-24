---
title: Webhooks
sidebar_position: 5
description: Receiving run events from ReelBolt - the Standard Webhooks format, headers, signature verification in Node and Python, events, payload, retries, automatic disabling, the delivery log and secret rotation.
---

# Webhooks

A webhook tells your server when a run changes state, so you do not need to poll. ReelBolt follows the
[Standard Webhooks](https://www.standardwebhooks.com) format.

## Create an endpoint

`POST /webhooks` with a public `https` URL and the events you want (scope `webhooks.manage`). You can add an
optional project filter. If you leave out `events`, you get `run.succeeded`, `run.failed` and `run.cancelled`. An address that is not public is refused. A redirecting address is not refused at create: redirects are not followed and count as failed deliveries, which can lead to the endpoint being disabled automatically. Only port 443 or 8443 is allowed. A workspace can have
10 endpoints. The answer holds the signing secret, `whsec_<base64>`, shown once. If you repeat the request with the same `Idempotency-Key`, the replay does not contain the secret again.

`PATCH /webhooks/{id}` changes the URL, description, events, project filter or `enabled`. `POST /webhooks/{id}/test` sends a `webhook.test` event to an enabled endpoint; test deliveries are limited to 5 per minute with one pending at a time. `GET /webhooks/{id}/deliveries` is the delivery log, and `POST /webhooks/{id}/deliveries/{deliveryId}/redeliver` sends a delivery again. `DELETE /webhooks/{id}` removes the endpoint.

## Events

| Event | When |
|---|---|
| `run.succeeded` | A run finished |
| `run.failed` | A run failed |
| `run.cancelled` | A run was stopped |
| `run.step_completed` | A step finished (you must ask for it) |
| `run_request.started` | A queued start began |
| `run_request.refused` | A queued start was refused |

## Request and payload

Each delivery is a `POST` with these headers:

| Header | Content |
|---|---|
| `webhook-id` | Unique event id. The same id is repeated on a retry |
| `webhook-timestamp` | Unix seconds |
| `webhook-signature` | `v1,<base64 signature>` |

```json
{
  "type": "run.succeeded",
  "timestamp": "2026-11-02T15:30:00Z",
  "data": {
    "runId": "...",
    "projectId": "...",
    "workflowId": "...",
    "status": "succeeded",
    "failureCode": null,
    "creditsCharged": 120
  }
}
```

`data` also carries `runRequestId` for the `run_request.*` events, and `stepId`, `stepOrder` and `stepType` for `run.step_completed`. Fields without a value are left out.

Answer with any `2xx` within 10 seconds to confirm delivery. Redirects are not followed and count as failed deliveries, which can lead to auto-disable.

Store processed `webhook-id` values for at least the tolerance window (5 minutes) and ignore repeats.

## Verify the signature

The signature is a base64 HMAC-SHA256 of `id.timestamp.body` using the secret after removing the `whsec_`
prefix and base64-decoding it. Use the raw body bytes, not a re-serialised copy. Reject old timestamps.

```js
import crypto from "node:crypto";

export function verify(secret, headers, rawBody) {
  const id = headers["webhook-id"];
  const ts = headers["webhook-timestamp"];
  const t = Number(ts);
  if (!Number.isFinite(t) || Math.abs(Date.now() / 1000 - t) > 300) return false;
  if (!headers["webhook-signature"]) return false;
  const key = Buffer.from(secret.replace(/^whsec_/, ""), "base64");
  const expected = crypto.createHmac("sha256", key).update(`${id}.${ts}.${rawBody}`).digest("base64");
  return headers["webhook-signature"].split(" ").some((part) => {
    const sig = part.replace(/^v1,/, "");
    return sig.length === expected.length &&
      crypto.timingSafeEqual(Buffer.from(sig), Buffer.from(expected));
  });
}
```

```python
import base64, hashlib, hmac, time

def verify(secret: str, headers: dict, raw_body: bytes) -> bool:
    msg_id = headers.get("webhook-id")
    ts = headers.get("webhook-timestamp")
    sigs = headers.get("webhook-signature")
    if not msg_id or not ts or not sigs:
        return False
    try:
        t = int(ts)
    except ValueError:
        return False
    if abs(time.time() - t) > 300:
        return False
    key = base64.b64decode(secret.removeprefix("whsec_"))
    signed = f"{msg_id}.{ts}.".encode() + raw_body
    expected = base64.b64encode(hmac.new(key, signed, hashlib.sha256).digest()).decode()
    return any(
        hmac.compare_digest(part.removeprefix("v1,"), expected)
        for part in sigs.split(" ")
    )
```

The official `standardwebhooks` libraries (Node, Python and others) do the same check.

## Retries and disabling

If your server does not answer `2xx`, ReelBolt retries after 1 minute, 5 minutes, 30 minutes, 2 hours,
6 hours and 12 hours, which is about a day in total. An endpoint that only fails for 3 days is disabled and
workspace Owners and Admins get a mail. To resume, set `enabled` to `true` with `PATCH /webhooks/{id}` or use the toggle in the dashboard; that starts a new 72-hour window. Deliveries are
kept for 30 days in the log.

## Rotate the secret

`POST /webhooks/{id}/rotate-secret` returns `{ "secret": "whsec_..." }`, shown once. For 24 hours deliveries carry signatures for both
the old and the new secret, so you can deploy the new one without missing events. With `rotate-secret?revokePrevious=true` the old secret stops working at once.
