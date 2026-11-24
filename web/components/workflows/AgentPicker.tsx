'use client';

import { Select, Group, Text, Badge } from '@mantine/core';
import { useT } from '@/i18n';
import { useAgents } from '@/lib/hooks/use-agents';
import { getAgentGroup } from '@/lib/utils/constants';
import { useMemo } from 'react';
import type { ComboboxItem, SelectProps } from '@mantine/core';

interface AgentOptionItem extends ComboboxItem {
  toolCount: number;
  generatesOutput: boolean;
}

interface AgentPickerProps {
  value: string | null;
  onChange: (value: string) => void;
  placeholder?: string;
  excludeIds?: string[];
}

function AgentOption({ item }: { item: AgentOptionItem }) {
  const t = useT('workflows.agentPicker');
  return (
    <Group justify="space-between" wrap="nowrap" gap="xs" style={{ width: '100%' }}>
      <Text size="sm" truncate>{item.label}</Text>
      <Group gap={4} wrap="nowrap">
        {item.toolCount > 0 && (
          <Badge color="blue" variant="light" size="xs">{t('toolsShort', { count: item.toolCount })}</Badge>
        )}
        {item.generatesOutput && (
          <Badge color="teal" variant="light" size="xs">{t('outBadge')}</Badge>
        )}
      </Group>
    </Group>
  );
}

const renderOption: SelectProps['renderOption'] = ({ option }) => <AgentOption item={option as AgentOptionItem} />;

export function AgentPicker({ value, onChange, placeholder, excludeIds }: AgentPickerProps) {
  const t = useT('workflows.agentPicker');
  const { data: agents } = useAgents();

  const options = useMemo(() => {
    if (!agents) return [];
    const groups: Record<string, AgentOptionItem[]> = {};
    for (const agent of agents) {
      if (excludeIds?.includes(agent.id)) continue;
      const group = getAgentGroup(agent.agentType);
      const groupLabel = t.has(`group.${group.replace(/ /g, '')}`) ? t(`group.${group.replace(/ /g, '')}`) : group;
      if (!groups[groupLabel]) groups[groupLabel] = [];
      groups[groupLabel].push({
        value: agent.id,
        label: agent.name,
        toolCount: agent.availableTools?.length ?? 0,
        generatesOutput: agent.generatesOutput,
      });
    }
    return Object.entries(groups).map(([group, items]) => ({ group, items }));
  }, [agents, excludeIds, t]);

  return (
    <Select
      aria-label={t('ariaLabel')}
      placeholder={placeholder ?? t('placeholder')}
      data={options}
      value={value}
      onChange={(v) => v && onChange(v)}
      searchable
      renderOption={renderOption}
    />
  );
}
