#!/bin/sh
# Exercise the whole release pipeline locally, with no cloud account and no real
# signing key (WP E10b).
#
#   runner/build/local-release.sh --work /tmp/reelbolt-release
#
# What this proves, end to end and offline:
#
#   * the release tool builds a manifest from real artifact files;
#   * it signs it with an Ed25519 key generated here, and the signature verifies;
#   * a TAGGED BUILD of the runner, stamped with that key's public half through
#     -ldflags, ACCEPTS the manifest through the real "reelbolt-runner update"
#     code path and installs the new binary, keeping the old one as .prev;
#   * a tampered artifact and a manifest signed by another key are both refused.
#
# That is the E9 contract's own "NOT verified: that a manifest produced by CI
# verifies against the public key compiled into a shipped build", closed locally.
#
# What it CANNOT prove, and does not pretend to: that the real release key
# (E10a) exists, that the artifacts are Authenticode-signed and install through
# msiexec, that R2 is reachable, or that the CI workflow as written runs - none
# of which is possible without the accounts. See runner/build/README.md.
#
# The dev key this generates is a TEST key. It is written under --work and never
# committed, and a "-tags release" build ignores the environment escape hatch it
# relies on, so it cannot be used to move a shipped runner.

set -eu

root=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
work=""
port=""

usage() {
	cat <<'USAGE'
usage: local-release.sh --work <dir> [--port <n>]

  --work  a scratch directory this script owns; it is emptied on every run
  --port  the local port the file server listens on (default: 18080)
USAGE
}

while [ $# -gt 0 ]; do
	case "$1" in
	--work)
		work="${2:-}"
		shift 2
		;;
	--port)
		port="${2:-}"
		shift 2
		;;
	-h | --help)
		usage
		exit 0
		;;
	*)
		echo "local-release.sh: unknown argument $1" >&2
		usage >&2
		exit 2
		;;
	esac
done
if [ -z "$work" ]; then
	echo "local-release.sh: --work is required" >&2
	usage >&2
	exit 2
fi
[ -n "$port" ] || port=18080

# Go is not installed on every machine this repository is worked on, so the
# build runs in the module's own container when it is missing. The runner's
# cmd/reelbolt-runner is pure Go, so no C toolchain is needed here.
# The work directory is mounted at the same path it has on the host, so a -out
# argument means the same file on both sides of the container boundary.
gorun() {
	if command -v go >/dev/null 2>&1; then
		(cd "$root/runner" && go "$@")
	else
		docker run --rm -v "$root:/src" -v "$work:$work" \
			-v reelforge-go-modcache:/go/pkg/mod -w /src/runner -e GOPROXY=off \
			golang:1.26-alpine go "$@"
	fi
}

# serve runs a static file server over the release tree, on whichever one this
# machine has.
serve() {
	if command -v python3 >/dev/null 2>&1; then
		python3 -m http.server "$port" --bind 127.0.0.1 --directory "$1"
	elif command -v busybox >/dev/null 2>&1; then
		busybox httpd -f -p "127.0.0.1:$port" -h "$1"
	elif command -v php >/dev/null 2>&1; then
		php -S "127.0.0.1:$port" -t "$1"
	else
		echo "local-release.sh: no static file server found (need python3, busybox httpd or php)" >&2
		exit 1
	fi
}

# Emptied rather than removed: --work may be a mount point (a container bind, or
# a tmpfs), and a mount point cannot be deleted.
mkdir -p "$work"
find "$work" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
mkdir -p "$work/keys" "$work/v1" "$work/v2" "$work/srv/beta" "$work/config"
echo "== local-release: work directory $work"

# ---- 1. a release key --------------------------------------------------------
echo "== 1. generate a release key (E10a, done here with a throwaway test key)"
gorun run ./cmd/reelbolt-manifest gen-key -out "$work/keys/release.key" -id local-test >"$work/keys/gen-key.txt"
stamp=$(sed -n 's/^  RUNNER_RELEASE_PUBLIC_KEY_CURRENT = //p' "$work/keys/gen-key.txt")
if [ -z "$stamp" ]; then
	echo "local-release.sh: gen-key did not print a public stamp" >&2
	cat "$work/keys/gen-key.txt" >&2
	exit 1
fi
echo "   public key: $stamp"

