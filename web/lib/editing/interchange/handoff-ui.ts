/**
 * R12.2 / R12.3: the words and choices of the "For another editor" card and of the import result
 * sheet. Pure: the components render these, the tests pin them. No jargon, no ids, no em dashes.
 */

import type { EditTimelineExportFormat, HandOffScope, ImportAmbiguousMedia, ImportMatchedMedia, ImportMediaCandidate, OfflineClip } from '../sync/api';

export type HandOffFormat = Exclude<EditTimelineExportFormat, 'srt' | 'vtt'>;
export type EditorApp = 'premiere' | 'resolve' | 'fcp' | 'other' | 'cuts';

export const EDITOR_APPS: readonly { value: EditorApp; label: string }[] = [
  { value: 'premiere', label: 'Premiere Pro' },
  { value: 'resolve', label: 'DaVinci Resolve' },
  { value: 'fcp', label: 'Final Cut Pro' },
  { value: 'other', label: 'Another editor' },
  { value: 'cuts', label: 'Cuts only (EDL)' },
];

const APP_FORMATS: Record<EditorApp, readonly HandOffFormat[]> = {
  premiere: ['otio', 'fcpxml'],
  resolve: ['otio', 'fcpxml'],
  fcp: ['fcpxml'],
  other: ['otio'],
  cuts: ['edl'],
};

/** The file types an app can be given, the first being the one we pick for it. */
export function formatsForApp(app: EditorApp): readonly HandOffFormat[] {
  return APP_FORMATS[app];
}

export function defaultFormatForApp(app: EditorApp): HandOffFormat {
  return APP_FORMATS[app][0];
}

export const HAND_OFF_FORMAT_LABELS: Record<HandOffFormat, string> = {
  otio: 'OpenTimelineIO (.otio)',
  fcpxml: 'Final Cut Pro XML (.fcpxml)',
  edl: 'Cuts only (.edl)',
};

/** Whether the file type has a video-track choice (the cut list holds one video track). */
export function formatNeedsTrack(format: HandOffFormat): boolean {
  return format === 'edl';
}

/** A size in words: "1.4 GB", "820 MB", "12 KB". */
export function sizeWords(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 KB';
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  if (unit === 0) return `${Math.max(1, Math.round(value))} KB`;
  return `${value >= 100 ? Math.round(value) : Math.round(value * 10) / 10} ${units[unit]}`;
}

/** "12 files, 1.4 GB" (or "No media files"). */
export function handOffSizeLine(scope: Pick<HandOffScope, 'mediaFiles' | 'mediaBytes'>): string {
  if (scope.mediaFiles <= 0) return 'No media files';
  return `${scope.mediaFiles} ${scope.mediaFiles === 1 ? 'file' : 'files'}, ${sizeWords(scope.mediaBytes)}`;
}

export type HandOffMediaState =
  | { kind: 'unknown' }
  | { kind: 'unsupported' }
  | { kind: 'ok'; line: string }
  | { kind: 'overCap'; line: string; reason: string };

/** What the media choice looks like for the scope the server returned. */
export function handOffMediaState(scope: HandOffScope | undefined): HandOffMediaState {
  if (!scope) return { kind: 'unknown' };
  if (!scope.supported) return { kind: 'unsupported' };
  const line = handOffSizeLine(scope);
  if (scope.wouldExceedCap) {
    return {
      kind: 'overCap',
      line,
      reason: `The video files add up to ${sizeWords(scope.mediaBytes)}, more than a package can hold (${sizeWords(scope.capBytes)}). Download the list of files instead and copy the originals yourself.`,
    };
  }
  return { kind: 'ok', line };
}

export type HandOffMediaChoice = 'zip' | 'manifest';

export const HAND_OFF_MEDIA_OPTIONS: readonly { value: HandOffMediaChoice; label: string }[] = [
  { value: 'zip', label: 'Include the video files' },
  { value: 'manifest', label: 'List files only' },
];

/** The media choice to start from: files in the package, unless they would not fit. */
export function initialMediaChoice(state: HandOffMediaState): HandOffMediaChoice {
  return state.kind === 'overCap' ? 'manifest' : 'zip';
}

/** Three plain lines per app, from the package README. We have not opened the package in these apps here. */
export const HOW_TO_OPEN: Record<EditorApp, readonly string[]> = {
  premiere: [
    'Unzip the whole package and keep the cut and the media folder side by side.',
    'In Premiere Pro choose File, Import and pick the cut. If it asks for media, choose Locate and pick the file with the same name.',
    'Should open in Premiere Pro. Tell us if it does not.',
  ],
  resolve: [
    'Unzip the whole package and keep the cut and the media folder side by side.',
    'In DaVinci Resolve choose File, Import, Timeline. If clips show as offline, right-click them and choose Relink Selected Clips, then pick the media folder.',
    'Should open in DaVinci Resolve. Tell us if it does not.',
  ],
  fcp: [
    'Unzip the whole package and keep the cut and the media folder side by side.',
    'In Final Cut Pro choose File, Import, XML and pick the cut. If a clip is missing, choose File, Relink Files.',
    'Should open in Final Cut Pro. Tell us if it does not.',
  ],
  other: [
    'Unzip the whole package and keep the cut and the media folder side by side.',
    'Import the cut as a timeline. Every clip is matched by its file name, so please do not rename the files.',
    'Should open in editors that read OpenTimelineIO. Tell us if it does not.',
  ],
  cuts: [
    'Unzip the whole package and keep the cut list and the media folder side by side.',
    'Import the cut list as an EDL. It holds the cuts of one video track only, with no effects, titles or audio.',
    'Should open in editors that read EDL files. Tell us if it does not.',
  ],
};

