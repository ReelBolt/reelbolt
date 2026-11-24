/**
 * The brand fonts the web can preview: the OFL families pinned in sandbox/fonts, converted to Latin
 * WOFF2 subsets under public/fonts/brand and self-hosted (never a CDN). Loading is lazy and only
 * ever triggered by a component that shows a font preview, never at app load. The CSS family names
 * are neutral aliases so the dashboard's own Inter is not touched.
 */

/** Served under the Next basePath (`/app`). */
export const BRAND_FONT_BASE_URL = '/app/fonts/brand';

/**
 * CSS aliases are neutral ("ReelBolt Brand Font N", N = position in ENTRIES), never the family name:
 * these are modified (subset, re-encoded) copies and an OFL Reserved Font Name must not be reused for one.
 */
const FAMILY_PREFIX = 'ReelBolt Brand Font';

interface BrandFontFace {
  file: string;
  /** A CSS font-weight descriptor: one weight for a static face, "min max" for a variable one. */
  weight: string;
}

interface BrandFontEntry {
  family: string;
  faces: readonly BrandFontFace[];
}

function variable(family: string, file: string, range: string): BrandFontEntry {
  return { family, faces: [{ file, weight: range }] };
}

function staticFamily(family: string, prefix: string, weights: readonly [string, number][]): BrandFontEntry {
  return { family, faces: weights.map(([name, weight]) => ({ file: `${prefix}-${name}.woff2`, weight: String(weight) })) };
}

const REGULAR_SEMI_BOLD: readonly [string, number][] = [['Regular', 400], ['SemiBold', 600], ['Bold', 700]];

const ENTRIES: readonly BrandFontEntry[] = [
  variable('Inter', 'Inter-Variable.woff2', '100 900'),
  variable('Montserrat', 'Montserrat-Variable.woff2', '100 900'),
  variable('DM Sans', 'DMSans-Variable.woff2', '100 1000'),
  variable('Space Grotesk', 'SpaceGrotesk-Variable.woff2', '300 700'),
  variable('JetBrains Mono', 'JetBrainsMono-Variable.woff2', '100 800'),
  variable('Playfair Display', 'PlayfairDisplay-Variable.woff2', '400 900'),
  staticFamily('Poppins', 'Poppins', REGULAR_SEMI_BOLD),
  staticFamily('Chakra Petch', 'ChakraPetch', REGULAR_SEMI_BOLD),
  staticFamily('Anton', 'Anton', [['Regular', 400]]),
  staticFamily('Bebas Neue', 'BebasNeue', [['Regular', 400]]),
  staticFamily('DM Serif Display', 'DMSerifDisplay', [['Regular', 400]]),
];

const BY_NAME: ReadonlyMap<string, BrandFontEntry> = new Map(ENTRIES.map((entry) => [entry.family.toLowerCase(), entry]));

const GENERIC_KEYWORDS: ReadonlySet<string> = new Set([
  'serif', 'sans-serif', 'monospace', 'cursive', 'fantasy', 'system-ui', 'ui-serif', 'ui-sans-serif',
  'ui-monospace', 'ui-rounded', 'math', 'emoji', 'fangsong',
]);

export type BrandFontResolution =
  | { kind: 'available'; family: string; cssFamily: string }
  | { kind: 'generic'; keyword: string }
  | { kind: 'unavailable'; name: string };

/** The families we can preview, in catalog order. */
export function brandFontFamilies(): string[] {
  return ENTRIES.map((entry) => entry.family);
}

const ALIAS_BY_FAMILY: ReadonlyMap<string, string> = new Map(
  ENTRIES.map((entry, index) => [entry.family.toLowerCase(), `${FAMILY_PREFIX} ${index + 1}`]),
);

/** The neutral CSS family a catalog family is declared under; null for a family we do not ship. */
export function brandCssFamily(family: string): string | null {
  return ALIAS_BY_FAMILY.get(family.toLowerCase()) ?? null;
}

/** Splits a CSS font stack on commas outside quotes, unquoting each name. */
export function parseFontStack(stack: string): string[] {
  const names: string[] = [];
  let current = '';
  let quote: string | null = null;
  for (const char of stack) {
    if (quote) {
      if (char === quote) quote = null;
      else current += char;
    } else if (char === '"' || char === "'") {
      quote = char;
    } else if (char === ',') {
      names.push(current);
      current = '';
    } else {
      current += char;
    }
  }
  names.push(current);
  return names.map((name) => name.trim().replace(/\s+/g, ' ')).filter((name) => name.length > 0);
}

/**
 * Resolves a fontFamily token value ("Inter", "'Chakra Petch', sans-serif", "sans-serif"). The
 * first catalog family in the stack wins; a stack that starts with a generic keyword is `generic`;
 * anything else is `unavailable`.
 */
export function resolveBrandFont(stack: string): BrandFontResolution {
  const names = parseFontStack(stack);
  for (const name of names) {
    const entry = BY_NAME.get(name.toLowerCase());
    const cssFamily = entry ? brandCssFamily(entry.family) : null;
    if (entry && cssFamily) return { kind: 'available', family: entry.family, cssFamily };
  }
  const first = names[0] ?? stack.trim();
  if (GENERIC_KEYWORDS.has(first.toLowerCase())) return { kind: 'generic', keyword: first.toLowerCase() };
  return { kind: 'unavailable', name: first };
}

const loading = new Map<string, Promise<void>>();

/**
 * Registers the family's faces with `document.fonts` (self-hosted WOFF2). Idempotent per family;
 * resolves once loaded and never rejects (a failed load leaves the fallback font in place).
 */
export function loadBrandFont(family: string): Promise<void> {
  const entry = BY_NAME.get(family.toLowerCase());
  if (!entry || typeof document === 'undefined' || typeof FontFace === 'undefined') return Promise.resolve();
  const existing = loading.get(entry.family);
  if (existing) return existing;
  const css = brandCssFamily(entry.family);
  if (!css) return Promise.resolve();
  const promise = Promise.all(
    entry.faces.map(async (face) => {
      const fontFace = new FontFace(css, `url(${BRAND_FONT_BASE_URL}/${face.file}) format('woff2')`, {
        weight: face.weight,
        display: 'swap',
      });
      document.fonts.add(await fontFace.load());
    }),
  ).then(
    () => undefined,
    () => undefined,
  );
  loading.set(entry.family, promise);
  return promise;
}
