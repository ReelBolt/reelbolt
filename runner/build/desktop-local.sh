#!/bin/sh
# Build the ReelBolt desktop runner for Linux (Ubuntu x86-64) WITHOUT needing a
# GUI toolchain, root, or a Windows/macOS machine: the tray and window are
# compiled inside a pinned Ubuntu 24.04 container, which is the environment the
# release builds on and the oldest glibc we support.
#
#   runner/build/desktop-local.sh [options]
#
#     --version V    version stamp for the binaries and the package
#                    (default: v0.1.0-local)
#     --out DIR      where the artifacts land (default: runner/build/out)
#     --engine E     container engine: podman or docker (default: auto-detect)
#     --no-deb       skip the .deb and produce only the binaries and a tarball
#
# It produces, in --out:
#
#   reelbolt-runner                    the headless daemon (CLI: pair, run,
#                                      status, folders add|list|remove, doctor)
#   reelbolt-runner-desktop            the tray and the window
#   reelbolt-runner_<version>_amd64.deb  installable with apt (unless --no-deb)
#   reelbolt-runner-<version>-linux-amd64.tar.gz   the same files, archived
#
# CRASH REPORTS. The desktop app is stamped with the opt-in crash collector,
# https://reelbolt.ai/runner/telemetry/v1/reports, exactly as a release is;
# set REELBOLT_TELEMETRY_ENDPOINT_STAMP to another https address or to "none" to
# stamp nothing. REELBOLT_SERVICE_ORIGIN (default https://reelbolt.ai) is the
# one ReelBolt address the app pairs with and links to; the collector follows it. Nothing is ever sent unless the person turns
# crash reporting on in the app.
#
# WHAT THIS IS NOT. It is a local build: it carries no release public key, so
# the runner refuses every self-update by design (runner/internal/update), and
# nothing here is signed. Signed, self-updating artifacts come from
# .github/workflows/runner-release.yml, which also has to be told what each
# artifact contains (see "Building the Linux desktop app" in README.md).
#
# WHY A CONTAINER, and why UBUNTU and not Alpine. runner/cmd/reelbolt-runner-desktop
# is a cgo build: Wails v3 needs gtk4 and webkitgtk-6.0 through pkg-config, which
# a machine without a desktop stack will not have. The Alpine recipe in that
# directory's README is a fast COMPILE-AND-VET CHECK; an Alpine build links musl
# and the Alpine webview, so the binary it produces will not run on Ubuntu at
# all. A real Linux artifact has to be built on glibc.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
repo=$(CDPATH='' cd -- "$here/../.." && pwd)

version="v0.1.0-local"
out="$here/out"
engine=""
build_deb=yes

while [ $# -gt 0 ]; do
	case "$1" in
	--version)
		version="$2"
		shift 2
		;;
	--out)
		out="$2"
		shift 2
		;;
	--engine)
		engine="$2"
		shift 2
		;;
	--no-deb)
		build_deb=no
		shift
		;;
	-h | --help)
		sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
		exit 0
		;;
	*)
		echo "desktop-local.sh: unknown argument $1 (try --help)" >&2
		exit 2
		;;
	esac
done

if [ -z "$engine" ]; then
	if command -v podman >/dev/null 2>&1; then
		engine=podman
	elif command -v docker >/dev/null 2>&1; then
		engine=docker
	else
		echo "desktop-local.sh: neither podman nor docker is installed: the tray needs a GTK/webview toolchain this script provides through a container" >&2
		exit 1
	fi
fi
command -v "$engine" >/dev/null 2>&1 || {
	echo "desktop-local.sh: $engine is not on PATH" >&2
	exit 1
}

# The window's UI is a vite bundle the Go binary embeds (go:embed all:dist), so
# it has to exist before the container compiles anything. Node lives on the host
# because that is where the frontend's dependencies are installed.
command -v node >/dev/null 2>&1 || {
	echo "desktop-local.sh: node is required to build the window's UI bundle" >&2
	exit 1
}
if [ ! -d "$repo/runner/frontend/node_modules" ]; then
	echo "desktop-local.sh: installing the window's UI dependencies (npm ci)"
	(cd "$repo/runner/frontend" && NODE_ENV=development npm ci --include=dev --no-audit --no-fund)
fi
echo "desktop-local.sh: bundling the window's UI"
(cd "$repo" && NODE_ENV=development node runner/cmd/reelbolt-runner-desktop/build.mjs)
# build.mjs replaces that directory, which takes the tracked .gitkeep with it —
# and that file is what keeps `go:embed all:dist` compiling in a fresh checkout.
: >"$repo/runner/cmd/reelbolt-runner-desktop/dist/.gitkeep"

mkdir -p "$out"
out_abs=$(CDPATH='' cd -- "$out" && pwd)

# The package is self-describing: what this build is, and that it is not signed.
cat >"$out_abs/REELBOLT-LOCAL-BUILD.md" <<EOF
# ReelBolt runner — local build ($version)

Built by runner/build/desktop-local.sh from $(git -C "$repo" rev-parse --short HEAD 2>/dev/null || echo "an unknown revision")
on $(date -u +%Y-%m-%dT%H:%MZ), for Ubuntu 24.04 x86-64 and newer.

This is a LOCAL BUILD: unsigned, and carrying no release public key, so it
refuses every self-update by design.

## What is installed

| Path | What it is |
| --- | --- |
| /opt/reelbolt/runner/reelbolt-runner-desktop | the tray and window: pairing, folder sync, status, settings |
| /opt/reelbolt/runner/reelbolt-runner | the same daemon headless, with the CLI |
| /usr/bin/reelbolt-runner-desktop | wrapper that runs the app from /opt/reelbolt/runner |
| /usr/bin/reelbolt-runner | wrapper that runs the daemon |
| /usr/share/applications/reelbolt-runner.desktop | the applications-menu entry |

