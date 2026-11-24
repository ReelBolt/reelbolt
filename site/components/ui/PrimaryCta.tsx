'use client';

import { Button } from '@/components/ui/Button';
import type { ButtonSize, ButtonVariant } from '@/components/ui/button-styles';
import { useSignupHref } from '@/components/analytics/useSignupHref';
import type { LandingPersona } from '@/lib/attribution-persona';
import { siteConfig } from '@/lib/site-config';
import { sendCtaClick } from '@/lib/site-events';
import { trackEvent } from '@/lib/gtag';
import { useT } from '@/i18n';

interface PrimaryCtaProps {
  /**
   * Where on the page this CTA sits: the only dimension the GA event carries, and the `detail` of
   * the first-party `site.cta_click`. A lower-case token (`hero`, `pricing-creator`, `for-creators-hero`).
   */
  location: string;
  /**
   * The audience this CTA speaks to, on an audience page, an audience section or a post written
   * for one audience. The signup link then carries `?for=<persona>`, unless the link the visitor
   * arrived through named an audience of its own, which wins while it holds. Without it the link
   * carries the audience remembered for the visit, if any (see `useSignupHref`). Ignored while the
   * CTA leads to the contact page.
   */
  persona?: LandingPersona;
  /** Defaults to the accent treatment a primary action gets. */
  variant?: ButtonVariant;
  size?: ButtonSize;
  block?: boolean;
  skew?: boolean;
  className?: string;
  /** Client call sites that must also react to the click (the mobile menu closes itself). */
  onNavigate?: () => void;
  /**
   * Overrides only the destination, keeping this CTA's label and GA event. The pricing page needs
   * it: each plan's button carries the plan it starts (`/app/signup?plan=creator`) while staying
   * the same "Start free" action in reports. The D25 switch still decides the destination's shape —
   * callers pass the flag-derived href, never a literal.
   */
  href?: string;
}

/**
 * The one primary call to action, in whichever state this build ships (see `siteCtas` in
 * `lib/site-config`): "Start free" once self-serve signup is live, the contact path until then.
 * Funnelling every call site through here is what keeps the D25 switch in a single place — no page
 * can hardcode "Start free" and outlive the flag.
 *
 * The click fires the CTA's GA4 event. `trackEvent` no-ops unless the visitor granted consent
 * (see `GoogleAnalytics`), so no custom event is sent for anyone who has not opted in; the tag's
 * own cookieless page-view signal is separate and described in the privacy policy. It also sends
 * the first-party `site.cta_click` (same consent gate, `lib/site-events`).
 *
 * The server-rendered href is the plain one. In the browser a link to the signup page also carries
 * the audience and the campaign parameters of the visitor's landing, in the URL only; nothing is
 * stored for it (`useSignupHref`).
 */
export function PrimaryCta({ location, persona, variant = 'accent', onNavigate, href, ...button }: PrimaryCtaProps) {
  const { msgKey, href: configuredHref, event } = siteConfig.primaryCta;
  const t = useT('layout.cta');
  const target = useSignupHref(href ?? configuredHref, persona);

  return (
    <Button
      {...button}
      variant={variant}
      href={target}
      onClick={() => {
        trackEvent(event, { location });
        sendCtaClick(location);
        onNavigate?.();
      }}
    >
      {t(msgKey)}
    </Button>
  );
}
