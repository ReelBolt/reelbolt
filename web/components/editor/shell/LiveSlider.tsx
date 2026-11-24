'use client';

import React, { useState } from 'react';
import { RangeSlider, Slider, type RangeSliderProps, type RangeSliderValue, type SliderProps } from '@mantine/core';

/**
 * W0.6 (UI2-02): a slider over a value the document owns. While the thumb is dragged it shows a
 * local draft (and reports it through `onChange`, for a live preview); on release `onChangeEnd`
 * commits once — one op, one undo step — and the draft gives way to the document's value.
 */
export function LiveSlider({ value, onChange, onChangeEnd, 'aria-label': ariaLabel, ...rest }: Omit<SliderProps, 'value' | 'onChangeEnd'> & { value: number; onChangeEnd: (value: number) => void }) {
  const [draft, setDraft] = useState<number | null>(null);
  return (
    <Slider
      {...rest}
      // The name belongs on the thumb (the slider role), not on the root div, where axe prohibits it.
      thumbLabel={rest.thumbLabel ?? ariaLabel}
      value={draft ?? value}
      onChange={(v) => {
        setDraft(v);
        onChange?.(v);
      }}
      onChangeEnd={(v) => {
        setDraft(null);
        if (v !== value) onChangeEnd(v);
      }}
    />
  );
}

/** The range variant of {@link LiveSlider}. */
export function LiveRangeSlider({ value, onChange, onChangeEnd, ...rest }: Omit<RangeSliderProps, 'value' | 'onChangeEnd'> & { value: RangeSliderValue; onChangeEnd: (value: RangeSliderValue) => void }) {
  const [draft, setDraft] = useState<RangeSliderValue | null>(null);
  return (
    <RangeSlider
      {...rest}
      value={draft ?? value}
      onChange={(v) => {
        setDraft(v);
        onChange?.(v);
      }}
      onChangeEnd={(v) => {
        setDraft(null);
        if (v[0] !== value[0] || v[1] !== value[1]) onChangeEnd(v);
      }}
    />
  );
}
