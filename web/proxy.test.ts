// @vitest-environment node

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NextRequest } from 'next/server';

// The gate asks the API that owns legal_documents what the deployment is serving (F-7). Mocked
// here so the tests are about the comparison, not the read; legal-server.test.ts covers the read.
const { servedLegalVersions } = vi.hoisted(() => ({ servedLegalVersions: vi.fn() }));
vi.mock('@/lib/auth/legal-server', () => ({ servedLegalVersions }));

import { createHmac, randomBytes } from 'crypto';
import { proxy } from './proxy';

// The gate verifies the signed session token itself (lib/auth/session-token.ts), so the tests mint
// real ones with the deployment's key, issuer and audience.
const KEY = randomBytes(32).toString('hex'); // generated per run: no key literal in source
const ISS = 'reelbolt-api';
const AUD = 'reelbolt-inference';
const b64 = (v: unknown) => Buffer.from(JSON.stringify(v)).toString('base64url');
function mint(claims: Record<string, unknown> = {}, { alg = 'HS256', key = KEY } = {}): string {
  const head = b64({ alg, typ: 'JWT' });
  const body = b64({ sub: '3f2b8c1e-5d4a-4e6f-9a7b-1c2d3e4f5a6b', iss: ISS, aud: [AUD], exp: Math.floor(Date.now() / 1000) + 3600, ...claims });
  const sig = createHmac('sha256', key).update(`${head}.${body}`).digest('base64url');
  return `${head}.${body}.${sig}`;
}

// Next strips the '/app' basePath before matching (the same reason a matcher of '/dashboard' is
// what gates /app/dashboard), so the paths handed to proxy() are bare.
function request(path: string, cookies: Record<string, string> = {}) {
  const cookieHeader = Object.entries(cookies)
    .map(([name, value]) => `${name}=${value}`)
    .join('; ');
  return new NextRequest(new URL(`http://localhost${path}`), {
    headers: cookieHeader ? { cookie: cookieHeader } : {},
  });
}

function userCookie(user: Record<string, unknown>): string {
  return encodeURIComponent(JSON.stringify(user));
}

function signedIn(extra: Record<string, unknown> = {}, token: string = mint()) {
  return {
    reelbolt_token: token,
    reelbolt_user: userCookie({ email: 'a@b.co', mustChangePassword: false, ...extra }),
  };
}

const location = (response: Response) => new URL(response.headers.get('location') ?? '', 'http://localhost').pathname;

// What the deployment is serving unless a test says otherwise.
const SERVED = { termsVersion: '2026-10', privacyVersion: '2026-10' };

beforeEach(() => {
  vi.stubEnv('JWT_SIGNING_KEY', KEY);
  vi.stubEnv('JWT_ISSUER', ISS);
  vi.stubEnv('JWT_AUDIENCE', AUD);
  servedLegalVersions.mockReset();
  servedLegalVersions.mockResolvedValue(SERVED);
});

describe('proxy — public auth paths', () => {
  it.each([
    '/signup',
    '/verify-email',
    '/forgot-password',
    '/reset-password',
    '/invite/abc123',
    '/login',
    '/offline',
    '/unsubscribe?token=u1.k1.abc.def',
    '/marketing/confirm?token=c1.k1.abc.1.def',
  ])('lets an unauthenticated visitor reach %s', async (path) => {
    const response = await proxy(request(path));
    expect(response.status).toBe(200);
    expect(response.headers.get('location')).toBeNull();
  });

  // S-31: public pages are exact matches, so a lookalike path is not public.
  it.each([
    '/loginX',
    '/login/x',
    '/signup-admin',
    '/verify-emails',
    '/reset-password/x',
    '/offline/x',
    '/sw.js.map',
    '/invite',
    '/invitex',
    '/invitations',
    '/apix',
    '/unsubscribeX',
    '/unsubscribe/x',
    '/unsubscribe-all',
    '/marketing/confirmX',
    '/marketing/confirm/x',
    '/marketing',
    '/marketing/other',
  ])('redirects an unauthenticated visitor away from the lookalike %s', async (path) => {
    const response = await proxy(request(path));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/login');
  });

  it('still redirects an unauthenticated visitor away from a protected page', async () => {
    const response = await proxy(request('/dashboard'));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/login');
  });

  it('lets an unauthenticated visitor reach an auth API route', async () => {
    // The pages call /api/v1/auth/* themselves; gating the prefix would bounce the calls too.
    expect((await proxy(request('/api/v1/auth/signup'))).status).toBe(200);
  });
});

