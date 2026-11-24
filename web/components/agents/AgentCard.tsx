'use client';

import { Card, Text, Group, Stack, ActionIcon, Tooltip, Collapse, Button } from '@mantine/core';
import { IconRobot, IconLock, IconExternalLink, IconChevronDown, IconChevronUp } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { useState } from 'react';
import { AgentTypeBadge } from './AgentTypeBadge';
import { AgentCapabilityBadge } from './AgentCapabilityBadge';
import { AgentSchemaViewer } from './AgentSchemaViewer';
import type { AgentDefinition } from '@/lib/types/agent';
import { agentDisplayDescription, agentDisplayName } from '@/lib/utils/agent-labels';
import { useT } from '@/i18n';

/**
 * One agent, written for customers: a role name ("Code reader") and one plain line about what it
 * does. Engine identifiers — the type enum, output schema name, tool count, stored description and
 * schema — sit behind a "Details" toggle.
 */
export function AgentCard({ agent }: { agent: AgentDefinition }) {
  const router = useRouter();
  const t = useT('agents.card');
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [schemaOpen, setSchemaOpen] = useState(false);
  const name = agentDisplayName(agent);
  const description = agentDisplayDescription(agent);

  return (
    <Card padding="lg" radius="md" withBorder>
      <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap">
          <Group
            gap="xs"
            style={{ flex: 1, cursor: 'var(--rb-cursor-pointer)', minWidth: 0 }}
            onClick={() => router.push(`/agents/${agent.id}`)}
          >
            <IconRobot size={20} />
            <Text fw={600} size="sm" truncate>{name}</Text>
          </Group>
          <Group gap={4} wrap="nowrap">
            {agent.isBuiltIn && (
              <Tooltip label={t('builtInTip')}>
                <IconLock size={16} color="gray" role="img" aria-label={t('builtIn')} />
              </Tooltip>
            )}
            <Tooltip label={t('openTip')}>
              <ActionIcon
                variant="subtle"
                size={44}
                aria-label={t('open', { name })}
                onClick={() => router.push(`/agents/${agent.id}`)}
              >
                <IconExternalLink size={16} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </Group>

        {description && (
          <Text size="sm" c="dimmed" lineClamp={2}>
            {description}
          </Text>
        )}

        <Button
          variant="subtle"
          size="compact-xs"
          color="gray"
          leftSection={detailsOpen ? <IconChevronUp size={12} /> : <IconChevronDown size={12} />}
          onClick={() => setDetailsOpen((o) => !o)}
          justify="start"
          aria-expanded={detailsOpen}
          style={{ alignSelf: 'flex-start' }}
        >
          {detailsOpen ? t('hideDetails') : t('details')}
        </Button>
        {detailsOpen && (
          <Stack gap="xs" data-testid="agent-details">
            <Text size="xs" c="dimmed">{t('internalName', { name: agent.name })}</Text>
            <AgentTypeBadge agentType={agent.agentType} color={agent.color} />
            <AgentCapabilityBadge agent={agent} />
            {agent.description && agent.description !== description && (
              <Text size="xs" c="dimmed">{agent.description}</Text>
            )}
            {agent.outputSchemaJson && (
              <>
                <Button
                  variant="subtle"
                  size="compact-xs"
                  color="green"
                  leftSection={schemaOpen ? <IconChevronUp size={12} /> : <IconChevronDown size={12} />}
                  onClick={() => setSchemaOpen((o) => !o)}
                  justify="start"
                  style={{ alignSelf: 'flex-start' }}
                >
                  {schemaOpen ? t('hideSchema') : t('showSchema')}
                </Button>
                <Collapse expanded={schemaOpen}>
                  <AgentSchemaViewer schemaJson={agent.outputSchemaJson} />
                </Collapse>
              </>
            )}
          </Stack>
        )}
      </Stack>
    </Card>
  );
}
