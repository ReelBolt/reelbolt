import { createTranslator } from 'use-intl/core';
import enWorkflows from '@/messages/en/workflows.json';

/**
 * English translator for the few pure (non-hook) helpers whose exported API is plain strings
 * (consumed by lib hooks and pinned by tests). Components pass their own `t` to get the active language.
 */
export function englishWorkflowsT(namespace: string): (key: string, values?: Record<string, string | number>) => string {
  const t = createTranslator({ locale: 'en', messages: { workflows: enWorkflows } as never, namespace: `workflows.${namespace}` as never });
  return (key, values) => (t as unknown as (k: string, v?: Record<string, string | number>) => string)(key, values);
}
