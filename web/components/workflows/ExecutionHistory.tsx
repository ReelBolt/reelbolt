'use client';

import { Stack, Card, Group, Text, Loader, Center, Badge, Tooltip } from '@mantine/core';
import { useExecutions } from '@/lib/hooks/use-executions';
import { StatusBadge } from '@/components/projects/StatusBadge';
import { formatDate, formatDurationLong } from '@/lib/utils/format';
import { EmptyState } from '@/components/shared/EmptyState';
import Link from 'next/link';
import { useT } from '@/i18n';
import { RUN_FORMAT_OPTIONS, resolutionLabel } from '@/components/styles/style-options';
import { executionComputeChip } from '@/lib/utils/compute-target';
import { runInputsSummary } from '@/components/workflow-apps/run-app-inputs';

interface ExecutionHistoryProps {
  projectId: string;
  workflowId: string;
  /**
   * The desktop builder's narrow rail. The wide row does not fit 380px: its badges, brief and date
   * are a single `nowrap` line built for the full page width, so the rail gets a stacked card that
   * wraps and clamps instead of overflowing sideways.
   */
  compact?: boolean;
}

/** First line of a brief, shortened for a one-line list row. */
function briefPreview(brief: string | null | undefined): string | null {
  const line = brief?.trim().split('\n')[0]?.trim();
  if (!line) return null;
  return line.length > 90 ? `${line.slice(0, 87)}…` : line;
}

export function ExecutionHistory({ projectId, workflowId, compact = false }: ExecutionHistoryProps) {
  const t = useT('execution.history');
  const tRoot = useT();
  const { data: executions, isLoading } = useExecutions(projectId, workflowId);

  if (isLoading) return <Center py="md"><Loader size="sm" /></Center>;
  if (!executions || executions.length === 0) {
    return <EmptyState title={t('emptyTitle')} description={t('emptyDescription')} />;
  }

  // Runs are numbered oldest-first ("Run 1" is the first one ever), whatever order the list is in.
  // A run that has not started yet (no startedAt) is the newest one.
  const ordered = [...executions].sort((a, b) => (a.startedAt ?? '￿').localeCompare(b.startedAt ?? '￿'));
  const runNumber = new Map(ordered.map((e, i) => [e.id, i + 1]));

  return (
    <Stack gap="sm">
      <Text fw={600} size="sm">{t('heading')}</Text>
      {executions.map((exec) => {
        const duration = exec.startedAt && exec.completedAt
          ? new Date(exec.completedAt).getTime() - new Date(exec.startedAt).getTime()
          : null;
        const brief = briefPreview(exec.userRequest);
        // An app run's own fields in words (docs/design/app-inputs.md 10.4): the first two, labelled by its own page.
        const fields = runInputsSummary(tRoot, exec.appInputs);
        // E8-UI: shown only when a step of this run actually ran on a machine the workspace
        // paired. The list carries each step's status, timing and compute target (but none of its
        // output — WorkflowsController.ListExecutions), so the chip needs no extra request.
        const computeChip = executionComputeChip(exec);
        const hasBadges = Boolean(exec.styleName || exec.outputFormat || exec.outputResolution || computeChip);
        const hasTiming = exec.iterationCount > 0 || duration !== null;
        const started = exec.startedAt ? formatDate(exec.startedAt) : t('starting');

        const badges = (
          <>
            {exec.styleName && <Badge size="xs" variant="light">{exec.styleName}</Badge>}
            {exec.outputFormat && (
              <Badge size="xs" variant="outline">
                {RUN_FORMAT_OPTIONS.find((o) => o.value === exec.outputFormat)?.label ?? exec.outputFormat}
              </Badge>
            )}
            {exec.outputResolution && (
              <Badge size="xs" variant="outline" color="grape">
                {resolutionLabel(exec.outputResolution)}
              </Badge>
            )}
            {computeChip && (
              <Tooltip label={computeChip.tooltip} multiline maw={300} openDelay={200}>
                <Badge size="xs" variant="light" color={computeChip.color} style={{ cursor: 'help' }}>
                  {computeChip.label}
                </Badge>
              </Tooltip>
            )}
          </>
        );

        return (
          <Card
            key={exec.id}
            component={Link}
            href={`/projects/${projectId}/workflows/${workflowId}/executions/${exec.id}`}
            withBorder
            padding="sm"
            radius="md"
            data-layout={compact ? 'compact' : 'wide'}
            // A long badge row or brief must never push the card wider than its column.
            style={{ textDecoration: 'none', cursor: 'var(--rb-cursor-pointer)', overflow: 'hidden' }}
          >
            {compact ? (
              <Stack gap={6}>
                {/* Wraps: in a narrow pane the date drops under the badge instead of printing over it. */}
                <Group justify="space-between" wrap="wrap" gap="xs">
                  <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
                    <StatusBadge status={exec.status} needsAttention={exec.needsAttention} />
                    <Text size="sm" fw={500} truncate>{t('runNumber', { number: runNumber.get(exec.id) ?? '' })}</Text>
                  </Group>
                  <Text size="xs" c="dimmed" style={{ flex: 'none' }}>{started}</Text>
                </Group>
                {hasBadges && <Group gap={4} wrap="wrap">{badges}</Group>}
                {fields && <Text size="xs" lineClamp={2} aria-label={`${tRoot('apps.run.history.label')}: ${fields}`} data-testid="run-fields">{fields}</Text>}
                {brief && <Text size="xs" c="dimmed" lineClamp={2}>{brief}</Text>}
                {hasTiming && (
                  <Group gap="sm" wrap="wrap">
                    {exec.iterationCount > 0 && (
                      <Text size="xs" c="dimmed">
                        {t('retries', { count: exec.iterationCount })}
                      </Text>
                    )}
                    {duration !== null && <Text size="xs" c="dimmed">{formatDurationLong(duration)}</Text>}
                  </Group>
                )}
              </Stack>
            ) : (
              <Group justify="space-between" wrap="nowrap">
                <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
                  <StatusBadge status={exec.status} needsAttention={exec.needsAttention} />
                  <Text size="sm" fw={500}>{t('runNumber', { number: runNumber.get(exec.id) ?? '' })}</Text>
                  {badges}
                  {fields && <Text size="xs" truncate aria-label={`${tRoot('apps.run.history.label')}: ${fields}`} data-testid="run-fields">{fields}</Text>}
                  {brief && <Text size="xs" c="dimmed" truncate>{brief}</Text>}
                </Group>
                <Group gap="sm" wrap="nowrap">
                  {exec.iterationCount > 0 && (
                    <Text size="xs" c="dimmed">{t('retries', { count: exec.iterationCount })}</Text>
                  )}
                  {duration !== null && (
                    <Text size="xs" c="dimmed">{formatDurationLong(duration)}</Text>
                  )}
                  <Text size="xs" c="dimmed">{started}</Text>
                </Group>
              </Group>
            )}
          </Card>
        );
      })}
    </Stack>
  );
}
