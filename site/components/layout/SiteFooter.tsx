import { SignInLink, SignupLink } from '@/components/analytics/SignupLink';
import { BrandMark } from '@/components/ui/BrandMark';
import { Link } from '@/i18n/link';
import { useCurrentLocale, useT } from '@/i18n';
import { docsHref } from '@/i18n/config';
import { LanguageSwitcher } from '@/components/layout/LanguageSwitcher';
import type { ReactNode } from 'react';
import { productVersion, siteConfig } from '@/lib/site-config';
import { ContactAddress } from '@/components/contact/ContactAddress';
import { CookiePreferencesLink } from '@/components/consent/CookiePreferencesLink';
import { FooterNewsletter } from '@/components/layout/NewsletterSignup';
import { TRADEMARK_STATEMENT } from '@/lib/legal-placeholders';
import { AUDIENCES } from '@/lib/audiences/registry';

// In-page anchors on the homepage are deliberate: they are the deepest real entry points the
// site has, and linking them here gives crawlers (and readers arriving on a legal page) a route
// straight back into the sections that explain the product.
const PRODUCT_LINKS = [
  { key: 'howItWorks', href: '/#how-it-works' },
  { key: 'features', href: '/features' },
  { key: 'examples', href: '/examples' },
  { key: 'pricing', href: '/pricing' },
  { key: 'recipes', href: '/recipes' },
  { key: 'blog', href: '/blog' },
  // Linked even while the runner is still being built, and deliberately so: the page states its
  // own availability ("not available yet", from `lib/runner-downloads`'s empty artifact list), so
  // the link never promises a download — while an unlinked page would be an orphan nobody could
  // reach or review. The label names the surface, not a claim about it.
  { key: 'desktopRunner', href: siteConfig.runnerPath },
  { key: 'whatYouGet', href: '/#whats-inside' },
  { key: 'faq', href: '/#faq' },
] as const;

// Deliberately a <p>, not a heading. These four labels repeat on every page of the site; as <h2>
// elements they sat in the document outline alongside each page's real section headings and
// diluted it. The landmark role and its accessible name come from the wrapping <nav
// aria-labelledby>, which is what actually lets assistive tech jump between footer groups.
function ColumnHeading({ id, children }: { id: string; children: ReactNode }) {
  return (
    <p id={id} className="font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted">
      {children}
    </p>
  );
}

function Lockup() {
  const t = useT('layout.brand');
  return (
    <Link href="/" className="flex items-center gap-2.5">
      <BrandMark size={28} />
      <span className="flex flex-col leading-none">
        <span className="font-display text-base font-bold leading-none text-ink">{siteConfig.name}</span>
        <span className="mt-1 font-mono text-[10px] uppercase tracking-eyebrow text-ink-muted">
          {t('tagline')}
        </span>
      </span>
    </Link>
  );
}

