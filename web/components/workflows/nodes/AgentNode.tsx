'use client';

import { memo } from 'react';
import { IconRobot } from '@tabler/icons-react';
import { useAgents } from '@/lib/hooks/use-agents';
import { agentRoleLabel } from '@/lib/utils/agent-roles';
import { NoAudioFilesNotice } from '../config-kit';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

/**
 * An AI agent step. The card names what the agent DOES ("Writes the narration"), not the step
 * type: eleven cards all reading "AGENT" told a customer nothing. Settings (which agent, what it
 * sees, caching) live in the side drawer.
 */
export const AgentNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const { step, projectId } = data;
  const { data: agents } = useAgents();
  const selectedAgent = agents?.find((a) => a.id === step.agentDefinitionId);
  const agentType = selectedAgent?.agentType;
  const role = selectedAgent ? agentRoleLabel(agentType) : step.agentDefinitionId ? tn('agent.aiStep') : tn('agent.chooseAgent');

  return (
    <StepNodeShell
      data={data}
      accentHex="#8b5cf6"
      color="violet"
      icon={<IconRobot size={13} />}
      kind={tn('agent.aiStep')}
      role={role}
      detail={selectedAgent ? tn('agent.detail', { name: selectedAgent.name }) : null}
      warning={!step.agentDefinitionId ? tn('agent.noAgent') : null}
    >
      {agentType === 'MusicSupervisor' && <NoAudioFilesNotice projectId={projectId} kind="music" />}
      {agentType === 'SoundDesigner' && <NoAudioFilesNotice projectId={projectId} kind="sfx" />}
    </StepNodeShell>
  );
});

AgentNode.displayName = 'AgentNode';
