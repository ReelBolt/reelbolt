# The compute fabric

The **compute fabric** is the part of ReelBolt that can run a workflow step somewhere other than the
engine process: on the customer's own paired desktop machine, or on a worker VM in our cloud pool.
Its control plane is the Go `runner-gateway`; its data plane is the runner protocol
([runner-protocol.md](runner-protocol.md)), which is what a runner speaks. The engine decides *where*
a step runs through one seam, `IComputeTarget`
(`inference/src/ReelBolt.WorkflowEngine/Services/Compute/IComputeTarget.cs`), and records the answer
on the step result. **No executor resolves that seam yet** — see "What is wired today and what is
not" before you conclude anything from the classes existing.

This page is the operator's and developer's map: what the three targets mean, what the selector
decides and why, how to turn the fabric on, **what is wired today and what is not**, and the two
deployment hazards that will bite an operator who skips the runbook.

**Nothing here is required for a self-host install.** `Compute:Mode` defaults to `Local` and the key
is deliberately absent from the shipped `inference/src/ReelBolt.WorkflowEngine/appsettings.json`, so
`docker compose up` needs no new configuration and the whole fabric is inert until an operator asks
for it. The reasoning is in the design record `docs/design/runner-protocol-adr.md` §1.

## Local models

A workspace can use a language model that runs on a member's own computer. That path is separate from the compute targets below: it does not use `Compute:Mode` or the target selector, and it needs only the runner gateway plus `Compute:GatewayBaseUrl` and `Compute:InternalToken`. See [`local-llm-gateway.md`](local-llm-gateway.md).

## The three targets

`ComputeTargetKind` (`inference/src/ReelBolt.Shared/Data/Models/ComputeTargetKind.cs`) is what the
engine **persists** on `workflow_step_results.compute_target`. It is stored as an `int` and is
append-only: the numbers are the stored values.

| Target | Value | What it means | Trust |
|---|---|---|---|
| `Local` | 0 | The engine ran the step in-process — the `sandbox-executor` plus in-process ffmpeg. This is what every install did before the fabric existed, and what `Compute:Mode=Local` produces. | This process |
| `CloudPool` | 1 | One of our own worker VMs, serving any organisation's `target=CloudPool` jobs. | Trusted, but its output is verified anyway |
| `OrgRunner` | 2 | The organisation's own paired machine (`target=OrgRunner` in the protocol). | **Untrusted by construction** — the binary is controlled by its owner — so its output is verified before promotion, tagged `WorkflowStepResult.FromRunner` and excluded from the step-result cache |

The protocol's two target strings (`"CloudPool"` and `"OrgRunner"`) are the same three-way choice
minus `Local`, which has no protocol representation at all — a local step is not a job. The mapping
between the two vocabularies lives in one place, `ComputeTargets` in
`inference/src/ReelBolt.WorkflowEngine/Services/Compute/IComputeTarget.cs`.

## How to turn the fabric on

Two settings, and one of them is on the gateway's side:

1. **`Compute:Mode=Fabric`** on the WorkflowEngine. That is the switch. With the key absent (the
   shipped state) the selector answers `Local` *before* it reads the database or calls the gateway,
   so an install that has never heard of the fabric behaves exactly as it did before.
2. **`Compute:InternalToken`**, or the `RUNNER_INTERNAL_TOKEN` environment variable — the same
   variable the gateway reads, so one value configures both sides. It is never logged. A gateway with
   **no** token configured answers `503 internal_api_disabled` on its whole `/internal/v1` surface;
   an **engine** with no token refuses every call rather than sending one unauthenticated.

Optionally `Compute:GatewayBaseUrl` (default `http://runner-gateway:8090`, the compose service name)
and the tuning keys `RunnerQueryCacheSeconds` (5), `RequestTimeoutSeconds` (30), `JobWaitSeconds`
(30) and `SessionWaitSeconds` (900).

