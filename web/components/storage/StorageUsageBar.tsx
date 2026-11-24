'use client';

import { Progress, Stack, Text } from '@mantine/core';
import { formatBytes } from '@/lib/utils/bytes';
import { useT } from '@/i18n';
import { storageFraction, storageUsageSentence, type ProjectStorageUsage, type StorageUsage } from '@/lib/types/storage';

/**
 * "4.2 GB of 15 GB used" with a bar, and the largest projects underneath
 * (docs/design/local-folder-sync-and-storage.md Part 2, "Shown to people"). Used by the Billing page
 * and by Settings, so the two can never disagree about the figure.
 *
 * `limitBytes` null is a plan with no storage limit: the bar is not a fraction of anything, so it is
 * drawn as a plain "Unlimited" bar rather than as 100 % full.
 */
export function StorageUsageBar({
  usage,
  withProjects = false,
  testId = 'storage-usage',
}: {
  usage: StorageUsage;
  /** Also list the top projects, as `GET /api/v1/storage/usage` returns them (largest first). */
  withProjects?: boolean;
  testId?: string;
}) {
  const t = useT('outputs.usage');
  const unlimited = usage.limitBytes === null;
  const limitBytes = usage.limitBytes;
  const fraction = storageFraction(usage);
  const percent = Math.round(fraction * 100);
  const nearlyFull = !unlimited && fraction >= 0.9;

  return (
    <Stack gap={6} data-testid={testId}>
      <Text size="sm" fw={600}>
        {storageUsageSentence(usage)}
      </Text>
      <Progress
        value={unlimited ? percent : percent}
        color={nearlyFull ? 'red' : undefined}
        size="md"
        radius="xl"
        aria-label={unlimited ? t('ariaUnlimited', { used: formatBytes(usage.usedBytes) }) : t('ariaPercent', { percent })}
        data-testid="storage-usage-bar"
      />
      {unlimited ? (
        <Text size="xs" c="dimmed">
          {t('unlimited')}
        </Text>
      ) : (
        <Text size="xs" c="dimmed">
          {t('free', { free: formatBytes(limitBytes! - usage.usedBytes), total: formatBytes(limitBytes!) })}
        </Text>
      )}

      {withProjects && usage.byProject.length > 0 && (
        <Stack gap={2} mt={4} data-testid="storage-top-projects">
          <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
            {t('largest')}
          </Text>
          {usage.byProject.map((project) => (
            <ProjectLine key={project.projectId} project={project} />
          ))}
        </Stack>
      )}
    </Stack>
  );
}

function ProjectLine({ project }: { project: ProjectStorageUsage }) {
  return (
    <Text size="sm" style={{ display: 'flex', justifyContent: 'space-between', gap: 12 }}>
      <span style={{ overflowWrap: 'anywhere' }}>{project.name}</span>
      <span style={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>{formatBytes(project.usedBytes)}</span>
    </Text>
  );
}
