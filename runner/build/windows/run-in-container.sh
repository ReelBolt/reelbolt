#!/bin/sh
# Run a command inside the pinned Windows toolchain container, with the repo
# mounted at /work. Builds the image if it is missing.
#
#   runner/build/windows/run-in-container.sh [--engine docker|podman] [--rebuild] -- CMD ARGS...
#
# Example (unsigned, zero secrets):
#   run-in-container.sh -- runner/build/windows/build-binaries.sh --version 1.2.3 --out dist
#   run-in-container.sh -- runner/build/windows/package.sh --version 1.2.3 --dist dist --out out --signing none
#
# Signing secrets are forwarded BY NAME (-e VAR, no value in argv) and only when
# they are set in the caller's environment. The container runs as the caller's uid
# so output files are not root-owned. Paths in CMD are relative to the repo root.
set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
repo=$(CDPATH='' cd -- "$here/../../.." && pwd)
image=${REELBOLT_WINDOWS_TOOLCHAIN_IMAGE:-reelbolt-windows-toolchain:local}
engine=""
rebuild=no

while [ $# -gt 0 ]; do
	case "$1" in
	--engine) engine=${2:-}; shift 2 ;;
	--rebuild) rebuild=yes; shift ;;
	--) shift; break ;;
	*) echo "run-in-container.sh: unknown option $1 (put the command after --)" >&2; exit 2 ;;
	esac
done
[ $# -gt 0 ] || { echo "run-in-container.sh: no command given after --" >&2; exit 2; }

if [ -z "$engine" ]; then
	for e in docker podman; do
		if command -v "$e" >/dev/null 2>&1; then engine=$e; break; fi
	done
fi
[ -n "$engine" ] || { echo "run-in-container.sh: neither docker nor podman is installed" >&2; exit 1; }

if [ "$rebuild" = yes ] || ! "$engine" image inspect "$image" >/dev/null 2>&1; then
	echo "run-in-container.sh: building $image"
	# REELBOLT_CONTAINER_NETWORK=host helps on hosts where the default bridge has no DNS.
	# shellcheck disable=SC2086
	"$engine" build ${REELBOLT_CONTAINER_NETWORK:+--network=$REELBOLT_CONTAINER_NETWORK} -f "$here/Dockerfile.toolchain" -t "$image" "$here"
fi

envs=""
for v in AZURE_TRUSTED_SIGNING_TOKEN TS_ENDPOINT TS_ACCOUNT TS_PROFILE TS_TIMESTAMP_URL EXPECTED_SIGNER_SUBJECT EXPECTED_SIGNER_THUMBPRINT \
	WINDOWS_SIGNING_PFX_BASE64 WINDOWS_SIGNING_PFX_PASSWORD LDFLAGS TAGS \
	FFMPEG_GPG_KEYRING SOURCES_OUT UPGRADE_CODE; do
	if [ -n "$(printenv "$v" 2>/dev/null || true)" ]; then
		envs="$envs -e $v"
	fi
done

userflag="--user $(id -u):$(id -g)"
[ "$engine" = podman ] && userflag="--userns=keep-id"

# shellcheck disable=SC2086
exec "$engine" run --rm --tmpfs /tmp:exec,mode=1777,size=${REELBOLT_CONTAINER_TMP:-6g} $userflag ${REELBOLT_CONTAINER_NETWORK:+--network=$REELBOLT_CONTAINER_NETWORK} $envs \
	-v "$repo:/work" -w /work "$image" "$@"
