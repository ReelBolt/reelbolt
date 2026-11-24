import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import { describeError } from '@/lib/api/describe-error';
import type { AppCostClass } from '@/lib/types/workflow';
import type {
  MarketplaceRecipeStatus,
  MarketplacePreviewStatus,
  MarketplaceRejectReason,
  MarketplaceFilters,
  MarketplaceRecipeCard,
  MarketplaceReportReason,
  MarketplaceSlotKind,
  MarketplaceSort,
} from '@/lib/types/marketplace';

/** Fewer ratings than this show "New" instead of an average. */
export const MIN_RATINGS_FOR_AVERAGE = 5;

export const SEARCH_DEBOUNCE_MS = 300;

export const DEFAULT_FILTERS: MarketplaceFilters = {
  q: '',
  category: '',
  cost: '',
  stepType: '',
  language: '',
  sort: 'new',
};

export function hasActiveFilters(f: MarketplaceFilters): boolean {
  return f.q.trim() !== '' || f.category !== '' || f.cost !== '' || f.stepType !== '' || f.language !== '';
}

export const SORTS: { value: MarketplaceSort; label: Msg }[] = [
  { value: 'new', label: msg('marketplace.gallery.sort.new') },
  { value: 'copied', label: msg('marketplace.gallery.sort.copied') },
  { value: 'rated', label: msg('marketplace.gallery.sort.rated') },
];

export function isSort(value: string): value is MarketplaceSort {
  return SORTS.some((s) => s.value === value);
}

/** The four cost classes of the app gallery, in the order the badges are explained there. */
export const COST_CLASSES: AppCostClass[] = ['No credits', 'Uses credits', 'Uses credits and a voice', 'Uses credits and AI shots'];

const CATEGORY_LABELS: Record<string, Msg> = {
  promo: msg('marketplace.gallery.categories.promo'),
  footage: msg('marketplace.gallery.categories.footage'),
  brand: msg('marketplace.gallery.categories.brand'),
  utility: msg('marketplace.gallery.categories.utility'),
};

/** The words for a category key, or null for one this build does not know (the caller shows the key). */
export function categoryLabel(key: string): Msg | null {
  return CATEGORY_LABELS[key] ?? null;
}

export const SLOT_KIND_LABELS: Record<MarketplaceSlotKind, Msg> = {
  provider: msg('marketplace.detail.slot.provider'),
  file: msg('marketplace.detail.slot.file'),
  voice: msg('marketplace.detail.slot.voice'),
};

export const REPORT_REASON_LABELS: Record<MarketplaceReportReason, Msg> = {
  illegal: msg('marketplace.report.reasons.illegal'),
  sexual: msg('marketplace.report.reasons.sexual'),
  copyright: msg('marketplace.report.reasons.copyright'),
  spam: msg('marketplace.report.reasons.spam'),
  misleading: msg('marketplace.report.reasons.misleading'),
  other: msg('marketplace.report.reasons.other'),
};

export type RatingDisplay = { kind: 'new' } | { kind: 'average'; average: number; count: number };

/** The average only from five ratings on; below that the card says "New". */
export function ratingDisplay(card: Pick<MarketplaceRecipeCard, 'ratingAverage' | 'ratingCount'>): RatingDisplay {
  if (card.ratingCount >= MIN_RATINGS_FOR_AVERAGE && card.ratingAverage !== null && Number.isFinite(card.ratingAverage)) {
    return { kind: 'average', average: card.ratingAverage, count: card.ratingCount };
  }
  return { kind: 'new' };
}

/** Languages are shown by name in the active language; an unknown code is shown as it is. */
export function languageName(code: string, locale: string): string {
  try {
    return new Intl.DisplayNames([locale], { type: 'language' }).of(code) ?? code;
  } catch {
    return code;
  }
}

/** Hover or keyboard focus plays the preview; touch devices and reduced motion never do. */
export function previewAllowed(env: { coarsePointer: boolean; reducedMotion: boolean }): boolean {
  return !env.coarsePointer && !env.reducedMotion;
}

const ERROR_KEYS: [string, string][] = [
  ['CONSENT_REQUIRED', 'marketplace.errors.consentRequired'],
  ['INVALID_FIELD', 'marketplace.errors.invalidField'],
  ['RECIPE_REMOVED', 'marketplace.errors.recipeRemoved'],
  ['INVALID_STATE', 'marketplace.errors.invalidState'],
  ['RECIPE_INVALID', 'marketplace.errors.recipeInvalid'],
  ['EXPRESSION_INVALID', 'marketplace.errors.expressionInvalid'],
  ['LOOP_BOUNDS', 'marketplace.errors.loopBounds'],
  ['PUBLISH_ATTEMPTS_LIMITED', 'marketplace.errors.attemptsLimited'],
  ['CONCURRENT_UPDATE', 'marketplace.errors.concurrentUpdate'],
  ['STALE_VERSION', 'marketplace.errors.staleVersion'],
];

/** The sentence for a failed marketplace call: this feature's own codes first, then the shared error catalogue. */
export function marketplaceErrorMessage(t: T, err: unknown): string {
  const code = typeof err === 'object' && err !== null ? (err as { code?: unknown }).code : null;
  if (code === 'RATING_NOT_ELIGIBLE') return t('marketplace.errors.ratingNotEligible');
  if (code === 'DAILY_PUBLISH_LIMIT') return t('marketplace.errors.dailyPublishLimit');
  if (code === 'LIVE_RECIPE_LIMIT') return t('marketplace.errors.liveRecipeLimit');
  if (code === 'TEXT_BLOCKED') return t('marketplace.errors.textBlocked');
  if (code === 'PREVIEW_REQUIRED') return t('marketplace.errors.previewRequired');
  for (const [c, key] of ERROR_KEYS) if (code === c) return t(key);
  if (code === 'CONSENT_VERSION_STALE') return t('marketplace.errors.consentStale');
  const status = typeof err === 'object' && err !== null ? (err as { status?: unknown }).status : null;
  if (status === 404) return t('marketplace.errors.notFound');
  return describeError(t, err);
}

