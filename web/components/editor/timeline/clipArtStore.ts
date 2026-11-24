/**
 * The cache behind the timeline's clip art (R03.4): for each source on the timeline it loads, once,
 * the derivatives list (waveform peaks), the filmstrip index and its sprite sheets, and answers the
 * painter's {@link ClipArt} questions synchronously from what has arrived. Anything not arrived is
 * null and the clip paints without it; `subscribe` fires when something lands so the canvas
 * repaints. Network and image decoding are injected so the store is testable.
 */

import type { Item, MediaClip } from '@/lib/editing/types';
import type { FilmstripIndex, MediaDerivativeRow, MediaRef } from '@/lib/editing/media/types';
import { filmstripDurationSec, resolveFilmstripThumb } from '@/lib/editing/media/filmstrip';
import { mediaRefOf } from '@/lib/editing/preview/scheduler';
import { derivativeContentUrl, fetchMediaDerivatives, fetchMediaFilmstrip, filmstripSheetUrl } from '@/lib/editing/api/media';
import { peakLodsBySize, peaksBucketMs, type ClipArt, type ClipImage, type PeakData, type ThumbCell } from './clipPaint';

export interface ClipArtLoaders {
  derivatives(projectId: string, ref: MediaRef): Promise<MediaDerivativeRow[]>;
  filmstrip(projectId: string, ref: MediaRef): Promise<{ filmstrip: FilmstripIndex }>;
  bytes(url: string): Promise<ArrayBuffer>;
  image(url: string): Promise<ClipImage>;
}

export const defaultLoaders: ClipArtLoaders = {
  derivatives: fetchMediaDerivatives,
  filmstrip: fetchMediaFilmstrip,
  bytes: async (url) => {
    const res = await fetch(url, { credentials: 'include' });
    if (!res.ok) throw new Error(`peaks ${res.status}`);
    return res.arrayBuffer();
  },
  image: (url) =>
    new Promise<ClipImage>((resolve, reject) => {
      const img = new Image();
      img.crossOrigin = 'use-credentials';
      img.onload = () => resolve(img);
      img.onerror = () => reject(new Error('sheet failed'));
      img.src = url;
    }),
};

interface SourceState {
  durationSec: number | null;
  peaks: PeakData | null;
  filmstrip: FilmstripIndex | null;
  images: Map<string, ClipImage | 'loading' | 'failed'>;
  offline: boolean;
  /** The first lookup settled (success or not): a still-null duration now means "unknown", not "not yet". */
  loaded: boolean;
}

const PEAK_CHANNELS = 2;

/** The assetRef of an item when it names media (a media clip), else null. */
function refOf(item: Item): MediaRef | null {
  if (item.type !== 'mediaClip') return null;
  const ref = mediaRefOf((item as MediaClip).assetRef);
  return ref as MediaRef | null;
}

export class ClipArtStore implements ClipArt {
  private readonly sources = new Map<MediaRef, SourceState>();
  private readonly listeners = new Set<() => void>();
  private version = 0;

  constructor(
    private readonly projectId: string,
    private readonly loaders: ClipArtLoaders = defaultLoaders,
  ) {}

  subscribe(cb: () => void): () => void {
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  }

  getVersion(): number {
    return this.version;
  }

  private bump(): void {
    this.version += 1;
    for (const l of this.listeners) l();
  }

  /** Starts loading a source once. Safe to call every paint. */
  ensure(ref: MediaRef): SourceState {
    const have = this.sources.get(ref);
    if (have) return have;
    const state: SourceState = { durationSec: null, peaks: null, filmstrip: null, images: new Map(), offline: false, loaded: false };
    this.sources.set(ref, state);
    void this.load(ref, state);
    return state;
  }

  private async load(ref: MediaRef, state: SourceState): Promise<void> {
    const [rows, film] = await Promise.allSettled([this.loaders.derivatives(this.projectId, ref), this.loaders.filmstrip(this.projectId, ref)]);
    if (rows.status === 'rejected' && film.status === 'rejected') {
      const status = (rows.reason as { status?: number } | undefined)?.status;
      if (status === 404) state.offline = true;
    }
    if (film.status === 'fulfilled') {
      state.filmstrip = film.value.filmstrip;
      const d = filmstripDurationSec(film.value.filmstrip);
      if (d > 0) state.durationSec = d;
    }
    if (rows.status === 'fulfilled') {
      const durations = rows.value.map((r) => r.durationSec).filter((d): d is number => typeof d === 'number' && d > 0);
      if (durations.length > 0) state.durationSec = Math.max(...durations);
      const peakRows = rows.value.filter((r) => r.kind === 'waveformPeaks');
      // Prefer the finest LOD that is still cheap to draw (100 ms); fall back to whichever exists.
      const bySize = peakLodsBySize(peakRows.map((r) => r.sizeBytes));
      const scored = peakRows
        .map((r, i) => ({ row: r, ms: peaksBucketMs(r.sizeBytes, PEAK_CHANNELS, state.durationSec ?? r.durationSec) ?? bySize[i] }))
        .filter((e): e is { row: MediaDerivativeRow; ms: number } => e.ms !== null)
        .sort((a, b) => Math.abs(a.ms - 100) - Math.abs(b.ms - 100));
      const pick = scored[0];
      if (pick) {
        try {
          const buf = await this.loaders.bytes(derivativeContentUrl(this.projectId, pick.row));
          state.peaks = { data: new Int16Array(buf, 0, Math.floor(buf.byteLength / 2)), channels: PEAK_CHANNELS, bucketMs: pick.ms };
        } catch {
          state.peaks = null;
        }
      }
    }
    state.loaded = true;
    this.bump();
  }

  sourceDurationSec(item: Item): number | null {
    const ref = refOf(item);
    return ref ? this.ensure(ref).durationSec : null;
  }

  thumbAspect(item: Item): number | null {
    const ref = refOf(item);
    if (!ref) return null;
    const d = this.ensure(ref).filmstrip?.densities[0];
    return d && d.thumbWidth > 0 && d.thumbHeight > 0 ? d.thumbWidth / d.thumbHeight : null;
  }

  thumb(item: Item, sourceSec: number): ThumbCell | null {
    const ref = refOf(item);
    if (!ref) return null;
    const state = this.ensure(ref);
    const pos = resolveFilmstripThumb(state.filmstrip, sourceSec, filmstripSheetUrl);
    if (!pos || !pos.sheetUrl) return null;
    const url = pos.sheetUrl;
    const have = state.images.get(url);
    if (have === undefined) {
      state.images.set(url, 'loading');
      this.loaders.image(url).then(
        (img) => {
          state.images.set(url, img);
          this.bump();
        },
        () => state.images.set(url, 'failed'),
      );
      return null;
    }
    if (have === 'loading' || have === 'failed') return null;
    return { image: have, sx: pos.thumbX * pos.thumbWidth, sy: pos.thumbY * pos.thumbHeight, sw: pos.thumbWidth, sh: pos.thumbHeight };
  }

  peaks(item: Item): PeakData | null {
    const ref = refOf(item);
    return ref ? this.ensure(ref).peaks : null;
  }

  lengthUnchecked(item: Item): boolean {
    const ref = refOf(item);
    if (!ref) return false;
    const state = this.ensure(ref);
    return state.loaded && !state.offline && state.durationSec === null;
  }

  offline(item: Item): boolean {
    const ref = refOf(item);
    return ref ? this.ensure(ref).offline : false;
  }
}
