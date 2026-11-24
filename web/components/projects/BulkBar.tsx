'use client';

import { useState } from 'react';
import { Button, Group, Select, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { projectOrganizationErrorMessage } from '@/lib/api/project-organization';
import { useProjectTagMutations } from '@/lib/hooks/use-project-tags';
import { MAX_BULK_PROJECTS, type OrganizeProjectsBody, type ProjectFolder, type ProjectTag } from '@/lib/types/project-organization';
import { useT } from '@/i18n';
import classes from './projects.module.css';

type Action = 'tag' | 'removeTag' | 'move';

interface Props {
  selectedIds: string[];
  tags: ProjectTag[];
  folders: ProjectFolder[];
  onDone: () => void;
}

/** Bulk organize: one request, all or nothing, one toast with the result. */
export function BulkBar({ selectedIds, tags, folders, onDone }: Props) {
  const t = useT('projects.organization.bulk');
  const tr = useT();
  const { organize } = useProjectTagMutations();
  const [action, setAction] = useState<Action | null>(null);
  const [choice, setChoice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const count = selectedIds.length;
  const tooMany = count > MAX_BULK_PROJECTS;

  const apply = async (body: Omit<OrganizeProjectsBody, 'projectIds' | 'addTagIds' | 'removeTagIds'> & Partial<OrganizeProjectsBody>) => {
    setBusy(true);
    try {
      await organize({ addTagIds: [], removeTagIds: [], ...body, projectIds: selectedIds });
      notifications.show({ message: t('done', { count }), color: 'green' });
      setAction(null);
      setChoice(null);
      onDone();
    } catch (err) {
      notifications.show({ message: t('failed', { message: projectOrganizationErrorMessage(err, tr) }), color: 'red' });
    } finally {
      setBusy(false);
    }
  };

  const submit = () => {
    if (!choice) return;
    if (action === 'tag') void apply({ addTagIds: [choice] });
    else if (action === 'removeTag') void apply({ removeTagIds: [choice] });
    else if (action === 'move') void apply({ folderId: choice });
  };

  const data = action === 'move'
    ? folders.map((f) => ({ value: f.id, label: f.name }))
    : tags.map((x) => ({ value: x.id, label: x.name }));

  const start = (a: Action) => { setAction(a); setChoice(null); };

  return (
    <div className={classes.bulkBar} role="region" aria-label={t('enter')}>
      <Group gap="xs">
        <Text size="sm" fw={600} aria-live="polite">{count === 0 ? t('none') : t('selected', { count })}</Text>
        {tooMany ? <Text size="sm" c="red.8" role="alert">{t('tooMany', { max: MAX_BULK_PROJECTS })}</Text> : null}
      </Group>
      <Group gap="xs" mt="xs">
        <Button variant="default" disabled={count === 0 || tooMany} onClick={() => start('tag')} style={{ minHeight: 44 }}>{t('tag')}</Button>
        <Button variant="default" disabled={count === 0 || tooMany} onClick={() => start('removeTag')} style={{ minHeight: 44 }}>{t('removeTag')}</Button>
        <Button variant="default" disabled={count === 0 || tooMany} onClick={() => start('move')} style={{ minHeight: 44 }}>{t('move')}</Button>
        <Button variant="default" disabled={count === 0 || tooMany} loading={busy && action === null} onClick={() => void apply({ clearFolder: true })} style={{ minHeight: 44 }}>{t('clearFolder')}</Button>
      </Group>
      {action ? (
        <Group gap="xs" mt="xs" align="flex-end">
          <Select
            aria-label={action === 'move' ? t('pickFolder') : t('pickTag')}
            placeholder={action === 'move' ? t('pickFolder') : t('pickTag')}
            data={data}
            value={choice}
            onChange={setChoice}
            size="md"
            nothingFoundMessage={action === 'move' ? undefined : t('noTags')}
          />
          <Button disabled={!choice} loading={busy} onClick={submit} style={{ minHeight: 44 }}>{t('apply')}</Button>
        </Group>
      ) : null}
    </div>
  );
}
