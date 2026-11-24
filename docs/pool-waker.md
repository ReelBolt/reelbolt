# The pool waker

Internal. This page is for operators and engineers; it is not published and has no user-guide page.

The pool waker starts a cloud server when CloudPool work is queued and no pool worker is online,
and deletes it when the work is done. It is off by default, in two places: the waker's own
`POOL_WAKER_ENABLED` and the engine's `Compute:PoolWaker:Enabled`. With both off, nothing in the
product changes. For the compute fabric see [`compute-fabric.md`](compute-fabric.md); for the worker
that runs on a node see [`cloud-worker.md`](cloud-worker.md).

## Design

Three processes are involved.

| Process | Role |
|---|---|
| `reelbolt-scaler loop` (the waker) | Reads demand and runners from the gateway, creates and deletes servers at the provider, sends a heartbeat. |
| `runner-gateway` | Mints node tokens and enrols the node name, reports waker health on `GET /internal/v1/runners`, hands the queued job to the node that connects. |
| Workflow engine | With `Compute:PoolWaker:Enabled`, queues a CloudPool job and waits for a node instead of running `Local`; falls back to `Local` on timeout. |

The waker never touches the database and never sees job contents. It talks to the gateway with the
internal bearer token (`RUNNER_INTERNAL_TOKEN`) and to the provider with the provider token.

Providers sit behind one interface (`sandbox/pkg/poolwaker/provider.go`). `POOL_PROVIDER=hetzner`
(the default) is the real one; `fake` is an in-memory provider for tests.

### The loop

The loop runs every `POOL_LOOP_INTERVAL_SECONDS` (default 15). Each iteration:

1. **List** the provider's servers that carry the waker's labels and this instance. A list failure
   counts as a provider error and stops the iteration after a heartbeat.
2. **Read** the gateway's runners and slot demand. If either read fails the waker creates nothing
   this iteration and reports `gateway_unreadable`.
3. **Reconcile** every listed server (see "Orphan cleanup").
4. **End** nodes that are done: idle and within `POOL_HOUR_END_MARGIN_SECONDS` of the end of their
   paid hour with an empty queue (see "Keeping a node to the end of its paid hour"), past the boot
   deadline without connecting, gone from the gateway longer than the dead grace, or older than the
   maximum lifetime. Ending a node revokes its token, deletes the server and writes a usage record.
5. **Scale up** toward the gateway's desired node count, but only for queued jobs that no connected
   free node and no booting node can take, subject to the caps below, the cooldown between
   creations (`POOL_COOLDOWN_SECONDS`) and the circuit breaker.
6. **Heartbeat** to the gateway.

### What counts as demand

The waker's demand is `awaitingWorker` from `GET /internal/v1/scaling`: every CloudPool job that is
waiting for a pool worker. That is a job with status `Queued` at any attempt (new, or retried after a
failure), plus an `Assigned` or `Running` job whose lease belongs to a
runner no longer connected to this gateway replica and has not been renewed for 30 seconds. The second
kind is what a reused job looks like when its earlier worker has gone: the idempotent job create hands
the engine the same job back (`reused ... attempt 1` in the engine log), it reads as running, and
before this count it was no demand at all, so no node was woken while the engine waited out its window.
This window is short: once its lease lapses a session job is expired rather than requeued, so the
count covers roughly 30 to 55 seconds after the last renewal and often ends before a woken node has
booted. The cost is bounded by the caps. The 30-second grace keeps a worker that drops and reconnects from costing a server.

Not demand: a job a connected worker holds, a finished job, and a job of another gateway replica
(the orphan check is made per replica). `desiredCloudPoolSlots` is unchanged (queued + running + buffer).
A gateway that predates the field reports only `queued`, and the waker falls back to it.

One job never buys two servers: a booting or free connected node counts against `awaitingWorker`
before a new one is created. The daily spend cap, the node cap and the hour-aligned keep-alive apply to
this demand exactly as to any other.

A node moves through `starting`, `connected`, `idle`, `draining`. Creating a node mints its token
first, then creates the server with the token in its user-data; if the create fails the token is
revoked again.

### Keeping a node to the end of its paid hour

Hetzner bills every started hour in full, counted from the server's created time, so deleting an
idle node after a few minutes saves nothing. An idle node therefore stays connected to the pool
and takes any job that arrives, one job at a time, until `POOL_HOUR_END_MARGIN_SECONDS` (default
300) before the end of the hour it has paid for. Then the waker revokes its token and deletes it
(usage reason `hour_end`).

