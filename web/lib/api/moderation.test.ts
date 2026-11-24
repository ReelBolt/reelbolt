import { beforeEach, describe, expect, it, vi } from 'vitest';

// `apiFetch` is stubbed so the assertions are on the URL and method the client builds, not on
// fetch semantics (client.test.ts already covers those). There is no MSW in this repo.
vi.mock('./client', async () => {
  const actual = await vi.importActual<typeof import('./client')>('./client');
  return { ...actual, apiFetch: vi.fn() };
});

import { apiFetch } from './client';
import {
  MODERATION_REVIEW_QUEUE_PATH,
  MODERATION_SETTINGS_PATH,
  acknowledgeModerationItem,
  fetchModerationReviewQueue,
  fetchModerationSettings,
  getModerationAcknowledgeUrl,
  getModerationReviewQueueUrl,
} from './moderation';
import type {
  ModerationAcknowledgeResponse,
  ModerationReviewQueue,
  ModerationSettings,
} from '../types/moderation';

const apiFetchMock = vi.mocked(apiFetch);

const QUEUE: ModerationReviewQueue = { items: [], total: 0, limit: 100 };

const ACKNOWLEDGED: ModerationAcknowledgeResponse = { id: 'item-1', reviewState: 'acknowledged' };

const SETTINGS: ModerationSettings = {
  enabled: true,
  model: 'omni-moderation-latest',
  blockThreshold: 0.9,
  reviewThreshold: 0.7,
  hardBlockCategories: ['sexual/minors'],
  timeoutSeconds: 10,
  auditRetentionDays: 30,
  failClosedAtPublish: true,
};

beforeEach(() => {
  apiFetchMock.mockReset();
  apiFetchMock.mockResolvedValue(QUEUE as never);
});

describe('getModerationReviewQueueUrl', () => {
  it('appends state then limit, in that order', () => {
    expect(getModerationReviewQueueUrl({ state: 'pending', limit: 100 })).toBe(
      `${MODERATION_REVIEW_QUEUE_PATH}?state=pending&limit=100`,
    );
  });

  it('sends no querystring when nothing is supplied', () => {
    expect(getModerationReviewQueueUrl()).toBe(MODERATION_REVIEW_QUEUE_PATH);
    expect(getModerationReviewQueueUrl({})).toBe(MODERATION_REVIEW_QUEUE_PATH);
  });

  it('drops an empty or whitespace-only state rather than sending a meaningless filter', () => {
    expect(getModerationReviewQueueUrl({ state: '   ', limit: 25 })).toBe(
      `${MODERATION_REVIEW_QUEUE_PATH}?limit=25`,
    );
  });

  it('keeps limit=0 rather than dropping it as falsy', () => {
    expect(getModerationReviewQueueUrl({ state: 'pending', limit: 0 })).toBe(
      `${MODERATION_REVIEW_QUEUE_PATH}?state=pending&limit=0`,
    );
  });

  it('encodeURIComponent-escapes the state', () => {
    expect(getModerationReviewQueueUrl({ state: 'pending review&x=1' })).toBe(
      `${MODERATION_REVIEW_QUEUE_PATH}?state=${encodeURIComponent('pending review&x=1')}`,
    );
  });
});

describe('fetchModerationReviewQueue', () => {
  it('fetches the pending page the review page asks for', async () => {
    const result = await fetchModerationReviewQueue({ state: 'pending', limit: 100 });

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(apiFetchMock.mock.calls[0][0]).toBe(
      `${MODERATION_REVIEW_QUEUE_PATH}?state=pending&limit=100`,
    );
    expect(apiFetchMock.mock.calls[0][1]).toBeUndefined();
    expect(result).toBe(QUEUE);
  });
});

describe('acknowledgeModerationItem', () => {
  it('POSTs to the item\'s acknowledge path and returns the server response', async () => {
    apiFetchMock.mockResolvedValue(ACKNOWLEDGED as never);

    const result = await acknowledgeModerationItem('item-1');

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(apiFetchMock.mock.calls[0][0]).toBe(
      `${MODERATION_REVIEW_QUEUE_PATH}/item-1/acknowledge`,
    );
    expect(apiFetchMock.mock.calls[0][1]).toEqual({ method: 'POST' });
    expect(result).toBe(ACKNOWLEDGED);
  });

  it('escapes the id in the acknowledge path', () => {
    expect(getModerationAcknowledgeUrl('a b/c')).toBe(
      `${MODERATION_REVIEW_QUEUE_PATH}/${encodeURIComponent('a b/c')}/acknowledge`,
    );
  });

  it('never sends a block, dismiss or delete method', async () => {
    await acknowledgeModerationItem('item-1');

    const options = apiFetchMock.mock.calls[0][1] as RequestInit;
    expect(options.method).toBe('POST');
    expect(apiFetchMock.mock.calls[0][0]).not.toMatch(/\/(block|dismiss|delete)(\?|$)/);
  });
});

describe('fetchModerationSettings', () => {
  it('reads the settings path without options', async () => {
    apiFetchMock.mockResolvedValue(SETTINGS as never);

    const result = await fetchModerationSettings();

    expect(apiFetchMock.mock.calls[0][0]).toBe(MODERATION_SETTINGS_PATH);
    expect(apiFetchMock.mock.calls[0][1]).toBeUndefined();
    expect(result).toBe(SETTINGS);
  });
});
