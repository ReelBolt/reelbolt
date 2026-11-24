// @vitest-environment node
//
// Covers T-014 (alpha-launch): the video editor is hidden while the picture editor keeps working.
// The redirects live in the middleware (`web/proxy.ts`), so this file drives `proxy()` directly as
// `proxy.test.ts` does, and renders the coming-soon page with the server renderer (it is a Server
// Component, so there is no DOM to hydrate).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NextRequest } from 'next/server';
import { createHmac, randomBytes } from 'crypto';
import { renderToStaticMarkup } from 'react-dom/server';
import { MantineProvider } from '@mantine/core';

// The proxy asks the API that owns legal_documents for the served versions; mocked so these tests
// are about the video-editor gate, not that read.
const { servedLegalVersions } = vi.hoisted(() => ({ servedLegalVersions: vi.fn() }));
vi.mock('@/lib/auth/legal-server', () => ({ servedLegalVersions }));

// `redirect` throws in Next; stubbed so the flag-on case can be asserted without unwinding a render.
const { redirect } = vi.hoisted(() => ({ redirect: vi.fn() }));
vi.mock('next/navigation', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  redirect,
}));

import { proxy } from '@/proxy';
import { videoEditorEnabled } from '@/lib/editor/config';
import EditorComingSoonPage from './page';

// proxy.ts verifies the signed session token, so the request carries a real one.
const KEY = randomBytes(32).toString('hex'); // generated per run: no key literal in source
const b64 = (v: unknown) => Buffer.from(JSON.stringify(v)).toString('base64url');
function sessionToken(): string {
  const head = b64({ alg: 'HS256', typ: 'JWT' });
  const body = b64({ sub: '3f2b8c1e-5d4a-4e6f-9a7b-1c2d3e4f5a6b', iss: 'reelbolt-api', aud: ['reelbolt-inference'], exp: Math.floor(Date.now() / 1000) + 3600 });
  return `${head}.${body}.${createHmac('sha256', KEY).update(`${head}.${body}`).digest('base64url')}`;
}

function request(path: string, cookies: Record<string, string> = {}) {
  const cookieHeader = Object.entries(cookies)
    .map(([name, value]) => `${name}=${value}`)
    .join('; ');
  return new NextRequest(new URL(`http://localhost${path}`), {
    headers: cookieHeader ? { cookie: cookieHeader } : {},
  });
}

function signedIn() {
  return {
    reelbolt_token: sessionToken(),
    reelbolt_user: encodeURIComponent(JSON.stringify({ email: 'a@b.co', mustChangePassword: false })),
  };
}

const location = (response: Response) =>
  new URL(response.headers.get('location') ?? '', 'http://localhost').pathname;

const SERVED = { termsVersion: '2026-10', privacyVersion: '2026-10' };

/** Every video-editor path the flag hides (bare: the '/app' basePath is stripped before matching). */
const VIDEO_EDITOR_PATHS = [
  '/editor',
  '/editor/preview',
  '/editor/p1/timelines',
  '/editor/p1/media',
  '/editor/p1/timeline/t1',
  '/editor/p1/review/t1',
  '/editor/p1/sr1',
  '/projects/p1/editor/sr1',
];

beforeEach(() => {
  vi.stubEnv('JWT_SIGNING_KEY', KEY);
  vi.stubEnv('JWT_ISSUER', 'reelbolt-api');
  vi.stubEnv('JWT_AUDIENCE', 'reelbolt-inference');
  servedLegalVersions.mockReset();
  servedLegalVersions.mockResolvedValue(SERVED);
  redirect.mockReset();
  delete process.env.VIDEO_EDITOR_ENABLED;
});

afterEach(() => {
  delete process.env.VIDEO_EDITOR_ENABLED;
});

describe('videoEditorEnabled', () => {
  it('defaults to true when the flag is unset (dev, CI and today\'s behaviour)', () => {
    expect(videoEditorEnabled({})).toBe(true);
  });

  it('is false only for the literal "false"', () => {
    expect(videoEditorEnabled({ VIDEO_EDITOR_ENABLED: 'false' })).toBe(false);
    expect(videoEditorEnabled({ VIDEO_EDITOR_ENABLED: 'true' })).toBe(true);
    expect(videoEditorEnabled({ VIDEO_EDITOR_ENABLED: '0' })).toBe(true);
    expect(videoEditorEnabled({ VIDEO_EDITOR_ENABLED: '' })).toBe(true);
  });
});

describe('proxy — the video editor is off', () => {
  beforeEach(() => {
    process.env.VIDEO_EDITOR_ENABLED = 'false';
  });

  it.each(VIDEO_EDITOR_PATHS)('redirects %s to the coming-soon page', async (path) => {
    const response = await proxy(request(path, signedIn()));
    expect(response.status).toBe(307);
    expect(location(response)).toBe('/editor-coming-soon');
  });

  it('never gates the picture editor at /editor/thumbnails/{documentId}', async () => {
    const response = await proxy(request('/editor/thumbnails/doc1', signedIn()));
    expect(response.status).toBe(200);
    expect(response.headers.get('location')).toBeNull();
  });

  it('does not gate the coming-soon page itself (no redirect loop)', async () => {
    const response = await proxy(request('/editor-coming-soon', signedIn()));
    expect(response.status).toBe(200);
    expect(response.headers.get('location')).toBeNull();
  });

  it('still sends a signed-out visitor to login, not to the coming-soon page', async () => {
    const response = await proxy(request('/editor'));
    expect(location(response)).toContain('/login');
  });
});

describe('proxy — the video editor is on', () => {
  it.each(VIDEO_EDITOR_PATHS)('leaves %s exactly as today', async (path) => {
    process.env.VIDEO_EDITOR_ENABLED = 'true';
    const response = await proxy(request(path, signedIn()));
    expect(response.status).toBe(200);
    expect(response.headers.get('location')).toBeNull();
  });

  it('renders the picture editor in both flag states', async () => {
    process.env.VIDEO_EDITOR_ENABLED = 'true';
    const on = await proxy(request('/editor/thumbnails/doc1', signedIn()));
    process.env.VIDEO_EDITOR_ENABLED = 'false';
    const off = await proxy(request('/editor/thumbnails/doc1', signedIn()));
    expect(on.status).toBe(200);
    expect(off.status).toBe(200);
  });
});

describe('EditorComingSoonPage', () => {
  const render = () =>
    renderToStaticMarkup(
      <MantineProvider>
        <EditorComingSoonPage />
      </MantineProvider>,
    );

  it('renders the pinned copy while the flag is off', () => {
    process.env.VIDEO_EDITOR_ENABLED = 'false';
    const html = render();
    expect(html).toContain('The video editor is coming soon. This 0.1 alpha ships without it.');
    expect(redirect).not.toHaveBeenCalled();
  });

  it('is not reachable while the flag is on: it redirects to the hub', () => {
    process.env.VIDEO_EDITOR_ENABLED = 'true';
    render();
    expect(redirect).toHaveBeenCalledWith('/editor');
  });
});
