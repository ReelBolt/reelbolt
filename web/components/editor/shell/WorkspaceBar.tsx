'use client';

import React from 'react';
import { Tooltip, UnstyledButton } from '@mantine/core';
import type { WorkspaceId } from '@/lib/editing/store';
import { ariaChord, displayChord } from '@/lib/editing/keymap';
import { WORKSPACES } from './workspaces';
import classes from './workspace.module.css';
import { useT } from '@/i18n';

export interface WorkspaceBarProps {
  active: WorkspaceId;
  onChange: (workspace: WorkspaceId) => void;
  /**
   * `switcher` (default): the compact segmented control in the workspace header, labels hidden on
   * narrower desktops (icons keep a tooltip). `dock`: the phone's bottom bar, thumb-reachable and
   * horizontally scrollable. `rail`: the tablet's 56 px column down the left edge, icon over label.
   */
  variant?: 'switcher' | 'dock' | 'rail';
  /** T-007: the live page chord per workspace, for the tooltip and `aria-keyshortcuts`. */
  chords?: Partial<Record<WorkspaceId, string>>;
}

const IS_MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform ?? '');

/**
 * The workspace switcher: Media · Cut · Edit · Compose · Color · Audio · Deliver.
 *
 * Switching a workspace is view state only — it changes nothing in the document, the revision or
 * the history (`EditorStore.setUi`), which the workspace tests assert. Every entry is a real button
 * reachable by keyboard, and the active one carries `aria-current="page"`.
 */
export function WorkspaceBar({ active, onChange, variant = 'switcher', chords }: WorkspaceBarProps) {
  const t = useT('editor.shell.workspaceBar');
  const dock = variant === 'dock';
  const rail = variant === 'rail';
  return (
    <nav aria-label={t('workspaces')} data-testid="workspace-bar" className={rail ? classes.rail : dock ? classes.dock : classes.switcher}>
      {WORKSPACES.map((workspace) => {
        const Icon = workspace.icon;
        const isActive = workspace.id === active;
        const workspaceLabel = t(`names.${workspace.id}.label`);
        const chord = chords?.[workspace.id];
        const button = (
          <UnstyledButton
            key={workspace.id}
            onClick={() => onChange(workspace.id)}
            aria-current={isActive ? 'page' : undefined}
            aria-label={t('workspace', { label: workspaceLabel })}
            aria-keyshortcuts={chord ? ariaChord(chord, IS_MAC) : undefined}
            data-testid={`workspace-${workspace.id}`}
            className={rail ? classes.railItem : dock ? classes.dockItem : classes.switcherItem}
          >
            <Icon size={dock || rail ? 20 : 16} stroke={1.7} />
            <span className={dock || rail ? undefined : classes.switcherLabel}>{workspaceLabel}</span>
          </UnstyledButton>
        );
        return dock || rail ? (
          button
        ) : (
          <Tooltip key={workspace.id} label={chord ? `${t(`names.${workspace.id}.description`)} (${displayChord(chord, IS_MAC)})` : t(`names.${workspace.id}.description`)} openDelay={500} multiline w={240} position="bottom">
            {button}
          </Tooltip>
        );
      })}
    </nav>
  );
}
