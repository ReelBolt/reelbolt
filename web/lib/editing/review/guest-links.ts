import { apiFetch } from '@/lib/api/client';


// ── shapes (docs/editor.md "Guest review links (Wave 4)") ────────────────────

export type GuestDecisionKind = 'approved' | 'changesRequested';

export interface GuestDecision {
  decision: GuestDecisionKind;
  guestName: string;
  note?: string | null;
  at: string;
}

export interface ReviewLinkSummary {
  id: string;
  label: string | null;
  status: 'active' | 'expired' | 'revoked';
  expiresAt: string;
  createdAt: string;
  viewCount: number;
  lastViewedAt: string | null;
  stale: boolean;
  decision?: GuestDecision | null;
  guestComments: number;
  unreadGuestActivity: boolean;
}

export interface ReviewLinkDetail extends ReviewLinkSummary {
  allowComments: boolean;
  renderCreatedAt: string;
  decisions: GuestDecision[];
}

export interface CreatedReviewLink {
  id: string;
  /** Shown ONCE: the list and detail routes never return it. */
  url: string;
  label: string | null;
  expiresAt: string;
  renderCreatedAt: string;
  stale: boolean;
}

export interface CreateReviewLinkBody {
  label?: string;
  renderExecutionId?: string;
  expiresInDays?: number;
  allowComments?: boolean;
}

export const EXPIRY_CHOICES = [1, 7, 14, 30, 90] as const;
export const DEFAULT_EXPIRY_DAYS = 14;

// ── owner side (signed in) ───────────────────────────────────────────────────

export const reviewLinksKey = (projectId: string, timelineId: string): string => `/api/v1/projects/${projectId}/edit-timelines/${timelineId}/review-links`;

export const listReviewLinks = async (projectId: string, timelineId: string): Promise<ReviewLinkSummary[]> =>
  ((await apiFetch<{ links?: ReviewLinkSummary[] } | null>(reviewLinksKey(projectId, timelineId)))?.links ?? []).filter(Boolean);

export const getReviewLink = (projectId: string, timelineId: string, linkId: string): Promise<ReviewLinkDetail> =>
  apiFetch<ReviewLinkDetail>(`${reviewLinksKey(projectId, timelineId)}/${linkId}`);

export const createReviewLink = (projectId: string, timelineId: string, body: CreateReviewLinkBody): Promise<CreatedReviewLink> =>
  apiFetch<CreatedReviewLink>(reviewLinksKey(projectId, timelineId), { method: 'POST', body: JSON.stringify(body) });

export const revokeReviewLink = (projectId: string, timelineId: string, linkId: string): Promise<void> =>
  apiFetch<void>(`${reviewLinksKey(projectId, timelineId)}/${linkId}`, { method: 'DELETE' });

/** Number of links with guest activity the owner has not looked at yet (drives the Review badge). */
export const unreadLinkCount = (links: readonly ReviewLinkSummary[] | undefined): number => (links ?? []).filter((l) => l.status === 'active' && l.unreadGuestActivity).length;

/** "Approved by Ana, Oct 26" / "Ana asked for changes: <note>". Plain text; the caller renders it as text. */
export function decisionSentence(d: GuestDecision): string {
  if (d.decision === 'approved') {
    const when = new Date(d.at).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    return `Approved by ${d.guestName}, ${when}`;
  }
  const note = d.note?.trim();
  return note ? `${d.guestName} asked for changes: ${note}` : `${d.guestName} asked for changes`;
}

/** Expiry in words: "in 6 days", "in 3 hours", "expired". */
export function expiryInWords(expiresAt: string, now: number = Date.now()): string {
  const ms = new Date(expiresAt).getTime() - now;
  if (!Number.isFinite(ms) || ms <= 0) return 'expired';
  const hours = Math.floor(ms / 3_600_000);
  if (hours < 1) return 'in under an hour';
  if (hours < 48) return `in ${hours} hour${hours === 1 ? '' : 's'}`;
  const days = Math.floor(hours / 24);
  return `in ${days} days`;
}

// ── guest side (anonymous) ───────────────────────────────────────────────────

export const GUEST_GONE_MESSAGE = 'This link has expired or was turned off.';
export const GUEST_RATE_MESSAGE = 'Too many requests. Try again in a minute.';
export const GUEST_NETWORK_MESSAGE = 'Could not reach ReelBolt. Check your connection and try again.';
export const GUEST_SERVER_MESSAGE = 'Something went wrong on our side. The link may still work: try again in a moment.';
export const GUEST_NAME_KEY = 'reelbolt:review:guest-name';

