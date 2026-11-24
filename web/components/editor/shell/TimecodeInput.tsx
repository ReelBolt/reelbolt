'use client';

import React, { useEffect, useRef, useState } from 'react';
import { TextInput, type TextInputProps } from '@mantine/core';
import {
  digitStreamPreview,
  framesToTimecode,
  parseTimecodeInput,
  TIMECODE_MAX_LENGTH,
  type Rational,
} from '@/lib/editing/time';

export interface TimecodeInputProps extends Omit<TextInputProps, 'value' | 'onChange' | 'defaultValue' | 'error' | 'inputMode'> {
  /** The frame count the document holds. */
  frames: number;
  rate: Rational;
  /** Called once with the final frame count when an entry is accepted (Enter or leaving the field). */
  onCommit: (frames: number) => void;
}

/** The "why it was refused" line stays under the field until the next edit or Escape. */

/**
 * R01.5: a timecode field that never keeps garbage. Typing digits fills the timecode from the right
 * (1215 shows 00:00:12:15), Enter or leaving the field commits, Esc cancels. An entry that is not a
 * time reverts the field to the stored value and says why under it for a few seconds. On a phone the
 * numeric keypad opens (`inputMode="numeric"`).
 */
export function TimecodeInput({ frames, rate, onCommit, onBlur, onKeyDown, onFocus, ...rest }: TimecodeInputProps) {
  const [draft, setDraft] = useState<string | null>(null);
  const [reason, setReason] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (timer.current !== null) clearTimeout(timer.current);
    },
    [],
  );

  const stored = framesToTimecode(frames, rate);

  const showReason = (text: string) => {
    setReason(text);
  };

  const commit = () => {
    if (draft === null) return;
    const result = parseTimecodeInput(draft, rate);
    setDraft(null);
    if (!result.ok) {
      showReason(result.reason);
      return;
    }
    setReason(null);
    if (result.frames !== frames) onCommit(result.frames);
  };

  return (
    <TextInput
      {...rest}
      value={draft ?? stored}
      inputMode="numeric"
      // A dialog around the field closes on Esc from the window (capture phase), so the field marks itself while it
      // holds an entry or a reason: that Esc only cancels the entry (Mantine skips targets with this attribute).
      data-mantine-stop-propagation={draft !== null || reason !== null ? 'true' : undefined}
      autoComplete="off"
      maxLength={TIMECODE_MAX_LENGTH + 1}
      error={reason}
      onFocus={(e) => {
        e.currentTarget.select();
        onFocus?.(e);
      }}
      onChange={(e) => {
        const typed = e.currentTarget.value.replace(/[^0-9:;]/g, '');
        if (reason) setReason(null);
        // Typing is a digit stream shown as the timecode it will become (the field always holds the 3 colons of
        // that preview, or none). A paste with another colon count, like 12:15, is kept as typed and parsed on commit.
        const colons = (typed.match(/:/g) ?? []).length;
        setDraft(colons === 0 || colons === 3 ? digitStreamPreview(typed) : typed);
      }}
      onBlur={(e) => {
        commit();
        onBlur?.(e);
      }}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          commit();
        } else if (e.key === 'Escape' && (draft !== null || reason !== null)) {
          // Esc cancels the entry only; it must not also close the dialog the field sits in.
          e.stopPropagation();
          setDraft(null);
          setReason(null);
        }
        onKeyDown?.(e);
      }}
    />
  );
}
