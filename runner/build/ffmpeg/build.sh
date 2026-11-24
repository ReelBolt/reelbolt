#!/bin/sh
# Build the runner's bundled static ffmpeg and ffprobe from pinned sources.
#
#   build.sh --target linux/amd64 --out <dir>     build for a target
#   build.sh --target linux/amd64 --pin           download, print digests, exit
#
# This runs INSIDE the release pipeline (see .github/workflows/runner-release.yml)
# on a native runner per target; windows/amd64 is the exception and is cross-built
# with mingw-w64 when the host is not Windows (runner/build/windows container).
# The point of the exercise is that the binary that ends up in an installer was
# built by our CI from sources whose digests are
# pinned in this directory.
#
# Fail-closed by design:
#   * an unpinned digest (CHANGE_ME) stops the build before anything is unpacked;
#   * every download is hashed and compared, and a mismatch deletes the file;
#   * the built ffmpeg is asked for its version and its licence, and the licence
#     must say GPL - a build that silently lost libx264 is an ffmpeg that cannot
#     produce output parity with the cloud, which is worse than no build at all.
#
# WP E10b. Sources and licence obligations: ./THIRD_PARTY_NOTICES.md.

set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
pins="$here/pins.env"

target=""
out=""
pin_only=no
jobs=""
sources_out=""

usage() {
	cat <<'USAGE'
usage: build.sh --target <os>/<arch> --out <dir> [--jobs N] [--sources-out <dir>]
       build.sh --target <os>/<arch> --pin

  --target       linux/amd64, linux/arm64 or windows/amd64
  --out          where ffmpeg and ffprobe are written (created if missing)
  --sources-out  keep the verified source tarballs here, so the release can
                 publish them and the written offer in THIRD_PARTY_NOTICES.md
                 resolves to the exact sources these binaries were built from
  --pin          download the sources, print their SHA-256 and exit; use it to
                 fill the digests in pins.env
  --jobs         parallel make jobs (default: the number of CPUs)
USAGE
}

while [ $# -gt 0 ]; do
	case "$1" in
	--target)
		target=${2:-}
		shift 2
		;;
	--out)
		out=${2:-}
		shift 2
		;;
	--pin)
		pin_only=yes
		shift
		;;
	--jobs)
		jobs="${2:-}"
		shift 2
		;;
	--sources-out)
		sources_out="${2:-}"
		shift 2
		;;
	-h | --help)
		usage
		exit 0
		;;
	*)
		echo "build.sh: unknown argument $1" >&2
		usage >&2
		exit 2
		;;
	esac
done

if [ -z "$target" ]; then
	echo "build.sh: --target is required" >&2
	usage >&2
	exit 2
fi
case "$target" in
linux/amd64 | linux/arm64 | windows/amd64) ;;
*)
	echo "build.sh: $target is not a target this release builds. D26 ships Windows x64 and Linux x64/arm64 only." >&2
	exit 2
	;;
esac
if [ "$pin_only" = no ] && [ -z "$out" ]; then
	echo "build.sh: --out is required" >&2
	usage >&2
	exit 2
fi

# shellcheck source=pins.env
. "$pins"

ffmpeg_url="https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz"
x264_url="https://code.videolan.org/videolan/x264/-/archive/${X264_COMMIT}/x264-${X264_COMMIT}.tar.bz2"
lame_url="https://downloads.sourceforge.net/project/lame/lame/${LAME_VERSION}/lame-${LAME_VERSION}.tar.gz"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT INT TERM

# digest_of prints the lowercase SHA-256 of a file: the same digest algorithm
# and the same alphabet the manifest uses.
digest_of() {
	if command -v sha256sum >/dev/null 2>&1; then
		sha256sum "$1" | cut -d' ' -f1
	elif command -v shasum >/dev/null 2>&1; then
		shasum -a 256 "$1" | cut -d' ' -f1
	else
		echo "build.sh: no sha256sum or shasum on this machine" >&2
		exit 1
	fi
}

# fetch downloads a URL to a file, following redirects (SourceForge redirects to
# a mirror) and retrying a flaky network rather than failing a release on it.
fetch() {
	curl -fsSL --retry 3 --retry-delay 5 -o "$2" "$1"
}

# verify compares a download against its pinned digest. An unpinned digest is a
# refusal, not a warning.
verify() {
	label=$1
	file=$2
	want=$3
	got=$(digest_of "$file")
	case "$want" in
	"" | CHANGE_ME)
		echo "build.sh: $label is not pinned." >&2
		echo "  the downloaded $label has SHA-256 $got" >&2
		echo "  put that value in $pins as ${label}_SHA256 and commit it before releasing." >&2
		exit 1
		;;
	esac
	if [ "$got" != "$want" ]; then
		echo "build.sh: $label does not match its pinned digest" >&2
		echo "  pinned:   $want" >&2
		echo "  download: $got" >&2
		echo "  The upstream artifact changed. Do NOT release it; find out why first." >&2
		rm -f "$file"
		exit 1
	fi
	echo "build.sh: $label verified ($got)"
}

