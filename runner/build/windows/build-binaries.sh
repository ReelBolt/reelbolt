#!/bin/sh
# Cross-build the Windows runner binaries on Linux (inside the pinned toolchain
# container, see Dockerfile.toolchain / run-in-container.sh).
#
#   build-binaries.sh --version 1.4.2 --out <dir>
#
# env: LDFLAGS  extra -ldflags for BOTH binaries (the workflow stamps main.Version)
#      TAGS     the bare tag list from `reelbolt-manifest ldflags` ("release" or empty); passed as -tags
#      FFMPEG_GPG_KEYRING, SOURCES_OUT  forwarded to ../ffmpeg/build.sh
#
# Produces <dir>/{reelbolt-runner.exe,reelbolt-runner-desktop.exe,ffmpeg.exe,
# ffprobe.exe,THIRD_PARTY_NOTICES.md}. No secrets are read or needed.
#
# CGO: both Go binaries build with CGO_ENABLED=0. Measured, not assumed: the Wails
# v3 beta.28 tray is pure Go on Windows (go-webview2 + syscall loaders), so no
# MinGW is needed for Go; mingw-w64 is only needed for the ffmpeg/x264/lame C code.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
runner=$(CDPATH='' cd -- "$here/../.." && pwd)

version=""
out=""
while [ $# -gt 0 ]; do
	case "$1" in
	--version) version=${2:-}; shift 2 ;;
	--out) out=${2:-}; shift 2 ;;
	*) echo "build-binaries.sh: unknown argument $1" >&2; exit 2 ;;
	esac
done
case "$version" in
[0-9]*.[0-9]*.[0-9]*) ;;
*) echo "build-binaries.sh: --version '$version' is not a dotted version (1.4.2)" >&2; exit 2 ;;
esac
[ -n "$out" ] || { echo "build-binaries.sh: --out is required" >&2; exit 2; }
mkdir -p "$out"
out=$(CDPATH='' cd -- "$out" && pwd)

ldflags=${LDFLAGS:-}
# TAGS is the bare list from `reelbolt-manifest ldflags` ("release" or empty): it goes behind -tags.
tags=${TAGS:+-tags $TAGS}

# The apt packages in the toolchain image are not version-pinned; record them.
dpkg-query -W -f='build-binaries.sh: toolchain ${Package} ${Version}\n' \
	gcc-mingw-w64-x86-64 g++-mingw-w64-x86-64 binutils-mingw-w64-x86-64 mingw-w64-x86-64-dev 2>/dev/null || true
echo "build-binaries.sh: ffmpeg + ffprobe (mingw-w64 cross build)"
# LDFLAGS/TAGS are Go settings; unset so they cannot leak into the C configure steps.
env -u LDFLAGS -u TAGS -u CGO_ENABLED sh "$runner/build/ffmpeg/build.sh" --target windows/amd64 --out "$out" ${SOURCES_OUT:+--sources-out "$SOURCES_OUT"}
rm -f "$out/ffmpeg-version.txt" "$out/ffmpeg-license.txt"

export GOOS=windows GOARCH=amd64 CGO_ENABLED=0

echo "build-binaries.sh: daemon"
(
	cd "$runner"
	go mod verify
	# shellcheck disable=SC2086
	go build -mod=readonly $tags -trimpath -ldflags "$ldflags" -o "$out/reelbolt-runner.exe" ./cmd/reelbolt-runner
)

echo "build-binaries.sh: tray (window frontend, then Wails host with -H=windowsgui)"
(
	cd "$runner"
	# Always from the lockfile, never a node_modules left by an earlier run.
	npm ci --prefix frontend --ignore-scripts --no-audit --no-fund
	node cmd/reelbolt-runner-desktop/build.mjs
	cd cmd/reelbolt-runner-desktop
	# The tray go.mod is tidy now, so a read-only build is the rule: -mod=readonly
	# (no scratch modfile, no -mod=mod), and the module download is verified first.
	go mod verify
	# shellcheck disable=SC2086
	go build -mod=readonly $tags -trimpath -ldflags "$ldflags -H=windowsgui" -o "$out/reelbolt-runner-desktop.exe" .
)

# build.mjs replaces dist/; put back the tracked placeholder so the tree stays clean.
touch "$runner/cmd/reelbolt-runner-desktop/dist/.gitkeep"
cp "$runner/build/ffmpeg/THIRD_PARTY_NOTICES.md" "$out/THIRD_PARTY_NOTICES.md"

for f in reelbolt-runner.exe reelbolt-runner-desktop.exe ffmpeg.exe ffprobe.exe THIRD_PARTY_NOTICES.md; do
	[ -s "$out/$f" ] || { echo "build-binaries.sh: $f missing or empty" >&2; exit 1; }
done
# The stamp cannot be asked of a Windows exe here; check the version string is in it.
if [ -z "$ldflags" ]; then
	echo "build-binaries.sh: LDFLAGS is empty; binaries are not version-stamped (fine for a rehearsal, not for a release)" >&2
elif ! grep -aq -- "$version" "$out/reelbolt-runner.exe"; then
	echo "build-binaries.sh: version $version not found in reelbolt-runner.exe; -ldflags did not stamp main.Version (set LDFLAGS)" >&2
	exit 1
fi
echo "build-binaries.sh: done -> $out"
