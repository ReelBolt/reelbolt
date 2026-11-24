/**
 * Whether this build collects client-side telemetry at all (alpha-launch T-018).
 *
 * A build-time switch, so it is inlined into the browser bundle and a self-host deployment that has
 * not opted in behaves exactly as it did before this module existed: nothing is collected, and the
 * app-side consent surface renders nothing. The alpha environment sets
 * `NEXT_PUBLIC_TELEMETRY_ENABLED=true`.
 *
 * This is the outer gate, not the consent gate. Even when it is true, nothing leaves the browser
 * until the person has opted in (see `consent.ts`).
 */
export function telemetryEnabled(): boolean {
  return process.env.NEXT_PUBLIC_TELEMETRY_ENABLED === 'true';
}
