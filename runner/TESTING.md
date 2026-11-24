# Testing the ReelBolt runner

Two kinds of check live here, and they are kept apart on purpose:

- **What CI runs.** `gofmt -l .`, `go vet ./...`, `go test ./... -race` in `runner/`, and
  `npm run build` + `npm test` in `runner/frontend`. Everything in this half is a Go test or a
  TypeScript test and runs on a machine with no desktop and no container runtime.
- **What a human has to run.** The tray, the window, the login item and the "no listening sockets"
  criterion need a real session with a real system tray. This document is that script.

The desktop binary is a **separate Go module** (`runner/cmd/reelbolt-runner-desktop`) because Wails
v3 is a cgo build. Building it needs a GUI toolchain; building everything else does not. See that
module's README for the exact commands.

## ## What CI proves

```sh
docker run --rm -v "$PWD:/src" -v reelforge-go-modcache:/go/pkg/mod -w /src/runner \
  -e GOPROXY=off golang:1.26-alpine sh -c 'apk add --no-cache build-base; gofmt -l .; go vet ./...; go test ./... -race -timeout=15m'
```

Note the mount: the runner module has a `replace` to `../sandbox`, so the volume must be the
repository root and `-w` must be `/src/runner`. Mounting `runner/` alone fails with
"replacement directory ../sandbox does not exist".

Cross-compilation, which is what proves the Windows-only files compile at all:

```sh
docker run --rm -v "$PWD:/src" -v reelforge-go-modcache:/go/pkg/mod -w /src/runner -e GOPROXY=off \
  golang:1.26-alpine sh -c 'apk add --no-cache build-base; \
    GOOS=windows GOARCH=amd64 go build ./... && GOOS=windows GOARCH=amd64 go vet ./... && \
    GOOS=linux   GOARCH=arm64 go build ./...'
```

That is a **compile and a vet, not an execution**. Nothing in this repository runs a Windows binary.

## ## What a human must run

Targets are Windows 11 and Ubuntu 24.04 (GNOME or KDE). There is no macOS build: decision D26
deferred the whole macOS runner, so every macOS step in the plan — the LaunchAgent, the template
tray icons, the manual test — is out of scope rather than stubbed.

Build and install the shell:

```sh
node runner/cmd/reelbolt-runner-desktop/build.mjs     # builds the window into the module
cd runner/cmd/reelbolt-runner-desktop && go build -o reelbolt-runner-desktop .
```

On Linux the build needs `gtk4` and `webkitgtk-6.0` development headers (`libgtk-4-dev`,
`libwebkitgtk-6.0-dev` on Debian/Ubuntu); on Windows it needs a MinGW-w64 toolchain. E10b runs it
on native runners for that reason.

### ## The tray, one state at a time

Each row is a thing to break on purpose and the sentence you should see. Run
`reelbolt-runner status` next to it for every row: **the tray and status must never disagree**, and
that is the single most important thing this script checks.

| What to do | Tray says | Why it matters |
| --- | --- | --- |
| Fresh machine, nothing paired | "Not paired" / "Pair this machine" | The menu offers Pairing and nothing else clickable except the window and Quit |
| Pair it, then quit the shell | The tray is gone; the window's header says "Runner not running" | A tray that outlives its runner is a tray that lies |
| Start it again with a paired device | "Ready for work" | Pause is offered, Resume is not |
| `reelbolt-runner pause --reason "testing"` | "Paused", with your reason on the second menu line, and Resume | The reason you typed is shown verbatim. Resume clears it, and no file is edited |
| Press Resume in the tray | Back to "Ready for work", or a sentence naming what is *still* blocking | "Resumed and still not working" is the case people hit; the message must say why |
| Revoke the device in the dashboard | "This machine was unpaired" / "Pair again" | The runner stops. The device key is kept, so Pair again reuses it — and the old device record stays revoked |
| Stop Docker/Podman, restart the shell | "This machine cannot run any job" | Not a silent green idle. The reason names what the probe tried |
| Set `schedule.mode` to `windows` with a window that excludes now | "Not taking work", with the schedule as the reason and "Open runner.json" | Resume must NOT be offered here: `governor.Resume` clears a hold, not a schedule gate |

