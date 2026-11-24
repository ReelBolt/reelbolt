'use client';

import Link from 'next/link';
import { Badge, Checkbox, Text, VisuallyHidden } from '@mantine/core';
import { IconFileMusic, IconMovie, IconPhoto, IconPlayerPlayFilled, IconTopologyRing, IconVideo } from '@tabler/icons-react';
import { VideoThumb } from '@/components/outputs/VideoThumb';
import { getOutputPreviewUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { outputMediaKind } from '@/lib/outputs/media-kind';
import { timeAgo } from '@/lib/files/model';
import { formatBytes } from '@/lib/utils/bytes';
import type { ProjectOverview } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';
import type { ProjectFolder, ProjectTag } from '@/lib/types/project-organization';
import { CardMenu } from './CardMenu';
import { PROJECT_DRAG_TYPE } from './ProjectRail';
import { TagChip } from './TagChip';
import { useFormat, useT } from '@/i18n';
import classes from './projects.module.css';

const MAX_CHIPS = 3;

/** Everything the card needs to tag, file, select and drag; absent = a plain read-only card. */
export interface CardOrganization {
  tags: ProjectTag[];
  folders: ProjectFolder[];
  showFolderCrumb: boolean;
  selecting: boolean;
  selected: boolean;
  onToggleSelect: () => void;
  onDragStart: () => void;
}

export function ProjectCard({ project, overview, organization }: {
  project: Project;
  overview?: ProjectOverview;
  organization?: CardOrganization;
}) {
  const t = useT('projects.card');
  const tr = useT();
  const fm = useFormat();
  const o = overview;
  const empty = !!o && o.sourceFileCount === 0 && o.workflowCount === 0 && o.renderCount === 0;
  const running = (o?.activeExecutionCount ?? 0) > 0;

  const org = organization;
  const chips = project.tags ?? [];
  const folderName = org?.showFolderCrumb && project.folderId ? org.folders.find((f) => f.id === project.folderId)?.name : undefined;

  return (
    <div
      className={`${classes.cardWrap} ${org?.selected ? classes.cardSelected : ''}`}
      draggable={!!org && !org.selecting}
      onDragStart={org && !org.selecting ? (e) => {
        e.dataTransfer.setData(PROJECT_DRAG_TYPE, project.id);
        e.dataTransfer.effectAllowed = 'move';
        org.onDragStart();
      } : undefined}
      data-testid="project-card"
    >
      {org?.selecting ? (
        <div className={classes.cardSelect}>
          <Checkbox
            checked={org.selected}
            onChange={org.onToggleSelect}
            aria-label={t('selectProject', { name: project.name })}
            size="md"
          />
        </div>
      ) : org ? <CardMenu project={project} tags={org.tags} folders={org.folders} /> : null}
    <Link
      href={`/projects/${encodeURIComponent(project.id)}`}
      className={classes.card}
      draggable={false}
      onClick={org?.selecting ? (e) => { e.preventDefault(); org.onToggleSelect(); } : undefined}
    >
      <div className={classes.media}>
        {o?.latestOutput ? (
          <VideoThumb src={getOutputVideoUrl(project.id, o.latestOutput.stepResultId)} fileName={o.latestOutput.fileName} imageSrc={outputMediaKind(o.latestOutput.fileName) === 'image' ? getOutputPreviewUrl(project.id, o.latestOutput.stepResultId, 320) : undefined} />
        ) : (
          <div className={classes.placeholder} aria-hidden>
            <IconMovie size={34} stroke={1.3} />
          </div>
        )}
        {running && (
          <div className={classes.badgeSlot}>
            <Badge variant="filled" color="blue" size="sm" leftSection={<IconPlayerPlayFilled size={10} />}>
              {t('running', { count: o!.activeExecutionCount })}
            </Badge>
          </div>
        )}
      </div>
      <div className={classes.body}>
        <div>
          <Text fw={650} lineClamp={1} style={{ letterSpacing: '-0.01em' }}>{project.name}</Text>
          <Text size="sm" c="dimmed" lineClamp={2} mih={40}>
            {project.description || (empty ? t('emptyHint') : t('noDescription'))}
          </Text>
        </div>
        {folderName || chips.length > 0 ? (
          <div className={classes.cardTags}>
            {folderName ? <Text size="xs" c="dimmed" w="100%" lineClamp={1}>{t('folderCrumb', { name: folderName })}</Text> : null}
            {chips.slice(0, MAX_CHIPS).map((c) => <TagChip key={c.id} name={c.name} color={c.color} />)}
            {chips.length > MAX_CHIPS ? (
              <Text size="xs" c="dimmed" component="span" title={chips.slice(MAX_CHIPS).map((c) => c.name).join(', ')} aria-label={t('moreTagsLabel', { count: chips.length - MAX_CHIPS })}>
                {t('moreTags', { count: chips.length - MAX_CHIPS })}
              </Text>
            ) : null}
          </div>
        ) : null}
        {o ? (
          <div className={classes.stats} aria-label={t('contents')}>
            <span className={classes.stat} title={t('videos')}><IconVideo size={14} aria-hidden /> {o.videoCount}<VisuallyHidden> {t('videosHidden')}</VisuallyHidden></span>
            <span className={classes.stat} title={t('audio')}><IconFileMusic size={14} aria-hidden /> {o.audioCount}<VisuallyHidden> {t('audioHidden')}</VisuallyHidden></span>
            <span className={classes.stat} title={t('images')}><IconPhoto size={14} aria-hidden /> {o.imageCount}<VisuallyHidden> {t('imagesHidden')}</VisuallyHidden></span>
            <span className={classes.stat} title={t('workflows')}><IconTopologyRing size={14} aria-hidden /> {o.workflowCount}<VisuallyHidden> {t('workflowsHidden')}</VisuallyHidden></span>
            <span className={classes.stat} title={t('renders')}><IconMovie size={14} aria-hidden /> {o.renderCount}<VisuallyHidden> {t('rendersHidden')}</VisuallyHidden></span>
          </div>
        ) : null}
        {o ? <Text size="xs" c="dimmed" data-testid="project-card-legend">{t('legend')}</Text> : null}
        <Text size="xs" c="dimmed">
          {t('active', { when: timeAgo(o?.lastActivityAt ?? project.updatedAt, undefined, tr, fm.locale) })}
          {o && o.totalBytes > 0 ? ` - ${formatBytes(o.totalBytes, fm)}` : ''}
        </Text>
      </div>
    </Link>
    </div>
  );
}
