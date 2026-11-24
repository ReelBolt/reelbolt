# reelbolt-runner

The headless ReelBolt runner: a desktop or cloud machine that renders Remotion
compositions and runs ffmpeg for the ReelBolt engine.

Two binaries live here. `reelbolt-runner` is the headless daemon (`cmd/reelbolt-runner`), and
`reelbolt-runner-desktop` is the Wails tray shell (`cmd/reelbolt-runner-desktop`) whose whole
behaviour is the `internal/ui` package. **The desktop binary is a separate Go module**, because
Wails is a cgo build and a module's dependencies resolve as a whole: requiring it from this
`go.mod` would stop `go vet ./...` working on any machine without a GUI toolchain. Everything in
this module builds and tests without one. See [`TESTING.md`](TESTING.md) for what is verified and
what needs a human.

It dials out to the runner gateway and never listens on a port, holds no
provider credential, and receives work only for the workspace it was paired
into. The protocol itself lives in `../sandbox/pkg/agent`; this module is the
daemon around it: configuration, the device key, the runtime image, job
scratch space, the resource governor, and the stop sequence.

## Commands

```
reelbolt-runner pair   [flags]   pair this machine into a ReelBolt workspace
reelbolt-runner run    [flags]   serve jobs until stopped (SIGTERM drains first)
reelbolt-runner status [flags]   show the local configuration and device state
reelbolt-runner pause  [flags]   stop taking new work until resumed
reelbolt-runner resume [flags]   take work again after a pause
reelbolt-runner doctor [flags]   probe the machine and print its capabilities
reelbolt-runner unpair [flags]   forget the device identity
reelbolt-runner update [flags]   check for, verify and install a signed release
reelbolt-runner version
```

A second binary, built from its own module under `cmd/reelbolt-runner-desktop`, is the desktop
shell: the same daemon in-process, with a tray and a window.

```
reelbolt-runner-desktop [--background] [--window] [--assets <dir>]
```

`doctor` is the one to run first: it checks ffmpeg, the container runtime, the
pinned runtime image and the container's egress posture, and prints the exact
capability document the gateway will route on.

## Where things live

| What | Where |
| --- | --- |
| Configuration | `os.UserConfigDir()/ReelBolt/runner.json` (0600) |
| Device key | the OS keyring, or `os.UserConfigDir()/ReelBolt/device.key` (0600) with a warning |
| Job scratch | `os.UserCacheDir()/ReelBolt/jobs/<jobId>`, deleted when the job ends |
| Workspaces | `os.UserCacheDir()/ReelBolt/workspaces/<id>`, bind-mounted into the workspace container |
| Logs | stderr, plus the file named by `logFile` (and `--log-file`) |
| Pause / resume requests | `os.UserCacheDir()/ReelBolt/governor.json`, written by `pause`/`resume` and consumed (then deleted) by the running daemon |
| Live runner state | `os.UserCacheDir()/ReelBolt/runner.live.json`, written by the daemon while it runs and removed when it stops |

The configuration file is a text file a user can edit, so it is validated on
every load, and the gateway it names must match the compiled-in allowlist
(`*.reelbolt.ai`). A development gateway is accepted only when it is named on
the command line **and** the binary is not a release build (`-tags release`).

## The runtime image

Jobs run in `sandbox/runtime-image.lock`'s pinned digest when the release
pipeline has published one; otherwise the reference comes from
`--runtime-image`, `runner.json` or `REELBOLT_RUNTIME_IMAGE`. Only a pinned digest
is advertised in `capabilities.runtimeImageDigest`, and the gateway refuses a job
whose `requires.runtimeImageDigest` differs - so a runner that is not running the
image it advertised receives no Remotion work instead of running the wrong one.

`runtimeimage_test.go` fails if the vendored copy in
`internal/runtimeimage/runtime-image.lock` drifts from the canonical file in the
sandbox module. (It is vendored because `go:embed` cannot reach outside its own
module.)

## The governor
## Auto-update

`reelbolt-runner update --check` reports what the release channel offers;
`update --apply` downloads, verifies and installs it; `update --rollback`
restores the binary the last update replaced. The daemon does the same check in
the background every `updateIntervalHours` (default 6) when `autoUpdate` is on,
which it is by default, and applies a release **only while no job is running**.
An update never interrupts a render: the count of jobs in flight is taken by the
job factory and released when the agent closes the session, and the updater is
handed the same gate so the refusal holds at the moment of the swap and not only
at the moment of the check.

