import { msg, type Msg } from '@/i18n/msg';
import type { EditTimelineVersion } from '@/lib/editing/sync';

/** A gap of this long between two versions starts a new editing session. */
export const SESSION_GAP_MS = 30 * 60_000;
export const NAME_MAX_LENGTH = 80;

export const VERSIONS_RULE = 'Auto-saved checkpoints are kept for 30 days. Name a version to keep it forever.';

const CAUSE_LABELS: Record<string, string> = {
  named: 'Named version',
  beforeRestore: 'Before you restored',
  beforeRender: 'Before a render',
  beforeProposal: 'Before an AI suggestion',
  duplicate: 'Copied from another timeline',
  import: 'Imported',
};

/** The cause in plain words; every automatic kind reads as an auto-saved checkpoint. */
export function causeLabel(version: Pick<EditTimelineVersion, 'cause' | 'name'> & { revision?: number }): string {
  if (version.name && version.cause === 'named') return CAUSE_LABELS.named;
  // Revision 1 is the timeline as it was created; the server never expires it.
  if (version.revision === 1 && version.cause !== 'beforeRestore') return 'Original version';
  return CAUSE_LABELS[version.cause] ?? 'Auto-saved checkpoint';
}

const DAY_MS = 86_400_000;

/** "deleted in 12 days" for an automatic checkpoint, "kept forever" for a named one. */
export function retentionText(version: Pick<EditTimelineVersion, 'expiresAt'>, now: number = Date.now()): string {
  if (!version.expiresAt) return 'kept forever';
  const days = Math.ceil((Date.parse(version.expiresAt) - now) / DAY_MS);
  if (days <= 0) return 'deleted today';
  return `deleted in ${days} ${days === 1 ? 'day' : 'days'}`;
}

export interface VersionSession {
  key: string;
  label: string;
  versions: EditTimelineVersion[];
}

const clock = (ms: number): string => new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });

function dayLabel(ms: number, now: number): string {
  const d = new Date(ms);
  const today = new Date(now);
  const startOf = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((startOf(today) - startOf(d)) / DAY_MS);
  if (diff === 0) return 'Today';
  if (diff === 1) return 'Yesterday';
  return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
}

/** Versions grouped by editing session (a gap of 30 minutes or more starts a new one), newest first. */
export function groupVersions(versions: readonly EditTimelineVersion[], now: number = Date.now()): VersionSession[] {
  const sorted = [...versions].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt) || b.revision - a.revision);
  const sessions: EditTimelineVersion[][] = [];
  for (const v of sorted) {
    const current = sessions[sessions.length - 1];
    const last = current?.[current.length - 1];
    if (current && last && Date.parse(last.createdAt) - Date.parse(v.createdAt) < SESSION_GAP_MS) current.push(v);
    else sessions.push([v]);
  }
  return sessions.map((list) => {
    const newest = Date.parse(list[0].createdAt);
    const oldest = Date.parse(list[list.length - 1].createdAt);
    const range = clock(newest) === clock(oldest) ? clock(newest) : `${clock(oldest)}-${clock(newest)}`;
    return { key: `${list[list.length - 1].revision}-${list[0].revision}`, label: `${dayLabel(newest, now)} ${range}`, versions: list };
  });
}

/* ---- Msg counterparts (display time only; nothing here is stored). ---- */

const V = 'editor-helpers-1.panels.versions';

/** {@link causeLabel} as a Msg. */
export function causeLabelMsg(version: Pick<EditTimelineVersion, 'cause' | 'name'> & { revision?: number }): Msg {
  if (version.name && version.cause === 'named') return msg(`${V}.cause.named`);
  if (version.revision === 1 && version.cause !== 'beforeRestore') return msg(`${V}.cause.original`);
  return msg(version.cause in CAUSE_LABELS ? `${V}.cause.${version.cause}` : `${V}.cause.auto`);
}

/** {@link retentionText} as a Msg. */
export function retentionMsg(version: Pick<EditTimelineVersion, 'expiresAt'>, now: number = Date.now()): Msg {
  if (!version.expiresAt) return msg(`${V}.retention.forever`);
  const days = Math.ceil((Date.parse(version.expiresAt) - now) / DAY_MS);
  return days <= 0 ? msg(`${V}.retention.today`) : msg(`${V}.retention.inDays`, { days });
}

/**
 * A session's heading as a Msg. `formatDay` renders an older day in the active locale (the component passes
 * `useFormat().date`); today and yesterday are catalogue words.
 */
export function sessionLabelMsg(session: VersionSession, formatDay: (ms: number) => string, now: number = Date.now()): Msg {
  const newest = Date.parse(session.versions[0].createdAt);
  const oldest = Date.parse(session.versions[session.versions.length - 1].createdAt);
  const range = clock(newest) === clock(oldest) ? clock(newest) : `${clock(oldest)}-${clock(newest)}`;
  const startOf = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((startOf(new Date(now)) - startOf(new Date(newest))) / DAY_MS);
  const day = diff === 0 ? msg(`${V}.session.today`) : diff === 1 ? msg(`${V}.session.yesterday`) : formatDay(newest);
  return msg(`${V}.session.label`, { day, range });
}
