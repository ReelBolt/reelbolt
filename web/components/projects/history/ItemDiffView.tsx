'use client';

import { Alert, Box, Loader, Stack, Text } from '@mantine/core';
import { VersionDiff } from '@/components/design-system/VersionDiff';
import { formatBytes } from '@/lib/utils/bytes';
import { useProjectHistoryItemDiff } from '@/lib/hooks/use-project-history';
import type { ProjectHistoryItemDiff } from '@/lib/types/project-history';
import { useFormat, useT } from '@/i18n';
import { HunkView } from './HunkView';

function emptyOr(value: string | null | undefined, empty: string): string {
  return value && value.trim() ? value : empty;
}

/** The body of one diff, picked by its mode. */
export function ItemDiffBody({ diff }: { diff: ProjectHistoryItemDiff }) {
  const t = useT('projectHistory');
  const fm = useFormat();
  const sizes = {
    from: formatBytes(diff.fromSize ?? 0, fm),
    to: formatBytes(diff.toSize ?? 0, fm),
  };
  const stepLabel = (s: { order: number; type: string | null; agentName: string | null }) =>
    s.type && s.agentName
      ? t('diff.stepWithAgent', { order: s.order, type: s.type, agent: s.agentName })
      : s.type
        ? t('diff.step', { order: s.order, type: s.type })
        : t('diff.stepUnknown', { order: s.order });

  switch (diff.mode) {
    case 'text':
      return <HunkView hunks={diff.hunks ?? []} />;
    case 'binary':
      return <Text size="sm">{t('diff.binary', sizes)}</Text>;
    case 'tooLarge':
      return <Text size="sm">{t('diff.tooLarge', sizes)}</Text>;
    case 'unavailable':
      return <Text size="sm">{t('diff.unavailable')}</Text>;
    case 'workflow': {
      const w = diff.workflow;
      if (!w) break;
      const sections = [
        { title: t('diff.workflowAdded'), lines: w.added.map(stepLabel) },
        { title: t('diff.workflowRemoved'), lines: w.removed.map(stepLabel) },
        {
          title: t('diff.workflowChanged'),
          lines: w.changed.map((s) => `${stepLabel(s)}. ${t('diff.changedFields', { fields: fm.list(s.changedFields) })}`),
        },
      ].filter((s) => s.lines.length > 0);
      if (sections.length === 0) break;
      return (
        <Stack gap="sm">
          {sections.map((s) => (
            <Box key={s.title}>
              <Text size="sm" fw={600}>{s.title}</Text>
              {s.lines.map((line) => <Text key={line} size="sm">{line}</Text>)}
            </Box>
          ))}
        </Stack>
      );
    }
    case 'timeline': {
      const tl = diff.timeline;
      if (!tl) break;
      return (
        <Text size="sm">
          {t('diff.timelineSummary', {
            fromRevision: tl.fromRevision,
            toRevision: tl.toRevision,
            fromTracks: tl.fromSummary.tracks,
            toTracks: tl.toSummary.tracks,
            fromClips: tl.fromSummary.clips,
            toClips: tl.toSummary.clips,
            fromFrames: tl.fromSummary.durationFrames,
            toFrames: tl.toSummary.durationFrames,
          })}
        </Text>
      );
    }
    case 'pronunciation': {
      const p = diff.pronunciation;
      if (!p) break;
      return (
        <Stack gap={4}>
          {p.added.length > 0 && <Text size="sm">{t('diff.pronunciationAdded', { terms: fm.list(p.added) })}</Text>}
          {p.removed.length > 0 && <Text size="sm">{t('diff.pronunciationRemoved', { terms: fm.list(p.removed) })}</Text>}
          {p.changed.length > 0 && <Text size="sm">{t('diff.pronunciationChanged', { terms: fm.list(p.changed) })}</Text>}
        </Stack>
      );
    }
    case 'project': {
      const p = diff.project;
      const empty = t('diff.emptyValue');
      if (!p || (!p.name && !p.description)) break;
      return (
        <Stack gap={4}>
          {p.name && <Text size="sm">{t('diff.projectName', { from: emptyOr(p.name.from, empty), to: emptyOr(p.name.to, empty) })}</Text>}
          {p.description && (
            <Text size="sm">{t('diff.projectDescription', { from: emptyOr(p.description.from, empty), to: emptyOr(p.description.to, empty) })}</Text>
          )}
        </Stack>
      );
    }
    case 'designSystem':
      if (diff.designSystem) {
        return (
          <Stack gap="xs">
            <Text size="sm" fw={600}>{t('diff.designSystem')}</Text>
            <VersionDiff diff={diff.designSystem} />
          </Stack>
        );
      }
      break;
    default:
      break;
  }
  return <Text size="sm" c="dimmed">{t('diff.none')}</Text>;
}

/** Fetches and shows one item's diff against `to` (a version id or `current`). */
export function ItemDiffView({
  projectId, versionId, kind, itemKey, to, name,
}: {
  projectId: string;
  versionId: string;
  kind: string;
  itemKey: string;
  to: string;
  name: string;
}) {
  const t = useT('projectHistory');
  const { data, error, isLoading } = useProjectHistoryItemDiff(projectId, versionId, kind, itemKey, to);
  return (
    <Stack gap="sm" data-testid="item-diff">
      <Text fw={600}>{t('diff.title', { name })}</Text>
      {isLoading && <Loader size="sm" aria-label={t('diff.loading')} />}
      {error && <Alert color="red">{t('diff.failed')}</Alert>}
      {data && <ItemDiffBody diff={data} />}
    </Stack>
  );
}
