// The launch-offer strip's copy, in one place so the pricing page renders it and `node --test` can
// assert it. This module lives outside `lib/pricing/**` on purpose: that directory is the vendored
// catalog and its pure derivations, and it must not grow presentation copy. Nothing here is a price
// — the strip states the discount, and every figure a reader sees still comes from the catalog.
import { FIRST_VIDEO_GRANT_CREDITS } from './legal-placeholders.ts';
import { SIGNUP_PATH } from './site-config.ts';

/**
 * The last day the founder price is open to new subscribers (23:59 Europe/Rome). The one place the
 * date is written: the strip's "until" text and the hide-after-the-end check both read it.
 */
export const FOUNDER_PRICE_ENDS_ON = '31 January 2027';
/** The same moment as an instant: 23:59 on 31 January 2027 in Rome (CET, UTC+1). */
export const FOUNDER_PRICE_ENDS_AT = '2027-01-31T23:59:00+01:00';

/** True while new subscribers can still get the founder price. */
export function founderPriceOpen(now: Date = new Date()): boolean {
  return now.getTime() <= Date.parse(FOUNDER_PRICE_ENDS_AT);
}

/**
 * Whether the strip shows. `openAtBuild` is what the static HTML was built with; the strip can only
 * ever go from shown to hidden, so a closed build stays closed and an open one closes at the end.
 */
export function founderStripVisible(openAtBuild: boolean, now: Date): boolean {
  return openAtBuild && founderPriceOpen(now);
}

/** The words the strip renders. The page maps these to markup; it repeats none of them. */
/** The numbers the founder-price and money-back lines state; the catalogue strings take them as parameters. */
export const FOUNDER_PERCENT_OFF = 50;
export const MONEY_BACK_DAYS = 14;

export const LAUNCH_OFFER = {
  eyebrow: 'Founder price',
  heading: `${FOUNDER_PERCENT_OFF}% off for new subscribers`,
  body: 'Open to new subscribers until 31 January 2027. The discount is applied automatically at checkout. There is no code to enter.',
  ctaLocation: 'pricing-launch-offer',
} as const;

/** The "until" text, built from the one date constant. */
export const FOUNDER_PRICE_UNTIL = `Open to new subscribers until ${FOUNDER_PRICE_ENDS_ON}`;

/** Money-back line under the plans. Links to the refunds clause of the Terms. */
export const MONEY_BACK = {
  text: `${MONEY_BACK_DAYS}-day money-back guarantee`,
  href: '/legal/terms#refunds',
} as const;

/** "First video on us" line, shown beside the trial call to action. */
export const FIRST_VIDEO_LINE = `Your first video is on us: ${FIRST_VIDEO_GRANT_CREDITS} credits when you verify your email. Trial exports are 720p with a watermark.`;

/**
 * Where the strip's call to action points. The strip carries the site's one primary action, so it
 * follows the D25 switch the same way `PrimaryCta` defaults to: the signup route once self-serve
 * signup is live, the contact path until then. Made explicit here so the strip's destination is
 * testable without rendering the page, and it returns the flag-derived route rather than a literal.
 */
export function launchOfferCtaHref(signupEnabled: boolean): string {
  return signupEnabled ? SIGNUP_PATH : '/contact';
}
