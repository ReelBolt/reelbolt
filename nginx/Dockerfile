# Licence files every published image carries under /licenses/ (GPL-SOURCE-OFFER.md). CI and release
# pass the repository root as the named build context `legal` (`--build-context legal=.`), which
# replaces this stage; a plain build gets placeholders that infra/scripts/check-image-publish-gate.sh rejects.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM nginx:1.31.9-alpine
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/

RUN apk add --no-cache openssl gettext curl

# The whole config ships in the image (the cloud runs prebuilt images with no bind
# mounts); the dev compose file still bind-mounts these over the top.
COPY nginx.conf auth.js i18n.js pick-locale.js locales.json locations.conf proxy-client-headers.conf security-headers.conf cloudflare-realip.conf /etc/nginx/
COPY http-server-dev.conf http-server-redirect.conf https-server.conf.template \
     common.conf.template runner-server.conf.template runner-locations.conf.template \
     private-server.conf.template slots-vars.conf.template slots-locations.conf /etc/nginx/
COPY fetch-paddle-ips.sh /etc/nginx/fetch-paddle-ips.sh
COPY render-config.sh reload-config.sh /etc/nginx/
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh /etc/nginx/fetch-paddle-ips.sh /etc/nginx/render-config.sh /etc/nginx/reload-config.sh

ENTRYPOINT ["/docker-entrypoint.sh"]
