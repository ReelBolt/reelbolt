'use client';

import { BrandMark } from '@/components/ui/BrandMark';
import { useState, type ComponentProps } from 'react';
import { Link } from '@/i18n/link';
import { useBasePath } from '@/i18n/use-base-path';
import { useCurrentLocale, useT } from '@/i18n';
import { docsHref } from '@/i18n/config';
import { siteConfig } from '@/lib/site-config';
import { TRADEMARK_SYMBOL } from '@/lib/trademark';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { SignInLink } from '@/components/analytics/SignupLink';

function Lockup() {
  const t = useT('layout.brand');
  return (
    <Link href="/" className="flex shrink-0 items-center gap-2.5">
      <BrandMark size={32} />
      <span className="flex flex-col leading-none">
        <span className="font-display text-lg font-bold leading-none text-ink">
          {siteConfig.name}
          <span className="ml-px align-super font-sans text-[10px] font-normal">{TRADEMARK_SYMBOL}</span>
        </span>
        <span className="mt-1 font-mono text-[10px] uppercase tracking-eyebrow text-ink-muted">
          {t('tagline')}
        </span>
      </span>
    </Link>
  );
}

/** `/docs/` is the documentation site, not a page of this app: a plain <a> in the visitor's language. */
function NavItem({
  item,
  ...props
}: { item: (typeof siteConfig.nav)[number] } & Omit<ComponentProps<'a'>, 'href'>) {
  const locale = useCurrentLocale();
  if (item.key === 'docs') return <a href={docsHref(locale)} {...props} />;
  return <Link href={item.href} {...props} />;
}

export function SiteHeader() {
  const [menuOpen, setMenuOpen] = useState(false);
  const pathname = useBasePath();
  const t = useT('layout.header');
  const nav = useT('layout.nav');

  return (
    <header className="sticky top-0 z-40 h-20 border-b border-line bg-paper">
      <div className="mx-auto flex h-20 max-w-6xl items-center justify-between gap-6 px-4 sm:px-6 lg:px-8 xl:max-w-7xl xl:px-6">
        <Lockup />

        {/* The desktop nav starts at xl (1280px), not lg: eight labels do not hold one line below
            that. The hamburger below xl carries all eight links plus Sign in, so the md-to-xl band
            is not left without navigation. See the measurement note below the CTA cluster. */}
        <nav className="hidden xl:flex xl:items-center xl:gap-4">
          {siteConfig.nav.map((item) => {
            const active = pathname === item.href;
            return (
              <NavItem
                key={item.href}
                item={item}
                aria-current={active ? 'page' : undefined}
                className={`relative whitespace-nowrap py-2 font-mono text-xs uppercase tracking-eyebrow transition-colors hover:text-ink ${
                  active ? 'text-ink' : 'text-ink-muted'
                }`}
              >
                {nav(item.key)}
                <span
                  aria-hidden="true"
                  className={`absolute inset-x-0 -bottom-px h-px bg-accent transition-opacity ${
                    active ? 'opacity-100' : 'opacity-0'
                  }`}
                />
              </NavItem>
            );
          })}
        </nav>

        {/* WHY THE DESKTOP NAV STARTS AT xl (D-10). The labels plus the CTA cluster wrap between
            1024px and 1180px in both signup states: "HOW IT / WORKS", "SIGN / IN" and "Start / free"
            break across lines. The cluster stays at md because it fits beside the lockup and the
            hamburger; only the nav moves. Between md and xl the hamburger carries every link plus
            Sign in, so no band loses navigation.
            WHY THE ROW IS WIDER AT xl AND HOLDS A gap-6. Italian labels are longer than English: at
            max-w-6xl the nav ran into the cluster, "CONTATTI" touching "ACCEDI" with 0px between them
            at every width from 1280px up, because the row stops growing at its max-width. So the row
            widens to max-w-7xl at xl, nothing in it shrinks, and the row's own gap-6 is the floor
            between the three groups. The widest state is Italian with signup off ("App · In arrivo"):
            the tighter nav gap and padding at xl keep it inside the row at a 1280px window even with
            a visible 15px scrollbar. e2e/tests/site/header-spacing.spec.ts measures it per locale. */}
        <div className="hidden shrink-0 items-center gap-3 whitespace-nowrap md:flex xl:gap-5">
          {siteConfig.signupEnabled ? (
            // A plain <a> inside, not next/link: /app/* is the other Next.js app and a client-side
            // navigation inside this app's router would 404 (see components/ui/Button.tsx). It
            // carries the visitor's audience and campaign like the signup links do.
            <SignInLink className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted transition-colors hover:text-ink">
              {t('signIn')}
            </SignInLink>
          ) : (
            <span className="border border-line px-3 py-1.5 font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
              {t('appComingSoon')}
            </span>
          )}
          <PrimaryCta location="header" variant="accent" skew />
        </div>

        <button
          type="button"
          aria-expanded={menuOpen}
          aria-controls="mobile-nav"
          aria-label={menuOpen ? t('closeMenu') : t('openMenu')}
          onClick={() => setMenuOpen((open) => !open)}
          className="flex h-11 w-11 items-center justify-center border border-line text-ink hover:bg-paper-2 xl:hidden"
        >
          {menuOpen ? (
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" aria-hidden="true" focusable="false">
              <path d="M6 6L18 18M18 6L6 18" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" />
            </svg>
          ) : (
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" aria-hidden="true" focusable="false">
              <path d="M4 7H20M4 12H20M4 17H20" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" />
            </svg>
          )}
        </button>
      </div>

      {menuOpen && (
        <div
          id="mobile-nav"
          className="absolute inset-x-0 top-20 w-full border-b border-line bg-paper px-4 pb-6 pt-2 shadow-sm xl:hidden"
        >
          <nav className="flex flex-col">
            {siteConfig.nav.map((item) => (
              <NavItem
                key={item.href}
                item={item}
                onClick={() => setMenuOpen(false)}
                className="flex min-h-11 items-center border-b border-line font-mono text-sm uppercase tracking-eyebrow text-ink-2"
              >
                {nav(item.key)}
              </NavItem>
            ))}
            {siteConfig.signupEnabled ? (
              <SignInLink
                onClick={() => setMenuOpen(false)}
                className="flex min-h-11 items-center border-b border-line font-mono text-sm uppercase tracking-eyebrow text-ink-muted"
              >
                {t('signIn')}
              </SignInLink>
            ) : (
              <span className="flex min-h-11 items-center border-b border-line font-mono text-sm uppercase tracking-eyebrow text-ink-muted">
                {t('appComingSoon')}
              </span>
            )}
            <PrimaryCta
              location="header-mobile"
              variant="accent"
              block
              className="mt-4"
              onNavigate={() => setMenuOpen(false)}
            />
          </nav>
        </div>
      )}
    </header>
  );
}
