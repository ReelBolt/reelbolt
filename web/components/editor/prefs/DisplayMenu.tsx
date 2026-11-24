'use client';

import React from 'react';
import { Menu, SegmentedControl, Stack, Text } from '@mantine/core';
import { IconAdjustmentsHorizontal } from '@tabler/icons-react';
import { EditorIcon } from '../ui/EditorIcon';
import { useEditorDisplay } from '../ui/EditorDisplay';
import type { EditorDensity, EditorSurround } from '@/lib/editing/display/display-prefs';
import { useT } from '@/i18n';

/**
 * VF13 / VF18: how the editor looks on this device. Density packs the controls tighter (a finger always gets
 * the comfortable size and the 44 px targets), and the surround keeps the editor dark while the app is light
 * (night grading). Both are remembered for this person on this device.
 */
export function DisplayMenu() {
  const t = useT('editor.prefs.displayMenu');
  const { preferredDensity, density, surround, coarsePointer, setDensity, setSurround } = useEditorDisplay();
  return (
    <Menu shadow="md" width={280} closeOnItemClick={false} position="bottom-end" withinPortal>
      <Menu.Target>
        <EditorIcon icon={IconAdjustmentsHorizontal} label={t('displaySettings')} size="md" data-testid="display-menu" aria-haspopup="menu" />
      </Menu.Target>
      <Menu.Dropdown p="sm">
        <Stack gap="sm">
          <Stack gap={4}>
            <Text fz="xs" fw={600}>
              {t('controlSize')}
            </Text>
            <SegmentedControl
              size="xs"
              fullWidth
              aria-label={t('controlSize2')}
              value={preferredDensity}
              onChange={(v) => setDensity(v as EditorDensity)}
              data={[
                { value: 'comfortable', label: t('comfortable') },
                { value: 'compact', label: t('compact') },
              ]}
              data-testid="display-density"
            />
            <Text fz="xs" c="dimmed" data-testid="display-density-note">
              {coarsePointer ? t('aTouchScreenAlways') : density === 'compact' ? t('smallerControlsAndTighter') : t('roomierControlsAndRows')}
            </Text>
          </Stack>
          <Stack gap={4}>
            <Text fz="xs" fw={600}>
              {t('editorSurround')}
            </Text>
            <SegmentedControl
              size="xs"
              fullWidth
              aria-label={t('editorSurround2')}
              value={surround}
              onChange={(v) => setSurround(v as EditorSurround)}
              data={[
                { value: 'match', label: t('matchTheApp') },
                { value: 'dark', label: t('alwaysDark') },
              ]}
              data-testid="display-surround"
            />
            <Text fz="xs" c="dimmed">
              {t('alwaysDarkKeepsThe')}
            </Text>
          </Stack>
        </Stack>
      </Menu.Dropdown>
    </Menu>
  );
}
