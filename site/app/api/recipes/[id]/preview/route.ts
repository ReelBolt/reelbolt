// Same-origin proxy for the public recipe API (docker build). The static export serves the twin under functions/api/recipes
// instead; both call proxyRecipes in lib/recipes/proxy.ts, so change one, change both. Read at request time, never prerendered.
import { isRecipeId } from '@/lib/recipes/types';
import { bareNotFound, proxyRecipes } from '@/lib/recipes/proxy';

export const dynamic = 'force-dynamic';

export async function GET(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isRecipeId(id)) return bareNotFound();
  return (await proxyRecipes({ apiUrl: process.env.REELBOLT_API_URL, kind: 'preview', id, request })).response;
}