Pause from the dashboard (the gateway's own hold) should show "Paused by ReelBolt" with **no**
Resume button: only the gateway can release its own hold, and the runner keeps the connection open
to hear that.

### ## The window

1. **Set up** — the runtime chips match `reelbolt-runner doctor`'s capability list. With no
   container runtime the panel is red and names what was tried. There is no pull percentage; the
   screen says why.
2. **Jobs** — run one job and watch it appear with a live duration and, when the executor reports
   one, a percentage. When it ends the row says "ended", never "succeeded": the runner is not told
   the outcome, and the screen says so under the table.
3. **Settings** — change "Jobs at once", reopen the window, and confirm the value persisted to
   `runner.json` and that `reelbolt-runner status` agrees. "Clear leftovers" is refused while a job
   is running.
4. **Device** — Unpair stops the runner and the header switches to "Not paired".

### ## No listening sockets

The shell serves its window over the webview's internal request path, not over a port. With the
shell running and a job in flight:

```sh
lsof -i -P -n | grep reelbolt-runner-desktop      # Linux: expect no output
netstat -ano | findstr reelbolt-runner-desktop    # Windows: expect no output
ss -tlnp | grep -i reelbolt                       # Linux: expect no output
```

The outbound connection to the gateway (`wss://reelbolt.ai/runner/v1/connect`) will appear in
`netstat -an`; that is the one socket the runner is supposed to have.

**Two build modes would break this and must never be used:** Wails' `server` build tag
(`go build -tags server`) starts an HTTP listener on purpose, and Wails' built-in MCP server
(`mcp_enabled.go`) starts another. Neither is used here. If a future change adds either, this
section is the check that catches it.

### ## Start at login

1. Fresh machine: the Settings switch is off.
2. Turn it on. Linux writes `${XDG_CONFIG_HOME:-~/.config}/autostart/reelbolt-runner.desktop`;
   Windows writes a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
   Both run the binary with `--background`, so logging in starts the tray and **no window**.
3. Log out and back in. The tray appears; no window opens.
4. Turn the switch off, log out and back in. Nothing starts. On Linux, `ls` the file to confirm it
   is gone.

Deleting the file (Linux) or the registry value (Windows) by hand is the same thing as the switch,
and the switch reads the real state rather than caching it.

### ## Crash reporting and diagnostics (E11)

The switch is off on a fresh machine. A plain `go build` has no collector address
compiled in and sends nothing; a build made with `reelbolt-manifest ldflags` (the
release workflow, `build/local-release.sh`) or `build/desktop-local.sh` is stamped
with the runner gateway's `POST /telemetry/v1/reports` unless
`REELBOLT_TELEMETRY_ENDPOINT_STAMP=none`. What a human can check is the shape of
the promise.

1. Open **Settings → Privacy and diagnostics**. The switch reads **off**.
2. Expand **See exactly what would be sent**. Two documents appear, built from
   *this* machine: a crash and a job failure. Check by eye that neither names a
   file, a path, a project, a device, a workspace or an address — that is the
   promise, and the preview is the thing to check it against.
3. Turn the switch on. The screen must say that this build has no collector, so
   nothing can be sent, and the counter must stay at zero. A build stamped with a
   collector address says the other sentence; both come from the shell.
4. Quit and reopen the window. The switch is still on, and `runner.json` has a
   `telemetry` block with `enabled`, `askedAt` and `consentVersion`.
5. Delete the `telemetry` block from `runner.json`, reopen the window: the
   **Set up** tab asks the question again. Answer either way and it stops asking.
6. Press **Export diagnostics**. A zip is written under
   `<cache>/ReelBolt/diagnostics/`. Open it and read `README.txt`, then check
   `logs/` and `runner.json` for your own home directory or user name: they must
   read `~` and `<redacted>`.
7. `lsof -i -P` / `netstat` again while doing all of the above: still no listening
   socket. A switch that turned the runner into a server would be the worst
   possible bug in this feature, and this is the step that catches it.

A report is only ever produced by a crash or a job the runner could not start.
To exercise the crash path on a real desktop, the shell's guarded goroutines are
the probe, the runner and pairing; there is no supported way to trigger one from
the UI, and inventing one would be a worse idea than the test that covers it.

### ## GNOME needs an extension

GNOME removed its tray by default. On Ubuntu 24.04 install the AppIndicator extension and log out
and back in:

```sh
sudo apt install gnome-shell-extension-appindicator
gnome-extensions enable appindicatorsupport@rgcjonas.gmail.com
```

Without it the process runs and the window works, but there is no icon — which is exactly why this
is a documented step and not something to discover.

## ## What this work did NOT run

Stated plainly, because a green gate is not a green product:

- **No desktop session was started.** This WP was written on a headless Linux machine. The tray,
  the window and the login item were never displayed or clicked. What was verified is that the
  desktop binary **builds and vets** against Wails v3.0.0-beta.28 with gtk4 and webkitgtk-6.0
  installed, and that every decision behind them (states, menu, remedies, actions) is a Go test.
- **No Windows binary was executed.** `GOOS=windows go build` and `go vet` pass; nothing more.
- **No real gateway was involved.** Pairing is tested against the pairing client's error mapping, not
  against a live `/api/v1/runners` pair. E13's harness is the end-to-end test.
- **The downloaded-media cache was never exercised against a real object store.** It
  (`sandbox/pkg/agent/srccache`, capped by `mediaCache.maxBytes` in `runner.json`, default 10 GiB,
  0 = off) is tested against an in-process store that answers `If-None-Match` the way S3, R2 and
  Garage document; a re-run against the deployment's Garage or R2 is what confirms a real `304`.
- **No crash report has reached the deployed collector yet.** The collector exists (the runner
  gateway's public `POST /telemetry/v1/reports`, behind nginx on the runner hostname) and the
  stamped address is checked against a real build by `cmd/reelbolt-manifest`'s
  `TestLdflagsStampARealBuild`; the runner's payloads are decoded by the gateway's own validator in
  `internal/telemetry/wire_test.go`. Posting one from a desktop to a deployed gateway, and finding
  its `telemetry_report` log line, is the step that is left.
- **No desktop session was started**, so the telemetry switch, the payload preview and the export
  button were never clicked. Their logic is Go tests; the script above is what closes the gap.
