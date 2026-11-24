'use client';

import React, { useRef } from 'react';
import { Button, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, Item } from '@/lib/editing/types';
import type { ViewerTool } from '@/lib/editing/store/editor-store';
import { composeEntries, findComposeItem, resolveComposeEntry, viewerToolForEntry, type ComposeEntry, type ComposeEntryId } from '@/lib/editing/compose/entries';
import { adjustmentClipOps } from '@/lib/editing/compose/adjustment';
import { framesToTimecode } from '@/lib/editing/time';
import { EditorPanel } from '../ui/EditorPanel';
import { PageEmptyState } from '../ui/PageEmptyState';
import { useT } from '@/i18n';
import { useLiveChord } from '../prefs/live-chords';
import classes from './ComposeList.module.css';
import { DEFAULT_ADJUSTMENT_NAME, DEFAULT_ADJUSTMENTS_TRACK_NAME } from '../shell/default-names';

export interface ComposeListProps {
  doc: EditTimeline;
  selection: readonly string[];
  readOnly: boolean;
  playheadFrame: number;
  inOut: { inFrame: number | null; outFrame: number | null };
  newId: () => string;
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSelect: (itemId: string) => void;
  /** The selected entry (null: the first one applies) and its setter; view state, never in the document. */
  composeEntry: ComposeEntryId | null;
  onComposeEntry: (entry: ComposeEntryId) => void;
  /** The current viewer tool, so leaving a shape entry can return the viewer to Select (§12 rule 3). */
  viewerTool?: ViewerTool;
  /**
   * Enters (`mask`, `reframe`, `pin`) or leaves (`select`) a viewer tool. `focusPicture: false` enters it without
   * moving focus (an arrow key in the list keeps the list); a click or Enter moves focus to the picture.
   */
  onViewerTool?: (tool: 'select' | 'mask' | 'reframe' | 'pin', options?: { focusPicture?: boolean }) => void;
}


type ComposeT = ReturnType<typeof useT>;

/** The plain name of an entry (static keys so the catalogue check can see them). */
export function entryName(t: ComposeT, id: ComposeEntryId): string {
  switch (id) {
    case 'mask': return t('entry.mask');
    case 'reframe': return t('entry.reframe');
    case 'keyer': return t('entry.keyer');
    case 'pin': return t('entry.pin');
    case 'effects': return t('entry.effects');
    case 'motion': return t('entry.motion');
    case 'chain': return t('entry.chain');
    case 'grade': return t('entry.grade');
    case 'matte': return t('entry.matte');
    case 'quad': return t('entry.quad');
  }
}

function kindName(t: ComposeT, item: Item): string {
  switch (item.type) {
    case 'mediaClip': return t('kind.media');
    case 'adjustmentClip': return t('kind.adjustment');
    case 'insertClip': return t('kind.insert');
    default: return t('kind.other');
  }
}

/**
 * Compose's left column (§5.5): the selected clip named with its type, and what is on it as a listbox. Nothing
 * selected keeps the page's empty state and the "Add adjustment clip" action here.
 */
