import { fetchRecipeList } from '@/lib/recipes/server';
import { siteConfig } from '@/lib/site-config';
import { HOME_FAQS, PRICING_FAQS } from '@/lib/faqs';
import { runnerDownloadAvailable } from '@/lib/runner-downloads';
import { listedPosts } from '@/lib/blog/posts';
import { currency, plans, taxNote } from '@/lib/pricing';
import catalog from '@/lib/pricing/plans.catalog.json';
import { buildLlmsFullTxt, type LlmsInput } from '@/lib/llms';

// The long form of `/llms.txt`: the same summary, plans and FAQ, plus every blog post's title,
// URL and description. Gathers the same inputs as `app/llms.txt/route.ts`; the text is built by
// `lib/llms.ts`, so the two cannot disagree about a price, a link or a FAQ answer.
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
    runnerAvailable: runnerDownloadAvailable(),
    runnerPath: siteConfig.runnerPath,
  };
}

export async function GET() {
  // Listed only when the gallery has something to show, as in the sitemap.
  const recipesAvailable = (await fetchRecipeList(process.env.REELBOLT_API_URL, { limit: '1' })).items.length > 0;
  return new Response(buildLlmsFullTxt({ ...input(), recipesAvailable }), {
    headers: {
      'Content-Type': 'text/plain; charset=utf-8',
      'Cache-Control': 'public, max-age=3600, s-maxage=3600',
    },
  });
}
