'use client';

import { ActionIcon, Badge, Button, Card, Group, Paper, Stack, Switch, Text, Tooltip } from '@mantine/core';
import { IconCircleCheck, IconCircleDashed, IconCircleX, IconEdit, IconPlugConnected, IconPlus, IconTrash } from '@tabler/icons-react';
import type {
  InferenceModel,
  ModelCapability,
  ModelCapabilityPrice,
} from '@/lib/types/inference-provider';
import { formatDate } from '@/lib/utils/format';
import { CAPABILITY_BADGE } from './InferenceProviderTable';
import { CapabilityPricesTable } from './CapabilityPricesTable';

/**
 * Everything a model list can ask its owner to do. The owner (the provider page) holds the modals
 * and the API calls; the list only says which model or capability an action is about.
 */
export interface ProviderModelActions {
  editModel: (model: InferenceModel) => void;
  deleteModel: (model: InferenceModel) => void;
  addCapability: (model: InferenceModel) => void;
  updateCapability: (model: InferenceModel, cap: ModelCapability, change: { isDefault?: boolean; isEnabled?: boolean }) => void;
  testCapability: (model: InferenceModel, cap: ModelCapability) => void;
  deleteCapability: (model: InferenceModel, cap: ModelCapability) => void;
  /** Present on the platform scope only; without them no price is rendered. */
  pricing?: {
    now: Date;
    addPrice: (model: InferenceModel, cap: ModelCapability) => void;
    retirePrice: (model: InferenceModel, cap: ModelCapability, price: ModelCapabilityPrice) => void;
  };
}

/** >=40px tap target on a phone; `0` from `sm` up leaves the control at its own size. */
const TAP = { base: 40, sm: 0 } as const;

function LastTest({ cap }: { cap: ModelCapability }) {
  if (cap.lastTestOk === null) {
    return (
      <Group gap={4} wrap="nowrap">
        <IconCircleDashed size={14} color="var(--mantine-color-gray-5)" />
        <Text size="xs" c="dimmed">Never tested</Text>
      </Group>
    );
  }
  const when = cap.lastTestAt ? formatDate(cap.lastTestAt) : '';
  return cap.lastTestOk ? (
    <Group gap={4} wrap="nowrap">
      <IconCircleCheck size={14} color="var(--mantine-color-green-6)" />
      <Text size="xs">Passed {when}</Text>
    </Group>
  ) : (
    <Tooltip label={cap.lastTestError || 'The last test failed'} multiline w={320}>
      <Group gap={4} wrap="nowrap">
        <IconCircleX size={14} color="var(--mantine-color-red-6)" />
        <Text size="xs" c="red">Failed {when}</Text>
      </Group>
    </Tooltip>
  );
}

function CapabilityRow({
  model,
  cap,
  busy,
  locked,
  actions,
}: {
  model: InferenceModel;
  cap: ModelCapability;
  busy: boolean;
  /** The system provider's rows: shown, priced, never changed by hand. */
  locked: boolean;
  actions: ProviderModelActions;
}) {
  const label = `${model.displayName} · ${cap.capability}`;
  return (
    <Paper withBorder p="xs" radius="sm" data-testid={`capability-${cap.id}`}>
      <Group justify="space-between" wrap="wrap" gap="xs">
        <Group gap="xs">
          <Badge color={CAPABILITY_BADGE[cap.capability] ?? 'gray'} variant="light">{cap.capability}</Badge>
          {cap.isDefault && <Badge color="violet" variant="filled" size="sm">Default</Badge>}
        </Group>
        <Group gap="md" wrap="wrap">
          <Switch
            size="xs"
            label="Default"
            aria-label={`Default for ${label}`}
            checked={cap.isDefault}
            disabled={busy || locked}
            onChange={(e) => actions.updateCapability(model, cap, { isDefault: e.currentTarget.checked })}
          />
          <Switch
            size="xs"
            label="Enabled"
            aria-label={`Enabled for ${label}`}
            checked={cap.isEnabled}
            disabled={busy || locked}
            onChange={(e) => actions.updateCapability(model, cap, { isEnabled: e.currentTarget.checked })}
          />
          <LastTest cap={cap} />
          {cap.capability !== 'Compute' && (
            <Button
              size="compact-xs"
              mih={TAP}
              variant="default"
              leftSection={<IconPlugConnected size={12} />}
              loading={busy}
              onClick={() => actions.testCapability(model, cap)}
              aria-label={`Test ${label}`}
            >
              {cap.lastTestOk === null ? 'Test' : 'Test again'}
            </Button>
          )}
          {!locked && (
            <ActionIcon
              size="sm"
              miw={TAP}
              mih={TAP}
              color="red"
              variant="subtle"
              onClick={() => actions.deleteCapability(model, cap)}
              aria-label={`Remove ${label}`}
            >
              <IconTrash size={14} />
            </ActionIcon>
          )}
        </Group>
      </Group>
      {actions.pricing && (
        <CapabilityPricesTable
          capability={cap}
          label={label}
          now={actions.pricing.now}
          onAdd={() => actions.pricing?.addPrice(model, cap)}
          onRetire={(price) => actions.pricing?.retirePrice(model, cap, price)}
        />
      )}
    </Paper>
  );
}

/**
 * The models under one provider, each with its capability rows: default, enabled, last test, Test
 * and remove, and — on the platform scope only — the capability's prices.
 */
export function ProviderModels({
  models,
  busyCapabilityId,
  locked,
  actions,
}: {
  models: InferenceModel[];
  busyCapabilityId: string | null;
  locked: boolean;
  actions: ProviderModelActions;
}) {
  if (models.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        No models yet. Add one to make this provider usable.
      </Text>
    );
  }
  return (
    <Stack gap="md">
      {models.map((model) => (
        <Card key={model.id} withBorder padding="sm" data-testid={`model-${model.id}`}>
          <Group justify="space-between" mb="xs" wrap="wrap">
            <Stack gap={0} style={{ minWidth: 0 }}>
              <Group gap="xs">
                <Text fw={600} style={{ wordBreak: 'break-word' }}>{model.displayName}</Text>
                {!model.isEnabled && <Badge color="gray" variant="light" size="sm">Disabled</Badge>}
              </Group>
              <Text size="xs" c="dimmed" ff="monospace" style={{ wordBreak: 'break-all' }}>{model.modelCode}</Text>
            </Stack>
            {!locked && (
              <Group gap="xs">
                <Button
                  size="compact-sm"
                  mih={TAP}
                  variant="light"
                  leftSection={<IconPlus size={14} />}
                  onClick={() => actions.addCapability(model)}
                  aria-label={`Add capability to ${model.displayName}`}
                >
                  Add capability
                </Button>
                <ActionIcon miw={TAP} mih={TAP} variant="subtle" onClick={() => actions.editModel(model)} aria-label={`Edit model ${model.displayName}`}>
                  <IconEdit size={16} />
                </ActionIcon>
                <ActionIcon miw={TAP} mih={TAP} color="red" variant="subtle" onClick={() => actions.deleteModel(model)} aria-label={`Delete model ${model.displayName}`}>
                  <IconTrash size={16} />
                </ActionIcon>
              </Group>
            )}
          </Group>
          <Stack gap="xs">
            {model.capabilities.map((cap) => (
              <CapabilityRow
                key={cap.id}
                model={model}
                cap={cap}
                busy={busyCapabilityId === cap.id}
                locked={locked}
                actions={actions}
              />
            ))}
          </Stack>
        </Card>
      ))}
    </Stack>
  );
}