- A node with a running job is never deleted by these rules. It is renewed into the next hour like
  any busy node: the next hour is charged to the spend ledger a little before the boundary
  (`HourLookahead`), once, and the node's margin moves to the end of that hour.
- Queued jobs at the margin or the boundary keep an idle node and renew it, because that node is
  the one that will take them. With nothing queued, an idle node is never renewed: it is let go.
  A queued job that no node ever claims therefore keeps renewing idle nodes; the daily spend cap, the
  node cap and the 6 hour maximum lifetime bound that.
- The waker does not create a server while a connected node has a free slot, or a node is still
  booting, for a queued job. Only jobs beyond that capacity add a server. The gateway's spare
  `RUNNER_POOL_BUFFER_SLOTS` therefore no longer starts a server by itself.
- One job at a time per node is enforced twice: a node started by the waker gets
  `SANDBOX_CPU_LIMIT=all`, which is one slot, so the gateway offers it at most one job, and the
  node's agent answers a second offer with `busy`.
- `POOL_IDLE_SECONDS` is an optional earlier end. Unset or `0` (the default) means hour-aligned.
  When set, a node idle that long with an empty queue is deleted, even though the hour is paid.

### Caps

| Cap | Default | Env | Behaviour at the limit |
|---|---|---|---|
| Nodes | 2 | `POOL_MAX_NODES` | Counts every labelled server the provider shows, not only the ones the waker is tracking. No more are created. |
| Spend per UTC day | EUR 5 | `POOL_DAILY_SPEND_CAP_EUR` | A node is charged for its first billed hour when created and for each further hour as it enters it. A charge that would pass the cap is refused: no node is created, and a node about to enter a new hour is deleted (at once if idle, just before the hour boundary if busy). The waker reports `daily_cap_reached` and stays capped until the UTC day rolls over. |
| Alert | 80% of the spend cap | `POOL_ALERT_FRACTION` | The heartbeat reason becomes `spend_alert` while the waker stays healthy. |
| Server price unknown | n/a | n/a | The waker fails closed: no node is created while it cannot read the hourly price (`price_unknown`). |

The spend ledger is a small JSON file (`POOL_STATE_FILE`) holding the current UTC day and the
amount spent. A corrupt ledger stops the waker from starting rather than resetting the day.

### Orphan cleanup

Servers the waker did not create in this run are handled on every iteration, which also covers a
restart:

- A labelled server that is **connected** to the gateway and younger than the maximum lifetime is
  **adopted**: the waker tracks it and does not delete it.
- A labelled server that is **young enough to still be booting** (within the boot deadline, or the
  gateway cannot be read) is left alone, and counts against the node cap.
- Any other labelled server is an **orphan**: it is deleted and a usage record with reason `orphan`
  is written.

The waker only ever lists and deletes servers carrying its pool label and its `POOL_INSTANCE`, so a
staging waker and a production waker in one account do not touch each other's servers. Use a Hetzner
project of its own for the pool.

Every node also carries a 6 hour `shutdown` timer in its cloud-init
(`sandbox/pkg/poolwaker/cloud-init.yaml`) as a last line against an orphan the waker lost track of. A powered
off server still bills until deleted, so the waker's cleanup still has to delete it.

### Circuit breaker

Three consecutive nodes that never connect within `POOL_BOOT_DEADLINE_SECONDS` open a breaker for
15 minutes: no node is created, the heartbeat reason is `breaker_open`. Three consecutive provider
errors report `provider_errors` and stop creation until a list succeeds.

### Heartbeat

Every iteration the waker sends `PUT /internal/v1/pool/waker`:

```json
{"healthy": true, "reason": "ok", "nodes": 1, "maxNodes": 2, "capped": false, "spendTodayEur": 0.0231, "instance": "default"}
```

The gateway stores it with its own timestamp and adds this to `GET /internal/v1/runners`:

```json
{"pool": {"wakerHealthy": true, "wakerAgeSeconds": 4, "reason": "ok", "capped": false}}
```

`wakerHealthy` is true only when the last heartbeat is at most 45 seconds old, reports `healthy`, and
is not `capped`. A stopped, crashed or capped waker therefore turns itself off for the engine within
45 seconds, and the engine goes back to running `Local`.

