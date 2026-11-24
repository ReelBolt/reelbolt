'use client';

import { useState } from 'react';
import { Select, Text, Group, Loader } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useAuth } from '@/lib/hooks/use-auth';
import { setAgentReasoningEffort } from '@/lib/api/agents';
import type { AgentReasoningEffort } from '@/lib/types/agent';
import { useT, type T } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface AgentReasoningEffortSelectProps {
  agentId: string;
  reasoningEffort: AgentReasoningEffort | null;
  /** A built-in agent is a row shared by every workspace, so only a platform admin may change it. */
  isBuiltIn: boolean;
  onUpdated: () => void;
}

const DEFAULT_VALUE = '__default__';

const EFFORTS: AgentReasoningEffort[] = ['none', 'low', 'medium', 'xhigh'];

function effortLabel(value: AgentReasoningEffort, t: T): string {
  switch (value) {
    case 'none': return t('none');
    case 'low': return t('low');
    case 'medium': return t('medium');
    case 'xhigh': return t('xhigh');
  }
}

/**
 * How hard this agent's model thinks before it answers.
 *
 * Same authority as the provider override on the API (`PUT /api/v1/agents/{id}/reasoning-effort`): a
 * built-in agent is the platform-wide default every workspace inherits, so it is a platform-admin
 * action; a custom agent belongs to its workspace, so it is that workspace's Owner/Admin. Everyone
 * else sees the current value read-only. The list is the API's closed vocabulary — `high` is not in
 * it, because the self-hosted chat template rejects it.
 */
export function AgentReasoningEffortSelect({
  agentId,
  reasoningEffort,
  isBuiltIn,
  onUpdated,
}: AgentReasoningEffortSelectProps) {
  const t = useT('agents.effort');
  const tAll = useT();
  const { isPlatformAdmin, isOrgAdmin } = useAuth();
  const [saving, setSaving] = useState(false);

  const mayEdit = isBuiltIn ? isPlatformAdmin : isOrgAdmin;

  if (!mayEdit) {
    return (
      <Group gap="xs">
        <Text size="sm" fw={500}>{t('label')}</Text>
        <Text size="sm" c="dimmed">{reasoningEffort ? effortLabel(reasoningEffort, t) : t('default')}</Text>
      </Group>
    );
  }

  const handleChange = async (value: string | null) => {
    const next = !value || value === DEFAULT_VALUE ? null : (value as AgentReasoningEffort);
    setSaving(true);
    try {
      await setAgentReasoningEffort(agentId, next);
      notifications.show({ title: t('updatedTitle'), message: t('updatedBody'), color: 'green' });
      onUpdated();
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tAll, err, t('failed')),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  };

  const options = [
    { value: DEFAULT_VALUE, label: t('default') },
    ...EFFORTS.map((value) => ({ value, label: effortLabel(value, t) })),
  ];

  return (
    <Select
      label={t('label')}
      description={t('description')}
      data={options}
      value={reasoningEffort ?? DEFAULT_VALUE}
      onChange={handleChange}
      disabled={saving}
      rightSection={saving ? <Loader size="xs" /> : undefined}
      allowDeselect={false}
      maw={360}
    />
  );
}
