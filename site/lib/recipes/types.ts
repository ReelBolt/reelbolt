// Public recipe DTOs (docs/design/recipe-marketplace.md, "Anonymous" routes) and the parsers that read an
// untrusted upstream body into them. Everything here is pure and never throws: anything malformed is dropped,
// so a junk response reads as "no recipes". Text fields (name, description, labels, prompts) are user text:
// they are kept as plain strings, with control characters removed, and callers render them as React text
// nodes only (never HTML, never markdown, never linkified).

export interface RecipeCard {
  id: string;
  name: string;
  description: string;
  category: string;
  language: string;
  costClass: string;
  stepCount: number;
  stepTypes: string[];
  authorLabel: string;
  copyCount: number;
  ratingAverage: number | null;
  ratingCount: number;
  previewSeconds: number;
  listedAt: string | null;
  version: number;
}

export interface RecipeStep {
  order: number;
  type: string;
  label: string;
  /** Short limits such as loop rounds or a spend cap. */
  limits: string[];
}

export interface RecipePrompt {
  stepOrder: number;
  field: string;
  text: string;
}

export interface RecipeSlot {
  kind: string;
  stepOrder: number;
  label: string;
}

export interface RecipeInput {
  key: string;
  label: string;
  kind: string;
}

export interface RecipeDetail extends RecipeCard {
  steps: RecipeStep[];
  prompts: RecipePrompt[];
  slots: RecipeSlot[];
  inputs: RecipeInput[];
  costNote: string;
}

export interface RecipeList {
  items: RecipeCard[];
  nextCursor: string | null;
}

export interface FacetEntry {
  key: string;
  count: number;
}

export interface RecipeFacets {
  costClasses: FacetEntry[];
  categories: FacetEntry[];
  stepTypes: FacetEntry[];
  languages: FacetEntry[];
}

/** The ratings needed before an average is shown anywhere. Never invented below it. */
export const MIN_RATINGS_FOR_AVERAGE = 5;

/** A recipe id is a random Guid. Anything else is not a recipe and is never sent upstream. */
const ID_PATTERN = /^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$/;

export function isRecipeId(value: unknown): value is string {
  return typeof value === 'string' && ID_PATTERN.test(value);
}

type Fields = Record<string, unknown>;

function isRecord(value: unknown): value is Fields {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// Control characters other than newline and tab (the server strips them too; this is the second line).
const CONTROL = new RegExp('[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F\\u007F-\\u009F\\u2028\\u2029]', 'g');

function text(value: unknown, max: number): string {
  if (typeof value !== 'string') return '';
  const clean = value.replace(CONTROL, '').replace(/\r\n?/g, '\n');
  return Array.from(clean).slice(0, max).join('');
}

function count(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? Math.floor(value) : 0;
}

function textList(value: unknown, max: number, items: number): string[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter((v): v is string => typeof v === 'string' && v.length > 0)
    .slice(0, items)
    .map((v) => text(v, max));
}

export function parseCard(raw: unknown): RecipeCard | null {
  if (!isRecord(raw) || !isRecipeId(raw.id)) return null;
  const name = text(raw.name, 200).trim();
  if (name === '') return null;
  const ratingCount = count(raw.ratingCount);
  const average = raw.ratingAverage;
  return {
    id: raw.id.toLowerCase(),
    name,
    description: text(raw.description, 2000),
    category: text(raw.category, 64),
    language: text(raw.language, 32),
    costClass: text(raw.costClass, 32),
    stepCount: count(raw.stepCount),
    stepTypes: textList(raw.stepTypes, 64, 40),
    authorLabel: text(raw.authorLabel, 120).trim(),
    copyCount: count(raw.copyCount),
    // An average is read only when it is a sane number AND there are enough ratings behind it.
    ratingAverage:
      typeof average === 'number' && Number.isFinite(average) && average >= 1 && average <= 5 &&
      ratingCount >= MIN_RATINGS_FOR_AVERAGE
        ? average
        : null,
    ratingCount,
    previewSeconds: count(raw.previewSeconds),
    listedAt: typeof raw.listedAt === 'string' && Number.isFinite(Date.parse(raw.listedAt)) ? raw.listedAt : null,
    version: count(raw.version),
  };
}

export function parseDetail(raw: unknown): RecipeDetail | null {
  const card = parseCard(raw);
  if (!card || !isRecord(raw)) return null;
  const list = (value: unknown): Fields[] => (Array.isArray(value) ? value.filter(isRecord).slice(0, 200) : []);
  return {
    ...card,
    steps: list(raw.steps).map((s) => ({ order: count(s.order), type: text(s.type, 64), label: text(s.label, 200), limits: textList(s.limits, 120, 10) })),
    prompts: list(raw.prompts)
      .map((p) => ({ stepOrder: count(p.stepOrder), field: text(p.field, 64), text: text(p.text, 4000) }))
      .filter((p) => p.text.trim() !== ''),
    slots: list(raw.slots).map((s) => ({ kind: text(s.kind, 32), stepOrder: count(s.stepOrder), label: text(s.label, 200) })),
    inputs: list(raw.inputs).map((i) => ({ key: text(i.key, 64), label: text(i.label, 200), kind: text(i.kind, 32) })),
    costNote: text(raw.costNote, 500),
  };
}

export function parseList(raw: unknown): RecipeList {
  if (!isRecord(raw) || !Array.isArray(raw.items)) return { items: [], nextCursor: null };
  const items = raw.items.map(parseCard).filter((c): c is RecipeCard => c !== null);
  const cursor = typeof raw.nextCursor === 'string' && raw.nextCursor.length > 0 && raw.nextCursor.length <= 512
    ? raw.nextCursor
    : null;
  return { items, nextCursor: cursor };
}

function facetList(value: unknown): FacetEntry[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter(isRecord)
    .slice(0, 100)
    .map((f) => ({ key: text(f.key, 64), count: count(f.count) }))
    .filter((f) => f.key !== '');
}

export function parseFacets(raw: unknown): RecipeFacets {
  const empty: RecipeFacets = { costClasses: [], categories: [], stepTypes: [], languages: [] };
  if (!isRecord(raw)) return empty;
  // `languages` may be plain keys or {key,count}; accept both.
  const languages = Array.isArray(raw.languages)
    ? raw.languages.map((l) => (typeof l === 'string' ? { key: l, count: 0 } : l))
    : [];
  return {
    costClasses: facetList(raw.costClasses),
    categories: facetList(raw.categories),
    stepTypes: facetList(raw.stepTypes),
    languages: facetList(languages),
  };
}

/** "GenerateImage" / "generate_image" to "Generate image": a readable label for a machine key. */
export function humanizeKey(key: string): string {
  const spaced = key
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim()
    .toLowerCase();
  return spaced === '' ? key : spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** Whole seconds as a short clock, e.g. 75 -> "1:15". */
export function formatSeconds(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

const COST_KEYS: Record<string, string> = {
  'no credits': 'none',
  'uses credits': 'credits',
  'uses credits and a voice': 'creditsVoice',
  'uses credits and ai shots': 'creditsShots',
};

/** The catalogue key for an API cost class word, or null for one we do not know. */
export function costKey(costClass: string): string | null {
  return COST_KEYS[costClass.trim().toLowerCase()] ?? null;
}
