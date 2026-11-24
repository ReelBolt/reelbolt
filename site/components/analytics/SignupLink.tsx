'use client';

import type { ReactNode } from 'react';
import type { LandingPersona } from '@/lib/attribution-persona';
import { sendCtaClick } from '@/lib/site-events';
import { siteConfig } from '@/lib/site-config';
import { useSignupHref } from './useSignupHref';

interface SignupLinkProps {
  /** Defaults to the signup page. Pass a flag-derived href (`planCtaHref`, …) to keep its query. */
  href?: string;
  /** This link's own audience; the arrival link's audience wins over it, and without either the link carries the one remembered for the visit. */
  persona?: LandingPersona;
  /**
   * Where the link sits (`footer`, `for-creators-hero`, …). When given, a click sends the
   * first-party `site.cta_click` with it, for a visitor who accepted cookies. No Google Analytics
   * event is sent from here: that stays with `PrimaryCta` and `PlanCtaButton`.
   */
  location?: string;
  className?: string;
  children: ReactNode;
}

/**
 * A plain `<a>` to the signup page for places that cannot use `PrimaryCta` (a text link, a custom
 * label). It gets the same link decoration as every other signup link: see `useSignupHref`.
 * A plain anchor on purpose: `/app/*` is the dashboard, a different app that `next/link` cannot route to.
 */
export function SignupLink({ href = siteConfig.signupPath, persona, location, className, children }: SignupLinkProps) {
  const decorated = useSignupHref(href, persona);
  return (
    <a href={decorated} className={className} onClick={location ? () => sendCtaClick(location) : undefined}>
      {children}
    </a>
  );
}

/**
 * The "Sign in" link. It carries the audience, the campaign and its landing page like a signup link does, because
 * the sign-in page can create an account too: a visitor who taps "Sign in" and then continues with
 * a provider gets a new account when none exists, and it should know where they came from.
 * A plain `<a>` for the same reason as above. No event is sent: signing in is not the call to action.
 */
export function SignInLink({
  className,
  onClick,
  children,
}: {
  className?: string;
  onClick?: () => void;
  children: ReactNode;
}) {
  const href = useSignupHref(siteConfig.loginPath);
  return (
    <a href={href} className={className} onClick={onClick}>
      {children}
    </a>
  );
}
