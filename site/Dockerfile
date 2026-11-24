# Licence files every published image carries under /licenses/ (GPL-SOURCE-OFFER.md). CI and release
# pass the repository root as the named build context `legal` (`--build-context legal=.`), which
# replaces this stage; a plain build gets placeholders that infra/scripts/check-image-publish-gate.sh rejects.
FROM alpine:3.24 AS legal
RUN for f in LICENSE THIRD_PARTY_NOTICES.md GPL-SOURCE-OFFER.md; do \
	echo "PLACEHOLDER: build with --build-context legal=<repository root>" > "/$f"; done

FROM node:26-alpine AS base

# Install dependencies only
FROM base AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci || npm install

# Development stage - runs dev server with Turbopack and hot reload
# Use with volume mounts for live code updates
FROM deps AS development
WORKDIR /app
COPY . .
ENV NODE_ENV=development
ENV PORT=3000
ENV HOSTNAME="0.0.0.0"
EXPOSE 3000
CMD ["npx", "next", "dev", "--turbopack", "-H", "0.0.0.0"]

# Build production bundle
FROM base AS builder
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
ARG NEXT_PUBLIC_SITE_URL
ENV NEXT_PUBLIC_SITE_URL=$NEXT_PUBLIC_SITE_URL
# D25 launch switch (docs/marketing-site.md launch checklist). Unset/anything but "true" leaves the
# site in its coming-soon state, which is the right default for a plain `docker build`.
ARG NEXT_PUBLIC_SIGNUP_ENABLED
ENV NEXT_PUBLIC_SIGNUP_ENABLED=$NEXT_PUBLIC_SIGNUP_ENABLED
# GA4 measurement ID (public, not a secret). Inlined into the bundle at build time, so a production
# image only has it if it is passed here; a runtime env var on the container is too late. Empty (the
# default) means no Google tag is ever injected. With an ID the tag loads on every page under Google
# Consent Mode (all storage denied until the visitor accepts); see GoogleAnalytics and lib/ga-consent.ts.
ARG NEXT_PUBLIC_GA_MEASUREMENT_ID=
ENV NEXT_PUBLIC_GA_MEASUREMENT_ID=$NEXT_PUBLIC_GA_MEASUREMENT_ID
# zero-downtime deploys: a stable Next deploymentId lets a tab opened on the previous release keep
# fetching its chunks from the new one (skew protection). the operator release script passes the app's source tree
# hash, so an unchanged app keeps its id and its build cache. Empty = Next's default, unchanged.
ARG NEXT_DEPLOYMENT_ID=
ENV NEXT_DEPLOYMENT_ID=$NEXT_DEPLOYMENT_ID
RUN npm run build

# Production stage - runs optimized precompiled Next.js standalone server
FROM base AS production
COPY --from=legal /LICENSE /THIRD_PARTY_NOTICES.md /GPL-SOURCE-OFFER.md /licenses/
WORKDIR /app
ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1

RUN addgroup --system --gid 1001 nodejs
RUN adduser --system --uid 1001 nextjs

COPY --from=builder /app/public ./public
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static

USER nextjs
EXPOSE 3000
ENV PORT=3000
ENV HOSTNAME="0.0.0.0"

CMD ["node", "server.js"]
