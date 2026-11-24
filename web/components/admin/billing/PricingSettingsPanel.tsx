'use client';

import { useState } from 'react';
import { Button, Card, Group, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { updatePricingSettings } from '@/lib/api/pricing';
import type { PricingSettings } from '@/lib/types/pricing';
import { formatCreditAmount, formatUsdPerUnit } from '@/lib/utils/billing';

/**
 * The global markup, the one pricing figure an admin edits here. The credit value it is divided by
 * is shown read-only: it is the published price of a credit and changes only with a release.
 *
 * Seeded from `settings` once; the caller remounts it (by key) when the saved settings change.
 */
export function PricingSettingsPanel({
  settings,
  onSaved,
}: {
  settings: PricingSettings;
  onSaved: () => void;
}) {
  const tAll = useT();
  const [markup, setMarkup] = useState(String(settings.markup));
  const [saving, setSaving] = useState(false);

  const parsed = Number(markup);
  const valid = markup.trim() !== '' && Number.isFinite(parsed) && parsed > 0;
  const changed = valid && parsed !== settings.markup;
  const exampleMarkup = valid ? parsed : settings.markup;
  const exampleCredits = settings.creditUsdValue > 0 ? exampleMarkup / settings.creditUsdValue : 0;

  const save = async () => {
    if (!valid) return;
    setSaving(true);
    try {
      await updatePricingSettings({ markup: parsed });
      notifications.show({
        title: 'Markup saved',
        message: 'Every price that does not set its own credits now uses it.',
        color: 'green',
      });
      onSaved();
    } catch (err: unknown) {
      notifications.show({
        title: 'Error',
        message: describeError(tAll, err, 'The markup could not be saved'),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  };

  return (
    <Card withBorder padding="md">
      <Stack gap="sm">
        <Text size="sm">
          Credits = cost × markup ÷ credit value, unless a price sets its own credits.
        </Text>
        <Group align="flex-end" wrap="wrap">
          <TextInput
            label="Markup"
            description="1.2 charges 20% over what the vendor charges us."
            inputMode="decimal"
            value={markup}
            onChange={(e) => setMarkup(e.currentTarget.value)}
            error={markup.trim() !== '' && !valid ? 'A positive number.' : undefined}
            w={200}
          />
          <TextInput
            label="Credit value"
            description="What one credit is worth. Fixed."
            value={`${formatUsdPerUnit(settings.creditUsdValue)} per credit`}
            readOnly
            w={200}
          />
          <Button onClick={save} loading={saving} disabled={!changed}>
            Save markup
          </Button>
        </Group>
        <Text size="xs" c="dimmed">
          At this markup, $1.00 of vendor cost is charged {formatCreditAmount(exampleCredits)} credits.
        </Text>
      </Stack>
    </Card>
  );
}
