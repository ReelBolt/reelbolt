'use client';

import { Button, Group, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconFolder, IconFolders, IconInbox, IconSettings } from '@tabler/icons-react';
import type { ProjectFolder, ProjectTag } from '@/lib/types/project-organization';
import type { FolderSelection } from '@/lib/projects/filter';
import { TagChip } from './TagChip';
import { useT } from '@/i18n';
import classes from './projects.module.css';

export const PROJECT_DRAG_TYPE = 'application/x-reelbolt-project';

interface Props {
  folders: ProjectFolder[];
  tags: ProjectTag[];
  totalCount: number;
  unfiledCount: number;
  folder: FolderSelection;
  tagIds: string[];
  dropTarget: string | null;
  onFolder: (f: FolderSelection) => void;
  onToggleTag: (id: string) => void;
  onClearTags: () => void;
  onManage: () => void;
  onDropOnFolder: (folderId: string | null, projectId: string) => void;
  onDropTarget: (id: string | null) => void;
}

export function ProjectRail({
  folders, tags, totalCount, unfiledCount, folder, tagIds, dropTarget,
  onFolder, onToggleTag, onClearTags, onManage, onDropOnFolder, onDropTarget,
}: Props) {
  const t = useT('projects.organization.rail');

  const item = (key: string, value: FolderSelection, label: string, count: number, icon: React.ReactNode, dropId: string | null | undefined) => {
    const active = folder === value;
    const droppable = dropId !== undefined;
    return (
      <UnstyledButton
        key={key}
        className={classes.railItem}
        data-active={active || undefined}
        data-drop={dropTarget === key || undefined}
        aria-current={active ? 'true' : undefined}
        onClick={() => onFolder(value)}
        onDragOver={droppable ? (e: React.DragEvent) => {
          if (!e.dataTransfer.types.includes(PROJECT_DRAG_TYPE)) return;
          e.preventDefault();
          onDropTarget(key);
        } : undefined}
        onDragLeave={droppable ? () => onDropTarget(null) : undefined}
        onDrop={droppable ? (e: React.DragEvent) => {
          const id = e.dataTransfer.getData(PROJECT_DRAG_TYPE);
          onDropTarget(null);
          if (id) { e.preventDefault(); onDropOnFolder(dropId, id); }
        } : undefined}
      >
        {icon}
        <span className={classes.railLabel}>{label}</span>
        <span className={classes.railCount}>{count}</span>
      </UnstyledButton>
    );
  };

  return (
    <nav className={classes.rail} aria-label={t('label')}>
      <Stack gap={2}>
        {item('all', 'all', t('all'), totalCount, <IconFolders size={16} aria-hidden />, undefined)}
        {item('unfiled', 'unfiled', t('unfiled'), unfiledCount, <IconInbox size={16} aria-hidden />, null)}
        <Text size="xs" fw={700} c="dimmed" tt="uppercase" mt="sm" mb={2}>{t('folders')}</Text>
        {folders.length === 0 ? <Text size="xs" c="dimmed">{t('noFolders')}</Text> : null}
        {folders.map((f) => item(f.id, f.id, f.name, f.projectCount, <IconFolder size={16} aria-hidden />, f.id))}
      </Stack>
      <Stack gap="xs" mt="md">
        <Group justify="space-between">
          <Text size="xs" fw={700} c="dimmed" tt="uppercase">{t('tags')}</Text>
          {tagIds.length > 0 ? <Button variant="subtle" size="compact-xs" onClick={onClearTags}>{t('clearTags')}</Button> : null}
        </Group>
        {tags.length === 0 ? <Text size="xs" c="dimmed">{t('noTags')}</Text> : (
          <>
            <Text size="xs" c="dimmed">{t('tagHint')}</Text>
            <div className={classes.chipRow}>
              {tags.map((tag) => (
                <TagChip key={tag.id} name={tag.name} color={tag.color} selected={tagIds.includes(tag.id)} onClick={() => onToggleTag(tag.id)} />
              ))}
            </div>
          </>
        )}
        <Button variant="default" leftSection={<IconSettings size={16} />} onClick={onManage} style={{ minHeight: 44 }}>{t('manage')}</Button>
      </Stack>
    </nav>
  );
}
