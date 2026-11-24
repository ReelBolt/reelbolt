import type { BillingSummary } from "@/lib/types/billing";
import { hasLivePaidPlan } from "./subscription";

/** What was being bought when the app opened a checkout, when known. */
export type CheckoutTarget =
  | { kind: "plan"; planKey: string }
  | { kind: "topUp"; credits: number };

/** The billing state at the moment the app opened a checkout: the baseline the return page compares against. */
export interface CheckoutSnapshot {
  planKey: string | null;
  balance: number;
  livePaid: boolean;
  at: number;
  target: CheckoutTarget | null;
}

const KEY = "reelbolt.checkout-snapshot";
/** A snapshot older than this belongs to a checkout that was abandoned; it proves nothing. */
const MAX_AGE_MS = 2 * 60 * 60 * 1000;

function store(): Storage | null {
  try {
    return typeof window === "undefined" ? null : window.sessionStorage;
  } catch {
    return null;
  }
}

export function takeSnapshot(summary: BillingSummary, target: CheckoutTarget | null, now = Date.now()): CheckoutSnapshot {
  return {
    planKey: summary.subscription?.planKey ?? null,
    balance: summary.credits.balance,
    livePaid: hasLivePaidPlan(summary),
    at: now,
    target,
  };
}

export function saveCheckoutSnapshot(snapshot: CheckoutSnapshot): void {
  try {
    store()?.setItem(KEY, JSON.stringify(snapshot));
  } catch {
    // storage full or blocked: the return page then stays neutral, which is the safe side
  }
}

export function readCheckoutSnapshot(now = Date.now()): CheckoutSnapshot | null {
  try {
    const raw = store()?.getItem(KEY);
    if (!raw) return null;
    const value = JSON.parse(raw) as Partial<CheckoutSnapshot>;
    if (typeof value.at !== "number" || typeof value.balance !== "number" || now - value.at > MAX_AGE_MS) return null;
    return {
      planKey: typeof value.planKey === "string" ? value.planKey : null,
      balance: value.balance,
      livePaid: value.livePaid === true,
      at: value.at,
      target: value.target ?? null,
    };
  } catch {
    return null;
  }
}

export function clearCheckoutSnapshot(): void {
  try {
    store()?.removeItem(KEY);
  } catch {
    // nothing to clear
  }
}

/**
 * Whether the summary now shows the purchase the snapshot was taken for. With a known target the
 * match is on that plan or pack; without one, any plan change or credit increase counts.
 */
export function purchaseVisible(before: CheckoutSnapshot, summary: BillingSummary): boolean {
  const now = takeSnapshot(summary, null);
  if (before.target?.kind === "plan") {
    return now.planKey === before.target.planKey && now.livePaid && (before.planKey !== now.planKey || !before.livePaid);
  }
  if (before.target?.kind === "topUp") {
    return now.balance - before.balance >= before.target.credits;
  }
  return now.planKey !== before.planKey || now.balance > before.balance || (now.livePaid && !before.livePaid);
}