The gateway needs `DATABASE_URL`, and it will not start without it: it authenticates every desktop
runner against `runner_devices.public_key` and re-checks `revoked_at` on live connections. It never
migrates — the Inference API owns `runner_devices` and `platform_settings`, the engine owns
`runner_jobs` and `runner_sessions`.

**Turning `Mode` on does not, today, route any step to a runner.** See the next section.

## What is wired today and what is not

This is the part a future reader must not have to rediscover, so it is stated explicitly rather than
implied by which classes exist.

**Wired and verified.** The selector, the gateway client, the per-execution session gate and the
output verifier are all registered in the engine's composition root
(`WorkflowEngineServiceCollectionExtensions`), and `ComputeFabricCompositionTests` proves they
resolve from the real `AddWorkflowEngineServices` — including that the engine comes up in `Local`
mode with **no** `Compute` configuration at all. The gateway itself (job queue, leases, fencing,
routing, the scaling endpoint, the kill switches) is complete and exercised against a real broker,
a real PostgreSQL and real WebSocket clients.

**Not wired.** The callers landed after this document was written — D7b routes the Remotion
workspace tools through `RemoteSandboxSession` and its journal, D11 routes the media tools through
`RemoteMediaWorkspace`, D8 publishes renders, voiceover WAVs and project sources through presigned
URLs into the job's staging key space, and D11's remote publish is one of the two places that calls
D12's `IOutputVerifier` (D8's render path is the other). They are all reached from `Execution/`, not
only from the DI registrations, so setting `Compute:Mode=Fabric` now routes real work.

Consequences worth stating plainly:

- A `CloudPool` job created at the gateway **stays `Queued`** until a pool worker connects: see
  "CloudPool workers and enrolment" below. The worker (`sandbox/cmd/reelbolt-worker`) exists, and
  [the pool waker](pool-waker.md) can start nodes for it on demand, but both are off in a deployment
  that has not set them up.
- `video.analyze.extract` **now has its engine caller.** `VideoAnalyze`'s extraction phase names
  every file it stages, writes, reads and publishes by a session-relative virtual path and reaches
  ffmpeg only through `IMediaWorkspace`, so a Fabric deployment sends the decode, the shot and
  silence detection, the WAV, the frame grids, the chroma tracking and the caption keyframes to the
  runner. The inference phase (ASR, vision captioning, subject location, the derush pre-pass, input
  screening) stays on the engine `because it needs provider credentials` — as
  [`runner-protocol.md`](runner-protocol.md) states — and reads the small media it consumes (each
  caption keyframe, the extracted WAV) back through the session's own `read` op, so the footage
  itself never crosses back. The ONE configuration that still runs locally is
  `TrackObjects`: markerless object tracking samples up to 600 MB of grayscale frames across a
  whole clip and so needs the source on the machine running it, which is the same
  "platform-only in v1" decision `runner-protocol.md` records; `RunnerEligible` is false for it
  and the selector answers Local rather than failing the step. Subject location is **not** in that
  set: it consumes one small keyframe JPEG per shot, which the workspace carries as bytes.

## The selector's decision table

`ComputeTargetSelector` (`inference/src/ReelBolt.WorkflowEngine/Services/Compute/ComputeTargetSelector.cs`)
answers one question — "where does this unit of work run?" — from four inputs: the deployment mode,
the organisation's `compute_preference`, the plan entitlement and whether an eligible runner is
online right now. It returns a `ComputeDecision`, a value: nothing is dispatched by producing one.

With `Compute:Mode=Local` (**the default**) it answers `Local` immediately, before any database read
and before any gateway call.

With `Fabric`:

