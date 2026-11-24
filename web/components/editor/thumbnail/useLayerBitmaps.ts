'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Bitmap, BitmapMap, LayerBitmaps } from '@/lib/editor/compose';
import { loadBitmap } from '@/lib/editor/canvas-dom';
import type { EditorLayer } from '@/lib/editor/model';
import { outputCheckDelay } from '@/lib/outputs/output-check';

/** How many times a layer image that is still being checked is waited for before it counts as unavailable. */
const MAX_PENDING_WAITS = 12;
/** The server's answer for an image that has no verdict yet. */
const IMAGE_PENDING = 'CONTENT_MODERATION_PENDING';

/** How long a URL that failed to load is left alone before a change to the layers tries it again. */
export const BITMAP_RETRY_AFTER_MS = 10_000;

/**
 * The waits before each further attempt at an image that failed for any reason other than being gone
 * (a network blip, a 5xx): one entry per retry, so a URL is tried `length + 1` times before the layer
 * is reported as unavailable.
 */
export const BITMAP_LOAD_BACKOFF_MS: readonly number[] = [500, 1_500];

/** The server's 404 code for a layer whose stored image no longer exists. */
const LAYER_IMAGE_MISSING = 'LAYER_IMAGE_MISSING';

export interface LayerPixels {
  /** The decoded pixels of every bitmap layer whose image has arrived, by layer id. */
  bitmaps: BitmapMap;
  /**
   * Bitmap layers whose image is gone: the server answered `404 LAYER_IMAGE_MISSING`. The canvas draws
   * a placeholder for them and Export leaves them out instead of waiting.
   */
  missing: ReadonlySet<string>;
  /**
   * Bitmap layers whose image could not be loaded for any other reason (a network error, a 5xx), even
   * after a few tries. Export must not go ahead without them: that would silently drop a layer.
   */
  unavailable: ReadonlySet<string>;
  /** Tries every unavailable image again now. */
  retry(): void;
  /** Hands over a picture that is already decoded for a URL (a mask just saved), so nothing is fetched or flashes. */
  seed(url: string, bitmap: Bitmap): void;
}

type Failure = 'missing' | 'unavailable' | 'pending';

/**
 * Asks the server why an image would not load. Only its own "this layer's image is gone" answer counts
 * as missing; anything else (another status, no answer at all) may pass, so it is worth trying again.
 */
async function whyNotLoaded(url: string): Promise<Failure> {
  try {
    const res = await fetch(url, { cache: 'no-store' });
    if (res.status !== 404 && res.status !== 409) return 'unavailable';
    const body: unknown = await res.json().catch(() => null);
    const code = body && typeof body === 'object' && 'code' in body ? (body as { code: unknown }).code : null;
    if (code === IMAGE_PENDING) return 'pending';
    return res.status === 404 && code === LAYER_IMAGE_MISSING ? 'missing' : 'unavailable';
  } catch {
    return 'unavailable';
  }
}

const without = <T,>(set: ReadonlySet<T>, value: T): ReadonlySet<T> => {
  if (!set.has(value)) return set;
  const next = new Set(set);
  next.delete(value);
  return next;
};
const withValue = <T,>(set: ReadonlySet<T>, value: T): ReadonlySet<T> =>
  set.has(value) ? set : new Set(set).add(value);

/**
 * Decodes the pixels of every bitmap layer (and the matte of any layer that has one) and hands the compositor a
 * map keyed by layer id. A layer image is immutable once written (the server serves it with an
 * `immutable` cache header), so each URL is fetched once per page; a layer that appears later (a
 * cut-out, an AI result) simply adds a URL. A text layer has no bitmap and is never requested.
 *
 * When an image fails to load, the server is asked why. A `404 LAYER_IMAGE_MISSING` means the image is
 * gone: the layer is reported in `missing`. Any other failure is retried after each of the
 * {@link BITMAP_LOAD_BACKOFF_MS} waits, and if it still fails the layer is reported in `unavailable`.
 * Either way the URL is then forgotten, so a later change to the layer list (at most every
 * {@link BITMAP_RETRY_AFTER_MS}) or a call to `retry` tries it again.
 */
