# The desktop runner

The desktop runner is the machine side of the compute fabric. This page is the
developer and operator's view: what the module is, how pairing works, what the
governor does, how the signed auto-update is verified, what must change before
a release can ship, and how to build and test the tree. The user-facing guide
— install, pairing, settings, what data leaves the machine, revoke and
troubleshooting — is [user-guide/account/desktop-runner.md](user-guide/account/desktop-runner.md).
The wire contract the runner speaks is [the runner protocol](runner-protocol.md);
the deployment-side view (the three targets, the selector, turning the fabric on)
is [compute-fabric.md](compute-fabric.md).

## Module layout

`runner/` is a single Go module, and it builds and tests without a GUI toolchain
or a container runtime. Two binaries live in it:

- **`reelbolt-runner`** (`runner/cmd/reelbolt-runner`) — the headless daemon:
  `pair`, `run`, `status`, `pause`, `resume`, `doctor`, `unpair`, `update`,
  `version`. It dials out to the runner gateway and never listens on a port.
- **`reelbolt-runner-desktop`** (`runner/cmd/reelbolt-runner-desktop`) — the Wails
  tray shell: the same daemon in-process, with a tray and a window. It is
  **a separate Go module**, because Wails v3 is a cgo build (gtk4 + webkitgtk on
  Linux, MinGW-w64 on Windows) and a module's dependencies resolve as a whole —
  requiring it from `runner/go.mod` would break `go vet ./...` on every machine
  without a GUI stack, which is every CI runner in this repository. See
  `runner/cmd/reelbolt-runner-desktop/README.md` for the build command and the
  two build tags (`-tags server`, the built-in MCP server) that open a TCP
  listener and must never be used in a product build.

The packages, and what each owns:

| Package | Owns |
| --- | --- |
| `internal/config` | `runner.json` (0600) under the OS user config dir: gateway URL, device id/name, slots (1 default, 16 max), runtime image, container CLI, per-job `limits`, the `schedule` block, update channel/interval/base-URL. Validated on **every** load — the file is user-editable, and the gateway it names must match the compiled-in allowlist. |
| `internal/identity` | the per-device Ed25519 key: OS keyring, or a 0600 `device.key` file with a logged warning. The private key never leaves the machine. |
| `internal/pairing` | the device's half of RFC 8628: `pair/start`, the poll loop, the result. It talks to the API, not the gateway, and holds no bearer token. |
| `internal/daemon` | the daemon: build executors from the capability probe, the governor, the auto-update loop, the SIGTERM/SIGINT drain sequence, the control channel that turns a gateway `drain` into a local pause/resume. |
| `internal/governor` | when this machine does **not** work: the `[schedule]` policy, an explicit pause, and the `[limits]` a job runs under. `status` names the reason; see below. |
| `internal/runtime` + `internal/runtimeimage` | the container runtime probe (Docker 24+, Podman 5+, a live-probed `--internal` no-egress network) and the pinned runtime image. The image reference is a digest when the release pipeline has published one; otherwise `--runtime-image`, `runner.json`, then `REELBOLT_RUNTIME_IMAGE`. Only a pinned digest is advertised in `capabilities.runtimeImageDigest`, and the gateway refuses a job whose `requires.runtimeImageDigest` differs. |
| `internal/caps` | the capability document the gateway routes on: container kind/mode, job types, ffmpeg version, fontset, max output bytes. A machine with no container runtime advertises `video.compile` and `video.analyze.extract` only; `remotion.workspace-session` needs a verified runtime. |
| `internal/update` | the signed auto-update: manifest format, the Ed25519 verification, the rollout rule, the install/rollback. Its own README is the contract. |
| `internal/ui` | everything the tray and the window say: the view, the menu, the actions, the first-run pairing wizard, the local API routes (`/view`, `/menu`, `/do`, `/settings`, `/pairing`), autostart, the single-instance lock. Plain Go, unit-tested, so a tray whose contents were decided in the Wails host could not disagree with the window. |
| `internal/foldersync` | computer folder sync: the link list, the fsnotify watcher, the local hash index, the manifest reconciliation and the status document. Explained in [local-folder-sync.md](local-folder-sync.md). |
| `internal/deviceapi` | the signed client for the Inference API's `/api/v1/device/**`: Ed25519 request signing over the method, the exact path and query, a timestamp, a nonce and the body hash. There is no bearer token. |

