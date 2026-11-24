# reelbolt-runner-desktop

The ReelBolt desktop runner: the daemon from `runner/` with a system tray and a window around it.
This directory is **its own Go module**, and that is the load-bearing decision here — see "Why a
separate module" below.

Almost nothing is in this directory. What the tray says, which rows its menu has, what a button
does and which state a machine is in all live in `runner/internal/ui`, where they are plain Go and
are unit-tested. This module is the Wails host: it builds the application, the tray and the window
and mounts the shell's local API. A tray whose contents were decided here could disagree with the
window, which is the one thing this design exists to prevent.

## Why a separate module

Wails v3 is a **cgo** build. On Linux it needs `gtk4` and `webkitgtk-6.0` through pkg-config; on
Windows it needs **nothing**: it builds with `CGO_ENABLED=0` (see "Windows" below). A Go module's dependencies are resolved as a whole, so adding
`github.com/wailsapp/wails/v3` to `runner/go.mod` would make `go vet ./...` in `runner/` fail on
any machine without a GUI stack — which is every CI runner this repository has, and the machine this
WP was written on.

A nested module is excluded from the parent's `./...`, so:

| Command | where | needs a GUI toolchain |
| --- | --- | --- |
| `go vet ./...` / `go test ./... -race` in `runner/` | the daemon, the governor, the shell | no |
| `go build ./...` in this directory | the tray and the window | yes |

Pinned: **`github.com/wailsapp/wails/v3 v3.0.0-beta.28`** — decision D17's "Wails v3 pinned to an
exact beta". It was still a beta on 2026-10-05, the day D17 was approved, and it still is.

## Building

```sh
node runner/cmd/reelbolt-runner-desktop/build.mjs    # vite build -> runner/frontend/dist -> ./dist
cd runner/cmd/reelbolt-runner-desktop
go build -o reelbolt-runner-desktop .
```

On Debian/Ubuntu, install `build-essential pkg-config libgtk-4-dev libwebkitgtk-6.0-dev` first. On
Alpine (which is what the container below uses): `build-base pkgconf gtk4.0-dev webkit2gtk-6.0-dev`.

A compile-and-vet check that needs no desktop, used while writing this and safe to re-run:

```sh
docker run --rm -v "$PWD:/src" -v reelforge-go-modcache:/go/pkg/mod \
  -w /src/runner/cmd/reelbolt-runner-desktop -e GOFLAGS= golang:1.26-alpine sh -c '
    apk add --no-cache build-base pkgconf gtk4.0-dev webkit2gtk-6.0-dev || exit 1
    gofmt -l . && go vet ./... && go build -o /tmp/desktop . && ls -la /tmp/desktop'
```

`dist/` is not committed: only a `.gitkeep`, so `go:embed all:dist` always compiles. A binary built
without it still runs and the window says what to run.

**The Alpine recipe above is a check, never a build.** It answers "does this compile and vet", quickly,
without a desktop. Do not ship what it produces: Alpine links musl and Alpine's webview, so the binary
will not run on a Debian/Ubuntu machine at all. For an artifact someone installs, use

```sh
runner/build/desktop-local.sh          # daemon + tray, .deb + tarball, built on Ubuntu 24.04
```

which compiles inside a pinned `ubuntu:24.04` container (no GUI toolchain or root needed on the host)
and packages the result. See `runner/build/README.md` → "Building the Linux desktop app locally".

**Runtime dependencies.** What the *build* needs is `build-essential pkg-config libgtk-4-dev
libwebkitgtk-6.0-dev`. What the resulting *binary* needs at runtime is `libgtk-4-1` and
`libwebkitgtk-6.0-4`: Wails v3 beta.28 uses webkitgtk-**6.0**, not webkit2gtk-4.1, and it links no
appindicator. `runner/build/linux/nfpm.yaml` used to name the wrong pair.


## Two build modes that must never be used

Wails opens a TCP listener in exactly two places, and both would break the runner's "no listening
sockets" property:

- **`-tags server`** turns the app into an HTTP server with no native window
  (`application_server.go` calls `net.Listen`).
- **The built-in MCP server** (`mcp_enabled.go`) does the same.

The normal desktop build calls neither. `runner/TESTING.md` has the `lsof`/`netstat` check that
catches a regression here.

## How the window talks to the shell

`application.ServiceOptions.Route` mounts an `http.Handler` on the **internal asset server**, which
serves the webview's requests *inside the process* — it is not a port. `runner/internal/ui`'s
`Handler` implements four routes:

| Route | Method | What it is |
| --- | --- | --- |
| `/view` | GET | the whole document the tray and the window render |
| `/menu` | GET | the tray menu for the current view, as data |
| `/do` | POST | one action: the same `App.Do` switch the tray uses |
| `/settings` | POST | a patch of `runner.json` (slots, channel, auto-update, login item) |
| `/pairing` | GET/POST/DELETE | the first-run wizard's device-flow state |
| `/telemetry/preview` | GET | the exact crash and job-failure documents E11 would send, built from this machine |

`apiService.ServeHTTP` normalises a leading `/api`, because whether the asset server strips the
`Route` prefix is an implementation detail this binary must not depend on.

## Windows

Built on Linux by `runner/build/windows/build-binaries.sh` (inside the pinned container from
`runner/build/windows/run-in-container.sh`), not on a Windows runner. Known drift found on the way:
this module's `go.mod` says `go 1.25.0` but requires the runner module (`go 1.26.0`) through
`replace`, so a plain `go build` stops with "updates to go.mod needed"; the build script works
around it with a scratch modfile. The fix is to bump `go.mod` to `go 1.26.0` and `go mod tidy`.

## What was verified, and what was not

Verified on the machine this WP was written on (headless Linux, no desktop):

- the module **builds** (a 17.8 MB binary) and **vets** clean against the pinned Wails beta, with
  `gtk4` and `webkitgtk-6.0` installed;
- `gofmt -l .` is empty;
- `GOOS=windows GOARCH=amd64 go build` of the **runner module** succeeds, so the Windows-only files
  compile;
- the Wails host itself cross-builds for Windows on Linux with `CGO_ENABLED=0` and
  `-ldflags "-H=windowsgui"`: `file` reports `PE32+ executable for MS Windows (GUI), x86-64`
  (14.8 MB, runner-distribution T-002, 2026-10-26). Wails v3 beta.28 is pure Go on Windows
  (WebView2 through syscall loaders), so MinGW is not needed for Go; it is only used for the bundled
  ffmpeg. `runner/build/windows/build-binaries.sh` is the build.

Not verified, and stated rather than implied:

- **the tray, the window and the login item were never displayed or clicked.** No desktop session
  was started;
- **no Windows binary was ever run**;
- **no real gateway was involved**: pairing is unit-tested against the client's error mapping;
- the exact shape of Wails' `Route` mounting, and whether a click on a tray menu item arrives on
  the right goroutine, are read from the pinned source rather than observed.

`runner/TESTING.md` is the manual script that closes those gaps on a real desktop.