# ---- 2. two tagged builds ----------------------------------------------------
#
# The build line comes from the tool that signs the manifest, so the variable
# paths cannot drift. It also stamps the crash collector (production by default;
# REELBOLT_TELEMETRY_ENDPOINT_STAMP=none or another https address overrides it),
# which the headless binary built here does not link, so it changes nothing. No -tags release here: a development build is the one that
# accepts a loopback http update host, and that is what this script serves.
echo "== 2. build reelbolt-runner 0.0.1 and 0.0.2 stamped with that key"
ldflags_for() {
	gorun run ./cmd/reelbolt-manifest ldflags -version "$1" -current-key "$stamp" |
		sed -n 's/^ldflags=//p'
}
# A stamp contains a colon (the key id), so the version, the build line and the
# output directory are passed as separate arguments rather than packed into one
# string and split apart again.
build_version() {
	build_version="$1"
	build_out="$2"
	build_flags=$(ldflags_for "$build_version")
	# shellcheck disable=SC2086
	gorun build -trimpath -ldflags "$build_flags" -o "$build_out/reelbolt-runner" ./cmd/reelbolt-runner
	printf '   %s reports: %s\n' "$build_out" "$("$build_out/reelbolt-runner" version)"
}
build_version 0.0.1 "$work/v1"
build_version 0.0.2 "$work/v2"

# ---- 3. package 0.0.2 --------------------------------------------------------
#
# The bundled media tools are stubs here: this script exercises the PIPELINE, not
# ffmpeg. runner/build/ffmpeg/build.sh is what produces the real ones, and it
# refuses to run until the digests in pins.env are pinned.
echo "== 3. package 0.0.2"
for tool in ffmpeg ffprobe; do
	printf '#!/bin/sh\necho "%s: a stub for the local release dry run"\n' "$tool" >"$work/v2/$tool"
	chmod 0755 "$work/v2/$tool"
done
"$root/runner/build/package.sh" \
	--target linux/amd64 \
	--version 0.0.2 \
	--dist "$work/v2" \
	--out "$work/dist" \
	--formats binary,archive
echo "   (appimage and deb are skipped: appimagetool and nfpm are not installed here)"

# ---- 4. manifest, signature, latest.json -------------------------------------
echo "== 4. assemble, sign, verify"
artifact="$work/dist/reelbolt-runner-0.0.2-linux-amd64"
gorun run ./cmd/reelbolt-manifest assemble \
	-version 0.0.2 \
	-channel beta \
	-rollout-percent 100 \
	-url-base "http://127.0.0.1:$port" \
	-out "$work/srv/beta/manifest.json" \
	-artifact "linux/amd64/tarball=$artifact"
RUNNER_LOCAL_TEST_KEY_FILE="$work/keys/release.key" \
	gorun run ./cmd/reelbolt-manifest sign \
	-manifest "$work/srv/beta/manifest.json" \
	-key-file "$work/keys/release.key" \
	-expect-public "$stamp"
gorun run ./cmd/reelbolt-manifest verify \
	-manifest "$work/srv/beta/manifest.json" \
	-public-key "$stamp" \
	-expect-version 0.0.2 \
	-expect-channel beta
gorun run ./cmd/reelbolt-manifest latest \
	-manifest "$work/srv/beta/manifest.json" \
	-public-key "$stamp" \
	-url-base "http://127.0.0.1:$port/beta/latest" \
	-dist "$work/dist" \
	-out "$work/srv/beta/latest.json" \
	-uploads-out "$work/srv/uploads.txt"

cp "$work/dist/reelbolt-runner-0.0.2-linux-amd64" "$work/srv/"

# ---- 5. serve it and update a 0.0.1 install ----------------------------------
echo "== 5. serve $work/srv on 127.0.0.1:$port and update an installed 0.0.1"
serve "$work/srv" >"$work/server.log" 2>&1 &
server_pid=$!
trap 'kill "$server_pid" 2>/dev/null || true' EXIT INT TERM
sleep 1

# XDG_CONFIG_HOME keeps the runner's own configuration inside --work: it writes
# runner.json on first use and this must not touch the caller's real config.
export XDG_CONFIG_HOME="$work/config"
install_dir="$work/install"
mkdir -p "$install_dir"
cp "$work/v1/reelbolt-runner" "$install_dir/reelbolt-runner"

base="http://127.0.0.1:$port"
run_update() {
	"$install_dir/reelbolt-runner" update --update-base-url "$base" --channel beta "$@"
}

echo "--- 5a. check (nothing is downloaded)"
check=$(run_update --check --json)
printf '%s\n' "$check"
# Decide answers "update" for a newer release this device is admitted to.
case "$check" in
*'"action": "update"'*) ;;
*)
	echo "local-release: the check did not offer the release:" >&2
	printf '%s\n' "$check" >&2
	exit 1
	;;
