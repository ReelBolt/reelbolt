'use client';

import { useState } from 'react';
import { Alert, Button, Card, Group, NumberInput, Progress, Stack, Text } from '@mantine/core';
import { saveVideoSpendLimit } from '@/lib/api/video-spend-limit';
import { useVideoSpendLimit } from '@/lib/hooks/use-video-spend-limit';
import { useT } from '@/i18n';
import { useCreditAmount } from './useCreditAmount';

type Draft = { limit: number | string; alert: number | string };

/** A NumberInput's value as the API wants it: a number, or null when the field is empty. */
function toCredits(value: number | string): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * The user's OWN daily limit on generated-video spend (docs/design/video-spend-limit.md). Empty means no limit: only the
 * credit balance and each step's own limit apply. The optional alert is shown here and on the run page; it sends nothing.
 */
export function VideoSpendLimitCard() {
  const t = useT('billing.videoLimit');
  const credits = useCreditAmount();
  const { data, mutate } = useVideoSpendLimit();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!data) return null;

  const form: Draft = draft ?? {
    limit: data.dailyLimitCredits ?? '',
    alert: data.alertThresholdCredits ?? '',
  };
  const limit = toCredits(form.limit);
  const alert = toCredits(form.alert);
  const alertAboveLimit = limit !== null && alert !== null && alert > limit;
  const percent = data.dailyLimitCredits && data.dailyLimitCredits > 0
    ? Math.min(100, (data.usedTodayCredits / data.dailyLimitCredits) * 100)
    : 0;

  const save = async (body: { dailyLimitCredits: number | null; alertThresholdCredits: number | null }) => {
    setSaving(true);
    setError(null);
    try {
      await mutate(await saveVideoSpendLimit(body), { revalidate: false });
      setDraft(null);
    } catch {
      setError(t('saveFailed'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Card withBorder radius="lg" padding="lg" data-testid="video-spend-limit-card">
      <Stack gap="sm">
        <Text fw={600}>{t('title')}</Text>
        <Text size="sm" c="dimmed">{t('intro')}</Text>

        <Text size="sm" data-testid="video-limit-status">
          {data.dailyLimitCredits === null
            ? t('statusNone', { used: credits(data.usedTodayCredits) })
            : t('statusLimit', {
              used: credits(data.usedTodayCredits),
              limit: credits(data.dailyLimitCredits),
              left: credits(data.remainingTodayCredits ?? 0),
            })}
        </Text>
        {data.dailyLimitCredits !== null && data.dailyLimitCredits > 0 && (
          <Progress value={percent} size="sm" radius="sm" color={data.limitReached ? 'red' : data.alertReached ? 'yellow' : undefined} aria-label={t('progressLabel')} />
        )}
        {data.alertReached && !data.limitReached && (
          <Alert color="yellow" variant="light" p="xs" data-testid="video-limit-alert">{t('alertReached')}</Alert>
        )}

        <Group align="flex-start" gap="md">
          <NumberInput
            label={t('limitLabel')}
            description={t('limitHelp')}
            placeholder={t('noLimit')}
            min={0}
            decimalScale={2}
            value={form.limit}
            onChange={(value) => setDraft({ ...form, limit: value })}
            data-testid="video-limit-input"
          />
          <NumberInput
            label={t('alertLabel')}
            description={t('alertHelp')}
            placeholder={t('noAlert')}
            min={0}
            decimalScale={2}
            value={form.alert}
            onChange={(value) => setDraft({ ...form, alert: value })}
            data-testid="video-alert-input"
          />
        </Group>
        {alertAboveLimit && <Text size="xs" c="red">{t('alertAboveLimit')}</Text>}
        {error && <Alert color="red" variant="light" p="xs">{error}</Alert>}

        <Group gap="sm">
          <Button
            loading={saving}
            disabled={draft === null || alertAboveLimit}
            onClick={() => save({ dailyLimitCredits: limit, alertThresholdCredits: alert })}
          >
            {t('save')}
          </Button>
          <Button
            variant="default"
            disabled={saving || (data.dailyLimitCredits === null && data.alertThresholdCredits === null)}
            onClick={() => save({ dailyLimitCredits: null, alertThresholdCredits: null })}
          >
            {t('clear')}
          </Button>
        </Group>
        <Text size="xs" c="dimmed">{t('footnote')}</Text>
      </Stack>
    </Card>
  );
}
