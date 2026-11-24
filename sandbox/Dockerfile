# Licence files that every published image carries under /licenses/ (GPL-SOURCE-OFFER.md says so).
# CI and release supply the repository root as the named build context `legal`
# (`--build-context legal=.`), which replaces this stage; a plain `docker build` without it gets
# placeholder files, and infra/scripts/check-image-publish-gate.sh rejects an image that has them.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM golang:1.27-alpine AS gobuild
WORKDIR /src

RUN apk add --no-cache ca-certificates git

ARG GOPROXY=https://proxy.golang.org,direct
ARG GOSUMDB=sum.golang.org
ENV GOPROXY=${GOPROXY}
ENV GOSUMDB=${GOSUMDB}

# go.sum is needed for every dependency to be verified; the gateway added the
# module's first external ones (pgx, coder/websocket, prometheus).
COPY go.mod go.sum ./
RUN set -eux; \
		for i in 1 2 3 4 5; do \
			go mod download && break; \
			echo "go mod download failed (attempt ${i}), retrying..."; \
			sleep $((i * 3)); \
		done

COPY . .

ARG VERSION=docker
RUN CGO_ENABLED=0 go build -ldflags="-s -w -X main.Version=${VERSION}" -o /app/sandbox-executor ./cmd/sandbox-executor
RUN CGO_ENABLED=0 go build -ldflags="-s -w -X main.Version=${VERSION}" -o /app/runner-gateway ./cmd/runner-gateway
RUN CGO_ENABLED=0 go build -ldflags="-s -w -X main.Version=${VERSION}" -o /app/reelbolt-worker ./cmd/reelbolt-worker
RUN CGO_ENABLED=0 go build -ldflags="-s -w -X main.Version=${VERSION}" -o /app/reelbolt-scaler ./cmd/reelbolt-scaler


# ─────────────────────────────────────────────────────────────────────────────
# sandbox-runtime — the image untrusted, model-authored code actually runs in.
#
# Everything in this stage is reachable by an attacker: `npm run build` executes
# whatever the caller wrote into package.json, so treat this filesystem as
# adversary-visible. It therefore contains only what Remotion needs to render:
# node, the Chrome shared libraries, ffmpeg, fonts, and the template.
#
# Deliberately NOT present here (all of which the control-plane stage adds on
# top): the Docker CLI, curl, and gnupg. A Docker client inside the
# untrusted container is a standing invitation — the moment any daemon endpoint
# becomes reachable it is root on the host — and it buys the render path nothing.
# ─────────────────────────────────────────────────────────────────────────────
FROM node:26-bookworm-slim AS sandbox-runtime

# Exact Debian ffmpeg build (infra/image-pins.env; tests keep the two equal). GPL-SOURCE-OFFER.md
# offers the source of THIS version, so a rebuild must not pick up a different one: the install
# fails if the distro no longer carries it, and the guard below re-checks what was installed.
ARG FFMPEG_DEBIAN_VERSION=7:5.1.9-0+deb12u1

