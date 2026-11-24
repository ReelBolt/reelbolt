'use client';

import { Modal, Stack, Text, Group, Button, Alert } from '@mantine/core';
import { IconCoin } from '@tabler/icons-react';
import { useT, useFormat } from '@/i18n';
import type { WorkflowStep } from '@/lib/types/workflow';

/**
 * What a workflow's paid video generation looks like, from its saved step definitions: whether any
 * VideoGenerate step exists and the sum of their Max spend caps (an UPPER BOUND on a fresh run,
 * not a quote: the real estimate depends on model, duration, takes and plan size).
 */
export function getVideoGenerateInfo(
  steps: Pick<WorkflowStep, 'stepType' | 'videoGenerateConfigJson'>[] | undefined,
): { has: boolean; capCredits: number | null } {
  const vg = (steps ?? []).filter((s) => s.stepType === 'VideoGenerate');
  if (vg.length === 0) return { has: false, capCredits: null };
  let total = 0;
  for (const s of vg) {
    try {
      const cap = Number((JSON.parse(s.videoGenerateConfigJson ?? '{}') as { maxSpendCredits?: unknown }).maxSpendCredits);
      if (!Number.isFinite(cap) || cap <= 0) return { has: true, capCredits: null };
      total += cap;
    } catch {
      return { has: true, capCredits: null };
    }
  }
  return { has: true, capCredits: total };
}

interface Props {
  opened: boolean;
  onClose: () => void;
  /** `regenerate: false` reuses previously generated clips (default); `true` pays for fresh ones. */
  onChoose: (regenerate: boolean) => void;
  capCredits: number | null;
  busy?: boolean;
  actionLabel?: string;
}

export function RegenerateVideoClipsModal({ opened, onClose, onChoose, capCredits, busy, actionLabel }: Props) {
  const t = useT('workflows.regenerate');
  const f = useFormat();
  return (
    <Modal opened={opened} onClose={onClose} title={t('title')} centered size="md">
      <Stack gap="md">
        <Text size="sm">
          {t('billedByProvider')}
        </Text>
        <Alert color="yellow" variant="light" icon={<IconCoin size={16} />}>
          <Text size="xs">
            {t('spendsMoney')}{' '}
            {capCredits !== null
              ? t('upperBound', { credits: f.number(capCredits, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) })
              : t('noEstimate')}
          </Text>
        </Alert>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={busy}>{t('cancel')}</Button>
          <Button color="orange" variant="light" onClick={() => onChoose(true)} disabled={busy}>
            {t('regenerate')}
          </Button>
          <Button onClick={() => onChoose(false)} loading={busy} variant="gradient" gradient={{ from: 'violet', to: 'purple' }}>
            {t('runAndReuse', { action: actionLabel ?? t('defaultAction') })}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