**Where state lives.** Configuration: `os.UserConfigDir()/ReelBolt/runner.json`.
Device key: OS keyring or a 0600 file. Job scratch:
`os.UserCacheDir()/ReelBolt/jobs/<jobId>`, deleted when the job ends.
Workspaces: `os.UserCacheDir()/ReelBolt/workspaces/<id>`, bind-mounted into the
workspace container. Pause/resume: a small request document in the cache dir,
written by `pause`/`resume`, consumed (then deleted) by the running daemon.
Live runner state: `runner.live.json`, written while the daemon runs and removed
when it stops. Folder sync adds its own files: the link list
`os.UserConfigDir()/ReelBolt/folders.json` (0600), a per-link hash index under
`os.UserCacheDir()/ReelBolt/folder-index/`, and the published status document
`folder-sync.status.json` in the runner config directory. Downloaded source
media: `os.UserCacheDir()/ReelBolt/media`, capped by `mediaCache.maxBytes` (see
below). Logs: stderr plus `logFile` if set.

The daemon listens on **no port**. The only socket is the outbound WebSocket to
the gateway; `runner/TESTING.md` carries the `lsof`/`netstat` check a human
runs to catch a regression.

## Pairing protocol

Pairing is RFC 8628 (device authorization grant). The machine proves possession
of a key it generates locally; the user types a short code into the dashboard
and approves the device there; the poll then yields the device id, the
organization and the gateway URL.

1. **Start** (`POST /api/v1/runners/pair/start`): the daemon uploads the 32-byte
   Ed25519 **public** key, the device name, the OS/arch and its version — what
   the approver sees — and receives a `userCode`, a `deviceCode`, a
   verification URI and the poll interval. A key already paired on the account is
   `already_paired` and the daemon says so.
2. **Approve**: the user opens the URI (the code is pre-filled as a query
   parameter), checks the code against what the machine shows, chooses which
   workspace the device goes into (its own Personal workspace, or a Team where
   they are Owner or Admin) and approves. Approving emails every Owner of that
   workspace. An unknown, expired, used or denied code, and a workspace the
   caller is not a member of, are **all the same 404** — the API refuses to
   distinguish them (D5), and the dashboard screen refuses to render a 403 where
   the server said 404.
3. **Poll**: the daemon polls no faster than the advertised interval, backs off
   on `slow_down`, and stops at the server's expiry (ten minutes). Denied is
   `access_denied`; expired is `expired_token`.
4. **Handshake**: `run` then dials the gateway's WebSocket and the first message
   is a `hello` carrying an Ed25519 signature over
   `reelbolt-runner-hello-v1|<deviceId>|<ts>|<nonce>|<gatewayHost>` — the host
   binding stops a hostile gateway replaying the hello elsewhere. The gateway
   verifies it against `runner_devices.public_key` and re-checks `revoked_at`
   for open connections. The full message catalogue, lease and fencing rules are
   in [the runner protocol](runner-protocol.md).