describe('proxy — platform admin guard', () => {
  const admin = () => mint({ platformAdmin: true });

  it('sends a non-admin away from /admin', async () => {
    const response = await proxy(request('/admin/users', signedIn()));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/dashboard');
    expect(new URL(response.headers.get('location') ?? '', 'http://localhost').searchParams.get('notice')).toBe('admin-only');
  });

  it('guards the marketplace admin page the same way', async () => {
    const denied = await proxy(request('/admin/marketplace', signedIn()));
    expect(denied.status).toBe(307);
    expect(location(denied)).toContain('/dashboard');
    expect((await proxy(request('/admin/marketplace', signedIn({}, admin())))).status).toBe(200);
  });

  it('admits a platform admin whose signed token carries platformAdmin', async () => {
    expect((await proxy(request('/admin/users', signedIn({ isPlatformAdmin: true }, admin())))).status).toBe(200);
  });

  it('treats the deprecated isAdmin token claim as a platform admin', async () => {
    expect((await proxy(request('/admin/users', signedIn({}, mint({ isAdmin: true }))))).status).toBe(200);
  });

  // platform-admin-access-incident: the reelbolt_user cookie is readable and writable by the browser.
  it('refuses an edited reelbolt_user cookie claiming isPlatformAdmin with a non-admin token', async () => {
    const response = await proxy(request('/admin/users', signedIn({ isPlatformAdmin: true, isAdmin: true })));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/dashboard');
  });

  it.each([
    ['a junk token', 'jwt'],
    ['an expired token', mint({ platformAdmin: true, exp: Math.floor(Date.now() / 1000) - 10 })],
    ['a token without exp', mint({ platformAdmin: true, exp: undefined })],
    ['a wrong-audience token', mint({ platformAdmin: true, aud: ['someone-else'] })],
    ['a wrong-issuer token', mint({ platformAdmin: true, iss: 'evil' })],
    ['a wrong-alg token', mint({ platformAdmin: true }, { alg: 'HS512' })],
    ['a runner token (kind=runner, sub=device:...)', mint({ platformAdmin: true, kind: 'runner', sub: 'device:3f2b8c1e-5d4a-4e6f-9a7b-1c2d3e4f5a6b' })],
    ['a token with any kind claim', mint({ platformAdmin: true, kind: 'session' })],
    ['a token whose sub is not a uuid', mint({ platformAdmin: true, sub: 'device:abc' })],
    ['a token signed with another key', mint({ platformAdmin: true }, { key: 'another-key-another-key-another-key' })],
  ])('treats %s with an admin cookie as signed out', async (_label, token) => {
    const response = await proxy(request('/admin/users', signedIn({ isPlatformAdmin: true }, token)));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/login');
  });

  it('treats an invalid token on any protected page as signed out', async () => {
    const response = await proxy(request('/dashboard', signedIn({}, 'jwt')));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/login');
  });

  it('treats every session as signed out when no signing key is configured', async () => {
    vi.stubEnv('JWT_SIGNING_KEY', '');
    const response = await proxy(request('/admin/users', signedIn({}, admin())));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/login');
  });

  it('lets an admin reach a public auth path without a redirect', async () => {
    expect((await proxy(request('/signup', signedIn({ isPlatformAdmin: true }, admin())))).status).toBe(200);
  });
});

describe('proxy — terms re-acceptance gate', () => {
  it('does not redirect a session whose cookie carries no terms version yet', async () => {
    expect((await proxy(request('/dashboard', signedIn()))).status).toBe(200);
    // Nothing to compare, so nothing is asked for either.
    expect(servedLegalVersions).not.toHaveBeenCalled();
  });

  it('does not redirect a session that already accepted the served version', async () => {
    const response = await proxy(request('/dashboard', signedIn({ termsVersion: SERVED.termsVersion })));
    expect(response.status).toBe(200);
    expect(servedLegalVersions).toHaveBeenCalled();
  });

  it('redirects a session that accepted an older version to /accept-terms', async () => {
    const response = await proxy(request('/dashboard', signedIn({ termsVersion: '2020-01' })));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/accept-terms');
  });

  // F-7. The version this session accepted is what the API recorded, and the version it is compared
  // against is what the API reports NOW. Neither is a build-time constant, so a publication the
  // built bundle never heard of is still a re-prompt — against the old code, which compared the
  // cookie to NEXT_PUBLIC_TERMS_VERSION, this returned 200 because the two agreed.
  it('re-prompts when the API publishes a version the built bundle never knew', async () => {
    servedLegalVersions.mockResolvedValue({ termsVersion: '2027-01', privacyVersion: '2027-01' });

    const response = await proxy(request('/dashboard', signedIn({ termsVersion: '2026-10' })));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/accept-terms');
  });

  // A session whose cookie carries a version the deployment never served — exactly what the old
  // signup let a caller record — is not treated as current just because it is non-empty.
  it('re-prompts a session carrying a version that was never served', async () => {
    const response = await proxy(request('/dashboard', signedIn({ termsVersion: '2099-12-future' })));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/accept-terms');
  });

  // The gate must not lock anyone out when the API cannot say what it serves: it moves people it
  // KNOWS are behind, and "no answer" is not "behind".
  it('leaves the session alone when the API does not answer', async () => {
    servedLegalVersions.mockResolvedValue(null);

    expect((await proxy(request('/dashboard', signedIn({ termsVersion: '2020-01' })))).status).toBe(200);
  });

  it('does not bounce /accept-terms itself (no redirect loop)', async () => {
    expect((await proxy(request('/accept-terms', signedIn({ termsVersion: '2020-01' })))).status).toBe(200);
    // The gate short-circuits before the read.
    expect(servedLegalVersions).not.toHaveBeenCalled();
  });

  it('leaves the stale-terms session able to reach a public auth path', async () => {
    expect((await proxy(request('/signup', signedIn({ termsVersion: '2020-01' })))).status).toBe(200);
  });
});