RUN apt-get update && apt-get install -y --no-install-recommends \
	ca-certificates \
	ffmpeg=${FFMPEG_DEBIAN_VERSION} \
	# Chrome dependencies
	libasound2 \
	libatk-bridge2.0-0 \
	libatk1.0-0 \
	libcups2 \
	libdbus-1-3 \
	libdrm2 \
	libgbm1 \
	libgtk-3-0 \
	libnspr4 \
	libnss3 \
	libx11-xcb1 \
	libxcomposite1 \
	libxdamage1 \
	libxfixes3 \
	libxkbcommon0 \
	libxrandr2 \
	xdg-utils \
	fonts-liberation \
	fonts-noto-color-emoji \
	&& rm -rf /var/lib/apt/lists/*

# Publication guard (GPL source offer): the installed ffmpeg is exactly the pinned version, and its
# build is free: no libfdk_aac encoder and no --enable-nonfree (a nonfree FFmpeg is not
# redistributable at all). Fails the build otherwise.
RUN set -eu; \
	v="$(dpkg-query -W -f='${Version}' ffmpeg)"; \
	test "$v" = "$FFMPEG_DEBIAN_VERSION" || { echo "ffmpeg is $v, pin is $FFMPEG_DEBIAN_VERSION" >&2; exit 1; }; \
	for b in ffmpeg ffprobe; do \
		out="$("$b" -hide_banner -buildconf 2>&1)$("$b" -hide_banner -encoders 2>&1)"; \
		case "$out" in *libfdk*|*nonfree*) echo "$b is not free: libfdk-aac or nonfree" >&2; exit 1;; esac; \
	done

# Build-time guard (vfr-sync-fix): ffmpeg 8.0.x's aselect does not cut audio, so a VideoCompile
# output would keep the full audio against a trimmed video. One lavfi run, no network: 10 s of
# 48 kHz mono s16 audio cut to 3 s must be ~288000 bytes, not 960000. Behavioural rather than a
# version pin, so it holds when the distro retires a package: 7.0.2, 7.1 and 8.1.3 pass, 8.0.1 fails.
RUN f="$(mktemp)" \
 && ffmpeg -v error -y -f lavfi -i sine=d=10:r=48000 -af 'aselect=lt(t\,3),asetpts=N/SR/TB' -f s16le -ac 1 "$f" \
 && n="$(wc -c < "$f")" && rm -f "$f" \
 && test "$n" -gt 100000 && test "$n" -lt 400000

# Bundled OFL font set (sandbox/fonts/, pinned upstream commit in fonts/UPSTREAM.md). Renders
# resolve these by family name locally; nothing is fetched from the network at render time.
# The manifest and licence files are copied too: harmless, and they keep provenance with the
# installed fonts.
COPY fonts/ /usr/share/fonts/truetype/reelbolt/
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
RUN fc-cache -f

WORKDIR /opt/remotion-template
COPY template/package.json template/package-lock.json ./
# Supply chain: exact versions from the committed lockfile (integrity hashes), no install scripts
# run. The build fails if any locked package declares an install script that is not on this
# reviewed list. esbuild: its postinstall only validates/optimizes the platform binary that the
# optional @esbuild/<platform> package already ships; it works without the script.
RUN npm ci --ignore-scripts --no-audit --no-fund \
	&& node -e "const ok=new Set(['node_modules/esbuild']);const bad=Object.entries(require('./package-lock.json').packages).filter(([k,v])=>v.hasInstallScript&&!ok.has(k)).map(([k])=>k);if(bad.length){console.error('unreviewed install scripts: '+bad.join(', '));process.exit(1)}"
COPY template/ ./

# Publication guard for the prebuilt FFmpeg inside @remotion/compositor-* (GPL source offer). Remotion
# builds it with --enable-libfdk-aac (non-free). It is a DOCUMENTED, NAMED exception, accepted only as
# Remotion publishes it: the package version equals the Remotion version, and every shipped binary hashes
# to the value recorded in infra/image-pins.env (the ARG defaults below; tests keep them equal). A
# compositor with libfdk/nonfree that differs in version or in any file fails the build, and so does any
# libav*/ffmpeg-like file in the compositor directory that is not in the pin list (a versioned
# libavcodec.so.N next to the pinned files, for example). A compositor
# without libfdk passes without the hashes. The pinned hashes are the ARG defaults below.
ARG COMPOSITOR_SHA256_LINUX_X64_GNU="ffmpeg=461169f4f3286904fbfa18212873073869b03c29ebbe99f415c50fdc7dd41113 ffprobe=3bad619e6da4abbd8fc13f46b08e869c28f38852a8a4f168579eef23b96eae1d remotion=987b641ee4a175b596da1b1fd5a46cf460471869b98c59facfa4199f2d00a1f5 libavcodec.so=c18bb207c02406639798398f8a9772e901fba2b86f2ea6ac414093168196216f libavdevice.so=1bf20339b8c1785730ae47cc31410e32beb39a6a00527a8fb14ab78b1b60c174 libavfilter.so=a0cb96a34f6884eb24741e925c0e3a6994ebe44a20fd197560e6773706f618a6 libavformat.so=491d43f55ed4c433717609ee70c7303876b8c9918c30727719c1a067c2824398 libavutil.so=6f0b2ba1381b48a0fb114689201801902db6d9540e80a5aa08493ad66089d0e5 libswresample.so=c923266ddc6f1b171b4d69b07a3e383c11faac8e530ed91dd42181cc8411de75 libswscale.so=8c6159a749905575323a15471db8ce2bbc720aebabe9b22a4aba3c42c949bb25"
ARG COMPOSITOR_SHA256_LINUX_ARM64_GNU="ffmpeg=93cac43f6ca0c301b1ca62d120f971d9d6ee1a09a94bb655cfcc7ddc9d0cdde2 ffprobe=c54e809fd6415c7ba1e6d89c062ced0ad4434820bb89654eec36268b3f132be2 remotion=8fc227dc302f537269f709790b846386f47147ffcca073bb468158c143ff6c2a libavcodec.so=66103329cde3c265ec449878a4ac2cc244cddcf1718799c49fe15337d79363fd libavdevice.so=edf90f471cc9eb51046e23243e8fc577a6122d7ad7e38af0023a202977df7b04 libavfilter.so=1156c3ec2f0c5439581be837ba65fdf9d9cad459bf017b240caff282f26d875e libavformat.so=1f1e30cb0e6cc42b45d65f7a3d0035ec840ef5029c9e1287602cbbb36c971d3c libavutil.so=bb21137be70cf2035ba7fdb3a320bb051b15921bc6d8da155dcf8022937ca13b libswresample.so=6793a093366a89602e4cfd18f46d3d61d6bbc12ae14f718d50da1600d784fa60 libswscale.so=87e439a92103c002ea4432ce883bda2a61c0bd0d1e839769e9c1ecc3c70a11a5"
RUN set -eu; \
	for d in node_modules/@remotion/compositor-*; do \
		[ -d "$d" ] || continue; \
		case "$d" in \
			*/compositor-linux-x64-gnu) pins="$COMPOSITOR_SHA256_LINUX_X64_GNU";; \
			*/compositor-linux-arm64-gnu) pins="$COMPOSITOR_SHA256_LINUX_ARM64_GNU";; \
			*) pins="";; \
		esac; \
		names=" $(for e in $pins; do printf '%s ' "${e%%=*}"; done)"; \
		find "$d" -type f \( -name 'libav*' -o -name 'libsw*' -o -name 'libpostproc*' -o -name 'ffmpeg*' -o -name 'ffprobe*' -o -name '*fdk*' \) > /tmp/compositor-files; \
		while IFS= read -r f; do \
			case "$names" in *" ${f##*/} "*) ;; *) echo "UNLISTED libav*/ffmpeg-like file $f in the compositor (not in the pin list)" >&2; exit 1;; esac; \
		done < /tmp/compositor-files; \
	done; \
	found=0; \
	for b in node_modules/@remotion/compositor-*/ffmpeg node_modules/@remotion/compositor-*/ffprobe; do \
		[ -e "$b" ] || continue; \
		found=1; d="${b%/*}"; \
		out="$("$b" -hide_banner -buildconf 2>&1)$("$b" -hide_banner -encoders 2>&1)"; \
		case "$out" in *libfdk*|*nonfree*) ;; *) continue;; esac; \
		case "$d" in \
			*/compositor-linux-x64-gnu) pins="$COMPOSITOR_SHA256_LINUX_X64_GNU";; \
			*/compositor-linux-arm64-gnu) pins="$COMPOSITOR_SHA256_LINUX_ARM64_GNU";; \
			*) echo "NON-FREE ffmpeg in unlisted package $d" >&2; exit 1;; \
		esac; \
		for e in $pins; do printf '%s  %s\n' "${e#*=}" "$d/${e%%=*}" > /tmp/compositor.sha256; sha256sum -c /tmp/compositor.sha256 >/dev/null || { echo "NON-FREE compositor $d: ${e%%=*} differs from the pinned hash" >&2; exit 1; }; done; \
	done; rm -f /tmp/compositor.sha256 /tmp/compositor-files; \
	test "$found" = 1 || { echo "no compositor ffmpeg found under node_modules/@remotion" >&2; exit 1; }; \
	node -e "const r=require('./package.json').dependencies.remotion;for(const d of require('fs').readdirSync('node_modules/@remotion').filter(n=>n.startsWith('compositor-'))){const v=require('./node_modules/@remotion/'+d+'/package.json').version;if(v!==r){console.error(d+' '+v+' != remotion '+r);process.exit(1)}}"

