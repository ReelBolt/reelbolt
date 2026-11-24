/**
 * The `.cube` files of grade nodes (L3), read once and held parsed. The planner asks `resolve(file)` at every frame:
 * a file not seen before starts loading (the same size cap and off-thread parse as the viewing LUT, `loadViewingLut`)
 * and answers null until it arrives, then `onReady` asks the viewer to draw the frame again. A refused file stays
 * refused (no retry loop); the node then draws without its LUT.
 */

import type { AssetRef } from '../types';
import { loadViewingLut } from './cube-lut-loader';
import type { CubeLut } from './cube-lut';
import type { GradeLutResolver, ResolvedGradeLut } from '../preview/grade-pass-plan';

/** Parsed node LUTs kept at once (least recently used dropped). */
export const GRADE_LUT_STORE_MAX = 8;

export interface GradeLutStoreOptions {
  /** The raw `.cube` text of a project file (the project-file content route). */
  fetchText: (fileId: string) => Promise<string>;
  /** A file finished loading: draw again. */
  onReady: () => void;
  /** The file's version token / size from the file listing, when known: part of the cache key so an edited file reloads. */
  versionOf?: (fileId: string) => string | null | undefined;
  /** The listed size in bytes, when known: an oversized file is refused before its body is requested. */
  sizeOf?: (fileId: string) => number | null | undefined;
  max?: number;
}

export interface GradeLutStore {
  resolve: GradeLutResolver;
  /** Starts the load on first sight. `loading` until the file arrives; `failed` for a refused file. */
  status(file: AssetRef): 'loading' | 'ready' | 'failed';
  dispose(): void;
}

type Entry = { state: 'loading' } | { state: 'failed' } | { state: 'ready'; lut: CubeLut; key: string };

export function createGradeLutStore(options: GradeLutStoreOptions): GradeLutStore {
  const max = options.max ?? GRADE_LUT_STORE_MAX;
  const entries = new Map<string, Entry>();
  let disposed = false;
  /** Files that were ready and then dropped for room: a status question alone never reloads them (only a draw does). */
  const evicted = new Set<string>();

  const keyOf = (fileId: string): string => `${fileId}:${options.versionOf?.(fileId) ?? ''}`;

  const start = (cacheKey: string, fileId: string): void => {
    evicted.delete(cacheKey);
    entries.set(cacheKey, { state: 'loading' });
    const size = options.sizeOf?.(fileId);
    loadViewingLut(() => options.fetchText(fileId), { key: `grade:${cacheKey}`, sizeHint: typeof size === 'number' ? size : undefined }).then(
      (lut) => {
        if (disposed) return;
        entries.set(cacheKey, { state: 'ready', lut, key: cacheKey });
        while (entries.size > max) {
          const oldest = entries.keys().next().value;
          if (oldest === undefined || oldest === cacheKey) break;
          entries.delete(oldest);
          evicted.add(oldest);
        }
        options.onReady();
      },
      () => {
        if (disposed) return;
        entries.set(cacheKey, { state: 'failed' });
        options.onReady();
      },
    );
  };

  return {
    resolve(file: AssetRef): ResolvedGradeLut | null {
      const fileId = file.projectFileId;
      if (!fileId || disposed) return null;
      const cacheKey = keyOf(fileId);
      const hit = entries.get(cacheKey);
      if (!hit) {
        start(cacheKey, fileId);
        return null;
      }
      if (hit.state !== 'ready') return null;
      // Re-insert so the Map's order is the LRU order.
      entries.delete(cacheKey);
      entries.set(cacheKey, hit);
      return { key: hit.key, lut: hit.lut };
    },
    status(file: AssetRef) {
      const fileId = file.projectFileId;
      if (!fileId || disposed) return 'failed';
      const hit = entries.get(keyOf(fileId));
      if (!hit) {
        if (evicted.has(keyOf(fileId))) return 'ready';
        start(keyOf(fileId), fileId);
        return 'loading';
      }
      return hit.state;
    },
    dispose() {
      disposed = true;
      entries.clear();
      evicted.clear();
    },
  };
}
