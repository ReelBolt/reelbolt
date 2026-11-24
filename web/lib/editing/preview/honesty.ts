/**
 * What the viewer may claim about the picture it shows (PA-18) and what it says when a clip
 * cannot be drawn (PA-13).
 *
 * `parity.ts` classifies what the DOCUMENT asks for. This module adds what the SESSION actually
 * did: a layer that is missing, a source that is not a proxy yet, a single-clip fallback or no
 * session at all. The badge is never "Preview faithful" while any of those hold.
 *
 * Badge order, most urgent first: `notReady` (part of the picture is not shown or not from its
 * proxy) → `renderRequired` → `approximate` → `faithful`.
 */

import { msg, type Msg } from '@/i18n/msg';
import type { EditTimeline, MediaClip } from '../types';
import { aClipMsg, honestyReasonMsg, itemTitleMsg, parityReasonMsg, previewSessionReasonMsg, reducedQualityLineMsg, sessionUnavailableLineMsg, simpleFallbackLineMsg, verdictLabelMsg } from './labels-msg';
import type { ParityClass, ParitySummary } from './parity';
import type { ProxyStatus } from './proxy-urls';
import type { FlagsFor } from './scheduler';
import type { MissingLayer, PreviewSessionKind } from './session';

export type BadgeKind = ParityClass | 'notReady';

/** One line of the badge's list: the reason, and the clip it is about (the line jumps to it) when it is about one. */
export interface BadgeEntry {
  text: string;
  itemId?: string;
  /** The same line as a message for the UI's language; `text` stays the stable English fallback. */
  msg?: Msg;
}

export interface PreviewBadge {
  kind: BadgeKind;
  label: string;
  /** The label as a message for the UI's language; `label` stays the stable English fallback. */
  labelMsg?: Msg;
  reasons: string[];
  /** The same reasons as lines for the badge's list: clip lines name their clip ("Clip "Interview": ...") and carry its id. */
  entries: BadgeEntry[];
}

export interface NotReadyClip {
  itemId: string;
  reason: string;
}

export interface PreviewConditions {
  sessionKind: PreviewSessionKind;
  sessionReason: string | null;
  /** Layers the session could not draw at the shown frame. */
  missing: readonly MissingLayer[];
  /** On-screen picture clips that are not playing from their proxy (see {@link notReadyClipsAt}). */
  notReady: readonly NotReadyClip[];
  /** A live session deliberately running below full quality (software graphics): its sentence, else null/absent. */
  reducedQuality?: string | null;
  /** The live session's graphics context is lost (or could not be rebuilt): its sentence; the badge is then "not ready". */
  contextLoss?: string | null;
  /** The Simple preview is applying a colour grade through its SVG filter (primaries only), so the picture is approximate. */
  simpleGrade?: boolean;
}

/** The Simple preview's grade is the primaries through an SVG filter, close but not the render's pixels. */
export const SIMPLE_GRADE_NOTE = "The simple preview shows the grade's primaries approximately.";

export const PREPARING_PREVIEW = 'Preparing preview…';
const OPEN_FAILED =
  "This clip's media could not be opened for preview. The preview tries again on its own; preparing its proxy in the Media tab also makes it play here.";
const NO_FRAME = 'This clip has no picture at this point of its source.';
const MISSING_REASON = 'A layer has no picture at this frame yet.';

function reasonForStatus(status: ProxyStatus): string | null {
  switch (status.state) {
    case 'pending':
      return `${PREPARING_PREVIEW} Looking for this clip's proxy.`;
    case 'original':
      return 'This clip has no proxy yet, so the preview reads its original file, which may stutter or fail to open.';
    case 'unavailable':
      return status.reason;
    default:
      return null;
  }
}

/** The enabled picture clips on screen at `frame` whose bytes are not a proxy yet. */
export function notReadyClipsAt(doc: EditTimeline, frame: number, flags: FlagsFor, statusFor: (clip: MediaClip) => ProxyStatus): NotReadyClip[] {
  const out: NotReadyClip[] = [];
  for (const track of doc.tracks) {
    if (track.kind !== 'video' || !flags(track).enabled) continue;
    for (const item of track.items) {
      if (item.type !== 'mediaClip' || item.recordIn > frame || item.recordOut <= frame) continue;
      if ((item as { enabled?: boolean }).enabled === false) continue;
      const reason = reasonForStatus(statusFor(item));
      if (reason) out.push({ itemId: item.id, reason });
    }
  }
  return out;
}

