'use client';

import { useState } from 'react';
import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useIsPhoneClass } from '@/lib/hooks/use-device-class';
import { IconFolder, IconFolderPlus, IconStack2 } from '@tabler/icons-react';
import { flattenFolders, normalizeDir, type FolderNode } from '@/lib/files/model';
import { useT } from '@/i18n';
import classes from './files.module.css';

/** Pick a destination folder (or type a new path) for one or more files. */
export function MoveDialog({
  opened, count, root, currentPath, loading, onClose, onMove,
}: {
  opened: boolean;
  count: number;
  root: FolderNode;
  currentPath: string;
  loading?: boolean;
  onClose: () => void;
  onMove: (targetPath: string) => void;
}) {
  const t = useT('files.move');
  const isPhone = useIsPhoneClass();
  const [target, setTarget] = useState<string>(currentPath);
  const [custom, setCustom] = useState('');
  const folders = flattenFolders(root);
  const effective = custom.trim() ? normalizeDir(custom) : target;

  return (
    <Modal
      opened={opened} onClose={onClose} closeButtonProps={{ size: 44 }} centered={!isPhone} fullScreen={!!isPhone} radius="lg"
      title={t('title', { count })}
    >
      <Stack gap="md">
        <div className={classes.list} role="radiogroup" aria-label={t('destination')} style={{ maxHeight: '45dvh', overflowY: 'auto' }}>
          <button type="button" role="radio" aria-checked={!custom.trim() && target === ''} className={classes.item}
            data-selected={!custom.trim() && target === ''} onClick={() => { setTarget(''); setCustom(''); }}>
            <IconStack2 size={18} /> <Text size="sm">{t('root')}</Text>
          </button>
          {folders.map((f) => (
            <button key={f.path} type="button" role="radio" aria-checked={!custom.trim() && target === f.path} className={classes.item}
              data-selected={!custom.trim() && target === f.path} onClick={() => { setTarget(f.path); setCustom(''); }}>
              <IconFolder size={18} />
              <Text size="sm" truncate>{f.path}</Text>
            </button>
          ))}
        </div>
        <TextInput
          label={t('newFolder')}
          placeholder={t('newFolderPlaceholder')}
          leftSection={<IconFolderPlus size={16} />}
          value={custom}
          onChange={(e) => setCustom(e.currentTarget.value)}
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{t('cancel')}</Button>
          <Button loading={loading} onClick={() => onMove(effective)} style={{ minHeight: 44 }}>
            {t('moveTo', { path: effective ? `/${effective}` : t('rootName') })}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
