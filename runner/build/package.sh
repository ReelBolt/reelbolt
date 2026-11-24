#!/bin/sh
# Package one built runner into the artifacts a release publishes.
#
#   package.sh --target linux/amd64 --version 1.4.2 --dist <dir> --out <dir> \
#              [--formats binary,archive,appimage,deb]
#
# --dist holds what the build produced:
#
#   reelbolt-runner      the binary (reelbolt-runner.exe on Windows)
#   ffmpeg, ffprobe      the pinned media tools (see ../ffmpeg/build.sh)
#   ffmpeg-license.txt   written by ../ffmpeg/build.sh, shipped in every artifact
#   THIRD_PARTY_NOTICES.md
#
# TWO KINDS OF OUTPUT, and they are not the same thing:
#
#   * the INSTALLERS a human downloads (the .tar.gz, the AppImage, the .deb);
#   * the UPDATE PAYLOAD the signed manifest describes, which is what a runner
#     that is already installed downloads and applies.
#
# The manifest payloads are dictated by how runner/internal/update applies them,
# not by what is convenient here:
#
#   appimage  the whole AppImage, swapped for the running one
#   deb       the real .deb: verified, then the user is told to upgrade (never
#             installed behind apt)
#   tarball   THE RAW EXECUTABLE, not a tar.gz. applyReplace() copies the staged
#             file byte for byte over the installed binary, so an archive here
#             would leave a gzipped file where the executable was. The format
#             name comes from runner/internal/update/manifest.go and cannot be
#             changed from this WP; see ../README.md.
#
# It also emits the raw executable as a plain file for the tarball entry, which
# is why the file names matter: reelbolt-manifest refuses a name that is not
# URL-safe, so every name here is lowercase ASCII with dots, dashes and
# underscores.
#
# WP E10b. The recipes are in this directory: nfpm.yaml, AppRun,
# reelbolt-runner.desktop, reelbolt-runner-wrapper, and ../windows/runner.wxs.

set -eu

here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)

target=""
version=""
dist=""
out=""
formats=""

usage() {
	cat <<'USAGE'
usage: package.sh --target <os>/<arch> --version <v> --dist <dir> --out <dir>
                  [--formats binary,archive,appimage,deb]

  --target   linux/amd64, linux/arm64 or windows/amd64
  --version  the release version, dotted (1.4.2)
  --dist     directory holding the built binary and the bundled ffmpeg/ffprobe
  --out      directory the artifacts are written to
  --formats  comma-separated; default: every format this target supports
             (linux: binary,archive,appimage,deb)
USAGE
}

while [ $# -gt 0 ]; do
	case "$1" in
	--target)
		target=${2:-}
		shift 2
		;;
	--version)
		version=${2:-}
		shift 2
		;;
	--dist)
		dist=${2:-}
		shift 2
		;;
	--out)
		out=${2:-}
		shift 2
		;;
	--formats)
		formats=${2:-}
		shift 2
		;;
	-h | --help)
		usage
		exit 0
		;;
	*)
		echo "package.sh: unknown argument $1" >&2
		usage >&2
		exit 2
		;;
	esac
done

for pair in "target:$target" "version:$version" "dist:$dist" "out:$out"; do
	name=${pair%%:*}
	value=${pair#*:}
	if [ -z "$value" ]; then
		echo "package.sh: --$name is required" >&2
		usage >&2
		exit 2
	fi
done
case "$version" in
[0-9]*.[0-9]*.[0-9]*) ;;
*)
	echo "package.sh: --version $version is not a dotted version (1.4.2)" >&2
	exit 2
	;;
esac

os=${target%%/*}
arch=${target##*/}
case "$os/$arch" in
linux/amd64 | linux/arm64 | windows/amd64) ;;
*)
	echo "package.sh: $target is not a release target. D26 ships Windows x64 and Linux x64/arm64 only; macOS is deferred." >&2
	exit 2
	;;
esac

if [ "$os" = windows ]; then
	echo "package.sh: the Windows installers are built on Linux by ../windows/package.sh (see ../windows/build-binaries.sh)." >&2
	exit 2
