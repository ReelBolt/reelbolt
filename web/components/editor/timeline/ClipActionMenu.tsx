'use client';

import React, { useState } from 'react';
import { Button, Divider, Group, Popover, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconArrowsHorizontal, IconChevronLeft, IconChevronRight, IconCut, IconDots, IconEraser, IconTrash } from '@tabler/icons-react';
import type { GapInfo } from '@/lib/editing/gaps';
import type { ClipMenuItem } from './ClipContextMenu';
import { useT } from '@/i18n';
import { useLiveChordLookup } from '../prefs/live-chords';

const tipOf = (label: string, chord: string | undefined): string | undefined => (chord ? `${label} (${chord})` : undefined);

/** Items the button list already carries itself; the rest of the shared action list is appended. */
const OWN_ITEMS = new Set(['split', 'lift', 'delete', 'close-gaps']);
/** Items whose reason is printed under the button (not tooltip-only): the R03 deferred ops. */
const SHOW_REASON = new Set(['join', 'fit-to-fill', 'replace']);

export interface ClipActionMenuProps {
  /** The selected clips' spans; Split needs the playhead inside one of them. */
  selectedItems: readonly { id: string; recordIn: number; recordOut: number }[];
  playheadFrame: number;
  readOnly: boolean;
  /** Ripple delete: remove the clip and close the gap. */
  onDelete: () => void;
  /** Remove the clip and leave a gap. */
  onLift: () => void;
  /** Split the selected clips at the playhead. */
  onSplit: () => void;
  onNudge: (deltaFrames: -1 | 1) => void;
  /** The gaps around the selection (null = no selection); drives the two Close gap actions. */
  gaps?: GapInfo | null;
  /** Close the gap before the selected clip: later clips on its track shift left by the gap. */
  onCloseGap?: (trackId: string, atFrame: number) => void;
  /** Close every gap between clips on the selected clip's track. */
  onCloseGaps?: (trackId: string) => void;
  /** R03.2: the rest of the shared clip action list (copy, paste, duplicate, move to track, ...), the same as the right-click menu. */
  extraActions?: readonly ClipMenuItem[];
}

/**
 * The clip actions as buttons: Delete, Lift, Split and Nudge one frame either way. Each is also a
 * keyboard shortcut on a desktop; this is the path for a finger (and for anyone who prefers to
 * click). Targets are 44 px tall; Delete sits apart from the neutral actions, below a divider.
 */
