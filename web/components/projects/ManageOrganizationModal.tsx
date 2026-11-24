'use client';

import { useState } from 'react';
import { ActionIcon, Button, Group, Modal, Select, Stack, Tabs, Text, TextInput } from '@mantine/core';
import { useIsPhoneClass } from '@/lib/hooks/use-device-class';
import { notifications } from '@mantine/notifications';
import { IconCheck, IconTrash } from '@tabler/icons-react';
import { msg, type Msg } from '@/i18n/msg';
import { projectOrganizationErrorMessage } from '@/lib/api/project-organization';
import { useProjectFolderMutations } from '@/lib/hooks/use-project-folders';
import { useProjectTagMutations } from '@/lib/hooks/use-project-tags';
import { TAG_COLORS, type ProjectFolder, type ProjectTag, type TagColor } from '@/lib/types/project-organization';
import { useT } from '@/i18n';
import { TagChip } from './TagChip';

const COLOUR_MSG: Record<TagColor, Msg> = {
  gray: msg('projects.organization.manage.colours.gray'),
  red: msg('projects.organization.manage.colours.red'),
  orange: msg('projects.organization.manage.colours.orange'),
  yellow: msg('projects.organization.manage.colours.yellow'),
  green: msg('projects.organization.manage.colours.green'),
  teal: msg('projects.organization.manage.colours.teal'),
  blue: msg('projects.organization.manage.colours.blue'),
  indigo: msg('projects.organization.manage.colours.indigo'),
  violet: msg('projects.organization.manage.colours.violet'),
  pink: msg('projects.organization.manage.colours.pink'),
};

const omit = (o: Record<string, string>, k: string) => Object.fromEntries(Object.entries(o).filter(([key]) => key !== k));

const asColour = (c: string): TagColor => ((TAG_COLORS as readonly string[]).includes(c) ? (c as TagColor) : 'gray');

interface Props {
  opened: boolean;
  onClose: () => void;
  tags: ProjectTag[];
  folders: ProjectFolder[];
}

