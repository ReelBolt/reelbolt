# Licence files every published image carries under /licenses/ (GPL-SOURCE-OFFER.md). CI and release
# pass the repository root as the named build context `legal` (`--build-context legal=.`), which
# replaces this stage; a plain build gets placeholders that infra/scripts/check-image-publish-gate.sh rejects.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
USER root
# poppler-utils is the only capability this service has. It parses untrusted zips and PDFs with no
# database, no storage and no provider keys, so the surface stays as small as the task allows.
# icu-libs gives the runtime Unicode normalisation data: zip entry names are NFC-normalised before they are
# de-duplicated, so "cafe\u0301" and "caf\u00e9" cannot both land. The .NET alpine base image sets
# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true and ships no ICU; it is switched off explicitly below. poppler-utils is
# pinned below; icu-libs stays unpinned (.hadolint.yaml ignores DL3018; the base image tag bounds it).
# poppler-utils is GPL: exact Alpine build (infra/image-pins.env; tests keep the two equal), checked after install.
ARG POPPLER_ALPINE_VERSION=25.12.0-r1
RUN apk add --no-cache poppler-utils=${POPPLER_ALPINE_VERSION} icu-libs \
 && l="$(apk list -I poppler-utils)" && test "${l%% *}" = "poppler-utils-${POPPLER_ALPINE_VERSION}"
USER $APP_UID
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/ReelBolt.Shared/ReelBolt.Shared.csproj", "src/ReelBolt.Shared/"]
COPY ["src/ReelBolt.ImportWorker/ReelBolt.ImportWorker.csproj", "src/ReelBolt.ImportWorker/"]
RUN dotnet restore "src/ReelBolt.ImportWorker/ReelBolt.ImportWorker.csproj"
COPY . .
WORKDIR "/src/src/ReelBolt.ImportWorker"
RUN dotnet build "ReelBolt.ImportWorker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ReelBolt.ImportWorker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ReelBolt.ImportWorker.dll"]
