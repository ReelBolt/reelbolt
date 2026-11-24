/**
 * The local key that makes a funnel event "once per user" (alpha-launch T-018).
 *
 * The session carries no user id (the `reelbolt_user` cookie holds an email and an org, not a
 * `sub`), so the email is the only stable per-person value on the device. It is **hashed**, not
 * stored raw: the key lands in `localStorage`, and a marker key is not worth writing an address
 * into. The hash is never sent — the payload carries only the event name — so this is a local
 * de-duplication key, not a collected identifier.
 */

/** cyrb53: a small, fast, well-distributed 53-bit string hash. Not cryptographic, and not used as one. */
function hash53(input: string): number {
  let h1 = 0xdeadbeef;
  let h2 = 0x41c6ce57;
  for (let i = 0; i < input.length; i += 1) {
    const ch = input.charCodeAt(i);
    h1 = Math.imul(h1 ^ ch, 2654435761);
    h2 = Math.imul(h2 ^ ch, 1597334677);
  }
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  return 4294967296 * (2097151 & h2) + (h1 >>> 0);
}

/**
 * A stable local key for a person's email, or `''` when there is none (signed out, or a cookie
 * without an address). An empty key makes `emitFunnelEvent` a no-op, which is the safe direction.
 */
export function userKeyFromEmail(email: string | null | undefined): string {
  const normalized = (email ?? '').trim().toLowerCase();
  if (!normalized) return '';
  return `u${hash53(normalized).toString(36)}`;
}
