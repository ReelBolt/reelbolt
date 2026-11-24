// The decision behind functions/_recipe-shell.ts, free of Pages-only imports so a test can run it: serve the
// prebuilt recipe page when the asset exists, else the client-rendered placeholder page (noindex).
export interface AssetsBinding {
  fetch(input: Request | string, init?: RequestInit): Promise<Response>;
}

export interface ShellContext {
  request: Request;
  env: { ASSETS?: AssetsBinding };
  next: () => Promise<Response>;
}

export interface LocaleRegistry {
  defaultLocale: string;
  codes: readonly string[];
}

export const PLACEHOLDER_ID = '_';

export async function recipeShellResponse(
  { request, env, next }: ShellContext,
  locale: string | undefined,
  registry: LocaleRegistry,
): Promise<Response> {
  // `/<anything>/recipes/<id>` also reaches this function; only a registered language prefix is ours.
  if (locale !== undefined && !registry.codes.includes(locale)) return next();
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    return new Response(null, { status: 405, headers: { allow: 'GET, HEAD' } });
  }
  const assets = env.ASSETS;
  if (!assets) return new Response(null, { status: 404 });

  const asset = await assets.fetch(request).catch(() => null);
  if (asset && asset.status !== 404) return asset;

  const url = new URL(request.url);
  const prefix = locale !== undefined && locale !== registry.defaultLocale ? `/${locale}` : '';
  const shell = await assets
    .fetch(new Request(new URL(`${prefix}/recipes/${PLACEHOLDER_ID}`, url), { method: 'GET' }))
    .catch(() => null);
  if (!shell || shell.status !== 200) return new Response(null, { status: 404 });
  const headers = new Headers(shell.headers);
  headers.set('x-robots-tag', 'noindex, nofollow');
  headers.set('cache-control', 'public, max-age=60');
  return new Response(request.method === 'HEAD' ? null : shell.body, { status: 200, headers });
}
