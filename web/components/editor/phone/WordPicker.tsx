'use client';

import React, { useRef, useState } from 'react';
import { Alert, Button, Select, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { wordRecordFrame, type TranscriptWord } from '@/lib/editing/transcript/transcript';
import { NeedsLargerScreen } from '../shell/LargerScreen';
import { moveEdgeTo, orderedRange, planWordDelete, stepRange, type WordRange } from './captions-logic';
import { useTranscriptWords } from './use-transcript-words';
import classes from './phone.module.css';
import { useT } from '@/i18n';
import { useHelperText } from './helper-text';
import { TranscriptStart } from '../panels/TranscriptStart';

const TAP = { minHeight: 44, minWidth: 44 } as const;

/** A press that stays this long on a word starts the selection (our own timer, no browser long-press). */
export const WORD_LONG_PRESS_MS = 500;
const MOVE_SLOP_PX = 10;

export interface WordPickerProps {
  doc: EditTimeline;
  projectId: string;
  seedStepResultId: string | null;
  readOnly: boolean;
  newItemId: () => string;
  /** One batch = one undo step. */
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSeek: (frame: number) => void;
  onDone?: () => void;
}

/**
 * Delete words (RM.12, text-based editing on a phone): the transcript as a flow of words. Press and hold a word
 * (or tap "Select words") to start a range, drag the two handles or use the arrow buttons to change it, then
 * Delete cuts exactly those words out and closes the gap, in one undo step. Paragraph reordering stays on a
 * larger screen.
 */
export function WordPicker({ doc, projectId, seedStepResultId, readOnly, newItemId, onApply, onSeek, onDone }: WordPickerProps) {
  const show = useHelperText();
  const tx = useT('editor.phone.wordPicker');
  const t = useTranscriptWords(doc, projectId, seedStepResultId);
  const [pickedTrack, setTrack] = useState<string | null>(null);
  const trackId = pickedTrack ?? t.mediaTracks[0]?.id ?? null;
  const words: TranscriptWord[] = t.onTrack(trackId);
  const [selectMode, setSelectMode] = useState(false);
  const [range, setRange] = useState<WordRange | null>(null);
  const [anchor, setAnchor] = useState<number | null>(null);
  const [guard, setGuard] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const press = useRef<{ x: number; y: number } | null>(null);
  const longPressed = useRef(false);
  const flow = useRef<HTMLDivElement | null>(null);

  const selected = range ? words.slice(range.start, range.end + 1) : [];
  const roughly = selected.filter((w) => w.estimated).length;

  const begin = (index: number) => {
    setAnchor(index);
    setRange({ start: index, end: index });
    setGuard(false);
    setMessage(null);
  };
  const clearTimer = () => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
  };
  const clear = () => {
    setRange(null);
    setAnchor(null);
    setSelectMode(false);
    setGuard(false);
  };

  const onWordDown = (index: number) => (e: React.PointerEvent) => {
    longPressed.current = false;
    press.current = { x: e.clientX, y: e.clientY };
    clearTimer();
    timer.current = setTimeout(() => {
      timer.current = null;
      longPressed.current = true;
      begin(index);
    }, WORD_LONG_PRESS_MS);
  };
  const onWordMove = (e: React.PointerEvent) => {
    const p = press.current;
    if (p && Math.hypot(e.clientX - p.x, e.clientY - p.y) > MOVE_SLOP_PX) clearTimer();
  };
  const onWordUp = () => clearTimer();

  const onWordClick = (index: number) => {
    if (longPressed.current) {
      longPressed.current = false;
      return;
    }
    if (range === null && !selectMode) {
      const frame = trackId ? wordRecordFrame(doc, trackId, words[index], t.fps, t.matches) : null;
      if (frame !== null) onSeek(frame);
      return;
    }
    if (range === null || anchor === null) {
      begin(index);
      return;
    }
    if (range.start === range.end && range.start === index) {
      clear();
      return;
    }
    setRange(orderedRange(anchor, index));
    setGuard(false);
  };

  const dragHandle = (edge: 'start' | 'end') => ({
    onPointerDown: (e: React.PointerEvent<HTMLSpanElement>) => {
      e.currentTarget.setPointerCapture?.(e.pointerId);
      e.preventDefault();
    },
    onPointerMove: (e: React.PointerEvent<HTMLSpanElement>) => {
      if (e.buttons === 0 && e.pointerType === 'mouse') return;
      if (!e.currentTarget.hasPointerCapture?.(e.pointerId)) return;
      const under = typeof document.elementFromPoint === 'function' ? document.elementFromPoint(e.clientX, e.clientY) : null;
      const word = under?.closest('[data-word-index]') as HTMLElement | null;
      if (!word || !flow.current?.contains(word) || !range) return;
      setRange(moveEdgeTo(range, edge, Number(word.dataset.wordIndex), words.length));
      setGuard(false);
    },
  });

  const run = (confirmed: boolean) => {
    if (!range || !trackId || selected.length === 0) return;
    if (roughly > 0 && !confirmed) {
      setGuard(true);
      return;
    }
    setGuard(false);
    try {
      const plan = planWordDelete(doc, trackId, selected, t.fps, t.matches, newItemId);
      if (plan.ops.length === 0) {
        setMessage(tx('thoseWordsAreNo'));
        return;
      }
      onApply(plan.ops, plan.label);
      setMessage(tx('theGapIsClosed', { v1: plan.label.replace('Delete', 'Deleted') }));
      clear();
    } catch (error) {
      setMessage(humanizeErrorT(tx, error, tx('thoseWordsCouldNot')));
    }
  };

  const step = (edge: 'start' | 'end', direction: -1 | 1) => {
    if (range) setRange(stepRange(range, edge, direction, words.length));
    setGuard(false);
  };

  return (
    <div className={classes.sheetStack} data-testid="word-picker">
      {t.mediaTracks.length > 1 && (
        <Select
          label={tx('wordsFrom')}
          data={t.mediaTracks.map((tr) => ({ value: tr.id, label: tr.name }))}
          value={trackId}
          allowDeselect={false}
          onChange={(v) => {
            setTrack(v);
            clear();
          }}
          styles={{ input: { minHeight: 44 } }}
          data-testid="word-picker-track"
        />
      )}
      {t.error && <Alert color="red" title={tx('theWordsCouldNot')}>{humanizeErrorT(tx, t.error, tx('tryAgainInA'))}</Alert>}
      {t.isLoading && <Text size="sm" c="dimmed">{tx('readingTheWords')}</Text>}
      {!t.isLoading && !t.error && words.length === 0 && (
        <div data-testid="word-picker-empty">
          <TranscriptStart doc={doc} projectId={projectId} readOnly={readOnly} tapStyle={TAP} emptyText={tx('thereAreNoSpoken')} />
        </div>
      )}
      {words.length > 0 && (
        <>
          <div className={classes.sheetRow}>
            <Button variant={selectMode || range ? 'filled' : 'default'} h={44} aria-pressed={selectMode || range !== null} disabled={readOnly} onClick={() => (selectMode || range ? clear() : setSelectMode(true))} data-testid="word-picker-select">
              {selectMode || range ? tx('cancelSelection') : tx('selectWords')}
            </Button>
          </div>
          <Text size="xs" c="dimmed">
            {range ? tx('dragTheRoundHandles') : selectMode ? tx('tapTheFirstWord') : tx('pressAndHoldA')}
          </Text>
          <div ref={flow} className={classes.wordFlow} data-testid="word-flow" onContextMenu={(e) => e.preventDefault()}>
            {words.map((w, i) => {
              const inRange = range !== null && i >= range.start && i <= range.end;
              return (
                <React.Fragment key={w.id}>
                  {range && i === range.start && (
                    // eslint-disable-next-line i18next/no-literal-string -- ids and key hints, not copy
                    <span className={classes.wordHandle} role="slider" aria-label={tx('selectionStart')} aria-valuemin={0} aria-valuemax={words.length - 1} aria-valuenow={range.start} tabIndex={0} data-edge="start" data-testid="word-handle-start" {...dragHandle('start')} />
                  )}
                  <button
                    type="button"
                    className={classes.wordChip}
                    data-word-index={i}
                    data-selected={inRange ? 'true' : undefined}
                    aria-pressed={inRange}
                    disabled={readOnly}
                    onPointerDown={onWordDown(i)}
                    onPointerMove={onWordMove}
                    onPointerUp={onWordUp}
                    onPointerCancel={onWordUp}
                    onClick={() => onWordClick(i)}
                    data-testid={`word-${i}`}
                  >
                    {w.text}
                    {w.estimated ? '≈' : '' /* eslint-disable-line i18next/no-literal-string -- ids and key hints, not copy */}
                  </button>
                  {range && i === range.end && (
                    // eslint-disable-next-line i18next/no-literal-string -- ids and key hints, not copy
                    <span className={classes.wordHandle} role="slider" aria-label={tx('selectionEnd')} aria-valuemin={0} aria-valuemax={words.length - 1} aria-valuenow={range.end} tabIndex={0} data-edge="end" data-testid="word-handle-end" {...dragHandle('end')} />
                  )}
                </React.Fragment>
              );
            })}
          </div>
          {range && (
            <>
              <div className={classes.sheetRow} data-testid="word-steppers">
                <Button variant="default" h={44} onClick={() => step('start', -1)} aria-label={tx('startOneWordEarlier')} data-testid="word-start-earlier">
                  {tx('start')}
                </Button>
                <Button variant="default" h={44} onClick={() => step('start', 1)} aria-label={tx('startOneWordLater')} data-testid="word-start-later">
                  {tx('start2')}
                </Button>
                <Button variant="default" h={44} onClick={() => step('end', -1)} aria-label={tx('endOneWordEarlier')} data-testid="word-end-earlier">
                  {tx('end')}
                </Button>
                <Button variant="default" h={44} onClick={() => step('end', 1)} aria-label={tx('endOneWordLater')} data-testid="word-end-later">
                  {tx('end2')}
                </Button>
              </div>
              <Text size="sm" data-testid="word-picker-summary">
                {tx('selectedSummary', { count: selected.length, text: selected.map((w) => w.text).join(' ') })}
              </Text>
              {guard && (
                <Alert color="orange" title={tx('someOfTheseWords')} data-testid="word-picker-guard">
                  <Text size="sm">{roughly} {tx('ofThemAreMarked')}</Text>
                  <div className={classes.sheetRow} style={{ marginTop: 8, gap: 12 }}>
                    <Button variant="default" h={44} onClick={() => setGuard(false)} data-testid="word-picker-guard-cancel">
                      {tx('keepThem')}
                    </Button>
                    <Button color="red" h={44} onClick={() => run(true)} data-testid="word-picker-guard-confirm">
                      {tx('cutAnyway')}
                    </Button>
                  </div>
                </Alert>
              )}
              <div className={classes.sheetRow} style={{ gap: 12 }}>
                <Button variant="default" h={44} onClick={clear} data-testid="word-picker-clear">
                  {tx('clear')}
                </Button>
                <Button color="red" h={44} disabled={readOnly || selected.length === 0} onClick={() => run(false)} data-testid="word-picker-delete">
                  {tx('deleteWords', { count: selected.length })}
                </Button>
              </div>
            </>
          )}
        </>
      )}
      <NeedsLargerScreen feature="paragraph-drag" what="Reordering paragraphs" hint={tx('openThisTimelineOn')} />
      {message && (
        <Text size="sm" role="status" data-testid="word-picker-message">
          {show(message)}
        </Text>
      )}
      {onDone && (
        <Button variant="subtle" h={44} onClick={onDone} data-testid="word-picker-done">
          {tx('done')}
        </Button>
      )}
    </div>
  );
}
