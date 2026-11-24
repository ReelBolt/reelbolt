'use client';

import React, { useCallback, useMemo, useState } from 'react';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import type { EditOp } from '@/lib/editing/engine';
import type { EditorStore } from '@/lib/editing/store';
import type { EditTimeline } from '@/lib/editing/types';
import { newGuid } from '@/lib/editing/ids';
import { framesToTimecode } from '@/lib/editing/time';
import { availableEventActions, planEventAction, type EventActionId } from '@/lib/editing/events/event-actions';
import { EVENT_TYPES, nearestEvent, type LaneEvent } from '@/lib/editing/events/events-data';
import { useEventsLanePref } from '@/lib/editing/events/lane-pref';
import { useEventsLane, type UseEventsLane } from '@/lib/editing/events/use-events-lane';
import { EventActionsMenu, type EventMenuItem } from './EventActionsMenu';
import type { EventsLaneProps } from './TimelineCanvas';

export interface EventsControllerDeps {
  store: EditorStore;
  doc: EditTimeline;
  projectId: string;
  timelineId: string;
  readOnly: boolean;
  /** Applies a program as ONE undo step; false when it was refused or held. */
  apply: (ops: readonly EditOp[], label: string) => boolean;
  /** Takes the user to the sound picker with the playhead already at `frame`. Absent = "Add sound here" is not offered. */
  openSounds?: (frame: number) => void;
  isLocked: (trackId: string) => boolean;
  /** Fetch data even when the lane is hidden (the phone sheet, which has no lane). */
  forceData?: boolean;
}

export interface EventsController {
  laneOn: boolean;
  snapOn: boolean;
  setLane: (on: boolean) => void;
  setSnap: (on: boolean) => void;
  data: UseEventsLane;
  /** Props for the canvas; undefined while the lane is hidden. */
  laneProps: EventsLaneProps | undefined;
  /** The lane header row; undefined while the lane is hidden. */
  header: { label: string; snap: boolean; snapLabel: string; onToggleSnap: () => void } | undefined;
  /** Record frames the playhead and clip edges may snap to (lane shown and snapping to events on). */
  snapFrames: readonly number[] | undefined;
  /** The element to render once, anywhere in the tree: the "Act on this event" menu. */
  menu: React.ReactNode;
  openMenu: (event: LaneEvent, at: { clientX: number; clientY: number }) => void;
  /** Keymap actions. */
  toggleLane: () => void;
  toggleSnap: () => void;
  actOnNearest: () => void;
  /** Phone sheet and menus: the actions that apply to an event, and running one. */
  actionsFor: (event: LaneEvent) => { id: EventActionId; label: string }[];
  run: (id: EventActionId, event: LaneEvent) => void;
  describe: (event: LaneEvent) => string;
  nearest: () => LaneEvent | null;
  step: (delta: 1 | -1) => void;
  activeKey: string | null;
}

/**
 * The Events lane's one controller: the persisted view setting, the data, the ticks the canvas paints, the
 * snap targets and the "Act on this event" menu. The desktop timeline, the keymap and the phone sheet all go
 * through it, so a click, a shortcut and a tap run the same plan as one undo step.
 */
