import { describe, expect, it, vi } from 'vitest';
import { KEEPALIVE_BODY_LIMIT, patchEditTimeline, stringifyDiscriminatorFirst, type PatchEditTimelineRequest } from '../api';

describe('stringifyDiscriminatorFirst', () => {
  it('writes every type discriminator first, at any depth, and changes nothing else', () => {
    const request = {
      baseRevision: 3,
      document: { tracks: [{ id: 't1', items: [{ id: 'c1', type: 'mediaClip', assetRef: { kind: 'projectFile', id: 'f1' }, recordIn: 0 }] }] },
      ops: [{ itemId: 'c1', type: 'lift' }],
    };
    const json = stringifyDiscriminatorFirst(request);
    expect(json).toContain('{"type":"mediaClip","id":"c1"');
    expect(json).toContain('{"type":"lift","itemId":"c1"}');
    expect(JSON.parse(json)).toEqual(request);
  });
});

describe('patchEditTimeline keepalive', () => {
  const doc = (padding: number) => ({ tracks: [], name: 'x'.repeat(padding) }) as unknown as PatchEditTimelineRequest['document'];
  const sendAndCapture = async (padding: number, keepalive: boolean): Promise<RequestInit> => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ revision: 2 }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    try {
      await patchEditTimeline('p', 't', { baseRevision: 1, ops: [], document: doc(padding) }, { keepalive });
      return (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    } finally {
      vi.unstubAllGlobals();
    }
  };

  it('sends a small body with keepalive when asked', async () => {
    expect((await sendAndCapture(10, true)).keepalive).toBe(true);
  });

  it('falls back to a normal fetch when the body is over the 64 KiB keepalive limit', async () => {
    expect((await sendAndCapture(KEEPALIVE_BODY_LIMIT, true)).keepalive).toBeUndefined();
  });

  it('never sets keepalive unless asked', async () => {
    expect((await sendAndCapture(10, false)).keepalive).toBeUndefined();
  });

  // The 64 KiB quota is shared by every keepalive request still in flight: a second one that would
  // overflow it is refused by the browser and lost on unload, so it goes as a normal fetch instead.
  it('shares the 64 KiB budget across keepalive requests still in flight', async () => {
    const pending: Array<(r: Response) => void> = [];
    const fetchMock = vi.fn(
      () => new Promise<Response>((resolve) => pending.push(resolve)),
    );
    vi.stubGlobal('fetch', fetchMock);
    const options = (i: number) => (fetchMock.mock.calls[i] as unknown as [string, RequestInit])[1];
    const answer = () => new Response(JSON.stringify({ revision: 2 }), { status: 200 });
    try {
      const big = 40 * 1024;
      const first = patchEditTimeline('p', 't', { baseRevision: 1, ops: [], document: doc(big) }, { keepalive: true });
      const second = patchEditTimeline('p', 't', { baseRevision: 1, ops: [], document: doc(big) }, { keepalive: true });
      await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
      expect(options(0).keepalive).toBe(true);
      expect(options(1).keepalive).toBeUndefined(); // 80 KiB would overflow the shared quota

      pending[0](answer());
      pending[1](answer());
      await Promise.all([first, second]);
      // The quota is released once the keepalive request settled.
      const third = patchEditTimeline('p', 't', { baseRevision: 1, ops: [], document: doc(big) }, { keepalive: true });
      await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
      expect(options(2).keepalive).toBe(true);
      pending[2](answer());
      await third;
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('releases the shared budget when a keepalive request fails', async () => {
    const fetchMock = vi.fn(async () => {
      throw new TypeError('network');
    });
    vi.stubGlobal('fetch', fetchMock);
    try {
      const big = 40 * 1024;
      await expect(patchEditTimeline('p', 't', { baseRevision: 1, ops: [], document: doc(big) }, { keepalive: true })).rejects.toThrow();
      await expect(patchEditTimeline('p', 't', { baseRevision: 1, ops: [], document: doc(big) }, { keepalive: true })).rejects.toThrow();
      expect((fetchMock.mock.calls[1] as unknown as [string, RequestInit])[1].keepalive).toBe(true);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