| Condition | Answer |
|---|---|
| The step is not runner-eligible in v1 (a `VideoAnalyze` with `TrackObjects`) | `Local` — it runs where it always has. It does **not** fail |
| The org may use neither a runner nor the pool | `COMPUTE_NOT_AVAILABLE` (a coded step failure) |
| Preference `CloudOnly` (and any preference downgraded to it because the plan withholds runner use) | `CloudPool` when the pool is allowed, otherwise `COMPUTE_NOT_AVAILABLE` |
| Preference `RunnerOnly`, an eligible desktop runner online | `OrgRunner` |
| Preference `RunnerOnly`, no eligible runner | **`NO_RUNNER_ONLINE` — the step FAILS.** There is no silent cloud fallback; that is the whole point of the preference |
| Preference `PreferRunner`, runner online | `OrgRunner` |
| Preference `PreferRunner`, no runner, pool allowed | `CloudPool` |
| Preference `Auto` with a runner online | `OrgRunner` (`Auto` behaves as `PreferRunner`) |
| Preference `Auto` with no runner, pool allowed | `CloudPool` (`Auto` behaves as `CloudOnly` until a runner is paired) |
| `PreferRunner`/`Auto`, no runner, pool **not** allowed | `NO_RUNNER_ONLINE` |
| Any row above that would answer `CloudPool`, but **no eligible pool worker is online** | `Local`, reason `pool_empty_local_fallback` (`Compute:LocalWhenPoolEmpty`, default `true`) |

### An empty pool runs on the engine's own host

A deployment that runs no pool worker (see "CloudPool workers and enrolment" below) has nobody to take
a pool job, and a pool job nobody can take is never leased: the engine queues it, waits
`SessionWaitSeconds`, and fails the step with "no runner was assigned to the session". Before this
rule a cloud deployment with no paired desktop runner therefore failed every Remotion step, while the
box the engine runs on already had a working sandbox. The selector now reads the same
`GET /internal/v1/runners` answer it uses for desktop runners (cached `Compute:RunnerQueryCacheSeconds`)
and, when the answer for the step's job type holds no `CloudPool` worker, returns `Local`. A desktop
runner that is online still wins, and `RunnerOnly` is untouched: it asked for the user's machine, so an
empty pool changes nothing. Set `Compute:LocalWhenPoolEmpty=false` (env `Compute__LocalWhenPoolEmpty`)
to get the old strict behaviour, which also stops `CloudOnly` from asking the gateway at all.

A query the gateway **refuses** (a wrong token) cannot confirm a pool worker either, so it reads as
"no pool" and the step runs `Local`.

### Waking a node: `PoolWakeAndWait`

With the pool waker on, an empty pool is not always a reason to run `Local`. When the answer would be
`pool_empty_local_fallback`, `Compute:PoolWaker:Enabled` is `true` and the gateway's
`GET /internal/v1/runners` reports `pool.wakerHealthy`, the decision stays `CloudPool` with reason
`pool_wake_and_wait`. The engine queues the job and waits for a runner to be assigned; the waker sees
the queue, starts a node, and the node claims the job.

| Setting | Default | Meaning |
|---|---|---|
| `Compute:PoolWaker:Enabled` | `false` | Master switch (env `Compute__PoolWaker__Enabled`). Off: behaviour is exactly the previous section. |
| `Compute:PoolWaker:WaitSeconds` | `240` | The most a step waits for a runner to be **assigned**. A job a worker already holds gets a further 60 s of grace before it is cancelled. |

`pool.wakerHealthy` is true only when the waker's last heartbeat is at most 45 s old, reports healthy,
and is not at its spend cap. If the wait runs out, or the gateway refuses the job, the job is cancelled
and the step runs `Local` with reason `pool_wake_timeout_local_fallback`. A step never fails because a
node did not come up. The waker itself is described in [`pool-waker.md`](pool-waker.md).

### 503 and transport failure are "the fabric is not deployed", and never fail a step

The gateway client maps DNS failure, connection refused, TLS failure, a client timeout and the
gateway's `503 internal_api_disabled` onto one exception, `ComputeFabricUnavailableException` — a
**deployment fact**, deliberately not an `HttpRequestException` that would turn "the fabric is not
deployed here" into a failed workflow. The selector degrades on it:

- Under any preference **except `RunnerOnly`**, the step runs **`Local`**. A gateway we cannot reach
  cannot take a `CloudPool` job either, so `Local` is the only answer that actually runs the work.
