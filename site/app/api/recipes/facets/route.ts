// Same-origin proxy for the public recipe API (docker build). The static export serves the twin under functions/api/recipes
// instead; both call proxyRecipes in lib/recipes/proxy.ts, so change one, change both. Read at request time, never prerendered.
import { proxyRecipes } from '@/lib/recipes/proxy';

export const dynamic = 'force-dynamic';

export async function GET(request: Request) {
  return (await proxyRecipes({ apiUrl: process.env.REELBOLT_API_URL, kind: 'facets', request })).response;
}
