# The CloudPool worker

## What the CloudPool worker is

`reelbolt-worker` is the binary a compute-plane VM runs. It dials the runner
gateway over a WebSocket, authenticates with the pool credential, advertises what
that machine can actually do, and executes the jobs the gateway assigns to it.
It is the first embedder of the runner library in `sandbox/pkg/agent`: the
protocol, the connection, the lease renewal, the op dedupe cache and the
reconnect backoff all live there, and this binary supplies only the
configuration, the capability probe, the session executors and the process
lifecycle.

It is deliberately not the `sandbox-executor`. That process is the self-host
HTTP control plane: it listens, the engine calls it, and it holds a bearer token
so nobody else can. A pool worker listens on nothing, is reached by nobody, and
is recognised by the gateway because it dialled out. The two share
`sandbox/pkg/container` and nothing else.

The spec it implements is [the runner protocol](runner-protocol.md).

## Enrolling a pool worker (and why the worker cannot enroll itself)

**A pool worker with no `runner_devices` row is refused 4401.** The gateway
requires a row with `kind='CloudPool'` whose `name` matches the `workerName`
in the hello, and it refuses rather than inventing a synthetic identity. That is
deliberate: an identity nobody enrolled is an identity nobody can revoke.

There are two ways a row gets there.

**On-demand nodes (the pool waker): the gateway enrols the node, with a token.**
When [the pool waker](pool-waker.md) wants a node it calls the gateway's
`POST /internal/v1/pool/nodes` with the node name. The gateway writes the
`CloudPool` row in one transaction and returns a short-lived node token
(`rbn1.<payload>.<signature>`, an HMAC over the name and an expiry, keyed by the
gateway's `RUNNER_POOL_TOKEN_KEY`). The waker puts that token in the node's
cloud-init as `RUNNER_POOL_CREDENTIAL`. The node runs no `enroll` step and holds
**no database URL and no internal token**: the only credential on it is a token
bound to its own name, expiring and revocable
(`DELETE /internal/v1/pool/nodes/{name}` sets `revoked_at`). A hello whose
credential starts `rbn1.` is checked for signature, expiry, name match and an
unrevoked row; a reconnect after the token expires is refused, so expiry is
terminal for that node. The static `RUNNER_POOL_CREDENTIALS` keep working
unchanged for hand-run workers. Without `RUNNER_POOL_TOKEN_KEY` (32 characters
or more) these routes answer `503 pool_tokens_disabled`.

**Hand-run workers: the out-of-band `enroll` subcommand.** For a VM you start
yourself, an operator runs enrolment once with the database credential:

```sh
DATABASE_URL=postgres://... REELBOLT_WORKER_NAME=pool-eu-1 reelbolt-worker enroll
```

`enroll` is idempotent: the same name always lands on the same row, and a
second boot updates `os`, `arch` and `runner_version` rather than inserting a
rival. Both paths follow the same rules, described next.

Three properties are worth knowing before changing either:

- **It never clears `revoked_at`.** A revoked identity stays revoked until an
  operator passes `-reinstate` (token issue answers `409 node_revoked` for a
  revoked name). If enrollment resurrected a burned worker on the next boot,
  revocation would be a decoration.
- **It refuses an ambiguous name.** The gateway's lookup is
  `ORDER BY created_at LIMIT 1`, so two rows sharing a name mean the worker's
  identity depends on row age. `enroll` reports the conflict and writes nothing.
- **The running worker must not hold the database credential.** `reelbolt-worker run`
  refuses to start with `DATABASE_URL` set. The enrollment credential and the
  service credential are different things, and only the first needs write access
  to `runner_devices`. On-demand nodes never see it at all.

The same refusal is what an operator sees when something goes wrong: a 4401 logs
a line naming the worker and the missing row, and exits with code 4 so a restart
loop does not paper over it.

**A worker name is an identity, and two live processes may not share one.** The
gateway supersedes by identity ([the runner protocol](runner-protocol.md), "the
new connection wins and the old one is closed with 4409"), and 4409 is terminal
in the agent library, so the displaced process exits and does not fight for the
socket. That is the right behaviour and the wrong deployment: give each VM its
own name (the hostname default does this) and never run two workers with one
enrolled row. It is also why the local smoke kills its own leftovers before it
starts: a surviving worker from an interrupted run reconnects to the same port
and steals the socket from the run that is starting.

## The worker's environment

| Variable | Meaning |
| --- | --- |
| `REELBOLT_GATEWAY_URL` | `wss://<app host>/runner/v1/connect` (or `wss://runner.<domain>/v1/connect` where nginx serves `RUNNER_HOSTNAME`). Required. |
| `RUNNER_POOL_CREDENTIAL` | The pool secret, or on an on-demand node the `rbn1.` node token the gateway minted for its name. Required. `RUNNER_POOL_CREDENTIAL_FILE` reads it from a mounted file instead (used only when the environment variable is empty). |
| `REELBOLT_WORKER_NAME` | The name of this worker's enrolled row. Defaults to the hostname — so a VM whose hostname changed has silently lost its identity. |
| `REELBOLT_WORKER_SLOTS` | Jobs run at once. Defaults to `floor(vCPU / SANDBOX_CPU_LIMIT)`, never below 1 and never above 64. |
| `REELBOLT_WORKER_CPUS` | Overrides the vCPU count, for a host or a container whose quota Go cannot see. |
| `REELBOLT_WORKER_SCRATCH` | Per-job scratch root. Defaults to `/var/lib/reelbolt/worker`. |
| `REELBOLT_WORKER_DRAIN_SECONDS` | How long the drain window is. Defaults to 120. |
| `SANDBOX_CONTAINER_CLI` | `docker` (default), `podman`, or `none` for a machine with no container runtime. |
| `SANDBOX_IMAGE` | The sandbox-runtime image. In the cloud this is the digest from `sandbox/runtime-image.lock`; on an on-demand node it comes from `/etc/reelbolt/node-image.env`, written when the snapshot was baked. |
| `SANDBOX_ROOT`, `SANDBOX_NETWORK`, `SANDBOX_CPU_LIMIT`, `SANDBOX_MEMORY_LIMIT`, `SANDBOX_PIDS_LIMIT`, `SANDBOX_TTL` | The container driver's settings, read exactly as `sandbox-executor` reads them, because it is the same driver. |
| `DATABASE_URL` | Read by `reelbolt-worker enroll` only, on a hand-run worker. Never set on an on-demand node. |

The vCPU count comes from `REELBOLT_WORKER_CPUS`, else the cgroup v2 `cpu.max`
quota, else the host's CPU count. The cgroup step is why this is not simply
`runtime.NumCPU()`: a worker shipped as a container with `--cpus=4` on a
64-core VM must advertise 4 slots, not 32.

## Capabilities: what a worker advertises, and why a digest matters

The capability document in the hello is what the gateway's claim query filters
on, so the worker only ever builds an executor for a job type it actually
advertised:

- **`video.compile` and `video.analyze.extract`** need ffmpeg. With a container
  runtime they run inside the runtime image, which is where the argv validator's
  guarantees are also enforced by the kernel: no network, a read-only root, the
  session directory as the only writable mount. Without one they run as host
  processes, which is what a desktop runner without Docker does.
- **`remotion.workspace-session`** additionally needs a **digest-pinned** runtime
  image. A locally built tag has no digest, and the worker then advertises the
  type not at all — a render whose runtime is not pinned is a render this fleet
  cannot reproduce, and offering it would be a promise the worker cannot keep.

A worker that advertises no job types is not an error, and the gateway counts it
as connected-but-useless rather than as capacity. It does mean no work will ever
arrive, so the worker says so in the log at startup and `reelbolt-worker caps`
prints the same document without connecting.

Startup also creates the `--internal` sandbox network if it is missing (a pool VM
is not a compose stack, and a missing network would fail every Remotion job at
container-create time with an error that does not name it) and removes leftover
workspace containers from a previous process. If the network cannot be created,
the worker degrades to the ffmpeg job types and says why, rather than failing to
start.

## Draining on SIGTERM

A spot reclaim or a scale-in sends SIGTERM. The worker then stops accepting work,
sends `drain` with its deadline and `bye`, closes, and exits — within
`REELBOLT_WORKER_DRAIN_SECONDS` (120 by default). Sessions it holds are not
waited for: the protocol's rule is that they are rehydrated elsewhere, so the
window bounds the drain, the goodbye and the close rather than a render.

Two consequences for the deployment: a systemd unit needs `TimeoutStopSec`
above the drain window, and a container platform needs a stop timeout above it
too. `sandbox/pkg/poolwaker/cloud-init.yaml` sets 150 seconds.

## The pool scaler: reading the scaling signal

`reelbolt-scaler` turns the gateway's answer into a node count:

```sh
RUNNER_GATEWAY_URL=http://runner-gateway:8090 \
RUNNER_INTERNAL_TOKEN=... \
POOL_SLOTS_PER_NODE=4 POOL_MAX_NODES=10 \
reelbolt-scaler -json
```

`GET /internal/v1/scaling` returns `desiredCloudPoolSlots` = queued + running + a
configurable buffer. **The scaler reads that number and never recomputes it**: a
second formula for the same quantity is a second thing to keep in step, and the
two would disagree the first time one of them changed. Its own arithmetic is only
the translation from slots to nodes, plus two rules:

- **Never below what is running.** Scaling a node away under a job that is
  already running fails that job; it is not a saving. `runningNodes` is a floor
  whatever `POOL_MAX_NODES` says.
- **A ceiling below that floor is reported, not obeyed.** An operator whose cap
  is smaller than the fleet in use has a configuration problem, and terminating
  renders would hide it.

It also reads `/metrics` and cross-checks the fleet-wide
`reelbolt_runner_queue_depth` and `reelbolt_runner_free_slots` gauges against the
endpoint's per-replica counts. Those two are the gateway's registrations and the
scaler only ever parses them — the free-slot and connection counts in the
endpoint describe one replica, while the queue depth is database-wide, and a
scaler pointed at a single replica sees the whole queue and one pod's capacity.
That combination over-scales, and saying so out loud is cheaper than discovering
it on a bill.

**Without `-apply` the scaler is a dry run.** It prints the decision and changes
nothing. With `-apply` it runs `POOL_SCALE_COMMAND` through a shell with the
whole decision in its environment (`POOL_NODES`, `POOL_DESIRED_SLOTS`,
`POOL_RUNNING`, `POOL_QUEUED`, `POOL_CAPPED_BY`, ...), so a logged hook
invocation is a complete record of the round. Every value in that environment is
an integer or a fixed word this program computed.

## What provides the provider call

The scaler above stays a single-shot decision with a dry run and a hook. The
provider call is made by [the pool waker](pool-waker.md), a separate loop in the
same binary (`reelbolt-scaler loop`, off unless `POOL_WAKER_ENABLED=true`). It adds
what this section's former list asked for:

1. **The provider and its credential**: a Hetzner Cloud API token, read from
   `HCLOUD_TOKEN` or `~/.config/reelbolt/hcloud.token`.
2. **A cooldown and convergence policy**: a control loop with a cooldown, an idle
   window, a node cap and a daily spend cap.
3. **The cloud-init that makes a node join the pool**: `sandbox/pkg/poolwaker/cloud-init.yaml`,
   with the node token and a snapshot image that already holds the images.

`POOL_MAX_NODES` is in an operator file, and the per-organisation
CloudPool cap is `pool_slot_cap`, which the gateway enforces at claim time rather
than the scaler or the waker.

## Building the image and running the local smoke test

The image is the `worker` target in `sandbox/Dockerfile` and builds in the CI
`docker` matrix alongside the others (`docs/ci.md`). It carries the worker and
the scaler, the docker CLI and ffmpeg. It deliberately does not carry the
sandbox-runtime image: that is a separate image pinned by digest, so the render's
runtime is a pinned artefact rather than whatever a node happened to have. There
is no GHCR in this repository: an on-demand node does not pull either image. Both
are built off-box and loaded into a Hetzner snapshot by an operator file,
which records their references in `/etc/reelbolt/node-image.env` ([`pool-waker.md`](pool-waker.md),
"Operating it"). There
is no `EXPOSE` and no healthcheck, because a pool worker listens on nothing —
its liveness is the connection the gateway's own
`reelbolt_runner_connected{kind="CloudPool"}` gauge reports.

```sh
sandbox/scripts/worker-smoke.sh
```

stands up a real gateway, a throwaway Postgres and a real worker, and drives one
`video.compile` job end to end. It proves, with nothing faked:

1. an unenrolled worker is refused 4401, and the refusal names the remedy;
2. `enroll` creates exactly one row, and a second run lands on it;
3. the same binary then handshakes as a `CloudPool` runner;
4. **ffmpeg actually runs inside a real container** of the sandbox-runtime image,
   encodes an MP4, and ffprobe reads its dimensions back through the session;
5. the job completes as `Succeeded`;
6. the scaling hook reads the endpoint and runs its command;
7. SIGTERM drains the worker and it exits.

It does **not** cover `remotion.workspace-session` (needs a digest-pinned
runtime), presigned `stage`/`publish` (needs an object store with a public
endpoint), or any cloud provider. The first two are D8/D11 territory; the third
is covered by the pool waker's own tests with a fake provider; no test calls a real cloud.

**What it caught.** Driving the real path is what found two defects that no fake
driver could: the media executor's default container CLI executed a binary named
`run` instead of the container CLI (every real media session failed while the
fake-CLI tests stayed green), and a session ended by the `close` op never
reached a terminal state — the job sat in `Running` until its lease expired and
the reaper called it `Expired`, so the engine would have been told
`session_lost` for a step that succeeded. Both are fixed and both now have
regression tests.
