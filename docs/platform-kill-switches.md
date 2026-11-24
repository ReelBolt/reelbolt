# Platform kill switches

WP F11. Two platform-wide kill switches and one per-organisation one, stored in a single
`platform_settings` table and enforced at the two places work can start: the WorkflowEngine's
admission check and the runner gateway's job assignment. Plus the periodic spend report that tells
an operator when a threshold has been crossed.

The point of this document is the part a switch cannot express in code: **what each switch does,
what it does not do, and what happens when the settings store itself is unreachable.**

## The switches

| Key | Scope | What it stops | What it does not stop |
|---|---|---|---|
| `compute` | platform | Admitting any new execution, and assigning any job to a runner. Queued work stays `Queued`. | Runs already in flight. Connections from runners, which stay connected and simply receive no work. |
| `inference` | platform | Admitting a new execution **whose organisation has no inference providers of its own** — i.e. one that would spend the platform's provider budget. | Runs already in flight. Organisations paying their own provider bill. |
| `org:<uuid>` | one organisation | Admitting that organisation's executions, assigning its queued runner jobs, and letting its desktop runners connect at all (`4403`). | Every other organisation. |

A row that does not exist means the switch is off. A row that exists with `paused = false` means the
same thing, and is kept rather than deleted so the table carries a record of who paused what and who
resumed it. Unknown keys are ignored by this build's readers, so a later work package can add one
without an older reader treating the table as unreadable.

## What happens when the settings store is unreachable

This is the failure mode a switch has to get right, and the answer is **paused**, everywhere, in both
the WorkflowEngine and the runner gateway:

- A read that fails never yields the previous value and never yields a default. The cached value is
  served **only** within its ten-second lifetime; past that, a failed refresh means "paused".
- A process that has never managed to read the table is paused from its first admission.
- The failure is logged once at ERROR on the transition (not once per admission) and counted in
  `reelbolt.platform.switches_unreadable` (engine) and `reelbolt_platform_switches_unreadable_total`
  (gateway), so an operator can alert on "the switches are unknown" rather than discovering it.
- Nothing is lost while it happens: refused work is left `Queued`, and
  `ExecutionRecoverySweeper` re-publishes a `Queued` row once it has been queued for its stale
  window (`WorkflowEngine:Lease:StaleQueuedMinutes`, ten minutes by default). A pause costs latency,
  not work.
- The cost of the opposite choice is why: a store outage that silently admitted work would be
  indistinguishable from a working switch, and the whole reason the switch exists is that spending
  has to stop when a human says so.

**The one deliberate asymmetry** is the runner gateway's `4403` refusal. The protocol defines
`4403` as terminal for the runner (a receiver MUST NOT retry it, and `pkg/agent` does not), so the
gateway refuses a hello for a disabled organisation **only on a value it actually read**. An
unreadable store accepts the connection and assigns no work — an idle runner is a recoverable state,
a runner whose reconnect loop has been killed by a transient database blip is not.

## Enforcing the switches

```bash
# Read the whole panel (platform admin only)
curl -sS -H "Authorization: Bearer $TOKEN" https://app.example.com/api/v1/platform/settings | jq

# Pause all compute, with a reason that will still be there after the incident
curl -sS -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"paused":true,"reason":"runaway spend, investigating"}' \
  https://app.example.com/api/v1/platform/settings/compute

# Stop one organisation spending anything (also refuses its desktop runners with 4403)
curl -sS -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"paused":true,"reason":"abuse report #1234"}' \
  "https://app.example.com/api/v1/platform/settings/org:5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22"

# Resume: recorded, not deleted
curl -sS -X PUT ... -d '{"paused":false,"reason":"resolved"}' .../api/v1/platform/settings/compute
```

The routes are under `/api/v1/platform/settings`, not `/api/v1/admin/*`, because nginx sends the
latter to the Go API. Authority is the platform-admin claim only: an organisation Owner is refused,
because `compute` stops every tenant.

**Propagation is up to ten seconds and there is no invalidation channel.** Both readers cache the
table for ten seconds; a write does not notify anything. That is deliberate — a switch whose effect
depended on a message being delivered would be a switch that can silently fail to take effect.