echo "build.sh: fetching sources for $target"
fetch "$ffmpeg_url" "$work/ffmpeg.tar.xz"
fetch "$x264_url" "$work/x264.tar.bz2"
fetch "$lame_url" "$work/lame.tar.gz"

if [ "$pin_only" = yes ]; then
	echo
	echo "Paste these into $pins and review the diff:"
	echo "  FFMPEG_SHA256=$(digest_of "$work/ffmpeg.tar.xz")"
	echo "  X264_SHA256=$(digest_of "$work/x264.tar.bz2")"
	echo "  LAME_SHA256=$(digest_of "$work/lame.tar.gz")"
	echo
	echo "Cross-check the ffmpeg one: the tarball has a detached GPG signature at"
	echo "  ${ffmpeg_url}.asc"
	echo "and upstream's release key is published at"
	echo "  https://ffmpeg.org/ffmpeg-release-signing-key.asc"
	exit 0
fi

verify FFMPEG "$work/ffmpeg.tar.xz" "$FFMPEG_SHA256"
verify X264 "$work/x264.tar.bz2" "$X264_SHA256"
verify LAME "$work/lame.tar.gz" "$LAME_SHA256"

# Keep the verified tarballs: THIRD_PARTY_NOTICES.md promises the complete
# corresponding source for these binaries at a stable URL, and the way that
# promise stays true is that the release uploads the same bytes the build
# verified rather than re-downloading them later from a URL that may have moved.
if [ -n "$sources_out" ]; then
	mkdir -p "$sources_out"
	cp "$work/ffmpeg.tar.xz" "$work/x264.tar.bz2" "$work/lame.tar.gz" "$sources_out/"
	cp "$here/build.sh" "$here/pins.env" "$sources_out/"
	echo "build.sh: sources kept in $sources_out"
fi

# ffmpeg publishes a detached signature, so verify it too when a keyring is
# supplied. Without one the pinned digest is the whole trust story, which is why
# the pin ceremony asks you to cross-check it by hand.
if [ -n "${FFMPEG_GPG_KEYRING:-}" ]; then
	fetch "$ffmpeg_url.asc" "$work/ffmpeg.tar.xz.asc"
	gpgv --keyring "$FFMPEG_GPG_KEYRING" "$work/ffmpeg.tar.xz.asc" "$work/ffmpeg.tar.xz"
	echo "build.sh: ffmpeg signature verified against $FFMPEG_GPG_KEYRING"
fi

cd "$work"
tar -xf ffmpeg.tar.xz
tar -xf x264.tar.bz2
tar -xf lame.tar.gz

# One prefix for x264 and lame, so ffmpeg's configure finds both through a
# single pkg-config path.
prefix="$work/prefix"
mkdir -p "$prefix"

target_os=${target%%/*}
target_arch=${target##*/}
host_flag=""
case "$target_os/$target_arch" in
linux/amd64) host_flag="--host=x86_64-linux-gnu" ;;
linux/arm64) host_flag="--host=aarch64-linux-gnu" ;;
windows/amd64) host_flag="--host=x86_64-w64-mingw32" ;;
esac

# Windows built on a non-Windows host (the Linux release container) is a cross
# build with mingw-w64 (gcc-mingw-w64-x86-64, posix threads). On a native
# MSYS2/MinGW shell uname says MINGW/MSYS and the original path is untouched, so
# the Linux targets and the MSYS2 path behave exactly as before.
cross_windows=no
if [ "$target_os" = windows ]; then
	case "$(uname -s)" in
	MINGW* | MSYS* | CYGWIN*) ;;
	*) cross_windows=yes ;;
	esac
fi
x264_host_flag="$host_flag"
# ffmpeg's configure has no --host (that is autoconf); libx264 and LAME are found
# through the prefix they were installed into.
ffmpeg_host_flag="--extra-cflags=-I$prefix/include --extra-ldflags=-L$prefix/lib"
if [ "$cross_windows" = yes ]; then
	if ! command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
		echo "build.sh: x86_64-w64-mingw32-gcc not found; install gcc-mingw-w64-x86-64 (or use runner/build/windows/run-in-container.sh)." >&2
		exit 1
	fi
	x264_host_flag="--host=x86_64-w64-mingw32 --cross-prefix=x86_64-w64-mingw32-"
	ffmpeg_host_flag="--arch=x86_64 --target-os=mingw32 --cross-prefix=x86_64-w64-mingw32- --pkg-config=pkg-config --pkg-config-flags=--static --extra-cflags=-I$prefix/include --extra-ldflags=-L$prefix/lib --extra-ldflags=-static"
