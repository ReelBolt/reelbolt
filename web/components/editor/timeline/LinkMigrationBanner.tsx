'use client';

import { useMemo, useState } from 'react';
import { Alert, Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { hasAudioStream } from '@/lib/editing/media/pool-insert';
import { migrateToLinkedAudio } from '@/lib/editing/link';
import { linkSummaryMsg } from '@/lib/editing/summary-msg';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { useT } from '@/i18n';
import classes from '../phone/phone.module.css';

const storageKey = (timelineId: string) => `reelforge.editor.linkMigrationDismissed.${timelineId}`;

function readDismissed(timelineId: string): boolean {
  try {
    return window.localStorage.getItem(storageKey(timelineId)) === '1';
  } catch {
    return false;
  }
}

export interface LinkMigrationBannerProps {
  timelineId: string;
  doc: EditTimeline;
  pool: readonly MediaPoolItem[] | undefined;
  readOnly: boolean;
  /** Applies the ops as one undo step (the shared batch dispatcher). */
  onApply: (ops: readonly EditOp[], label: string) => void;
}

/**
 * R08.1 migration: an existing timeline whose clips have sound that is not on its own track yet is
 * offered "Add sound tracks to my clips" once. It never runs by itself; the plan is shown in plain
 * words, applying it is one undo step, and the banner can be dismissed for good.
 */
export function LinkMigrationBanner({ timelineId, doc, pool, readOnly, onApply }: LinkMigrationBannerProps) {
  const t = useT('editor-timeline.linkMigration');
  const phone = useDeviceClass() === 'phone';
  const [dismissed, setDismissed] = useState(() => (typeof window === 'undefined' ? true : readDismissed(timelineId)));
  const plan = useMemo(() => {
    if (!pool) return null;
    const probes = new Map<string, boolean | null>();
    for (const item of pool) probes.set(item.mediaRef.slice(item.mediaRef.indexOf(':') + 1), hasAudioStream(item));
    return migrateToLinkedAudio(doc, probes);
  }, [doc, pool]);

  const summary = plan ? t.msg(linkSummaryMsg(plan.linked, plan.added)) : '';

  if (readOnly || dismissed || !plan || plan.ops.length === 0) return null;

  const apply = () => {
    onApply(plan.ops, t('apply'));
    notifications.show({ color: 'blue', title: t('addedTitle'), message: t('addedMessage', { summary }) });
  };
  const dismiss = () => {
    try {
      window.localStorage.setItem(storageKey(timelineId), '1');
    } catch {
      // Private mode: the banner only comes back after a reload.
    }
    setDismissed(true);
  };

  // The phone keeps this to one row: at full size it took the room of the picture and pushed Play off screen.
  if (phone) {
    return (
      <div className={classes.linkBanner} data-testid="link-migration-banner" role="region" aria-label={t('region')}>
        <span className={classes.linkBannerText}>{t('phoneBanner', { count: plan.linked + plan.added })}</span>
        <Button h={44} px={10} size="xs" fz="xs" flex="0 0 auto" aria-label={t('apply')} data-testid="link-migration-apply" onClick={apply}>
          {t('phoneApply')}
        </Button>
        <Button h={44} px={8} size="xs" fz="xs" flex="0 0 auto" variant="subtle" color="gray" data-testid="link-migration-dismiss" onClick={dismiss}>
          {t('notNow')}
        </Button>
      </div>
    );
  }

  return (
    <Alert color="blue" variant="light" py={6} px="sm" data-testid="link-migration-banner" role="region" aria-label={t('region')}>
      <Group justify="space-between" wrap="wrap" gap="xs">
        <Text size="sm">{t('banner', { summary })}</Text>
        <Group gap="xs" wrap="nowrap">
          <Button
            size="compact-sm"
            data-testid="link-migration-apply"
            onClick={apply}
          >
            {t('apply')}
          </Button>
          <Button
            size="compact-sm"
            variant="subtle"
            color="gray"
            data-testid="link-migration-dismiss"
            onClick={dismiss}
          >
            {t('notNow')}
          </Button>
        </Group>
      </Group>
    </Alert>
  );
}
