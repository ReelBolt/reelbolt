// Which trust facts a page states, by the state of the signup switch (`siteCtas` in
// lib/site-config). Pure, so `node --test` can load it; `components/proof/TrustStrip` renders it.
//
// Two of the six facts are about STARTING a free account: "no card required" and "your first video
// is on us". On a build with signup off the page's only button leads to the contact page and the
// signup API answers 404, so those two would promise something nobody can do. The other four are
// true of the product whichever way a person gets in.

export const TRUST_ITEMS = ['noCard', 'firstVideo', 'reviewed', 'refund', 'speed', 'alpha'] as const;
export type TrustItem = (typeof TRUST_ITEMS)[number];

/** The facts that only hold while a visitor can start a free account from this site. */
export const SIGNUP_ONLY_TRUST_ITEMS: readonly TrustItem[] = ['noCard', 'firstVideo'];

export function trustItems(signupEnabled: boolean): readonly TrustItem[] {
  return signupEnabled ? TRUST_ITEMS : TRUST_ITEMS.filter((item) => !SIGNUP_ONLY_TRUST_ITEMS.includes(item));
}
