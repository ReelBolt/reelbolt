'use client';

import React from 'react';
import { Menu, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';

export interface EventMenuItem {
  id: string;
  label: string;
  run: () => void;
}

/**
 * "Act on this event" at the pointer (right click, long press or tap, or the keyboard's context-menu key).
 * Only the actions that can run are in the list: one that no existing edit can express is absent, not disabled.
 * Targets are 44 px tall on a coarse pointer.
 */
export function EventActionsMenu({ anchor, title, ariaLabel, items, onClose }: {
  anchor: { x: number; y: number } | null;
  title: string;
  ariaLabel: string;
  items: readonly EventMenuItem[];
  onClose: () => void;
}) {
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  return (
    <Menu opened={anchor !== null} onChange={(open) => !open && onClose()} position="bottom-start" withinPortal shadow="md" closeOnItemClick transitionProps={{ duration: 0 }} offset={0}>
      <Menu.Target>
        <div aria-hidden style={{ position: 'fixed', left: anchor?.x ?? -9999, top: anchor?.y ?? -9999, width: 1, height: 1, pointerEvents: 'none' }} />
      </Menu.Target>
      <Menu.Dropdown data-testid="event-actions-menu" aria-label={ariaLabel}>
        <Menu.Label>
          <Text size="xs" c="dimmed" data-testid="event-actions-title">{title}</Text>
        </Menu.Label>
        {items.map((item) => (
          <Menu.Item key={item.id} mih={coarse ? 44 : undefined} onClick={item.run} data-testid={`event-action-${item.id}`}>
            {item.label}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
