// Server-side reads of the public recipe API, for the build (prerendered gallery, detail pages, sitemap) and
// for on-demand rendering in the Node container. Every function swallows failure and returns an empty
// answer: a missing REELBOLT_API_URL or an erroring API must never fail a build or a page render. The
// backend is read at BUILD time for the static export, so REELBOLT_API_URL has to be set when it is built.
import { upstreamUrl, type FetchLike } from './proxy.ts';
import {
  isRecipeId,
  parseDetail,
  parseFacets,
  parseList,
  type RecipeCard,
  type RecipeDetail,
  type RecipeFacets,
  type RecipeList,
} from './types.ts';

/** Most recipes the build prerenders, listed in the sitemap and given a static detail page. */
export const BUILD_RECIPE_CAP = 500;
const PAGE = 50;
const TIMEOUT_MS = 8000;

const defaultFetch: FetchLike = (url, init) =>
  fetch(url, { ...init, next: { revalidate: 60 } } as RequestInit);

async function getJson(url: string | null, fetchImpl: FetchLike): Promise<unknown> {
  if (url === null) return null;
  try {
    const res = await fetchImpl(url, {
      headers: { accept: 'application/json' },
      signal: AbortSignal.timeout(TIMEOUT_MS),
      credentials: 'omit',
    } as RequestInit);
    return res.status === 200 ? await res.json() : null;
  } catch {
    return null;
  }
}

export async function fetchRecipeList(
  apiUrl: string | undefined,
  query: Record<string, string> = {},
  fetchImpl: FetchLike = defaultFetch,
): Promise<RecipeList> {
  const url = upstreamUrl(apiUrl, 'list', undefined, new URLSearchParams(query));
  return parseList(await getJson(url, fetchImpl));
}

export async function fetchRecipeFacets(apiUrl: string | undefined, fetchImpl: FetchLike = defaultFetch): Promise<RecipeFacets> {
  return parseFacets(await getJson(upstreamUrl(apiUrl, 'facets'), fetchImpl));
}

export async function fetchRecipeDetail(
  apiUrl: string | undefined,
  id: string,
  fetchImpl: FetchLike = defaultFetch,
): Promise<RecipeDetail | null> {
  if (!isRecipeId(id)) return null;
  return parseDetail(await getJson(upstreamUrl(apiUrl, 'detail', id), fetchImpl));
}

/** Every listed recipe up to `cap`, following the cursor. Empty when the API is unset or failing. */
export async function fetchAllListedRecipes(
  apiUrl: string | undefined,
  cap = BUILD_RECIPE_CAP,
  fetchImpl: FetchLike = defaultFetch,
): Promise<RecipeCard[]> {
  const all: RecipeCard[] = [];
  const seen = new Set<string>();
  let cursor: string | null = null;
  for (let page = 0; page < Math.ceil(cap / PAGE) + 1 && all.length < cap; page += 1) {
    const query: Record<string, string> = { limit: String(PAGE), sort: 'new' };
    if (cursor) query.cursor = cursor;
    const result: RecipeList = await fetchRecipeList(apiUrl, query, fetchImpl);
    for (const card of result.items) {
      if (!seen.has(card.id) && all.length < cap) {
        seen.add(card.id);
        all.push(card);
      }
    }
    if (!result.nextCursor || result.items.length === 0) break;
    cursor = result.nextCursor;
  }
  return all;
}
