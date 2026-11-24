#!/bin/sh
# Host side of the Linux release build: run linux-inside.sh in the pinned Ubuntu
# container, so the host needs docker and nothing else (no sudo, no apt, no Go).
#
#   linux-container.sh --target linux/amd64 --version 1.4.2 --dist dist
#
# Environment: LDFLAGS and TAGS (from `reelbolt-manifest ldflags`), HAS_KEY
# (true|false; checks the trust root landed), REELBOLT_BUILD_JOBS (optional).
#
# NO SHARED CACHE: /cache is a per-run tmpfs, so Go modules and npm packages are
# fetched fresh and verified every run. A named volume would be state a pull_request
# job on the same pool could poison before a signed artifact is built from it.
#
# The container runs as root (apt needs it) and hands every file it wrote back to
# the invoking user on exit, so a self-hosted runner's workspace stays removable.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
repo=$(CDPATH='' cd -- "$here/../../.." && pwd)
# shellcheck source=pins.env
. "$here/pins.env"

target=""
version=""
dist=""
while [ $# -gt 0 ]; do
	case "$1" in
	--target) target=${2:-}; shift 2 ;;
	--version) version=${2:-}; shift 2 ;;
	--dist) dist=${2:-}; shift 2 ;;
	*) echo "linux-container.sh: unknown argument $1" >&2; exit 2 ;;
	esac
done
for pair in "target:$target" "version:$version" "dist:$dist"; do
	[ -n "${pair#*:}" ] || { echo "linux-container.sh: --${pair%%:*} is required" >&2; exit 2; }
done
case "$target" in
linux/amd64 | linux/arm64) ;;
*) echo "linux-container.sh: $target is not a Linux release target" >&2; exit 2 ;;
esac
command -v docker >/dev/null 2>&1 || { echo "linux-container.sh: docker is required" >&2; exit 1; }

case "$dist" in /*) ;; *) dist="$repo/$dist" ;; esac
mkdir -p "$dist"
rel=${dist#"$repo"/}
case "$rel" in "$dist") echo "linux-container.sh: --dist must be inside the repository checkout" >&2; exit 2 ;; esac

arch=${target##*/}
# --platform makes an arm64 leg on an amd64 host an emulated (slow) build rather
# than a silently wrong one; on a matching host it is a no-op.
exec docker run --rm \
	--platform "linux/$arch" \
	-v "$repo:/src" \
	--tmpfs /cache:exec,mode=0755,size=8g \
	-e DEBIAN_FRONTEND=noninteractive \
	-e TARGET="$target" \
	-e VERSION="$version" \
	-e DIST_REL="$rel" \
	-e RELEASE_LDFLAGS="${LDFLAGS:-}" \
	-e RELEASE_TAGS="${TAGS:-}" \
	-e HAS_KEY="${HAS_KEY:-false}" \
	-e REELBOLT_BUILD_JOBS="${REELBOLT_BUILD_JOBS:-}" \
	-e HOST_UID="$(id -u)" \
	-e HOST_GID="$(id -g)" \
	-w /src \
	"$BUILD_IMAGE" \
	bash /src/runner/build/ci/linux-inside.sh
