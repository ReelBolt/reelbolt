import { useCallback, useMemo, useState, useRef } from 'react';
import { allMarkerRows } from '@/lib/editing/markers/markers';
import { type StabilizeState, mediaRefOf, classifyParity, type ParityClass } from '@/lib/editing/preview';
import type { Track, MediaClip, Item } from '@/lib/editing/types';
import { clipBadgePaints, type ClipBadgeKind, clipBadges, badgeParity } from '@/lib/editing/display/clip-badges';
import { parityReasonMsg } from '@/lib/editing/preview/labels-msg';
import { badgeMenuItems } from '../timeline/badgeMenu';
import { useExternalJobMarks } from '@/lib/editing/background/job-marks';
import { type PaintUi } from '../timeline/paint';
import { useT } from '@/i18n';
import type { PreviewSourcesCtx } from './usePreviewSources';

/** Track flags, what the preview shows exactly (parity), the clip badges, and the marker rows. */
export function usePreviewParity(ctx: PreviewSourcesCtx) {
  const { store, ui, proxyResolver, proxyVersion, doc, hdrKindFor, gradeLutStatus } = ctx;
  const tBadges = useT('editor-timeline.badges');
  // ── flags + parity ────────────────────────────────────────────────────────
  const flags = useCallback((track: Track) => store.effectiveTrackFlags(track), [store]);
  const paintFlags: PaintUi['trackFlags'] = useCallback(
    (track: Track) => ({ ...store.effectiveTrackFlags(track), collapsed: ui.trackOverrides[track.id]?.collapsed ?? false }),
    [store, ui.trackOverrides],
  );
  // A stabilised clip: the stabilised proxy is ready, or being made (the job mark carries its progress).
  const externalJobMarks = useExternalJobMarks();
  const stabilizeFor = useCallback(
    (clip: MediaClip): StabilizeState | null => {
      const ref = mediaRefOf(clip.assetRef);
      if (!ref) return null;
      if (proxyResolver.stabilizedUrlFor(ref)) return { state: 'ready' };
      const mark = externalJobMarks.find((m) => m.id === `stabilize:${ref}`);
      if (mark?.state === 'failed') return { state: 'failed' };
      return { state: 'preparing', percent: mark?.progress !== undefined ? mark.progress * 100 : null };
    },
    // `proxyVersion` changes when a listing lands; the marks change with the stream.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [proxyResolver, externalJobMarks, proxyVersion],
  );
  const parity = useMemo(() => classifyParity(doc, { hdrKindFor, gradeLutStatus, stabilizeFor }), [doc, hdrKindFor, gradeLutStatus, stabilizeFor]);
  const parityOf = useCallback(
    (itemId: string): ParityClass => parity.items.find((i) => i.itemId === itemId)?.parity ?? 'faithful',
    [parity],
  );
  // One pass per document/parity change over every top-level item (never mutated after the memo returns).
  const badgeCache = useMemo(() => {
    const findings = new Map(parity.items.map((i) => [i.itemId, i.findings] as const));
    const cache = new Map<string, ReturnType<typeof clipBadgePaints>>();
    for (const tr of doc.tracks) for (const it of tr.items) cache.set(it.id, clipBadgePaints(it, doc, findings.get(it.id) ?? []));
    return cache;
  }, [doc, parity]);
  const badgesOf = useCallback(
    (item: Item) => badgeCache.get(item.id) ?? clipBadgePaints(item, doc, parity.items.find((i) => i.itemId === item.id)?.findings ?? []),
    [badgeCache, doc, parity],
  );
  const badgeTitle = useCallback(
    (itemId: string, kind: ClipBadgeKind | 'more'): string | null => {
      const item = doc.tracks.flatMap((tr) => tr.items).find((i) => i.id === itemId);
      if (!item) return null;
      const badges = clipBadges(item, doc);
      if (kind === 'more') return tBadges('moreTitle', { count: badges.length });
      const badge = badges.find((b) => b.kind === kind);
      if (!badge) return null;
      const name = tBadges(badge.labelKey);
      const reasons = badgeParity(badge, parity.items.find((i) => i.itemId === itemId)?.findings ?? []).reasons;
      return reasons.length > 0 ? tBadges('titleWithReason', { name, reason: tBadges.msg(parityReasonMsg(reasons[0]!)) }) : badge.enabled ? name : tBadges('titleOff', { name });
    },
    [doc, parity, tBadges],
  );
  const [badgeMenu, setBadgeMenu] = useState<{ x: number; y: number; items: ReturnType<typeof badgeMenuItems> } | null>(null);
  const revealRef = useRef<(itemId: string, kind: ClipBadgeKind) => void>(() => {});
  const revealAdjustment = useCallback((itemId: string, kind: ClipBadgeKind) => revealRef.current(itemId, kind), []);
  const markerRows = useMemo(() => allMarkerRows(doc), [doc]);
  const rationaleFrames = useMemo(() => markerRows.filter((r) => r.kind === 'rationale').map((r) => r.frame), [markerRows]);

  return { flags, paintFlags, externalJobMarks, stabilizeFor, parityOf, badgesOf, badgeTitle, badgeMenu, setBadgeMenu, revealRef, revealAdjustment, markerRows, rationaleFrames };
}

export type PreviewParityCtx = PreviewSourcesCtx & ReturnType<typeof usePreviewParity>;
