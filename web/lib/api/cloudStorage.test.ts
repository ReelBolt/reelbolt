import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import {
  cloudErrorCode,
  cloudRetryAfterSeconds,
  createProjectCloudLink,
  deleteProjectCloudLink,
  describeCloudConnectOutcome,
  describeCloudError,
  describeDriveKind,
  disconnectCloudConnection,
  listCloudConnections,
  listCloudDrives,
  listCloudFolder,
  listCloudProviders,
  listProjectCloudLinks,
  needsReconnect,
  projectCloudReturnPath,
  providerName,
  startCloudConnect,
  syncProjectCloudLink,
  updateProjectCloudLink,
} from './cloudStorage';

function mockFetchOnce(status: number, body: unknown, statusText = 'OK') {
  const json = typeof body === 'string'
    ? () => Promise.reject(new SyntaxError('not json'))
    : () => Promise.resolve(body);
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: status >= 200 && status < 300, status, statusText, json }));
}

const call = (n = 0) => {
  const [url, init] = vi.mocked(fetch).mock.calls[n];
  return { url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined };
};

async function errorOf(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise;
  } catch (err) {
    return err as ApiError;
  }
  throw new Error('expected rejection');
}

const refusal = (status: number, body: Record<string, unknown>) => new ApiError(String(body.error), status, [], body);

/** The codes the controllers write. None of them may ever reach the screen as-is. */
const CODES: [number, string][] = [
  [404, 'provider_not_found'], [404, 'no_workspace'], [404, 'link_not_found'], [404, 'connection_not_found'],
  [404, 'folder_not_found'], [400, 'invalid_id'], [400, 'invalid_include_paths'], [409, 'already_linked'],
  [409, 'reconnect_required'], [424, 'provider_error'], [429, 'sync_too_soon'], [503, 'provider_not_configured'],
  [503, 'public_base_url_missing'],
];

afterEach(() => vi.unstubAllGlobals());

describe('the cloud storage client', () => {
  it('reads providers, connections and a project’s links from their endpoints', async () => {
    mockFetchOnce(200, [{ provider: 'dropbox', displayName: 'Dropbox', configured: true }]);
    await expect(listCloudProviders()).resolves.toHaveLength(1);
    expect(call().url).toBe('/api/v1/cloud-storage/providers');

    mockFetchOnce(200, []);
    await listCloudConnections();
    expect(call().url).toBe('/api/v1/cloud-storage/connections');

    mockFetchOnce(200, []);
    await listProjectCloudLinks('p1');
    expect(call().url).toBe('/api/v1/projects/p1/cloud-links');
  });

  it('starts connecting with the return path in a POST body and returns the sign-in URL', async () => {
    mockFetchOnce(200, { authorizeUrl: 'https://accounts.google.com/o/oauth2/v2/auth?x=1' });
    await expect(startCloudConnect('google-drive', projectCloudReturnPath('p1'))).resolves.toBe('https://accounts.google.com/o/oauth2/v2/auth?x=1');
    expect(call()).toEqual({ url: '/api/v1/cloud-storage/google-drive/oauth/start', method: 'POST', body: { returnTo: '/app/projects/p1?tab=cloud' } });
  });

  it('lists drives and one folder level, leaving out the ids that mean "own space" and "top"', async () => {
    mockFetchOnce(200, [{ driveId: null, name: 'My Drive', kind: 'personal' }]);
    await listCloudDrives('c1');
    expect(call().url).toBe('/api/v1/cloud-storage/connections/c1/drives');

    mockFetchOnce(200, { folder: { folderId: null, name: '', displayPath: '/' }, items: [], truncated: false });
    await listCloudFolder('c1', null, null);
    expect(call().url).toBe('/api/v1/cloud-storage/connections/c1/folders');

    mockFetchOnce(200, { folder: { folderId: 'f/1', name: 'x', displayPath: '/x' }, items: [], truncated: false });
    await listCloudFolder('c1', 'drive 1', 'f/1');
    expect(call().url).toBe('/api/v1/cloud-storage/connections/c1/folders?driveId=drive+1&folderId=f%2F1');
  });

  it('links, changes, syncs and unlinks with the documented methods and bodies', async () => {
    mockFetchOnce(201, { id: 'l1' }, 'Created');
    await createProjectCloudLink('p1', { connectionId: 'c1', driveId: null, folderId: 'f1', includePaths: [] });
    expect(call()).toEqual({ url: '/api/v1/projects/p1/cloud-links', method: 'POST', body: { connectionId: 'c1', driveId: null, folderId: 'f1', includePaths: [] } });

    mockFetchOnce(200, { id: 'l1' });
    await updateProjectCloudLink('p1', 'l1', { includePaths: ['brand'] });
    expect(call()).toEqual({ url: '/api/v1/projects/p1/cloud-links/l1', method: 'PATCH', body: { includePaths: ['brand'] } });

    mockFetchOnce(202, { status: 'already_syncing' }, 'Accepted');
    await expect(syncProjectCloudLink('p1', 'l1')).resolves.toEqual({ status: 'already_syncing' });
    expect(call()).toMatchObject({ url: '/api/v1/projects/p1/cloud-links/l1/sync', method: 'POST' });

    mockFetchOnce(204, '', 'No Content');
    await expect(deleteProjectCloudLink('p1', 'l1')).resolves.toBeUndefined();
    expect(call()).toMatchObject({ url: '/api/v1/projects/p1/cloud-links/l1', method: 'DELETE' });

    mockFetchOnce(204, '', 'No Content');
    await disconnectCloudConnection('c1');
    expect(call()).toMatchObject({ url: '/api/v1/cloud-storage/connections/c1', method: 'DELETE' });
  });

  it('keeps the error body, so the code, the sentence and the wait are all readable', async () => {
    mockFetchOnce(429, { error: 'sync_too_soon', message: 'A sync was started a moment ago.', retryAfterSeconds: 12 }, 'Too Many Requests');
    const err = await errorOf(syncProjectCloudLink('p1', 'l1'));
    expect(cloudErrorCode(err)).toBe('sync_too_soon');
    expect(cloudRetryAfterSeconds(err)).toBe(12);
    expect(describeCloudError(err)).toBe('A sync was started a moment ago. Try again in 12 seconds.');
  });
});

