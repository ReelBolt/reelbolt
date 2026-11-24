'use client';

import { useState } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import { Button, Group, Modal, Select, Stack, Switch, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { createCapability, type ProviderScope } from '@/lib/api/inference-providers';
import type { InferenceModel, InferenceProvider, SelectableCapability } from '@/lib/types/inference-provider';
import { CAPABILITY_HINTS, DEFAULT_NOUN, capabilityOptionsForKind } from './provider-catalog';

/**
 * Adds one capability to a model. Offered only what the provider's kind can serve and the model
 * does not already have; `Compute` is never offered. The caller mounts it only while it is open, so
 * every open starts from fresh state.
 */
export function AddCapabilityForm({
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
  model: InferenceModel;
  scope: ProviderScope;
}) {
  const tAll = useT();
  const present = new Set(model.capabilities.map((c) => c.capability));
  const options = capabilityOptionsForKind(provider.kind).filter((o) => !present.has(o.value));
  const [capability, setCapability] = useState<SelectableCapability | null>(options[0]?.value ?? null);
  const [isDefault, setIsDefault] = useState(false);
  const [isEnabled, setIsEnabled] = useState(true);
  const [saving, setSaving] = useState(false);

  const submit = async () => {
    if (!capability) return;
    setSaving(true);
    try {
      await createCapability(provider.id, model.id, { capability, isDefault, isEnabled }, scope);
      notifications.show({ title: 'Capability added', message: `${model.displayName} now serves ${capability}.`, color: 'green' });
      onSuccess();
      onClose();
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'The capability could not be added'),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  };

  // Full screen on a phone so the form is not a cramped card with the keyboard over it.
  const isMobile = useMediaQuery('(max-width: 47.99375em)');

  return (
    <Modal opened={opened} onClose={onClose} title={`Add a capability to ${model.displayName}`} centered fullScreen={isMobile}>
      <Stack>
        {options.length === 0 ? (
          <Text size="sm" c="dimmed">
            This model already has every capability a {provider.kind} provider can serve.
          </Text>
        ) : (
          <>
            <Select
              label="Capability"
              description={capability ? CAPABILITY_HINTS[capability] : undefined}
              data={options}
              value={capability}
              onChange={(v) => setCapability(v as SelectableCapability | null)}
              allowDeselect={false}
              maxDropdownHeight={240}
            />
            <Switch
              label={`Set as default ${capability ? DEFAULT_NOUN[capability] : ''} model`}
              checked={isDefault}
              onChange={(e) => setIsDefault(e.currentTarget.checked)}
            />
            <Switch label="Enabled" checked={isEnabled} onChange={(e) => setIsEnabled(e.currentTarget.checked)} />
          </>
        )}
        <Group justify="flex-end" wrap="wrap">
          <Button variant="default" w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} onClick={onClose}>Cancel</Button>
          <Button w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} onClick={submit} loading={saving} disabled={!capability}>Add capability</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
