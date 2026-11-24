'use client';

import { useRef, useCallback } from 'react';
import { Group, Text, Button } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { toggleItem, selectRange, selectItem } from '@/lib/editing/selection';
import { newGuid } from '@/lib/editing/ids';
import { nextTrackName, cleanTrackName } from '../timeline/trackLayout';
import { selectModeTap } from '../phone/phone-actions';
import { useT } from '@/i18n';
import type { CommandsCtx } from './useCommands';

/** What the timeline canvas and its track headers call back into: selection and track operations. */
export function useTimelineCallbacks(ctx: CommandsCtx) {
  const { store, selectMode, editing, doc, ui, readOnly, dispatch, undo } = ctx;
  const tx = useT('editor.editorWorkspace');
  // ── timeline callbacks ────────────────────────────────────────────────────
  const anchorRef = useRef<string | null>(null);
  const onSelect = useCallback(
    (itemId: string | null, modifiers: { shift: boolean; toggle: boolean }) => {
      const s = store.getState();
      if (selectMode) {
        // A tap on empty lane space must not throw the group away; only the Clear button does.
        const next = selectModeTap(s.doc, s.ui.selection, itemId);
        if (next === null) return;
        editing.setSelectedGap(null);
        store.setUi({ selection: next });
        return;
      }
      if (itemId === null) {
        store.setUi({ selection: [] });
        anchorRef.current = null;
        editing.setSelectedGap(null);
        return;
      }
      editing.setSelectedGap(null);
      if (modifiers.toggle) store.setUi({ selection: toggleItem(s.doc, s.ui.selection, itemId) });
      else if (modifiers.shift) store.setUi({ selection: selectRange(s.doc, anchorRef.current, itemId) });
      else {
        store.setUi({ selection: selectItem(s.doc, itemId) });
        anchorRef.current = itemId;
      }
    },
    [store, editing, selectMode],
  );

  const onToggleTrack = useCallback(
    (trackId: string, flag: 'locked' | 'enabled' | 'mute' | 'solo' | 'collapsed') => {
      const track = doc.tracks.find((t) => t.id === trackId);
      if (!track) return;
      const current = flag === 'collapsed' ? (ui.trackOverrides[trackId]?.collapsed ?? false) : store.effectiveTrackFlags(track)[flag];
      if (flag === 'collapsed' || readOnly) {
        // Collapse is view state; and a conflicted timeline keeps the P3 override so the preview still obeys.
        store.setTrackOverride(trackId, { [flag]: !current });
        return;
      }
      // P4: the flag is a document field now. Clear the override so the document is the one truth.
      store.setTrackOverride(trackId, { [flag]: undefined });
      dispatch({ type: 'setTrackProperty', trackId, key: flag, value: !current }, `${!current ? 'Set' : 'Clear'} track ${flag}`);
    },
    [store, doc, ui.trackOverrides, readOnly, dispatch],
  );

  const onAddTrack = useCallback(
    (kind: 'video' | 'audio') => {
      const id = newGuid();
      dispatch(
        {
          type: 'addTrack',
          index: doc.tracks.length,
          track: { id, kind, name: nextTrackName(doc.tracks, kind), color: null, height: 64, locked: false, enabled: true, mute: false, solo: false, busId: null, items: [] },
        },
        `Add ${kind} track`,
      );
    },
    [doc, dispatch],
  );

  // R03.5: add above / below a track, rename / recolour / resize through setTrackProperty, and reorder (moveTrack).
  const onAddTrackAt = useCallback(
    (index: number, kind: 'video' | 'audio') => {
      dispatch(
        {
          type: 'addTrack',
          index,
          track: { id: newGuid(), kind, name: nextTrackName(doc.tracks, kind), color: null, height: 64, locked: false, enabled: true, mute: false, solo: false, busId: null, items: [] },
        },
        `Add ${kind} track`,
      );
    },
    [doc, dispatch],
  );
  const onSetTrackProperty = useCallback(
    (trackId: string, key: 'name' | 'color' | 'height', value: string | number | null) => {
      const clean = key === 'name' ? cleanTrackName(String(value)) : value;
      if (clean === null && key === 'name') return;
      const label = key === 'name' ? tx('renameTrack') : key === 'color' ? tx('changeTrackColour') : tx('changeTrackHeight');
      dispatch({ type: 'setTrackProperty', trackId, key, value: clean as string | number | null }, label);
    },
    [dispatch, tx],
  );
  const onMoveTrack = useCallback((trackId: string, toIndex: number) => dispatch({ type: 'moveTrack', trackId, toIndex }, tx('moveTrack')), [dispatch, tx]);

  // Removing a track takes its clips with it and is one click: it is undoable, and the toast
  // says so with an Undo button right there (a phone has no Ctrl+Z).
  const onRemoveTrack = useCallback(
    (trackId: string) => {
      const name = doc.tracks.find((t) => t.id === trackId)?.name ?? tx('theTrack');
      if (!dispatch({ type: 'removeTrack', trackId }, tx('removeTrack'))) return;
      const id = `track-removed-${trackId}`;
      notifications.show({
        id,
        color: 'gray',
        title: tx('removed', { name }),
        autoClose: 12_000,
        message: (
          <Group gap="xs" wrap="nowrap">
            <Text size="sm">{tx('theTrackAndEverything')}</Text>
            <Button
              size="compact-sm" fz="xs"
              variant="light"
              onClick={() => {
                undo();
                notifications.hide(id);
              }}
              data-testid="removed-track-undo"
            >
              {tx('undo')}
            </Button>
          </Group>
        ),
      });
    },
    [dispatch, doc.tracks, undo, tx],
  );


  const onTarget = useCallback(
    (trackId: string) => {
      const targeted = new Set(ui.targetedTrackIds);
      if (targeted.has(trackId)) targeted.delete(trackId);
      else targeted.add(trackId);
      store.setUi({ targetedTrackIds: [...targeted] });
    },
    [store, ui.targetedTrackIds],
  );

  return { onSelect, onToggleTrack, onAddTrack, onAddTrackAt, onSetTrackProperty, onMoveTrack, onRemoveTrack, onTarget };
}

export type TimelineCallbacksCtx = CommandsCtx & ReturnType<typeof useTimelineCallbacks>;
