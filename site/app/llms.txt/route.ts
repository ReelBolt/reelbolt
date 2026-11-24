import { fetchRecipeList } from '@/lib/recipes/server';
import { siteConfig } from '@/lib/site-config';
import { HOME_FAQS, PRICING_FAQS } from '@/lib/faqs';
import { runnerDownloadAvailable } from '@/lib/runner-downloads';
import { listedPosts } from '@/lib/blog/posts';
import { currency, plans, taxNote } from '@/lib/pricing';
import catalog from '@/lib/pricing/plans.catalog.json';
import { buildLlmsTxt, type LlmsInput } from '@/lib/llms';

// `llms.txt` is the emerging convention for handing AI assistants a clean, plain-text summary of
// a site instead of making them infer it from rendered HTML. Served as a route rather than a
// static file in `public/` so the absolute URLs stay correct for whatever NEXT_PUBLIC_SITE_URL
// this deployment was built with, and so the FAQ answers can never drift from the ones rendered on
// the pages: both read the same `HOME_FAQS` / `PRICING_FAQS` arrays. The text itself is built by
// `lib/llms.ts`; this file only gathers the inputs. `llms-full.txt` gathers the same inputs.
export const dynamic = 'force-static';

function input(): LlmsInput {
  return {
    siteUrl: siteConfig.url,
    name: siteConfig.name,
    shortDescription: siteConfig.shortDescription,
    cta: siteConfig.primaryCta,
    plans,
    currency,
    taxNote,
    topUpExpiresAfterMonths: catalog.topUp?.expiresAfterMonths,
    posts: listedPosts().map(({ slug, title, description, tags }) => ({ slug, title, description, tags })),
    faqs: { home: HOME_FAQS, pricing: PRICING_FAQS },
    // `/runner` is listed only once the desktop runner has something to download.
    runnerAvailable: runnerDownloadAvailable(),
    runnerPath: siteConfig.runnerPath,
  };
}

export async function GET() {
  // Listed only when the gallery has something to show, as in the sitemap.
  const recipesAvailable = (await fetchRecipeList(process.env.REELBOLT_API_URL, { limit: '1' })).items.length > 0;
  return new Response(buildLlmsTxt({ ...input(), recipesAvailable }), {
    headers: {
      'Content-Type': 'text/plain; charset=utf-8',
      'Cache-Control': 'public, max-age=3600, s-maxage=3600',
    },
  });
}