fi

if [ -z "$formats" ]; then
	formats="binary,archive,appimage,deb"
fi

exe="$dist/reelbolt-runner"
for f in "$exe" "$dist/ffmpeg" "$dist/ffprobe"; do
	if [ ! -f "$f" ]; then
		echo "package.sh: $f is missing; run make/build first (--dist must hold the built tree)" >&2
		exit 1
	fi
done
mkdir -p "$out"
out=$(CDPATH='' cd -- "$out" && pwd)

name="reelbolt-runner-$version-$os-$arch"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT INT TERM

has_format() {
	case ",$formats," in
	*",$1,"*) return 0 ;;
	*) return 1 ;;
	esac
}

# ---- binary (the manifest payload for the "tarball" format) -----------------
#
# The raw executable, byte for byte what the runner will copy over itself. This
# is the file the manifest entry with format "tarball" must point at.
if has_format binary; then
	cp "$exe" "$out/$name"
	chmod 0755 "$out/$name"
	echo "package.sh: $out/$name (raw executable: the manifest tarball payload)"
fi

# ---- archive (the installer a human downloads) ------------------------------
#
# A user installs this by hand. It is NOT a manifest artifact: a manifest
# "tarball" entry is the raw executable above.
#
# NOTE for later: the self-update swaps the binary and leaves the bundled ffmpeg
# in place. A release that changes ffmpeg therefore needs the archive (or the
# installer) reinstalled by hand; the manifest cannot express "also replace
# ffmpeg" and this package does not pretend otherwise.
if has_format archive; then
	mkdir -p "$stage/$name"
	cp "$exe" "$dist/ffmpeg" "$dist/ffprobe" "$stage/$name/"
	cp "$here/ffmpeg/THIRD_PARTY_NOTICES.md" "$stage/$name/"
	if [ -f "$dist/ffmpeg-license.txt" ]; then
		cp "$dist/ffmpeg-license.txt" "$stage/$name/"
	fi
	cat >"$stage/$name/README.txt" <<'TXT'
ReelBolt Runner -- manual install

  1. Put this directory somewhere you own, for example ~/.local/opt/reelbolt-runner
  2. Put it on your PATH:
       export PATH="$HOME/.local/opt/reelbolt-runner:$PATH"
  3. reelbolt-runner pair
  4. reelbolt-runner run

The runner looks for ffmpeg and ffprobe as programs named "ffmpeg" and "ffprobe"
ON YOUR PATH. The copies in this directory are the versions ReelBolt releases
with, so keeping this directory ahead of the system's is what makes a job here
produce the same output as the same job in the cloud.

Updates: a copy in a directory you own updates itself from the signed manifest
(reelbolt-runner update --check). See docs at https://reelbolt.ai/runner.

ffmpeg and ffprobe are separate GPL programs, invoked as subprocesses; this
product does not link them. See THIRD_PARTY_NOTICES.md in this directory.
TXT
	tar -C "$stage" -czf "$out/$name.tar.gz" "$name"
	echo "package.sh: $out/$name.tar.gz"
fi

