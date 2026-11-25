# Licence files every published image carries under /licenses/ (GPL-SOURCE-OFFER.md). CI and release
# pass the repository root as the named build context `legal` (`--build-context legal=.`), which
# replaces this stage; a plain build gets placeholders that infra/scripts/check-image-publish-gate.sh rejects.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine AS base
USER root
# ffmpeg/ffprobe run in-process for the video-editing pipeline (plan §2) — never in the sandbox
# service, which is untrusted-code territory this feature deliberately does not touch. Both the
# video scratch directory and the shared Data Protection key ring (/keys — encrypts/decrypts
# inference-provider API keys, mounted here and in the Inference API) are created and chowned to
# $APP_UID here, in the image, because Docker seeds a fresh named volume's ownership from the
# image directory at first mount (R12); creating them only at container runtime would leave the
# volumes root-owned and unwritable by the non-root user.
ARG FFMPEG_ALPINE_VERSION=8.1.3-r0
RUN apk add --no-cache ffmpeg=${FFMPEG_ALPINE_VERSION} font-dejavu \
 && mkdir -p /var/tmp/reelbolt-video /keys \
 && chown -R $APP_UID:$APP_UID /var/tmp/reelbolt-video /keys

# Publication guard (GPL source offer): pinned version installed (infra/image-pins.env; tests keep the
# two equal), free build (no libfdk_aac, no nonfree).
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

# Build-time guard (audio-cleanup): local VoiceBasic cleanup needs ffmpeg's arnndn (RNNoise) filter
# plus the others the Cleanup/ chains use. Fail the build rather than ship an image that cannot clean.
RUN ffmpeg -hide_banner -filters | grep -q arnndn \
 && ffmpeg -hide_banner -filters | grep -q afftdn \
 && ffmpeg -hide_banner -filters | grep -q bandreject
# Build-time guard (video-enhance): the local enhancement engines need hqdn3d/nlmeans (denoise),
# minterpolate+tmix (motion blur and frame interpolation), cas (fast upscale), zscale (HDR to
# SDR), noise (keep-grain), tpad/apad/fps/scale (duration repair and conform). Names are matched
# exactly on the filter-name column, not as a substring. Fail the image build rather than ship an engine that cannot enhance.
RUN for f in hqdn3d nlmeans minterpolate tmix cas zscale noise tpad apad fps scale; do \
      ffmpeg -hide_banner -filters | awk -v f="$f" '$2 == f { found = 1 } END { exit !found }' \
        || { echo "ffmpeg is missing the $f filter" >&2; exit 1; }; \
    done
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/ReelBolt.Shared/ReelBolt.Shared.csproj", "src/ReelBolt.Shared/"]
COPY ["src/ReelBolt.WorkflowEngine/ReelBolt.WorkflowEngine.csproj", "src/ReelBolt.WorkflowEngine/"]
RUN dotnet restore "src/ReelBolt.WorkflowEngine/ReelBolt.WorkflowEngine.csproj"
COPY . .
WORKDIR "/src/src/ReelBolt.WorkflowEngine"
RUN dotnet build "ReelBolt.WorkflowEngine.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ReelBolt.WorkflowEngine.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
WORKDIR /app
COPY --from=publish /app/publish .
# Vendored SKILL.md corpus (see inference/skills/UPSTREAM.md) — loaded once at runtime by
# SkillCorpusService from Skills:CorpusPath (default /app/skills, matching this path).
COPY skills/ /app/skills/
# First-party block library (inference/blocks/), hash-verified at load by BlockCatalog from
# Blocks:CorpusPath (default /app/blocks, matching this path).
COPY blocks/ /app/blocks/
# Vendored watermark raster (WP B8b): the trial output policy composites it with ffmpeg's
# overlay, which needs a BITMAP and brand/ ships SVG only. Rendered from brand/mark-mono.svg by
# brand/scripts/render-watermark.mjs. VideoEditing:WatermarkAssetPath points at this path.
COPY assets/ /app/assets/
# Vendored caption fonts (SIL OFL 1.1, see inference/fonts/captions/UPSTREAM.md) — libass burns
# captions with them via the ass filter's fontsdir= (VideoEditing:CaptionFontsDir, default this
# path). Under /usr/share/fonts so fontconfig sees them too. Alpine's ffmpeg is built with
# --enable-libass, so the ass filter needs no extra package.
COPY fonts/captions/ /usr/share/fonts/reelbolt-captions/
ENTRYPOINT ["dotnet", "ReelBolt.WorkflowEngine.dll"]
