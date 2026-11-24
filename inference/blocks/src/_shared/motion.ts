// ReelBolt block library: shared motion helpers (MIT, see LICENSE in the blocks directory).
// Copied into src/blocks/_shared/ by AddBlock; blocks import it as '../_shared/motion.ts'.

/** Clamp a number to the range 0..1. */
export const clamp01 = (value: number): number => Math.min(1, Math.max(0, value));

/** Cubic ease-out: fast start, soft landing. Input and output are 0..1. */
export const easeOutCubic = (t: number): number => 1 - Math.pow(1 - clamp01(t), 3);

/** Cubic ease-in-out. Input and output are 0..1. */
export const easeInOutCubic = (t: number): number => {
  const x = clamp01(t);
  return x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2;
};