After a successful install the daemon stops, so a supervisor (systemd, the
Windows service) starts it again on the new binary. Run it under one if you want
updates to complete unattended.

Nothing is installed that was not verified first, in this order: the compiled-in
Ed25519 release key over the manifest, the SHA-256 and byte length of the
artifact, the operating system's own signature on Windows, and a re-hash of the
staged file at the moment of the swap. A download is written to a `.part` file
and renamed into place only when it is complete and correct, so a partial
download is never installed. The previous binary is kept as
`<binary>.prev` and `update --rollback` restores it.

**An unreachable update host is not a failure of the runner.** The check fails,
the daemon logs it and keeps serving jobs, and the next check retries. A
signature that does not verify is refused the same way: nothing is downloaded or
installed, and the runner keeps running the build it has.

This build has **no release key compiled in** — the repository holds none — so
`status` and `doctor` report `keys none (this build refuses to update)` and every
update is refused before the network is touched. That is the honest state of the
tree until E10b stamps a key; see `internal/update/README.md` for the manifest
format, the signature, the rollout rule and how to publish a release.

### Update settings in runner.json

| Key | Default | Meaning |
| --- | --- | --- |
| `updateChannel` | `stable` | `stable` or `beta`. |
| `autoUpdate` | `true` | Set it to `false` to stop the background check. `update --check`/`--apply` still work. |
| `updateIntervalHours` | `6` | How often the background check runs (max 168). |
| `updateBaseUrl` | the product download host | Override for a staging bucket. A host outside the product domain needs `--dev-update-host` on a non-release build. |
| `devUpdateHosts` | empty | Extra update hosts a non-release build may use. |


## Resource posture

`internal/governor` decides when this machine does **not** work. It is three
things: the `[schedule]` policy, an explicit pause, and the `[limits]` a job
runs under.

### The one invariant

**The governor blocks work only for a reason it can evaluate right now.** An
unreadable control file, an unparsable schedule entry, an idle probe this
machine cannot answer or a power source it cannot see all resolve to *allowed*,
and the verdict says it was degraded. A runner that silently stops earning
because a probe broke is a worse failure than one that works while its user is
at the keyboard - the second is visible in `status`, the first is not.

Configuration is the exception, and deliberately so: a `runner.json` the daemon
cannot read or validate is refused **at load time** with a message naming the
field, before anything can be paused. And a pause never outlives the process
that honoured it.

### `[schedule]`

```json
"schedule": {
  "mode": "only-when-idle",
  "idleMinutes": 10,
  "windows": [{"days": ["mon", "tue", "wed", "thu", "fri"], "start": "18:00", "end": "08:00"}],
  "onBattery": "pause"
}
```

| key | values | meaning |
| --- | --- | --- |
| `mode` | `always` (default), `idle`, `ac`, `windows` | aliases such as `only-when-idle` and `only-on-ac-power` are accepted |
| `idleMinutes` | minutes, default 5 | how long the user must be away in `idle` mode |
| `windows` | list of `{days, start, end}` | an independent gate in **every** mode; an end before its start wraps past midnight and belongs to the day it starts on |
| `onBattery` | `allow` (default), `pause` | applies in every mode |

Where the machine's state comes from: `GetLastInputInfo` and
`GetSystemPowerStatus` on Windows, logind's system idle hint and
`/sys/class/power_supply` on Linux. **The Linux idle hint is best-effort**: a
desktop session sets it (typically when the screen blanks), so it is a lower
bound on input idleness, and a machine whose session never sets it reports
"unknown" - which allows work and says so. D26 scopes the runner to Windows and
Linux; there is no macOS implementation and no macOS power API anywhere in this
package.

A machine the schedule has stopped does not merely refuse work: it drops its
connection to the gateway entirely, so it is not holding a socket, a heartbeat
timer and a lease while it is idle.

### Pause and resume

`reelbolt-runner pause` writes one small request document next to the runner's
other state; the daemon reads it within a second, applies it, and deletes it.
`reelbolt-runner resume` does the same in reverse, and both wait for the running
daemon to acknowledge before printing anything - "I asked" and "it happened"
are different claims.

* **Local trigger** - the two commands above, or `Daemon.Pause`/`Daemon.Resume`
  in-process, which is what the desktop shell's tray uses.