The private key never leaves the machine and is never sent anywhere. Nothing the
pairing flow returns is a bearer token: the later gateway handshake is the
signature above, so there is nothing to keep besides the key and the three
values the poll returned. Revocation (from the dashboard's **Runners** page) is
immediate — the device is disconnected, receives no more work, and disappears
from the list; pairing again is from the machine itself.

**Against a deployment**: pairing only completes when the API has a gateway to hand out
(`RUNNER_GATEWAY_URL`); with it empty the runner stops with "approved, but the service has not
published an address for runners to connect to yet" (`pairing.ErrNoGateway`). A release runner accepts only a `wss://` gateway on `reelbolt.ai` or a subdomain.
With `COMPUTE_MODE=Local` a paired runner receives no workflow jobs (folder sync still works).
The full requirement list and the read-only probe are in
an operator document.

## Computer folder sync (the Folders feature)

`reelbolt-runner`, and the desktop app that embeds it, can copy folders on this machine into ReelBolt
projects — one way, live, **while the app is running**, not only during a render. This is the feature that
makes iCloud Drive work at all: Apple offers no API for a server to read it, but iCloud for Windows exposes
iCloud Drive as an ordinary folder, so it syncs like any other. It needs no container runtime and no gateway
connection: it talks only to the Inference API over signed HTTPS (`internal/deviceapi`), so it works whether
or not the compute fabric is deployed.

- **Windows and Linux only**, the same scope as the rest of the runner (D26). There is no macOS desktop app.
- **The engine** is `internal/foldersync`: per link, an fsnotify watch per directory, events debounced for
  2 s, a file uploaded only after its size and mtime have been stable for 2 s, a local hash index so an
  unchanged file is never hashed twice, and a full manifest reconciliation at start, after a wake or a
  reconnect, and every 15 minutes as a safety net. A folder needing more than 20,000 directory watches falls
  back to a rescan every 2 minutes and says so. The rules, the mass-delete guard, the device API and the
  storage limits are in [local-folder-sync.md](local-folder-sync.md); the person-facing guide is
  [user-guide/local-folder-sync.md](user-guide/projects/local-folder-sync.md).
- **What leaves the machine is only the files in the folder you chose.** A signed request carries the
  project, the target folder, the file's folder-relative path, its size and its SHA-256; the file's bytes go
  straight to object storage through presigned URLs, never through the app's own socket. The local absolute
  path is sent only as the link's display string, and files outside the chosen folder are never read. A file
  that is online-only is skipped rather than opened, because opening it would make the OS download it.
- **The unit of work is a link**, created from the machine (the folder picker is local). A link can be
  paused and resumed, and removing it keeps everything already copied.
- **How to stop it.** Remove the link (the app's **Folders → Remove**, or
  `reelbolt-runner folders remove <linkId>`); the files already copied stay in the project. Pause it
  (`reelbolt-runner folders pause <linkId>`) to stop copying without unlinking. Stopping the process stops
  folder sync entirely — nothing syncs while the app is closed, which is why the dashboard shows a computer
  as offline. A machine paired before folder sync existed has no API origin and refuses to guess one:
  `reelbolt-runner config set-api <url>` sets it, or pair the machine again.
- **Command line**: `reelbolt-runner folders list|add|remove|pause|resume`, plus `config set-api|show`.
  `run` starts the sync engine beside the job loop, so a headless machine syncs without a window. The app's
  **Folders** screen and `folders list` read the same published status document, so they cannot disagree.

## The governor: schedules, pause and per-job limits

`internal/governor` decides when this machine does **not** work. It is three
things: the `[schedule]` policy, an explicit pause, and the `[limits]` a job
runs under.

**The one invariant: the governor blocks work only for a reason it can evaluate
right now.** An unreadable control file, an unparsable schedule entry, an idle
probe this machine cannot answer, a power source it cannot see — all resolve to
*allowed, degraded*, and the verdict says it was degraded. A runner that
silently stops earning because a probe broke is a worse failure than one that
works while its user is at the keyboard: the second is visible in `status`, the
first is not. Configuration is the deliberate exception: a `runner.json` the
daemon cannot read or validate is refused **at load time** with a message naming
the field. And a pause never outlives the process that honoured it: restart
resumes.

`[schedule]` gates (all independent; `mode` is `always`, `idle`, `ac` or
`windows`, with aliases like `only-when-idle` accepted; an end before its start
wraps past midnight):

| Gate | Verdict when blocked | Source of the machine state |
| --- | --- | --- |
| `idle` — the user has been away `idleMinutes` (default 5) | `user-active` | `GetLastInputInfo` on Windows; logind's system idle hint on Linux, which is **best-effort** (a session that never sets it reports "unknown", which allows work and says so) |
| `onBattery` (`pause`) | `on-battery` | `GetSystemPowerStatus` on Windows; `/sys/class/power_supply` on Linux |
| `windows` — `{days, start, end}` | `outside-window` | the wall clock |

D26 scopes the runner to **Windows x64 and Linux x64/arm64; there is no macOS
build**, and no macOS power API anywhere in this package. A machine the schedule
has stopped does not merely refuse work: it **drops its connection to the
gateway entirely** — it is not holding a socket, a heartbeat timer and a lease
while it is idle.

**Pause and resume.** `reelbolt-runner pause` writes one small request document;
the daemon reads it within a second, applies it, and deletes it. `resume` does
the same in reverse, and both wait for the running daemon to acknowledge before
printing anything. `reelbolt-runner status` names the reason a machine is not
working, and `resume` clears an explicit hold without editing a file. Triggers:

- **Local**: the two commands, or `Daemon.Pause`/`Daemon.Resume` in-process —
  what the desktop tray uses.
- **Remote**: the gateway's `drain` message. The runner reads its reason:
  `paused_by_user` pauses, `resumed_by_user` resumes, and **any other reason is
  ignored**, so a gateway deploy or shutdown can never park a machine. (Nothing
  on the gateway side sends those reasons yet; the dashboard button is a later
  WP and needs no protocol change.)

There is no way to pause so the current job finishes first: a pause requeues
whatever is in flight, which the gateway handles exactly as it handles any
other lost lease.

**`[limits]`** — `memory`, `cpus`, `pids`, `ffmpegThreads`, `priority`
(`below-normal`: `nice` on Unix, `BELOW_NORMAL_PRIORITY_CLASS` on Windows),
`diskQuotaBytes` — were the hard-coded numbers in the daemon; they are
configuration now, and no executor holds a literal. They land as `--memory` /
`--cpus` / `--pids-limit` on container jobs, as `nice`/`-threads N` on host
ffmpeg, and as the session's scratch quota. Values larger than the machine are
**clamped** (CPUs to the core count, memory to 80% of RAM) and the clamp is
logged and shown by `status` rather than refused; a value outside the
sane range (memory under 256 MiB, more than 64 CPUs, a process count under
16) is refused at load.

## Signed auto-update

`reelbolt-runner update --check` reports what the release channel offers;
`update --apply` downloads, verifies and installs; `update --rollback` restores
the binary the last update replaced. The daemon does the same check in the
background every `updateIntervalHours` (default 6, max 168) when `autoUpdate` is
on (the default), and applies a release **only while no job is running** — the
in-flight count is the same gate at the moment of the swap as at the moment of
the check, so an update never interrupts a render. After a successful install the
daemon stops and a supervisor (systemd user unit, the Windows service) restarts
it on the new binary.

**An unreachable update host is not a failure of the runner.** The check fails,
the daemon logs it and keeps serving jobs; the next check retries. A signature
that does not verify is refused the same way: nothing is downloaded or
installed.

Nothing is installed that was not verified first, in this order: the compiled-in
Ed25519 release key over the manifest (two slots, `current` and `next`, staged
by `ldflags` at release-build time), the SHA-256 and exact byte length of the
artifact, the operating system's own signature on Windows, and a re-hash of the
staged file at the moment of the swap. A download is written to a `.part` file
and renamed into place only when complete and correct.

**This tree holds no release key.** Both slots are empty in every build made
from this source, so `status` and `doctor` report `keys none (this build
refuses to update)` and every update is refused **before the network is
touched**. The release pipeline (E10b) is what stamps a key and publishes the
manifest and its signature; `internal/update/README.md` is the manifest format,
signature, rollout rule and how to publish a release. The update settings in
`runner.json` are `updateChannel` (`stable`/`beta`), `autoUpdate`,
`updateIntervalHours` and `updateBaseUrl` (a host outside the product domain
needs `--dev-update-host` on a non-release build).

## Job names, job outcomes, the media cache and crash reports

Four things the desktop app shows come from the platform rather than from the
app itself, and each is narrower than it might look.

- **Job names.** Each `assign` may carry a `display` block (workspace, project
  and workflow names, plus the project, workflow and execution ids for a
  dashboard link). The runner gateway reads it once, best effort, within two
  seconds, and only from the job's own organization; when it cannot, the job is
  offered without names. It reaches the app as `agent.Job.Display` and then as
  the `WorkspaceName`/`ProjectName`/`WorkflowName`/`ProjectID`/`WorkflowID`/
  `ExecutionID` fields of every `daemon.JobEvent`. Nothing decides anything by
  it.
- **How a job ended here.** The worker agent tells the session how the runner's
  part of a job ended (`agent.EndAware`, called just before `Close`), and the
  daemon's finished event carries it as `Outcome`: `finished` (the engine closed
  the session normally), `cancelled`, `timed-out`, `failed` (it could not run on
  this machine, with one plain sentence in `OutcomeDetail`) or `interrupted` (a
  lost lease, a lost connection, a drain or the runner stopping). That is what
  this machine did, not whether the workflow run succeeded, which the dashboard
  says. A job the runner refused before any session existed produces no started
  event and so no finished one; it goes to the separate failure stream instead.