- Under `RunnerOnly` it still reports `NO_RUNNER_ONLINE`. A workspace that asked for its own machine
  or nothing gets the coded failure, not a silent downgrade to platform compute.

An engine with `Fabric` set but no internal token is classified the same way, deliberately: the
caller's recovery is identical.

A **4xx refusal** (a wrong token, a malformed query, a body the client cannot parse) is a different
class — `RunnerGatewayRequestException`. The selector logs it as a warning and reads it as "no
runner confirmed", never as a step failure: a refusal says nothing about whether a runner exists.

The runner-presence answer is cached for `Compute:RunnerQueryCacheSeconds` (5 s), **including a
negative and an unavailable one**, so a wrong token cannot put a gateway call on the path of every
step. Over one execution that means an outage can be noticed up to five seconds late.

### The pool-worker trap the selector avoids itself

`GET /internal/v1/runners?org=` returns `CloudPool` workers too: a pool worker's `orgId` is null and
the gateway's filter only excludes entries whose org is *non-empty and different*. Counting one as
"this org has a runner" would send the org's work to our own pool while reporting it as local
compute. The selector therefore checks `Kind == Desktop` itself rather than trusting the query
filter, and a test pins it.

## The per-execution session gate

`runner_sessions` carries a partial `UNIQUE(execution_id) WHERE closed_at IS NULL`, so **at most one
session row may be open for a workflow execution at a time**. The gateway opens that row when a
runner is *assigned* a job and deliberately tolerates a violation (a savepoint, logged, and the job
still runs — just without a session row of its own), because the constraint belongs to the schema.

That makes the invariant the **engine's** to keep, and it matters for a concrete reason: a job with
no session row has no `runner_sessions.gateway_instance` for D5c's op routing to use, so the engine
would have no way to address the runner executing it.

`IExecutionSessionGate` (`inference/src/ReelBolt.WorkflowEngine/Services/Compute/ExecutionSessionGate.cs`)
is that rule, expressed once: it hands out **at most one lease per execution id**, held from the
moment the job is created (a runner may be assigned it at any instant) until the job reaches a
terminal state. Different executions never queue behind each other, so one busy tenant cannot starve
another.

**What it costs.** The two halves cannot be separated: `RunnerGatewayClient.OpenJobAsync` takes the
execution's slot *and* creates the job in the same call, so a second session of the same execution
**queues behind the first** rather than failing fast. The queue is bounded by
`Compute:SessionWaitSeconds` (900 s by default); when it expires the caller gets
`ComputeSessionBusyException` and the step fails with **`RUNNER_SESSION_BUSY`**. A workflow that
genuinely needed two concurrent runner sessions in one execution would wait for itself until that
bound, then fail — which is the intended outcome, because the schema forbids the alternative.

### Parking the workspace session before a media step

Two *sequential* sessions are a different matter, and they used to deadlock. The Remotion workspace
session (`RemoteSandboxSession`) pins its job — and with it the execution's slot — for the **whole
execution**: it was let go only when the execution ended or when one of the session's *own* ops hit
`session_lost`. A live run with an Author step (sandbox tools) at step 3 and a `VideoAnalyze` at step
5, both sent to the org's paired runner, logged `video.analyze.extract -> OrgRunner` and then nothing
for minutes: one job for the execution, the Author's `remotion.workspace-session`, still `Running`
with its `runner_sessions` row open, while the analyze step waited for a slot whose owner had
finished its step long before (and would have failed `RUNNER_SESSION_BUSY` after 900 s). Killing the
runner did not help, because nothing watches that job and the lease is in-process. Any workflow with
a sandbox step before a runner-backed `VideoAnalyze`/`VideoCompile` hit it — the promo templates put
the Author last, which is why it went unseen; `MotionGraphicsPlanner`/`MotionGraphicsDirector` with
render tools followed by `VideoCompile` would hit it too.

