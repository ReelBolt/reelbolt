import type { MetadataRoute } from 'next';
import { siteConfig } from '@/lib/site-config';
import { runnerDownloadAvailable } from '@/lib/runner-downloads';
import { alternateLanguages, isTranslatedPath, locales, localizePath } from '@/i18n/config';
import { fetchAllListedRecipes } from '@/lib/recipes/server';
import { recipeSitemapEntries } from '@/lib/recipes/sitemap';
import { listedPosts, postsByTag, tagsWithPosts } from '@/lib/blog/posts';
import { AUDIENCE_PATHS } from '@/lib/audiences/registry';

interface SitemapEntry {
  path: string;
  /** Overrides the build-time stamp (a recipe's own listing date). */
  lastModified?: Date;
  changeFrequency: MetadataRoute.Sitemap[number]['changeFrequency'];
  priority: number;
}

// Only routes that are actually live belong here — a submitted sitemap that repeatedly 404s is a
// negative quality signal to crawlers, so don't pre-list a page ahead of the phase that ships it.
const routes: SitemapEntry[] = [
  { path: '/', changeFrequency: 'weekly', priority: 1.0 },
  { path: '/features', changeFrequency: 'monthly', priority: 0.9 },
  { path: '/examples', changeFrequency: 'monthly', priority: 0.7 },
  { path: '/pricing', changeFrequency: 'monthly', priority: 0.9 },
  // The six audience pages (`/for/<slug>`), from the one registry the routes are built from.
  ...AUDIENCE_PATHS.map((path) => ({ path, changeFrequency: 'monthly' as const, priority: 0.8 })),
  { path: '/about', changeFrequency: 'monthly', priority: 0.7 },
  { path: '/contact', changeFrequency: 'monthly', priority: 0.8 },
  { path: '/legal/privacy', changeFrequency: 'yearly', priority: 0.2 },
  { path: '/legal/terms', changeFrequency: 'yearly', priority: 0.2 },
  { path: '/legal/acceptable-use', changeFrequency: 'yearly', priority: 0.2 },
  { path: '/legal/refunds', changeFrequency: 'yearly', priority: 0.2 },
  { path: '/legal/runner-eula', changeFrequency: 'yearly', priority: 0.2 },
];

export const dynamic = 'force-static';

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  // One timestamp for the whole generation rather than per-route dates: every route here is
  // statically compiled into the same build, so a build is exactly when any of them could last
  // have changed. Inventing distinct per-page dates would be fiction, and crawlers discount
  // `lastmod` values they catch being wrong.
  const lastModified = new Date();

  // `/runner` is listed only once the desktop runner has something to download. The route answers
  // 200 either way, but until then it is a "not available yet" placeholder that is `noindex` and
  // links to nothing, and submitting that to a search engine is the same negative signal as
  // pre-listing a page ahead of its phase. The predicate is the same one the page itself uses, so
  // the two can never disagree. See `lib/runner-downloads` and decision D25.
  const base: SitemapEntry[] = runnerDownloadAvailable()
    ? [...routes, { path: siteConfig.runnerPath, changeFrequency: 'monthly', priority: 0.6 }]
    : routes;

  // The recipe gallery and every recipe listed when the site was built (cap 500). Like /runner, the gallery
  // is listed only once it has something in it: with no REELBOLT_API_URL at build time, or an API that errors
  // or lists nothing, there is nothing to submit. Recipes listed after the build are not here until the
  // next deploy. Each recipe carries its own listing date, never the build time.
  const recipes = await fetchAllListedRecipes(process.env.REELBOLT_API_URL);
  const entries: SitemapEntry[] = [...base, ...recipeSitemapEntries(recipes)];

  const abs = (path: string) => new URL(path, siteConfig.url).toString();
  return [
    // A translated page is listed once per language; every entry carries the full hreflang set
    // (x-default is English), so search engines pair the versions. English-only pages (legal,
    // blog) are listed once and carry none.
    ...entries.flatMap(({ path, changeFrequency, priority, lastModified: own }) => {
      if (!isTranslatedPath(path)) {
        return [{ url: abs(path), lastModified: own ?? lastModified, changeFrequency, priority }];
      }
      const languages = Object.fromEntries(
        Object.entries(alternateLanguages(path)).map(([lang, p]) => [lang, abs(p)]),
      );
      return locales.map((locale) => ({
        url: abs(localizePath(locale, path)),
        lastModified: own ?? lastModified,
        changeFrequency,
        priority,
        alternates: { languages },
      }));
    }),
    ...blogSitemapEntries(),
  ];
}

/**
 * Blog index, every post, and every tag page that has posts. Post dates come from the post itself
 * (`updated ?? date`), never the build time, so a crawler sees a change only when the post changed.
 */
function blogSitemapEntries(): MetadataRoute.Sitemap {
  const day = (value: string) => new Date(`${value}T00:00:00Z`);
  const posts = listedPosts(); // newest first
  const latestOf = (items: { date: string; updated?: string }[]) =>
    items.map((item) => item.updated ?? item.date).sort().at(-1) ?? posts[0]?.date ?? '1970-01-01';

  const url = (path: string) => new URL(path, siteConfig.url).toString();
  const entries: MetadataRoute.Sitemap = [
    {
      url: url('/blog'),
      lastModified: day(latestOf(posts)),
      changeFrequency: 'weekly',
      priority: 0.8,
    },
  ];
  for (const post of posts) {
    entries.push({
      url: url(`/blog/${post.slug}`),
      lastModified: day(post.updated ?? post.date),
      changeFrequency: 'monthly',
      priority: 0.6,
    });
  }
  for (const tag of tagsWithPosts()) {
    entries.push({
      url: url(`/blog/tag/${tag}`),
      lastModified: day(latestOf(postsByTag(tag))),
      changeFrequency: 'weekly',
      priority: 0.5,
    });
  }
  return entries;
}