export function ManageOrganizationModal({ opened, onClose, tags, folders }: Props) {
  const t = useT('projects.organization.manage');
  const tr = useT();
  const isPhone = useIsPhoneClass();
  const tagApi = useProjectTagMutations();
  const folderApi = useProjectFolderMutations();
  const [newTag, setNewTag] = useState('');
  const [newFolder, setNewFolder] = useState('');
  const [confirm, setConfirm] = useState<{ kind: 'tag' | 'folder'; id: string } | null>(null);
  const [names, setNames] = useState<Record<string, string>>({});

  const run = async (work: () => Promise<unknown>) => {
    try {
      await work();
      return true;
    } catch (err) {
      notifications.show({ title: t('failed'), message: projectOrganizationErrorMessage(err, tr), color: 'red' });
      return false;
    }
  };

  const nameFor = (id: string, fallback: string) => names[id] ?? fallback;
  const commitName = async (id: string, original: string, save: (name: string) => Promise<unknown>) => {
    const next = nameFor(id, original).trim();
    if (!next || next === original) {
      setNames((n) => omit(n, id));
      return;
    }
    if (await run(() => save(next))) setNames((n) => omit(n, id));
  };

  const confirmBox = (kind: 'tag' | 'folder', id: string, name: string, count: number, remove: () => Promise<unknown>) =>
    confirm?.kind === kind && confirm.id === id ? (
      <Stack gap="xs" p="sm" role="alertdialog" aria-label={t('delete')}>
        <Text size="sm">{kind === 'tag' ? t('deleteTag', { name, count }) : t('deleteFolder', { name, count })}</Text>
        <Group>
          <Button color="red" onClick={async () => { if (await run(remove)) setConfirm(null); }} style={{ minHeight: 44 }}>{t('confirmDelete')}</Button>
          <Button variant="default" onClick={() => setConfirm(null)} style={{ minHeight: 44 }}>{t('cancel')}</Button>
        </Group>
      </Stack>
    ) : null;

  return (
    <Modal opened={opened} onClose={onClose} title={t('title')} size="lg" closeButtonProps={{ size: 44, 'aria-label': t('close') }} centered={!isPhone} fullScreen={!!isPhone} radius="lg">
      <Tabs defaultValue="tags" keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="tags" style={{ minHeight: 44 }}>{t('tabTags')}</Tabs.Tab>
          <Tabs.Tab value="folders" style={{ minHeight: 44 }}>{t('tabFolders')}</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="tags">
          <Stack>
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                const name = newTag.trim();
                if (name && (await run(() => tagApi.createTag({ name, color: 'gray' })))) setNewTag('');
              }}
            >
              <Group align="flex-end" wrap="nowrap">
                <TextInput style={{ flex: 1 }} aria-label={t('newTag')} placeholder={t('newTag')} maxLength={40} value={newTag} onChange={(e) => setNewTag(e.currentTarget.value)} size="md" />
                <Button type="submit" disabled={!newTag.trim()} style={{ minHeight: 44 }}>{t('add')}</Button>
              </Group>
            </form>
            {tags.length === 0 ? <Text size="sm" c="dimmed">{t('empty')}</Text> : null}
            {tags.map((tag) => (
              <Stack key={tag.id} gap={4}>
                <Group wrap="nowrap" align="center">
                  <TextInput
                    style={{ flex: 1 }}
                    aria-label={t('nameOf', { name: tag.name })}
                    value={nameFor(tag.id, tag.name)}
                    maxLength={40}
                    onChange={(e) => setNames((n) => ({ ...n, [tag.id]: e.currentTarget.value }))}
                    onBlur={() => void commitName(tag.id, tag.name, (name) => tagApi.updateTag(tag.id, { name }))}
                    onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); e.currentTarget.blur(); } }}
                    size="md"
                  />
                  <Select
                    w={140}
                    size="md"
                    allowDeselect={false}
                    aria-label={t('colourOf', { name: tag.name })}
                    value={asColour(tag.color)}
                    data={TAG_COLORS.map((c) => ({ value: c, label: tr.msg(COLOUR_MSG[c]) }))}
                    onChange={(c) => { if (c) void run(() => tagApi.updateTag(tag.id, { color: c as TagColor })); }}
                  />
                  <ActionIcon variant="default" size={44} aria-label={`${t('delete')}: ${tag.name}`} onClick={() => setConfirm({ kind: 'tag', id: tag.id })}>
                    <IconTrash size={18} />
                  </ActionIcon>
                </Group>
                <Group gap="xs"><TagChip name={tag.name} color={tag.color} /><Text size="xs" c="dimmed">{t('projects', { count: tag.projectCount })}</Text></Group>
                {confirmBox('tag', tag.id, tag.name, tag.projectCount, () => tagApi.deleteTag(tag.id))}
              </Stack>
            ))}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="folders">
          <Stack>
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                const name = newFolder.trim();
                if (name && (await run(() => folderApi.createFolder({ name })))) setNewFolder('');
              }}
            >
              <Group align="flex-end" wrap="nowrap">
                <TextInput style={{ flex: 1 }} aria-label={t('newFolder')} placeholder={t('newFolder')} maxLength={60} value={newFolder} onChange={(e) => setNewFolder(e.currentTarget.value)} size="md" />
                <Button type="submit" disabled={!newFolder.trim()} style={{ minHeight: 44 }}>{t('add')}</Button>
              </Group>
            </form>
            {folders.length === 0 ? <Text size="sm" c="dimmed">{t('empty')}</Text> : null}
            {folders.map((f) => (
              <Stack key={f.id} gap={4}>
                <Group wrap="nowrap" align="center">
                  <TextInput
                    style={{ flex: 1 }}
                    aria-label={t('nameOf', { name: f.name })}
                    value={nameFor(f.id, f.name)}
                    maxLength={60}
                    onChange={(e) => setNames((n) => ({ ...n, [f.id]: e.currentTarget.value }))}
                    onBlur={() => void commitName(f.id, f.name, (name) => folderApi.renameFolder(f.id, { name }))}
                    onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); e.currentTarget.blur(); } }}
                    size="md"
                    rightSection={names[f.id] !== undefined ? <IconCheck size={14} aria-hidden /> : undefined}
                  />
                  <Text size="xs" c="dimmed">{t('projects', { count: f.projectCount })}</Text>
                  <ActionIcon variant="default" size={44} aria-label={`${t('delete')}: ${f.name}`} onClick={() => setConfirm({ kind: 'folder', id: f.id })}>
                    <IconTrash size={18} />
                  </ActionIcon>
                </Group>
                {confirmBox('folder', f.id, f.name, f.projectCount, () => folderApi.deleteFolder(f.id))}
              </Stack>
            ))}
          </Stack>
        </Tabs.Panel>
      </Tabs>
    </Modal>
  );
}