const NOUN: Partial<Record<string, string>> = {
  mediaClip: 'Clip',
  titleClip: 'Title',
  caption: 'Caption',
  generatorClip: 'Generator',
  insertClip: 'Insert',
  adjustmentClip: 'Adjustment',
  transition: 'Transition',
  compoundClip: 'Compound clip',
  multicamClip: 'Multicam clip',
};

/** "Clip "Interview"" - what a list line calls an item; the bare noun when it has no name. */
export function itemTitle(itemType: string, name: string): string {
  const noun = NOUN[itemType] ?? 'Item';
  return name.trim() ? `${noun} "${name.trim()}"` : noun;
}

/** The lines the badge lists for the document's non-faithful items, each naming its clip and carrying its id. */
export function parityEntries(items: ParitySummary['items']): BadgeEntry[] {
  return items.filter((i) => i.parity !== 'faithful').flatMap((i) => {
    // One line per finding, worst first; an item with no recorded findings still lists its headline reason.
    // (The same sentence once: a clip with a transform and a crop shares one.)
    const reasons = [...new Set(i.findings.length > 0 ? i.findings.map((f) => f.reason) : [i.reason])];
    return reasons.map((reason) => ({
      text: `${itemTitle(i.itemType, i.itemName)}: ${reason}`,
      itemId: i.itemId,
      msg: msg('editor-lib.honesty.lineWithTitle', { title: itemTitleMsg(i.itemType, i.itemName), reason: parityReasonMsg(reason) }),
    }));
  });
}