export function ComposeList({ doc, selection, readOnly, playheadFrame, inOut, newId, onApply, onSelect, composeEntry, onComposeEntry, viewerTool = 'select', onViewerTool }: ComposeListProps) {
  const t = useT('editor-misc.compose');
  const item = findComposeItem(doc, selection);
  const entries = composeEntries(item);
  const current = resolveComposeEntry(entries, composeEntry);
  const chordFor: Partial<Record<ComposeEntryId, ReturnType<typeof useLiveChord>>> = { mask: useLiveChord('toolMask'), reframe: useLiveChord('toolReframe'), pin: useLiveChord('toolCornerPin') };
  const adjustChord = useLiveChord('addAdjustmentClip');
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});

  const addAdjustment = () => {
    const { ops, clipId } = adjustmentClipOps(doc, { playheadFrame, inOut }, newId, { clip: DEFAULT_ADJUSTMENT_NAME, track: DEFAULT_ADJUSTMENTS_TRACK_NAME });
    onApply(ops, t('addAdjustmentLabel'));
    onSelect(clipId);
  };

  const choose = (entry: ComposeEntry, focusPicture = true) => {
    onComposeEntry(entry.id);
    const next = viewerToolForEntry(entry, viewerTool);
    if (next === 'mask' || next === 'reframe' || next === 'pin' || next === 'select') onViewerTool?.(next, { focusPicture });
  };

  const stateText = (entry: ComposeEntry): string => {
    const name = entryName(t, entry.id);
    const s = entry.state;
    if (s.kind === 'count') return t('entryCount', { name, count: s.count });
    if (s.kind === 'shape') return t('entryShape', { name, shape: s.shape === 'ellipse' ? t('shapeEllipse') : t('shapeRect') });
    if (s.kind === 'object') return t('entryShape', { name, shape: t('shapeObject') });
    // The node view is a read-only view with no on or off state, so it is just its name.
    if (entry.id === 'chain') return name;
    return entry.on ? t('entryOn', { name }) : t('entryOff', { name });
  };

  const onKeyDown = (e: React.KeyboardEvent, index: number) => {
    // Space is the editor's play / pause here, not "activate this entry" (Enter and a click do that).
    if (e.key === ' ') e.preventDefault();
    // A modified arrow is a global chord (it runs there); the list moves only on the bare key.
    if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
    const last = entries.length - 1;
    const to = e.key === 'ArrowDown' ? Math.min(last, index + 1) : e.key === 'ArrowUp' ? Math.max(0, index - 1) : e.key === 'Home' ? 0 : e.key === 'End' ? last : -1;
    if (to < 0) return;
    e.preventDefault();
    const target = entries[to];
    refs.current[target.id]?.focus();
    // The arrow selects the entry and keeps focus on the list; Enter or a click is what moves focus to the picture.
    choose(target, false);
  };

  if (!item) {
    return (
      <EditorPanel title={t('title')} testId="compose-workspace" scroll={false}>
        <Stack gap="sm">
          {selection.length > 1 ? (
            <Text size="xs" c="dimmed" data-testid="compose-multi">{t('multiSelect')}</Text>
          ) : (
            <PageEmptyState page="compose" hint={t('selectHint')} testId="compose-empty" />
          )}
          <Button size="compact-xs" fz="xs" variant="light" leftSection={<IconPlus size={12} />} disabled={readOnly} onClick={addAdjustment} aria-keyshortcuts={adjustChord.aria} title={adjustChord.display ? adjustChord.tip(t('addAdjustmentLabel')) : undefined} data-testid="compose-add-adjustment">
            {inOut.inFrame !== null && inOut.outFrame !== null ? t('addAdjustmentRange') : t('addAdjustmentAt', { time: framesToTimecode(playheadFrame, doc.settings.rate) })}
          </Button>
          <Text size="xs" c="dimmed">{t('adjustmentNote')}</Text>
        </Stack>
      </EditorPanel>
    );
  }

  return (
    <EditorPanel title={t('title')} testId="compose-workspace" scroll={false}>
      <Stack gap="xs">
        <Text size="xs" fw={600} data-testid="compose-target" style={{ overflowWrap: 'anywhere' }}>
          {t('headerLine', { name: item.name ?? '', kind: kindName(t, item) })}
        </Text>
        {entries.length === 0 ? (
          <Text size="xs" c="dimmed">{t('noControls', { type: item.type })}</Text>
        ) : (
          <Stack gap={2} role="listbox" aria-label={t('listLabel')} data-testid="compose-list" data-editor-shortcuts="through">
            {entries.map((entry, index) => {
              const selected = entry.id === current;
              return (
                <UnstyledButton
                  key={entry.id}
                  ref={(el: HTMLButtonElement | null) => {
                    refs.current[entry.id] = el;
                  }}
                  role="option"
                  aria-selected={selected}
                  aria-current={selected ? 'true' : undefined}
                  aria-keyshortcuts={chordFor[entry.id]?.aria}
                  title={chordFor[entry.id]?.display ? chordFor[entry.id]?.tip(entryName(t, entry.id)) : undefined}
                  tabIndex={selected ? 0 : -1}
                  onClick={() => choose(entry)}
                  onKeyDown={(e: React.KeyboardEvent) => onKeyDown(e, index)}
                  onKeyUp={(e: React.KeyboardEvent) => {
                    if (e.key === ' ') e.preventDefault();
                  }}
                  data-testid={`compose-entry-${entry.id}`}
                  data-on={entry.on ? 'true' : 'false'}
                  className={classes.entry}
                  px="xs"
                  fz="xs"
                  c={entry.on || selected ? undefined : 'dimmed'}
                  fw={selected ? 600 : 400}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    borderRadius: 4,
                    background: selected ? 'var(--rb-ed-chip-bg)' : 'transparent',
                    borderInlineStart: `3px solid ${selected ? 'var(--mantine-primary-color-filled)' : 'transparent'}`,
                  }}
                >
                  {stateText(entry)}
                </UnstyledButton>
              );
            })}
          </Stack>
        )}
      </Stack>
    </EditorPanel>
  );
}
