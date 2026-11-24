'use client';

import { useState } from 'react';
import { ActionIcon, Button, Checkbox, Group, Menu, Modal, Popover, Radio, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconDots, IconFolderShare, IconTags } from '@tabler/icons-react';
import { projectOrganizationErrorMessage } from '@/lib/api/project-organization';
import { useProjectTagMutations } from '@/lib/hooks/use-project-tags';
import { MAX_TAGS_PER_PROJECT, type ProjectFolder, type ProjectTag } from '@/lib/types/project-organization';
import type { Project } from '@/lib/types/project';
import { useT } from '@/i18n';
import { TagChip } from './TagChip';
import classes from './projects.module.css';

interface Props {
  project: Project;
  tags: ProjectTag[];
  folders: ProjectFolder[];
}

/** Per-card menu: the keyboard and phone path for tagging and filing (drag is only a shortcut). */
export function CardMenu({ project, tags, folders }: Props) {
  const t = useT('projects.organization');
  const tr = useT();
  const { createTag, setOrganization } = useProjectTagMutations();
  const [tagsOpen, setTagsOpen] = useState(false);
  const [moveOpen, setMoveOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const [target, setTarget] = useState<string>(project.folderId ?? '');

  const current = (project.tags ?? []).map((x) => x.id);
  const atCap = current.length >= MAX_TAGS_PER_PROJECT;
  const q = query.trim();
  const shown = tags.filter((x) => x.name.toLowerCase().includes(q.toLowerCase()));
  const exact = tags.some((x) => x.name.toLowerCase() === q.toLowerCase());

  const fail = (title: string, err: unknown) =>
    notifications.show({ title, message: projectOrganizationErrorMessage(err, tr), color: 'red' });

  const saveTags = async (ids: string[]) => {
    setBusy(true);
    try {
      await setOrganization(project.id, { tagIds: ids, folderId: project.folderId ?? null });
    } catch (err) {
      fail(t('tagsPopover.saveFailed'), err);
    } finally {
      setBusy(false);
    }
  };

  const toggle = (id: string, on: boolean) => {
    if (on && atCap) return;
    void saveTags(on ? [...current, id] : current.filter((x) => x !== id));
  };

  const create = async () => {
    if (atCap) return;
    setBusy(true);
    try {
      const made = await createTag({ name: q, color: 'gray' });
      setQuery('');
      await setOrganization(project.id, { tagIds: [...current, made.id], folderId: project.folderId ?? null });
    } catch (err) {
      fail(t('tagsPopover.saveFailed'), err);
    } finally {
      setBusy(false);
    }
  };

  const move = async () => {
    setBusy(true);
    try {
      await setOrganization(project.id, { tagIds: current, folderId: target || null });
      const name = folders.find((f) => f.id === target)?.name;
      notifications.show({ message: name ? t('move.moved', { name }) : t('move.movedOut'), color: 'green' });
      setMoveOpen(false);
    } catch (err) {
      fail(t('move.failed'), err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={classes.cardMenu}>
      <Popover opened={tagsOpen} onChange={setTagsOpen} position="bottom-end" withArrow shadow="md" trapFocus width={280}>
        <Popover.Target>
          <div>
            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <ActionIcon variant="default" size={44} radius="xl" aria-label={t('card.menu', { name: project.name })}>
                  <IconDots size={18} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item leftSection={<IconTags size={16} />} onClick={() => setTagsOpen(true)} style={{ minHeight: 44 }}>
                  {t('card.menuTags')}
                </Menu.Item>
                <Menu.Item
                  leftSection={<IconFolderShare size={16} />}
                  onClick={() => { setTarget(project.folderId ?? ''); setMoveOpen(true); }}
                  style={{ minHeight: 44 }}
                >
                  {t('card.menuMove')}
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </div>
        </Popover.Target>
        <Popover.Dropdown aria-label={t('tagsPopover.title')}>
          <Stack gap="xs">
            <TextInput
              aria-label={t('tagsPopover.find')}
              placeholder={t('tagsPopover.find')}
              value={query}
              maxLength={40}
              data-autofocus
              onChange={(e) => setQuery(e.currentTarget.value)}
              onKeyDown={(e) => { if (e.key === 'Enter' && q && !exact) { e.preventDefault(); void create(); } }}
            />
            {atCap ? <Text size="xs" c="red.8" role="status">{t('tagsPopover.limit', { max: MAX_TAGS_PER_PROJECT })}</Text> : null}
            <Stack gap={4} mah={220} style={{ overflowY: 'auto' }}>
              {tags.length === 0 && !q ? <Text size="xs" c="dimmed">{t('tagsPopover.none')}</Text> : null}
              {shown.map((x) => {
                const on = current.includes(x.id);
                return (
                  <Checkbox
                    key={x.id}
                    checked={on}
                    disabled={busy || (!on && atCap)}
                    onChange={(e) => toggle(x.id, e.currentTarget.checked)}
                    label={<TagChip name={x.name} color={x.color} />}
                    styles={{ root: { minHeight: 36, display: 'flex', alignItems: 'center' } }}
                  />
                );
              })}
              {q && shown.length === 0 && exact === false ? <Text size="xs" c="dimmed">{t('tagsPopover.noMatch')}</Text> : null}
            </Stack>
            {q && !exact ? (
              <Button variant="light" size="sm" disabled={busy || atCap} onClick={() => void create()} style={{ minHeight: 44 }}>
                {t('tagsPopover.create', { name: q })}
              </Button>
            ) : null}
            <Button variant="default" onClick={() => setTagsOpen(false)} style={{ minHeight: 44 }}>{t('tagsPopover.done')}</Button>
          </Stack>
        </Popover.Dropdown>
      </Popover>

      <Modal opened={moveOpen} onClose={() => setMoveOpen(false)} title={t('move.title')} closeButtonProps={{ size: 44 }} centered radius="lg">
        <Stack>
          <Radio.Group value={target} onChange={setTarget} aria-label={t('move.title')}>
            <Stack gap={4}>
              <Radio value="" label={t('move.noFolder')} styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }} />
              {folders.map((f) => (
                <Radio key={f.id} value={f.id} label={f.name} styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }} />
              ))}
            </Stack>
          </Radio.Group>
          {folders.length === 0 ? <Text size="sm" c="dimmed">{t('move.noFolders')}</Text> : null}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setMoveOpen(false)} style={{ minHeight: 44 }}>{t('move.cancel')}</Button>
            <Button loading={busy} onClick={() => void move()} style={{ minHeight: 44 }}>{t('move.move')}</Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