# ---- AppImage ---------------------------------------------------------------
#
# The AppImage is the format that self-updates in place: the runner detects it
# through $APPIMAGE (runner/internal/update/apply.go) and swaps the whole image.
if has_format appimage; then
	if ! command -v appimagetool >/dev/null 2>&1; then
		echo "package.sh: appimagetool is not installed; ci/linux-inside.sh installs the release pinned in ci/pins.env" >&2
		exit 1
	fi
	appdir="$stage/$name.AppDir"
	mkdir -p "$appdir/usr/bin"
	cp "$exe" "$appdir/usr/bin/reelbolt-runner"
	# The tray and the window. AppRun execs exactly this path, so an AppImage
	# built without it mounts, runs AppRun, and dies on "not found" — which is
	# what the Linux artifacts did until this copy was added.
	cp "$dist/reelbolt-runner-desktop" "$appdir/usr/bin/reelbolt-runner-desktop"
	cp "$dist/ffmpeg" "$appdir/usr/bin/ffmpeg"
	cp "$dist/ffprobe" "$appdir/usr/bin/ffprobe"
	cp "$here/linux/AppRun" "$appdir/AppRun"
	chmod 0755 "$appdir/AppRun"
	cp "$here/linux/reelbolt-runner.desktop" "$appdir/reelbolt-runner.desktop"
	cp "$here/../assets/icons/reelbolt-runner-256.png" "$appdir/reelbolt-runner.png"
	cp "$here/ffmpeg/THIRD_PARTY_NOTICES.md" "$appdir/"
	# ARCH must be exported for appimagetool: it refuses to guess the
	# architecture of an image it is asked to build.
	case "$arch" in
	amd64) export ARCH=x86_64 ;;
	arm64) export ARCH=aarch64 ;;
	esac
	# --no-appstream: this build has no AppStream metadata, and appimagetool
	# otherwise spends a network round trip trying to validate that it has none.
	#
	# The AppImage is deliberately NOT GPG-signed: the runner cannot check an
	# embedded signature itself, so the manifest's Ed25519 signature (plus the
	# SHA256SUMS and the optional cosign bundle) is the trust chain. See README.md.
	(cd "$stage" && appimagetool --no-appstream "$appdir" "$out/$name.AppImage")
	echo "package.sh: $out/$name.AppImage"
fi

# ---- deb (nfpm) -------------------------------------------------------------
#
# A .deb install is package-managed: the runner never replaces a file dpkg owns,
# it verifies the release and tells the user to upgrade (apply.go,
# isPackageManaged). apt is the thing that installs it.
if has_format deb; then
	if ! command -v nfpm >/dev/null 2>&1; then
		echo "package.sh: nfpm is not installed; ci/linux-inside.sh installs the version pinned in ci/pins.env" >&2
		exit 1
	fi
	debroot="$stage/deb"
	mkdir -p "$debroot"
	cp "$exe" "$debroot/reelbolt-runner"
	# The tray and the window, and the wrapper that puts it on PATH, so the
	# package's desktop entry launches something. Both are staged here because
	# nfpm.yaml places them (and claims the webview dependency they need).
	cp "$dist/reelbolt-runner-desktop" "$debroot/reelbolt-runner-desktop"
	cp "$here/linux/reelbolt-runner-desktop-wrapper" "$debroot/reelbolt-runner-desktop-wrapper"
	cp "$dist/ffmpeg" "$dist/ffprobe" "$debroot/"
	cp "$here/linux/reelbolt-runner-wrapper" "$debroot/reelbolt-runner-wrapper"
	cp "$here/linux/reelbolt-runner.desktop" "$debroot/"
	cp "$here/../assets/icons/reelbolt-runner-256.png" "$debroot/reelbolt-runner.png"
	cp "$here/ffmpeg/THIRD_PARTY_NOTICES.md" "$debroot/"
	if [ -f "$dist/ffmpeg-license.txt" ]; then
		cp "$dist/ffmpeg-license.txt" "$debroot/"
	fi
	# nfpm substitutes ${VERSION} and ${ARCH} from the environment, but NOT
	# ${DEBROOT} inside a `src` glob — so nfpm runs FROM the staged tree and
	# linux/nfpm.yaml names its files relative to this directory. (--config and
	# --target are absolute, so the change of directory is safe.)
	(cd "$debroot" && VERSION="$version" ARCH="$arch" OUTDIR="$out" \
		nfpm package --config "$here/linux/nfpm.yaml" --packager deb --target "$out/")
	# nfpm writes a prerelease's "-" as "~" in the file name (1.4.2-beta.1 ->
	# 1.4.2~beta.1) and reelbolt-manifest refuses "~" in an artifact name. The file
	# name is cosmetic to dpkg (the version lives in the control file), so rename.
	for f in "$out"/reelbolt-runner_*~*_"$arch".deb; do
		[ -f "$f" ] || continue
		mv "$f" "$(printf '%s' "$f" | sed 's/~/-/')"
	done
	echo "package.sh: $out (deb)"
fi

echo "package.sh: done -- staged artifacts are the ones the manifest will describe"
