/**
 * Whether the workflow app view (the project's app gallery and the app run page) is available.
 *
 * Runtime, read from the server environment per request, exactly like `lib/editor/config.ts`
 * (`VIDEO_EDITOR_ENABLED`): a `NEXT_PUBLIC_*` value would be baked into the image at build time, so
 * a plain `process.env` read keeps turning the app view off a container restart, not a rebuild.
 *
 * Defaults to **true**. `WORKFLOW_APPS_ENABLED=false` falls back to today's Workflows tab and
 * workflow detail page, unchanged. It is a surface switch, never an authorization decision: the
 * `workflow-apps` endpoints stay reachable to an authenticated caller.
 */
export function workflowAppsEnabled(env: Record<string, string | undefined> = process.env): boolean {
  return env.WORKFLOW_APPS_ENABLED !== 'false';
}
