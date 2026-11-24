# Public API (internal design)

Customer docs: [`user-guide/api/`](user-guide/api/index.md). This page records how the API is built.

## Flag and config

`PublicApi:Enabled` (env `PUBLIC_API_ENABLED`, default `false`) is read by the Inference API and by web
(server-side, to hide the page). Off: the ApiKey handler fails every key, `/api/public/v1/**`,
`/api/v1/org/api-keys/**` and `/api/v1/org/webhooks/**` answer `404`, and the webhook consumer and worker do
nothing. Other keys in `PublicApi:*`: `MaxKeysPerOrg` (50), `MaxLifetimeDays` (365), `RateLimit:RequestsPerMinute` (300), `RateLimit:WritesPerMinute` (60), `AuditRetentionDays` (90),
`IdempotencyHours` (24), `ExpiryWarning:Enabled` (true), `RunQueue:MaxQueuedPerKey` (20), `RunQueue:PollSeconds` (15), `RunQueue:BatchSize` (5),
`RunQueue:StaleStartingMinutes` (5), `Webhooks:MaxEndpointsPerOrg` (10), `Webhooks:DisableAfterHours` (72),
`Webhooks:PreviousSecretHours` (24), `Webhooks:RetentionDays` (30), `Webhooks:PollSeconds` (5), `Webhooks:BatchSize` (50),
`Webhooks:Concurrency` (8), `Webhooks:TimeoutSeconds` (10).

## Authentication

- A policy scheme (`JwtOrApiKey`) picks the `ApiKey` handler for `Authorization: Bearer rbk_...` and
  `JwtBearer` for everything else. The Go API never sees keys; nginx routes `/api/public/` to inference only.
- Key format `rbk_<id>_<secret>`: a 12-character id for lookup, a 32-byte secret. Stored: id, SHA-256 of the
  token, last four. Only the key id is logged or audited.
- The handler reads the key row on every call (no cache) and builds the principal: `sub` = the key's service
  user, role from that user's membership, `scope`, `api_key_id`, never platform admin. Failures:
  `invalid_api_key`, `api_key_revoked`, `api_key_expired`, `api_key_inactive`. The plan is re-checked on every request (402 after a downgrade). Scope refusals are audited.
- Revoke cancels the key's running and queued work, disables the webhooks the key created, and stops the engine acting for it. A key may retry or resume only runs it started (403 `run_not_owned_by_api_key`).
- One scope model with MCP OAuth tokens: the `scope` claim. The guard (`McpScopeGuard`, general scope guard)
  refuses a scoped principal on every route unless the endpoint declares a scope it holds
  (`[RequireScope]`, or `AnyApiKeyScope`). Public endpoints are fail-closed: a public endpoint without a
  declared scope answers `scope_not_declared`, and an unscoped session JWT gets `api_key_required`. This
  closes the earlier gap that MCP tokens were not scope-limited on REST routes (PR #581).
- Project restriction lives in `ProjectAccessService`: a key with a project list gets `NotFound` for other
  projects; list endpoints filter; such a key cannot create projects.

## Allowing media to be sent

A run started with a key may give the app's permission to send media (`brief.inputs.allowMediaEgress = true`, see
[`design/app-inputs.md`](design/app-inputs.md) "Permission to send media as an app field") only when the key holds the
scope `media.egress` (`ApiScopes.MediaEgress`). No endpoint declares the scope: `PublicRunStartService` checks it only
when the run's own request sets the value, reading the key's scopes from its row on that start (the queue dispatcher has
no request principal; a key that is not found holds none). Without it: `403 {error: {code: "insufficient_scope",
message: "This key may not allow sending media. Add the media.egress permission to the key in the app.",
requiredScopes: ["media.egress"]}}`, the shape of every scope refusal, and nothing is started. The scope is chosen when
the key is created by an Owner or Admin (off by default; scopes are immutable, so an existing key needs a new key) and
shows in the key list like any other. The value is per run, never implied by the scope: a run that needs the permission
and does not send it is `400 APP_INPUT_MISSING` (`inputId: "allowMediaEgress"`). The run stores
`allowMediaEgressBy: "api_key"` in its brief, the saved steps are never written, and the `201` carries `mediaEgress
{allowed, steps[{stepName, sends}]}`. Retry and resume re-bind the run's own stored value. The start is one request row
in `api_key_audit_events` like any other; no separate audit stream was added.

## Service accounts

Each key has a hidden `application_users` row with `is_service_account = true`, one `Member` membership, no
Personal org, no usable password and email `apikey+<keyid>@service.invalid`. Rename key = rename user;
revoke keeps both rows. It must never count as a seat, sign in, become Owner/Admin/Billing, or receive mail.

Exclusion paths (from T-008 and T-010):

- Go: login, OAuth, forgot/reset, member lists, role change, notifications, marketing, product events,
  organization delete.
- .NET: JWT refusal (except `act=workflow-engine`), purge, export, marketing, admin search.

## Caps in the ledger

`CreditLedger.ReserveAsync` takes an optional `apiKeyId`. Inside the org advisory lock it sums `held` of open
`api_key_spend` rows plus `charged` of closed rows for the UTC day, ISO week (Monday UTC) and UTC month, and
refuses with `API_KEY_BUDGET_EXCEEDED` or `API_KEY_INACTIVE`. Settle and release close the row. A settle that
overruns its hold is bounded by the existing overdraft tolerance and counts toward the next reserve. The
engine passes `workflow_executions.api_key_id` per step. Billing off: no holds, caps not enforced. The API
preflight returns `402` before anything starts.

