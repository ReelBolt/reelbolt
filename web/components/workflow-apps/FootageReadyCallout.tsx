'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Button, Group, Paper, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconMovie } from '@tabler/icons-react';
import { applyWorkflowTemplate } from '@/lib/api/workflows';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { fileKindsOf } from './gallery-model';

/** The recipe a project with clips and no code is led to. */
export const FOOTAGE_RECIPE_KEY = 'edit-my-video';

interface FileLike {
  originalFileName: string;
  mimeType: string;
  category?: string;
}

/**
 * How many uploaded video clips there are, when the project is a footage project: at least one clip and
 * no code. Renders and agent files are not clips the person uploaded, so they are not counted. 0 otherwise.
 */
export function footageClipCount(files: readonly FileLike[] | undefined | null): number {
  const list = files ?? [];
  if (fileKindsOf(list).code) return 0;
  return list.filter((f) => (f.mimeType ?? '').startsWith('video/') && f.category !== 'outputFiles' && f.category !== 'agentFiles').length;
}

/**
 * "Your clips are ready": shown on Files and Overview when a project has video clips and no code. The
 * primary button sets up the highlight edit app (the same call the recipe card makes) and opens it; the
 * secondary goes to the Apps tab filtered to Footage.
 */
export function FootageReadyCallout({ projectId, count }: { projectId: string; count: number }) {
  const t = useT('apps.footageReady');
  const tAll = useT();
  const router = useRouter();
  const [busy, setBusy] = useState(false);

  if (count <= 0) return null;

  const start = async () => {
    if (busy) return;
    setBusy(true);
    try {
      const workflow = await applyWorkflowTemplate(projectId, FOOTAGE_RECIPE_KEY, true);
      router.push(`/projects/${projectId}/workflows/${workflow.id}`);
    } catch (err: unknown) {
      notifications.show({
        title: t('failedTitle'),
        message: describeError(tAll, err, t('tryAgainLater')),
        color: 'red',
      });
      setBusy(false);
    }
  };

  return (
    <Paper withBorder p="md" radius="md" data-testid="footage-ready" role="region" aria-label={t('title')}>
      <Group justify="space-between" align="center" gap="md" wrap="wrap">
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0, flex: 1 }}>
          <IconMovie size={22} aria-hidden />
          <div style={{ minWidth: 0 }}>
            <Text fw={650}>{t('title')}</Text>
            <Text size="sm" c="dimmed">{t('body', { count })}</Text>
          </div>
        </Group>
        <Group gap="xs">
          <Button onClick={() => void start()} loading={busy} style={{ minHeight: 44 }}>
            {busy ? t('settingUp') : t('primary')}
          </Button>
          <Button
            component={Link}
            href={`/projects/${projectId}?tab=workflows&category=footage`}
            variant="default"
            style={{ minHeight: 44 }}
          >
            {t('secondary')}
          </Button>
        </Group>
      </Group>
    </Paper>
  );
}
