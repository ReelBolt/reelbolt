'use client';

import { useState } from 'react';
import Link from 'next/link';
import { Button, CloseButton, Group, Paper, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useT } from '@/i18n';
import { PHONE_QUERY, TOUCH_TARGET_PX } from '@/lib/breakpoints';
import { useWorkflowAppsEnabled } from '@/components/shell/WorkflowAppsFlag';
import { useAuth } from '@/lib/hooks/use-auth';
import { userKeyFromEmail } from '@/lib/telemetry';
import type { DashboardOverview } from '@/lib/types/dashboard';
import { dismissRunNudge, isRunNudgeDismissed, nudgeProject } from './run-nudge';

/**
 * One small, dismissible card for a project that has files and no run: one button to the project's
 * apps. Dismissal is remembered per person and project in this browser.
 */
export function RunNudge({ projectId, projectName }: { projectId: string; projectName: string }) {
  const t = useT('apps.runNudge');
  const { user } = useAuth();
  const userKey = userKeyFromEmail(user?.email);
  const phone = useMediaQuery(PHONE_QUERY, false, { getInitialValueInEffect: false }) ?? false;
  const [gone, setGone] = useState(false);
  const appsEnabled = useWorkflowAppsEnabled();
  if (!appsEnabled || !userKey || gone || isRunNudgeDismissed(userKey, projectId)) return null;

  return (
    <Paper withBorder p="md" radius="md" data-testid="run-nudge" role="region" aria-label={t('title')}>
      <Group justify="space-between" align={phone ? 'stretch' : 'center'} gap="md" wrap={phone ? 'wrap' : 'nowrap'}>
        <Stack gap={2} style={{ minWidth: 0, flex: 1 }}>
          <Text fw={650}>{t('title')}</Text>
          <Text size="sm" c="dimmed">{t('body', { project: projectName })}</Text>
        </Stack>
        <Group gap="xs" wrap="nowrap" style={phone ? { width: '100%' } : undefined}>
          <Button
            component={Link}
            href={`/projects/${projectId}?tab=workflows`}
            fullWidth={phone}
            style={{ minHeight: TOUCH_TARGET_PX, flex: phone ? 1 : undefined }}
          >
            {t('cta')}
          </Button>
          <CloseButton
            size={TOUCH_TARGET_PX}
            aria-label={t('dismiss')}
            onClick={() => {
              dismissRunNudge(userKey, projectId);
              setGone(true);
            }}
          />
        </Group>
      </Group>
    </Paper>
  );
}

/** The dashboard's version: picks the project itself. */
export function DashboardRunNudge({
  overview,
  projects,
}: {
  overview: DashboardOverview | undefined;
  projects: readonly { id: string; name: string }[] | undefined;
}) {
  const { user } = useAuth();
  const userKey = userKeyFromEmail(user?.email);
  const appsEnabled = useWorkflowAppsEnabled();
  const picked = appsEnabled ? nudgeProject(overview, (id) => isRunNudgeDismissed(userKey, id)) : null;
  const name = picked ? projects?.find((p) => p.id === picked.projectId)?.name : undefined;
  if (!picked || !name) return null;
  return <RunNudge key={picked.projectId} projectId={picked.projectId} projectName={name} />;
}
