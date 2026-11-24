import { timeAgo } from '@/lib/files/model';
import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import type {
  AppCategory,
  AppCostClass,
  RecipeNeedAction,
  WorkflowAppLastRun,
  WorkflowRecipeCard,
} from '@/lib/types/workflow';

export type CategoryFilter = 'all' | AppCategory;

export const CATEGORY_FILTERS: { value: CategoryFilter; label: Msg }[] = [
  { value: 'all', label: msg('apps.gallery.categories.all') },
  { value: 'promo', label: msg('apps.gallery.categories.promo') },
  { value: 'footage', label: msg('apps.gallery.categories.footage') },
  { value: 'brand', label: msg('apps.gallery.categories.brand') },
  { value: 'utility', label: msg('apps.gallery.categories.utility') },
];

export interface CostBadge {
  label: Msg;
  /** `amber` marks the classes that disclose real, capped spend. Never the only signal: the words carry it. */
  tone: 'plain' | 'amber';
  hint: Msg | null;
}

const COST_BADGES: Record<AppCostClass, CostBadge> = {
  'No credits': {
    label: msg('apps.cost.noCredits.label'),
    tone: 'plain',
    hint: msg('apps.cost.noCredits.hint'),
  },
  'Uses credits': { label: msg('apps.cost.credits.label'), tone: 'plain', hint: msg('apps.cost.credits.hint') },
  'Uses credits and a voice': {
    label: msg('apps.cost.creditsVoice.label'),
    tone: 'plain',
    hint: msg('apps.cost.creditsVoice.hint'),
  },
  'Uses credits and AI shots': {
    label: msg('apps.cost.creditsShots.label'),
    tone: 'amber',
    hint: msg('apps.cost.creditsShots.hint'),
  },
};

export function costBadgeFor(costClass: AppCostClass): CostBadge {
  return COST_BADGES[costClass] ?? { label: msg('apps.cost.other', { label: costClass }), tone: 'plain', hint: null };
}

export type RunStatusKind = 'finished' | 'failed' | 'running' | 'stopped' | 'never';

export interface RunStatusLine {
  kind: RunStatusKind;
  /** The word; always shown next to the icon. */
  word: Msg;
  /** "2h ago" when the run finished and said when. */
  when: string | null;
}

export function runStatusFor(lastRun: WorkflowAppLastRun | null, t?: T, locale?: string): RunStatusLine {
  if (!lastRun) return { kind: 'never', word: msg('apps.gallery.status.never'), when: null };
  const when = lastRun.finishedAt ? timeAgo(lastRun.finishedAt, Date.now(), t, locale) : null;
  switch (lastRun.status) {
    case 'Passed':
      return { kind: 'finished', word: msg('apps.gallery.status.finished'), when };
    case 'Failed':
      return { kind: 'failed', word: msg('apps.gallery.status.failed'), when };
    case 'Queued':
    case 'Running':
      return { kind: 'running', word: msg('apps.gallery.status.running'), when: null };
    default:
      return { kind: 'stopped', word: msg('apps.gallery.status.stopped'), when };
  }
}

/** Title and tagline, case-insensitive: the gallery's one search box. */
export function matchesSearch(card: { title: string; tagline: string | null }, query: string): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return card.title.toLowerCase().includes(q) || (card.tagline ?? '').toLowerCase().includes(q);
}

export function matchesCategory(card: { category: AppCategory }, filter: CategoryFilter): boolean {
  return filter === 'all' || card.category === filter;
}

export function filterCards<T extends { title: string; tagline: string | null; category: AppCategory }>(
  cards: T[],
  query: string,
  filter: CategoryFilter,
): T[] {
  return cards.filter((c) => matchesCategory(c, filter) && matchesSearch(c, query));
}

/** The plans section of the billing page: where a plan is compared and changed. */
export const PLANS_PATH = '/settings/billing#plans';

