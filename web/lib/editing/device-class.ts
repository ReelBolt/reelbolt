/**
 * The editor's device classes (remediation plan section 3, decision D1).
 *
 * - phone: narrower than 600 px, or shorter than 500 px with a coarse pointer (a landscape phone,
 *   about 844x390);
 * - tablet: 600-1100 px wide, or any coarse pointer above the phone rule;
 * - desktop: wider than 1100 px with a fine pointer.
 *
 * The predicate is pure so it can be tested without a browser; `PHONE_MEDIA_QUERY` is the same rule
 * as a CSS media query, for `useMediaQuery`.
 */
export type DeviceClass = 'phone' | 'tablet' | 'desktop';

export interface ViewportFacts {
  width: number;
  height: number;
  /** `(pointer: coarse)`: the primary pointer is a finger. */
  coarsePointer: boolean;
}

export const PHONE_MAX_WIDTH = 600;
export const PHONE_MAX_LANDSCAPE_HEIGHT = 500;
export const TABLET_MAX_WIDTH = 1100;

export function deviceClassOf({ width, height, coarsePointer }: ViewportFacts): DeviceClass {
  if (width < PHONE_MAX_WIDTH) return 'phone';
  if (coarsePointer && height < PHONE_MAX_LANDSCAPE_HEIGHT) return 'phone';
  if (coarsePointer || width <= TABLET_MAX_WIDTH) return 'tablet';
  return 'desktop';
}

export function isPhoneViewport(facts: ViewportFacts): boolean {
  return deviceClassOf(facts) === 'phone';
}

/** The phone rule as a media query (the `.98` keeps "< 600" exact at fractional widths). */
export const PHONE_MEDIA_QUERY = `(max-width: ${PHONE_MAX_WIDTH - 0.02}px), (max-height: ${PHONE_MAX_LANDSCAPE_HEIGHT - 0.02}px) and (pointer: coarse)`;
