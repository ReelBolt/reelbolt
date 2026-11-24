/**
 * What a paid picture tool tells the user before and after it runs. One estimate shape, from the
 * capabilities route (and the graphics list), in plain words: "about 4.5 credits" before, "Used 4.5
 * credits" after, nothing at all when billing is off (never "0 credits").
 */

export interface PictureEstimate {
  billingEnabled: boolean;
  credits?: number;
  balanceCredits?: number;
  affordable: boolean;
  unavailableReason?: string | null;
  reason?: string | null;
}

/** "1 credit", "4.5 credits": at most two decimals, no trailing zeros. */
export function creditsText(credits: number): string {
  const rounded = Math.round(credits * 100) / 100;
  return `${rounded} ${rounded === 1 ? 'credit' : 'credits'}`;
}

/** "about 4.5 credits", or null when nothing is charged or no figure is known. */
export function estimateLine(estimate: PictureEstimate | null | undefined): string | null {
  if (!estimate || !estimate.billingEnabled || estimate.credits === undefined || !(estimate.credits > 0)) return null;
  return `about ${creditsText(estimate.credits)}`;
}

/** A Run button's words with its price: "Erase the painted area, about 4.5 credits"; just the action when no price is known. */
export function runLabel(action: string, estimate: PictureEstimate | null | undefined): string {
  const line = estimateLine(estimate);
  return line && !isUnaffordable(estimate) ? `${action}, ${line}` : action;
}

/** True when billing is on and the balance does not cover the run. */
export function isUnaffordable(estimate: PictureEstimate | null | undefined): boolean {
  return !!estimate && estimate.billingEnabled && !estimate.affordable;
}

/** The sentence beside a disabled Run button, or null when the run is affordable. */
export function unaffordableText(estimate: PictureEstimate | null | undefined): string | null {
  if (!estimate || !isUnaffordable(estimate)) return null;
  const need = estimate.credits !== undefined ? creditsText(estimate.credits) : 'more credits';
  const have = estimate.balanceCredits !== undefined ? ` You have ${creditsText(estimate.balanceCredits)}.` : '';
  return `This needs ${need}.${have} Add credits to run it.`;
}

/** "Used 4.5 credits" after a run, or null when nothing was charged. */
export function usedLine(chargedCredits: number | null | undefined): string | null {
  if (chargedCredits === null || chargedCredits === undefined || !(chargedCredits > 0)) return null;
  return `Used ${creditsText(chargedCredits)}`;
}

/** Reads the `X-Charged-Credits` header value ("4.5"), or null. */
export function parseChargedHeader(value: string | null | undefined): number | null {
  if (!value) return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/** The message for a tool no provider serves: the admin can fix it, a member asks one. */
export function noProviderText(canConfigure: boolean): string {
  return canConfigure
    ? 'Picture tools are not set up in this workspace yet.'
    : 'Ask your workspace admin to set up picture tools.';
}