## Pairing, then folder sync

1. Launch **ReelBolt Runner** and pair it with the short code from the dashboard's
   Runners page.
2. **Folders → Add folder**: pick a directory, choose its project, confirm.
3. Headless machines:

       reelbolt-runner folders add ~/Footage --project <project-id>
       reelbolt-runner folders list
       reelbolt-runner run

Rendering additionaly needs a container runtime and ffmpeg:
\`sudo apt install docker.io ffmpeg\` (or podman).
EOF

# One container, one run: apt for the toolchain, then Go (Ubuntu 24.04 ships an
# older Go than these modules need), then both binaries, then nfpm for the .deb.
# The caches are named volumes so a second run is quick.
deb_version=$(printf '%s' "$version" | sed 's/^v//')

# The crash collector (see CRASH REPORTS above). The variable is a library one,
# so it is addressed by its full import path.
# The ReelBolt origin everything else hangs off (config.ServiceOrigin): pairing, the
# device API, dashboard links and help. One host serves all of it.
service_origin="${REELBOLT_SERVICE_ORIGIN:-https://reelbolt.ai}"
service_origin="${service_origin%/}"
telemetry_stamp="${REELBOLT_TELEMETRY_ENDPOINT_STAMP:-$service_origin/runner/telemetry/v1/reports}"
telemetry_ldflag="-X github.com/vecchiotom/reelbolt/runner/internal/config.ServiceOrigin=$service_origin"
if [ "$telemetry_stamp" != "none" ]; then
	telemetry_ldflag="$telemetry_ldflag -X github.com/vecchiotom/reelbolt/runner/internal/telemetry.CollectorEndpoint=$telemetry_stamp"
fi
deb_step='
if [ "'"$build_deb"'" = yes ]; then
	export PATH="/usr/local/go/bin:/go/bin:$PATH"
	go install github.com/goreleaser/nfpm/v2/cmd/nfpm@latest
	nfpm package --config /src/runner/build/linux/nfpm-local.yaml --packager deb --target /out
fi'


echo "desktop-local.sh: building for linux/amd64 in ubuntu:24.04 with $engine"
"$engine" run --rm \
	-v "$repo:/src:Z" \
	-v "reelforge-go-modcache:/go/pkg/mod" \
	-v "$out_abs:/out:Z" \
	-e DEBIAN_FRONTEND=noninteractive \
	-e GOPATH=/go \
	-e GOFLAGS= \
	-e VERSION="$version" \
	-e TELEMETRY_LDFLAG="$telemetry_ldflag" \
	-e DEB_VERSION="$deb_version" \
	-e ARCH=amd64 \
	-w /src/runner \
	docker.io/library/ubuntu:24.04 \
	bash -c '
		set -eu
		apt-get update -qq
		apt-get install -y -qq build-essential pkg-config libgtk-4-dev libwebkitgtk-6.0-dev ca-certificates curl tar >/dev/null
		curl -fsSL https://go.dev/dl/go1.27.0.linux-amd64.tar.gz -o /tmp/go.tgz
		tar -C /usr/local -xzf /tmp/go.tgz
		export PATH="/usr/local/go/bin:$PATH"

		echo "-- daemon"
		go build -trimpath -ldflags "-X main.Version=$VERSION $TELEMETRY_LDFLAG" -o /out/reelbolt-runner ./cmd/reelbolt-runner

		echo "-- tray and window"
		(cd cmd/reelbolt-runner-desktop && go build -trimpath -ldflags "-X main.Version=$VERSION $TELEMETRY_LDFLAG" -o /out/reelbolt-runner-desktop .)

		'"$deb_step"'
	'

# A tarball too, for anyone who would rather not use a package: the same layout
# the .deb installs, minus dpkg.
stage="$out_abs/stage"
rm -rf "$stage"
mkdir -p "$stage/opt/reelbolt/runner" \
	"$stage/usr/bin" \
	"$stage/usr/share/applications" \
	"$stage/usr/share/icons/hicolor/256x256/apps" \
	"$stage/usr/share/doc/reelbolt-runner"
install -m755 "$out_abs/reelbolt-runner" "$stage/opt/reelbolt/runner/reelbolt-runner"
install -m755 "$out_abs/reelbolt-runner-desktop" "$stage/opt/reelbolt/runner/reelbolt-runner-desktop"
install -m755 "$repo/runner/build/linux/reelbolt-runner-wrapper" "$stage/usr/bin/reelbolt-runner"
install -m755 "$repo/runner/build/linux/reelbolt-runner-desktop-wrapper" "$stage/usr/bin/reelbolt-runner-desktop"
install -m644 "$repo/runner/build/linux/reelbolt-runner.desktop" "$stage/usr/share/applications/reelbolt-runner.desktop"
install -m644 "$repo/runner/assets/icons/reelbolt-runner-256.png" "$stage/usr/share/icons/hicolor/256x256/apps/reelbolt-runner.png"
tar -czf "$out_abs/reelbolt-runner-$version-linux-amd64.tar.gz" -C "$stage" opt usr
rm -rf "$stage"

echo
echo "desktop-local.sh: artifacts in $out_abs"
ls -l "$out_abs"/reelbolt-runner "$out_abs"/reelbolt-runner-desktop
[ "$build_deb" = yes ] && ls -l "$out_abs"/*.deb || true
ls -l "$out_abs"/*.tar.gz
echo
echo "Install on this machine:"
echo "  sudo apt install '$out_abs'/\$(cd '$out_abs' && ls *.deb | head -1)"
echo "Or without a package manager, from the tarball:"
echo "  sudo tar -xzf '$out_abs'/reelbolt-runner-$version-linux-amd64.tar.gz -C /"
