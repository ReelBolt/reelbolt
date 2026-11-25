# The repository's /docs tree, for the assistant's documentation search. Supplied as a NAMED build
# context (compose `additional_contexts: platform-docs: ./docs`, CI `build-contexts`), which
# overrides this empty stage; a plain `docker build` without it ships no docs and search reports
# itself unavailable instead of failing the build. See docs/platform-docs-search.md.
# Licence files every published image carries under /licenses/ (GPL-SOURCE-OFFER.md). CI and release
# pass the repository root as the named build context `legal` (`--build-context legal=.`), which
# replaces this stage; a plain build gets placeholders that infra/scripts/check-image-publish-gate.sh rejects.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM scratch AS platform-docs

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine AS base
USER root
# Ingest moderation samples uploaded media with the shared FfmpegModerationMediaSampler.
# Exact Alpine ffmpeg build (infra/image-pins.env; tests keep the two equal). Kept: the ingest
# moderation sampler (FfmpegModerationMediaSampler) runs ffmpeg in this image.
ARG FFMPEG_ALPINE_VERSION=8.1.3-r0
RUN apk add --no-cache ffmpeg=${FFMPEG_ALPINE_VERSION}
# Publication guard (GPL source offer): pinned version installed, free build (no libfdk_aac, no nonfree).
RUN set -eu; \
	l="$(apk list -I ffmpeg)"; test "${l%% *}" = "ffmpeg-${FFMPEG_ALPINE_VERSION}"; \
	for b in ffmpeg ffprobe; do \
		out="$("$b" -hide_banner -buildconf 2>&1)$("$b" -hide_banner -encoders 2>&1)"; \
		case "$out" in *libfdk*|*nonfree*) echo "$b is not free: libfdk-aac or nonfree" >&2; exit 1;; esac; \
	done
# The Data Protection key ring (used to encrypt/decrypt inference-provider API keys, shared with
# the WorkflowEngine via the same volume) must be pre-owned by the non-root app user here, in the
# image, because Docker seeds a fresh named volume's ownership from the image directory at first
# mount — creating it only at container runtime leaves the volume root-owned and unwritable,
# crashing every inference-provider write with an UnauthorizedAccessException.
RUN mkdir -p /keys && chown -R $APP_UID:$APP_UID /keys
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/ReelBolt.Shared/ReelBolt.Shared.csproj", "src/ReelBolt.Shared/"]
COPY ["src/ReelBolt.Inference.Api/ReelBolt.Inference.Api.csproj", "src/ReelBolt.Inference.Api/"]
RUN dotnet restore "src/ReelBolt.Inference.Api/ReelBolt.Inference.Api.csproj"
COPY . .
WORKDIR "/src/src/ReelBolt.Inference.Api"
RUN dotnet build "ReelBolt.Inference.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ReelBolt.Inference.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
WORKDIR /app
COPY --from=publish /app/publish .
# Vendored SKILL.md corpus (see inference/skills/UPSTREAM.md) — the Inference API doesn't run
# agents itself (only the WorkflowEngine does, via SkillCorpusService), but ships the same corpus
# alongside its own copy of the built-in agent prompts for consistency and any future admin-side
# skill diagnostics.
COPY skills/ /app/skills/
COPY --from=platform-docs . /app/platform-docs/
ENTRYPOINT ["dotnet", "ReelBolt.Inference.Api.dll"]
