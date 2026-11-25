# Written offer of corresponding source (GPL)

ReelBolt's own code is licensed under the AGPL-3.0-only (see [LICENSE](LICENSE)). Some ReelBolt container images also contain programs that other people wrote and that are licensed under the GNU General Public License (GPL), version 2 or later, or under the LGPL. This file is the written offer the GPL requires for those programs. It covers every ReelBolt container image published to a public registry.

The programs, and the images that carry them:

| Program | Version | Image |
|---|---|---|
| FFmpeg, `ffmpeg` and `ffprobe` (Debian package `ffmpeg`, built with GPL components such as libx264) | pinned in [infra/image-pins.env](infra/image-pins.env), `FFMPEG_DEBIAN_VERSION` | `reelbolt-sandbox-runtime`, `reelbolt-sandbox-executor` |
| FFmpeg, `ffmpeg` and `ffprobe` (Alpine package `ffmpeg`) | `FFMPEG_ALPINE_VERSION` | `reelbolt-workflow-engine`, `reelbolt-inference-api`, `reelbolt-worker` |
| poppler-utils (`pdftoppm`, `pdftotext`, ...; Alpine package `poppler-utils`) | `POPPLER_ALPINE_VERSION` | `reelbolt-import-worker` |
| FFmpeg inside the npm packages `@remotion/compositor-*` (prebuilt by Remotion) | `REMOTION_VERSION` | `reelbolt-sandbox-runtime`, `reelbolt-sandbox-executor` |
| The operating-system base packages of each image (musl, busybox, libraries, and so on) | as installed in the image | every image |

ReelBolt runs `ffmpeg`, `ffprobe` and the poppler tools as separate programs and does not link them into its own code. ReelBolt installs the Debian and Alpine packages unmodified.

## The offer

For at least three (3) years from the date we last distributed an image that contains one of these programs, we will give anyone who asks the complete corresponding source code of those programs, as they are in that image, on a medium customarily used for software interchange, for a charge no more than the cost of physically performing the distribution. You may also simply download it, as described below.

To ask, write to **legal@reelbolt.ai** and name the image, tag or digest. The address currently forwards to ReelBolt's maintainer.

This offer is made to anyone who received the image, not only to the person who got it from us. It does not change your rights under any licence.

## How to get the source without asking

Each image release has a source bundle: a tarball with the source packages of every package installed in that image, at the exact installed versions, a manifest, and the reference for the Remotion compositor FFmpeg. It is attached to the GitHub release of the same version, next to its SHA-256 file. The release assets are named `reelbolt-source-<image>-<digest-prefix>.tar.gz` and `.sha256`.

To rebuild a bundle yourself for an image you hold:

```
infra/scripts/gpl-source-bundle.sh ghcr.io/<owner>/reelbolt-<image>@sha256:<digest>
```

The script reads the package database of that exact image, then downloads the matching Debian source packages (`apt-get source`) or Alpine package sources (the `APKBUILD` and the files it names) at those versions.

## How to find the exact build in an image

- Container images carry this file, the `LICENSE` and `THIRD_PARTY_NOTICES.md` in `/licenses/`.
- `docker run --rm --entrypoint ffmpeg <image> -version` prints the FFmpeg version; add `-L` for the licence and `-buildconf` for the build options. The Debian or Alpine package version is `dpkg-query -W ffmpeg` or `apk info -v ffmpeg` inside the image.
- The image labels `org.opencontainers.image.revision` and `.version` name the ReelBolt commit and release the image was built from; `org.opencontainers.image.source` names the repository, whose [Dockerfiles](sandbox/Dockerfile) hold the pinned versions.
- Debian sources: <https://sources.debian.org/src/ffmpeg/>. Alpine sources: <https://gitlab.alpinelinux.org/alpine/aports>.
- The FFmpeg in `@remotion/compositor-*`: the version is in `node_modules/@remotion/compositor-*/package.json` inside the sandbox images. Remotion builds it from the FFmpeg sources and the build scripts in its repository at the matching tag, <https://github.com/remotion-dev/remotion>, `packages/compositor`. The bundle records that reference and the FFmpeg version the binary reports.

## What we ship from Remotion

The sandbox images contain Remotion's compositor, unmodified, taken from Remotion's own npm packages `@remotion/compositor-linux-x64-gnu` and `@remotion/compositor-linux-arm64-gnu`, version 4.0.542 (see `REMOTION_VERSION` in [infra/image-pins.env](infra/image-pins.env)). It includes an FFmpeg built with GPL options (libx264, libx265) and with libfdk-aac, which is non-free. ReelBolt did not build it and cannot offer more than a reference for its source: Remotion's repository, <https://github.com/remotion-dev/remotion>, `packages/compositor`, at the tag for that version, and the FFmpeg release named in the bundle's `COMPOSITOR-FFMPEG.md`. Its licence is the Remotion License, <https://www.remotion.dev/docs/license>. This offer does not claim that the Remotion licence resolves the GPL or libfdk-aac questions, or that Remotion has cleared redistribution; anyone running these images needs their own Remotion licence unless they are covered by Remotion's Free License (for-profit organisations of up to three people, three included; an organisation of more than three people, four or more, is not).

## Desktop runner

The desktop runner's FFmpeg is a different build, and its offer is in [runner/build/ffmpeg/THIRD_PARTY_NOTICES.md](runner/build/ffmpeg/THIRD_PARTY_NOTICES.md).

## Other notices

The full list of third-party software is in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
