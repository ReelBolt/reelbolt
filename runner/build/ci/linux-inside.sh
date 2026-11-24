#!/bin/bash
# Runs INSIDE the pinned Ubuntu container (see linux-container.sh). Installs the
# toolchain, builds ffmpeg, the daemon and the tray, checks the stamps landed and
# packages tarball/AppImage/deb. Needs root in the container, never on the host.
set -euo pipefail

: "${TARGET:?}" "${VERSION:?}" "${DIST_REL:?}" "${HOST_UID:?}" "${HOST_GID:?}"
arch=${TARGET##*/}
cd /src
# shellcheck source=pins.env
. runner/build/ci/pins.env

finish() {
	# Hand everything this container wrote back to the host user.
	chown -R "$HOST_UID:$HOST_GID" /src 2>/dev/null || true
}
trap finish EXIT

case "$arch" in
amd64) go_sha=$GO_SHA256_AMD64; node_sha=$NODE_SHA256_AMD64; node_arch=x64; ait_sha=$APPIMAGETOOL_SHA256_AMD64; ait_arch=x86_64 ;;
arm64) go_sha=$GO_SHA256_ARM64; node_sha=$NODE_SHA256_ARM64; node_arch=arm64; ait_sha=$APPIMAGETOOL_SHA256_ARM64; ait_arch=aarch64 ;;
*) echo "linux-inside.sh: unsupported arch $arch" >&2; exit 2 ;;
esac

fetch() { # url sha256 dest
	curl -fsSL --retry 3 -o "$3" "$1"
	echo "$2  $3" | sha256sum -c - >/dev/null || { echo "linux-inside.sh: sha256 mismatch for $1" >&2; rm -f "$3"; exit 1; }
}

echo "-- toolchain (apt, inside the container)"
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
	build-essential nasm pkg-config libgtk-4-dev libwebkitgtk-6.0-dev \
	ca-certificates curl xz-utils git file squashfs-tools >/dev/null

tmp=$(mktemp -d)
fetch "https://go.dev/dl/go$GO_VERSION.linux-$arch.tar.gz" "$go_sha" "$tmp/go.tgz"
tar -C /usr/local -xzf "$tmp/go.tgz"
fetch "https://nodejs.org/dist/v$NODE_VERSION/node-v$NODE_VERSION-linux-$node_arch.tar.xz" "$node_sha" "$tmp/node.txz"
tar -C /usr/local --strip-components=1 -xJf "$tmp/node.txz"
fetch "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-$ait_arch.AppImage" "$ait_sha" /usr/local/bin/appimagetool
chmod 0755 /usr/local/bin/appimagetool
rm -rf "$tmp"

export PATH="/usr/local/go/bin:/cache/go/bin:$PATH"
export GOPATH=/cache/go GOMODCACHE=/cache/go/pkg/mod GOCACHE=/cache/go/build GOFLAGS="-buildvcs=false -mod=readonly"
export npm_config_cache=/cache/npm
# No FUSE in a container: run the AppImage by extraction.
export APPIMAGE_EXTRACT_AND_RUN=1
go install "github.com/goreleaser/nfpm/v2/cmd/nfpm@$NFPM_VERSION"

dist="/src/$DIST_REL"
mkdir -p "$dist"

echo "-- ffmpeg and ffprobe"
runner/build/ffmpeg/build.sh --target "$TARGET" --out "$dist" --sources-out "$dist/sources" ${REELBOLT_BUILD_JOBS:+--jobs "$REELBOLT_BUILD_JOBS"}

echo "-- daemon and tray"
cd runner
go mod verify
# The stamp arrives as RELEASE_LDFLAGS/RELEASE_TAGS, never as LDFLAGS: autotools
# builds (x264, lame, ffmpeg) read $LDFLAGS and a Go "-X main.Version=..." in it
# makes their compiler check fail with "No working C compiler found".
# RELEASE_TAGS is the bare tag list from `reelbolt-manifest ldflags` ("release", or empty
# for a dry run), NOT a flag: it goes behind -tags. (The old workflow passed it
# unquoted as `go build $TAGS`, which would have built a package named "release".)
go build ${RELEASE_TAGS:+-tags "$RELEASE_TAGS"} -trimpath -ldflags "${RELEASE_LDFLAGS:-}" -o "$dist/reelbolt-runner" ./cmd/reelbolt-runner
npm ci --prefix frontend --ignore-scripts --no-audit --no-fund
node cmd/reelbolt-runner-desktop/build.mjs
# build.mjs replaces dist/ and takes the tracked .gitkeep with it; restore it so the
# checkout stays clean (the file is what keeps `go:embed all:dist` compiling).
: >cmd/reelbolt-runner-desktop/dist/.gitkeep
(cd cmd/reelbolt-runner-desktop && go mod verify && go build ${RELEASE_TAGS:+-tags "$RELEASE_TAGS"} -trimpath -ldflags "${RELEASE_LDFLAGS:-}" -o "$dist/reelbolt-runner-desktop" .)
cd /src

# A stamp that did not land is a release that reports "dev" and can never be
# updated to, so it is checked here rather than discovered by a user.
echo "-- check the stamps landed"
got=$("$dist/reelbolt-runner" version)
echo "$got"
case "$got" in
*"$VERSION"*) ;;
*) echo "::error::the built binary reports '$got', not version $VERSION; -ldflags did not stamp main.Version"; exit 1 ;;
esac
out=$("$dist/reelbolt-runner" update --check 2>&1 || true)
if [ "${HAS_KEY:-false}" = "true" ]; then
	if printf '%s' "$out" | grep -q "no release public key"; then
		echo "::error::the release public key was not stamped into the binary"; printf '%s\n' "$out"; exit 1
	fi
elif ! printf '%s' "$out" | grep -q "no release public key"; then
	echo "::error::this build was supposed to carry no trust root and did not say so"; printf '%s\n' "$out"; exit 1
fi

echo "-- package (tarball payload, archive, AppImage, deb)"
# The AppImage is not GPG-signed: see runner/build/README.md. The manifest's
# Ed25519 signature is the trust root the update path uses.
runner/build/package.sh --target "$TARGET" --version "$VERSION" --dist "$dist" --out "$dist/artifacts"