describe('proxy — privacy acknowledgement gate', () => {
  it('sends a session whose accepted Privacy version is behind to /accept-terms', async () => {
    servedLegalVersions.mockResolvedValue({ termsVersion: '2026-10', privacyVersion: '2026-11' });
    const response = await proxy(request('/dashboard', signedIn({ termsVersion: '2026-10', privacyVersion: '2026-10' })));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/accept-terms');
  });

  it('leaves a session current on both documents alone', async () => {
    const response = await proxy(request('/dashboard', signedIn({ termsVersion: '2026-10', privacyVersion: '2026-10' })));
    expect(response.status).toBe(200);
  });

  // A cookie issued before the Go API carried privacyVersion: treating "absent" as "behind" would
  // send every existing session to /accept-terms at once.
  it('does not redirect a session without privacyVersion for Privacy', async () => {
    servedLegalVersions.mockResolvedValue({ termsVersion: '2026-10', privacyVersion: '2026-11' });
    const response = await proxy(request('/dashboard', signedIn({ termsVersion: '2026-10' })));
    expect(response.status).toBe(200);
  });

  it('never blocks unsubscribing, even for a session behind on both documents', async () => {
    servedLegalVersions.mockResolvedValue({ termsVersion: '2026-11', privacyVersion: '2026-11' });
    const response = await proxy(
      request('/unsubscribe?token=u1.k1.abc.def', signedIn({ termsVersion: '2026-10', privacyVersion: '2026-10' })),
    );
    expect(response.status).toBe(200);
    expect(response.headers.get('location')).toBeNull();
  });

  it('never blocks /settings/data, where data export and deletion live', async () => {
    servedLegalVersions.mockResolvedValue({ termsVersion: '2026-11', privacyVersion: '2026-11' });
    const response = await proxy(request('/settings/data', signedIn({ termsVersion: '2026-10', privacyVersion: '2026-10' })));
    expect(response.status).toBe(200);
    expect(response.headers.get('location')).toBeNull();
  });

  // S-22: only the export/deletion page is exempt, matched exactly. The rest of the account area,
  // and anything that merely starts with /settings/data, stays behind the gate.
  it.each(['/settings', '/settings/billing', '/settings/usage', '/settings/dataX', '/settings/data/x'])(
    'keeps %s behind the gate for a session behind on the documents',
    async (path) => {
      servedLegalVersions.mockResolvedValue({ termsVersion: '2026-11', privacyVersion: '2026-11' });
      const response = await proxy(request(path, signedIn({ termsVersion: '2026-10', privacyVersion: '2026-10' })));
      expect(response.status).toBe(307);
      expect(location(response)).toContain('/accept-terms');
    },
  );

  it('never blocks sign-out', async () => {
    servedLegalVersions.mockResolvedValue({ termsVersion: '2026-11', privacyVersion: '2026-11' });
    const response = await proxy(request('/api/auth/logout', signedIn({ termsVersion: '2026-10', privacyVersion: '2026-10' })));
    expect(response.status).toBe(200);
  });
});

describe('proxy — must-change-password still wins', () => {
  it('sends a temporary-password session to /change-password', async () => {
    const response = await proxy(request('/dashboard', signedIn({ mustChangePassword: true })));
    expect(response.status).toBe(307);
    expect(location(response)).toContain('/change-password');
  });
});

