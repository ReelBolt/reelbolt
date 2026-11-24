/**
 * The app's breakpoints, one place. They are the editor's device classes (`lib/editing/device-class.ts`):
 * phone is narrower than 600 px, tablet is 600 to 1100 px, desktop is wider than 1100 px. New and touched
 * code reads these instead of declaring its own `48em` or `1200px`.
 *
 * CSS modules cannot import a constant, so each query below is quoted next to the rule that mirrors it
 * (`AppRunPage.module.css`, `header.module.css`); `breakpoints.test.ts` pins the numbers.
 */
import {
  PHONE_MAX_LANDSCAPE_HEIGHT,
  PHONE_MAX_WIDTH,
  PHONE_MEDIA_QUERY,
  TABLET_MAX_WIDTH,
} from '@/lib/editing/device-class';

export { PHONE_MAX_LANDSCAPE_HEIGHT, PHONE_MAX_WIDTH, PHONE_MEDIA_QUERY, TABLET_MAX_WIDTH };

/** Phone, including a landscape phone: `(max-width: 599.98px), (max-height: 499.98px) and (pointer: coarse)`. */
export const PHONE_QUERY = PHONE_MEDIA_QUERY;

/** Phone or tablet (a single column layout): `(max-width: 1100px)`. */
export const TABLET_DOWN_QUERY = `(max-width: ${TABLET_MAX_WIDTH}px)`;

/** The smallest comfortable touch target, in CSS px. */
export const TOUCH_TARGET_PX = 44;
