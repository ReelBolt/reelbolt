'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { AgentDefinition, AgentModelChoice, OrgAgentOverride } from '../types/agent';

export function useAgents() {
  return useSWR<AgentDefinition[]>('/api/v1/agents', fetcher);
}

export function useAgent(id: string) {
  return useSWR<AgentDefinition>(id ? `/api/v1/agents/${id}` : null, fetcher);
}

/**
 * The active workspace's provider choices for built-in agent types (A17-UI). Owner/Admin only:
 * `GET /api/v1/org/agent-overrides` answers 403 for a Member or Billing caller, so callers must
 * gate the fetch on the role.
 */
export function useOrgAgentOverrides(enabled: boolean = true) {
  return useSWR<OrgAgentOverride[]>(enabled ? '/api/v1/org/agent-overrides' : null, fetcher);
}

/**
 * The recommended and effective model per built-in agent in the active workspace.
 * `GET /api/v1/org/agent-models` answers 403 for a Member or Billing caller, so gate the fetch on the role.
 */
export function useOrgAgentModels(enabled: boolean = true) {
  return useSWR<AgentModelChoice[]>(enabled ? '/api/v1/org/agent-models' : null, fetcher);
}