Reasons: `ok`, `spend_alert` (still healthy), `daily_cap_reached`, `breaker_open`,
`provider_errors`, `gateway_unreadable`, `price_unknown`, `size_unavailable`.

### Usage records

When a node ends the waker appends one JSON line to `POOL_USAGE_FILE` (default `usage.jsonl` next to
the ledger file):

| Field | Type | Meaning |
|---|---|---|
| `node` | string | Node name, `rb-pool-...`. |
| `provider` | string | `hetzner` or `fake`. |
| `serverType` | string | Server type, for example `ccx33`. |
| `instance` | string | `POOL_INSTANCE`. |
| `createdAt` | RFC 3339 | When the waker created the server (for an orphan, the server's own creation time). |
| `readyAt` | RFC 3339 or `null` | When the node connected; `null` if it never did. |
| `endedAt` | RFC 3339 | When the server was deleted. |
| `nodeSeconds` | integer | Created to ended. |
| `billedHours` | integer | Hours billed, rounded up, at least 1. |
| `estimatedEUR` | number | `billedHours` times the hourly price. An estimate, not an invoice. |
| `jobSeconds` | `null` | Reserved. Always `null` today. |
| `reason` | string | `hour_end`, `idle` (only with `POOL_IDLE_SECONDS`), `boot_deadline`, `worker_gone`, `orphan`, and the other end reasons. |

Nothing reads this file yet. **Charging credits for pool time is a follow-up** that will consume this
schema; until then pool compute is a platform cost and is not metered to organisations.

### Metrics

With `-listen :9108` the loop serves Prometheus text on `/metrics`, with the prefix
`reelbolt_poolwaker_`: `nodes`, `max_nodes`, `spend_today_eur`, `spend_cap_eur`, `capped`,
`breaker_open`, `nodes_created_total`, `nodes_adopted_total`, `orphans_found_total`,
`boot_failures_total`, `breaker_opened_total`, `provider_errors_total` and `node_seconds_total`.
Bind it to a private address; it has no authentication.

## Configuration

Waker (`reelbolt-scaler loop`):

| Variable | Default | Meaning |
|---|---|---|
| `POOL_WAKER_ENABLED` | `false` | The loop refuses to run unless this is `true`. |
| `POOL_PROVIDER` | `hetzner` | `hetzner` or `fake`. |
| `POOL_INSTANCE` | required for `hetzner` | Deployment label (`staging`, `production`); scopes list and delete. |
| `POOL_MAX_NODES` | `2` | Hard node cap. |
| `POOL_SLOTS_PER_NODE` | `1` | Slots one node serves; translates gateway slot demand into nodes. |
| `POOL_HOUR_END_MARGIN_SECONDS` | `300` | An idle node is deleted this long before the end of its paid hour. Must be under 3600. |
| `POOL_IDLE_SECONDS` | `0` (off) | Optional earlier end: delete a node after this much idle time with an empty queue. Off keeps nodes to the end of the hour. |
| `POOL_COOLDOWN_SECONDS` | `60` | Minimum time between node creations. |
| `POOL_BOOT_DEADLINE_SECONDS` | `300` | Time a node has to connect. |
| `POOL_DAILY_SPEND_CAP_EUR` | `5` | UTC-day spend cap. |
| `POOL_ALERT_FRACTION` | `0.8` | Fraction of the cap that raises `spend_alert`. |
| `POOL_LOOP_INTERVAL_SECONDS` | `15` | Time between iterations. |
| `POOL_STATE_FILE` | `pool-waker-ledger.json` | Spend ledger path. A directory gets `ledger.json` inside it. |
| `POOL_USAGE_FILE` | `usage.jsonl` beside the ledger | Usage record path. |
| `RUNNER_GATEWAY_URL` | none | Gateway base URL. Required. A plain `http://` URL is refused unless it is loopback or the compose service name, or `RUNNER_GATEWAY_ALLOW_PLAIN_HTTP=true`. On the two-box deployment it is `https://<ENGINE_PRIVATE_IP>:8443`. |
| `RUNNER_GATEWAY_CA_FILE`, `RUNNER_GATEWAY_CLIENT_CERT_FILE`, `RUNNER_GATEWAY_CLIENT_KEY_FILE` | none | Mutual TLS to the gateway (TLS 1.3 minimum): the CA, and the waker's own client certificate and key. Set all three or none; an inconsistent set is refused. |
| `REELBOLT_ROLE` | none | With `media`, the scaler refuses to start with a provider token unless `POOL_WAKER_ALLOW_ON_MEDIA_HOST=true`. The waker belongs on the control host. |
| `RUNNER_INTERNAL_TOKEN` | none | Gateway internal bearer token. Required. |
| `POOL_NODE_GATEWAY_URL` | none | The URL nodes dial. Required. On the two-box deployment it is `ws://<ENGINE_PRIVATE_IP>:8091/v1/connect`, the private `pool-edge` listener, which forwards only `/v1/connect` to the gateway. |
| `SANDBOX_CPU_LIMIT` | `all` | Passed to each node; slots = floor(vCPU / limit). `all` = every core of the node, one slot (see "All cores"). |
| `POOL_SIZE_REFUSAL_BACKOFF_SECONDS` | `3600` | How long a server size the provider refused is left alone (see "Server sizes"). |
| `HCLOUD_TOKEN` | none | Hetzner API token. If unset, `~/.config/reelbolt/hcloud.token`. |
| `HCLOUD_SNAPSHOT_ID` | none | The baked snapshot id, or `latest` for the newest snapshot labelled `reelbolt-pool-image=1`. Required for `hetzner`. |
| `HCLOUD_SERVER_TYPES` | `ccx33,ccx23,ccx13` | Server sizes in order of preference (see "Server sizes"). |
| `HCLOUD_SERVER_TYPE` | none | One size; used only when `HCLOUD_SERVER_TYPES` is unset. |
| `HCLOUD_FIREWALL` | required | Id of a firewall with no inbound rules; the scaler refuses to start without it. |
| `HCLOUD_LOCATION` | unset | Optional placement. |
| `HCLOUD_NETWORK` | unset | Id of the Hetzner private network nodes join. Requires `HCLOUD_NETWORK_SUBNET`. |
| `HCLOUD_NETWORK_SUBNET` | unset | Dedicated node subnet inside that network, for example `10.0.2.0/24` (private IPv4, `/28` or larger). The waker attaches each node with an explicit address from it, so host rules can isolate it. |

Flags: `-once` runs one iteration, `-dry-run` logs decisions and creates or deletes nothing,
`-listen ADDR` serves metrics.

Gateway: `RUNNER_POOL_TOKEN_KEY` (32 characters or more) enables node tokens. Unset, the pool routes
answer `503 pool_tokens_disabled`, and the waker cannot create nodes. `HCLOUD_TOKEN_FILE` names a
0600 file holding the Hetzner token (used by the compose service; takes precedence over the home
file). The compose wiring and the ordered go-live steps are in the operator runbook.

Engine: `Compute:PoolWaker:Enabled` (default `false`) and `Compute:PoolWaker:WaitSeconds` (default
`240`). See [`compute-fabric.md`](compute-fabric.md), "Waking a node".

### Server sizes

Pool nodes are Hetzner dedicated-core `ccx` servers, so a job gets whole cores of its own and its
speed does not depend on a neighbour. `HCLOUD_SERVER_TYPES` is an ordered, comma-separated list; the
waker takes the first size that is not backing off, has a known price and still fits under the daily
cap. The default is `ccx33,ccx23,ccx13`. The single `HCLOUD_SERVER_TYPE` of earlier versions still
works when `HCLOUD_SERVER_TYPES` is unset.

| Size (nbg1, net) | vCPU | EUR/h |
|---|---|---|
| `ccx13` | 2 | 0.069 |
| `ccx23` | 4 | 0.138 |
| `ccx33` | 8 | 0.222 |
| `ccx43` | 16 | 0.442 |
| `ccx53` | 32 | 0.855 |

Prices move; the waker always reads the live hourly price from the API and never uses this table.
The gateway's demand is a node count, not a job size, so there is no job-to-size mapping: the
order of the list is the policy. To prefer a bigger server, put it first.

A refusal of one size is not an outage. When the API refuses a create for a reason tied to the
account or the size (`resource_limit_exceeded`, such as "dedicated core limit exceeded", `resource_unavailable`,
`placement_error`), the waker revokes the node credential it had minted, leaves that size alone for
`POOL_SIZE_REFUSAL_BACKOFF_SECONDS` (default 3600) and tries the next size in the same iteration. The
refusal is counted in `size_refused_total`, is not a provider error and not a boot failure (it does
not open the circuit breaker). When every size is backing off the waker creates nothing, reports
`size_unavailable` (unhealthy, so the engine runs `Local`) and makes no further create calls until a
backoff ends. The account's core limit is a Hetzner setting; the waker never tries to raise it.

The caps do not change: EUR 5 per UTC day and 2 servers by default, still fail closed, now priced
at the size that is actually created. When the preferred size no longer fits under the remaining
daily budget, a cheaper size further down the list is used; when none fits, nothing is created
(`daily_cap_reached`). A size whose price cannot be read is skipped, never guessed
(`price_unknown` when no size has a price).

### All cores

A node exists for one heavy job at a time, so on a pool node that job gets the whole machine. On
every other host nothing below changes. Where compute is limited today, and what a pool node does:

| Knob | Where | Default elsewhere | On a pool node |
|---|---|---|---|
| `SANDBOX_CPU_LIMIT` (container `--cpus`, and slots = floor(vCPU / limit)) | worker env | `2` | `all`: the node's whole-core count, so one container and one slot. Any number still works. |
| `SANDBOX_MEMORY_LIMIT` | worker env | `2g` | `auto`: three quarters of the node's memory. |
| `SANDBOX_PIDS_LIMIT` | worker env | `256` | `2048` (Chromium tabs and ffmpeg threads count). |
| `REELBOLT_WORKER_USE_ALL_CORES` | worker env | off | `true`: the worker raises the engine's ffmpeg `-threads N` and Remotion `--concurrency=N` to the core count. |
| ffmpeg containers (VideoAnalyze, VideoCompile, TimelineRender) | worker | no CPU limit | unchanged (they were never limited). |
| `VideoEditing:FfmpegThreads` (4), `MaxConcurrentJobs` (1), `DerivativeMaxConcurrentJobs` (1), `AnalysisProxyThreads` (0 = every core) | engine | as listed | not changed. The engine builds the commands; the worker raises their thread counts. These settings still describe the engine host. |
| `Compute:Render:DefaultConcurrency` (2) | engine | `2` | not changed; the worker raises `--concurrency` per the rule below. |
| Pool systemd unit | node | n/a | no CPU cap in the unit (`CPUQuota` is not set); Docker runs the worker with `--network host` and no `--cpus`. |

The raise rule is narrow on purpose. Only an explicit integer of 2 or more that is below the core
count is raised. `-threads 0` and `auto` already mean every core. A `1` is how the engine, and the
out-of-memory retry of a render, ask for one-at-a-time, so it is never raised. A value at or
above the core count is left alone (Remotion refuses a concurrency above the core count), and a
percentage is left alone. Only the worker does this, only when `REELBOLT_WORKER_USE_ALL_CORES` is
true, and only the number changes.

One heavy job per node comes from the slot count: `all` is one slot, so a node takes one job at a
time and the waker adds a node (up to `POOL_MAX_NODES`) for the next. `POOL_SLOTS_PER_NODE` stays `1`.
The live engine host's settings are not changed by any of this.

### Boot time

A node boots from the baked snapshot to a worker log line in about 25 to 33 seconds (measured:
`cpx22` 33 s, `ccx13` 29.6 s, `ccx33` 24.5 s). Only about 9 to 12 s of that is the operating
system: kernel 3.5 s and userspace 6 to 8 s. The slow parts were `systemd-networkd-wait-online`
(1.6 s), `snapd` (about 1 s) and cloud-init (2 to 4 s). an operator file runs during the
bake and:

- masks `snapd` and its units, `systemd-networkd-wait-online`, the apt timers and
  `unattended-upgrades`, the motd, man-db, fwupd and e2scrub timers, ubuntu-advantage, ModemManager,
  multipathd and the lxd installer (only units the image has; nothing is uninstalled);
- writes `/etc/cloud/cloud.cfg.d/99-reelbolt-pool.cfg`: `datasource_list: [ Hetzner, None ]` (no
  probing of other datasources) and no package update or upgrade at boot.

It refuses to mask cloud-init, docker, containerd, networkd, resolved or ssh. The start path is
unchanged: cloud-init writes `/etc/reelbolt/worker.env` and the worker unit from the user-data and
enables the unit in `runcmd`, so the worker starts from systemd as soon as cloud-init is done. Nothing
waits for ssh, and no ssh key is in the image. With `wait-online` masked, `network-online.target` is
reached when networkd is up rather than when a link is configured, so the worker's first dial can
happen a moment early; it redials with a jittered backoff from one second, so that costs one
retry, not a failure. The numbers above were taken before these changes; measure a node baked with
them before quoting a new figure.

### Release snapshot

Two compatibility rules. Re-bake before you upgrade the waker: it now sends `SANDBOX_CPU_LIMIT=all`
and `SANDBOX_MEMORY_LIMIT=auto`, which a worker from an older snapshot passes straight to Docker and
cannot start with. And pruning deletes snapshots older than the newest two, including one that a
numeric `HCLOUD_SNAPSHOT_ID` pins, so use `HCLOUD_SNAPSHOT_ID=latest` with the release hook.

A node is only as current as the snapshot it starts from, so every release bakes one:

The release hook is an operator script that is not in the public repository. It takes the release
sha as the version.

Run it off the control host, after the release's images are built. It runs an operator script (the
snapshot is labelled `reelbolt-pool-image=1` and `reelbolt-version=<sha>`), then
`prune-snapshots.sh --keep 2`, which keeps the two newest snapshots that carry that label and deletes
the older ones. Only labelled snapshots are ever listed for deletion, each is re-checked by id just
before its delete, and the snapshot just baked is protected. A failed bake prunes nothing; a failed
prune leaves a good snapshot and exits 6. Without `--apply` it prints the plan and calls nothing.
`prune-snapshots.sh` on its own previews by default (it reads the project, deletes nothing) and
needs `--apply` to delete.

