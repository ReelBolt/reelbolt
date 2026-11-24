import { msg, type Msg } from '@/i18n/msg';
import type { EditTimelineExportFormat, HandOffScope } from '../sync/api';
import type { HandOffCounts } from './hand-off';
import type { EditorApp, HandOffMediaChoice, HandOffMediaState } from './handoff-ui';

/** i18n counterparts of hand-off.ts and handoff-ui.ts (English originals unchanged). Catalogue: `editor-lib.handoff.*`. */

const K = 'editor-lib.handoff';

/** EDITOR_APPS[].label: only 'other' and 'cuts' are words (Premiere Pro, DaVinci Resolve, Final Cut Pro are product names and stay as the lib's label). */
export const editorAppLabelMsg = (app: EditorApp, productName: string): Msg => (app === 'other' || app === 'cuts' ? msg(`${K}.apps.${app}`) : msg('editor-lib.common.raw', { text: productName }));
/** HAND_OFF_FORMAT_LABELS[format] */
export const handOffFileTypeMsg = (format: 'otio' | 'fcpxml' | 'edl'): Msg => msg(`${K}.fileTypes.${format}`);
/** FORMAT_LABELS[format] / FORMAT_SUMMARY[format] */
export const formatLabelMsg = (format: EditTimelineExportFormat): Msg => msg(`${K}.formats.${format}`);
export const formatSummaryMsg = (format: EditTimelineExportFormat): Msg => msg(`${K}.summary.${format}`);

/** handOffLines(format, counts, options): one message per line. */
export function handOffLinesMsg(format: EditTimelineExportFormat, counts: HandOffCounts, options: { includeAudio?: boolean } = {}): Msg[] {
  const line = (key: string, n: number): Msg => msg(`${K}.lines.${key}`, { n });
  switch (format) {
    case 'srt':
    case 'vtt':
      return counts.captions > 0 ? [line('caption', counts.captions)] : [];
    case 'edl':
      return counts.videoClips > 0 ? [line('videoCut', counts.videoClips)] : [];
    case 'fcpxml': {
      const lines = counts.videoClips > 0 ? [line('videoClip', counts.videoClips)] : [];
      if (options.includeAudio !== false && counts.audioClips > 0) lines.push(line('audioClip', counts.audioClips));
      if (counts.markers > 0) lines.push(line('marker', counts.markers));
      return lines;
    }
    case 'otio': {
      const lines: Msg[] = [];
      if (counts.videoClips > 0) lines.push(line('videoClip', counts.videoClips));
      if (counts.audioClips > 0) lines.push(line('audioClip', counts.audioClips));
      if (counts.titles > 0) lines.push(line('title', counts.titles));
      if (counts.captions > 0) lines.push(line('caption', counts.captions));
      if (counts.markers > 0) lines.push(line('marker', counts.markers));
      return lines;
    }
  }
}
export const mediaSentenceMsg = (mode: 'reelbolt-url' | 'none'): Msg => msg(mode === 'reelbolt-url' ? `${K}.media.url` : `${K}.media.none`);

/** sizeWords(bytes): "1.4 GB"; the number goes through ICU so the decimal mark follows the locale. */
export function sizeWordsMsg(bytes: number): Msg {
  if (!Number.isFinite(bytes) || bytes <= 0) return msg(`${K}.size.value`, { value: 0, unit: 'KB' });
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  if (unit === 0) return msg(`${K}.size.value`, { value: Math.max(1, Math.round(value)), unit: units[0] });
  return msg(`${K}.size.value`, { value: value >= 100 ? Math.round(value) : Math.round(value * 10) / 10, unit: units[unit] });
}
/** handOffSizeLine(scope) */
export function handOffSizeLineMsg(scope: Pick<HandOffScope, 'mediaFiles' | 'mediaBytes'>): Msg {
  if (scope.mediaFiles <= 0) return msg(`${K}.size.noFiles`);
  return msg(`${K}.size.files`, { n: scope.mediaFiles, size: sizeWordsMsg(scope.mediaBytes) });
}
/** handOffMediaState(scope)'s `overCap.reason` */
export const handOffOverCapMsg = (scope: Pick<HandOffScope, 'mediaBytes' | 'capBytes'>): Msg => msg(`${K}.overCap`, { size: sizeWordsMsg(scope.mediaBytes), cap: sizeWordsMsg(scope.capBytes) });
/** The `line` of an `ok` / `overCap` state. */
export function handOffMediaLineMsg(state: HandOffMediaState, scope: Pick<HandOffScope, 'mediaFiles' | 'mediaBytes'>): Msg | null {
  return state.kind === 'ok' || state.kind === 'overCap' ? handOffSizeLineMsg(scope) : null;
}
/** HAND_OFF_MEDIA_OPTIONS[].label */
export const handOffMediaChoiceMsg = (value: HandOffMediaChoice): Msg => msg(`${K}.mediaChoice.${value}`);

/** HOW_TO_OPEN[app]: the three lines, in order. */
export function howToOpenMsgs(app: EditorApp): Msg[] {
  const unzip = msg(`${K}.howTo.${app === 'cuts' ? 'unzipList' : 'unzip'}`);
  return [unzip, msg(`${K}.howTo.${app}`), msg(`${K}.howTo.${app}Open`)];
}
/** handOffFailureWords(status, code, fallbackMessage): the server's own sentence still shows as it is. */
export function handOffFailureMsg(status: number, code: string | null, fallbackMessage: string | null): Msg {
  if (code === 'CONTENT_MODERATION_PENDING') return msg(`${K}.failure.checking`);
  if (code === 'HANDOFF_TOO_LARGE' || status === 409) {
    return fallbackMessage && !/[{}]/.test(fallbackMessage) ? msg('editor-lib.common.raw', { text: fallbackMessage }) : msg(`${K}.failure.tooLarge`);
  }
  if (code === 'HANDOFF_BUSY' || status === 429) return msg(`${K}.failure.busy`);
  if (status === 404) return msg(`${K}.failure.notFound`);
  if (status === 401) return msg(`${K}.failure.signedOut`);
  return msg(`${K}.failure.generic`);
}
/** offlineReasonWords(reason) / matchedHowWords(how) / importHeadline(matched, offline) */
export const offlineReasonMsg = (reason: string | undefined): Msg => msg(`${K}.offline.${reason === 'notFound' || reason === 'ambiguous' || reason === 'durationMismatch' ? reason : 'other'}`);
export const matchedHowMsg = (how: string): Msg => msg(`${K}.matched.${how === 'id' || how === 'nameAndDuration' ? how : 'name'}`);
export const importHeadlineMsg = (matched: number, offline: number): Msg => (offline === 0 ? msg(`${K}.headline.nothingOffline`, { matched }) : msg(`${K}.headline.someOffline`, { matched, offline }));
/** relink.ts: the `label` of a successful buildRelinkOps. */
export const relinkLabelMsg = (): Msg => msg(`${K}.relinkLabel`);
