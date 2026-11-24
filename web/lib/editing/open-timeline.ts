import { notifications } from '@mantine/notifications';
import { createEditTimeline, listEditTimelines, type UnplacedItem } from './sync/api';

const TRACK_WORDS: Record<UnplacedItem['track'], { one: string; many: string }> = {
  V1: { one: 'video clip', many: 'video clips' },
  V2: { one: 'screen insert', many: 'screen inserts' },
  V3: { one: 'graphic', many: 'graphics' },
  A2: { one: 'sound', many: 'sounds' },
  A3: { one: 'music track', many: 'music tracks' },
  censor: { one: 'censored stretch', many: 'censored stretches' },
};

/** Plain-words note for items a run's timeline could not place, or null when there are none. */
export function unplacedMessage(items: readonly UnplacedItem[] | undefined | null): string | null {
  if (!items || items.length === 0) return null;
  const counts = new Map<string, { one: string; many: string; n: number }>();
  for (const item of items) {
    const w = TRACK_WORDS[item.track] ?? { one: 'item', many: 'items' };
    const hit = counts.get(w.one);
    if (hit) hit.n += 1;
    else counts.set(w.one, { ...w, n: 1 });
  }
  const parts = [...counts.values()].map((c) => `${c.n} ${c.n === 1 ? c.one : c.many}`);
  const total = items.length;
  return `${total} ${total === 1 ? 'item' : 'items'} from this run could not be placed on the timeline (${parts.join(', ')}). The timeline is open without ${total === 1 ? 'it' : 'them'}.`;
}

/** Shows the yellow, stay-until-closed note; no-op for an empty or absent list. */
export function notifyUnplaced(items: readonly UnplacedItem[] | undefined | null): void {
  const message = unplacedMessage(items);
  if (!message) return;
  notifications.show({ color: 'yellow', title: 'Some items were left out', message, autoClose: false });
}

/**
 * Where the editor lives. There is one video editor — the editable v2 timeline — and one hub that
 * lists what it can open. Every link into either goes through these helpers, so a route change is
 * made in one place.
 */

export type EditorHubView = 'videos' | 'pictures';

export const EDITOR_HUB_VIEWS: readonly EditorHubView[] = ['videos', 'pictures'];

/** Views the hub used to have; links to them still land on Videos. */
export type LegacyEditorHubView = 'timelines' | 'media';

export function isEditorHubView(value: unknown): value is EditorHubView {
  return typeof value === 'string' && (EDITOR_HUB_VIEWS as readonly string[]).includes(value);
}

export function isLegacyEditorHubView(value: unknown): value is LegacyEditorHubView {
  return value === 'timelines' || value === 'media';
}

/** `/editor?project=…&view=…`; the default view (videos) is left out of the URL, and a legacy view maps to it. */
export function editorHubHref(projectId?: string | null, view: EditorHubView | LegacyEditorHubView = 'videos'): string {
  const params = new URLSearchParams();
  if (projectId) params.set('project', projectId);
  if (view === 'pictures') params.set('view', view);
  const query = params.toString();
  return query ? `/editor?${query}` : '/editor';
}

export const timelineEditorHref = (projectId: string, timelineId: string): string =>
  `/editor/${encodeURIComponent(projectId)}/timeline/${encodeURIComponent(timelineId)}`;

export const timelineReviewHref = (projectId: string, timelineId: string): string =>
  `/editor/${encodeURIComponent(projectId)}/review/${encodeURIComponent(timelineId)}`;

/**
 * The timeline a workflow render opens into: the most recently edited timeline already seeded from
 * that render, or a new one seeded from it. Re-opening a render therefore returns to the user's own
 * edit instead of starting over, and never creates a second copy behind their back.
 *
 * Throws what the API threw — a render with no timeline manifest is refused by the server (400/404),
 * and the caller says so in words.
 */
export async function openTimelineForRender(
  projectId: string,
  stepResultId: string,
  name = 'Edited video',
): Promise<string> {
  const existing = (await listEditTimelines(projectId))
    .filter((t) => t.seedStepResultId === stepResultId)
    .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))[0];
  if (existing) return existing.id;
  const created = await createEditTimeline(projectId, { name, seedStepResultId: stepResultId });
  notifyUnplaced(created.unplacedItems);
  return created.id;
}

/** "3 min ago", "Yesterday", "Oct 4": short, for list rows. Falls back to the ISO date. */
export function relativeTime(iso: string, now: number = Date.now()): string {
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return iso;
  const seconds = Math.round((now - then) / 1000);
  if (seconds < 45) return 'Just now';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  if (days === 1) return 'Yesterday';
  if (days < 7) return `${days} days ago`;
  const date = new Date(then);
  const sameYear = date.getFullYear() === new Date(now).getFullYear();
  return date.toLocaleDateString('en-US', sameYear ? { month: 'short', day: 'numeric' } : { month: 'short', day: 'numeric', year: 'numeric' });
}