The waker follows the newest one: set `HCLOUD_SNAPSHOT_ID=latest` and each create boots from the
newest available snapshot labelled `reelbolt-pool-image=1` (looked up at most every five minutes). A
numeric `HCLOUD_SNAPSHOT_ID` still pins one snapshot. If no labelled snapshot exists, nothing is
created and the log says to bake one.

## Failure modes

| What breaks | What happens |
|---|---|
| Waker not running, crashed, or capped | The heartbeat goes stale or says not healthy; within 45 s `wakerHealthy` is false and steps run `Local`. |
| Gateway unreadable | The waker creates nothing and reports `gateway_unreadable`. Existing nodes are not deleted for lack of information. |
| Node never connects | Deleted at the boot deadline; three in a row open the breaker for 15 minutes. |
| Reused job, no node wakes | The engine logs the wait up front (`waits up to Ns for a pool worker on job ... status, attempt, new or reused`) and, for a reused job a worker still holds, a warning. The waker logs `no job is waiting for a pool worker` when it declines for lack of demand. |
| Node connects but the job waits too long | The engine gives up after the smaller of `WaitSeconds` and `RemoteMedia:AssignWaitSeconds` (a job a worker already holds gets a further 60 s of grace first), cancels the job and runs the step `Local` (`pool_wake_timeout_local_fallback`). The step does not fail. |
| Provider API errors | After three in a row the waker reports `provider_errors` and creates nothing. |
| Spend cap reached | No creation; heartbeat `capped`; engine runs `Local`. Resets at 00:00 UTC. |
| Token key unset on the gateway | `503 pool_tokens_disabled`; no nodes. |
| Waker restarts | Connected nodes are adopted, booting ones are left, the rest are deleted as orphans. |

