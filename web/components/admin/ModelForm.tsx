'use client';

import { useEffect, useState } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import { Autocomplete, Button, Group, Modal, Select, Stack, Switch, TextInput } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { createModel, updateModel, type ProviderScope } from '@/lib/api/inference-providers';
import type { InferenceModel, InferenceProvider, SelectableCapability } from '@/lib/types/inference-provider';
import {
  CAPABILITY_HINTS,
  DEFAULT_NOUN,
  KIND_FIELD_HINTS,
  capabilityOptionsForKind,
  fallbackCapability,
  modelPlaceholderFor,
  modelSuggestions,
} from './provider-catalog';

interface ModelFormValues {
  modelCode: string;
  displayName: string;
  isEnabled: boolean;
  capability: SelectableCapability;
  isDefault: boolean;
}

/**
 * Adds a model under a provider (with its first capability) or edits one (code, display name,
 * enabled). The capability list is limited to what the provider's kind can serve, and the model
 * suggestions follow the chosen capability — exactly as on the provider form.
 */
export function ModelForm({
  opened,
  onClose,
  onSuccess,
  provider,
  model,
  scope,
}: {
  opened: boolean;
  onClose: () => void;
  onSuccess: () => void;
  provider: InferenceProvider;
  /** The model being edited, or nothing to add one. */
  model?: InferenceModel;
  scope: ProviderScope;
}) {
  const tAll = useT();
  const [saving, setSaving] = useState(false);
  const isEdit = !!model;
  const firstCapability = fallbackCapability(provider.kind);

  const form = useForm<ModelFormValues>({
    initialValues: {
      modelCode: model?.modelCode ?? '',
      displayName: model?.displayName ?? '',
      isEnabled: model?.isEnabled ?? true,
      capability: firstCapability,
      isDefault: false,
    },
    validate: {
      modelCode: (v) => (!v.trim() ? 'This field is required' : null),
    },
  });

  useEffect(() => {
    if (!opened) return;
    form.setValues({
      modelCode: model?.modelCode ?? '',
      displayName: model?.displayName ?? '',
      isEnabled: model?.isEnabled ?? true,
      capability: firstCapability,
      isDefault: false,
    });
    form.resetDirty();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, model]);

  const { modelLabel } = KIND_FIELD_HINTS[provider.kind] ?? KIND_FIELD_HINTS.OpenAICompatible;
  const { capability } = form.values;

  const submit = form.onSubmit(async (values) => {
    setSaving(true);
    try {
      const modelCode = values.modelCode.trim();
      const displayName = values.displayName.trim() || modelCode;
      if (isEdit) {
        await updateModel(provider.id, model.id, { modelCode, displayName, isEnabled: values.isEnabled }, scope);
        notifications.show({ title: 'Model updated', message: displayName, color: 'green' });
      } else {
        await createModel(
          provider.id,
          {
            modelCode,
            displayName,
            isEnabled: values.isEnabled,
            capabilities: [{ capability: values.capability, isDefault: values.isDefault, isEnabled: true }],
          },
          scope,
        );
        notifications.show({ title: 'Model added', message: displayName, color: 'green' });
      }
      onSuccess();
      onClose();
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'The model could not be saved'),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  });

  // Full screen on a phone so the form is not a cramped card with the keyboard over it.
  const isMobile = useMediaQuery('(max-width: 47.99375em)');

  return (
    <Modal opened={opened} onClose={onClose} title={isEdit ? `Edit ${model.displayName}` : 'Add a model'} centered fullScreen={isMobile}>
      <form onSubmit={submit}>
        <Stack>
          {!isEdit && (
            <Select
              label="Capability"
              description={CAPABILITY_HINTS[capability]}
              data={capabilityOptionsForKind(provider.kind)}
              allowDeselect={false}
              maxDropdownHeight={240}
              {...form.getInputProps('capability')}
            />
          )}
          <Autocomplete
            label={modelLabel}
            placeholder={modelPlaceholderFor(provider.kind, capability)}
            data={modelSuggestions(provider.kind, capability)}
            maxDropdownHeight={240}
            {...form.getInputProps('modelCode')}
          />
          <TextInput
            label="Display name"
            placeholder={form.values.modelCode.trim() || 'GPT-4o mini'}
            description="What people see when they choose this model. Blank uses the model code."
            {...form.getInputProps('displayName')}
          />
          {!isEdit && (
            <Switch
              label={`Set as default ${DEFAULT_NOUN[capability] ?? 'chat'} model`}
              {...form.getInputProps('isDefault', { type: 'checkbox' })}
            />
          )}
          <Switch label="Enabled" {...form.getInputProps('isEnabled', { type: 'checkbox' })} />
          <Group justify="flex-end" mt="sm" wrap="wrap">
            <Button variant="default" w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} onClick={onClose}>Cancel</Button>
            <Button type="submit" w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} loading={saving}>{isEdit ? 'Save model' : 'Add model'}</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