describe('proxy — guest review links', () => {
  const token = 'A'.repeat(20) + '-_' + 'b'.repeat(21); // 43 characters of the base64url alphabet

  it.each([`/review/g/${token}`, `/review/g/${token}?x=1`])('lets a signed-out visitor reach %s', async (path) => {
    expect(token).toHaveLength(43);
    const response = await proxy(request(path));
    expect(response.status).toBe(200);
    expect(response.headers.get('location')).toBeNull();
  });

  it.each([
    '/review/g/x',
    '/review/g/',
    '/review/g',
    `/review/g/${token}/extra`,
    `/review/g/${token}/`,
    `/review/g/${token}A`,
    `/review/g/${token.slice(1)}`,
    `/review/gX${token}`,
    `/review/gg/${token}`,
    `/review/g/${token.slice(1)}.`,
    `/review/g/${token.slice(1)}%`,
    `/review/${token}`,
    `/xreview/g/${token}`,
  ])('keeps %s behind the sign-in', async (path) => {
    const response = await proxy(request(path));
    expect(response.status).toBeGreaterThanOrEqual(300);
    expect(location(response)).toMatch(/^\/login\/?$/);
  });
});

describe('proxy — community recipe gallery', () => {
  const id = '3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b';

  it.each(['/community', '/community?q=reel', `/community/${id}`, `/community/${id.toUpperCase()}?x=1`])(
    'lets a signed-out visitor reach %s',
    async (path) => {
      const response = await proxy(request(path));
      expect(response.status).toBe(200);
      expect(response.headers.get('location')).toBeNull();
    },
  );

  it.each([
    '/community/',
    '/community/x',
    '/community/new',
    `/community/${id}/`,
    `/community/${id}/extra`,
    `/community/${id}0`,
    `/community/${id.slice(1)}`,
    `/community/${id.replace('3f2b', '3f2g')}`,
    `/community/${id}%2F..`,
    `/communityX`,
    `/community-x/${id}`,
    `/xcommunity/${id}`,
    `/community/${id}/publish`,
    '/community/mine',
  ])('keeps %s behind the sign-in', async (path) => {
    const response = await proxy(request(path));
    expect(response.status).toBeGreaterThanOrEqual(300);
    expect(location(response)).toMatch(/^\/login\/?$/);
  });
});

describe('sign-in redirect carries a safe next', () => {
  const target = async (path: string) => {
    const response = await proxy(request(path));
    return new URL(response.headers.get('location') ?? '', 'http://localhost');
  };

  it('returns the user to the page they asked for, with its query', async () => {
    const url = await target('/projects/1?tab=files');
    expect(url.pathname).toMatch(/^\/login\/?$/);
    expect(url.searchParams.get('next')).toBe('/app/projects/1?tab=files');
    expect(url.searchParams.get('reason')).toBeNull();
  });

  it.each(['/dashboard', '/'])('adds no next for the default %s', async (path) => {
    expect((await target(path)).searchParams.has('next')).toBe(false);
  });

  it.each([
    '//evil.com/x',
    '/%5Cevil.com',
    '/%2F%2Fevil.com',
    '/x?u=https://evil.com',
    '/x%0A//evil.com',
  ])('never lets %s become an off-site or malformed next', async (path) => {
    const url = await target(path);
    const next = url.searchParams.get('next');
    expect(url.origin).toBe('http://localhost');
    if (next !== null) {
      expect(next.startsWith('/app/')).toBe(true);
      expect(next.startsWith('//')).toBe(false);
      expect(next).not.toMatch(/[\\\s]|:\/\//);
    }
  });
});

describe('proxy — names that survive Next decoding the cookie once', () => {
  const user = { email: 'a@b.co', mustChangePassword: false };
  it.each(['100%', '%41', 'Ada+L', 'say "hi"', 'Zoë 山田 🎬'])('lets a session with displayName %s through', async (name) => {
    const response = await proxy(request('/dashboard', signedIn({ displayName: name, orgName: name })));
    expect(location(response)).not.toBe('/login');
  });

  it('lets through the actual Go-escaped cookie string (space as +)', async () => {
    const raw = encodeURIComponent(JSON.stringify({ ...user, displayName: '100% Ada L.' })).replace(/%20/g, '+');
    const response = await proxy(request('/dashboard', { reelbolt_token: mint(), reelbolt_user: raw }));
    expect(location(response)).not.toBe('/login');
  });

  it('treats a malformed cookie as signed out instead of throwing', async () => {
    const response = await proxy(request('/dashboard', { reelbolt_token: mint(), reelbolt_user: '%E0%A4%A' }));
    expect(location(response)).toBe('/login');
  });
});
