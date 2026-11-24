'use client';

import React from 'react';
import { UnstyledButton } from '@mantine/core';
import { IconAdjustments, IconFolder, IconScissors, IconTextCaption, IconVolume } from '@tabler/icons-react';
import classes from './phone.module.css';
import { useT } from '@/i18n';

export type DockItem = 'media' | 'edit' | 'audio' | 'look' | 'captions';

export const DOCK_ITEMS: readonly { id: DockItem; icon: React.ComponentType<{ size?: number | string; 'aria-hidden'?: boolean }> }[] = [
  { id: 'media', icon: IconFolder },
  { id: 'edit', icon: IconScissors },
  { id: 'audio', icon: IconVolume },
  { id: 'look', icon: IconAdjustments },
  { id: 'captions', icon: IconTextCaption },
];

export interface PhoneDockProps {
  /** The dock item whose sheet is open, if any. */
  active: DockItem | null;
  onSelect: (item: DockItem) => void;
  /** `rail`: the landscape phone's left rail instead of the bottom bar. */
  orientation?: 'bar' | 'rail';
}

/** The phone's five tools. Each opens a sheet; none is a placeholder. */
export function PhoneDock({ active, onSelect, orientation = 'bar' }: PhoneDockProps) {
  const t = useT('editor.phone.phoneDock');
  return (
    <nav className={classes.dock} data-orientation={orientation} aria-label={t('editorTools')} data-testid="phone-dock">
      {DOCK_ITEMS.map(({ id, icon: Icon }) => (
        <UnstyledButton key={id} className={classes.dockItem} aria-pressed={active === id} onClick={() => onSelect(id)} data-testid={`dock-${id}`}>
          <Icon size={22} aria-hidden />
          <span>{t(`dock.${id}`)}</span>
        </UnstyledButton>
      ))}
    </nav>
  );
}
