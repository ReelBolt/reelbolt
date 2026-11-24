import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dropzone/styles.css';
import './global.css';

import { Suspense } from 'react';
import { Chakra_Petch, JetBrains_Mono } from 'next/font/google';
import type { Viewport } from 'next';
import { ColorSchemeScript, MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { DeletionCancelledNotice } from '@/components/auth/DeletionCancelledNotice';
import { APP_NAME, routeTitle } from '@/lib/metadata';
import { PRODUCT_VERSION } from '@/lib/version';
import { localeInfo } from '@/i18n/config';
import { IntlProvider } from '@/i18n/provider';
import { getLocale, getT } from '@/i18n/server';
import { messagesFor } from '@/i18n/messages';
import { theme } from './theme';
import { GoogleAnalytics } from '@/components/analytics/GoogleAnalytics';

const display = Chakra_Petch({ subsets: ['latin'], weight: ['500', '600', '700'], variable: '--font-display-face', display: 'swap' });
const mono = JetBrains_Mono({ subsets: ['latin'], weight: ['400', '500', '700'], variable: '--font-mono-face', display: 'swap' });

// The alpha notice is dismissed for the DEVICE (`localStorage`), so a returning visitor's very
// first paint has to know it: a server-rendered bar would show, then vanish once hydration read
// storage — the flash users complained about. An inline script in `<head>` marks
// `<html data-alpha-dismissed="1">` before the body is parsed and the rule below keeps the bar
// hidden until React hydrates and unmounts it. No mismatch: the server snapshot is still "not
// dismissed", the attribute only *hides* what the client is about to remove.
//
// The key is the same string `components/shell/AlphaBanner.tsx` builds with
// `alphaDismissalKey(PRODUCT_VERSION)`; it is spelled out here because a `'use client'` module's
// exports are client references a Server Component cannot read — `AlphaBanner.test.tsx` pins the
// two spellings together so they cannot drift.
const ALPHA_DISMISSED_KEY = 'reelbolt.alpha-banner.dismissed';
const ALPHA_DISMISSED_ATTR = 'data-alpha-dismissed';
const alphaBootScript = `(function(){try{if(localStorage.getItem('${ALPHA_DISMISSED_KEY}:${PRODUCT_VERSION}')==='1'){document.documentElement.setAttribute('${ALPHA_DISMISSED_ATTR}','1')}}catch(e){}})()`;
const alphaDismissedCss = `[${ALPHA_DISMISSED_ATTR}='1'] [data-alpha-banner]{display:none!important}`;

// Browser-level favicon (tab + PWA launcher).
//
// The single brand master (brand/app-icon.svg — the #2E1065 rounded square, rasterized by
// `npm run icons` into web/public/favicon-*.png) is a DARK icon: a dark square with a lavender
// (#C4B5FD) R and a violet (#A855F7) bolt. It therefore already reads correctly on a dark browser
// tab. What it was missing — and what made the tab look "stuck on the light variant" in dark
// mode — is that there is no dark-variant entry at all. Next.js 16's file-based `icon.svg` /
// `favicon` convention emits ONE <link rel="icon"> with NO `media` attribute (verified in
// node_modules/next/dist/docs/.../app-icons.md), so the browser had no way to select a dark icon
// and fell back to the single entry in every colour scheme.
//
// `metadata.icons` is the platform-supported surface for a colour-scheme variant (see
// node_modules/next/dist/docs/.../generate-metadata.md: `icons.icon` accepts `{ url, media }`).
// We emit the dark entry from the SAME master — the master is already the dark square, so this
// requires no redraw and no new brand artwork (there is no separate dark master in /brand, and
// inventing one is out of scope per docs/brand.md). It also does not touch any byte-synced copy
// (web/app/icon.svg, web/public/logo.svg, web/assets/logo.svg all stay the master, per
// brand/scripts/sync.mjs --check). The existing file-based icon.svg keeps serving the light-mode
// <link>; the metadata entry adds the dark-mode one the browser was missing.
//
// The dashboard's tab titles: each page names itself and the template appends the product name,
// so no two pages are indistinguishable in a tab strip. `default` is required whenever a
// template is set and it is what a route carrying no title of its own falls back to (app/page.tsx
// is a bare redirect, and the signed-out (auth) pages are outside the dashboard).
//
// Every route's title lives in a co-located server `layout.tsx` rather than in the page itself:
// the pages under (app) are all client components, and Next reads `metadata` from Server
// Components only — see node_modules/next/dist/docs/01-app/03-api-reference/04-functions/
// generate-metadata.md ("The `metadata` object and `generateMetadata` function exports are
// **only supported in Server Components**"). `routeTitle` is in lib/metadata.ts, with the
// reason every segment has to re-declare the template.
// Metadata is a function so the description follows the request locale (route layouts do the same
// with `const t = await getT('<ns>')`, then `routeTitle(t('title'))`).
export async function generateMetadata() {
  const t = await getT('common');
  return {
  title: routeTitle(APP_NAME),
  description: t('meta.description'),
  robots: { index: false, follow: false },
  icons: {
    icon: [
      // Dark-mode tab: the master is already the #2E1065 dark square, so it reads correctly here.
      { url: '/favicon-512.png', sizes: '512x512', type: 'image/png', media: '(prefers-color-scheme: dark)' },
    ],
  },
  };
}

// themeColor matches app/manifest.ts's theme_color (web/app/theme.ts's violet primary).
export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  themeColor: '#7C3AED',
};

export default async function RootLayout({ children }: { children: React.ReactNode }) {
  // Resolved per request (saved user locale, rb_locale cookie, Accept-Language, en); see i18n/request.ts.
  const locale = await getLocale();
  const { htmlLang, dir } = localeInfo(locale);
  return (
    <html lang={htmlLang} dir={dir} className={`${display.variable} ${mono.variable}`} suppressHydrationWarning>
      <head>
        <ColorSchemeScript defaultColorScheme="auto" />
        {/* Hides the alpha bar before React hydrates, for a visitor who already dismissed it. */}
        <style dangerouslySetInnerHTML={{ __html: alphaDismissedCss }} />
        {/* Runs before the body is parsed, so the attribute is set before the bar can paint. */}
        <script dangerouslySetInnerHTML={{ __html: alphaBootScript }} />
      </head>
      <body>
        <IntlProvider locale={locale} messages={messagesFor(locale)}>
        <MantineProvider theme={theme} defaultColorScheme="auto">
          <Notifications position="top-right" />
          {/* Reads ?deletion=cancelled, which a Google/GitHub sign-in that cancelled a pending
              account deletion lands with; on every page because that landing page varies. */}
          <Suspense fallback={null}>
            <DeletionCancelledNotice />
          </Suspense>
          {children}
          <GoogleAnalytics />
        </MantineProvider>
        </IntlProvider>
      </body>
    </html>
  );
}
