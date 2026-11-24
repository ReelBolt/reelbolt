'use client';

import { useState } from 'react';
import { Modal, TextInput, Textarea, Button, Stack, ColorInput } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { createAgent, updateAgent } from '@/lib/api/agents';
import type { AgentDefinition } from '@/lib/types/agent';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/** Example colour shown in the empty field: a hex code, not copy. */
const COLOR_EXAMPLE = '#3B82F6';

interface AgentFormProps {
  opened: boolean;
  onClose: () => void;
  onSuccess: () => void;
  agent?: AgentDefinition;
}

export function AgentForm({ opened, onClose, onSuccess, agent }: AgentFormProps) {
  const t = useT('agents.form');
  const tAll = useT();
  const [loading, setLoading] = useState(false);
  const isEdit = !!agent;

  const form = useForm({
    initialValues: {
      name: agent?.name || '',
      description: agent?.description || '',
      systemPrompt: agent?.systemPrompt || '',
      color: agent?.color || '',
    },
    validate: {
      name: (v) => (!v.trim() ? t('nameRequired') : null),
      systemPrompt: (v) => (!v.trim() ? t('promptRequired') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setLoading(true);
    try {
      if (isEdit) {
        await updateAgent(agent.id, values);
        notifications.show({ title: t('updatedTitle'), message: t('updatedBody'), color: 'green' });
      } else {
        await createAgent(values);
        notifications.show({ title: t('createdTitle'), message: t('createdBody'), color: 'green' });
      }
      form.reset();
      onSuccess();
      onClose();
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tAll, err, t('failed')),
        color: 'red',
      });
    } finally {
      setLoading(false);
    }
  });

  return (
    <Modal opened={opened} onClose={onClose} title={isEdit ? t('editTitle') : t('createTitle')} centered size="lg">
      <form onSubmit={handleSubmit}>
        <Stack>
          <TextInput label={t('name')} placeholder={t('namePlaceholder')} {...form.getInputProps('name')} />
          <Textarea label={t('description')} placeholder={t('descriptionPlaceholder')} rows={2} {...form.getInputProps('description')} />
          <Textarea label={t('prompt')} placeholder={t('promptPlaceholder')} rows={8} {...form.getInputProps('systemPrompt')} />
          <ColorInput label={t('color')} placeholder={COLOR_EXAMPLE} {...form.getInputProps('color')} />
          <Button type="submit" loading={loading} fullWidth>
            {isEdit ? t('update') : t('create')}
          </Button>
        </Stack>
      </form>
    </Modal>
  );
}
