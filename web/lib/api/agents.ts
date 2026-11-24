import { apiFetch } from './client';
import type { AgentDefinition, AgentReasoningEffort, CreateAgentRequest, OrgAgentOverride, UpdateAgentRequest } from '../types/agent';

export function getAgents(): Promise<AgentDefinition[]> {
  return apiFetch<AgentDefinition[]>('/api/v1/agents');
}

export function getAgent(id: string): Promise<AgentDefinition> {
  return apiFetch<AgentDefinition>(`/api/v1/agents/${id}`);
}

export function createAgent(data: CreateAgentRequest): Promise<AgentDefinition> {
  return apiFetch<AgentDefinition>('/api/v1/agents', {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

export function updateAgent(id: string, data: UpdateAgentRequest): Promise<AgentDefinition> {
  return apiFetch<AgentDefinition>(`/api/v1/agents/${id}`, {
    method: 'PUT',
    body: JSON.stringify(data),
  });
}

export function deleteAgent(id: string): Promise<void> {
  return apiFetch<void>(`/api/v1/agents/${id}`, { method: 'DELETE' });
}

export function setAgentInferenceProvider(id: string, inferenceProviderId: string | null): Promise<AgentDefinition> {
  return apiFetch<AgentDefinition>(`/api/v1/agents/${id}/inference-provider`, {
    method: 'PUT',
    body: JSON.stringify({ inferenceProviderId }),
  });
}

/** Sets (or, with null, clears) how hard the agent's model thinks. Platform admin for a built-in; Owner/Admin for a custom agent. */
export function setAgentReasoningEffort(id: string, reasoningEffort: AgentReasoningEffort | null): Promise<AgentDefinition> {
  return apiFetch<AgentDefinition>(`/api/v1/agents/${id}/reasoning-effort`, {
    method: 'PUT',
    body: JSON.stringify({ reasoningEffort }),
  });
}

/** The active workspace's provider choices for BUILT-IN agent types. Owner/Admin only. */
export function getOrgAgentOverrides(): Promise<OrgAgentOverride[]> {
  return apiFetch<OrgAgentOverride[]>('/api/v1/org/agent-overrides');
}

/**
 * Sets the workspace's provider for one built-in agent type. The provider must be one the workspace
 * can actually resolve (an enabled platform row, or its own) and must be a Chat one — anything else
 * is a 404 or a 400, so callers offer only the selectable Chat rows.
 */
export function setOrgAgentOverride(agentType: string, inferenceProviderId: string): Promise<OrgAgentOverride> {
  return apiFetch<OrgAgentOverride>(`/api/v1/org/agent-overrides/${encodeURIComponent(agentType)}`, {
    method: 'PUT',
    body: JSON.stringify({ inferenceProviderId }),
  });
}

/** Clears it — idempotent, so clearing one that is not set is still success. */
export function clearOrgAgentOverride(agentType: string): Promise<void> {
  return apiFetch<void>(`/api/v1/org/agent-overrides/${encodeURIComponent(agentType)}`, {
    method: 'DELETE',
  });
}
