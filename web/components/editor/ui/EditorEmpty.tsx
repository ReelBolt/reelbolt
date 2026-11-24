'use client';

import React from 'react';
import { Button, Stack, Text } from '@mantine/core';
import { EditorIcon, type EditorIconComponent } from './EditorIcon';
import classes from './ui.module.css';

export interface EditorEmptyProps {
  icon?: EditorIconComponent;
  /** One plain sentence: what is missing and why it matters. */
  title: string;
  /** An optional second sentence: what to do. */
  hint?: React.ReactNode;
  /** The one primary thing to do about it. */
  action?: { label: string; onClick: () => void; testId?: string; disabled?: boolean; loading?: boolean };
  testId?: string;
  /** Inline in a section of a panel: no minimum height, tighter padding. */
  compact?: boolean;
}

/**
 * The editor's one empty state: an icon, a plain sentence and (when there is something to do) one primary action. It
 * replaces the grey help boxes that said "nothing here" in 16 px text with nowhere to go.
 */
export function EditorEmpty({ icon, title, hint, action, testId, compact = false }: EditorEmptyProps) {
  return (
    <Stack align="center" gap="xs" className={[classes.empty, compact ? classes.emptyCompact : ''].filter(Boolean).join(' ')} role="status" data-testid={testId}>
      {icon ? <EditorIcon icon={icon} size="lg" color="dimmed" /> : null}
      <Text fz="sm" fw={600} ta="center">
        {title}
      </Text>
      {hint ? (
        <Text fz="xs" c="dimmed" ta="center" maw={320}>
          {hint}
        </Text>
      ) : null}
      {action ? (
        <Button size="compact-sm" fz="xs" onClick={action.onClick} disabled={action.disabled} loading={action.loading} data-testid={action.testId}>
          {action.label}
        </Button>
      ) : null}
    </Stack>
  );
}
