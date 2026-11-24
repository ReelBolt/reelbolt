'use client';

import { useState } from 'react';
import { MultiSelect, Text, Group, Loader } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useAuth } from '@/lib/hooks/use-auth';
import { useSkills } from '@/lib/hooks/use-skills';
import { setAgentSkills } from '@/lib/api/skills';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface AgentSkillsSelectProps {
  agentId: string;
  assignedSkills: string[] | null;
  onUpdated: () => void;
}

export function AgentSkillsSelect({ agentId, assignedSkills, onUpdated }: AgentSkillsSelectProps) {
  const { isPlatformAdmin: isAdmin } = useAuth();
  const { data, isLoading } = useSkills();
  const t = useT('agents.skills');
  const tAll = useT();
  const [saving, setSaving] = useState(false);

  const value = assignedSkills ?? [];

  if (!isAdmin) {
    return (
      <Group gap="xs">
        <Text size="sm" fw={500}>{t('label')}</Text>
        <Text size="sm" c="dimmed">{value.length > 0 ? value.join(', ') : t('none')}</Text>
      </Group>
    );
  }

  if (isLoading) {
    return (
      <Group gap="xs">
        <Text size="sm" fw={500}>{t('label')}</Text>
        <Loader size="xs" />
      </Group>
    );
  }

  const skillData = (data ?? []).map((s) => ({ value: s.name, label: s.displayName }));

  const handleChange = async (newValue: string[]) => {
    setSaving(true);
    try {
      await setAgentSkills(agentId, newValue);
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

  return (
    <MultiSelect
      label={t('label')}
      description={t('description')}
      data={skillData}
      value={value}
      onChange={handleChange}
      disabled={saving}
      rightSection={saving ? <Loader size="xs" /> : undefined}
      maw={480}
    />
  );
}
