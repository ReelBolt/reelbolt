import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import {
  MARKETING_ADMIN_LINKS_URL,
  createMarketingLink,
  getMarketingLinks,
  getMarketingLinksFunnel,
  marketingLinkErrorCode,
  marketingLinkUrl,
  marketingLinksFunnelUrl,
  patchMarketingLink,
} from './marketing-links';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  fetchMock.mockReset();
});

describe('marketing links API', () => {
  it('builds the admin routes of the contract', () => {
    expect(MARKETING_ADMIN_LINKS_URL).toBe('/api/v1/marketing/admin/links');
    expect(marketingLinkUrl('2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40')).toBe(
      '/api/v1/marketing/admin/links/2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40',
    );
    expect(marketingLinkUrl('a/b')).toBe('/api/v1/marketing/admin/links/a%2Fb');
    expect(marketingLinksFunnelUrl(90)).toBe('/api/v1/marketing/admin/links/funnel?days=90');
  });

  it('reads the list from the items envelope, and an odd body as no links', async () => {
    fetchMock.mockResolvedValueOnce(json({ items: [{ id: 'l1', code: 'hn-launch' }] }));
    expect((await getMarketingLinks()).items).toEqual([{ id: 'l1', code: 'hn-launch' }]);
    fetchMock.mockResolvedValueOnce(json(null));
    expect(await getMarketingLinks()).toEqual({ items: [] });
  });

  it('reads the funnel as sent', async () => {
    const body = {
      days: 30,
      generatedAt: '2026-11-11T08:00:00Z',
      urlCaptureEnabled: true,
      items: [
        {
          linkId: 'l1', code: 'hn-launch', channel: 'hackernews', medium: 'community', campaign: 'launch-2026-11',
          placement: 'Show HN', destinationPath: '/', archived: false, costMinor: null, costCurrency: null,
          clicks: 9, botHits: 1, visitors: 4, signups: 3, verified: 2, onboarded: 2, firstProject: 1, firstRun: 1, firstVideo: 0, paid: 0,
        },
      ],
      unattributed: { signups: 5, verified: 4, onboarded: 3, firstProject: 2, firstRun: 1, firstVideo: 1, paid: 0 },
    };
    fetchMock.mockResolvedValueOnce(json(body));
    expect(await getMarketingLinksFunnel(marketingLinksFunnelUrl(30))).toEqual(body);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/marketing/admin/links/funnel?days=30');
  });

  it('never yields NaN or a guessed setting from a partial funnel body', async () => {
    fetchMock.mockResolvedValueOnce(json({ items: [{ linkId: 'l1', code: 'x', clicks: 2 }] }));
    const funnel = await getMarketingLinksFunnel(marketingLinksFunnelUrl(7));
    expect(funnel.urlCaptureEnabled).toBe(false);
    expect(funnel.unattributed).toEqual({ signups: 0, verified: 0, onboarded: 0, firstProject: 0, firstRun: 0, firstVideo: 0, paid: 0 });
    expect(funnel.items[0]).toMatchObject({ linkId: 'l1', clicks: 2, botHits: 0, visitors: 0, signups: 0, paid: 0 });
  });

  it('posts a new link and patches an existing one as JSON', async () => {
    fetchMock.mockImplementation(() => Promise.resolve(json({ id: 'l1' })));
    await createMarketingLink({
      code: 'hn-launch', channel: 'hackernews', medium: 'community', campaign: 'launch', placement: 'Show HN', destinationPath: '/',
    });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/marketing/admin/links');
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ method: 'POST' });
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({
      code: 'hn-launch', channel: 'hackernews', medium: 'community', campaign: 'launch', placement: 'Show HN', destinationPath: '/',
    });

    await patchMarketingLink('l1', { persona: null, archived: true });
    expect(fetchMock.mock.calls[1][0]).toBe('/api/v1/marketing/admin/links/l1');
    expect(fetchMock.mock.calls[1][1]).toMatchObject({ method: 'PATCH' });
    // An explicit null survives: it is how the API tells "clear" from "leave alone".
    expect(fetchMock.mock.calls[1][1].body).toBe('{"persona":null,"archived":true}');
  });

  it('reads the refusal code of the API, and none from anything else', async () => {
    fetchMock.mockResolvedValueOnce(json({ error: 'link_code_taken', field: 'code' }, 409));
    const refusal = await createMarketingLink({
      code: 'hn-launch', channel: 'hackernews', medium: 'community', campaign: 'launch', placement: 'Show HN', destinationPath: '/',
    }).catch((error: unknown) => error);
    expect(refusal).toBeInstanceOf(ApiError);
    expect((refusal as ApiError).status).toBe(409);
    expect(marketingLinkErrorCode(refusal)).toBe('link_code_taken');
    expect(marketingLinkErrorCode(new ApiError('x', 400, [], { error: 'destination_path_invalid' }))).toBe('destination_path_invalid');
    expect(marketingLinkErrorCode(new ApiError('x', 400, [], { error: 'something_else' }))).toBeNull();
    expect(marketingLinkErrorCode(new ApiError('x', 500))).toBeNull();
    expect(marketingLinkErrorCode(new Error('offline'))).toBeNull();
  });
});