export function previewBadge(parity: ParitySummary, conditions: PreviewConditions): PreviewBadge {
  const titleOf = (itemId: string): string => {
    const item = parity.items.find((i) => i.itemId === itemId);
    return item ? itemTitle(item.itemType, item.itemName) : 'A clip';
  };
  const titleMsgOf = (itemId: string): Msg => {
    const item = parity.items.find((i) => i.itemId === itemId);
    return item ? itemTitleMsg(item.itemType, item.itemName) : aClipMsg();
  };
  const parityReasons = parity.items.filter((i) => i.parity !== 'faithful').flatMap((i) => (i.findings.length > 0 ? i.findings.map((f) => f.reason) : [i.reason]));
  const parityLines = parityEntries(parity.items);
  const notReadyLines: BadgeEntry[] = [];
  if (conditions.sessionKind === 'unavailable') notReadyLines.push({ text: conditions.sessionReason ?? 'The preview session is unavailable.', msg: sessionUnavailableLineMsg(conditions.sessionReason) });
  if (conditions.sessionKind === 'live' && conditions.contextLoss) notReadyLines.push({ text: conditions.contextLoss, msg: previewSessionReasonMsg(conditions.contextLoss) });
  for (const clip of conditions.notReady) notReadyLines.push({ text: `${titleOf(clip.itemId)}: ${clip.reason}`, itemId: clip.itemId, msg: msg('editor-lib.honesty.lineWithTitle', { title: titleMsgOf(clip.itemId), reason: honestyReasonMsg(clip.reason) }) });
  for (const layer of conditions.missing) notReadyLines.push({ text: `${titleOf(layer.itemId)}: ${MISSING_REASON}`, itemId: layer.itemId, msg: msg('editor-lib.honesty.lineWithTitle', { title: titleMsgOf(layer.itemId), reason: honestyReasonMsg(MISSING_REASON) }) });
  const sessionLines: BadgeEntry[] = [];
  if (conditions.sessionKind === 'fallback') sessionLines.push({ text: `Simple preview: ${conditions.sessionReason ?? 'one clip at a time, from its small copy.'} Your edits are still exact in the final render.`, msg: simpleFallbackLineMsg(conditions.sessionReason) });
  if (conditions.sessionKind === 'fallback' && conditions.simpleGrade) sessionLines.push({ text: SIMPLE_GRADE_NOTE, msg: honestyReasonMsg(SIMPLE_GRADE_NOTE) });
  if (conditions.sessionKind === 'live' && conditions.reducedQuality) sessionLines.push({ text: `Reduced quality: ${conditions.reducedQuality}`, msg: reducedQualityLineMsg(conditions.reducedQuality) });

  const unique = (list: BadgeEntry[]) => {
    const seen = new Set<string>();
    return list.filter((entry) => (seen.has(entry.text) ? false : (seen.add(entry.text), true)));
  };
  const notReadyReasons = [
    ...(conditions.sessionKind === 'unavailable' ? [conditions.sessionReason ?? 'The preview session is unavailable.'] : []),
    ...(conditions.sessionKind === 'live' && conditions.contextLoss ? [conditions.contextLoss] : []),
    ...conditions.notReady.map((clip) => clip.reason),
    ...(conditions.missing.length > 0 ? [MISSING_REASON] : []),
  ];
  const sessionReasons = [
    ...(conditions.sessionKind === 'fallback' ? [conditions.sessionReason ?? 'Single-clip preview.'] : []),
    ...(conditions.sessionKind === 'fallback' && conditions.simpleGrade ? [SIMPLE_GRADE_NOTE] : []),
    ...(conditions.sessionKind === 'live' && conditions.reducedQuality ? [conditions.reducedQuality] : []),
  ];
  const degraded = sessionLines.length > 0;
  const make = (kind: BadgeKind, label: string, labelMsg: Msg, entries: BadgeEntry[], reasons: string[]): PreviewBadge => {
    const deduped = unique(entries);
    return { kind, label, labelMsg, entries: deduped, reasons: [...new Set([...reasons])] };
  };
  if (notReadyLines.length > 0) {
    return make('notReady', 'Preview not ready', verdictLabelMsg('notReady'), [...notReadyLines, ...sessionLines, ...parityLines], [...notReadyReasons, ...sessionReasons, ...parityReasons]);
  }
  if (parity.worst === 'renderRequired') {
    return make('renderRequired', `Render required (${parity.renderRequiredCount})`, verdictLabelMsg('renderRequired', { count: parity.renderRequiredCount }), [...parityLines, ...sessionLines], [...parityReasons, ...sessionReasons]);
  }
  if (parity.worst === 'approximate' || degraded) {
    const count = parity.approximateCount;
    const label = conditions.sessionKind === 'fallback' ? 'Simple preview' : count > 0 ? `Approximate preview (${count})` : 'Approximate preview';
    const labelMsg = verdictLabelMsg('approximate', { count, simple: conditions.sessionKind === 'fallback' });
    return make('approximate', label, labelMsg, [...sessionLines, ...parityLines], [...sessionReasons, ...parityReasons]);
  }
  return { kind: 'faithful', label: 'Preview faithful', labelMsg: verdictLabelMsg('faithful'), reasons: [], entries: [] };
}

/**
 * The one sentence the viewer shows over a picture with a hole in it, or `null` when every layer
 * was drawn. A source still being looked up reads "Preparing preview…"; a failure is said in plain
 * words.
 */
export function viewerNotice(doc: EditTimeline, missing: readonly MissingLayer[], statusFor: (clip: MediaClip) => ProxyStatus): string | null {
  if (missing.length === 0) return null;
  const clipOf = (itemId: string): MediaClip | null => {
    for (const track of doc.tracks) {
      for (const item of track.items) if (item.id === itemId && item.type === 'mediaClip') return item;
    }
    return null;
  };
  // A failure outranks "still preparing": the user can act on it.
  if (missing.some((layer) => layer.cause === 'openFailed')) return OPEN_FAILED;
  for (const layer of missing) {
    if (layer.cause !== 'noUrl') continue;
    const clip = clipOf(layer.itemId);
    const status: ProxyStatus = clip ? statusFor(clip) : { state: 'pending' };
    if (status.state === 'unavailable') return status.reason;
  }
  if (missing.some((layer) => layer.cause === 'noUrl' || layer.cause === 'notDecoded')) return PREPARING_PREVIEW;
  return NO_FRAME;
}
