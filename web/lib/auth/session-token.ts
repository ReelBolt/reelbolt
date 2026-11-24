// Server only: uses node:crypto and the signing key. Never import from a client component.
import { createHmac, timingSafeEqual } from 'crypto';

/**
 * Verifies the httpOnly `reelbolt_token` session JWT exactly as the Go API does
 * (api/services/jwt_service.go): HS256 only, constant-time signature compare, `iss`/`aud` equal to
 * the deployment's JWT_ISSUER/JWT_AUDIENCE, and `exp` present and in the future. This, not the
 * editable `reelbolt_user` cookie, is the authority for platform-admin UI (proxy.ts, /api/auth/me).
 */
export type SessionClaims = Record<string, unknown>;

export interface SessionTokenConfig {
  secret: string;
  issuer: string;
  audience: string;
}

/** The verifier settings from the environment; null when no signing key is configured (fail closed). */
export function sessionTokenConfig(): SessionTokenConfig | null {
  const secret = process.env.JWT_SIGNING_KEY;
  if (!secret) return null;
  return {
    secret,
    // Same defaults as api/config/config.go.
    issuer: process.env.JWT_ISSUER || 'reelbolt-api',
    audience: process.env.JWT_AUDIENCE || 'reelbolt-inference',
  };
}

// A user session's `sub` is the user's uuid (canonical form, as the Go API signs it).
const USER_SUBJECT = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function base64UrlDecode(input: string): Buffer {
  const padded = input.replace(/-/g, '+').replace(/_/g, '/');
  const pad = padded.length % 4 === 0 ? '' : '='.repeat(4 - (padded.length % 4));
  return Buffer.from(padded + pad, 'base64');
}

function parseObject(b64: string): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(base64UrlDecode(b64).toString('utf8'));
    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

export function verifySessionToken(
  token: string | undefined,
  config: SessionTokenConfig | null,
  nowMs: number = Date.now(),
): SessionClaims | null {
  if (!token || !config) return null;
  const parts = token.split('.');
  if (parts.length !== 3) return null;
  const [headerB64, payloadB64, signatureB64] = parts;

  const header = parseObject(headerB64);
  if (!header || header.alg !== 'HS256') return null;

  const expected = createHmac('sha256', config.secret).update(`${headerB64}.${payloadB64}`).digest();
  const actual = base64UrlDecode(signatureB64);
  if (expected.length !== actual.length || !timingSafeEqual(expected, actual)) return null;

  const claims = parseObject(payloadB64);
  if (!claims) return null;

  if (typeof claims.exp !== 'number' || nowMs >= claims.exp * 1000) return null;
  if (claims.iss !== config.issuer) return null;
  const aud = claims.aud;
  const audOk = typeof aud === 'string' ? aud === config.audience : Array.isArray(aud) && aud.includes(config.audience);
  if (!audOk) return null;

  // Runner/device tokens (jwt_service.go GenerateRunnerToken: kind=runner, sub=device:<id>) share the
  // key, iss and aud. Like Go's middleware.Auth, only a user session counts: no `kind`, uuid `sub`.
  if (claims.kind !== undefined) return null;
  if (typeof claims.sub !== 'string' || !USER_SUBJECT.test(claims.sub)) return null;

  return claims;
}

/** The platform-admin bit from verified claims (`isAdmin` is the one-release alias). */
export function isPlatformAdminClaims(claims: SessionClaims | null): boolean {
  if (!claims) return false;
  return claims.platformAdmin === true || claims.isAdmin === true;
}
