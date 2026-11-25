# Third-party notices

ReelBolt includes and uses third-party software, fonts and other material. Each keeps its own licence. This file is the summary and carries the notices that need a human-readable statement; the full generated lists are under [docs/third-party/](docs/third-party/).

ReelBolt itself is licensed under the GNU Affero General Public License v3.0 (see the `LICENSE` file); a commercial licence is also offered (see [COMMERCIAL.md](COMMERCIAL.md)). Each component below keeps its own licence.

## Contents

1. [Remotion](#remotion)
2. [FFmpeg and other GPL components](#ffmpeg-and-other-gpl-components)
3. [Special notices for packages](#special-notices-for-packages)
4. [Package lists](#package-lists)
5. [Fonts](#fonts)
6. [Agent skills](#agent-skills)
7. [Test data and small datasets](#test-data-and-small-datasets)
8. [Software included in container images](#software-included-in-container-images)
9. [Services started from other projects' images](#services-started-from-other-projects-images)
10. [Machine-learning models](#machine-learning-models)
    (media tools bundled with the desktop runner follow in their own section)
11. [Trademarks](#trademarks)

## Remotion

ReelBolt renders videos with [Remotion](https://www.remotion.dev) (`remotion` and `@remotion/*`, version 4.0.542, pinned in `sandbox/template/package.json`). Remotion is **not** open source: it is source-available under the [Remotion License](https://github.com/remotion-dev/remotion/blob/main/LICENSE.md). ReelBolt does not include Remotion source code; the sandbox image installs it from npm at build time.

- Remotion's Free License covers individuals, for-profit organisations of up to three people (three included), non-profits, and anyone evaluating it. An organisation of more than three people (four or more) needs a Company License. Remotion's "Remotion for Automators" option applies only to Company License users.
- Anyone who builds or runs ReelBolt is the Remotion licensee for that installation. The hosted service runs under its own Remotion licence; that does not extend to a self-hosted installation.
- ReelBolt does not let anyone upload their own Remotion project to be rendered. Remotion's terms prohibit running a service that does.
- Code that ReelBolt's agents write is Remotion code and is subject to Remotion's licence. Remotion code that is exported or downloaded must carry a "built with Remotion" notice that links to <https://www.remotion.dev/docs/license>. The notice is about the code, not about the videos it renders.

*Remotion confirmed to the project owner that this use is covered by its free licence. That is the owner's statement, not a published Remotion document, and the Remotion licence does not settle the GPL or libfdk-aac question for the container images (see below and [GPL-SOURCE-OFFER.md](GPL-SOURCE-OFFER.md)).*

The npm packages `@remotion/compositor-*` ship a prebuilt FFmpeg/FFprobe and libav* shared libraries built with `--enable-gpl` (libx264, libx265) and `--enable-libfdk-aac`. Their licence in npm metadata is the Remotion License; the FFmpeg inside is GPL.

See also the [Remotion licence section of the README](README.md#remotion-licence).

## FFmpeg and other GPL components

ReelBolt starts `ffmpeg` and `ffprobe` as separate processes. It does not link libav* libraries into its own programs. The written offer of corresponding source for every GPL program in the container images is in [GPL-SOURCE-OFFER.md](GPL-SOURCE-OFFER.md); each image carries it, the `LICENSE` and this file under `/licenses/`.

| Where | What is installed | Licence | Source |
|---|---|---|---|
| `inference` image (`inference/src/ReelBolt.Inference.Api/Dockerfile`) and `workflow-engine` image (`inference/src/ReelBolt.WorkflowEngine/Dockerfile`) | Alpine package `ffmpeg` (`apk add ffmpeg`) | GPL, as built by Alpine | Alpine package page and aports repository, links in [the image table](#software-included-in-container-images) |
| `worker` image (`sandbox/Dockerfile`, `worker` target) | Alpine package `ffmpeg` | GPL, as built by Alpine | same as above |
| `sandbox-runtime` image (`sandbox/Dockerfile`, `sandbox-runtime` target) | Debian package `ffmpeg` | GPL, as built by Debian | Debian source package `ffmpeg`, <https://sources.debian.org/src/ffmpeg/> |
| Desktop runner installers | `ffmpeg` and `ffprobe` built from FFmpeg, x264 and LAME | GPL-2.0-or-later (`--enable-gpl`, libx264, libmp3lame) | Source offer in [runner/build/ffmpeg/THIRD_PARTY_NOTICES.md](runner/build/ffmpeg/THIRD_PARTY_NOTICES.md) |
| npm packages `@remotion/compositor-*` | Prebuilt FFmpeg and libav* libraries, built with `--enable-gpl` (libx264, libx265) and `--enable-libfdk-aac` | FFmpeg parts are GPL; the npm package licence is the Remotion License | Published by Remotion; see the Remotion repository |

For the images, ReelBolt installs the distribution packages unmodified, at exact versions pinned in [infra/image-pins.env](infra/image-pins.env) (a rebuild cannot silently change the binary the offer covers). The corresponding source of the exact version in an image is the source bundle of the release (built by `infra/scripts/gpl-source-bundle.sh` from the image's package database), or the distribution's source package: the Alpine `ffmpeg` package and its build recipe in Alpine aports, and the Debian `ffmpeg` source package. `ffmpeg -version` inside the image names the version.

**Remotion's compositor in the sandbox images.** The `sandbox-runtime` and `sandbox-executor` images contain Remotion's compositor, unmodified, exactly as published in Remotion's own npm packages `@remotion/compositor-linux-x64-gnu` and `@remotion/compositor-linux-arm64-gnu`, version 4.0.542 (pinned in `sandbox/template/package-lock.json`). It is a prebuilt `ffmpeg`, `ffprobe`, the libav* shared libraries and Remotion's `remotion` binary. Its FFmpeg is built with GPL options (`--enable-gpl`, libx264, libx265) **and with libfdk-aac, which is non-free**, and Remotion's renderer maps its `aac` audio codec to that encoder. Its licence is the Remotion License (<https://github.com/remotion-dev/remotion/blob/main/LICENSE.md>, <https://www.remotion.dev/docs/license>); its source and build scripts are in Remotion's repository, <https://github.com/remotion-dev/remotion>, `packages/compositor`, at the matching tag. ReelBolt did not build or change it. ReelBolt does not claim that the Remotion licence settles the GPL or libfdk-aac questions for redistribution of these images, and anything said here about Remotion's licence is not a statement that Remotion has cleared redistribution. Anyone who runs these images needs their own Remotion licence unless the Free License applies to them (see [Remotion](#remotion): individuals, for-profit organisations of up to three people (three included), non-profits, evaluation); an organisation of more than three people (four or more) needs a Company License.

This is a named, documented exception in ReelBolt's build checks. The Dockerfile guard and `infra/scripts/check-image-publish-gate.sh` fail any other `ffmpeg` or libavcodec in any image that contains libfdk-aac or `--enable-nonfree`, and accept this compositor only at version 4.0.542 with every shipped file matching the SHA-256 values recorded in [infra/image-pins.env](infra/image-pins.env).

For the desktop runner, the complete corresponding source for each release, plus the build script and pins, is offered at the location and by the written route stated in [runner/build/ffmpeg/THIRD_PARTY_NOTICES.md](runner/build/ffmpeg/THIRD_PARTY_NOTICES.md). That file also records the release workflow step that fails a release when the sources are missing.

The `poppler-utils` package in the `import-worker` image is also GPL; see the image table.

## Special notices for packages

**Mediabunny and its encoder add-ons (MPL-2.0).** `mediabunny` 1.61.3 (used by `web/` and, through Remotion, `sandbox/template`) and `@mediabunny/aac-encoder`, `@mediabunny/flac-encoder`, `@mediabunny/mp3-encoder` 1.61.3 are Copyright Vanilagy (Mediabunny authors), licensed under the Mozilla Public License 2.0, https://www.mozilla.org/MPL/2.0/. They are used unmodified; the source is available at https://github.com/Vanilagy/mediabunny. The encoder add-ons embed WebAssembly builds of FFmpeg's AAC encoder (LGPL-2.1+), LAME (LGPL-2.0+) and libFLAC (BSD-3-Clause); only `@remotion/web-renderer` inside the render sandbox pulls the MP3 encoder in.

**sharp and libvips (Apache-2.0 and LGPL-3.0-or-later).** `sharp` 0.35.5 is Copyright Lovell Fuller and contributors (Apache-2.0). The prebuilt packages `@img/sharp-libvips-*` 1.3.4 contain libvips (LGPL-3.0-or-later, https://github.com/libvips/libvips) and its dependencies as dynamically loaded shared libraries; `@img/sharp-win32-*` and `@img/sharp-wasm32` are tagged `Apache-2.0 AND LGPL-3.0-or-later` (wasm32 also MIT). They are optional, platform-selected, used by Next.js image optimisation and icon scripts; the unmodified libraries can be replaced by the user. Source: https://github.com/lovell/sharp-libvips.

**caniuse-lite (CC-BY-4.0).** Browser-support data by Alexis Deveria / Fyrd (https://github.com/Fyrd/caniuse), licensed CC-BY-4.0, https://creativecommons.org/licenses/by/4.0/. Changes: none, bundled via Browserslist.

**SkiaSharp native assets (MIT, with bundled third-party code).** `SkiaSharp.NativeAssets.*` 3.119.2 (the Linux package ships in the `inference` image) are licensed MIT by Microsoft and bundle Skia (BSD-3-Clause, Google), HarfBuzz ("Old MIT"), ANGLE (BSD-3-Clause) and other libraries. The package licence is in [docs/third-party/SkiaSharp-LICENSE.txt](docs/third-party/SkiaSharp-LICENSE.txt). The package's own `THIRD-PARTY-NOTICES.txt` (2,716 lines, identical in the Linux, macOS and Windows native-asset packages) is reproduced verbatim, unmodified, in [docs/third-party/SkiaSharp-NativeAssets-THIRD-PARTY-NOTICES.txt](docs/third-party/SkiaSharp-NativeAssets-THIRD-PARTY-NOTICES.txt).

**RabbitMQ.Client (Apache-2.0 OR MPL-2.0).** Used under Apache-2.0.

**json-schema (AFL-2.1 OR BSD-3-Clause), opener / path-is-inside (WTFPL OR MIT), @types/* and others offering a choice.** Used under BSD-3-Clause and MIT respectively.

**argparse (Python-2.0).** Python Software Foundation License 2.0, permissive; copyright Python Software Foundation and Vitaly Puzrin.

**Packages needing no attribution (0BSD, Unlicense, CC0-1.0, MIT-0)** are still listed in section 4 for completeness.

---

## Package lists

Copyright holders and licences for every shipped dependency (runtime only; dev-only and test-only packages are excluded):

- [npm packages (1640 versions)](docs/third-party/npm.md)
- [Go modules (60 versions)](docs/third-party/go.md)
- [NuGet packages (107)](docs/third-party/nuget.md)
- [Licence texts used by those lists](docs/third-party/licences.md)

Holder provenance for npm: 1440 from the package's own licence file, 136 from the registry author field, 63 from the npm maintainer, 1 unknown. Entries read from the author field or maintainer are marked `confirm`.

## Fonts

All fonts are licensed under the SIL Open Font License, Version 1.1 (<https://openfontlicense.org>). Each family's licence text, with its copyright line, is kept beside the font files as `OFL.txt` or `OFL-<Family>.txt` in `sandbox/fonts/`, `inference/fonts/captions/`, `web/public/fonts/` and `brand/fonts/`. Source: `github.com/google/fonts` at commit `47d54ca1ebb14fd864695f1d39dc734f39017c33`. Fonts in `sandbox/fonts/` and `inference/fonts/` are unmodified; the files in `web/public/fonts/` are Latin subsets re-encoded as WOFF2 (rebuild recipe in `web/public/fonts/brand/README.md`).

| Family | Copyright holder, as stated in the family's OFL text |
|---|---|
| Anton | The Anton Project Authors (github.com/googlefonts/AntonFont) |
| Bebas Neue | Dharma Type |
| Chakra Petch | The Chakra Petch Project Authors (github.com/m4rc1e/Chakra-Petch) |
| DM Sans | The DM Sans Project Authors (github.com/googlefonts/dm-fonts) |
| DM Serif Display | Adobe, with Google LLC (Reserved Font Name stated in its OFL text) |
| Inter | The Inter Project Authors (github.com/rsms/inter) |
| JetBrains Mono | The JetBrains Mono Project Authors (github.com/JetBrains/JetBrainsMono) |
| Montserrat | The Montserrat.Git Project Authors (github.com/JulietaUla/Montserrat) |
| Playfair Display | The Playfair Display Project Authors (github.com/clauseggers/Playfair-Display), Reserved Font Name "Playfair Display" |
| Poppins | The Poppins Project Authors (github.com/itfoundry/Poppins) |
| Space Grotesk | The Space Grotesk Project Authors (github.com/floriankarsten/space-grotesk) |

Installed from the operating system's packages in container images: Liberation fonts (OFL 1.1), Noto Color Emoji (OFL 1.1), DejaVu fonts (Bitstream Vera-derived licence).

## Agent skills

### Taste Skill (design taste)

`inference/skills/taste-skill/`, `minimalist-skill/`, `brutalist-skill/`: from <https://github.com/Leonxlnx/taste-skill> at commit `717446e07a78d4e6d7918b1fde1a263378eebac9`. The `SKILL.md` files are unmodified. Each directory carries the licence text below as `LICENSE`. The `REELBOLT-PREFACE.md` file in each directory is written by ReelBolt and is not part of the upstream work.

```
MIT License

Copyright (c) 2026 Leonxlnx

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Remotion skills

`inference/skills/remotion-captions/`, `remotion-create/`, `remotion-markup/`, `remotion-multimedia/` and `remotion-render/` are written for ReelBolt and covered by this repository's licence. They describe Remotion's public API in original wording; no text from Remotion's documentation or from the `remotion-dev/skills` repository is included. `inference/skills/UPSTREAM.md` records this.

## Test data and small datasets

| Item | Source | Licence |
|---|---|---|
| `api/services/disposable_domains.txt` | `github.com/disposable-email-domains/disposable-email-domains`, revision `1aac72a0` | CC0 1.0 Universal (text in `api/services/disposable_domains.LICENSE.txt`) |
| `inference/tests/.../TestData/PdqParity/bridge-1-original.jpg` | Meta's ThreatExchange PDQ regression image (`pdq/data/reg-test-input/dih/bridge-1-original.jpg`, byte-identical, SHA-256 checked); notice in the folder's `README.md` | BSD-3-Clause, Copyright (c) Meta Platforms, Inc. and affiliates |
| Recorded SAM 3.1 responses in `TestData/prompted-region-effects/` | Output returned to ReelBolt by fal.ai for clips made by ReelBolt | Data; no third-party content |
| Other fixtures (`silence.mp3`, `testsrc2`/`sine` clips) | Generated with ffmpeg | n/a |

## Software included in container images

ReelBolt's Docker images are built on the following base images and install the following operating-system packages unmodified from their distributions. Their licences and source are those of the distribution.

| Component | Where | Licence | Source |
|---|---|---|---|
| FFmpeg (Alpine package `ffmpeg`, pinned) | `workflow-engine`, `inference` (inference-api) and `worker` images | GPL-2.0-or-later AND LGPL-2.1-or-later (APKBUILD `license=`) | <https://pkgs.alpinelinux.org/package/edge/community/x86_64/ffmpeg>, aports at <https://gitlab.alpinelinux.org/alpine/aports/-/blob/master/community/ffmpeg/APKBUILD> |
| FFmpeg (Debian package `ffmpeg`) | `sandbox-runtime` and `sandbox-executor` images | GPL (version as built by Debian; pinned) | <https://sources.debian.org/src/ffmpeg/>; source bundle of the release |
| poppler-utils | `import-worker` image | GPL-2.0-or-later (APKBUILD `license=`) | <https://pkgs.alpinelinux.org/package/edge/main/x86_64/poppler-utils>, aports at <https://gitlab.alpinelinux.org/alpine/aports/-/blob/master/main/poppler/APKBUILD> |
| Chromium `chrome-headless-shell` | `sandbox-runtime` image | Chromium licences (BSD-3 and others); notices in the directory `node_modules/.remotion/chrome-headless-shell/` of the image | Downloaded through Remotion, which fetches Chrome for Testing builds |
| Remotion and its npm dependencies | `sandbox-runtime` image | Remotion License (see [Remotion](#remotion)); each npm package under its own licence (see [npm.md](docs/third-party/npm.md)) | npm registry |
| FFmpeg inside `@remotion/compositor-*` (second FFmpeg: `ffmpeg`, `ffprobe`, libav* shared libraries) | `sandbox-runtime` and `sandbox-executor` images | GPL-2.0-or-later (`--enable-gpl`, libx264, libx265) with the non-free libfdk-aac; unmodified, version 4.0.542 (see above) | npm packages `@remotion/compositor-linux-*-gnu` in `sandbox/template/package-lock.json`; Remotion's repository at the same tag, `packages/compositor`; reference recorded in the source bundle |
| Chromium `chrome-headless-shell` (headless shell, Chrome for Testing build) | `sandbox-runtime` and `sandbox-executor` images | Chromium licences (BSD-3 and others, including some LGPL and MPL components); notices in `node_modules/.remotion/chrome-headless-shell/` of the image | Chromium source for the build version, <https://chromium.googlesource.com/chromium/src>; downloaded at image build time through Remotion |
| busybox and musl (Alpine base packages) | `api`, `site`, `web`, `mcp` (Alpine Node base), `runner-gateway`, `pool-waker`, `worker`, `docs-site`, `nginx`, `workflow-engine`, `inference`, `import-worker` images | busybox GPL-2.0-only; musl MIT | Alpine aports, <https://gitlab.alpinelinux.org/alpine/aports/-/tree/master/main/busybox>; included in the source bundle |
| Debian base packages (glibc LGPL-2.1+, bash and coreutils GPL-3.0+, and the rest of the `node:bookworm-slim` base) | `sandbox-runtime` and `sandbox-executor` images | each package's own licence, in `/usr/share/doc/<package>/copyright` | <https://sources.debian.org>; included in the source bundle |
| Docker CLI | `sandbox-executor` image | Apache-2.0 | <https://download.docker.com/linux/debian/> |
| nginx, njs | `nginx` image | BSD-2-Clause | <https://nginx.org> |
| Node.js | web, site, mcp, sandbox images | MIT | <https://nodejs.org> |
| Go toolchain (build only) | api, sandbox build stages | BSD-3-Clause | <https://go.dev> |
| .NET runtime and SDK | `inference`, `workflow-engine`, `import-worker` images | MIT | <https://github.com/dotnet> |

If you receive one of these images from us, you may obtain the corresponding source of any GPL component from the distribution links above; you may also ask for it in writing at **legal@reelbolt.ai** (see also [SUPPORT.md](SUPPORT.md)). The terms of that offer, and how to find the exact build in an image, are in [GPL-SOURCE-OFFER.md](GPL-SOURCE-OFFER.md).

## Services started from other projects' images

`docker-compose.yml` names these images. They are pulled by the person who runs the stack, from their publishers, under the publishers' licences. ReelBolt does not rebuild or modify them.

| Image | Publisher | Licence (publisher's; source) |
|---|---|---|
| `postgres:16-alpine` | PostgreSQL Global Development Group | PostgreSQL Licence (https://github.com/postgres/postgres/blob/master/COPYRIGHT) |
| `rabbitmq:3-management-alpine` | Broadcom / VMware | Mozilla Public License 2.0, some files Apache-2.0 (https://github.com/rabbitmq/rabbitmq-server/blob/main/LICENSE) |
| `dxflrs/garage` | Deuxfleurs | GNU AGPL-3.0 (https://git.deuxfleurs.fr/Deuxfleurs/garage/src/branch/main-v1/LICENSE) |
| `qdrant/qdrant` | Qdrant | Apache-2.0 (https://github.com/qdrant/qdrant/blob/master/LICENSE) |
| `ghcr.io/speaches-ai/speaches` | speaches-ai | MIT (https://github.com/speaches-ai/speaches/blob/master/LICENSE) |
| `ghcr.io/huggingface/text-embeddings-inference` | Hugging Face | Apache-2.0 (https://github.com/huggingface/text-embeddings-inference/blob/main/LICENSE) |
| `caddy` | Caddy | Apache-2.0 (https://github.com/caddyserver/caddy/blob/master/LICENSE) |
| `grafana/grafana` (opt-in profile) | Grafana Labs | GNU AGPL-3.0 (https://github.com/grafana/grafana/blob/main/LICENSE) |
| `willfarrell/autoheal` | Will Farrell | MIT (https://github.com/willfarrell/docker-autoheal/blob/main/LICENSE) |

Images the operator supplies themselves (`COMFYUI_IMAGE`, `DECISION_IMAGE`) are the operator's responsibility.

## Machine-learning models

ReelBolt does not include or redistribute model weights. Optional local services download weights from their publishers on first use. Models the documentation or default configuration refers to:

| Model | Used for | Licence (publisher's; source) |
|---|---|---|
| Qwen3-Embedding-0.6B (Alibaba Cloud) | Local text embeddings | Apache-2.0 (https://huggingface.co/Qwen/Qwen3-Embedding-0.6B) |
| Whisper models via faster-whisper | Local transcription | faster-whisper: MIT (<https://github.com/SYSTRAN/faster-whisper/blob/master/LICENSE>); the model weights you download follow their own model card |
| Kokoro-82M | Optional local speech synthesis | Apache-2.0 (https://huggingface.co/hexgrad/Kokoro-82M) |
| Wan 2.2 TI2V-5B | Optional self-hosted video generation (ComfyUI) | Apache-2.0 (https://huggingface.co/Wan-AI/Wan2.2-TI2V-5B) |
| Fish Audio models (self-hosted) | Optional speech and voice cloning | Fish Audio Community License Agreement v1.0 (the `fish-speech` repository licence, <https://github.com/fishaudio/fish-speech/blob/main/LICENSE>); the model cards list CC-BY-NC-SA-4.0. The Community License allows research and non-commercial use, and commercial use only below its revenue threshold; anything beyond that, and any hosted service, needs a separate licence from Fish Audio. Read both texts before use. Fish Audio's hosted API is a different service under its own terms. |
| Meta SAM 3 / 3.1 | Segmentation, called through fal.ai | Meta's SAM License (the Hugging Face model card lists the licence as `other`: <https://huggingface.co/facebook/sam3>), applied by the vendor; no weights included |
| FLUX.1 [dev] family | Called through the Black Forest Labs hosted API only | Weights are non-commercial (FLUX.1 [dev] Non-Commercial License: https://huggingface.co/black-forest-labs/FLUX.1-dev); ReelBolt never self-hosts them |

Hosted AI services (Anthropic, OpenAI, Google, DeepSeek, MiniMax, Higgsfield, fal.ai, Black Forest Labs, Fish Audio) are used with the operator's own accounts and are governed by those providers' terms.

## Media tools bundled with the desktop runner

The desktop runner's installers include `ffmpeg` and `ffprobe` built from FFmpeg 7.1, x264 (commit `b35605ace3ddf7c1a5d67a2eb553f034aef41d55`) and LAME 3.100. The build is licensed GPL-2.0-or-later (configured with `--enable-gpl`, `--enable-libx264`, `--enable-libmp3lame`; no non-free components). The tools run as separate processes and are not linked into any ReelBolt program. Full text, source offer and patent remarks: `runner/build/ffmpeg/THIRD_PARTY_NOTICES.md`. The complete corresponding source for each release is offered in that file, which states where the source is published and the written route to request it. The release workflow (`.github/workflows/runner-release.yml`) is built to fail a release whose source files are missing. The contact for a written request is **legal@reelbolt.ai** (see also [SUPPORT.md](SUPPORT.md)).

## Camera log profiles

<!-- counsel review: batched -->
The transfer curves and gamut matrices behind the camera log profiles are implemented in our own code from the publicly available technical documents of Apple, DJI, Sony, Panasonic, Canon, ARRI, Fujifilm, Nikon and RED:

- Apple: `IDT.Apple.AppleLog_BT2020.ctl`, a vendor-supplied transform in the Academy's ACES `aces-dev` project. Copyright © 2015 Academy of Motion Picture Arts and Sciences (A.M.P.A.S.). Portions contributed by others as indicated. All rights reserved. Academy Color Encoding System (ACES) software and tools are provided by the Academy under the following terms and conditions: A worldwide, royalty-free, non-exclusive right to copy, modify, create derivatives, and use, in source and binary forms, is hereby granted, subject to acceptance of this license.
- DJI: *White Paper on D-Log and D-Gamut of DJI Cinema Color System*, Rev. 1.0.
- Sony: *Technical Summary for S-Gamut3.Cine/S-Log3 and S-Gamut3/S-Log3*.
- Panasonic: *V-Log/V-Gamut REFERENCE MANUAL*, Rev. 1.0.
- Canon: *Description of the Canon Log, Canon Log 2 and Canon Log 3 Gamma Curves*.
- ARRI: *ALEXA Log C Curve - Usage in VFX* and *ARRI LogC4 Logarithmic Color Space Specification*.
- Fujifilm: *F-Log Data Sheet* and *F-Log2 Data Sheet*.
- Nikon: *N-Log Specification Document*, Version 1.0.0.
- RED: *White Paper on REDWideGamutRGB and Log3G10*.

No manufacturer LUT file is included. Product names are trademarks of their owners and are used descriptively.

## Trademarks

"ReelBolt", the ReelBolt logo and the artwork in `brand/` are not licensed for reuse by this repository's licence. Other names (Remotion, FFmpeg, Docker, PostgreSQL, and so on) belong to their owners.

## Sample media

The two short cat clips under `inference/tests/ReelBolt.WorkflowEngine.Tests/TestData/prompted-region-effects/sam31-rle/clips/` are AI-generated test inputs. They were generated with MiniMax (model as recorded in the provenance file next to them: `MiniMax-H3-Max`). They contain no real person or animal and are provided under this repository's licence. MiniMax's own terms for generated output were not independently verified by the project when these files were published; if you are a rights holder or from MiniMax and have a concern, contact legal@reelbolt.ai.
