/**
 * Whether the public API surface (the workspace "API keys" page) is available.
 *
 * Runtime, read from the server environment per request, exactly like `lib/workflow-apps/config.ts`:
 * a `NEXT_PUBLIC_*` value would be baked into the image at build time, so a plain `process.env`
 * read keeps turning the page off from a container restart, not a rebuild.
 *
 * Defaults to **false** (unlike the workflow app view): the public API is an opt-in surface. It is
 * only a surface switch, never an authorization decision — the server refuses every call itself
 * (`PUBLIC_API_ENABLED=false` answers 404), and an enabled page still requires Owner or Admin.
 */
export function publicApiEnabled(env: Record<string, string | undefined> = process.env): boolean {
  return env.PUBLIC_API_ENABLED === 'true';
}
