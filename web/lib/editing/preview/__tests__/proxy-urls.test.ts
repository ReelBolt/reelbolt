/** @vitest-environment node */
/**
 * PA-1 / PA-6 / ui1 D5: the proxy lookup that feeds the viewer. The old effect wrote a `null`
 * placeholder, dropped the in-flight answer whenever it re-ran (the pool SWR resolving, an edit,
 * StrictMode) and then skipped the ref because it was "already in the map": the viewer stayed black
 * until a reload, and a proxy built while the editor was open was never picked up.
 */

import { describe, expect, it, vi } from 'vitest';
import { mediaRefsIn, ProxyUrlResolver } from '../proxy-urls';
import { doc, media, track } from '../../__tests__/fixtures';
import type { MediaDerivativeRow } from '../../media/types';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const proxyRow = (id: string): MediaDerivativeRow => ({ id, kind: 'proxy', mimeType: 'video/mp4', sizeBytes: 1, durationSec: 1, width: 640, height: 360, contentUrl: '' });

function resolverWith(fetchDerivatives: (ref: string) => Promise<MediaDerivativeRow[]>) {
  return new ProxyUrlResolver({
    fetchDerivatives,
    proxyUrl: (row) => `/proxy/${row.id}`,
    originalUrl: (ref) => (ref.startsWith('file:') ? `/original/${ref.slice(5)}` : null),
  });
}

describe('ProxyUrlResolver', () => {
  it('never exposes a placeholder: an unanswered lookup is "pending", not a null URL that sticks', () => {
    const resolver = resolverWith(() => new Promise(() => undefined));
    resolver.request(['file:a']);
    expect(resolver.status('file:a')).toEqual({ state: 'pending' });
    expect(resolver.urlFor('file:a')).toBeNull();
  });

  it('keeps an in-flight answer across re-runs and asks the API once', async () => {
    const answer = deferred<MediaDerivativeRow[]>();
    const fetchDerivatives = vi.fn(() => answer.promise);
    const resolver = resolverWith(fetchDerivatives);
    const changed = vi.fn();
    resolver.subscribe(changed);
    // Mount, the pool SWR resolving, an edit, StrictMode's double run: the request is repeated.
    resolver.request(['file:a']);
    resolver.request(['file:a']);
    resolver.request(['file:a'], { refresh: true });
    expect(fetchDerivatives).toHaveBeenCalledTimes(1);
    answer.resolve([proxyRow('p1')]);
    await answer.promise;
    await Promise.resolve();
    expect(resolver.urlFor('file:a')).toBe('/proxy/p1');
    expect(resolver.status('file:a')).toEqual({ state: 'proxy', url: '/proxy/p1' });
    expect(changed).toHaveBeenCalled();
  });

  it('plays the original while no proxy exists, and picks the proxy up on a refresh', async () => {
    let rows: MediaDerivativeRow[] = [];
    const resolver = resolverWith(async () => rows);
    resolver.request(['file:a']);
    await vi.waitFor(() => expect(resolver.status('file:a').state).toBe('original'));
    expect(resolver.urlFor('file:a')).toBe('/original/a');

    // Without a refresh a known answer is not re-asked.
    rows = [proxyRow('p2')];
    resolver.request(['file:a']);
    await Promise.resolve();
    expect(resolver.urlFor('file:a')).toBe('/original/a');

    // The media pool changed (a proxy was built): sources without a proxy are asked again.
    resolver.request(['file:a'], { refresh: true });
    await vi.waitFor(() => expect(resolver.urlFor('file:a')).toBe('/proxy/p2'));
  });

  it('falls back to the original when the derivative list cannot be read', async () => {
    const resolver = resolverWith(async () => {
      throw new Error('offline');
    });
    resolver.request(['file:a']);
    await vi.waitFor(() => expect(resolver.urlFor('file:a')).toBe('/original/a'));
  });

  it('says why a source with neither a proxy nor an original route cannot play', async () => {
    const resolver = resolverWith(async () => []);
    resolver.request(['odd:x']);
    await vi.waitFor(() => expect(resolver.status('odd:x').state).toBe('unavailable'));
    const status = resolver.status('odd:x');
    expect(status.state === 'unavailable' && status.reason).toMatch(/no proxy/i);
  });

  it('keeps answers that land after dispose-free unsubscribes (StrictMode remount)', async () => {
    const answer = deferred<MediaDerivativeRow[]>();
    const resolver = resolverWith(() => answer.promise);
    const off = resolver.subscribe(() => undefined);
    resolver.request(['file:a']);
    off();
    const listener = vi.fn();
    resolver.subscribe(listener);
    answer.resolve([proxyRow('p3')]);
    await vi.waitFor(() => expect(listener).toHaveBeenCalled());
    expect(resolver.urlFor('file:a')).toBe('/proxy/p3');
  });

  it('lists the media refs of every clip in a document once', () => {
    const d = doc([
      track('v', 'video', [media('c1', 0, 10), media('c2', 10, 20, 0, { assetRef: { kind: 'stepResult', stepResultId: 's1' } })]),
      track('a', 'audio', [media('a1', 0, 10)]),
    ]);
    expect([...mediaRefsIn(d)].sort()).toEqual(['file:00000000-0000-4000-8000-000000000001', 'step_result:s1']);
  });
});