So on its runner path, right before `OpenJobAsync`, `MediaWorkspaceFactory` asks
`ISandboxSessionRegistry.ParkExecutionAsync` to **park** the execution's workspace session
(`RemoteSandboxSession.ParkAsync`):

1. **Never under an op.** The park takes the session's op lock — the lock every op, its session-loss
   recovery, and a write *together with its journal entry* already run under — so it never closes a
   session the runner is still executing an op on, nor between a write and the entry that lets the
   write survive. It waits at most `Compute:SessionWaitSeconds` for an op in flight.
2. **End the job, then wait for it to be terminal.** The session is closed with the protocol's
   `close` op, which is how the gateway ends a session's job (`finishClosedSession`: *"session X
   closed by the engine: job Y is Succeeded"*). The gateway finishes the job *after* it has written the
   op's answer, so the engine reads the job back until it is terminal — `FinishJob`/`CancelLease`
   close the `runner_sessions` row in the same transaction. A close that fails, or a job that is still
   running after `Compute:SandboxSession:ParkWaitSeconds` (30 s), is **cancelled** and waited for again:
   a runner that never acknowledges is marked `Cancelled` by the gateway when its 10-second grace
   window (`RUNNER_CANCEL_GRACE_SEC`) closes.
3. **Only then release the slot.** A job confirmed terminal (or unknown to the gateway) releases the
   lease and the media job is created. One that still is not terminal **keeps the slot**, and the
   media step fails at once with `RUNNER_SESSION_BUSY` instead of waiting 900 s — holding a slot that
   is not needed costs one step, releasing one that is still in use would put two sessions in the
   execution. The next sandbox op tries to end that job again before opening anything.
4. **The journal stays open.** Unlike the execution-end close, a park does not seal it. The next
   sandbox op — a `ReviewLoop` looping back to the Author, or any sandbox tool — re-opens a session
   through the **same path a session loss is rebuilt by**: re-resolve the target through
   `IComputeTarget` (the runner may be gone: a `RunnerOnly` organization gets `NO_RUNNER_ONLINE`, a
   selector that now answers Local fails the op, since a parked workspace is restored only onto a
   runner), a new job under a new purpose (`workspace-session-N`), and one `hydrate` op replaying the
   journal. A session that could not be hydrated never serves an op; the workspace stays parked and
   the next op tries again.

A park is a no-op that makes **no gateway call** when the session holds no slot (never opened, bound
locally, already parked), when the media step's decision is Local, and under `Compute:Mode=Local`,
where the registry answers before it looks at anything. An unreachable gateway is not swallowed:
it propagates to the media factory, which runs the step locally exactly as it does for any gateway
that vanishes mid-open, and leaves the workspace session as it was.

**What a park does NOT put back.** The journal records the engine's own mutations — the sandbox
authoring tools' `writeFile` and `deletePath` — and a hydrate replays exactly that and nothing else. A
file that appeared in the workspace some other way is not journalled, so a parked session comes back
without it: a project file a `stage` put into `public/` for `staticFile()`, and anything an `exec`
produced inside the workspace (a render's own output directory, a preview's stills). The bytes of a
finished render are unaffected — they are published to object storage and survive the session that
made them — but the runner's copy of a staged asset must be staged again before it is used again.
That is the journal's pre-existing boundary, not a new one: a session replaced after a runner was lost
has always come back the same way. Parking makes it happen on a schedule instead of only when a
laptop closes its lid, which is why it is written down here.

The media session itself still releases its slot when its lease is disposed, right after its own
`close` op, without reading the job back — the same race (the gateway finishing that job a moment
after it answered) applies to it in principle, but its window is the gateway's own handler finishing
before a runner is assigned the next job, and it is unchanged by this.

The guarantee is **in-process**, and that is sufficient: an execution is run by exactly one engine
instance (the `WorkflowEngine:Lease` execution lease establishes it), so a second instance cannot be
running the same execution's steps concurrently. The gateway's own tolerance stays the backstop for
everything the gate cannot see — a job created by an older build, a run migrated mid-flight.