/** Plain words for a refused hand-off request (the body code first, then the status). */
export function handOffFailureWords(status: number, code: string | null, fallbackMessage: string | null): string {
  if (code === 'HANDOFF_TOO_LARGE' || status === 409) {
    return fallbackMessage && !/[{}]/.test(fallbackMessage) ? fallbackMessage : 'The video files are too big for one package. Choose "List files only" instead.';
  }
  if (code === 'HANDOFF_BUSY' || status === 429) return 'Two packages are already being prepared for you. Wait for one to finish, then try again.';
  if (status === 404) return 'This timeline could not be found. Reload the page and try again.';
  if (status === 401) return 'Your session has ended. Sign in again to continue.';
  return 'The package could not be prepared. Try again in a moment.';
}

// ── Import result (R12.3) ────────────────────────────────────────────────────────────────────────

/** Why a clip is offline, in words. */
export function offlineReasonWords(reason: string | undefined): string {
  switch (reason) {
    case 'notFound':
      return 'No file with this name in the project';
    case 'ambiguous':
      return 'Two files share this name; pick one';
    case 'durationMismatch':
      return "The file's length differs from the clip";
    default:
      return 'This file could not be matched';
  }
}

/** How a file was matched, in words. */
export function matchedHowWords(how: string): string {
  switch (how) {
    case 'id':
      return 'Same file as the export';
    case 'nameAndDuration':
      return 'Matched by name and length';
    default:
      return 'Matched by name';
  }
}

/** "3 files matched, 2 offline" style headline. */
export function importHeadline(matched: number, offline: number): string {
  const m = `${matched} ${matched === 1 ? 'file' : 'files'} matched`;
  if (offline === 0) return `${m}. Nothing is offline.`;
  return `${m}, ${offline} offline.`;
}

export interface ImportSheetModel {
  matched: ImportMatchedMedia[];
  offline: { itemId: string; name: string; reason: string; reasonWords: string; candidates: ImportMediaCandidate[] }[];
}

/** The sheet's rows from the 201 body; an old server (no `matched`/`ambiguous`) still gives the offline rows. */
export function importSheetModel(body: { matched?: ImportMatchedMedia[]; offline?: OfflineClip[]; ambiguous?: ImportAmbiguousMedia[] }): ImportSheetModel {
  const ambiguous = new Map((body.ambiguous ?? []).map((a) => [a.name.toLowerCase(), a.candidates]));
  return {
    matched: body.matched ?? [],
    offline: (body.offline ?? []).map((o) => ({
      itemId: o.itemId,
      name: o.name,
      reason: o.reason,
      reasonWords: offlineReasonWords(o.reason),
      candidates: o.reason === 'ambiguous' ? rankCandidates(ambiguous.get(o.name.toLowerCase()) ?? []) : [],
    })),
  };
}

/** Candidates in the order to offer them: the closest length to the clip first, unknown lengths last, then by size and name. */
export function rankCandidates(candidates: readonly ImportMediaCandidate[], clipSeconds?: number | null): ImportMediaCandidate[] {
  const distance = (c: ImportMediaCandidate): number =>
    c.durationSec == null ? Number.POSITIVE_INFINITY : clipSeconds == null ? 0 : Math.abs(c.durationSec - clipSeconds);
  return [...candidates].sort((a, b) => {
    const da = distance(a);
    const db = distance(b);
    if (da !== db) return da < db ? -1 : 1;
    if (a.sizeBytes !== b.sizeBytes) return b.sizeBytes - a.sizeBytes;
    return a.name.localeCompare(b.name);
  });
}

/** A candidate as one line: "interview.mov, 2:05, 340 MB". */
export function candidateLabel(c: ImportMediaCandidate): string {
  const parts = [c.name];
  if (c.durationSec != null && c.durationSec >= 0) {
    const total = Math.round(c.durationSec);
    parts.push(`${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`);
  }
  parts.push(sizeWords(c.sizeBytes));
  return parts.join(', ');
}

/** The media reference the editor's relink flow takes for a project file. */
export function fileMediaRef(fileId: string): string {
  return `file:${fileId}`;
}

// The sheet follows the import across the page change from the timelines list to the editor.
const STORAGE_PREFIX = 'reelbolt:import-result:';

export function stashImportResult(timelineId: string, body: { matched?: ImportMatchedMedia[]; offline?: OfflineClip[]; ambiguous?: ImportAmbiguousMedia[] }): void {
  if (typeof window === 'undefined') return;
  if ((body.offline ?? []).length === 0 && (body.matched ?? []).length === 0) return;
  try {
    window.sessionStorage.setItem(STORAGE_PREFIX + timelineId, JSON.stringify({ matched: body.matched ?? [], offline: body.offline ?? [], ambiguous: body.ambiguous ?? [] }));
  } catch {
    /* storage unavailable: the offline banner in the editor still shows the problem */
  }
}

export function clearImportResult(timelineId: string): void {
  if (typeof window === 'undefined') return;
  try {
    window.sessionStorage.removeItem(STORAGE_PREFIX + timelineId);
  } catch {
    /* nothing to clear */
  }
}

/** Reads the stashed result without removing it (a lazy initialiser may run twice); the sheet clears it on close. */
export function peekImportResult(timelineId: string): ImportSheetModel | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.sessionStorage.getItem(STORAGE_PREFIX + timelineId);
    if (!raw) return null;
    return importSheetModel(JSON.parse(raw) as Parameters<typeof importSheetModel>[0]);
  } catch {
    return null;
  }
}
