/**
 * Which bytes the viewer decodes for each source — the proxy when one exists, the original
 * otherwise — and, when neither is known yet, WHY (so the viewer can say "Preparing preview…"
 * instead of showing black).
 *
 * The resolver outlives React effect runs on purpose. A lookup is never cancelled: whatever the
 * derivative API answers is kept, however many times the editor re-renders, re-runs its effects or
 * (in development) double-mounts them. Before this module the editor kept the map itself, wrote a
 * `null` placeholder before fetching, dropped the answer when the effect re-ran and then skipped
 * the source because it was "already known" — the viewer stayed black until a reload (PA-1).
 *
 * `request(refs, { refresh: true })` asks again for every source that has no proxy yet: the editor
 * calls it when the media pool changes, so a proxy built while the editor is open is picked up.
 * Listeners hear every change, which is the viewer's cue to re-present the paused frame (PA-6).
 */

import type { EditTimeline } from '../types';
import { pickProxyRow, type MediaDerivativeRow } from '../media/types';
import { mediaRefOf } from './scheduler';
import { pickStabilizedRow } from './stabilize-proxy';

export type ProxyStatus =
  /** The derivative lookup has not answered yet. */
  | { state: 'pending' }
  /** A proxy row exists: the viewer decodes it. */
  | { state: 'proxy'; url: string }
  /** No proxy (yet): the viewer decodes the original bytes. */
  | { state: 'original'; url: string }
  /** Nothing decodable: no proxy row and no original route. */
  | { state: 'unavailable'; reason: string };

export interface ProxyResolverDeps {
  fetchDerivatives(mediaRef: string): Promise<MediaDerivativeRow[]>;
  proxyUrl(row: MediaDerivativeRow): string;
  originalUrl(mediaRef: string): string | null;
  /** Asks the service for the converted proxy of (source, camera profile). Called at most once per pair per session. */
  requestConverted?(mediaRef: string, cameraProfile: string): void;
  /** Asks the service for the stabilised proxy of a source. Called at most once per source per session (plus explicit retries). */
  requestStabilized?(mediaRef: string): void;
}

const PENDING: ProxyStatus = Object.freeze({ state: 'pending' });
export const NO_MEDIA_REASON = 'This source has no proxy yet and no original the preview can read.';

export class ProxyUrlResolver {
  private readonly statuses = new Map<string, ProxyStatus>();
  private readonly inFlight = new Set<string>();
  private readonly listeners = new Set<() => void>();
  /** Ready converted proxies: source -> camera profile -> url. */
  private readonly converted = new Map<string, Map<string, string>>();
  /** (source, profile) pairs a clip in the open timeline asked for, and the ones already requested (once per session). */
  private readonly wanted = new Set<string>();
  private readonly requestedConversions = new Set<string>();
  /** Ready stabilised proxies: source -> url. */
  private readonly stabilized = new Map<string, string>();
  /** Sources a stabilised clip in the open timeline uses, and the ones already requested (once per session). */
  private readonly wantedStabilized = new Set<string>();
  private readonly requestedStabilized = new Set<string>();

  constructor(private readonly deps: ProxyResolverDeps) {}

  status(mediaRef: string | null): ProxyStatus {
    if (!mediaRef) return PENDING;
    return this.statuses.get(mediaRef) ?? PENDING;
  }

  urlFor(mediaRef: string | null, cameraProfile?: string | null): string | null {
    if (mediaRef && cameraProfile) {
      const hit = this.converted.get(mediaRef)?.get(cameraProfile);
      if (hit) return hit;
    }
    const status = this.status(mediaRef);
    return status.state === 'proxy' || status.state === 'original' ? status.url : null;
  }

  /** The stabilised proxy's url when it is ready (listed), else null. */
  stabilizedUrlFor(mediaRef: string | null): string | null {
    return (mediaRef && this.stabilized.get(mediaRef)) || null;
  }

  /**
   * A clip with stabilisation on uses these sources: when no stabilised proxy is listed for one, it is requested ONCE per
   * session (never on hover). Needs the lookup to have answered first; a source still pending is requested when it does.
   */
  wantStabilized(refs: Iterable<string>): void {
    for (const ref of refs) {
      this.wantedStabilized.add(ref);
      this.flushStabilized(ref);
    }
  }

  /** The explicit "Retry" after a failed run: asks again and looks the source up again. */
  retryStabilized(mediaRef: string): void {
    this.requestedStabilized.delete(mediaRef);
    this.wantedStabilized.add(mediaRef);
    this.flushStabilized(mediaRef);
    this.request([mediaRef], { refresh: true });
  }

  private flushStabilized(ref: string): void {
    if (!this.statuses.has(ref) || !this.wantedStabilized.has(ref) || this.stabilized.has(ref) || this.requestedStabilized.has(ref)) return;
    this.requestedStabilized.add(ref);
    this.deps.requestStabilized?.(ref);
  }

  /** True when `urlFor(mediaRef, cameraProfile)` is the converted proxy (the picture already carries the conversion). */
  bakedFor(mediaRef: string | null, cameraProfile: string | null | undefined): boolean {
    return !!mediaRef && !!cameraProfile && this.converted.get(mediaRef)?.has(cameraProfile) === true;
  }

  /**
   * A clip in the open timeline carries these (source, profile) pairs: when no converted proxy is ready for one, it is
   * requested ONCE per session (the plain proxy plays with the GL input stage meanwhile). Needs the lookup to have
   * answered first; a pair whose source is still pending is requested when it does.
   */
  wantProfiles(pairs: Iterable<{ mediaRef: string; cameraProfile: string }>): void {
    for (const { mediaRef, cameraProfile } of pairs) {
      this.wanted.add(this.pairKey(mediaRef, cameraProfile));
      this.flushWanted(mediaRef);
    }
  }

