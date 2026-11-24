'use client';

import { useState } from 'react';
import { Stack, Text, TextInput, type TextInputProps } from '@mantine/core';
import { hasRemovedCharacters, REMOVED_CHARACTERS_NOTE, sanitizeTitleText } from '@/lib/editing/title-text';

interface OverlayTextInputProps extends Omit<TextInputProps, 'onChange' | 'onBlur' | 'defaultValue' | 'value'> {
  /** The text stored on the clip (the field shows it, edited, until it is committed). */
  value: string;
  /** Called on blur with the cleaned text when it differs from `committed`. */
  onCommit: (text: string) => void;
  /** What counts as unchanged (defaults to `value`); a field that shows a transformed view passes its own. */
  committed?: string;
  /** Maps what the person typed to what is stored (captions use "/" for a line break). */
  toStored?: (typed: string) => string;
  'data-testid'?: string;
  noteTestId?: string;
}

/**
 * A title/caption text field. While the person types, a plain sentence under it says when a character the
 * title font does not keep (an emoji, a colon, a quote mark) is in the text; on blur the stored text is
 * the cleaned one, and the field shows it.
 */
export function OverlayTextInput({ value, committed, onCommit, toStored, noteTestId, style, ...rest }: OverlayTextInputProps) {
  const [removed, setRemoved] = useState(false);
  const store = toStored ?? ((typed: string) => typed);
  return (
    <Stack gap={2} style={style}>
      <TextInput
        {...rest}
        defaultValue={value}
        onChange={(e) => setRemoved(hasRemovedCharacters(e.currentTarget.value))}
        onBlur={(e) => {
          const clean = sanitizeTitleText(e.currentTarget.value);
          if (clean !== e.currentTarget.value) e.currentTarget.value = clean;
          const next = store(clean);
          if (next.length > 0 && next !== (committed ?? value)) onCommit(next);
        }}
        onKeyDown={(e) => e.stopPropagation()}
      />
      {removed && (
        <Text size="xs" c="orange.8" role="status" data-testid={noteTestId ?? 'overlay-text-note'}>
          {REMOVED_CHARACTERS_NOTE}
        </Text>
      )}
    </Stack>
  );
}
