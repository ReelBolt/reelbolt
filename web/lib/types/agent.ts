export interface AgentDefinition {
  id: string;
  name: string;
  description: string;
  agentType: string;
  systemPrompt: string;
  isBuiltIn: boolean;
  ownerId: string | null;
  createdAt: string;
  color: string | null;
  outputSchemaJson: string | null;
  availableTools: string[] | null;
  generatesOutput: boolean;
  outputSchemaName: string | null;
  inferenceProviderId: string | null;
  inferenceProviderName: string | null;
  /** Raw stored value — null/empty for built-ins always, may be set for custom agents. */
  assignedSkills: string[] | null;
  /** The resolved set to DISPLAY — the SkillCatalog default for built-ins, `assignedSkills` (or []) for custom agents. */
  effectiveSkills: string[];
  /** Stored thinking effort, or null/absent = "use the default" (configuration, then the agent's own). */
  reasoningEffort?: AgentReasoningEffort | null;
}

/** The accepted vocabulary — `high` is deliberately absent (the self-hosted Qwen template rejects it). */
export type AgentReasoningEffort = 'none' | 'low' | 'medium' | 'xhigh';

/**
 * One row of `GET /api/v1/org/agent-overrides` (A17-UI): the workspace's own Chat provider choice
 * for a BUILT-IN agent type. Built-in `agent_definitions` rows are shared by every workspace, so a
 * workspace's choice cannot live on them — it lives here, keyed (workspace, agent type).
 *
 * `inferenceProviderId` null means "no override"; the API deletes the row rather than storing a
 * null, so a listed row always carries an id.
 */
export interface OrgAgentOverride {
  agentType: string;
  inferenceProviderId: string | null;
  updatedAt: string;
}

/** Where an agent's effective model in a workspace comes from. */
export type AgentModelSource = 'workspace' | 'recommended' | 'default';

/** One row of `GET /api/v1/org/agent-models` (Owner/Admin): the recommendation and the effective model. */
export interface AgentModelChoice {
  agentType: string;
  recommendedModelId: string | null;
  recommendedModelName: string | null;
  /** One line on why the platform recommends it; null when there is none. */
  why: string | null;
  choiceModelId: string | null;
  effectiveModelId: string | null;
  effectiveModelName: string | null;
  source: AgentModelSource;
}

export interface CreateAgentRequest {
  name: string;
  description: string;
  systemPrompt: string;
  color?: string;
}

export interface UpdateAgentRequest {
  name?: string;
  description?: string;
  systemPrompt?: string;
  color?: string;
}