export interface GuestComment {
  id: string;
  parentId?: string | null;
  authorName: string;
  guest: boolean;
  body: string;
  frame: number;
  durationFrames?: number | null;
  resolved: boolean;
  createdAt: string;
}

export interface GuestReview {
  title: string;
  expiresAt: string;
  allowComments: boolean;
  renderCreatedAt: string;
  stale: boolean;
  durationSec?: number | null;
  /** The timeline's frame rate, so a note's frame number means the same thing as in the editor. */
  rate?: { num: number; den: number } | null;
  comments: GuestComment[];
  decision?: { decision: GuestDecisionKind; guestName: string; at: string } | null;
}

/** `gone`: the link is dead (404/410/403). `network`: the request never arrived. `server`: our side failed, the link may be fine. */
export type GuestFailureKind = 'gone' | 'rate' | 'invalid' | 'network' | 'server' | 'other';

/** Any failure of the anonymous API. `message` is always a sentence that is safe to show a guest. */
export class GuestLinkError extends Error {
  readonly kind: GuestFailureKind;
  constructor(kind: GuestFailureKind, message: string) {
    super(message);
    this.name = 'GuestLinkError';
    this.kind = kind;
  }
}

/** The one place a guest request is made: no cookies, no referrer, no cache, token only in the path. */
async function guestRequest<T>(token: string, suffix: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`/api/v1/review-links/${encodeURIComponent(token)}${suffix}`, {
      ...init,
      credentials: 'omit',
      referrerPolicy: 'no-referrer',
      cache: 'no-store',
      headers: init?.body ? { 'Content-Type': 'application/json' } : undefined,
    });
  } catch {
    throw new GuestLinkError('network', GUEST_NETWORK_MESSAGE);
  }
  if (res.status === 429) throw new GuestLinkError('rate', GUEST_RATE_MESSAGE);
  if (res.status === 400) {
    let message = 'That could not be saved. Check what you typed and try again.';
    try {
      const body: unknown = await res.json();
      const m = (body as { message?: unknown } | null)?.message;
      if (typeof m === 'string' && m.trim() && m.length < 200) message = m.trim();
    } catch {
      // keep the generic sentence
    }
    throw new GuestLinkError('invalid', message);
  }
  if (res.status === 409) throw new GuestLinkError('invalid', 'This link has reached its limit for notes.');
  if (res.status >= 500) throw new GuestLinkError('server', GUEST_SERVER_MESSAGE);
  if (!res.ok) throw new GuestLinkError('gone', GUEST_GONE_MESSAGE);
  try {
    return (await res.json()) as T;
  } catch {
    throw new GuestLinkError('server', GUEST_SERVER_MESSAGE);
  }
}

export const guestMediaUrl = (token: string): string => `/api/v1/review-links/${encodeURIComponent(token)}/media`;

export const fetchGuestReview = (token: string): Promise<GuestReview> => guestRequest<GuestReview>(token, '');

export const postGuestComment = (token: string, body: { name: string; body: string; frame: number; durationFrames?: number | null; parentId?: string | null }): Promise<GuestComment> =>
  guestRequest<GuestComment>(token, '/comments', { method: 'POST', body: JSON.stringify(body) });

export const postGuestDecision = (token: string, body: { decision: GuestDecisionKind; name: string; note?: string }): Promise<GuestDecision> =>
  guestRequest<GuestDecision>(token, '/decision', { method: 'POST', body: JSON.stringify(body) });

/** Threads for the guest list: roots sorted by time, replies oldest first. */
export function guestThreads(comments: readonly GuestComment[]): { root: GuestComment; replies: GuestComment[] }[] {
  const roots = comments.filter((c) => !c.parentId).sort((a, b) => a.frame - b.frame || a.createdAt.localeCompare(b.createdAt));
  return roots.map((root) => ({ root, replies: comments.filter((c) => c.parentId === root.id).sort((a, b) => a.createdAt.localeCompare(b.createdAt)) }));
}

/** A changes request needs a note unless the guest has already commented. */
export function changesNeedsNote(note: string, guestHasCommented: boolean): boolean {
  return note.trim().length === 0 && !guestHasCommented;
}
