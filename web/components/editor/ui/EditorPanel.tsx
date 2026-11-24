'use client';

import React from 'react';
import { Group, Text } from '@mantine/core';
import classes from './ui.module.css';

export interface EditorPanelProps {
  /** The panel's heading (also its accessible name). */
  title: string;
  /** Controls on the right of the header (icon buttons, a switch). */
  actions?: React.ReactNode;
  /** A line under the heading in dimmed 12 px text. */
  description?: React.ReactNode;
  children: React.ReactNode;
  testId?: string;
  /** Let the body scroll (default) or leave scrolling to the content. */
  scroll?: boolean;
  /** Extra class for the body. */
  bodyClassName?: string;
}

/**
 * The editor's one panel: a header row (title, actions) over a body that scrolls on its own, so the header never
 * scrolls away and a long panel never grows the page. Spacing is 8 px inside, 4/8/16 between groups.
 */
export function EditorPanel({ title, actions, description, children, testId, scroll = true, bodyClassName }: EditorPanelProps) {
  const headingId = React.useId();
  return (
    <section className={[classes.panel, scroll ? '' : classes.panelFlow].filter(Boolean).join(' ')} aria-labelledby={headingId} data-testid={testId}>
      <header className={classes.panelHeader}>
        <Group justify="space-between" wrap="nowrap" gap="xs" mih="var(--rb-control-h, 36px)">
          <Text id={headingId} component="h2" fz="sm" fw={600} m={0} truncate>
            {title}
          </Text>
          {actions ? (
            <Group gap={4} wrap="nowrap">
              {actions}
            </Group>
          ) : null}
        </Group>
        {description ? (
          <Text fz="xs" c="dimmed">
            {description}
          </Text>
        ) : null}
      </header>
      <div className={[classes.panelBody, scroll ? classes.panelScroll : '', bodyClassName ?? ''].filter(Boolean).join(' ')}>{children}</div>
    </section>
  );
}
