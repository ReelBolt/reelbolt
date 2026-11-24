// @vitest-environment node

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createHmac, randomBytes } from 'crypto';

const jar = vi.hoisted(() => ({ token: undefined as string | undefined }));
vi.mock('next/headers', () => ({
  cookies: async () => ({ get: (name: string) => (name === 'reelbolt_token' && jar.token ? { value: jar.token } : undefined) }),
}));

import { GET } from './route';

const KEY = randomBytes(32).toString('hex'); // generated per run: no key literal in source
const b64 = (v: unknown) => Buffer.from(JSON.stringify(v)).toString('base64url');
function mint(claims: Record<string, unknown> = {}): string {
  const head = b64({ alg: 'HS256', typ: 'JWT' });
  const body = b64({
    sub: '3f2b8c1e-5d4a-4e6f-9a7b-1c2d3e4f5a6b',
    email: 'a@b.co',
    iss: 'reelbolt-api',
    aud: ['reelbolt-inference'],
    exp: Math.floor(Date.now() / 1000) + 3600,
    ...claims,
  });
  const sig = createHmac('sha256', KEY).update(`${head}.${body}`).digest('base64url');
  return `${head}.${body}.${sig}`;
}

beforeEach(() => {
  vi.stubEnv('JWT_SIGNING_KEY', KEY);
  vi.stubEnv('JWT_ISSUER', 'reelbolt-api');
  vi.stubEnv('JWT_AUDIENCE', 'reelbolt-inference');
  jar.token = undefined;
});

describe('GET /api/auth/me', () => {
  it('answers the verified platform-admin bit', async () => {
    jar.token = mint({ platformAdmin: true });
    const response = await GET();
    expect(response.status).toBe(200);
    expect((await response.json()).user.isPlatformAdmin).toBe(true);
  });

  it.each([
    ['wrong issuer', { iss: 'evil' }],
    ['wrong audience', { aud: ['other'] }],
    ['no exp', { exp: undefined }],
    ['kind=runner and a device sub', { kind: 'runner', sub: 'device:3f2b8c1e-5d4a-4e6f-9a7b-1c2d3e4f5a6b' }],
    ['a non-uuid sub', { sub: 'not-a-uuid' }],
  ])('rejects a token with %s', async (_label, claims) => {
    jar.token = mint({ platformAdmin: true, ...claims });
    const response = await GET();
    expect(response.status).toBe(401);
    expect((await response.json()).user).toBeNull();
  });

  it('rejects a missing token', async () => {
    expect((await GET()).status).toBe(401);
  });

  it.each([
    ['200', () => mint()],
    ['401 (invalid token)', () => mint({ iss: 'evil' })],
    ['401 (no token)', () => undefined],
  ])('marks the %s answer private, no-store', async (_label, token) => {
    jar.token = token();
    const response = await GET();
    expect(response.headers.get('cache-control')).toBe('private, no-store');
  });

  it('marks the 500 answer (no signing key) private, no-store', async () => {
    vi.stubEnv('JWT_SIGNING_KEY', '');
    jar.token = mint();
    const response = await GET();
    expect(response.status).toBe(500);
    expect(response.headers.get('cache-control')).toBe('private, no-store');
  });
});
