/**
 * The workflow page's "App | Advanced" view (docs/design/workflow-apps.md 4.5): the URL carries it as
 * `?view=advanced` (per workflow, shareable) and the browser remembers the last choice per user.
 * Pure helpers; the page owns the reads and writes so tests can drive them with a plain `window`.
 */
export type WorkflowView = 'app' | 'advanced';

export const WORKFLOW_VIEW_PARAM = 'view';

/** The view a `?view=` value names, or null for anything else (an unknown value never sticks). */
export function parseWorkflowView(value: string | null | undefined): WorkflowView | null {
  return value === 'advanced' || value === 'app' ? value : null;
}

/** localStorage key of the per-user choice. `userKey` is the `userKeyFromEmail` output, never the raw email. */
export function workflowViewStorageKey(userKey: string | null | undefined): string {
  return `reelbolt.workflowView.${(userKey || 'anonymous').toLowerCase()}`;
}

/**
 * The view to open with: the URL wins, then the remembered choice, then `fallback`.
 * `fallback` is the app: a bare URL means the app (spec 4.3, 4.6).
 */
export function resolveWorkflowView(
  search: string,
  remembered: string | null | undefined,
  fallback: WorkflowView = 'app',
): WorkflowView {
  return (
    parseWorkflowView(new URLSearchParams(search).get(WORKFLOW_VIEW_PARAM)) ??
    parseWorkflowView(remembered) ??
    fallback
  );
}

/** `search` with the view spelled out (a shared link means the same view for someone with another memory). Other params are kept. */
export function withWorkflowView(search: string, view: WorkflowView): string {
  const params = new URLSearchParams(search);
  params.set(WORKFLOW_VIEW_PARAM, view);
  const next = params.toString();
  return next ? `?${next}` : '';
}
