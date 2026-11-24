# Observability

Operator and developer reference for ReelBolt's telemetry (work package F9): what the three
services export, how to switch it on, what a span or metric may carry, and which dashboards to
build. The backend decision is Grafana Cloud's EU free tier over OTLP (decision D19,
[`design/cloud-provider-adr.md`](design/cloud-provider-adr.md)); nothing in the code is specific to
Grafana, so any OTLP/HTTP collector works. Secrets handling is in [`secrets.md`](secrets.md).

## Turning export on

Export is off by default. Each service exports traces and metrics over OTLP/HTTP only when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set to a non-blank value (the per-signal
`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` and `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` also count). With
none set, no exporter, span processor or extra instrumentation is registered, the global providers
stay the no-op defaults, and startup is identical to a build without telemetry. This holds for the
Go API (`api/telemetry`), the Inference API and the engine (`ReelBolt.Shared.Observability`), and
each has a test pinning it.

The exporters read the standard variables themselves:

| Variable | Purpose |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | The collector base URL. Blank disables everything. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` (the cloud compose default, and what Grafana Cloud's OTLP gateway expects). |
| `OTEL_EXPORTER_OTLP_HEADERS` | Auth, for Grafana Cloud `Authorization=Basic <base64 instanceId:token>`. A secret. |
| `OTEL_DEPLOYMENT_ENVIRONMENT` | Becomes `deployment.environment` (falls back to `ASPNETCORE_ENVIRONMENT`). |
| `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES` | Standard overrides, honoured by every service. |

The cloud compose layer passes all of these to `go-api`,
`inference` and `workflow-engine` with empty defaults; the values go in the per-host `.env`
(`env.control.example`, `env.engine.example`). To enable it, create a Grafana Cloud stack in an EU
region, create an access policy with `metrics:write` and `traces:write`, base64-encode
`<instanceId>:<token>`, and put the endpoint and header in both hosts' `.env`.

## What is exported

Resource attributes on every signal: `service.name` (`ReelBolt.Inference.Api`,
`ReelBolt.WorkflowEngine`, `ReelBolt.GoApi`), `service.version` and `deployment.environment`.

Traces: ASP.NET Core and `HttpClient` spans, EF Core spans (statement text is not recorded, which
is the instrumentation's default), MassTransit publish and consume spans (the `MassTransit`
source, which carries `traceparent` through RabbitMQ headers so a request in the Inference API and
the execution it queues are one trace), the engine's own `ReelBolt.WorkflowEngine` source
(`ExecuteWorkflow` and the step spans), and `otelhttp` server spans in the Go API.

Metrics: ASP.NET Core and `HttpClient` runtime metrics, the MassTransit meter, the engine's
`reelbolt.workflows.active`, `reelbolt.workflows.completed`, `reelbolt.step.duration_ms` and the
decision-gate histograms, `InferenceTenancyDiagnostics`, and the Inference API's billing webhook
meter `ReelBolt.Inference.Billing.Webhooks` (see
[Billing webhooks and reconcile](#billing-webhooks-and-reconcile)). Database connection pool metrics
are listed in [`database-connections.md`](database-connections.md#connection-metrics).

Logs are not exported over OTLP. They stay in the container logs (`docker compose logs inference`).

What is not wired yet: nginx does not start or forward a trace context (it would need the nginx
otel module), so a browser request's trace begins at the first service. The runner gateway and the
cloud worker are not instrumented, so a trace currently ends at the engine. Both belong with the
runner-fabric work.

## Tenant tagging and cardinality

Every span can carry `reelbolt.org_id`, the organization the work ran for. Traces are the right
place for it: a trace is one request, so the high cardinality costs nothing. It is set from:

- the Inference API's `ITenantContext` (the validated token's organization), stamped when the span
  ends because an ASP.NET Core span starts before the token is validated;
- the engine's execution context (`WorkflowExecutionContext.OrganizationId`), plus an explicit tag
  on the `ExecuteWorkflow` span;
- the Go API's authentication middleware, after the organization membership check.

Metrics never carry an organization id or any other per-tenant value. The only tenant-derived
metric dimensions are `plan_tier` and `compute_target`, both normalized to a closed set before
use (`ReelBoltTelemetry.NormalizePlanTier` collapses unknown plan keys to `other`, and compute
target is `cloud`, `runner` or `unknown`). They are attached to `reelbolt.workflows.completed` and
`reelbolt.step.duration_ms`. Today `compute_target` is always `cloud`, because an execution row
does not yet record where its media work ran; the dimension becomes meaningful when the runner
fabric writes it. Per-organization numbers (spend, usage, refunds) belong in the usage ledger, not
in metrics, and Grafana reads them from Postgres.

Spans carry no user content: no prompts, model output, file names or request bodies are added by
this work, in line with the data-processing note in the cloud ADR.

## Dashboards to build

Build these in Grafana once data is flowing. The first four read OTLP metrics; the last two read
Postgres through a read-only data source, because the usage ledger is the source of truth for money.
The six dashboards now ship versioned under an operator file with
their provisioning; the table below is the design intent, not a promise that every panel matches it.

| Dashboard | Source | Panels |
|---|---|---|
| Queue depth and slots | RabbitMQ metrics from the broker host (or CloudAMQP) plus `reelbolt.workflows.active` | Messages ready on `workflow-execution`, active executions against `WorkflowEngine:MaxConcurrency`, executions started per minute. |
| Step latency | `reelbolt.step.duration_ms` by `step.type` and `agent.type` | p50 and p95 per step type, split by `plan_tier`; alert when `VideoCompile` or `Agent` p95 doubles against its 7-day median. |
| Outcomes | `reelbolt.workflows.completed` by `status` and `plan_tier` | Passed, failed and cancelled per hour, failure ratio, with the `ExecutionRecoverySweeper` interruptions called out. |
| API health | ASP.NET Core and `otelhttp` request metrics | Request rate, 5xx ratio and p95 per service. |
| LLM spend per hour | usage ledger (B2), `SUM(cost_usd)` grouped by hour and by provider | Spend per hour against the daily budget caps, top providers, cost per completed execution. |
| Refunds | usage ledger refund rows | Refund count and credits per hour, refunds as a share of executions, split by failure reason. |

Alerts worth setting at the start: no `reelbolt.workflows.completed` datapoint for 30 minutes
while the queue is non-empty, failure ratio above 20% for 15 minutes, and queue depth growing
while active executions sit at the cap (a capacity signal for the autoscaler).

Database connection alerts, against the pool caps and budget in
[`database-connections.md`](database-connections.md#connection-budget-arithmetic): the Inference
API's `reelbolt.db.server.connections` divided by `reelbolt.db.server.max_connections` above 0.8
for 5 minutes (warn) or above 0.9 for 2 minutes (page); no data from
`reelbolt.db.server.connections` for 10 minutes while the Inference API is up (the sampler cannot
reach the database or is turned off); any service's pool with used connections at
its max for 2 minutes (`db.client.connection.count{db.client.connection.state=used}` against
`db.client.connection.max`, or `reelbolt_runner_gateway_db_pool_acquired_connections` against
`reelbolt_runner_gateway_db_pool_max_connections` for the gateway), or Npgsql's
`db.client.connection.npgsql.timeouts` increasing, both signs of pool exhaustion; and the Go API's
`db.client.connection.wait_count` rising steadily, which means its cap is too low for the load.

## Billing webhooks and reconcile

The Inference API's meter `ReelBolt.Inference.Billing.Webhooks` (`BillingWebhookDiagnostics`) covers
Paddle webhooks and the lost-webhook recovery described in
[billing.md](billing.md#when-a-paddle-webhook-is-lost). Its tags are closed sets; no instrument
carries an organization, subscription or event id.

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `reelbolt.billing.webhook.signature_failures` | counter | `provider` | Webhooks rejected by signature verification (answered `400`). |
| `reelbolt.billing.webhook.processing_failures` | counter | `provider`, `kind` | Webhooks whose processing threw (answered `500`, so Paddle retries). |
| `reelbolt.billing.webhook.outcomes` | counter | `provider`, `outcome` (`applied`, `duplicate`, `stale`, `unresolved`) | Every processed webhook. |
| `reelbolt.billing.reconcile.runs` | counter | `trigger` (`lazy`, `sweep`, `admin`), `outcome` (`healed`, `insync`, `toorecent`, `nothingtoreconcile`, `providerunavailable`, `stale`, `busy`) | Every reconcile run. |
| `reelbolt.billing.reconcile.stuck_watches` | gauge | none | Changes still waiting on their webhook past `Billing:Reconcile:StuckAfterSeconds`. |
| `reelbolt.billing.webhook.unresolved_24h` | gauge | none | Webhooks of the last 24 hours that ended `Unresolved`. |

The two gauges hold the value last counted by the reconcile sweep or the daily spend report, so they
stay at 0 until the first sweep has run. An abandoned checkout (its last check asked Paddle and found no live
subscription) is not counted in `stuck_watches`, logs no `billing.change_stuck` and does not raise the daily
`billing_webhooks` alert. A checkout of an organization with no billing customer (`last_error = 'no_customer'`)
still counts.

The two failure counters are also kept as in-memory totals for the process (`BillingWebhookDiagnostics.Totals`).
The reconcile sweep and the daily report read them to push an alert even where no metrics backend is set up.

Log lines carry stable EventIds:

| EventId | Name | Level | When |
|---|---|---|---|
| 7101 | `billing.reconcile_healed` | Information | A reconcile applied Paddle's state (a webhook was lost). |
| 7102 | `billing.reconcile_failed` | Warning or Error | A reconcile could not read Paddle, could not apply its state ("Review manually"), or threw while applying. |
| 7103 | `billing.change_stuck` | Warning | A change's webhook is overdue; the sweep logs one per stuck watch after each pass. |
| 7104 | `billing.webhook_signature_failed` | Warning | A webhook failed signature verification. |
| 7105 | `billing.webhook_processing_failed` | Error | A webhook's processing threw. |
| 7106 | `billing.webhook_unresolved` | Warning | A webhook was recorded but could not be resolved to an organization or plan. |
| 7107 | `billing.webhook_rejected` | Warning | A correctly signed webhook used the reserved `reconcile:` event-id prefix; refused, never processed. |
| 7108 | `billing.webhook_failures_alert` | Warning | After a sweep: this instance's signature or processing failures grew since its previous alert. At most one an hour per process; also POSTed as a `billing_webhooks` alert to `Billing:SpendReport:AlertWebhookUrl`. |

Logs are not exported, so search the Inference API's container logs for the event name or the
message text, for example:

```bash
docker compose logs --since 24h inference | grep -E 'reconcile|webhook|Billing webhooks over'
```

Suggested alerts, on the exported metrics:

- `reelbolt.billing.webhook.signature_failures` above 0 over 15 minutes (a wrong or rotated
  `PADDLE_WEBHOOK_SECRET`, or forged traffic).
- `reelbolt.billing.webhook.processing_failures` above 0 (Paddle is retrying; a retry that keeps
  failing becomes a lost webhook).
- `reelbolt.billing.reconcile.stuck_watches` above 0 for 30 minutes.
- `reelbolt.billing.webhook.unresolved_24h` above 0 (a webhook naming an unknown organization or
  a price id missing from `plans.provider_price_ids_json`).
- `reelbolt.billing.reconcile.runs` with `outcome` `providerunavailable` or `stale`.

The daily spend report adds a log line (including this instance's signature and processing failures
since its last report) and an alert with scope `billing_webhooks`. The sweep pushes the hourly failure
alert above. See [billing.md](billing.md#alerts).

## Error tracking, the alpha funnel and client telemetry

alpha-launch T-018 adds three things on top of the traces and metrics above. The **delta** was
established first, and it is worth stating plainly: the OTLP export (endpoint, headers, protocol,
resource attributes) already existed in all three services and is unchanged. Nothing below rebuilds
an exporter; each item is a new *signal* on the destination that already exists.

### Where the destination is

The same one as the rest of the telemetry: whatever `OTEL_EXPORTER_OTLP_ENDPOINT` names, with the
standard `OTEL_EXPORTER_OTLP_*` variables. Both `docker-compose.yml` (self-host) and
an operator file (cloud) now pass those through to `go-api`, `inference` and
`workflow-engine`, and the three `*.example` env files document them. Leave the endpoint blank and
nothing is exported — including everything below.

### Error tracking (server)

| Instrument | Type | Tags | Service |
|---|---|---|---|
| `reelbolt.errors` | counter | `kind`, `environment` | Go API, Inference API, engine |

`kind` is a closed set: `unhandled`, `handled`, `upstream`, `validation`, `client` (Inference API
only). Anything else collapses to `other`, so a caller cannot mint a metric series. Only an error's
**type** is recorded, never its message — a message can carry user prose. The active span is stamped
with `error.kind` and `error.type` and set to error status.

- **Inference API**: `ErrorTrackingMiddleware` records an unhandled request exception and then
  **rethrows** it, so the response the caller sees is unchanged. A client report opens its own
  `client.error` span instead, because the ingestion request itself succeeded.
- **Engine**: `ReelBoltDiagnostics.RecordError` on the engine's existing meter.
- **Go API**: `telemetry.RecordError`, and the `Wrap` handler records a panic before re-panicking.
  Like everything in `api/telemetry`, it is inert while export is off.

### The alpha funnel

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `reelbolt.alpha.funnel` | counter | `event` | One per **newly claimed** funnel event. `event` is one of the four below. |

The four events are `alpha.signup`, `alpha.first_project`, `alpha.first_run` and
`alpha.first_export_initiated`. `alpha.first_export` was renamed to `alpha.first_export_initiated`
(product-events-funnel): the old series meant the same thing — a click on the Download control — so
a chart that spans the rename reads the two as one series.

**Where each one is emitted — the transport is split, and deliberately so.**

| Event | Emitted by | Where |
|---|---|---|
| `alpha.signup` | the **Go API**, server-side | `handleSignup`, `signupViaInvite` and the first OAuth sign-in, right after the account row is written |
| `alpha.first_project` | the **browser** | the project form's success handler |
| `alpha.first_run` | the **browser** | after the execute API accepts the run |
| `alpha.first_export_initiated` | the **browser** | the Download action on a render |

`alpha.signup` cannot come from the browser: the signup endpoint answers a **uniform 202** whether or
not the address already had an account (an anti-enumeration property), so the client genuinely
cannot know an account was created — and firing on the 202 would count an address that already had
one. The Go handler is the only place that knows, and it emits only when a row was written. That
property is not to be weakened to make the client able to tell: it would trade an anti-enumeration
guarantee for a metric.

The three browser events fire on the **real transition** — a project was created, a run started, an
export was asked for — never on a page load, and never twice on a retry (a `localStorage` marker is
written before the send, so the delivery is at-most-once). They are **consent-gated**: with consent
withheld, no event leaves the browser. The counter is aggregate and carries no user id.

`alpha.first_export_initiated` counts an export **initiated, not confirmed**: the Download control is
a bare `<a download>`, which gives the page no success signal, so a click is the closest real
transition available. The name carries the caveat, so a reader of the series cannot mistake it for a
completed export. Read that series as demand for exports, not as completed exports.

The browser's marker is a convenience, not the guarantee. The server keeps its own durable claim in
`alpha_funnel_claims` (primary key `(user_id, event)`), and the counter moves **only** when a claim is
newly inserted — so one authenticated caller cannot drive the counter without bound. A request may
carry at most `TelemetryController.MaxEventsPerBatch` (16) events, and an unknown event name or an
oversized batch is refused before anything is claimed.

To read the funnel, chart `reelbolt.alpha.funnel` by `event` over the alpha window; the four series
are the funnel steps, and each counts a user at most once. The `deployment.environment` resource
attribute separates alpha from any other environment on the same stack.

### Launch-analytics events

The events added for launch analytics (`site.visit`, `signup.started`, `activation.*`, `billing.*`,
`product.*`) are rows in `product_events`, not OTLP series. They do not move `reelbolt.alpha.funnel`.
Read them from Postgres through the BI role; the names, emitters and known gaps are in
[`product-events.md`](product-events.md#the-event-vocabulary). The anonymous
`POST /api/v1/telemetry/visit` endpoint is gated by `TELEMETRY_VISIT_INGEST_ENABLED` (default `false`,
answers `404`), and the retention sweep logs `Deleted {Count} expired visitor-only product events`
at information level when it removes rows. Product-analytics GA4 and nginx logs are covered in
[`provider-data-handling.md`](provider-data-handling.md#product-analytics-what-leaves-the-browser-and-what-google-receives).

### The ingest now persists rows as well as counting

alpha-launch T-018 counted funnel events; product-events-funnel makes the ingest **persist them** as
well. An accepted browser funnel event now writes one `product_events` row (`source = 'client'`) in
the same statement that claims it, and the counter above still moves only when that claim is new. A
consent decision writes a row too (`source = 'consent'`) — granted or declined — and moves no
counter. A client error report still becomes only a counter and a span, never a row.

The same work widens the funnel the server records. Four steps the browser cannot observe are now
written server-side, as `source = 'server'` rows only — they move no counter series:

| Event | Emitter |
|---|---|
| `alpha.email_verified` | the Go API, when a verification token is consumed |
| `alpha.workspace_created` | the Go API, on Personal-workspace provisioning and on `POST /api/v1/orgs` |
| `alpha.invite_accepted` | the Go API, on both accept routes |
| `alpha.first_payment` | the Inference API, when a paid transaction is committed |

`alpha.first_payment` means the **organization activated**: it is at most one row per organization,
not one per transaction, and revenue per payment stays in `billing_payments`. The consent rows are
the ones that turn a decliner's silence into a stated fact rather than a drop-off. The table, the
`source` vocabulary, the three reader outcomes and the funnel query are in
[`product-events.md`](product-events.md) — that page is authoritative for the funnel.

### Client telemetry and consent

`web/lib/telemetry/` is the browser side. It is the E11 runner precedent applied to the browser:
opt-in, a **struct-as-allowlist** of what is collected, and a **payload preview** shown before the
person opts in.

- **What is collected.** A funnel event carries only its name. A client error report carries only
  the error's **class name** and a **redacted route**; the error's message and stack are **not
  collected at all**, because they can carry user content and the server does not rely on a client's
  own redaction. Files, media, prompts, project or document names and form values are never read out
  of anything.
- **Route redaction, both sides.** A route is reduced to a bare pathname: the query string and
  fragment are dropped, anything that is not a bare pathname is refused, and every segment that is
  not one of the app's static route words becomes `:id` — so `/invite/<token>` reaches the sink as
  `/invite/:id` and never as the token. The browser's `routePath` mirrors the server's
  `RouteRedactor`, and `Services/Telemetry/route-redaction.corpus.json` is the contract both read:
  each suite asserts its own static-segment set **equals** the corpus's and that every case redacts
  the same way, so the two sides cannot drift. The server is the enforcement point; the client is
  untrusted.
- **The consent surface.** `TelemetryConsentSurface` is mounted in the app layout. `/app` had no
  consent surface before — `CookieConsentProvider` covers `site/` only — and client-side telemetry on
  the device is the ePrivacy art. 5(3) case, so this is the gate. It is opt-in (an unanswered visitor
  has not consented) and non-blocking: a non-modal Mantine `Dialog` popup rendered through a portal
  at `position: fixed` above the shell's chrome, so where it is mounted no longer decides where it
  appears. It takes focus when it appears (a keyboard user can answer it) but traps none, and gates
  **nothing**: every product action works exactly as it did while the question is on screen, and
  declining leaves it unchanged. The copy is short — the four funnel steps, and the kind of error and
  the page it happened on, never files, prompts or names — and the exact payloads it would send sit
  in a collapsed disclosure. ESC or the close button dismisses it for the current page only; a
  dismissal is not a choice, so only Allow and Not now write one. Its wording is handed to T-021 for
  the privacy policy and alpha notice.
- **The transport** is the first-party endpoint `POST /api/v1/telemetry/events` and
  `POST /api/v1/telemetry/errors` on the Inference API. No third-party SDK phones home. Every send is
  best-effort: a failure is swallowed and never reaches the person.

### Configuration

| Config key | `.env` variable | Default | Meaning |
|---|---|---|---|
| `Telemetry:ClientIngestEnabled` | `TELEMETRY_CLIENT_INGEST_ENABLED` | `false` | Enables the client telemetry endpoints. False answers `404`, as before. |
| web build | `NEXT_PUBLIC_TELEMETRY_ENABLED` | `false` | The browser switch: the funnel, error reports and the consent surface. Inlined at build time. |

| `Telemetry:VisitIngestEnabled` | `TELEMETRY_VISIT_INGEST_ENABLED` | `false` | Enables the anonymous visit endpoint. False answers `404`. |
| `Telemetry:VisitRetentionMonths` | none (config only) | `13` | Months a visitor-only `product_events` row is kept. |
| `Telemetry:RetentionMonths` | none (config only) | `12` | Months account-linked `product_events` (except `consent` rows) and `feedback_reports` are kept. |

The client ingest and the browser switch (the first two rows) must both be true for the funnel to be measurable. Neither defaults to on, so a deployment that has
not opted in behaves exactly as it did before this work.

## Ambient product-health signals

Four deterministic detectors over surfaces the Inference API already has emit a structured
`ProductHealthSignal` when a condition an operator should see occurs (T-020). They **report and never
act**: a signal is never read back into a decision, is never shown to a user as a consequence, and can
never refuse, gate, approve or alter a request. The approval-card model is untouched. A detector that
changed behaviour would be a defect, not a feature.

**The sink is one OTLP span per signal**, named `product_health.signal`, on the service's own
`ActivitySource` (`ReelBolt.Inference.Api`, already registered by `AddReelBoltTelemetry`). A span was
chosen over a database row because a row would need a table and a migration for data that is pure
telemetry, while the OTLP trace pipeline is already wired and watched. With no collector configured the
span is created and dropped, exactly as today; with no `ActivityListener` attached `StartActivity`
returns `null` and nothing is recorded at all.

**Metadata only, never user prose.** The span carries exactly the signal's fields and nothing else:

| Tag | Meaning |
|---|---|
| `product_health.kind` | One of the four kinds below. |
| `product_health.source` | Where it was observed, a stable word (`assistant.tool_result`, `assistant.approval_gate`, `workflow.run_start`). |
| `reelbolt.org_id`, `reelbolt.user_id` | The tenant and acting user; `00000000-…` only where the seam cannot name one. |
| `reelbolt.project_id`, `reelbolt.execution_id` | Present when the observation has one. |
| `product_health.step_order` | The workflow step's order, for `workflow_step_failed`. |
| `product_health.code` | A closed-set machine code the seam already computed (e.g. `PLAN_NOT_ENTITLED`). Never prose. |

No detector reads a message body, prompt, tool argument or model output, so no user text can reach the
sink. There is deliberately no field that could carry one.

| Kind | Condition | Observed at |
|---|---|---|
| `tool_call_failed` | A tool result whose JSON says `"ok": false`. | The assistant's tool-result seam (`AssistantTurnGuard`) and the approval gate. |
| `request_refused` | A request refused with **402, 409 or 503**. | `RunStartGate`, which answers every REST run start. |
| `workflow_step_failed` | A run report naming a step whose persisted status is `Failed`; one signal per failed step, carrying its `step_order`. | The assistant's tool-result seam (the run tools report a run's steps). |
| `conversation_abandoned_after_error` | An assistant turn that ended on an error **without producing a reply**. | The approval gate, when a gated tool throws. |

The detectors are deterministic and bounded: a closed-set test over data the caller already has (a
status code, a JSON `ok:false`, a persisted `Failed`), with no model call, no summarisation and no
narration. Each detector method returns `void` — that is the property, not a style: there is no value a
response path could branch on. A detector's own failure (a malformed payload, a sink that throws) is
swallowed inside the detector, so telemetry can never turn into a failed user request.

Signals carry an organization id, so they are personal-data-adjacent and fall under the alpha's GDPR
position. Retention for these categories is 12 months, then deleted or aggregated (operator ruling 2026-10-26;
`TELEMETRY_RETENTION_PERIOD` in `site/lib/legal-placeholders.ts`), and the Privacy Policy names Grafana Cloud
as the recipient. The stack is in the EU region (operator, 2026-10-26) and the page says so (`OBSERVABILITY_PROVIDER_REGION = 'EU'`). What T-021 needs: T-020 hands T-021 the collected-fields list (the table above), the sink (an OTLP span to
the Grafana Cloud EU destination of this document), and the notice wording below.

**Alpha-notice wording for T-021** (one clause, to sit beside the existing telemetry sentence):

> We also record product-health signals (a failed tool call, a refused request, a failed workflow
> step, or a chat that ended on an error) as metadata only, so we can see when the alpha is
> struggling. Each is linked to your account and workspace, and so to your email address; it is not
> anonymous. It carries no message content.

## Step change requests

| Instrument | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `reelbolt.inference.change_requests` | counter | `step_type`, `outcome` (`created`, `refused_validation`, `refused_rate_limit`, `refused_credits`, `refused_other`) | Every change request attempt (Inference API, meter `ReelBolt.Inference.ChangeRequests`). `step_type` is `unknown` when the request was refused before its step was resolved. |
| `reelbolt.inference.change_request_scope_fraction` | histogram | `step_type` | Share (0..1) of the step a created request redoes. |
| `reelbolt.engine.change_request_items` | counter | `step_type`, `kind` (`redone`, `carried`) | Lines, clips or takes a Voiceover, VideoGenerate or AudioGenerate change run redid or carried over (engine, meter `ReelBolt.Engine.ChangeRequests`). |

## Adding telemetry

Register new `ActivitySource` or `Meter` names in the service's own registration, and pass any
extra meter to `AddReelBoltTelemetry`. Tag spans freely, but a new metric dimension must come from
a closed set; if a value could be an id, a name or free text, it is a span attribute. The shared
helpers live in `inference/src/ReelBolt.Shared/Observability/ReelBoltTelemetry.cs` and
`api/telemetry/telemetry.go`.
