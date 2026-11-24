import type { NextConfig } from 'next';
import createNextIntlPlugin from 'next-intl/plugin';

// Locale comes from the route, never from a header (the static export has no server): the plugin
// only wires the request config that loads the catalogues. See i18n/request.ts.
const withNextIntl = createNextIntlPlugin('./i18n/request.ts');

// STATIC_EXPORT=1 builds the legacy, unused static bundle (plain files in out/, once meant for
// Cloudflare Pages; nothing deploys it). The default is the Node standalone server the `site`
// container runs, which is what serves reelbolt.ai. See docs/marketing-site.md.
const staticExport = process.env.STATIC_EXPORT === '1';

// Extra hostnames/IPs allowed to load the dev server (HMR, /_next) besides localhost, e.g. a
// phone or another machine on your LAN. Comma separated, wildcards allowed ("192.168.*.*").
// Empty by default; dev only. See NEXT_DEV_ALLOWED_ORIGINS in .env.example.
const extraDevOrigins = (process.env.NEXT_DEV_ALLOWED_ORIGINS ?? '')
  .split(',')
  .map((origin) => origin.trim())
  .filter(Boolean);

const nextConfig: NextConfig = {
  output: staticExport ? 'export' : 'standalone',
  // Skew protection for blue-green deploys: a stable id (the app's source tree hash, set by the operator release script) lets
  // tabs from the previous release keep loading chunks. Undefined when unset, so Next's default applies.
  deploymentId: process.env.NEXT_DEPLOYMENT_ID || undefined,
  // Inline the critical CSS into each page's <head> so first paint does not wait on a stylesheet
  // request. The CSS is small (one Tailwind bundle), so the inlined share stays modest.
  experimental: {
    inlineCss: true,
    // Two root layouts ((en) and [locale]) leave no single layout to build the 404 from.
    globalNotFound: true,
  },
  // Catalogues are read from disk by i18n/request.ts.
  outputFileTracingIncludes: { '/*': ['./messages/**/*'] },
  allowedDevOrigins: ['localhost', '127.0.0.1', ...extraDevOrigins],
  images: staticExport
    ? { unoptimized: true }
    : {
        formats: ['image/avif', 'image/webp'],
      },
  // Mirrors public/_headers for the Node server. headers() is not supported with output 'export',
  // where only the legacy static bundle's public/_headers would apply.
  ...(staticExport
    ? {}
    : {
        async headers() {
          return [
            {
              source: '/(.*)',
              headers: [
                { key: 'X-Content-Type-Options', value: 'nosniff' },
                { key: 'X-Frame-Options', value: 'DENY' },
                { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
                { key: 'Permissions-Policy', value: 'camera=(), microphone=(), geolocation=(), payment=()' },
              ],
            },
          ];
        },
      }),
};

export default withNextIntl(nextConfig);