/** The sign-in page that returns to `pathname` afterwards (the login flow carries `next`, with the '/app' base path). */
export function signInHref(pathname: string | null): string {
  const bare = pathname && pathname.startsWith('/') && !pathname.startsWith('//') ? pathname : '/community';
  return `/login?next=${encodeURIComponent(`/app${bare}`)}`;
}

/** The chip a recipe shows its author: preparing, waiting for review, live, or the end states. */
export function statusLabel(status: MarketplaceRecipeStatus, preview: MarketplacePreviewStatus | undefined): Msg {
  if (preview === 'failed' && (status === 'draft' || status === 'pending')) return msg('marketplace.status.previewFailed');
  switch (status) {
    case 'draft':
      return msg('marketplace.status.preparing');
    case 'pending':
      return msg('marketplace.status.pending');
    case 'listed':
      return msg('marketplace.status.live');
    case 'rejected':
      return msg('marketplace.status.rejected');
    case 'removed':
      return msg('marketplace.status.removed');
    case 'unpublished':
      return msg('marketplace.status.unpublished');
    default:
      return msg('marketplace.status.unknown');
  }
}

export const PREVIEW_STATUS_LABELS: Record<MarketplacePreviewStatus, Msg> = {
  preparing: msg('marketplace.status.previewPreparing'),
  ready: msg('marketplace.status.previewReady'),
  failed: msg('marketplace.status.previewFailed'),
};

export const REJECT_REASON_LABELS: Record<MarketplaceRejectReason, Msg> = {
  content: msg('marketplace.admin.reasons.content'),
  unsafe: msg('marketplace.admin.reasons.unsafe'),
  copyright: msg('marketplace.admin.reasons.copyright'),
  low_quality: msg('marketplace.admin.reasons.low_quality'),
  other: msg('marketplace.admin.reasons.other'),
};

/** The few languages a recipe can be labelled with in the publish dialog. */
export const PUBLISH_LANGUAGES = ['en', 'it'];

export const NAME_MAX = 80;
export const DESCRIPTION_MAX = 1000;

/** The name of a reason category the server sent (a category only). Unknown categories are shown as sent. */
export function reasonCategoryLabel(category: string): Msg | null {
  const map: Record<string, Msg> = { ...REJECT_REASON_LABELS, ...REPORT_REASON_LABELS };
  return map[category] ?? null;
}

const REFUSAL_KEYS: Record<string, string> = {
  CUSTOM_AGENT: 'marketplace.refusals.CUSTOM_AGENT',
  RUN_BOUND_SOURCE: 'marketplace.refusals.RUN_BOUND_SOURCE',
  STEP_TYPE_NOT_SHAREABLE: 'marketplace.refusals.STEP_TYPE_NOT_SHAREABLE',
  HIDDEN_WORKFLOW_KIND: 'marketplace.refusals.HIDDEN_WORKFLOW_KIND',
  TOO_LARGE: 'marketplace.refusals.TOO_LARGE',
  TEXT_BLOCKED: 'marketplace.refusals.TEXT_BLOCKED',
  PREVIEW_REQUIRED: 'marketplace.refusals.PREVIEW_REQUIRED',
  EXPRESSION_INVALID: 'marketplace.refusals.EXPRESSION_INVALID',
  LOOP_BOUNDS: 'marketplace.refusals.LOOP_BOUNDS',
};

/** A publish refusal in words of the active language; the server's own text only for a code this build does not know. */
export function refusalMessage(t: T, refusal: { code: string; message: string }): string {
  const key = REFUSAL_KEYS[refusal.code];
  return key ? t(key) : refusal.message;
}

const REMOVED_FIELD_KEYS: Record<string, string> = {
  providerId: 'marketplace.fields.providerId',
  voiceId: 'marketplace.fields.voiceId',
  fileId: 'marketplace.fields.fileId',
  projectFileId: 'marketplace.fields.fileId',
  agentId: 'marketplace.fields.agentId',
};

/** One "will be removed" line: a known field is named in words; an unknown one is shown as the server sent it. */
export function removedLine(t: T, r: { stepOrder: number; field: string; what: string }): string {
  const key = REMOVED_FIELD_KEYS[r.field];
  return key
    ? t('marketplace.publish.removedKnown', { order: r.stepOrder, field: t(key) })
    : t('marketplace.publish.removedItem', { order: r.stepOrder, field: r.field, what: r.what });
}

const AUTHOR_REASONS: Record<string, Msg> = {
  content: msg('marketplace.mine.reasons.content'),
  policy: msg('marketplace.mine.reasons.policy'),
  preview: msg('marketplace.mine.reasons.preview'),
  copyright: msg('marketplace.mine.reasons.copyright'),
  quality: msg('marketplace.mine.reasons.quality'),
  other: msg('marketplace.mine.reasons.other'),
};

/**
 * The sentence an author sees for a rejection or removal. Only the closed author-safe set is named; any other value
 * (a safety or audit code, anything unexpected) gets the generic policy wording and the raw string is never shown.
 */
export function authorReasonMessage(category: string): Msg {
  return Object.prototype.hasOwnProperty.call(AUTHOR_REASONS, category) ? AUTHOR_REASONS[category] : AUTHOR_REASONS.policy;
}
