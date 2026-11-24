'use client';

import { useState } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import useSWR from 'swr';
import { Alert, Button, Group, Modal, Select, Stack, Text, TextInput, Textarea, UnstyledButton } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { IconInfoCircle } from '@tabler/icons-react';
import { fetcher } from '@/lib/api/client';
import { createPrice } from '@/lib/api/inference-providers';
import { priceBookUrl } from '@/lib/api/pricing';
import type { InferenceModel, InferenceProvider, ModelCapability } from '@/lib/types/inference-provider';
import { UNITS_BY_CAPABILITY, type PriceBookEntry, type UsageKind } from '@/lib/types/pricing';
import {
  displayUnitNoun,
  formatPriceCost,
  fromDisplayUnit,
  parseDateTimeLocalValue,
  toDateTimeLocalValue,
  toDisplayUnit,
} from '@/lib/utils/billing';

interface PriceFormValues {
  unit: UsageKind;
  variant: string;
  cost: string;
  override: string;
  effectiveFrom: string;
  notes: string;
}

const parseNumber = (value: string): number | null => {
  if (value.trim() === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
};

/**
 * Adds a price row to one PLATFORM model capability. Never mounted on a workspace page: workspace
 * capabilities are not priced and the org API has no price routes.
 *
 * Costs are typed per DISPLAY unit — per million tokens for token units, per second, per image — and
 * stored per single unit, because a per-token price is too small to type or read. The form starts
 * from the price book's suggestion for the chosen unit, when there is one. The caller mounts it only
 * while it is open, so every open starts from fresh values.
 */
export function PriceForm({
  onClose,
  onSuccess,
  provider,
  model,
  capability,
}: {
  onClose: () => void;
  onSuccess: () => void;
  provider: InferenceProvider;
  model: InferenceModel;
  capability: ModelCapability;
}) {
  const tAll = useT();
  const units = UNITS_BY_CAPABILITY[capability.capability];
  const [saving, setSaving] = useState(false);

  const form = useForm<PriceFormValues>({
    initialValues: {
      unit: units[0],
      variant: '',
      cost: '',
      override: '',
      effectiveFrom: toDateTimeLocalValue(new Date()),
      notes: '',
    },
    validate: {
      cost: (value) => {
        const n = parseNumber(value);
        if (n === null) return value.trim() === '' ? 'Required.' : 'Not a number.';
        return n < 0 ? 'A cost cannot be negative.' : null;
      },
      override: (value) => {
        if (value.trim() === '') return null;
        const n = parseNumber(value);
        if (n === null) return 'Not a number.';
        return n < 0 ? 'Credits cannot be negative.' : null;
      },
      effectiveFrom: (value) =>
        value.trim() === '' || parseDateTimeLocalValue(value) !== null ? null : 'Not a valid date and time.',
    },
  });

  const apply = (entry: PriceBookEntry) => {
    form.setValues({
      unit: entry.unit,
      variant: entry.variant ?? '',
      cost: String(toDisplayUnit(entry.costUsdPerUnit, entry.unit)),
      notes: entry.notes ?? '',
    });
  };

  // The price book is only ever a starting point: it pre-fills the form for the first unit it knows,
  // and every suggestion stays one click away below.
  const { data: book } = useSWR<PriceBookEntry[]>(
    priceBookUrl(provider.kind, capability.capability, model.modelCode),
    fetcher,
    {
      onSuccess: (entries) => {
        if (form.isDirty('cost')) return;
        const first = entries.find((e) => e.unit === form.values.unit) ?? entries.find((e) => units.includes(e.unit));
        if (first) apply(first);
      },
    },
  );
  const suggestions = (book ?? []).filter((e) => units.includes(e.unit));

  const handleUnitChange = (value: string | null) => {
    if (!value) return;
    const unit = value as UsageKind;
    form.setFieldValue('unit', unit);
    const match = suggestions.find((e) => e.unit === unit);
    if (match) apply(match);
  };

  const { unit } = form.values;
  const noun = displayUnitNoun(unit);

  const submit = form.onSubmit(async (values) => {
    setSaving(true);
    try {
      const cost = parseNumber(values.cost) ?? 0;
      const override = parseNumber(values.override);
      const variant = values.variant.trim();
      const notes = values.notes.trim();
      await createPrice(provider.id, model.id, capability.id, {
        unit: values.unit,
        variant: variant || undefined,
        costUsdPerUnit: fromDisplayUnit(cost, values.unit),
        // Typed in credits per display unit; stored in millicredits per single unit.
        creditsMillisPerUnitOverride: override === null ? undefined : fromDisplayUnit(override * 1000, values.unit),
        effectiveFrom: parseDateTimeLocalValue(values.effectiveFrom) ?? undefined,
        notes: notes || undefined,
      });
      notifications.show({
        title: 'Price added',
        message: 'It starts at the chosen time and closes the open price for the same unit and variant.',
        color: 'green',
      });
      onSuccess();
      onClose();
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'The price could not be added'),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  });

  // Full screen on a phone so the form is not a cramped card with the keyboard over it.
  const isMobile = useMediaQuery('(max-width: 47.99375em)');

  return (
    <Modal opened onClose={onClose} title={`Add a price: ${model.displayName} · ${capability.capability}`} centered fullScreen={isMobile}>
      <form onSubmit={submit}>
        <Stack gap="sm">
          <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />}>
            <Text size="sm">
              Credits = cost × markup ÷ credit value, unless a price sets its own credits. A price is never
              edited: a new one for the same unit and variant replaces the open one from its start time.
            </Text>
          </Alert>
          {suggestions.length > 0 && (
            <Stack gap={4}>
              <Text size="xs" c="dimmed">From the price book:</Text>
              {suggestions.map((entry) => (
                <UnstyledButton
                  key={`${entry.unit}:${entry.variant ?? ''}`}
                  onClick={() => apply(entry)}
                  fz="sm"
                  c="blue"
                  mih={{ base: 40, sm: 0 }}
                  ta="left"
                >
                  {entry.unit}
                  {entry.variant ? ` (${entry.variant})` : ''}: {formatPriceCost(entry.costUsdPerUnit, entry.unit)}
                </UnstyledButton>
              ))}
            </Stack>
          )}
          <Select
            label="Unit"
            data={units.map((u) => ({ value: u, label: u }))}
            allowDeselect={false}
            maxDropdownHeight={240}
            {...form.getInputProps('unit')}
            onChange={handleUnitChange}
          />
          <TextInput
            label="Variant"
            description="Optional. Separates prices of one unit, such as a video resolution (1080p) or an image tier."
            {...form.getInputProps('variant')}
          />
          <TextInput
            label={`Cost (USD per ${noun})`}
            description="What the vendor charges us."
            inputMode="decimal"
            {...form.getInputProps('cost')}
          />
          <TextInput
            label={`Credits override (credits per ${noun})`}
            description="Optional. Leave blank to charge cost × markup ÷ credit value."
            inputMode="decimal"
            {...form.getInputProps('override')}
          />
          <TextInput
            label="Effective from"
            description="Empty means now, on the server."
            placeholder="YYYY-MM-DDTHH:MM"
            {...form.getInputProps('effectiveFrom')}
          />
          <Textarea label="Notes" rows={2} {...form.getInputProps('notes')} />
          <Group justify="flex-end" mt="xs" wrap="wrap">
            <Button variant="default" w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} onClick={onClose}>Cancel</Button>
            <Button type="submit" w={{ base: '100%', sm: 'auto' }} h={{ base: 40, sm: 36 }} loading={saving}>Add price</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
