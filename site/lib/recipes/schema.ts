// JSON-LD for the recipe gallery and detail pages, from REAL fields only: no rating markup at all (the site bans
// aggregateRating, docs/marketing-site.md), no prices. The star average is visible page text only.
// Pure (no imports from the app) so a test can pin the output. `origin` is the site origin; `pathFor` maps a
// site path to the page's localized path.
import type { RecipeCard, RecipeDetail } from './types.ts';

type PathFor = (path: string) => string;

const abs = (origin: string, path: string) => new URL(path, origin).toString();

/** ISO 8601 duration for whole seconds, e.g. 75 -> "PT1M15S". */
export function isoDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  const m = Math.floor(s / 60);
  const rest = s % 60;
  return `PT${m > 0 ? `${m}M` : ''}${rest > 0 || m === 0 ? `${rest}S` : ''}`;
}

export function recipeGallerySchema(
  items: readonly RecipeCard[],
  opts: { origin: string; name: string; description: string; pathFor: PathFor },
) {
  const { origin, pathFor } = opts;
  return {
    '@context': 'https://schema.org',
    '@type': 'CollectionPage',
    name: opts.name,
    description: opts.description,
    url: abs(origin, pathFor('/recipes')),
    mainEntity: {
      '@type': 'ItemList',
      numberOfItems: items.length,
      itemListElement: items.map((item, index) => ({
        '@type': 'ListItem',
        position: index + 1,
        url: abs(origin, pathFor(`/recipes/${item.id}`)),
        name: item.name,
      })),
    },
  };
}

export function recipeDetailSchema(recipe: RecipeDetail, opts: { origin: string; pathFor: PathFor; inLanguage?: string }) {
  const { origin, pathFor } = opts;
  const poster = abs(origin, `/api/recipes/${recipe.id}/poster`);
  const video =
    recipe.previewSeconds > 0
      ? {
          '@type': 'VideoObject',
          name: recipe.name,
          description: recipe.description || recipe.name,
          thumbnailUrl: poster,
          contentUrl: abs(origin, `/api/recipes/${recipe.id}/preview`),
          duration: isoDuration(recipe.previewSeconds),
          ...(recipe.listedAt ? { uploadDate: recipe.listedAt } : {}),
        }
      : null;
  return {
    '@context': 'https://schema.org',
    '@type': 'CreativeWork',
    name: recipe.name,
    description: recipe.description || recipe.name,
    url: abs(origin, pathFor(`/recipes/${recipe.id}`)),
    ...(recipe.authorLabel ? { creator: { '@type': 'Person', name: recipe.authorLabel } } : {}),
    ...(recipe.listedAt ? { datePublished: recipe.listedAt } : {}),
    image: poster,
    ...(video ? { video } : {}),
  };
}
