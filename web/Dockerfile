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
# alpha-launch T-018: the app-side telemetry switch. A NEXT_PUBLIC_* value is inlined into the
# browser bundle here, so a production image must carry it at build time. Off by default, which
# is the right default for a plain `docker build` (see docker-compose.yml, web build.args).
ARG NEXT_PUBLIC_TELEMETRY_ENABLED=false
ENV NEXT_PUBLIC_TELEMETRY_ENABLED=$NEXT_PUBLIC_TELEMETRY_ENABLED
ARG NEXT_PUBLIC_GA_MEASUREMENT_ID=
ENV NEXT_PUBLIC_GA_MEASUREMENT_ID=$NEXT_PUBLIC_GA_MEASUREMENT_ID
# mobile-nav-stuck: the commit the image is built from becomes Next's build id and the build id the
# feedback diagnostics report (next.config.ts). Empty = a per-build timestamp.
ARG GIT_SHA=
ENV GIT_SHA=$GIT_SHA
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
