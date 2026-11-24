import type { SocialPost, SocialPostStatus, SocialPrivacy, SocialProviderId } from '@/lib/types/social';

/** Privacy choices per platform: YouTube has three, LinkedIn only public and connections. */
export function privacyChoices(provider: SocialProviderId): SocialPrivacy[] {
  if (provider === 'youtube') return ['public', 'unlisted', 'private'];
  if (provider === 'linkedin') return ['public', 'connections'];
  return ['public'];
}

/** The post-captions platform id whose saved text a connected account's provider reads. */
export function postTextPlatform(provider: SocialProviderId): string {
  return provider === 'youtube' ? 'youtube_shorts' : provider;
}

export function hasTitle(provider: SocialProviderId): boolean {
  return provider === 'youtube';
}

export function parseHashtags(text: string): string[] {
  return text.split(/[\s,]+/).map((h) => h.replace(/^#+/, '').trim()).filter(Boolean);
}

export function hashtagsText(tags: readonly string[]): string {
  return tags.map((h) => `#${h}`).join(' ');
}

const pad = (n: number) => String(n).padStart(2, '0');

/** `2026-11-20T09:30:00` from a date input value (`2026-11-20`) and a time input value (`09:30`). */
export function localDateTime(date: string, time: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date) || !/^\d{2}:\d{2}$/.test(time)) return null;
  return `${date}T${time}:00`;
}

export function splitLocal(local: string | null): { date: string; time: string } {
  const m = local?.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/);
  return { date: m?.[1] ?? '', time: m?.[2] ?? '09:00' };
}

export function browserTimeZone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
}

export function timeZoneChoices(current: string): string[] {
  let all: string[] = [];
  try {
    const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
    all = supported ? supported('timeZone') : [];
  } catch { all = []; }
  return all.includes(current) ? all : [current, ...all];
}

/** The day a post belongs to on the calendar, in the person's own zone: its wall-clock local time when it has one. */
export function postDayKey(post: SocialPost): string | null {
  if (post.scheduledLocal) return post.scheduledLocal.slice(0, 10);
  const iso = post.scheduledAt ?? post.publishedAt;
  if (!iso) return null;
  const d = new Date(iso);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export interface CalendarCell { key: string; day: number; inMonth: boolean }

/** Weeks (Monday first) covering a month; `month` is 0-based. */
export function monthGrid(year: number, month: number): CalendarCell[][] {
  const first = new Date(year, month, 1);
  const offset = (first.getDay() + 6) % 7;
  const start = new Date(year, month, 1 - offset);
  const weeks: CalendarCell[][] = [];
  const cursor = new Date(start);
  do {
    const week: CalendarCell[] = [];
    for (let i = 0; i < 7; i += 1) {
      week.push({
        key: `${cursor.getFullYear()}-${pad(cursor.getMonth() + 1)}-${pad(cursor.getDate())}`,
        day: cursor.getDate(),
        inMonth: cursor.getMonth() === month,
      });
      cursor.setDate(cursor.getDate() + 1);
    }
    weeks.push(week);
  } while (cursor.getMonth() === month);
  return weeks;
}

export function groupByDay(posts: readonly SocialPost[]): Map<string, SocialPost[]> {
  const map = new Map<string, SocialPost[]>();
  for (const p of posts) {
    const key = postDayKey(p);
    if (!key) continue;
    map.set(key, [...(map.get(key) ?? []), p]);
  }
  return map;
}

export const OPEN_STATUSES: readonly SocialPostStatus[] = ['scheduled', 'publishing', 'unknown'];

export function statusColor(status: SocialPostStatus): string {
  switch (status) {
    case 'published': return 'green';
    case 'scheduled': return 'blue';
    case 'publishing': return 'cyan';
    case 'failed': return 'red';
    case 'unknown': return 'orange';
    default: return 'gray';
  }
}