export function useEventsLaneController(deps: EventsControllerDeps): EventsController {
  const { store, doc, projectId, timelineId, readOnly, apply, openSounds, isLocked, forceData = false } = deps;
  const t = useT('editor-timeline.eventsLane');
  const { pref, setLane, setSnap } = useEventsLanePref(timelineId);
  const data = useEventsLane(projectId, doc, pref.lane || forceData);
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [anchor, setAnchor] = useState<{ x: number; y: number; event: LaneEvent } | null>(null);

  const typeWord = useCallback((type: string): string => ((EVENT_TYPES as readonly string[]).includes(type) ? t(`types.${type}`) : t('types.other')), [t]);
  const timeOf = useCallback((e: LaneEvent): string => framesToTimecode(e.frame, doc.settings.rate), [doc.settings.rate]);
  // The kind of picture (graphic, text, footage) joins the tooltip when the analysis gave one.
  const describe = useCallback(
    (e: LaneEvent): string =>
      e.kind !== undefined
        ? t('tooltipKind', { type: typeWord(e.type), time: timeOf(e), kind: t(`kinds.${e.kind}`) })
        : t('tooltip', { type: typeWord(e.type), time: timeOf(e) }),
    [t, typeWord, timeOf],
  );
  const labels = useMemo(() => Object.fromEntries(EVENT_TYPES.map((type) => [type, t(`types.${type}`)])), [t]);

  const canOpenSounds = openSounds !== undefined;
  const actionsFor = useCallback(
    (e: LaneEvent) => availableEventActions(doc, e, { readOnly, isLocked, canOpenSounds }).map((id) => ({ id, label: t(`actions.${id}`) })),
    [doc, readOnly, isLocked, canOpenSounds, t],
  );

  const run = useCallback(
    (id: EventActionId, e: LaneEvent) => {
      const current = store.getState().doc;
      const plan = planEventAction(id, { doc: current, event: e, newId: newGuid, isLocked });
      if (plan.kind === 'none') {
        notifications.show({ color: 'yellow', title: t('nothingChanged'), message: t(`reasons.${plan.reason}`) });
        return;
      }
      if (plan.kind === 'sound') {
        store.setUi({ playheadFrame: plan.frame });
        openSounds?.(plan.frame);
        notifications.show({ color: 'blue', title: t('actions.addSound'), message: t('soundHint', { time: timeOf(e) }) });
        return;
      }
      // The label names the step in the undo history; the ops are one program, so it is one step.
      if (apply(plan.ops, `${t(`actions.${id}`)} (${timeOf(e)})`) && plan.select) store.setUi({ selection: [plan.select] });
    },
    [store, isLocked, openSounds, apply, t, timeOf],
  );

  const openMenu = useCallback((e: LaneEvent, at: { clientX: number; clientY: number }) => setAnchor({ x: at.clientX, y: at.clientY, event: e }), []);
  const nearest = useCallback((): LaneEvent | null => nearestEvent(data.events, store.getState().ui.playheadFrame), [data.events, store]);

  const activate = useCallback(
    (e: LaneEvent) => {
      setActiveKey(e.key);
      store.setUi({ playheadFrame: e.frame, playbackSpeed: 0 });
    },
    [store],
  );
  const step = useCallback(
    (delta: 1 | -1) => {
      const list = data.events;
      if (list.length === 0) return;
      const at = list.findIndex((e) => e.key === activeKey);
      const frame = store.getState().ui.playheadFrame;
      const next = at >= 0 ? list[Math.min(list.length - 1, Math.max(0, at + delta))] : delta > 0 ? list.find((e) => e.frame > frame) : [...list].reverse().find((e) => e.frame < frame);
      if (next) activate(next);
    },
    [data.events, activeKey, store, activate],
  );

  const laneProps: EventsLaneProps | undefined = useMemo(
    () => (pref.lane ? { items: data.events, labels, snap: pref.snap, activeKey, describe, ariaLabel: t('ariaLabel'), onActivate: activate, onMenu: openMenu } : undefined),
    [pref.lane, pref.snap, data.events, labels, activeKey, describe, t, activate, openMenu],
  );
  const header = useMemo(
    () => (pref.lane ? { label: t('laneLabel'), snap: pref.snap, snapLabel: pref.snap ? t('snapIsOn') : t('snapIsOff'), onToggleSnap: () => setSnap(!pref.snap) } : undefined),
    [pref.lane, pref.snap, setSnap, t],
  );
  const snapFrames = useMemo(() => (pref.lane && pref.snap && data.events.length > 0 ? data.events.map((e) => e.frame) : undefined), [pref.lane, pref.snap, data.events]);

  const menuItems: EventMenuItem[] = anchor ? actionsFor(anchor.event).map(({ id, label }) => ({ id, label, run: () => run(id, anchor.event) })) : [];
  const menu = (
    <EventActionsMenu
      anchor={anchor ? { x: anchor.x, y: anchor.y } : null}
      title={anchor ? t('menuTitle', { type: typeWord(anchor.event.type), time: timeOf(anchor.event) }) : ''}
      ariaLabel={t('menuAria')}
      items={menuItems}
      onClose={() => setAnchor(null)}
    />
  );

  const toggleLane = useCallback(() => setLane(!pref.lane), [setLane, pref.lane]);
  const toggleSnap = useCallback(() => setSnap(!pref.snap), [setSnap, pref.snap]);
  const actOnNearest = useCallback(() => {
    const target = nearest();
    if (!target) {
      notifications.show({ color: 'yellow', title: t('noEventsTitle'), message: t('noEventsHere') });
      return;
    }
    activate(target);
    const rect = typeof document === 'undefined' ? null : document.querySelector('[data-testid="timeline-canvas"]')?.getBoundingClientRect();
    openMenu(target, { clientX: (rect?.left ?? 0) + 120, clientY: (rect?.top ?? 0) + 80 });
  }, [nearest, activate, openMenu, t]);

  return { laneOn: pref.lane, snapOn: pref.snap, setLane, setSnap, data, laneProps, header, snapFrames, menu, openMenu, toggleLane, toggleSnap, actOnNearest, actionsFor, run, describe, nearest, step, activeKey };
}