export function SiteFooter() {
  const t = useT('layout.footer');
  const cta = useT('layout.cta');
  const audiences = useT('audiences.items');
  const locale = useCurrentLocale();
  return (
    <footer className="border-t border-line bg-paper-2 pb-20 md:pb-0">
      <div className="mx-auto max-w-6xl px-4 sm:px-6 lg:px-8">
        <FooterNewsletter />

        <div className="grid grid-cols-1 border-line sm:grid-cols-2 sm:divide-x sm:divide-line lg:grid-cols-4">
          <div className="border-t border-line py-10 first:border-t-0 sm:px-8 sm:first:pl-0 lg:border-t-0">
            <ColumnHeading id="footer-product">{t('product')}</ColumnHeading>
            <nav aria-labelledby="footer-product">
              <ul className="mt-4 space-y-0">
                {PRODUCT_LINKS.map((link) => (
                  <li key={link.href}>
                    <Link href={link.href} className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                      {t(link.key)}
                    </Link>
                  </li>
                ))}
                <li>
                  {/* A plain <a>: /docs/ is the documentation site, not a page of this app. */}
                  <a href={docsHref(locale)} className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {t('docs')}
                  </a>
                </li>
              </ul>
            </nav>
          </div>

          <div className="border-t border-line py-10 sm:px-8 lg:border-t-0">
            <ColumnHeading id="footer-company">{t('company')}</ColumnHeading>
            <nav aria-labelledby="footer-company">
              <ul className="mt-4 space-y-0">
                <li>
                  <Link href="/about" className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {t('about')}
                  </Link>
                </li>
                <li>
                  <Link
                    href={siteConfig.salesCta.href}
                    className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink"
                  >
                    {cta(siteConfig.salesCta.msgKey)}
                  </Link>
                </li>
                {siteConfig.signupEnabled && (
                  <li>
                    {/* A plain <a> inside: /app/* is the other Next.js app, so next/link would 404.
                        SignupLink is the client leaf that adds the visitor's audience and campaign
                        parameters to the link in the browser. */}
                    <SignupLink
                      href={siteConfig.primaryCta.href}
                      location="footer"
                      className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink"
                    >
                      {cta(siteConfig.primaryCta.msgKey)}
                    </SignupLink>
                  </li>
                )}
                <li>
                  {siteConfig.signupEnabled ? (
                    <SignInLink className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                      {t('signIn')}
                    </SignInLink>
                  ) : (
                    <span className="inline-flex min-h-11 items-center text-sm text-ink-muted">{t('appComingSoon')}</span>
                  )}
                </li>
              </ul>
            </nav>
          </div>

          <div className="border-t border-line py-10 sm:px-8 lg:border-t-0 lg:border-l">
            <ColumnHeading id="footer-legal">{t('legal')}</ColumnHeading>
            <nav aria-labelledby="footer-legal">
              <ul className="mt-4 space-y-0">
                <li>
                  <Link href="/legal/privacy" className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {t('privacy')}
                  </Link>
                </li>
                <li>
                  <Link href="/legal/terms" className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {t('terms')}
                  </Link>
                </li>
                <li>
                  <Link href="/legal/acceptable-use" className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {t('acceptableUse')}
                  </Link>
                </li>
                <li>
                  <Link href="/legal/refunds" className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {t('refunds')}
                  </Link>
                </li>
                <li>
                  <Link href="/legal/runner-eula" className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {t('runnerEula')}
                  </Link>
                </li>
                <li>
                  <CookiePreferencesLink className="inline-flex min-h-11 items-center text-sm" />
                </li>
              </ul>
            </nav>
          </div>

          <div className="border-t border-line py-10 sm:px-8 sm:last:pr-0 lg:border-t-0">
            <ColumnHeading id="footer-contact">{t('contact')}</ColumnHeading>
            <ContactAddress className="mt-4" />
          </div>
        </div>

        {/* The six audience pages. They are linked here and from the home page, and deliberately not
            from the header: its width is measured per language (e2e/tests/site/header-spacing.spec.ts). */}
        <div className="border-t border-line py-6">
          <ColumnHeading id="footer-audiences">{t('madeFor')}</ColumnHeading>
          <nav aria-labelledby="footer-audiences">
            <ul className="mt-2 flex flex-wrap gap-x-6">
              {AUDIENCES.map((audience) => (
                <li key={audience.slug}>
                  <Link href={audience.path} className="inline-flex min-h-11 items-center text-sm text-ink-muted hover:text-ink">
                    {audiences(`${audience.messageKey}.name`)}
                  </Link>
                </li>
              ))}
            </ul>
          </nav>
        </div>

        <div className="border-t border-line py-8">
          <p className="max-w-3xl text-sm text-ink-muted">
            {t.rich('disclosure', {
              name: siteConfig.name,
              privacy: (chunks) => (
                <Link
                  href={`${siteConfig.url}/legal/privacy`}
                  className="text-accent-ink underline underline-offset-2 hover:text-accent"
                >
                  {chunks}
                </Link>
              ),
            })}
          </p>
        </div>

        <div className="flex flex-col gap-6 border-t border-line py-8 sm:flex-row sm:items-center sm:justify-between">
          <Lockup />
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <span className="font-mono text-xs text-ink-faint">{t('version', { version: productVersion })}</span>
            <p className="font-mono text-xs text-ink-faint">
              {t('copyright', { year: String(new Date().getFullYear()), name: siteConfig.name })}
            </p>
            <LanguageSwitcher />
          </div>
        </div>

        <div className="flex flex-col gap-1 border-t border-line py-4 sm:flex-row sm:flex-wrap sm:items-center sm:gap-x-4">
          <p className="font-mono text-xs text-ink-faint">{t('alpha', { version: productVersion })}</p>
          <Link
            href="/legal/privacy"
            className="inline-flex min-h-11 items-center font-mono text-xs text-accent-ink underline underline-offset-2 hover:text-accent"
          >
            {t('privacyTelemetry')}
          </Link>
          <p className="font-mono text-xs text-ink-faint">{TRADEMARK_STATEMENT}</p>
        </div>
      </div>
    </footer>
  );
}
