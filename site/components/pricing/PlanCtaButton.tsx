'use client';

import { Button } from '@/components/ui/Button';
import type { ButtonVariant } from '@/components/ui/button-styles';
import { useSignupHref } from '@/components/analytics/useSignupHref';
import type { LandingPersona } from '@/lib/attribution-persona';
import { trackEvent } from '@/lib/gtag';
import { siteConfig } from '@/lib/site-config';
import { sendCtaClick } from '@/lib/site-events';

/**
 * A plan card's button. `PrimaryCta` always prints the site-wide label, and a plan card needs its
 * own ("Choose Creator"), so this carries the label and keeps the same GA event `PrimaryCta` fires,
 * plus the same first-party `site.cta_click` and the same signup-link decoration (the plan in the
 * href's query is kept; see `useSignupHref`).
 * It is the only client leaf on the pricing cards; the card itself stays a server component.
 */
export function PlanCtaButton({
  location,
  href,
  label,
  variant,
  persona,
}: {
  location: string;
  href: string;
  label: string;
  variant: ButtonVariant;
  persona?: LandingPersona;
}) {
  const target = useSignupHref(href, persona);
  return (
    <Button
      href={target}
      variant={variant}
      size="lg"
      block
      onClick={() => {
        trackEvent(siteConfig.primaryCta.event, { location });
        sendCtaClick(location);
      }}
    >
      {label}
    </Button>
  );
}