## What the switches do not do

- **They do not interrupt a running execution.** A pause stops admissions, not work in flight. An
  operator who needs a specific run stopped stops it; the credit gate and the video-generation daily
  budget still bound its spend. A switch that killed half-finished runs would trade a bounded
  overspend for destroyed work.
- **`inference` is a proxy, not a proof.** It decides with "does this organisation own at least one
  `inference_providers` row", which is cheap and answerable before the workflow's steps are loaded.
  An organisation that owns one provider but resolves some other capability to a platform-managed
  row is still admitted, and will still spend. Resolving it exactly means resolving every capability
  every agent of the run can reach, at admission.
- **`org:<uuid>` does not revoke anything.** It stops new work and new connections; an execution
  already running for that organisation continues, and a revoked runner stays revoked through the
  separate device-revocation path.
- **`compute` does not scale the pool down.** Draining an autoscaled node is F12's `drain`; this
  switch only stops the gateway handing out jobs.

## The daily spend report

`DailySpendReportService` (Inference API) sums `usage_events` over a rolling window (24 hours by
default), logs one line per window with the total, the credits, the event count, the number of
unattributed events and the largest-spending organisations, and raises an alert when
`Billing:SpendReport:DailyThresholdUsd` (platform-wide) or
`Billing:SpendReport:OrganizationThresholdUsd` (per organisation) is crossed.

| Config key | Default | Meaning |
|---|---|---|
| `Billing:SpendReport:Enabled` | `false` | Run the report at all. Self-host leaves it off. |
| `Billing:SpendReport:IntervalHours` | `24` | Window length and reporting period. |
| `Billing:SpendReport:InitialDelaySeconds` | `300` | Wait before the first report. |
| `Billing:SpendReport:DailyThresholdUsd` | `0` | Platform alert threshold; `0` disables it. |
| `Billing:SpendReport:OrganizationThresholdUsd` | `0` | Per-organisation alert threshold; `0` disables it. |
| `Billing:SpendReport:TopOrganizations` | `10` | How many organisations the report names. |
| `Billing:SpendReport:AlertWebhookUrl` | empty | Where alerts are POSTed. Empty means log-only, which is the honest default: the alert is a log line and the `reelbolt.billing.spend_alerts` counter. |

An alert is best effort: the report logs it, counts it and POSTs it (ten-second timeout), and a
webhook that is down is logged and swallowed rather than taking the report down. The report never
pauses anything by itself — F11's automatic brake is the credit gate, and the operator's is
`compute_paused`. Provider-side budget alerts (an account-level cap at the cloud and LLM providers)
are still the outer ring, because they are what fires when the ledger itself is the broken part.

## The daily operator digest

Nobody else tells the operator that a run failed for a platform reason or that a feedback report came
in, so `OperatorFailureDigestService` (Inference API) sends ONE mail a day through the Go relay
(`/internal/mail/send`, template `operator_digest`, English only). It is off by default and does nothing
(no query, no mail) until enabled.

The mail lists, for the last `LookbackHours`: step results that failed with a class the platform bears
(the same refund rule as the credit gate, `ActivityFailureRefund`), grouped by failure code and step
type (count, organizations affected, first and last time) with up to `MaxRows` latest lines (time, step
type, code, run id, the first 160 characters of the error after `SecretRedactor`); the runs by final
status; and the feedback reports filed (count, kind, first 160 characters, redacted). What the text scrub does:
every link (`scheme://...`, also percent-encoded, JSON-escaped or split by a space in its password) becomes `[link]`
whole, with no host, path or query kept; keys, passwords, tokens, cookie and authorization header values and
`user:password@host` shapes are redacted; e-mail addresses become `[email]`; `projects/<id>/...` storage paths are cut.
Nothing else is removed: other ids, names and file names (a local path, `uploads/<id>/My Wedding.mov`) in an
error or feedback excerpt can remain, so treat the mail as operator-only. An unclassified failure (no failure
class) is listed by step type, code and count only, with no text, and an agent's "Output preview" is cut off. A
text that cannot be scrubbed within the regex time limit is sent as `[unreadable]`. The window runs from the
previous scheduled slot to this one. A failed send is tried at most three times a day (the count is a row in the
database, so a restart or a second replica does not reset it); a 4xx from the relay is never retried, a 5xx or a
timeout is. The relay answers 202 before it talks to the mail server, so a later mail-server failure still counts
as sent. The window query is index-served: two partial indexes on failed step results (`completed_at`, and
`executed_at` for the rare failed row with no completion time), added by the engine migration
`AddFailedStepResultWindowIndexes`. Nothing to report sends no mail unless `SendWhenEmpty`.

