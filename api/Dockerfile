# Licence files every published image carries under /licenses/ (GPL-SOURCE-OFFER.md). CI and release
# pass the repository root as the named build context `legal` (`--build-context legal=.`), which
# replaces this stage; a plain build gets placeholders that infra/scripts/check-image-publish-gate.sh rejects.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM golang:1.27-alpine AS build
WORKDIR /src

RUN apk add --no-cache ca-certificates git

ARG GOPROXY=https://proxy.golang.org,direct
ARG GOSUMDB=sum.golang.org
ENV GOPROXY=${GOPROXY}
ENV GOSUMDB=${GOSUMDB}

COPY go.mod go.sum ./
RUN set -eux; \
		for i in 1 2 3 4 5; do \
			go mod download && break; \
			echo "go mod download failed (attempt ${i}), retrying..."; \
			sleep $((i * 3)); \
		done

COPY . .

ARG VERSION=0.4.0-alpha
RUN CGO_ENABLED=0 go build -mod=mod -ldflags="-s -w -X main.Version=${VERSION}" -o /app/reelbolt .

FROM alpine:3.24
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
RUN apk add --no-cache wget && \
    addgroup -S reelbolt && adduser -S reelbolt -G reelbolt && \
    install -d -m 0700 -o reelbolt -g reelbolt /var/lib/reelbolt

WORKDIR /app
COPY --from=build /app/reelbolt .
RUN chown reelbolt:reelbolt /app/reelbolt

USER reelbolt
EXPOSE 8080
ENTRYPOINT ["./reelbolt"]
