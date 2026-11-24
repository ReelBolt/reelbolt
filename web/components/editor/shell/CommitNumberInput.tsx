'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { NumberInput, type NumberInputProps } from '@mantine/core';

/** How long typing may pause before the typed number is committed (arrow-key repeats collapse the same way). */
export const COMMIT_PAUSE_MS = 600;

export type CommitNumberInputProps = Omit<NumberInputProps, 'value' | 'onChange' | 'defaultValue'> & {
  /** The number the document holds. */
  value: number;
  /** Called ONCE per edit with the final number: on Enter, on leaving the field, or after a pause in typing. */
  onCommit: (value: number) => void;
  /** Pause after the last keystroke before committing (default {@link COMMIT_PAUSE_MS}). */
  pauseMs?: number;
};

/**
 * S20 (editor-v2-playback-verify): a number field over a value the document owns. Typing "25" used to write
 * two ops ("2", then "25") - two PATCH candidates, two undo steps, and the intermediate value could be
 * refused with a toast. Here the keystrokes only fill a local draft; the draft is committed once - on Enter,
 * on leaving the field, after a short pause, or when the field goes away - and then gives way to the
 * document's value (so a refused value never stays on screen).
 *
 * `clampBehavior` is "none": an out-of-range number is committed as typed and the engine refuses it in
 * plain words (W0.6 d3 N1), instead of being silently clamped on blur.
 */
export function CommitNumberInput({ value, onCommit, pauseMs = COMMIT_PAUSE_MS, onBlur, onKeyDown, ...rest }: CommitNumberInputProps) {
  const [draft, setDraft] = useState<string | number | null>(null);
  const pending = useRef<number | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const commitRef = useRef(onCommit);
  useEffect(() => {
    commitRef.current = onCommit;
  });

  const flush = useCallback(() => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    const n = pending.current;
    pending.current = null;
    setDraft(null);
    if (n !== null) commitRef.current(n);
  }, []);

  // A field that unmounts mid-edit (the selection moved on) still commits what was typed.
  useEffect(() => () => {
    if (timer.current !== null) clearTimeout(timer.current);
    const n = pending.current;
    pending.current = null;
    if (n !== null) commitRef.current(n);
  }, []);

  return (
    <NumberInput
      clampBehavior="none"
      {...rest}
      value={draft ?? value}
      onChange={(v) => {
        setDraft(v);
        const n = typeof v === 'number' ? v : Number.parseFloat(String(v));
        pending.current = Number.isFinite(n) && n !== value ? n : null;
        if (timer.current !== null) clearTimeout(timer.current);
        timer.current = pending.current === null ? null : setTimeout(flush, pauseMs);
      }}
      onBlur={(e) => {
        flush();
        onBlur?.(e);
      }}
      onKeyDown={(e) => {
        if (e.key === 'Enter') flush();
        onKeyDown?.(e);
      }}
    />
  );
}
