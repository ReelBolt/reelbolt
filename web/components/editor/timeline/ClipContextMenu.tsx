'use client';

import React, { useRef } from 'react';
import { Menu, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useT } from '@/i18n';
import type { KeyAction } from '@/lib/editing/keymap';
import { useLiveChordLookup } from '../prefs/live-chords';

/** The submenus the desktop menu folds the long tail into (the phone list stays flat). */
export type ClipMenuSub = 'arrange' | 'link' | 'select' | 'picture';

/** One entry of the clip action list: the same list backs the right-click menu and the "..." button. */
export interface ClipMenuItem {
  id: string;
  label: string;
  /** The catalogue action behind the item: its LIVE chord is shown right-aligned and in the tooltip (none when unbound or beaten). */
  action?: KeyAction;
  /** Why it is unavailable (shown as the item's tooltip) - an item with a reason is disabled. */
  unavailable?: string;
  danger?: boolean;
  /** A one-line note under the label (what the action will do to its neighbours). */
  note?: string;
  /** A divider is drawn above this item. */
  dividerBefore?: boolean;
  /** The right-click menu puts this item in a submenu (opened where the first of its members sits); lists that are flat ignore it. */
  sub?: ClipMenuSub;
  run: () => void;
}

export interface ClipContextMenuProps {
  /** Where the pointer was (viewport coordinates); null = closed. */
  anchor: { x: number; y: number } | null;
  items: readonly ClipMenuItem[];
  onClose: () => void;
  /** The menu's accessible name (the clip menu by default). */
  label?: string;
}

/** The list as the menu draws it: top-level entries in order, each submenu at the place of its first member. */
type Entry = { kind: 'item'; item: ClipMenuItem } | { kind: 'sub'; sub: ClipMenuSub; first: ClipMenuItem; members: ClipMenuItem[] };

function entriesOf(items: readonly ClipMenuItem[]): Entry[] {
  const out: Entry[] = [];
  const subs = new Map<ClipMenuSub, Extract<Entry, { kind: 'sub' }>>();
  for (const item of items) {
    if (!item.sub) {
      out.push({ kind: 'item', item });
      continue;
    }
    const existing = subs.get(item.sub);
    if (existing) {
      existing.members.push(item);
    } else {
      const entry: Extract<Entry, { kind: 'sub' }> = { kind: 'sub', sub: item.sub, first: item, members: [item] };
      subs.set(item.sub, entry);
      out.push(entry);
    }
  }
  return out;
}

/**
 * R03.2 / T-018: the action list at the pointer. Items that cannot run say why; targets are 44 px tall on
 * a coarse pointer. Each item's chord is read from the live keymap, so a remap changes it here. The long
 * tail sits in submenus, so the list is short enough never to scroll at 720 px of height.
 */
/** Keeps a menu inside the viewport: flip/shift place it, this scrolls it when it is still taller (720 px, 200% zoom). */
const DROPDOWN_FIT: React.CSSProperties = { maxHeight: 'calc(100dvh - 16px)', overflowY: 'auto' };

/** One submenu. ArrowLeft inside it hands the focus back to its entry, so the arrows keep working in the parent list. */
function SubEntry({ sub, label, coarse, children }: { sub: ClipMenuSub; label: string; coarse: boolean; children: React.ReactNode }) {
  const entryRef = useRef<HTMLButtonElement>(null);
  return (
    <Menu.Sub position="right-start" offset={0} middlewares={{ flip: true, shift: { padding: 8, crossAxis: true } }}>
      <Menu.Sub.Target>
        <Menu.Sub.Item ref={entryRef} mih={coarse ? 44 : undefined} data-testid={`ctx-sub-${sub}`}>
          {label}
        </Menu.Sub.Item>
      </Menu.Sub.Target>
      <Menu.Sub.Dropdown
        style={DROPDOWN_FIT}
        data-testid={`ctx-sub-${sub}-menu`}
        onKeyDown={(event: React.KeyboardEvent) => {
          if (event.key === 'ArrowLeft') entryRef.current?.focus();
        }}
      >
        {children}
      </Menu.Sub.Dropdown>
    </Menu.Sub>
  );
}

export function ClipContextMenu({ anchor, items, onClose, label }: ClipContextMenuProps) {
  const t = useT('editor-timeline.menu');
  const chordOf = useLiveChordLookup();
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const subLabel = (sub: ClipMenuSub): string => {
    switch (sub) {
      case 'arrange':
        return t('sub.arrange');
      case 'link':
        return t('sub.link');
      case 'select':
        return t('sub.select');
      case 'picture':
        return t('sub.picture');
    }
  };
  const row = (item: ClipMenuItem) => {
    const chord = item.action ? chordOf(item.action) : undefined;
    return (
      <Menu.Item
        key={item.id}
        color={item.danger ? 'red' : undefined}
        disabled={item.unavailable !== undefined}
        style={item.unavailable !== undefined ? { opacity: 1 } : undefined}
        title={item.unavailable ?? (chord ? `${item.label} (${chord})` : undefined)}
        mih={coarse ? 44 : undefined}
        onClick={item.run}
        data-testid={`ctx-${item.id}`}
        rightSection={
          chord ? (
            <Text size="xs" c="dimmed" ml="md" data-testid={`ctx-${item.id}-chord`}>
              {chord}
            </Text>
          ) : null
        }
      >
        <div style={item.unavailable !== undefined ? { opacity: 0.6 } : undefined}>{item.label}</div>
        {item.note !== undefined && (
          <Text size="xs" c="dimmed" maw={260} data-testid={`ctx-${item.id}-note`}>
            {item.note}
          </Text>
        )}
        {item.unavailable !== undefined && (
          <Text size="xs" maw={260} style={{ color: 'var(--rb-ed-fg-muted)' }} data-testid={`ctx-${item.id}-reason`}>
            {item.unavailable}
          </Text>
        )}
      </Menu.Item>
    );
  };
  return (
    <Menu opened={anchor !== null} onChange={(open) => !open && onClose()} position="bottom-start" withinPortal shadow="md" closeOnItemClick transitionProps={{ duration: 0 }} offset={0} middlewares={{ flip: true, shift: { padding: 8, crossAxis: true } }}>
      <Menu.Target>
        <div aria-hidden style={{ position: 'fixed', left: anchor?.x ?? -9999, top: anchor?.y ?? -9999, width: 1, height: 1, pointerEvents: 'none' }} />
      </Menu.Target>
      <Menu.Dropdown data-testid="clip-context-menu" style={DROPDOWN_FIT} aria-label={label ?? t('clipActions')}>
        {entriesOf(items).map((entry) =>
          entry.kind === 'item' ? (
            <React.Fragment key={entry.item.id}>
              {entry.item.dividerBefore && <Menu.Divider />}
              {row(entry.item)}
            </React.Fragment>
          ) : (
            <React.Fragment key={`sub-${entry.sub}`}>
              {entry.first.dividerBefore && <Menu.Divider />}
              <SubEntry sub={entry.sub} label={subLabel(entry.sub)} coarse={coarse}>
                {entry.members.map((member) => (
                  <React.Fragment key={member.id}>
                    {member !== entry.first && member.dividerBefore && <Menu.Divider />}
                    {row(member)}
                  </React.Fragment>
                ))}
              </SubEntry>
            </React.Fragment>
          ),
        )}
      </Menu.Dropdown>
    </Menu>
  );
}
