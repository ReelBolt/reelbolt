'use client';

import { Badge, Group, Text } from '@mantine/core';
import { getOutputPreviewUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { timeAgo } from '@/lib/files/model';
import { formatVideoLength, renderStage, renderTitle } from '@/lib/utils/run-summary';
import { outputMediaKind } from '@/lib/outputs/media-kind';
import { useT } from '@/i18n';
import { VideoThumb } from './VideoThumb';
import type { PlayableOutput } from './OutputPlayer';
import classes from './outputs.module.css';

/** Swipeable, snap-scrolling rail of render thumbnails. Pure selection: the parent decides what a tap does. */
export function OutputRail({
  outputs,
  activeId,
  onSelect,
  showProject = false,
}: {
  outputs: PlayableOutput[];
  activeId?: string | null;
  onSelect: (o: PlayableOutput) => void;
  showProject?: boolean;
}) {
  const t = useT('outputs');
  return (
    <div className={classes.rail} role="list" aria-label={t('rail.label')}>
      {outputs.map((o) => {
        const stage = outputMediaKind(o.fileName) === 'image' ? null : renderStage(o);
        const title = renderTitle(o);
        const length = formatVideoLength(o.durationSec);
        const stageLabel = stage === 'final' ? t('stage.final') : stage === 'draft' ? t('stage.draft') : t('stage.inProgress');
        return (
        <button
          key={o.stepResultId}
          type="button"
          role="listitem"
          className={classes.railItem}
          data-active={o.stepResultId === activeId}
          onClick={() => onSelect(o)}
          aria-label={stage ? t('rail.playStage', { title, stage: stageLabel }) : t('rail.play', { title })}
        >
          <VideoThumb src={getOutputVideoUrl(o.projectId, o.stepResultId)} fileName={o.fileName} imageSrc={outputMediaKind(o.fileName) === 'image' ? getOutputPreviewUrl(o.projectId, o.stepResultId, 320) : undefined} />
          <div>
            <Text size="sm" fw={600} lineClamp={1}>{title}</Text>
            <Group gap={6} wrap="nowrap">
              {stage && (
                <Badge size="xs" variant={stage === 'final' ? 'filled' : 'light'} color={stage === 'final' ? 'green' : 'gray'} style={{ flexShrink: 0 }}>
                  {stageLabel}
                </Badge>
              )}
              <Text size="xs" c="dimmed" lineClamp={1}>
                {showProject && o.projectName ? `${o.projectName} - ` : ''}{timeAgo(o.producedAt)}{length ? ` · ${length}` : ''}
              </Text>
            </Group>
          </div>
        </button>
        );
      })}
    </div>
  );
}