export function ClipActionMenu({ selectedItems, playheadFrame, readOnly, onDelete, onLift, onSplit, onNudge, gaps = null, onCloseGap, onCloseGaps, extraActions = [] }: ClipActionMenuProps) {
  const t = useT('editor-timeline.actionMenu');
  const chordOf = useLiveChordLookup();
  const [opened, setOpened] = useState(false);
  const coarsePointer = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const count = selectedItems.length;
  const canSplit = selectedItems.some((item) => item.recordIn < playheadFrame && playheadFrame < item.recordOut);
  const disabled = readOnly || count === 0;
  // A menu left open when the selection goes away must not pop open again with the next selection.
  if (disabled && opened) setOpened(false);
  const run = (action: () => void, close: boolean) => () => {
    action();
    if (close) setOpened(false);
  };

  return (
    <Popover
      opened={opened && !disabled}
      onChange={setOpened}
      position="bottom-start"
      withinPortal
      shadow="md"
      transitionProps={{ duration: 0 }}
      middlewares={{
        flip: true,
        shift: { padding: 8 },
        // The list scrolls inside the room the screen leaves, so Delete at its foot is always reachable.
        size: {
          padding: 8,
          apply({ availableHeight, elements }) {
            elements.floating.style.maxHeight = `${Math.max(160, Math.floor(availableHeight))}px`;
          },
        },
      }}
    >
      <Popover.Target>
        <Button
          variant="subtle"
          color="gray"
          size="compact-sm" fz="xs"
          h={coarsePointer ? 44 : undefined}
          px={coarsePointer ? 6 : undefined}
          leftSection={<IconDots size={16} stroke={1.7} />}
          disabled={disabled}
          onClick={() => setOpened((o) => !o)}
          aria-haspopup="dialog"
          aria-expanded={opened && !disabled}
          aria-label={t('clipActions')}
          data-testid="clip-actions-open"
          fw={500}
        >
          {t('clip')}
        </Button>
      </Popover.Target>
      <Popover.Dropdown p="sm" data-testid="clip-actions-menu" aria-label={t('clipActions')} style={{ overflowY: 'auto' }}>
        <Stack gap={8} w={260}>
          <Text size="sm" fw={600}>
            {t('selected', { count })}
          </Text>
          <Group gap={8} grow wrap="nowrap">
            <Button h={44} variant="default" leftSection={<IconChevronLeft size={16} />} onClick={run(() => onNudge(-1), false)} aria-label={t('nudgeEarlier', { count: 1 })} data-testid="clip-action-nudge-back">
              {t('oneFrame', { count: 1 })}
            </Button>
            <Button h={44} variant="default" rightSection={<IconChevronRight size={16} />} onClick={run(() => onNudge(1), false)} aria-label={t('nudgeLater', { count: 1 })} data-testid="clip-action-nudge-forward">
              {t('oneFrame', { count: 1 })}
            </Button>
          </Group>
          <Button h={44} variant="default" leftSection={<IconCut size={16} />} disabled={!canSplit} onClick={run(onSplit, true)} data-testid="clip-action-split">
            {t('split')}
          </Button>
          {!canSplit && (
            <Text size="xs" data-testid="clip-action-split-hint">
              {t('splitHint')}
            </Text>
          )}
          <Button h={44} variant="default" leftSection={<IconEraser size={16} />} onClick={run(onLift, true)} data-testid="clip-action-lift">
            {t('lift')}
          </Button>
          {onCloseGap && (
            <Button
              h={44}
              variant="default"
              leftSection={<IconArrowsHorizontal size={16} />}
              disabled={!gaps || gaps.beforeAtFrame === null}
              onClick={run(() => gaps && gaps.beforeAtFrame !== null && onCloseGap(gaps.trackId, gaps.beforeAtFrame), true)}
              data-testid="clip-action-close-gap"
            >
              {t('closeGapBefore')}
            </Button>
          )}
          {onCloseGaps && (
            <Button
              h={44}
              variant="default"
              leftSection={<IconArrowsHorizontal size={16} />}
              disabled={!gaps || gaps.interiorGaps === 0}
              onClick={run(() => gaps && onCloseGaps(gaps.trackId), true)}
              data-testid="clip-action-close-gaps"
            >
              {gaps && gaps.interiorGaps > 0 ? t('closeAllGaps', { track: gaps.trackName }) : t('noGaps')}
            </Button>
          )}
          {extraActions.filter((a) => !OWN_ITEMS.has(a.id)).map((a) => (
            <React.Fragment key={a.id}>
            <Button
              h={44}
              variant="default"
              justify="space-between"
              disabled={a.unavailable !== undefined}
              title={a.unavailable ?? tipOf(a.label, a.action ? chordOf(a.action) : undefined)}
              onClick={run(a.run, true)}
              data-testid={`clip-action-${a.id}`}
            >
              {a.label}
            </Button>
            {a.unavailable !== undefined && SHOW_REASON.has(a.id) && (
              <Text size="xs" data-testid={`clip-action-${a.id}-reason`}>
                {a.unavailable}
              </Text>
            )}
            </React.Fragment>
          ))}
          <Divider my={4} />
          <Button h={44} variant="default" c="var(--mantine-color-red-text)" leftSection={<IconTrash size={16} />} onClick={run(onDelete, true)} data-testid="clip-action-delete">
            {t('delete')}
          </Button>
          <Text size="xs">{t('deleteHint')}</Text>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
