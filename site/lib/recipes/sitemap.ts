// The recipe entries of the sitemap, pure so a test can pin them. `lastModified` is the recipe's own listing
// date (never the build time); a recipe without one is left for the caller's default.
import type { RecipeCard } from './types.ts';

export interface RecipeSitemapEntry {
  path: string;
  changeFrequency: 'daily' | 'weekly';
  priority: number;
  lastModified?: Date;
}

/** Empty when nothing was listed at build time: the gallery is then not submitted either (nothing to index). */
export function recipeSitemapEntries(recipes: readonly Pick<RecipeCard, 'id' | 'listedAt'>[]): RecipeSitemapEntry[] {
  if (recipes.length === 0) return [];
  return [
    { path: '/recipes', changeFrequency: 'daily', priority: 0.8 },
    ...recipes.map((r) => ({
      path: `/recipes/${r.id}`,
      changeFrequency: 'weekly' as const,
      priority: 0.6,
      ...(r.listedAt ? { lastModified: new Date(r.listedAt) } : {}),
    })),
  ];
}
