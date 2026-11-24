# ReelBolt documentation site. Build context is ./docs-site; the Markdown itself comes from the
# repository's /docs/user-guide folder through a NAMED build context (compose `additional_contexts`,
# CI `build_contexts: docs=./docs/user-guide`). Only the user guide is ever handed to this build: the
# developer docs are not published and never enter this image's build context. The empty stage below
# is the fallback for a plain `docker build` without it, and is overridden whenever the named
# context is supplied. `docs_i18n` is the same for the translated pages (/docs/user-guide-i18n:
# `<locale>/docusaurus-plugin-content-docs/current/**` plus the theme strings): compose
# `additional_contexts`, CI `build_contexts: docs_i18n=./docs/user-guide-i18n`. Empty = English only.
# Licence files every published image carries under /licenses/ (GPL-SOURCE-OFFER.md). CI and release
# pass the repository root as the named build context `legal` (`--build-context legal=.`), which
# replaces this stage; a plain build gets placeholders that infra/scripts/check-image-publish-gate.sh rejects.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM scratch AS docs
FROM scratch AS docs_i18n

FROM node:26-alpine AS build
WORKDIR /site
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
COPY --from=docs . /docs/user-guide/
COPY --from=docs_i18n . /docs/user-guide-i18n/
ARG DOCS_SITE_URL=http://localhost
ENV DOCS_SITE_URL=$DOCS_SITE_URL
# Cap the Node heap: an unbounded build on a small box froze production (2026-11-10).
RUN NODE_OPTIONS=--max-old-space-size=3072 npm run build

# Static files only — nginx serves them under /docs/, the same base path the root reverse proxy
# forwards unchanged.
FROM nginx:1.31.9-alpine AS production
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /site/build /usr/share/nginx/html/docs
EXPOSE 80