## Output verification

Runner output is untrusted, and verification happens **before promotion**, not after:

- The engine computes the object's SHA-256 itself; a runner-reported digest is treated as a hint,
  never as the answer.
- The object is probed with `ffprobe` **over a presigned GET of the staging object using an HTTP
  range**, so nothing is downloaded: duration (± one frame, else 0.25 s), width, height, codec and
  frame rate are compared against the platform-side job spec.
- A spot check (`Compute:OutputVerification:SpotCheckPercent`, default 10) runs `blackdetect` and
  `freezedetect` on three random one-second windows — **on `OrgRunner` output only**. A
  `CloudPool` worker is ours, so its output is trusted but still verified.
- A failure **deletes the staged object** and returns a `RUNNER_OUTPUT_*` code rather than throwing.

Two billing consequences ride on the same fact. `FromRunner` is **derived** from
`ComputeTarget == OrgRunner`, never set independently, so the tag and the target cannot disagree;
and every `RUNNER_OUTPUT_*` code is classified `FailureClass.Runner`, which
`CreditGate.IsRefundable` explicitly maps to *not refundable* — unmapped failure codes refund by
default, which would be the wrong answer for a runner's bad output.

## The self-host matrix

| Deployment | `Compute:Mode` | Gateway | Runner | What runs the work |
|---|---|---|---|---|
| `docker compose up` (default, and the only supported self-host path) | absent → `Local` | not needed, not started | none | `sandbox-executor` + in-process ffmpeg |
| Self-host with a paired desktop machine *(not reachable today)* | `Fabric` | required | the paired device | `OrgRunner` |
| Our cloud | `Fabric` | required | cloud workers | `CloudPool`, `OrgRunner` when a customer has one |

Self-host **keeps the `Local` target**. Retiring `sandbox-executor` in favour of an embedded worker
is a post-launch follow-up, not v1, and no part of this document asks a self-hoster to run a gateway.

The optional `fabric` compose profile exists for developers only, and the default
`docker compose up` does not start it — see "Running the gateway locally".

## Deployment hazards

### `x-max-priority` is a queue ARGUMENT, and an existing broker can refuse it

The engine declares the `workflow-execution` queue with `EnablePriority(...)` so that plan priority
(B8c) actually reorders the queue. **`x-max-priority` is a queue argument**, and a broker answers a
re-declaration that differs from the existing queue with `PRECONDITION_FAILED` (406).

The consequence is a real deployment hazard: **an engine upgrading onto a broker that already holds
the argument-less `workflow-execution` queue will fail to start its bus until that queue is deleted.**
Drain it first — messages sitting in the old queue are not migrated.

Escape hatch: `WorkflowEngine:QueuePriority:MaxPriority=0` (compose:
`WORKFLOW_QUEUE_MAX_PRIORITY=0`) keeps the pre-B8c declaration and disables the ordering. It is the
**only** value that does.

### CloudPool workers and enrolment

The gateway refuses a pool worker with `4401` unless a `runner_devices` row exists for it
(`kind='CloudPool'`, name = `hello.auth.workerName`): fail-closed rather than inventing a synthetic
identity. Two things write that row:

- **The pool waker**, for on-demand nodes: the gateway enrols the node name when the waker asks for a
  node token, so the node holds no database URL.
- **`reelbolt-worker enroll`**, for a VM an operator starts by hand.

Both are described in [`cloud-worker.md`](cloud-worker.md). With neither set up, a pool job created
through the internal API is created successfully and then **stays `Queued`**, with no error; this is why
the selector's empty-pool rule exists. Pairing a *desktop* runner is a different path and also writes a
`runner_devices` row.

### Other things that make a fabric look broken when it is not

- **`RUNNER_POOL_CREDENTIALS` empty**: logged at startup, and no CloudPool worker can authenticate.
- **`RUNNER_JOB_TOKEN_KEY` unset**: fine for one replica; with several, a runner that reconnects to a
  different replica is told its lease was lost. Set it for any multi-replica deployment.