### Fallback to Local

Every failure above ends the same way for the user: the step runs on the engine's own host, as it did
before the waker existed. The waker can make a step slower or cheaper to run elsewhere; it cannot make
one fail.

## Security model

- **Provider token.** Read from `HCLOUD_TOKEN` or `~/.config/reelbolt/hcloud.token`. The file must be
  a regular file, mode `0600` or stricter, owned by the user running the waker; otherwise the waker
  refuses it. The token is held in a type that prints as `[redacted]` in logs and JSON, and error
  messages name the path, never the content. Use a project of its own for the pool, so a leak
  reaches nothing else. Never commit the token.
- **Where it runs.** On a two-box deployment the waker runs on the control host, on a compose network of
  its own (no path to Postgres, RabbitMQ or the API), and reaches the media host's gateway over mutual
  TLS with its own client certificate. The media host holds no provider token.
- **Internal token.** `RUNNER_INTERNAL_TOKEN` stays on the waker host and the gateway. It is never put
  in user-data.
- **Node tokens.** `rbn1.` + base64url(`{name, exp}`) + `.` + base64url(HMAC-SHA256). Bound to one
  node name, expiring (default 4 hours, clamped by the gateway to 60 s to 24 h), revocable by name,
  and verified by the gateway at the handshake. A revoked name stays revoked. The node token is the
  only credential on a node.
