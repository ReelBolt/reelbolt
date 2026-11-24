'use client';

import React, { useState } from 'react';
import Link from 'next/link';
import { Alert, Anchor, Badge, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { IconBox, IconWand, IconUserCircle } from '@tabler/icons-react';
import { useAvatarWords } from '@/components/avatars/avatar-words';
import { useFormat, useT } from '@/i18n';
import { avatarStatus, isObjectAvatar, statusColor } from '@/lib/avatars/model';
import { subjectGroups, usableSubjects } from '@/lib/avatars/subjects';
import { useAvatarRequirements, useProjectAvatars } from '@/lib/hooks/use-avatars';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import { SubjectClipDialog } from '../subjects/SubjectClipDialog';
import { useSubjectClipJobs, type SubjectClipApi, type SubjectClipJob } from '../subjects/useSubjectClipJob';

export interface SubjectsPanelProps {
  projectId: string;
  /** A view-only editor: clips cannot be made here, and the panel says so instead of offering the button. */
  readOnly?: boolean;
  /** Phone: 44 px controls. */
  touch?: boolean;
  api?: SubjectClipApi;
}

const JOB_COLOR: Record<SubjectClipJob['state'], string> = { queued: 'gray', running: 'blue', ready: 'green', failed: 'red', cancelled: 'gray' };

/**
 * The media pool's "Avatars" tab: the project's people and objects with their status, "Generate clip" (a new
 * video that shows them, made by an AI model and added to the project's media), the clips being made in this
 * session, and the way to the project's Avatars tab where they are added and managed. Nothing here touches the
 * timeline: a finished clip is an ordinary file in the Media tab.
 */
export function SubjectsPanel({ projectId, readOnly = false, touch = false, api }: SubjectsPanelProps) {
  const t = useT('editor-media.subjects');
  const tAv = useT('avatars');
  const f = useFormat();
  const words = useAvatarWords();
  const { data: requirements, error: requirementsError } = useAvatarRequirements(projectId);
  const enabled = Boolean(requirements && !requirementsError);
  const { data: avatars, error, isLoading } = useProjectAvatars(projectId, enabled);
  const { jobs, cancel, dismiss } = useSubjectClipJobs(projectId, api);
  const [dialogOpen, setDialogOpen] = useState(false);

  const groups = subjectGroups(avatars);
  const all = groups.flatMap((g) => g.avatars);
  const usable = usableSubjects(avatars);
  const control = touch ? 44 : undefined;
  const avatarsTab = `/projects/${projectId}?tab=avatars`;
  const namesOf = (job: SubjectClipJob): string => (job.names.length > 0 ? f.list([...job.names]) : t('jobs.unnamed'));

  return (
    <Stack gap="sm" p="xs" data-testid="subjects-panel">
      <Group gap="xs" justify="space-between" wrap="nowrap">
        <Text fw={600} size="sm">{t('title')}</Text>
        <EarlyPreviewLabel />
      </Group>
      <Text size="xs" c="dimmed">{t('intro')}</Text>

      {readOnly ? (
        <Text size="sm" c="dimmed" data-testid="subjects-read-only">{t('readOnly')}</Text>
      ) : usable.length > 0 ? (
        <Button leftSection={<IconWand size={16} />} h={control} style={{ alignSelf: 'flex-start' }} onClick={() => setDialogOpen(true)} data-testid="subjects-generate">
          {t('generate')}
        </Button>
      ) : all.length > 0 ? (
        <Text size="sm" c="dimmed" data-testid="subjects-none-usable">{t('noneUsable')}</Text>
      ) : null}

      {jobs.length > 0 && (
        <Stack gap={6} data-testid="subjects-jobs" role="list" aria-label={t('jobs.title')}>
          <Text size="xs" fw={600} c="dimmed" aria-hidden>{t('jobs.title')}</Text>
          {jobs.map((job) => {
            const active = job.state === 'queued' || job.state === 'running';
            return (
              <Stack key={job.executionId} gap={2} role="listitem" data-testid="subjects-job" data-state={job.state}>
                <Group gap="xs" wrap="nowrap" align="center">
                  {active ? <Loader size="xs" aria-hidden /> : null}
                  <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>{namesOf(job)}</Text>
                  <Badge size="sm" fz="xs" tt="none" variant="light" color={JOB_COLOR[job.state]} role="status" data-testid="subjects-job-state">
                    {t(`jobs.state.${job.state}`)}
                  </Badge>
                  {active && !readOnly ? (
                    <Button size="compact-sm" fz="xs" h={control} miw={control} variant="default" aria-label={t('jobs.cancelNamed', { names: namesOf(job) })} onClick={() => void cancel(job)} data-testid="subjects-job-cancel">
                      {t('jobs.cancel')}
                    </Button>
                  ) : null}
                  {!active ? (
                    <Button size="compact-sm" fz="xs" h={control} miw={control} variant="subtle" color="gray" aria-label={t('jobs.dismissNamed', { names: namesOf(job) })} onClick={() => dismiss(job)} data-testid="subjects-job-dismiss">
                      {t('jobs.dismiss')}
                    </Button>
                  ) : null}
                </Group>
                {job.state === 'failed' ? (
                  <Text size="xs" c="red" role="alert" data-testid="subjects-job-message">{job.message ?? t('jobs.failedFallback')}</Text>
                ) : null}
              </Stack>
            );
          })}
          <Text size="xs" c="dimmed" data-testid="subjects-jobs-note">{t('jobs.note')}</Text>
        </Stack>
      )}

      {error ? <Alert color="red" variant="light" data-testid="subjects-load-error">{t('loadFailed')}</Alert> : null}
      {isLoading ? <Loader size="sm" aria-label={t('loading')} /> : null}

      {!isLoading && !error && avatars && all.length === 0 ? (
        <Stack gap={2} data-testid="subjects-empty">
          <Text size="sm" fw={600}>{t('emptyTitle')}</Text>
          <Text size="xs" c="dimmed">{t('emptyHint')}</Text>
        </Stack>
      ) : null}

      {groups.map((group) => (
        <Stack key={group.kind} gap={4} data-testid={`subjects-group-${group.kind === 'Object' ? 'objects' : 'people'}`}>
          <Text size="xs" fw={600} c="dimmed">{group.kind === 'Object' ? t('objects', { count: group.avatars.length }) : t('people', { count: group.avatars.length })}</Text>
          <Stack gap={4} role="list">
            {group.avatars.map((avatar) => {
              const status = avatarStatus(avatar);
              const object = isObjectAvatar(avatar);
              return (
                <Stack key={avatar.id} gap={0} role="listitem" data-testid="subjects-row" data-usable={avatar.selectable ? 'true' : 'false'}>
                  <Group gap="xs" wrap="nowrap" align="center" style={{ minHeight: touch ? 44 : 28 }}>
                    {object ? <IconBox size={18} stroke={1.4} aria-hidden /> : <IconUserCircle size={18} stroke={1.4} aria-hidden />}
                    <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>{avatar.name}</Text>
                    <Badge size="sm" fz="xs" tt="none" variant="outline" color={object ? 'teal' : 'grape'} data-testid="subjects-kind">
                      {object ? tAv('card.kindObject') : tAv('card.kindPerson')}
                    </Badge>
                    <Badge size="sm" fz="xs" tt="none" variant="light" color={statusColor(status)} data-testid="subjects-status">
                      {words.statusLabel(avatar, status)}
                    </Badge>
                  </Group>
                  {!avatar.selectable ? <Text size="xs" c="dimmed" pl={26}>{words.reasonOf(avatar)}</Text> : null}
                </Stack>
              );
            })}
          </Stack>
        </Stack>
      ))}

      <Anchor component={Link} href={avatarsTab} size="sm" style={touch ? { minHeight: 44, display: 'inline-flex', alignItems: 'center' } : undefined} data-testid="subjects-manage">
        {t('manage')}
      </Anchor>

      <SubjectClipDialog projectId={projectId} opened={dialogOpen && !readOnly} onClose={() => setDialogOpen(false)} avatars={avatars} touch={touch} api={api} />
    </Stack>
  );
}