- **`RABBITMQ_URL` unset**: the `RunnerDeviceRevoked` fast path is off and revocation relies on the
  60-second `revoked_at` re-check, which is the protocol's own authoritative backstop.
- **Kill switches**: an unreadable `platform_settings` is **PAUSED** everywhere, in the engine and on
  the gateway, and a **disabled organisation** makes the gateway refuse a hello with `4403` — which
  is terminal for the runner, so the gateway applies it only to a value it actually read. See
  [platform-kill-switches.md](platform-kill-switches.md).

## Running the gateway locally

The gateway image is the `runner-gateway` target of `sandbox/Dockerfile`: an Alpine image holding
one WebSocket endpoint plus `/healthz` and `/metrics`, with no Docker CLI, no Node and no sandbox
tooling. It is the only image in this tree that faces the public internet, so what it does not
contain matters as much as what it does.

An optional compose profile starts it for a developer:

```bash
# Default stack is unchanged: no gateway, Compute:Mode=Local.
docker compose up -d

# Start the gateway too. Its service name is what Compute:GatewayBaseUrl already names.
docker compose --profile fabric up -d runner-gateway
```

The profile contains **only the gateway**. Pool workers are not part of the compose stack: they run on
separate VMs ([`cloud-worker.md`](cloud-worker.md)) or start on demand ([`pool-waker.md`](pool-waker.md)).
Starting the gateway alone is still
useful: it is the engine's default `Compute:GatewayBaseUrl`, it exposes the F11 switch metrics, and
it is where the `503 internal_api_disabled` shape can be observed directly.

To make the engine actually call it, add `COMPUTE_MODE=Fabric` and a matching
`RUNNER_INTERNAL_TOKEN` to `.env`. Both are passed through to both services, so one value configures
both sides. **The engine still routes nothing to a runner** — see "What is wired today and what is
not".

For the public path, nginx already ships `nginx/runner-server.conf.template`: it is rendered only
when `UPSTREAM_RUNNER_GATEWAY` is set, so with no runner deployment there is no `wss://runner.<domain>`
server block at all.

## Verifying a deployment

```bash
# The gateway is up and holds no runner.
curl -s localhost:8090/healthz

# Its internal API is open, and says which runners are online (needs the shared token).
curl -s -H "Authorization: Bearer $RUNNER_INTERNAL_TOKEN" \
  'http://localhost:8090/internal/v1/runners?org=<org-guid>'
# -> 401 without the bearer;  503 {"error":"internal_api_disabled"} when the GATEWAY has no token.

# The kill-switch readers agree, and neither is guessing.
curl -s localhost:8090/metrics | grep reelbolt_platform_switches_unreadable_total
curl -s localhost:8090/metrics | grep reelbolt_runner_connected

# Which target each step actually ran on.
psql "$DATABASE_URL" -c "select compute_target, from_runner, count(*) from workflow_step_results group by 1,2;"
```

Published in a real run of this image against a throwaway PostgreSQL: `/healthz` is `200`, the
internal API answers `503 internal_api_disabled` with no `RUNNER_INTERNAL_TOKEN` and `401` without a
bearer when it has one, `GET /internal/v1/runners` answers `{"online":0,"runners":[]}`, and two
`POST /internal/v1/jobs` with the same idempotency key produce exactly **one** `runner_jobs` row
(`201` then `200`, the same `jobId`). Dropping `platform_settings` at runtime logs
`platform switches are unreadable`, increments `reelbolt_platform_switches_unreadable_total`, and
leaves the gateway serving while it assigns nothing — the "unreadable means paused, but never a
refused hello" rule, observed rather than described.

## Failure codes a compute decision produces

These reach the user the same way every other engine failure code does, in the step's error details.
The classification is asserted by the B6b drift guard, so adding one without classifying it fails the
build.

