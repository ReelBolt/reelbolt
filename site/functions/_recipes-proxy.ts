// Shared body of the Pages Functions under functions/api/recipes (the static export's twin of app/api/recipes).
// The shared logic is lib/recipes/proxy.ts; this adds only the Cache API for list, facets and detail (60 s, keyed
// on origin, path and the SANITISED query, so a junk query string cannot bypass it). Media is streamed, never
// cached here. Outside Workers (local tests) there is no cache and every request goes upstream.
import {
  methodNotAllowed,
  proxyRecipes,
  recipesCacheKey,
  type FetchLike,
  type RecipeKind,
} from '../lib/recipes/proxy.ts';

export interface RecipesEnv {
  // Public origin of the API. Unset: lists answer empty, everything else a bare 404.
  REELBOLT_API_URL?: string;
}

interface EdgeCache {
  match(request: Request): Promise<Response | undefined>;
  put(request: Request, response: Response): Promise<void>;
}

function edgeCache(): EdgeCache | null {
  return typeof caches !== 'undefined' ? (caches as unknown as { default: EdgeCache }).default : null;
}

export async function handleRecipes(
  kind: RecipeKind,
  request: Request,
  env: RecipesEnv,
  id?: string,
  fetchImpl?: FetchLike,
): Promise<Response> {
  if (request.method !== 'GET' && request.method !== 'HEAD') return methodNotAllowed();
  const cacheable = kind === 'list' || kind === 'facets' || kind === 'detail';
  const cache = cacheable && request.method === 'GET' ? edgeCache() : null;
  const key = new Request(recipesCacheKey(request.url, kind), { method: 'GET' });
  if (cache) {
    const hit = await cache.match(key).catch(() => undefined);
    if (hit) return hit;
  }
  const { response, cacheable: worthCaching } = await proxyRecipes({
    apiUrl: env.REELBOLT_API_URL,
    kind,
    id,
    request,
    fetchImpl,
  });
  if (cache && worthCaching) await cache.put(key, response.clone()).catch(() => undefined);
  return response;
}