# @remotion/renderer has no postinstall step — chrome-headless-shell is downloaded
# lazily, on first render, from https://www.remotion.dev/chrome-headless-shell. Every
# sandbox container this reaches at runtime is on the --internal (no egress) network by
# design, so that download can never succeed there. Pre-download it here instead, at
# image-build time, when the network IS available (this stage isn't --internal).
#
# The download lands in an architecture-specific directory (linux64/... on amd64,
# linux-arm64/... on arm64, with different executable names). Resolve the real
# executable and link it to ONE stable, relative path so nothing downstream
# (sandbox/pkg/container, template/package.json, the engine's tools) names an architecture.
# The link is relative so it survives the `cp -r` of the template into /workspace.
# The build fails if no executable is found.
# hadolint ignore=DL3003,DL4006
RUN set -eux; \
	npx remotion browser ensure --chrome-mode=headless-shell; \
	cd /opt/remotion-template/node_modules/.remotion; \
	bin="$(find chrome-headless-shell -type f \( -name chrome-headless-shell -o -name headless_shell \) | head -n 1)"; \
	test -n "$bin" && test -x "$bin"; \
	ln -sfn "$bin" headless-shell; \
	test -x headless-shell

# QA-P1-16: the chown is deliberately NARROW — only Remotion's own browser cache, the
# chrome-headless-shell this build just downloaded AS ROOT into node_modules/.remotion, is handed
# to the node user, because Remotion verifies/refreshes that cache as whichever user renders. The
# rest of the template stays root-owned, so it is NOT writable by the node user every sandbox
# container runs as: in particular reelbolt/check-layout.mjs, the layout lint the platform's
# CheckLayout tool executes, can no longer be rewritten by a render (the "shipped in the image, not
# the workspace" guarantee). The sandbox never writes here anyway — the template is copied into
# /workspace at container start and every render, bundle and scratch file lives there.
RUN chown -R node:node /opt/remotion-template/node_modules/.remotion