- **The downloaded-media cache.** A stage op's source file is kept in
  `os.UserCacheDir()/ReelBolt/media` (`sandbox/pkg/agent/srccache`), keyed by
  the object (the presigned URL without its signature) and revalidated with the
  object store by `If-None-Match` on the next stage of the same object, so a
  re-run downloads nothing it already has while the store still authorizes every
  read. Each hit is copied into the job's directory and its SHA-256 re-checked.
  `runner.json` sets the cap as `mediaCache.maxBytes`: absent means 10 GiB, 0
  turns it off, anything else must be at least 256 MiB. Least-recently-used
  entries are evicted down to the cap. The cloud `reelbolt-worker` does not use
  it.
- **Crash reports.** With the person's consent only, the app posts
  `reelbolt.telemetry/1` documents to the runner gateway's public
  `POST /telemetry/v1/reports` (reached through nginx on the runner hostname).
  The gateway re-validates each against the runner's own allowlist
  (`sandbox/pkg/telemetrywire`), rate limits per address, writes one
  `telemetry_report` log line, counts it in
  `reelbolt_runner_gateway_telemetry_reports_total{kind}` and stores nothing.
  Release builds are stamped with
  `https://reelbolt.ai/runner/telemetry/v1/reports` by
  `reelbolt-manifest ldflags`; `REELBOLT_TELEMETRY_ENDPOINT_STAMP` overrides it,
  and `none` stamps nothing.