  private pairKey(mediaRef: string, cameraProfile: string): string {
    return `${mediaRef}\u0000${cameraProfile}`;
  }

  private hasUnmetWanted(mediaRef: string): boolean {
    for (const key of this.wanted) {
      const [ref, profile] = key.split('\u0000');
      if (ref === mediaRef && !this.converted.get(ref)?.has(profile as string)) return true;
    }
    return this.wantedStabilized.has(mediaRef) && !this.stabilized.has(mediaRef);
  }

  private flushWanted(mediaRef: string): void {
    if (!this.statuses.has(mediaRef)) return;
    for (const key of this.wanted) {
      const [ref, profile] = key.split('\u0000') as [string, string];
      if (ref !== mediaRef || this.converted.get(ref)?.has(profile) || this.requestedConversions.has(key)) continue;
      this.requestedConversions.add(key);
      this.deps.requestConverted?.(ref, profile);
    }
  }

  /**
   * Makes sure every ref has (or is getting) an answer. Without `refresh` a known answer is final;
   * with it, sources that have no proxy yet are asked again. A ref already being asked is skipped.
   */
  request(refs: Iterable<string>, options: { refresh?: boolean } = {}): void {
    for (const ref of refs) {
      if (this.inFlight.has(ref)) continue;
      const known = this.statuses.get(ref);
      if (known?.state === 'proxy' && !this.hasUnmetWanted(ref)) continue;
      if (known && !options.refresh) continue;
      this.lookup(ref);
    }
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  private lookup(ref: string): void {
    this.inFlight.add(ref);
    void this.deps
      .fetchDerivatives(ref)
      .then(
        (rows) => ({ proxy: pickProxyRow(rows), converted: convertedRowsOf(rows), stabilizedRow: pickStabilizedRow(rows) }),
        () => ({ proxy: null, converted: [] as MediaDerivativeRow[], stabilizedRow: null }),
      )
      .then(({ proxy, converted, stabilizedRow }) => {
        this.inFlight.delete(ref);
        const urls = new Map<string, string>();
        for (const row of converted) urls.set(row.cameraProfile as string, this.deps.proxyUrl(row));
        const convertedChanged = JSON.stringify([...urls]) !== JSON.stringify([...(this.converted.get(ref) ?? [])]);
        const stabilizedUrl = stabilizedRow ? this.deps.proxyUrl(stabilizedRow) : null;
        const stabilizedChanged = (this.stabilized.get(ref) ?? null) !== stabilizedUrl;
        if (stabilizedUrl) this.stabilized.set(ref, stabilizedUrl);
        else this.stabilized.delete(ref);
        if (urls.size > 0) this.converted.set(ref, urls);
        else this.converted.delete(ref);
        const original = this.deps.originalUrl(ref);
        const next: ProxyStatus = proxy
          ? { state: 'proxy', url: this.deps.proxyUrl(proxy) }
          : original
            ? { state: 'original', url: original }
            : { state: 'unavailable', reason: NO_MEDIA_REASON };
        const previous = this.statuses.get(ref);
        this.statuses.set(ref, next);
        this.flushWanted(ref);
        this.flushStabilized(ref);
        if (convertedChanged || stabilizedChanged || !previous || JSON.stringify(previous) !== JSON.stringify(next)) this.notify();
      });
  }

  private notify(): void {
    for (const listener of [...this.listeners]) listener();
  }
}

/** The converted proxy rows of a listing (the ones with a camera profile), latest per profile. */
function convertedRowsOf(rows: readonly MediaDerivativeRow[] | null | undefined): MediaDerivativeRow[] {
  const byProfile = new Map<string, MediaDerivativeRow>();
  for (const row of Array.isArray(rows) ? rows : []) {
    if (row && row.kind === 'proxy' && row.cameraProfile) byProfile.set(row.cameraProfile, row);
  }
  return [...byProfile.values()];
}

/** Every (source, camera profile) a document's clips ask to be converted through. */
export function inputProfilesIn(doc: EditTimeline): Array<{ mediaRef: string; cameraProfile: string }> {
  const seen = new Set<string>();
  const out: Array<{ mediaRef: string; cameraProfile: string }> = [];
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.type !== 'mediaClip' || !item.inputProfile) continue;
      const mediaRef = mediaRefOf(item.assetRef);
      if (!mediaRef) continue;
      const key = `${mediaRef}\u0000${item.inputProfile}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ mediaRef, cameraProfile: item.inputProfile });
    }
  }
  return out;
}

/** Every media ref a document's clips read (the flattened document, so nested clips count). */
export function mediaRefsIn(doc: EditTimeline): Set<string> {
  const refs = new Set<string>();
  for (const track of doc.tracks) {
    for (const item of track.items) {
      const ref = (item as { assetRef?: Parameters<typeof mediaRefOf>[0] | null }).assetRef;
      const mediaRef = ref ? mediaRefOf(ref) : null;
      if (mediaRef) refs.add(mediaRef);
    }
  }
  return refs;
}

/**
 * The input stage a clip's layer runs, from what the resolver knows: null for a clip with no profile and for an HDR clip
 * (the renderer ignores the profile there too). `baked` = the proxy being played is the converted one for this profile.
 */
export function inputStageOf(
  clip: { inputProfile?: string | null; assetRef?: Parameters<typeof mediaRefOf>[0] | null },
  resolver: Pick<ProxyUrlResolver, 'bakedFor'>,
  isHdr: boolean,
): { profile: string; baked: boolean } | null {
  if (!clip.inputProfile || isHdr) return null;
  const mediaRef = clip.assetRef ? mediaRefOf(clip.assetRef) : null;
  return { profile: clip.inputProfile, baked: resolver.bakedFor(mediaRef, clip.inputProfile) };
}