export function useLayerBitmaps(layers: readonly EditorLayer[]): LayerPixels {
  const requests = useRef(new Map<string, Promise<Bitmap>>());
  const failedAt = useRef(new Map<string, number>());
  const matteUrls = useRef(new Set<string>());
  const timers = useRef(new Set<ReturnType<typeof setTimeout>>());
  const alive = useRef(true);
  const [loaded, setLoaded] = useState<ReadonlyMap<string, Bitmap>>(() => new Map());
  const [missingUrls, setMissingUrls] = useState<ReadonlySet<string>>(() => new Set());
  const [unavailableUrls, setUnavailableUrls] = useState<ReadonlySet<string>>(() => new Set());

  useEffect(() => {
    const pending = timers.current;
    alive.current = true;
    return () => {
      alive.current = false;
      for (const timer of pending) clearTimeout(timer);
      pending.clear();
    };
  }, []);

  const start = useCallback((url: string) => {
    const attempt = (tries: number, waits = 0): Promise<Bitmap> =>
      (matteUrls.current.has(url) ? loadBitmap(url, { matte: true }) : loadBitmap(url)).catch(async (cause: unknown) => {
        const why = await whyNotLoaded(url);
        if (why === 'pending' && waits < MAX_PENDING_WAITS) {
          // Still being checked: wait on the back-off without using up the ordinary retries.
          await new Promise<void>((resolve) => {
            const timer = setTimeout(() => {
              timers.current.delete(timer);
              resolve();
            }, outputCheckDelay(waits));
            timers.current.add(timer);
          });
          if (!alive.current) throw new Error('Unmounted.');
          return attempt(tries, waits + 1);
        }
        if (why === 'missing' || tries >= BITMAP_LOAD_BACKOFF_MS.length) {
          throw Object.assign(new Error('A layer image could not be loaded.', { cause }), { why });
        }
        await new Promise<void>((resolve) => {
          const timer = setTimeout(() => {
            timers.current.delete(timer);
            resolve();
          }, BITMAP_LOAD_BACKOFF_MS[tries]);
          timers.current.add(timer);
        });
        if (!alive.current) throw new Error('Unmounted.');
        return attempt(tries + 1);
      });

    const request = attempt(0);
    requests.current.set(url, request);
    request.then(
      (bitmap) => {
        failedAt.current.delete(url);
        if (!alive.current) return;
        setLoaded((prev) => new Map(prev).set(url, bitmap));
        setMissingUrls((prev) => without(prev, url));
        setUnavailableUrls((prev) => without(prev, url));
      },
      (error: unknown) => {
        requests.current.delete(url);
        failedAt.current.set(url, Date.now());
        if (!alive.current) return;
        const why = (error as { why?: Failure }).why;
        if (why === 'missing') {
          setMissingUrls((prev) => withValue(prev, url));
          setUnavailableUrls((prev) => without(prev, url));
        } else if (why === 'unavailable' || why === 'pending') {
          setUnavailableUrls((prev) => withValue(prev, url));
        }
      },
    );
  }, []);

  useEffect(() => {
    const wanted = new Set<string>();
    for (const layer of layers) {
      if (layer.kind === 'text') continue;
      wanted.add(layer.imageUrl);
      if (layer.matteUrl) {
        wanted.add(layer.matteUrl);
        matteUrls.current.add(layer.matteUrl);
      }
    }
    const now = Date.now();
    for (const url of wanted) {
      if (requests.current.has(url)) continue;
      const lastFailure = failedAt.current.get(url);
      if (lastFailure !== undefined && now - lastFailure < BITMAP_RETRY_AFTER_MS) continue;
      start(url);
    }
  }, [layers, start]);

  const retry = useCallback(() => {
    if (unavailableUrls.size === 0) return;
    for (const url of unavailableUrls) {
      if (!requests.current.has(url)) start(url);
    }
    setUnavailableUrls(new Set());
  }, [start, unavailableUrls]);

  const bitmaps = useMemo(() => {
    const map = new Map<string, LayerBitmaps>();
    for (const layer of layers) {
      if (layer.kind === 'text') continue;
      const image = loaded.get(layer.imageUrl);
      if (!image) continue;
      const matte = layer.matteUrl ? loaded.get(layer.matteUrl) : undefined;
      map.set(layer.id, { image, matte });
    }
    return map;
  }, [layers, loaded]);

  const missing = useMemo(() => {
    const ids = new Set<string>();
    if (missingUrls.size === 0) return ids;
    for (const layer of layers) {
      if (layer.kind !== 'text' && !bitmaps.has(layer.id) && missingUrls.has(layer.imageUrl)) ids.add(layer.id);
    }
    return ids;
  }, [bitmaps, missingUrls, layers]);

  // A layer whose matte will not load cannot be drawn correctly either, so it counts as unavailable.
  const unavailable = useMemo(() => {
    const ids = new Set<string>();
    if (unavailableUrls.size === 0) return ids;
    for (const layer of layers) {
      if (layer.kind === 'text' || missing.has(layer.id)) continue;
      const matteUrl = layer.matteUrl;
      if (unavailableUrls.has(layer.imageUrl) || (matteUrl && unavailableUrls.has(matteUrl))) ids.add(layer.id);
    }
    return ids;
  }, [layers, missing, unavailableUrls]);

  const seed = useCallback((url: string, bitmap: Bitmap) => {
    requests.current.set(url, Promise.resolve(bitmap));
    setLoaded((prev) => new Map(prev).set(url, bitmap));
  }, []);

  return { bitmaps, missing, unavailable, retry, seed };
}