- **What runs on the node.** Docker with the worker container and the sandbox-runtime image, both
  loaded from the snapshot and pinned by digest in `/etc/reelbolt/node-image.env`; no registry pull,
  no database URL, no sshd key. The worker holds the Docker socket and runs customer media, so one
  VM per node is the isolation boundary and the node holds nothing else of value. No inbound port is
  needed; attach a firewall with no inbound rules through `HCLOUD_FIREWALL`.
- **Private network.** Nodes join the private network in their own subnet and dial only the pool edge. A
  Hetzner Cloud Firewall does not filter private traffic, so an operator file installs
  host rules (chain `RB-POOL-ISOLATION`, in `INPUT` and `DOCKER-USER`) on both boxes, and the node's
  cloud-init locks the node side. Apply them before any node exists. Steps, verification and rollback are in
  the operator runbook.
- **Blast radius.** The waker can create at most `POOL_MAX_NODES` servers and spend at most
  `POOL_DAILY_SPEND_CAP_EUR` a day, and it can only list and delete servers with its own label.

## Operating it

The order matters. Do not set a flag before the step before it is done.

### 1. Stage 0: measure

Measure how long a fresh server takes to reach a worker log line, cold and cached. It creates one
small server, deletes it, and prints a timing table. It is a dry run unless you pass `--apply`.

