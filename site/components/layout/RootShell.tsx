import { NextIntlClientProvider } from 'next-intl';
import { getMessages } from 'next-intl/server';
import { localeMeta } from '@/i18n/config';
import { getT, setRequestLocale } from '@/i18n/server';
import { Suspense } from 'react';
import { Chakra_Petch, JetBrains_Mono } from 'next/font/google';
import { SiteHeader } from '@/components/layout/SiteHeader';
import { SiteFooter } from '@/components/layout/SiteFooter';
import { StickyMobileCta } from '@/components/layout/StickyMobileCta';
import { JsonLd } from '@/components/seo/JsonLd';
import { CookieConsentProvider } from '@/components/consent/CookieConsentProvider';
import { CookieBanner } from '@/components/consent/CookieBanner';
import { GoogleAnalytics } from '@/components/analytics/GoogleAnalytics';
import { AttributionTracker } from '@/components/analytics/AttributionTracker';
import { PageViewTracker } from '@/components/analytics/PageViewTracker';
import { PromoBanner } from '@/components/promo/PromoBanner';
import { CHROME_NAMESPACES, pickNamespaces } from '@/components/layout/PageMessages';
// Side-effect import: assertCatalog() runs at module load, so a malformed plan catalog fails the build.
import '@/lib/pricing';
import { organizationSchema, websiteSchema } from '@/lib/structured-data';
import '@/app/globals.css';

const display = Chakra_Petch({
  subsets: ['latin'],
  weight: ['700'],
  variable: '--font-display-face',
  display: 'swap',
});
const mono = JetBrains_Mono({
  subsets: ['latin'],
  weight: ['400', '500', '700'],
  variable: '--font-mono-face',
  display: 'swap',
});

export async function RootShell({ locale, children }: { locale: string; children: React.ReactNode }) {
  setRequestLocale(locale);
  // Chrome namespaces only; each page adds the ones its own client components read (PageMessages).
  const messages = pickNamespaces((await getMessages()) as Record<string, unknown>, CHROME_NAMESPACES);
  const meta = localeMeta(locale);
  const t = await getT('layout', locale);
  // Only ever sourced from the env var — never hardcode a measurement ID.
  // With an ID, `GoogleAnalytics` mounts the Google tag on every page under Google Consent Mode:
  // all storage denied (no cookie, cookieless pings only) until the visitor accepts the banner.
  // Without one there is no tag at all. See lib/ga-consent.ts.
  const gaId = process.env.NEXT_PUBLIC_GA_MEASUREMENT_ID;

  return (
    <html lang={meta.htmlLang} dir={meta.dir} className={`${display.variable} ${mono.variable}`}>
      <body className="font-sans antialiased min-h-screen flex flex-col bg-paper text-ink">
        <NextIntlClientProvider locale={locale} messages={messages}>
        <a
          href="#main"
          className="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-50 focus:rounded-[2px] focus:bg-accent-strong focus:px-4 focus:py-2 focus:text-sm focus:font-semibold focus:text-ink"
        >
          {t('skipToContent')}
        </a>
        <CookieConsentProvider>
          <AttributionTracker />
          {gaId && (
            <GoogleAnalytics gaId={gaId}>
              <Suspense fallback={null}>
                <PageViewTracker />
              </Suspense>
            </GoogleAnalytics>
          )}
          <PromoBanner />
          <SiteHeader />
          <main id="main" className="flex-1 pb-20 md:pb-0">
            {children}
          </main>
          <SiteFooter />
          <StickyMobileCta />
          <CookieBanner />
        </CookieConsentProvider>
        </NextIntlClientProvider>
        <JsonLd data={organizationSchema(locale)} />
        <JsonLd data={websiteSchema(locale)} />
      </body>
    </html>
  );
}
