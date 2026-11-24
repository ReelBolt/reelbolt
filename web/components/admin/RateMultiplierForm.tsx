'use client';

import { useEffect } from 'react';
import { Alert, Button, Group, Modal, Select, Stack, Text, TextInput } from '@mantine/core';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { IconInfoCircle } from '@tabler/icons-react';
import { createRateMultiplier } from '@/lib/api/pricing';
import { USAGE_KINDS, type UsageBillingMode, type UsageKind } from '@/lib/types/pricing';
import { parseDateTimeLocalValue, toDateTimeLocalValue } from '@/lib/utils/billing';

const ALL_KINDS = '__all_kinds__';

const BILLING_MODES: UsageBillingMode[] = ['Platform', 'Byo', 'Runner'];


/** Writes a new multiplier row, closing the open one it supersedes for the same mode and usage kind. */
export function RateMultiplierForm({
  opened,
  onClose,
  onSuccess,
}: {
  opened: boolean;
  onClose: () => void;
  onSuccess: () => void;
}) {
  const tAll = useT();
  const form = useForm({
    initialValues: {
      billingMode: 'Platform' as UsageBillingMode,
      usageKind: ALL_KINDS as string,
      multiplier: '',
      effectiveFrom: '',
    },
    validate: {
      multiplier: (value) => {
        if (value.trim() === '') return 'Required.';
        const parsed = Number(value);
        if (!Number.isFinite(parsed)) return 'Not a number.';
        return parsed < 0 ? 'A multiplier cannot be negative.' : null;
      },
      effectiveFrom: (value) =>
        value.trim() === '' || !Number.isNaN(new Date(value).getTime()) ? null : 'Not a valid date and time.',
    },
  });

  useEffect(() => {
    if (!opened) return;
    form.setValues({
      billingMode: 'Platform',
      usageKind: ALL_KINDS,
      multiplier: '',
      effectiveFrom: toDateTimeLocalValue(new Date()),
    });
    form.resetDirty();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened]);

  const submit = form.onSubmit(async (values) => {
    try {
      await createRateMultiplier({
        billingMode: values.billingMode,
        usageKind: values.usageKind === ALL_KINDS ? null : (values.usageKind as UsageKind),
        multiplier: Number(values.multiplier),
        effectiveFrom: parseDateTimeLocalValue(values.effectiveFrom),
      });
      notifications.show({
        title: 'Multiplier saved',
        message: 'A new multiplier row starts at that instant and closes the one it supersedes.',
        color: 'green',
      });
      onSuccess();
      onClose();
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'Failed to save the multiplier'),
        color: 'red',
      });
    }
  });

  return (
    <Modal opened={opened} onClose={onClose} title="New rate multiplier" centered>
      <form onSubmit={submit}>
        <Stack gap="sm">
          <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />}>
            <Text size="sm">
              A multiplier scales the credits a priced event costs. Choosing &quot;all kinds&quot; sets the
              mode default; a kind-specific row beats it.
            </Text>
          </Alert>
          <Select
            label="Billing mode"
            data={BILLING_MODES}
            allowDeselect={false}
            {...form.getInputProps('billingMode')}
          />
          <Select
            label="Usage kind"
            data={[
              { value: ALL_KINDS, label: 'All kinds (mode default)' },
              ...USAGE_KINDS.map((kind) => ({ value: kind, label: kind })),
            ]}
            allowDeselect={false}
            {...form.getInputProps('usageKind')}
          />
          <TextInput
            label="Multiplier"
            description="1 = full price, 0 = free. The seed ships Platform 1, BYO 0 and Runner 0."
            inputMode="decimal"
            {...form.getInputProps('multiplier')}
          />
          <TextInput
            label="Effective from"
            description="Empty means now, on the server."
            placeholder="YYYY-MM-DDTHH:MM"
            {...form.getInputProps('effectiveFrom')}
          />
          <Group justify="flex-end" mt="xs">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit">Create multiplier</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