# Each sandbox container is started with `--entrypoint sh` and `--user node`, so
# no entrypoint is set here. Runs as node by default for anything that does not.
USER node


# ─────────────────────────────────────────────────────────────────────────────
# control-plane — the sandbox service itself (trusted).
#
# This is the image that holds the Docker socket, so it is the one thing in the
# system whose compromise is equivalent to root on the host. It builds on the
# runtime stage purely for layer reuse; the extra tooling below must never be
# allowed to leak back into the image untrusted code runs in.
# ─────────────────────────────────────────────────────────────────────────────
FROM sandbox-runtime AS control-plane

# The control plane talks to the Docker socket and must run as root; that is
# the documented trust boundary for this stage (see docs/sandbox-service.md).
# hadolint ignore=DL3002
USER root

RUN apt-get update && apt-get install -y --no-install-recommends \
	curl \
	gnupg \
	&& install -m 0755 -d /etc/apt/keyrings \
	&& curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc \
	&& chmod a+r /etc/apt/keyrings/docker.asc \
	&& echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian bookworm stable" > /etc/apt/sources.list.d/docker.list \
	&& apt-get update \
	&& apt-get install -y --no-install-recommends docker-ce-cli \
	&& rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=gobuild /app/sandbox-executor ./

EXPOSE 8080
ENTRYPOINT ["/app/sandbox-executor"]


# ─────────────────────────────────────────────────────────────────────────────
# runner-gateway — the WebSocket endpoint runners dial (docs/runner-protocol.md).
#
# A separate target from control-plane on purpose: the two share only the Go
# build stage. This image holds no Docker CLI, no Node and no sandbox tooling —
# it serves one WebSocket endpoint plus /healthz and /metrics, reads
# runner_devices, and nothing else. It is the only image in this tree that faces
# the public internet, so what it does NOT contain matters as much as what it
# does.
#
# PORT defaults to 8090 to match the upstream nginx already expects
# (an operator file, UPSTREAM_RUNNER_GATEWAY).
# ─────────────────────────────────────────────────────────────────────────────
FROM alpine:3.24 AS runner-gateway

RUN apk add --no-cache ca-certificates wget && \
    addgroup -S reelbolt && adduser -S reelbolt -G reelbolt

WORKDIR /app
COPY --from=gobuild /app/runner-gateway .
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
RUN chown reelbolt:reelbolt /app/runner-gateway

USER reelbolt
EXPOSE 8090
ENTRYPOINT ["/app/runner-gateway"]


# ─────────────────────────────────────────────────────────────────────────────
# pool-waker — the on-demand pool's scaler loop (docs/pool-waker.md).
#
# It calls the gateway's internal API and the cloud provider's API, and nothing else. So this image
# holds the scaler binary only: no Docker CLI, no Docker socket, no ffmpeg, no worker, no shell
# tooling beyond busybox. It runs as a fixed unprivileged user (uid 10001) and listens on nothing but
# its own loopback metrics port, which is what the health check reads. The provider token is a
# mounted file (HCLOUD_TOKEN_FILE), never an image or environment value.
# ─────────────────────────────────────────────────────────────────────────────
FROM alpine:3.24 AS pool-waker

RUN apk add --no-cache ca-certificates wget && \
    addgroup -S -g 10001 poolwaker && adduser -S -u 10001 -G poolwaker poolwaker && \
    mkdir -p /var/lib/pool-waker && chown poolwaker:poolwaker /var/lib/pool-waker

WORKDIR /app
COPY --from=gobuild /app/reelbolt-scaler .
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/

