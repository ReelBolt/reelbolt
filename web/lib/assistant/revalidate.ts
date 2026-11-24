import type { AssistantActionKind } from '../types/assistant';

/**
 * Which cached lists and records an assistant change can have made out of date.
 *
 * Projects come first and always: a project's own record, its files, its workflows and its runs all
 * live under `/api/v1/projects`, which is what nearly every action touches. The rest are the few
 * kinds of change that reach beyond a project.
 */
const EXTRA_PREFIXES: Partial<Record<AssistantActionKind, readonly string[]>> = {
  run: ['/api/v1/billing', '/api/v1/dashboard'],
  style: ['/api/v1/styles'],
  agent: ['/api/v1/agents', '/api/v1/org/agent-overrides', '/api/v1/org/agent-models', '/api/v1/skills'],
  connection: ['/api/v1/inference-providers', '/api/v1/org/inference-providers'],
};

const ALWAYS: readonly string[] = ['/api/v1/projects'];

/** The URL prefixes to revalidate once an action of this kind is done. */
export function revalidationPrefixes(kind: AssistantActionKind): readonly string[] {
  return [...ALWAYS, ...(EXTRA_PREFIXES[kind] ?? [])];
}

/**
 * An SWR key filter for `mutate(filter)`. Only string keys are ever considered — a tuple key belongs
 * to something else — and a key matches on its path, so `/api/v1/projects` also reaches
 * `/api/v1/projects/{id}/files?…` but never `/api/v1/projectsx`.
 */
export function revalidationFilter(kind: AssistantActionKind): (key: unknown) => boolean {
  const prefixes = revalidationPrefixes(kind);
  return (key) => {
    if (typeof key !== 'string') return false;
    return prefixes.some((prefix) => key === prefix || key.startsWith(`${prefix}/`) || key.startsWith(`${prefix}?`));
  };
}