Unpairing from the app also removes the computer on the server
(`DELETE /api/v1/device/me`, described in
[local-folder-sync.md](local-folder-sync.md)), exactly as removing it from the
dashboard would.

## Release status and what E10b must supply

The desktop shell is built and cross-compiled (Windows x64, Linux x64/arm64 —
the latter two are **compile-and-vet proofs, not executions**; nothing in this
repository runs a Windows binary). What is true of the desktop product today,
stated plainly:

- **No signed installer exists.** There is no release key in this tree, so no
  signed artifact has ever been produced. The marketing site's `/runner` page
  therefore ships in its "not available yet" state: its download list is driven
  by `site/lib/runner-downloads`'s `runnerArtifacts`, which is empty, and no
  build can advertise a download that does not exist.
- **The signed auto-update refuses everything** for the same reason: an empty
  trust root is `ErrNoTrustedKey`, and the refusal happens before the network
  is touched.
- **No desktop session has been started in this project.** The tray is built
  and cross-compiled; no icon has been displayed. `runner/TESTING.md` is the
  human script — tray states, the window, the login item, the no-listening-
  sockets check — that a release build runs against a real session on Windows 11
  and Ubuntu 24.04.
- **The cloud pool is not in service.** The `reelbolt-worker` binary exists
  (`sandbox/cmd/reelbolt-worker`, with the out-of-band `enroll` subcommand) and
  `sandbox/pkg/poolwaker/cloud-init.yaml` is the fragment that runs it, but **no
  running deployment enrolls `CloudPool` `runner_devices` rows**, so a pool job
  stays `Queued`. See [cloud-worker.md](cloud-worker.md) for the enroll command
  and what a provider launch must supply.

So the criteria that need a real pipeline, key or account are: a stamped release
key (E10b), a published signed manifest per channel (E10b), a real desktop
session for the human test script, and a cloud provider with credentials for the
pool (F12). Nothing in this tree can produce those; none of them is faked here.

## Building and testing the tree

```sh
# The default suite needs no container runtime and no container.
docker run --rm -v "$PWD:/src" -v reelforge-go-modcache:/go/pkg/mod \
  -w /src/runner -e GOPROXY=off golang:1.26-alpine \
  sh -c 'apk add --no-cache build-base; gofmt -l .; go vet ./...; go test ./... -race -timeout=15m'
```

The mount is the **repository root**, not `runner/` alone: `runner/go.mod` has
`replace .../sandbox => ../sandbox`, so mounting `runner/` fails with
"replacement directory ../sandbox does not exist". The signed auto-update runs
entirely offline against local test servers (`go test ./internal/update -race`).

The real-container tests — a real workspace container from the runtime image, a
real Remotion CLI run inside it, a real ffmpeg encode — are a human's:

```sh
REELBOLT_RUNNER_DOCKER_TESTS=1 go test ./internal/daemon -run Real -v -timeout=30m
REELBOLT_RUNNER_DOCKER_TESTS=1 REELBOLT_TEST_REMOTION_RENDER=1 go test ./internal/daemon -run Real -v -timeout=45m
```

The desktop module is built separately (GUI toolchain required — see its README);
cross-compilation of the daemon for all three D26 targets is in `runner/TESTING.md`
and is a compile-and-vet proof, not an execution. The `TestRunnerHandshakesWithTheGateway`
test is known-intermittent; re-run it before blaming it.