ENV POOL_STATE_FILE=/var/lib/pool-waker/ledger.json
ENV POOL_USAGE_FILE=/var/lib/pool-waker/usage.jsonl

USER 10001:10001
VOLUME ["/var/lib/pool-waker"]
ENTRYPOINT ["/app/reelbolt-scaler"]
CMD ["loop", "-listen", "127.0.0.1:9108"]


# ─────────────────────────────────────────────────────────────────────────────
# worker — the CloudPool compute-plane worker (docs/cloud-worker.md).
#
# This image is the one that runs CUSTOMER RENDERS, so it is the second image in
# this tree whose compromise is a host compromise: it holds the Docker socket in
# order to start the sandbox-runtime container each render happens in. That is
# the same trust boundary the control-plane stage documents, and it is why the
# worker is a separate image from the gateway — the gateway faces the public
# internet and holds neither the socket nor a runtime.
#
# It carries:
#   - the worker and the pool scaler, both static Go binaries;
#   - the docker CLI, because the driver shells out to it (SANDBOX_CONTAINER_CLI
#     can point it at podman instead);
#   - ffmpeg, for two reasons: the capability probe reports the host's ffmpeg
#     version (a worker that cannot show one advertises NO job types at all), and
#     a worker with no container runtime runs media sessions as host processes.
#
# It deliberately does NOT carry the sandbox-runtime image: that is pulled by
# digest on the VM (sandbox/pkg/poolwaker/cloud-init.yaml, sandbox/runtime-image.lock) so
# the render's runtime is a pinned artefact rather than whatever a node happened
# to have.
#
# Runs as root, like control-plane and for the same reason: whoever can reach
# the Docker socket is already root on the host, so dropping privileges here
# would buy nothing and break the socket's group ownership in every environment
# that sets the socket up differently.
# ─────────────────────────────────────────────────────────────────────────────
FROM alpine:3.24 AS worker

# Exact Alpine ffmpeg build (infra/image-pins.env; tests keep the two equal).
ARG FFMPEG_ALPINE_VERSION=8.1.3-r0
RUN apk add --no-cache ca-certificates docker-cli ffmpeg=${FFMPEG_ALPINE_VERSION}

# Publication guard (GPL source offer): pinned version installed, free build (no libfdk_aac, no nonfree).
RUN set -eu; \
	l="$(apk list -I ffmpeg)"; test "${l%% *}" = "ffmpeg-${FFMPEG_ALPINE_VERSION}"; \
	for b in ffmpeg ffprobe; do \
		out="$("$b" -hide_banner -buildconf 2>&1)$("$b" -hide_banner -encoders 2>&1)"; \
		case "$out" in *libfdk*|*nonfree*) echo "$b is not free: libfdk-aac or nonfree" >&2; exit 1;; esac; \
	done

# Build-time guard (vfr-sync-fix): ffmpeg 8.0.x's aselect does not cut audio, so a VideoCompile
# output would keep the full audio against a trimmed video. One lavfi run, no network: 10 s of
# 48 kHz mono s16 audio cut to 3 s must be ~288000 bytes, not 960000. Behavioural rather than a
# version pin, so it holds when the distro retires a package: 7.0.2, 7.1 and 8.1.3 pass, 8.0.1 fails.
RUN f="$(mktemp)" \
 && ffmpeg -v error -y -f lavfi -i sine=d=10:r=48000 -af 'aselect=lt(t\,3),asetpts=N/SR/TB' -f s16le -ac 1 "$f" \
 && n="$(wc -c < "$f")" && rm -f "$f" \
 && test "$n" -gt 100000 && test "$n" -lt 400000

WORKDIR /app
COPY --from=gobuild /app/reelbolt-worker /app/reelbolt-scaler ./
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/

# The scratch root the agent creates one directory per job in, and the driver's
# workspace root. Both are volumes on a pool VM so a restart does not fill the
# node's image layer with a customer's half-rendered project.
ENV REELBOLT_WORKER_SCRATCH=/var/lib/reelbolt/worker
ENV SANDBOX_ROOT=/var/lib/reelbolt/sandboxes
RUN mkdir -p /var/lib/reelbolt/worker /var/lib/reelbolt/sandboxes

# No EXPOSE and no healthcheck: a pool worker listens on nothing. It dials the
# gateway, and its liveness is the connection the gateway's own
# reelbolt_runner_connected{kind="CloudPool"} gauge reports. "docker compose
# --wait" therefore cannot wait on it; a deployment watches that gauge instead.
ENTRYPOINT ["/app/reelbolt-worker"]