* **Remote trigger** - the gateway's `drain` message. The runner reads its
  reason: `paused_by_user` pauses, `resumed_by_user` resumes, and **any other
  reason is ignored** so a gateway deploy or shutdown can never park a machine.
  A remote pause keeps the connection (the worker agent already stops accepting
  assignments when the gateway says so) because the gateway has to stay able to
  resume it. Nothing on the gateway side sends those reasons yet; the dashboard
  button is a later WP, and no protocol change is needed for it.
* **Also: restarting the runner resumes it.** The pause lives in memory for one
  run, and any request left behind by an earlier process is deleted at startup.

There is no way to pause a runner so that it finishes its current job first:
`agent.Drain` tears sessions down and the worker library exposes no
assignment-level reject hook. A pause therefore requeues whatever is in flight,
which the gateway handles exactly as it handles any other lost lease.

**If the governor has paused your runner and you disagree:** run
`reelbolt-runner status`. It names the reason. If it is an explicit pause,
`reelbolt-runner resume` clears it - one command, whoever placed it. If it is
the schedule, the same line names the gate and when it next changes
("work resumes Mon 09:00"); change or remove the `[schedule]` block, or run
`reelbolt-runner resume`, which is a no-op against a schedule gate and will say
so. If nothing responds at all, kill the process: pause is never persisted, so
a restart always comes back working.

### `[limits]`

```json
"limits": {
  "memory": "4g",
  "cpus": 2,
  "pids": 512,
  "ffmpegThreads": 2,
  "priority": "below-normal",
  "diskQuotaBytes": "8g"
}
```

These were the hard-coded numbers in `internal/daemon/executors.go`; they are
configuration now, and no executor holds a literal. `memory` and `diskQuotaBytes`
accept a number of bytes or a string with a unit (binary, so `4g` is 4 GiB).
Values larger than the machine are clamped (CPUs to the core count, memory to
80% of RAM) and the clamp is logged and shown by `status`, rather than refused;
a value that would wedge the machine outright (a 1 MiB cap, 4096 cores) is
refused at load.

Where each one lands:

| limit | container jobs | host ffmpeg |
| --- | --- | --- |
| `memory`, `cpus`, `pids` | `--memory`, `--cpus`, `--pids-limit` | - |
| `priority` | `--cpu-shares` (a container has no nice level) | `nice` (Unix) / `BELOW_NORMAL_PRIORITY_CLASS` (Windows) |
| `ffmpegThreads` | via the cgroup (`--cpus`) | `-threads N` in the ffmpeg argv |
| `diskQuotaBytes` | the session's scratch quota | the session's scratch quota |

The media package's container runner sets no resource flags of its own and its
host runner sets neither a thread count nor a priority, so both are applied at
the daemon's own CLI and `media.CommandRunner` seams.

## Running as a service

SIGTERM and SIGINT both drain: the runner tells the gateway to stop offering
work, waits `drainSeconds` (default 5), then sends `bye` and closes. A second
signal skips the wait. Exit codes: `0` clean stop, `1` failure, `2` usage,
`3` not paired.

systemd (Linux), as a user unit so the device key's keyring is reachable:

```ini
[Unit]
Description=ReelBolt runner
After=network-online.target docker.service

[Service]
ExecStart=/usr/local/bin/reelbolt-runner run --headless
Restart=on-failure
RestartSec=10

[Install]
WantedBy=default.target
```

Windows: register the binary as a service with `sc.exe create` pointing at
`reelbolt-runner run --headless`; the installer in E10b does this properly, and the
binary handles the service stop signal the same way it handles SIGTERM. On a
machine where the service cannot reach the user's keyring, the 0600 key file in
the table above is used and the fallback is logged.

## Tests

```sh
# The default suite needs no container runtime and no container.
go test ./... -race -timeout=15m

# The signed auto-update runs entirely offline against local test servers.
go test ./internal/update -race -v

# The real-container tests: a real workspace container from the runtime image,
# a real Remotion CLI run inside it, and a real ffmpeg encode.
REELBOLT_RUNNER_DOCKER_TESTS=1 go test ./internal/daemon -run Real -v -timeout=30m

# ... plus a full Remotion render (bundles the composition and drives Chrome).
REELBOLT_RUNNER_DOCKER_TESTS=1 REELBOLT_TEST_REMOTION_RENDER=1 go test ./internal/daemon -run Real -v -timeout=45m
```

The `runner` module `replace`s the sandbox module to `../sandbox`, so it builds
with the repository checked out, not from the module alone: mount the
repository root when building it in a container.