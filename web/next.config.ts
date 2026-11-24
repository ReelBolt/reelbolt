import type { NextConfig } from 'next';
import createNextIntlPlugin from 'next-intl/plugin';

// No locale routing in the dashboard: the request locale comes from i18n/request.ts.
const withNextIntl = createNextIntlPlugin('./i18n/request.ts');

// Extra hostnames/IPs allowed to load the dev server (HMR, /_next) besides localhost, e.g. a
// phone or another machine on your LAN. Comma separated, wildcards allowed ("192.168.*.*").
// Empty by default; dev only. See NEXT_DEV_ALLOWED_ORIGINS in .env.example.
const extraDevOrigins = (process.env.NEXT_DEV_ALLOWED_ORIGINS ?? '')
  .split(',')
  .map((origin) => origin.trim())
  .filter(Boolean);

// One build id for both Next's /_next/static/<id>/ path and the client (Copy diagnostics reads it).
// Without GIT_SHA the fallback is pinned in process.env on first load, so every later load of this config in
// the same build (and its child workers) gets the same id and NEXT_PUBLIC_BUILD_ID matches .next/BUILD_ID.
const buildId =
  process.env.GIT_SHA ||
  process.env.NEXT_PUBLIC_BUILD_ID ||
  (process.env.NEXT_PUBLIC_BUILD_ID = `local-${Date.now().toString(36)}`);

const nextConfig: NextConfig = {
  generateBuildId: async () => buildId,
  env: { NEXT_PUBLIC_BUILD_ID: buildId },
  output: 'standalone',
  // Skew protection for blue-green deploys: a stable id (the app's source tree hash, set by the operator release script) lets
  // tabs from the previous release keep loading chunks. Undefined when unset, so Next's default applies.
  deploymentId: process.env.NEXT_DEPLOYMENT_ID || undefined,
  // The catalogues are read from disk at request time (i18n/messages.ts); make sure the standalone build ships them.
  outputFileTracingIncludes: { '/**': ['./messages/**/*', './i18n/locales.json'] },
  basePath: '/app',
  devIndicators: false,
  allowedDevOrigins: ['localhost', '127.0.0.1', ...extraDevOrigins],
  // A guest review link carries its credential in the path (docs/design/guest-review-links.md): no referrer,
  // no caching, no indexing. basePath ('/app') is added to `source` by Next.
  async headers() {
    return [
      {
        source: '/review/g/:token',
        headers: [
          { key: 'Referrer-Policy', value: 'no-referrer' },
          { key: 'X-Robots-Tag', value: 'noindex, nofollow' },
          { key: 'Cache-Control', value: 'private, no-store' },
          { key: 'X-Content-Type-Options', value: 'nosniff' },
        ],
      },
    ];
  },
};

export default withNextIntl(nextConfig);