fi

export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:$prefix/lib64/pkgconfig"
make_jobs="$jobs"
if [ -z "$make_jobs" ]; then
	make_jobs=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 2)
fi

echo "build.sh: building x264"
(
	cd "x264-${X264_COMMIT}"
	# shellcheck disable=SC2086
	./configure --prefix="$prefix" --enable-static --disable-cli --disable-opencl $x264_host_flag
	make -j"$make_jobs"
	make install
)

echo "build.sh: building lame"
(
	cd "lame-${LAME_VERSION}"
	# shellcheck disable=SC2086
	./configure --prefix="$prefix" --enable-static --disable-shared --disable-frontend $host_flag
	make -j"$make_jobs"
	make install
)

echo "build.sh: building ffmpeg"
(
	cd "ffmpeg-${FFMPEG_VERSION}"
	# shellcheck disable=SC2086
	./configure --prefix="$prefix" $FFMPEG_CONFIGURE_FLAGS $ffmpeg_host_flag
	make -j"$make_jobs"
	make install
)

mkdir -p "$out"
suffix=""
if [ "$target_os" = windows ]; then
	suffix=".exe"
fi
cp "$prefix/bin/ffmpeg$suffix" "$out/ffmpeg$suffix"
cp "$prefix/bin/ffprobe$suffix" "$out/ffprobe$suffix"
chmod 0755 "$out/ffmpeg$suffix" "$out/ffprobe$suffix"

# The licence check is why this is a build script and not a tarball download:
# --enable-libx264 is GPL-only, and an ffmpeg that quietly lost it would produce
# output that differs from the cloud's, so a build that lost it fails here
# rather than shipping.
if [ "$cross_windows" = yes ]; then
	# A Windows exe cannot run here. ffmpeg embeds its configure line and the
	# library list in the binary, so the same GPL/libx264 check reads those.
	strings -a "$out/ffmpeg$suffix" | grep -m1 -- "--enable-gpl" >"$out/ffmpeg-version.txt" || true
	strings -a "$out/ffmpeg$suffix" | grep -m1 -i "x264" >>"$out/ffmpeg-version.txt" || true
	echo "build.sh: cross-built windows binary not executed; licence read from embedded strings"
else
	"$out/ffmpeg$suffix" -hide_banner -version >"$out/ffmpeg-version.txt"
fi
if ! grep -q -- "--enable-gpl" "$out/ffmpeg-version.txt"; then
	echo "build.sh: the built ffmpeg is not a GPL build; libx264 is missing and output parity is lost." >&2
	cat "$out/ffmpeg-version.txt" >&2
	exit 1
fi
if ! grep -q "libx264" "$out/ffmpeg-version.txt"; then
	echo "build.sh: the built ffmpeg has no libx264." >&2
	exit 1
fi
if [ "$cross_windows" = no ]; then
	"$out/ffmpeg$suffix" -hide_banner -L >"$out/ffmpeg-license.txt"
fi

# Behavioural guard (vfr-sync-fix): ffmpeg 8.0.x's aselect does not cut audio, which would
# leave a VideoCompile output holding the full sound against a trimmed picture (the engine
# carries the same guard in its image). One lavfi run, no network: 10 s of 48 kHz mono s16
# cut to 3 s must be ~288000 bytes, not 960000. A Windows exe cannot run here; the source
# version is the same, so the Linux target is the measurement.
if [ "$cross_windows" = no ]; then
	guard=$(mktemp)
	"$out/ffmpeg$suffix" -v error -y -f lavfi -i sine=d=10:r=48000 \
		-af 'aselect=lt(t\,3),asetpts=N/SR/TB' -f s16le -ac 1 "$guard"
	guard_bytes=$(wc -c <"$guard")
	rm -f "$guard"
	if [ "$guard_bytes" -lt 100000 ] || [ "$guard_bytes" -gt 400000 ]; then
		echo "build.sh: this ffmpeg does not cut audio with aselect ($guard_bytes bytes for 3 of 10 s; expected ~288000)." >&2
		echo "  A VideoCompile output would keep the full sound against a trimmed picture. Do not release it." >&2
		exit 1
	fi
	echo "build.sh: aselect cuts audio ($guard_bytes bytes for 3 of 10 s)"
fi

echo "build.sh: built $target"
if [ "$cross_windows" = no ]; then
	"$out/ffmpeg$suffix" -hide_banner -version | head -1
fi
echo "  $(digest_of "$out/ffmpeg$suffix")  ffmpeg$suffix"
echo "  $(digest_of "$out/ffprobe$suffix")  ffprobe$suffix"
