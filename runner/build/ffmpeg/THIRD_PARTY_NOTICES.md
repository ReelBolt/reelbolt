# Third-party notices - the bundled media tools

This directory builds the `ffmpeg` and `ffprobe` binaries that ship inside the
ReelBolt desktop runner's installers. They are the only GPL components in the
product, and they are shipped deliberately: the runner needs libx264 and
libmp3lame so that a render or a compile performed on a user's machine produces
the same bytes as the same job performed in the cloud (see
`docs/design/desktop-runner-adr.md`, decision 4).

## How they are used

ReelBolt invokes `ffmpeg` and `ffprobe` as **separate processes** over argv. No
libav\* library is linked into `reelbolt-runner`, into any other ReelBolt
binary, or into anything ReelBolt distributes. This is the aggregation boundary
the GPL draws, and it is what makes bundling a GPL tool next to a proprietary
program permissible.

## What is bundled

| Component | Version | Licence | Source |
| --- | --- | --- | --- |
| FFmpeg | pinned in `pins.env` (`FFMPEG_VERSION`) | GPL-2.0-or-later, because `--enable-gpl` is required for libx264 | <https://ffmpeg.org/releases/> |
| x264 | pinned in `pins.env` (`X264_COMMIT`) | GPL-2.0-or-later | <https://code.videolan.org/videolan/x264> |
| LAME (libmp3lame) | pinned in `pins.env` (`LAME_VERSION`) | LGPL-2.0-or-later | <https://lame.sourceforge.io/> |

The exact versions of a released runner are recorded in that release's build
log and in the `ffmpeg-version.txt` and `ffmpeg-license.txt` files the build
writes next to the binaries: `build.sh` refuses to finish unless the build it
produced reports `--enable-gpl` and lists `libx264`, so a released binary's
licence text is a fact produced by the build, not a claim in this file.

## Source offer

The complete corresponding source for the exact binaries in a release is
published, unmodified, at:

    https://downloads.reelbolt.ai/runner/source/

with one directory per released runner version:

    https://downloads.reelbolt.ai/runner/source/<version>/ffmpeg-<version>.tar.xz
    https://downloads.reelbolt.ai/runner/source/<version>/x264-<commit>.tar.bz2
    https://downloads.reelbolt.ai/runner/source/<version>/lame-<version>.tar.gz
    https://downloads.reelbolt.ai/runner/source/<version>/build.sh
    https://downloads.reelbolt.ai/runner/source/<version>/pins.env

These are the upstream tarballs exactly as downloaded, byte for byte, plus the
build script and its pins that produced the binaries. Corresponding source is
also available in writing: write to legal@reelbolt.ai and we will send it on
a physical medium for no more than the cost of the medium and postage, valid
for three years from the date the release was published.

These files are uploaded by `runner/build/publish.sh`, run by the `publish` job in
`.github/workflows/runner-release.yml`, from the tarballs `build.sh` verified during
the build itself - not re-downloaded afterwards, so the sources published are the
ones the binaries were built from. They are uploaded **before** the signed manifest
and `latest.json` go live, so a release is never findable without its sources, and
the script **fails the release** when they are missing: an offer that resolves to nothing
is a licence violation rather than a documentation bug.

## Windows: the statically linked runtime

The Windows `ffmpeg.exe` and `ffprobe.exe` are cross-built on Linux with mingw-w64
(gcc, POSIX threads) and linked statically, so they carry pieces of the toolchain's
runtime in addition to ffmpeg, x264 and LAME:

* the mingw-w64 C runtime and headers (permissive licences, ZPL 2.1 / public domain);
* winpthreads, mingw-w64's POSIX threads library (MIT/BSD-style licence);
* libgcc (and libstdc++ if linked), under the GCC Runtime Library Exception,
  which lets the combined work be distributed under the GPL of the whole.

The runtime comes from the Ubuntu 24.04 archive packages `gcc-mingw-w64-x86-64`,
`g++-mingw-w64-x86-64` and `binutils-mingw-w64-x86-64` installed in the toolchain
image. The base image is pinned by digest but the apt packages are not pinned to
a version; `windows/build-binaries.sh` therefore prints the installed package
versions (Ubuntu archive versions, not pinned) into the build log of every release, and the matching source is
available from Ubuntu (and Debian) and, in writing, on the terms above. To check what a given binary actually
imports, run `x86_64-w64-mingw32-objdump -p ffmpeg.exe | grep "DLL Name"` inside
the toolchain container: only Windows system DLLs should be listed.

## What is NOT bundled

* The sandbox runtime image (`sandbox/Dockerfile`, `sandbox-runtime` target)
  carries the Debian `ffmpeg` package. That image is distributed on its own and
  its licence obligations are its own; see `docs/sandbox-service.md`.
* Nothing here covers the hosted ReelBolt service, which does not distribute
  these binaries.

## H.264 and AAC patent pools

libx264 and the AAC encoder are covered by patent pools (MPEG LA / Via LA) in
some jurisdictions. This is flagged for legal review in the desktop-runner ADR
and does not block the beta; it is recorded here so that the obligation is
visible next to the binaries rather than only in a plan.
