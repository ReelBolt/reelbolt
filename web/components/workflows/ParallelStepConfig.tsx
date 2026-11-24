'use client';

import { useState } from 'react';
import { Group, Stack, Table, Text, ThemeIcon, ActionIcon } from '@mantine/core';
import { IconRobot, IconX } from '@tabler/icons-react';
import { AgentPicker } from './AgentPicker';
import { useAgents } from '@/lib/hooks/use-agents';
import { useT } from '@/i18n';
import type { StepData } from './WorkflowStepList';

export interface ParallelStepConfigProps {
  /** Ordered AgentDefinition ids that run concurrently. */
  parallelAgentIds: string[];
  onChange: (updates: Partial<StepData>) => void;
}

/**
 * Agent-group editor for a `StepType.Parallel` step, shared by the canvas node and the linear
 * (phone) step card. It keeps `agentDefinitionId` pointed at the first agent in the group, since
 * that FK is non-nullable server-side even though a parallel step resolves its real agents from
 * `parallelAgentIdsJson`.
 */
export function ParallelStepConfig({ parallelAgentIds, onChange }: ParallelStepConfigProps) {
  const t = useT('wfSteps.parallel');
  const [pickerValue, setPickerValue] = useState<string | null>(null);
  const { data: agents } = useAgents();

  const agentIds = parallelAgentIds ?? [];
  const getAgentName = (id: string) => agents?.find((a) => a.id === id)?.name ?? id;

  const addAgent = (agentId: string) => {
    if (!agentId || agentIds.includes(agentId)) return;
    const updated = [...agentIds, agentId];
    onChange({ parallelAgentIds: updated, agentDefinitionId: updated[0] ?? '' });
    setPickerValue(null);
  };

  const removeAgent = (index: number) => {
    const updated = agentIds.filter((_, i) => i !== index);
    onChange({ parallelAgentIds: updated, agentDefinitionId: updated[0] ?? '' });
  };

  return (
    <Stack gap="xs" onClick={(e) => e.stopPropagation()}>
      <Text size="xs" c="dimmed" fw={600}>
        {t('intro')}
      </Text>

      {agentIds.length > 0 && (
        <Table striped highlightOnHover withTableBorder withColumnBorders fz="xs">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>#</Table.Th>
              <Table.Th>{t('agentColumn')}</Table.Th>
              <Table.Th style={{ width: 32 }} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {agentIds.map((id, i) => (
              <Table.Tr key={id}>
                <Table.Td>
                  <ThemeIcon size="sm" variant="light" color="cyan" radius="xl">
                    <IconRobot size={11} />
                  </ThemeIcon>
                </Table.Td>
                <Table.Td>
                  {/* `maw` in percent rather than px: this same editor renders both in a
                      fixed-width canvas node and in a full-width phone card. */}
                  <Text size="xs" truncate maw="100%">{getAgentName(id)}</Text>
                </Table.Td>
                <Table.Td>
                  <ActionIcon
                    size="xs"
                    color="red"
                    variant="subtle"
                    onClick={() => removeAgent(i)}
                    aria-label={t('removeAgent', { name: getAgentName(id) })}
                  >
                    <IconX size={12} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Group gap="xs" align="flex-end">
        <div style={{ flex: 1 }}>
          <AgentPicker
            value={pickerValue}
            onChange={(id) => {
              if (id) addAgent(id);
            }}
            placeholder={t('addPlaceholder')}
            excludeIds={agentIds}
          />
        </div>
      </Group>

      <Text size="xs" c="dimmed">
        {/* eslint-disable-next-line i18next/no-literal-string -- JSON shape example, not copy */}
        {t('outputFormat')} <code>[{`{"agentName":"...","output":"..."}`}, ...]</code>
      </Text>
    </Stack>
  );
}
