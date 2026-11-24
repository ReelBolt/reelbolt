// What a visitor asked for before they had an account, kept across the round trip that creates it:
// the plan a pricing card started (`?plan=`) and the page they were on their way to (`?next=`).
//
// The round trip leaves this app: a provider sign-in goes to Google or GitHub and comes back
// through the API, and an email sign-up continues from a link in the inbox that carries only its
// token. Neither can hold these two values today, so without this they were dropped and a person
// who pressed "Choose Pro" (or followed a link to a page behind sign-in) arrived with the request
// gone.
//
// This is functional storage, not measurement: one small localStorage entry, written only when the
// person starts creating an account (submits the form or presses a provider button), read once
// after sign-in in the same browser, never sent anywhere, and removed when used or when it lapses.
// It holds no address: an email sign-up is bound to a local hash of the address typed (the same
// local key the onboarding mirror uses), so another person signing in on this browser never
// inherits it; a provider sign-up has no address to bind to and is honoured only for the few
// minutes the provider round trip takes. Both values are sanitized again on the way out.
//
// A person who finishes on another device, or in another browser, simply arrives without it, as
// before. Carrying the two values through the OAuth `state` and the emailed link would cover that
// case too; that lives in the API.

import { userKeyFromEmail } from '@/lib/telemetry/identity';
import { safeNextRedirect } from './redirect';
import { sanitizePlan, type SignupCarry } from './signup-carry';

export const SIGNUP_INTENT_KEY = 'rb-signup-intent:v1';

/** An email sign-up waits for the inbox: the link lasts a day and can be sent again. */
export const EMAIL_INTENT_TTL_MS = 3 * 24 * 60 * 60 * 1000;
/** A provider sign-up comes straight back: anything older is not this sign-up. */
export const PROVIDER_INTENT_TTL_MS = 30 * 60 * 1000;

export interface SignupIntent {
  plan?: string;
  next?: string;
}

interface StoredIntent extends SignupIntent {
  /** When it was written (epoch milliseconds). */
  at: number;
  /** The local key of the address an email sign-up was sent for; absent for a provider sign-up. */
  who?: string;
}

function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null;
  }
}

function readStored(): StoredIntent | null {
  try {
    const raw = storage()?.getItem(SIGNUP_INTENT_KEY);
    if (!raw) return null;
    const value = JSON.parse(raw) as Record<string, unknown>;
    if (typeof value !== 'object' || value === null || typeof value.at !== 'number') return null;
    const out: StoredIntent = { at: value.at };
    const plan = sanitizePlan(typeof value.plan === 'string' ? value.plan : undefined);
    if (plan) out.plan = plan;
    const next = safeNextRedirect(value.next);
    if (next) out.next = next;
    if (typeof value.who === 'string' && value.who) out.who = value.who;
    return out;
  } catch {
    return null;
  }
}

function write(value: StoredIntent | null): void {
  try {
    const store = storage();
    if (!store) return;
    if (value && (value.plan || value.next)) store.setItem(SIGNUP_INTENT_KEY, JSON.stringify(value));
    else store.removeItem(SIGNUP_INTENT_KEY);
  } catch {
    // storage blocked or full: the person arrives without the two values, as before
  }
}

/**
 * Remembers what the person's link carried, at the moment they start creating an account. `email`
 * is the address an email sign-up was sent for; leave it out for a provider button. A link that
 * carried neither value writes nothing and leaves an earlier entry alone: that one is bound to its
 * own address (or lapses within minutes), so it can only ever reach the person it belongs to.
 * Never throws.
 */
export function rememberSignupIntent(
  carry: Pick<SignupCarry, 'plan' | 'next'>,
  options: { email?: string } = {},
  now: number = Date.now(),
): void {
  if (!carry.plan && !carry.next) return;
  const who = userKeyFromEmail(options.email);
  write({
    at: now,
    ...(carry.plan ? { plan: carry.plan } : {}),
    ...(carry.next ? { next: carry.next } : {}),
    ...(who ? { who } : {}),
  });
}

/**
 * The intent that belongs to the signed-in person (`userKey` is `userKeyFromEmail` of their
 * address), or null. An entry for another address, a lapsed one and a malformed one are removed
 * and answer null.
 */
export function readSignupIntent(userKey: string, now: number = Date.now()): SignupIntent | null {
  if (!userKey) return null;
  const stored = readStored();
  if (!stored) return null;
  const age = now - stored.at;
  const fresh = stored.who
    ? stored.who === userKey && age >= 0 && age <= EMAIL_INTENT_TTL_MS
    : age >= 0 && age <= PROVIDER_INTENT_TTL_MS;
  // A provider sign-up binds to whoever signs in next, once: from then on it is theirs alone.
  if (fresh && !stored.who) write({ ...stored, who: userKey });
  if (!fresh) {
    write(null);
    return null;
  }
  if (!stored.plan && !stored.next) return null;
  return { ...(stored.plan ? { plan: stored.plan } : {}), ...(stored.next ? { next: stored.next } : {}) };
}

/** Forgets one of the two values once it has been used (or declined); the other one stays. */
export function clearSignupIntent(part: keyof SignupIntent): void {
  const stored = readStored();
  if (!stored) return;
  const rest: StoredIntent = { ...stored };
  delete rest[part];
  write(rest);
}
