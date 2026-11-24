// The picture Google's merchant-listing and Product rules require on every Product-like node
// (`image` is a critical field: without it the page loses its listing). One file, one source: the
// 512 px app icon built from the /brand mark (see docs/brand.md), served from `public/`. Pure
// (no `@/` imports) so `node --test` can load it.
export const PRODUCT_IMAGE_PATH = '/favicon-512.png';

/** Absolute URL of the product image on the given origin, as schema.org requires. */
export function productImageUrl(origin: string): string {
  return new URL(PRODUCT_IMAGE_PATH, origin).toString();
}
