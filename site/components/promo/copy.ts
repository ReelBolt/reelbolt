// The English fallback for a promotion without a headline. Plain data only (no React or Next imports), so
// lib/promotions/active.ts can read the fallback offer text from the same place.

export const PROMO_COPY = {
  // Used when a promotion has no headline: "{percentOff}% off" for a percentage, this otherwise.
  // The banner, newsletter and price chrome moved to the catalogue (messages/<locale>/promo.json).
  fallbackOffer: 'A limited-time offer is on',
} as const;
