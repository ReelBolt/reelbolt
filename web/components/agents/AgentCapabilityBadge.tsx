'use client';

import { Badge, Group, Tooltip } from '@mantine/core';
import { IconTools, IconFileExport } from '@tabler/icons-react';
import type { AgentDefinition } from '@/lib/types/agent';
import { useT } from '@/i18n';

export function AgentCapabilityBadge({ agent }: { agent: AgentDefinition }) {
  const t = useT('agents.cap');
  const toolCount = agent.availableTools?.length ?? 0;

  return (
    <Group gap={6}>
      {toolCount > 0 && (
        <Tooltip
          label={agent.availableTools!.join(', ')}
          multiline
          maw={320}
          withArrow
        >
          <Badge
            color="blue"
            variant="outline"
            size="sm"
            leftSection={<IconTools size={10} />}
          >
            {t('tools', { count: toolCount })}
          </Badge>
        </Tooltip>
      )}
      {agent.generatesOutput && (
        <Tooltip
          label={agent.outputSchemaName ? t('schemaTip', { name: agent.outputSchemaName }) : t('structuredTip')}
          withArrow
        >
          <Badge
            color="green"
            variant="outline"
            size="sm"
            leftSection={<IconFileExport size={10} />}
          >
            {agent.outputSchemaName ?? t('output')}
          </Badge>
        </Tooltip>
      )}
    </Group>
  );
}