esac
case "$check" in
*'"releaseVersion": "0.0.2"'*) ;;
*)
	echo "local-release: the check did not name 0.0.2:" >&2
	printf '%s\n' "$check" >&2
	exit 1
	;;
esac

echo "--- 5b. apply (downloads, verifies against the compiled-in key, installs)"
apply=$(run_update --apply --json)
printf '%s\n' "$apply"
if [ ! -f "$install_dir/reelbolt-runner.prev" ]; then
	echo "local-release: apply did not keep the previous binary as .prev" >&2
	exit 1
fi
if ! cmp -s "$install_dir/reelbolt-runner" "$work/v2/reelbolt-runner"; then
	echo "local-release: the installed binary is not the one the manifest described" >&2
	exit 1
fi
echo "   installed 0.0.2; previous binary kept at reelbolt-runner.prev"
echo "   running binary reports: $("$install_dir/reelbolt-runner" version) (the swap takes effect on restart)"

# ---- 6. the refusals ---------------------------------------------------------
echo "== 6. refusals"
echo "--- 6a. an artifact the manifest did not sign"
cp "$work/srv/reelbolt-runner-0.0.2-linux-amd64" "$work/srv/artifact.good"
printf 'x' >>"$work/srv/reelbolt-runner-0.0.2-linux-amd64"
cp "$install_dir/reelbolt-runner.prev" "$install_dir/reelbolt-runner"
set +e
tampered=$(run_update --apply 2>&1)
tampered_code=$?
set -e
printf '%s\n' "$tampered"
if [ "$tampered_code" -eq 0 ]; then
	echo "local-release: a tampered artifact was accepted" >&2
	exit 1
fi
if ! printf '%s' "$tampered" | grep -q "does not match the signed manifest"; then
	echo "local-release: the tampered artifact was refused for the wrong reason" >&2
	exit 1
fi
if ! cmp -s "$install_dir/reelbolt-runner" "$install_dir/reelbolt-runner.prev"; then
	echo "local-release: the tampered update changed the installed binary" >&2
	exit 1
fi
mv "$work/srv/artifact.good" "$work/srv/reelbolt-runner-0.0.2-linux-amd64"

echo "--- 6b. a manifest signed by somebody else"
gorun run ./cmd/reelbolt-manifest gen-key -out "$work/keys/other.key" -id impostor >/dev/null
gorun run ./cmd/reelbolt-manifest sign -manifest "$work/srv/beta/manifest.json" -key-file "$work/keys/other.key"
cp "$work/srv/beta/manifest.json.sig" "$work/srv/beta/manifest.json.sig.impostor"
set +e
foreign=$(run_update --check 2>&1)
foreign_code=$?
set -e
printf '%s\n' "$foreign"
if [ "$foreign_code" -eq 0 ]; then
	echo "local-release: a manifest signed by another key was accepted" >&2
	exit 1
fi
if ! printf '%s' "$foreign" | grep -q "signature does not verify"; then
	echo "local-release: the foreign signature was refused for the wrong reason" >&2
	exit 1
fi

echo "--- 6c. a manifest that no build carries a key for"
cp "$work/keys/release.key" "$work/keys/other.key"
gorun run ./cmd/reelbolt-manifest sign -manifest "$work/srv/beta/manifest.json" -key-file "$work/keys/release.key"
keyless="$work/v1/reelbolt-runner"
gorun build -trimpath -ldflags "$(gorun run ./cmd/reelbolt-manifest ldflags -version 0.0.99 -no-release-key | sed -n 's/^ldflags=//p')" -o "$keyless" ./cmd/reelbolt-runner
set +e
nokey=$("$keyless" update --check 2>&1)
nokey_code=$?
set -e
printf '%s\n' "$nokey"
if [ "$nokey_code" -eq 0 ]; then
	echo "local-release: a build with no trust root did not refuse" >&2
	exit 1
fi
if ! printf '%s' "$nokey" | grep -q "no release public key"; then
	echo "local-release: the unkeyed build refused for the wrong reason" >&2
	exit 1
fi
echo "   (that is E9's documented state of every build made from this source tree)"

echo
echo "== local-release: OK"
echo "   artifact     $work/dist/reelbolt-runner-0.0.2-linux-amd64"
echo "   manifest     $work/srv/beta/manifest.json"
echo "   signature    $work/srv/beta/manifest.json.sig"
echo "   latest.json  $work/srv/beta/latest.json"
echo
echo "   The key used here is a throwaway test key. A real release needs the E10a"
echo "   key, Windows signing and R2 credentials: runner/build/README.md."
