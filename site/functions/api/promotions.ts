// LEGACY, UNUSED: nothing deploys this Pages Function (reelbolt.ai is the `site` container behind nginx).
// Cloudflare Pages Function serving GET /api/promotions for the static export. A same-origin proxy
// for the billing API's anonymous promotions and plan prices, so the browser never calls the API
// directly. The docker build serves app/api/promotions/route.ts instead; both use loadPromotionsBody
// from lib/promotions/active.ts.
//
// The merged body is cached for a minute in the Cache API (caches.default), keyed on origin and path
// only (a query string cannot bypass it), so a burst of pricing views makes one pair of upstream calls.
// A partial answer (either upstream read failed) is served but never cached. Outside Workers (local tests) there is
// no cache and every request goes upstream.
import { loadPromotionsResult, promotionsCacheKey, PROMOTIONS_CACHE_CONTROL } from '../../lib/promotions/active';

interface Env {
  // Public origin of the API. Unset: the endpoint answers empty lists.
  REELBOLT_API_URL?: string;
}

interface EdgeCache {
  match(request: Request): Promise<Response | undefined>;
  put(request: Request, response: Response): Promise<void>;
}

/** The Workers default cache, or null where the Cache API does not exist. */
function edgeCache(): EdgeCache | null {
  return typeof caches !== 'undefined' ? (caches as unknown as { default: EdgeCache }).default : null;
}

export const onRequestGet = async ({ request, env }: { request: Request; env: Env }) => {
  const cache = edgeCache();
  const key = new Request(promotionsCacheKey(request.url), { method: 'GET' });
  if (cache) {
    const hit = await cache.match(key).catch(() => undefined);
    if (hit) return hit;
  }

  const { body, complete } = await loadPromotionsResult(env.REELBOLT_API_URL);
  const response = new Response(JSON.stringify(body), {
    headers: { 'content-type': 'application/json', 'cache-control': PROMOTIONS_CACHE_CONTROL },
  });
  if (cache && complete) await cache.put(key, response.clone()).catch(() => undefined);
  return response;
};