| Setting | Default | Meaning |
|---|---|---|
| `OPERATOR_DIGEST_ENABLED` / `Operations:FailureDigest:Enabled` | `false` | Turn the digest on. |
| `OPERATOR_DIGEST_EMAIL` / `Operations:FailureDigest:RecipientEmail` | empty | The operator address. Required when enabled (startup fails without it). **Set the same variable on go-api**: the relay refuses `operator_digest` for any other recipient. |
| `Operations:FailureDigest:HourUtc` | `6` | From this UTC hour the day's digest is due. |
| `Operations:FailureDigest:LookbackHours` | `24` | Window length. |
| `Operations:FailureDigest:MaxRows` | `50` | Most failure lines and most feedback lines listed. |
| `Operations:FailureDigest:SendWhenEmpty` | `false` | Mail even when there is nothing to report. |

Once a day across replicas: each replica checks every 15 minutes and the one that inserts the day's
marker row in `mail_rate_counters` (bucket `operator-digest:failures`) sends; the others see a conflict.
A transient failure (relay down, 5xx, query error) removes the marker so a later check retries, at most
three attempts a day (counted in `mail_rate_counters`, bucket `operator-digest:failures:attempts`; each
attempt queries once). A 4xx from the relay (for example it refuses the recipient because go-api's
`OPERATOR_DIGEST_EMAIL` differs) is logged once as an error and not retried that day. A mail failure never
stops the service; logs name the recipient only by its `rcpt=` reference.

Text is scrubbed beyond `SecretRedactor`: e-mail addresses become `[email]`, an http(s) URL is cut to its
host (`?[redacted]` when it had a query), `projects/<guid>/...` storage keys become `projects/[redacted]`,
and `password=`, `token=`, `key=`, `sig=` style values are masked, all before the 160-character cut.

Cost: the query filters on `COALESCE(completed_at, executed_at)` over `workflow_step_results`, and no
existing index on that table or on `workflow_executions` covers a time column, so each attempt is a scan
of both. That is one scan a day (at most three on a bad day) and the API cannot add an index to engine
tables; if the tables grow large, the engine would have to add one.

## Verifying the switches

```bash
# The engine's refusal counter: zero while every switch is off, non-zero while a pause is refusing work
# reelbolt.platform.executions_deferred{reason="compute_paused"|"organization_disabled"|...}
# The gateway's deferred claims and its unreadable-store counter
# reelbolt_runner_gateway_claims_deferred_total  /  reelbolt_platform_switches_unreadable_total
```

The behaviour is pinned by tests rather than by this document:

- `PlatformSwitchesTests` — an unreadable store is paused, a stale `not paused` is never served past
  the TTL, a resumed platform is visible within the TTL, and concurrent callers share one read.
- `PlatformKillSwitchAdmissionTests` — "kill switch on means new runs stay Queued" asserted on the
  execution's row, "org limit 1 means the second run waits", and the cross-replica count not stealing
  a run this replica can admit.
- `PlatformSettingsControllerTests` — a non-admin (including an organisation Owner) is forbidden and
  writes nothing.
- `DailySpendReportTests` — the window is exactly the window, and a webhook that is down does not
  take the report down.
- The runner gateway's `switches_test.go` and its Postgres-backed claim tests — the same fail-closed
  rules at assignment, plus the `4403` path for a disabled organisation.

The go-live checklist's rows 5.4 and 5.5 ("global kill switch `compute_paused`" and "kill switch on
⇒ new runs stay Queued") are exercised by these tests on a local stack; they still have to be
observed once on staging, which is why an operator file keeps them as MANUAL rows.