/** Where the fix for a missing thing lives: the action link on a recipe's `Needs` line. */
export function needsHref(projectId: string, action: RecipeNeedAction): string | null {
  switch (action) {
    case 'add-files':
      return `/projects/${projectId}?tab=files`;
    case 'build-profile':
      return `/projects/${projectId}?tab=profile`;
    case 'connect-provider':
      return '/org/inference-providers';
    case 'upgrade-plan':
      // "X is not included in your plan": the plans are where that changes.
      return PLANS_PATH;
    default:
      // A newer backend's action this build does not know: no link, the caller shows plain text.
      return null;
  }
}

/** How many recipes the empty-project state leads with. */
export const EMPTY_STATE_RECIPES = 3;

/** What the project's files point to: a code archive suggests promo recipes, video clips footage recipes. */
export interface ProjectFileKinds {
  code: boolean;
  video: boolean;
}

const CODE_FILE = /\.(js|jsx|ts|tsx|py|rb|go|java|cs|php|html|css|vue|svelte)$/i;

export function fileKindsOf(files: readonly { originalFileName: string; mimeType: string }[] | undefined | null): ProjectFileKinds {
  const list = files ?? [];
  return {
    code: list.some((f) => CODE_FILE.test(f.originalFileName)),
    video: list.some((f) => (f.mimeType ?? '').startsWith('video/')),
  };
}

/**
 * The kinds the ranking goes by. The files decide when they say anything. When they hold no code,
 * the person's own answer to "what do you have" stands in: someone who starts from footage, or from
 * an idea they will film, is led to the footage recipes before a single clip is uploaded, so the
 * code-based promo recipes never lead a project that will hold no code. A project with code keeps
 * its promo-first order whatever the answer was, and `code` (or no answer) changes nothing.
 */
export function rankingKinds(kinds: ProjectFileKinds, material: string | null | undefined): ProjectFileKinds {
  const startsFromFootage = material === 'footage' || material === 'idea';
  return { code: kinds.code, video: kinds.video || (!kinds.code && startsFromFootage) };
}

/**
 * The recipes an empty project leads with (spec 4.2): those whose category matches the files present
 * come first (code then promo, video clips then footage), then readiness (fewest things missing),
 * catalog order within.
 */
export function suggestedRecipes(
  recipes: WorkflowRecipeCard[],
  count = EMPTY_STATE_RECIPES,
  kinds: ProjectFileKinds = { code: false, video: false },
): WorkflowRecipeCard[] {
  const matches = (r: WorkflowRecipeCard) =>
    (kinds.code && r.category === 'promo') || (kinds.video && r.category === 'footage') ? 0 : 1;
  return recipes
    .map((r, i) => ({ r, i }))
    .sort((a, b) => matches(a.r) - matches(b.r) || a.r.missing.length - b.r.missing.length || a.i - b.i)
    .slice(0, count)
    .map(({ r }) => r);
}

/**
 * The recipe list for a project that already has apps: recipes the project is ready for come first,
 * and a project with video clips and no code lists Footage before Promo. Catalog order breaks ties, so
 * a project with code or no files keeps the Promo-first order.
 */
export function rankRecipes(recipes: WorkflowRecipeCard[], kinds: ProjectFileKinds): WorkflowRecipeCard[] {
  const footageFirst = kinds.video && !kinds.code;
  const categoryRank = (r: WorkflowRecipeCard) => (footageFirst ? (r.category === 'footage' ? 0 : r.category === 'promo' ? 2 : 1) : 0);
  return recipes
    .map((r, i) => ({ r, i }))
    .sort((a, b) => Number(a.r.fit !== 'ready') - Number(b.r.fit !== 'ready') || categoryRank(a.r) - categoryRank(b.r) || a.i - b.i)
    .map(({ r }) => r);
}

/** An app made for the project that nobody has run yet, in the promo category (the code-based one). */
export function isUnrunPromoApp(app: { category: string; lastRun: unknown }): boolean {
  return app.category === 'promo' && !app.lastRun;
}
