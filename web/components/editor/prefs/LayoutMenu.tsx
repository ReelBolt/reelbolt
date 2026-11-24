'use client';

import React, { useState } from 'react';
import { Button, Group, Menu, Modal, Text, TextInput } from '@mantine/core';
import { IconLayoutDashboard, IconTrash } from '@tabler/icons-react';
import type { SavedLayout } from '@/lib/editing/api/editor-p9';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';

export interface LayoutMenuProps {
  workspace: string;
  layouts: readonly SavedLayout[];
  /** The layout as it is on screen now (sizes and tabs), for "Save current". */
  current: () => Omit<SavedLayout, 'name'>;
  onApply: (layout: SavedLayout) => void;
  /** Puts this page's panels back to the geometry table's default sizes. */
  onReset?: () => void;
  /** Puts every page back to the table's default sizes; the menu asks for confirmation first. */
  onResetAll?: () => void;
  onSaveLayouts: (layouts: SavedLayout[]) => void;
}

/**
 * P9: saved layouts per workspace — the panel sizes and open tabs, kept in the user's preferences so
 * they follow them. "Save current" stores (or replaces, by name) the layout on screen; the list shows
 * this workspace's layouts.
 */
export function LayoutMenu({ workspace, layouts, current, onApply, onReset, onResetAll, onSaveLayouts }: LayoutMenuProps) {
  const t = useT('editor.prefs.layoutMenu');
  const [name, setName] = useState('');
  const [confirming, setConfirming] = useState<string | null>(null);
  const [confirmResetAll, setConfirmResetAll] = useState(false);
  const mine = layouts.filter((l) => l.workspace === workspace);
  return (
    <>
    <Menu shadow="md" width={240} closeOnItemClick={false} position="bottom-end">
      <Menu.Target>
        <EditorIcon icon={IconLayoutDashboard} size="lg" label={t('savedLayouts')} data-testid="layout-menu" />
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t('savedLayouts2', { workspace })}</Menu.Label>
        {mine.length === 0 && <Menu.Item disabled>{t('noneYet')}</Menu.Item>}
        {mine.map((layout) =>
          confirming === layout.name ? (
            <Group key={layout.name} gap={4} px="xs" py={4} wrap="nowrap" data-testid={`layout-delete-confirm-row-${layout.name}`}>
              <Text size="xs" style={{ flex: 1 }}>
                {t('deleteTheLayoutIt', { name: layout.name })}
              </Text>
              <Button
                size="compact-xs" fz="xs"
                color="red"
                onClick={() => {
                  onSaveLayouts(layouts.filter((l) => !(l.workspace === workspace && l.name === layout.name)));
                  setConfirming(null);
                }}
                data-testid={`layout-delete-confirm-${layout.name}`}
              >
                {t('delete')}
              </Button>
              <Button size="compact-xs" fz="xs" variant="default" onClick={() => setConfirming(null)} data-testid={`layout-delete-keep-${layout.name}`}>
                {t('keep')}
              </Button>
            </Group>
          ) : (
            <Group key={layout.name} gap={0} wrap="nowrap">
              <Menu.Item style={{ flex: 1 }} onClick={() => onApply(layout)} data-testid={`layout-apply-${layout.name}`}>
                {layout.name}
              </Menu.Item>
              <EditorIcon icon={IconTrash} size="sm" color="red" mr={4} label={t('deleteLayout', { name: layout.name })} onClick={() => setConfirming(layout.name)} data-testid={`layout-delete-${layout.name}`} />
            </Group>
          ),
        )}
        <Menu.Divider />
        {onReset && (
          <Menu.Item closeMenuOnClick onClick={onReset} data-testid="layout-reset-page">
            {t('resetThisPage')}
          </Menu.Item>
        )}
        {onResetAll && (
          <Menu.Item closeMenuOnClick onClick={() => setConfirmResetAll(true)} data-testid="layout-reset-all">
            {t('resetEveryPage')}
          </Menu.Item>
        )}
        <Menu.Divider />
        <Group gap={4} p={4} wrap="nowrap">
          <TextInput size="xs" aria-label={t('layoutName')} placeholder={t('layoutName2')} value={name} onChange={(e) => setName(e.currentTarget.value)} maxLength={80} data-testid="layout-name" />
          <Button
            size="compact-xs" fz="xs"
            disabled={name.trim().length === 0}
            onClick={() => {
              const layout: SavedLayout = { ...current(), name: name.trim(), workspace };
              onSaveLayouts([...layouts.filter((l) => !(l.workspace === workspace && l.name === layout.name)), layout].slice(-50));
              setName('');
            }}
            data-testid="layout-save"
          >
            {t('save')}
          </Button>
        </Group>
      </Menu.Dropdown>
    </Menu>
    <Modal opened={confirmResetAll} onClose={() => setConfirmResetAll(false)} title={t('resetEveryPageTitle')} centered size="sm" data-testid="layout-reset-all-dialog">
      <Text size="sm">{t('resetEveryPageBody')}</Text>
      <Group justify="flex-end" gap="xs" mt="md">
        <Button variant="default" onClick={() => setConfirmResetAll(false)} data-testid="layout-reset-all-cancel">
          {t('cancel')}
        </Button>
        <Button
          color="red"
          onClick={() => {
            onResetAll?.();
            setConfirmResetAll(false);
          }}
          data-testid="layout-reset-all-confirm"
        >
          {t('resetEveryPage')}
        </Button>
      </Group>
    </Modal>
    </>
  );
}