The measurement script is an operator script that is not in the public repository. Write your own
equivalent: create a server from the snapshot, wait for the first worker log line, delete the
server, and record the time.

You must name where the images come from (`--worker-image` and `--runtime-image` on a registry you
control, or `--tarball-url` / `--tarball-file`); the repository has no GHCR. Put the token in place
first:

```sh
mkdir -p ~/.config/reelbolt && chmod 700 ~/.config/reelbolt
printf '%s\n' '<token>' > ~/.config/reelbolt/hcloud.token
chmod 600 ~/.config/reelbolt/hcloud.token
```

It costs cents. Decide from the table whether a snapshot start is fast enough for `WaitSeconds`.

### 2. Bake the snapshot

Run the bake on a laptop or a builder, never on the control host. It builds both images, loads them
on a temporary server, writes `/etc/reelbolt/node-image.env`, snapshots, and deletes the server.

The bake script is an operator script that is not in the public repository. It takes the version tag
to bake and runs only when you explicitly allow it.

It prints the snapshot id. Bake again for every release: an operator file does it and prunes old snapshots (see "Release snapshot"), and `HCLOUD_SNAPSHOT_ID=latest` makes the waker use the newest.
Details are in an operator file.

### 3. Dry-run the waker

On the control host, with the snapshot id and gateway settings in the environment:

```sh
POOL_WAKER_ENABLED=true HCLOUD_SNAPSHOT_ID=<id> POOL_INSTANCE=production \
  reelbolt-scaler loop -once -dry-run
```

It logs what it would do and creates nothing. Check the logged settings and the heartbeat reason.

### 4. Turn the flags on

1. Set `RUNNER_POOL_TOKEN_KEY` on the gateway and restart it.
2. Run the waker (on a two-box deployment: the `pool-waker` compose service on the control host, see
   the operator runbook), with `POOL_WAKER_ENABLED=true`.
3. Check `GET /internal/v1/runners` for `pool.wakerHealthy: true`.
4. Set `Compute:PoolWaker:Enabled=true` on the engine (env `Compute__PoolWaker__Enabled=true`) and
   restart it.

To turn it off, set the engine flag to `false` first, then stop the waker. Servers the waker left
behind are deleted by the next waker that runs with the same `POOL_INSTANCE`, or by hand in the
provider console (look for the pool label).

### Watching it

- `spend_today_eur` against `spend_cap_eur`, and the `spend_alert` reason, which appears at 80%.
- `orphans_found_total` rising means nodes are being lost; read the waker log for the cause.
- `boot_failures_total` and `breaker_open` point at a bad snapshot or a gateway URL nodes cannot reach.
- Compare `estimatedEUR` in the usage file with the provider invoice at month end.
