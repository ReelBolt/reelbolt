/**
 * Sizing the picture editor's stage so the whole editor fits the window (audit d6 N1: at 1440 × 900 the
 * stage's bottom edge and the zoom bar sat below the fold because a fixed `100dvh - 210px` guessed the
 * page chrome too small). The editor measures what is really above and below its layout instead.
 */

/** The smallest stage worth showing; a shorter window scrolls rather than squeezing it further. */
export const STAGE_MIN_HEIGHT = 420;

/** The smallest phone stage (it is also the CSS `min-height` there): a taller chrome scrolls rather than squeezing it. */
export const PHONE_STAGE_MIN_HEIGHT = 300;

/**
 * The stage height that ends the layout at the window's bottom edge: the window less what sits above the
 * layout (`top`, from the top of the page) and the padding below it (`below`), in CSS pixels.
 */
export function stageHeight(viewportHeight: number, top: number, below: number): number {
  return Math.max(STAGE_MIN_HEIGHT, Math.floor(viewportHeight - top - below));
}

const px = (value: string): number => {
  const n = parseFloat(value);
  return Number.isFinite(n) ? n : 0;
};

/**
 * The space under `el` up to the bottom of the page: its own bottom margin, then the bottom padding,
 * border and margin of every box around it. The editor's layout is the last thing on its page, so this
 * is everything below it.
 */
export function spaceBelow(el: Element): number {
  let total = px(getComputedStyle(el).marginBottom);
  for (let box = el.parentElement; box; box = box.parentElement) {
    const s = getComputedStyle(box);
    total += px(s.paddingBottom) + px(s.borderBottomWidth) + px(s.marginBottom);
  }
  return total;
}