describe('describeCloudError', () => {
  it('prefers the server’s own plain sentence', () => {
    const err = refusal(409, { error: 'reconnect_required', message: 'Dropbox no longer accepts ReelBolt’s access to this account. Reconnect it in Settings.' });
    expect(describeCloudError(err)).toBe('Dropbox no longer accepts ReelBolt’s access to this account. Reconnect it in Settings.');
    expect(needsReconnect(err)).toBe(true);
  });

  it.each(CODES)('gives %i %s a sentence and never prints the code', (status, code) => {
    const sentence = describeCloudError(refusal(status, { error: code }));
    expect(sentence).not.toContain(code);
    expect(sentence).not.toMatch(/_/);
    expect(sentence.length).toBeGreaterThan(10);
  });

  it('says how long to wait for a 429, with or without a number', () => {
    expect(describeCloudError(refusal(429, { error: 'sync_too_soon', retryAfterSeconds: 1 }))).toMatch(/Try again in 1 second\./);
    expect(describeCloudError(refusal(429, { error: 'sync_too_soon' }))).toMatch(/Try again in a few seconds/);
  });

  it('ignores a "message" that is only a code', () => {
    expect(describeCloudError(refusal(424, { error: 'provider_error', message: 'provider_error' }))).toMatch(/did not answer as expected/);
  });
});

describe('the words around it', () => {
  it('describes every way back from connecting an account in plain words', () => {
    expect(describeCloudConnectOutcome('connected', 'Dropbox', null)).toEqual({ color: 'green', message: 'Dropbox account connected. Now choose a folder.' });
    for (const reason of ['denied', 'invalid_state', 'expired', 'session_mismatch', 'exchange_failed', 'account_unavailable', 'not_configured', 'something_new']) {
      const notice = describeCloudConnectOutcome('error', 'OneDrive', reason);
      expect(notice).not.toBeNull();
      if (reason.includes('_')) expect(notice!.message).not.toContain(reason);
      expect(notice!.message).not.toMatch(/_/);
    }
    expect(describeCloudConnectOutcome('error', 'OneDrive', 'denied')!.color).toBe('yellow');
    expect(describeCloudConnectOutcome(null, 'OneDrive', null)).toBeNull();
  });

  it('names providers and drive kinds the way people do', () => {
    expect(providerName('google-drive')).toBe('Google Drive');
    expect(providerName('onedrive', [{ provider: 'onedrive', displayName: 'OneDrive & SharePoint', configured: true }])).toBe('OneDrive & SharePoint');
    expect(describeDriveKind('sharePointLibrary')).toBe('SharePoint library');
    expect(describeDriveKind('sharedDrive')).toBe('Shared drive');
    expect(describeDriveKind('teamSpace')).toBe('Team space');
    expect(describeDriveKind('personal')).toBe('Your files');
  });
});