**Default daily cap.** `PublicApi:DefaultDailyCapCredits` (200) is the daily cap of a new key that sends no
`caps.dailyCredits`. Setting it higher, or removing it with `caps.noDailyCap: true`, needs top-level
`confirmCaps: true` on create and PATCH, otherwise `400 cap_confirmation_required`; `noDailyCap` together
with `dailyCredits` is `400 invalid_caps`. Removing it stores `api_keys.daily_cap_waived_at`. PATCH leaves the
daily cap unchanged when `caps` omits it or sends `dailyCredits: null` (it can never remove a cap; removal is `noDailyCap` plus `confirmCaps`; weekly and monthly still overwrite). On PATCH, `confirmCaps` is needed only to raise the daily cap above both the default and the key's current cap, or to remove a cap (the key has one, or is legacy); the same value, a lower value, or re-saving a waived key needs none. Keys created before the default
exist keep no daily cap (null cap, no waiver): they are grandfathered and listed as `legacy_uncapped`. Each
key item carries `dailyCap: { status: capped | waived | legacy_uncapped, defaultCredits }` and the list
carries `defaultDailyCapCredits`. Enforcement is unchanged.

## Trial is read-only

A plan with no API access (Trial) may create keys, and those keys may read: `GET`, `HEAD`, and the `POST`
route ending `/estimate`. Everything else, including minting output links (`/link`), is refused. Every other call (runs, stop/resume/retry, uploads, import, deleting
a file, workflow writes, webhook create/patch/delete/test/rotate/redeliver, run-request cancel) is refused by
`PublicApiGuardFilter` with `402 API_PLAN_READ_ONLY` (feature `api_write`, plus the plan that unlocks it) before
any credit hold, upload or idempotency record is written. The per-key rate limiter runs first, so refused writes are counted and throttled like any call. A queued run of a read-only plan is refused the
same way by `ApiRunQueueDispatcher`. The rule lives in `PublicApiReadRule.IsRead`.

## Queued run requests

Retry and resume take the same hourly slot as a start, charge the calling key, and answer `429 RUN_RATE_LIMITED` when full (never queued). A start over the hourly run limit (source `api`) is stored in `api_run_requests` and answered `202`. Accept-time
gates are everything except the run-rate guard. `ApiRunQueueDispatcher` (BackgroundService, 15 s,
`FOR UPDATE SKIP LOCKED`, FIFO per key) re-runs the whole start sequence as the service user; a full guard
pushes `not_before`, any other refusal marks `refused`, success marks `started` with `execution_id`.

## Idempotency, rate limits, audit

- `api_idempotency_records`: 24 h per key; same body replays, different body `422`, in flight `409`.
- `api_key_rate_counters`: Postgres fixed-window counter, 300/min and 60/min writes.
- `api_key_audit_events`: written after the response through a bounded channel and a batch writer; the route
  template only, never the raw path; dropped events are counted by a metric; kept 90 days. `last_used_at`
  updates at most once a minute.

## Webhooks

A MassTransit consumer in the Inference API turns `WorkflowExecutionCompleted`, `Failed` and (when subscribed)
`WorkflowStepCompleted` into `webhook_deliveries` rows, idempotent on event id plus endpoint.
`WebhookDeliveryWorker` (`FOR UPDATE SKIP LOCKED`) posts with a 10 s timeout and retries at 1 m, 5 m, 30 m,
2 h, 6 h, 12 h. Secrets (`whsec_`) are encrypted with `ISecretProtector`. Endpoints are public https only and
no redirects are followed. An endpoint failing for 72 h (`Webhooks:DisableAfterHours`) is disabled and Owners and Admins are notified; `PATCH enabled: true` re-enables it with a clean window. Create is idempotent with the `secret` field redacted from the stored response, so a replay never repeats it; rotation is not idempotent. For `PreviousSecretHours` after a rotation the previous secret signs alongside the new one.

## App input schema

`GET /projects/{p}/workflows` and `GET .../workflows/{w}` return the app's `appInputs` and an `inputSchema`: a JSON
Schema (object) of `brief.inputs`, derived from the app manifest by the pure builder in
`Services/PublicApi/PublicAppInputSchema.cs`. Each `appInput` also carries the additive fields `default`, `help`,
`example`, `maxLength` and `step`. All of them are additive: existing clients ignore them. A typed input
(`kind` "input") is sent as `brief.inputs[id]`; a brief field as `brief.<field>`; files as `brief.sourceFiles`.
The customer walkthrough is `user-guide/api/apps.md`.

## Tables and owners

| Table | Owner | Notes |
|---|---|---|
| `api_keys` | API | Tenant filter |
| `api_key_spend` | API | Engine maps it (`ExcludeFromMigrations`) |
| `api_key_audit_events`, `api_key_rate_counters`, `api_idempotency_records`, `api_run_requests` | API | Reached through the key |
| `webhook_endpoints` | API | Tenant filter |
| `webhook_deliveries` | API | Child of the endpoint |
| `workflow_executions.api_key_id` | Engine | Column |
| `application_users.is_service_account` | API | Column |

See also [`metering-and-billing.md`](metering-and-billing.md).