| Code | When | Failure class |
|---|---|---|
| `NO_RUNNER_ONLINE` | `RunnerOnly` (or an unentitled `PreferRunner`/`Auto`) with no eligible runner online, including when the fabric itself is unreachable | `UserInput` — the user's own choice coming back at them |
| `COMPUTE_NOT_AVAILABLE` | The organisation may use neither a runner nor the pool | `EntitlementDenied` |
| `RUNNER_SESSION_BUSY` | The `Compute:SessionWaitSeconds` wait for the execution's single session slot expired, or the execution's workspace session could not be parked for a media step | `PlatformInternal` |
| `RUNNER_OUTPUT_*` | Output verification failed: missing, too large, unreadable, duration/frame-size/codec/frame-rate mismatch, checksum mismatch, blank, promote failed | `Runner` (except `RUNNER_OUTPUT_PROMOTE_FAILED`, which is `PlatformInternal`) |

None of these is refunded as a platform fault: see [metering-and-billing.md](metering-and-billing.md).

### Silent sessions

A remote media session must not go quiet for the op deadline plus the 30 minute session cap. `stat`, `read` and `close` carry `Compute:MediaSession:SmallOpTimeoutSeconds` (default 120 s) instead of the 900 s bulk deadline, and every op logs its start and duration at Information. On the node, the presigned-transfer client has a 15 s connect, 15 s TLS and 60 s response-header deadline, and a `stage` download that receives no bytes for 60 s fails (`stage_failed`, retryable) instead of holding the op to its deadline. A mid-step failure still fails the step; only the pool wake/assign failures fall back to Local.

## The beat grid on a remote media session

`MusicBeatProbe` measures a music track's beat inside the media session: stage the file, decode a 16 kHz
mono s16 WAV (32,000 bytes per second), read the WAV back, analyse it on the engine.

The failure: binary reads travel base64, which is 4/3 larger. An inline `read` therefore has a safe raw cap of
2.75 MiB (`MediaSessionOps.SafeInlineReadBytes`), not 4 MiB. A 3.86 MB WAV (120.7 s) encodes to about 5.15 MB, over
the 4 MiB frame, so the connection closed (1009), the node reconnected in a loop, and the op ran to `op_timeout`
(a 504 after 240 s). That was the stall behind compile job `Expired`, then `compute_target 0`.

The fix: the agent answers `file_too_large` at once when the encoded result would not fit one 4 MiB payload
(`sandbox/pkg/agent/inline.go`). The engine stats first and reads a larger file by `publish` then fetch from object
storage, bounded by the caller's `maxBytes` (default `MediaSessionOps.MaxEngineReadBytes`, 256 MiB). A read or
decode failure that loses the grid is logged with its exception class and message and surfaces as
`beat_grid_unavailable` (analyze warning) or `beat_grid_failed:<cause>` (compile, in `beatSync.reason` and the
Dynamics warning), with the cause. A track that decoded and has no beat stays silent (`no_beat_grid`). The Dynamics
planner measures through the same workspace path (the MusicPlan-selected track first), not a local ffmpeg.

The pool pin for compile in the operator runbook stays as is: the pool stays off for
compile.

Traced for run 4f8aff78 (compile job `Expired`, then `compute_target 0`): `WaitForAssignmentAsync` sees the terminal
job, cancels it, disposes the session and opens a fresh `LocalMediaWorkspace` (`PoolWakeTimeoutLocalFallback`); the
compile opens its workspace once, so `ResolveBeatGridAsync` / `ApplyBeatSyncAsync` / `MusicBeatProbe` receive that
local one, and no per-session state (staged music name, `beat-sync-music`) points into the dead session. An analysis
without beat facts is re-measured by the compile, never trusted as "unavailable". `MusicBeatProbeExpiredJobFallbackTests`
drives the real factory over an `Expired` job and measures on the resulting workspace: it passes, so the lost grid in
that run was NOT reproduced by code; the cause stays unconfirmed (a different MusicPlan track with no detectable beat,
or a local decode failure, are the open candidates) and any recurrence now reports `beat_grid_failed:<cause>`.
