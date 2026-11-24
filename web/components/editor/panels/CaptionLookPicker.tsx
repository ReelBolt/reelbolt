'use client';

import React from 'react';
import { Box, Group, SimpleGrid, Stack, Text, UnstyledButton } from '@mantine/core';
import type { CaptionLook, TextPosition } from '@/lib/editing/types';
import { CAPTION_LOOK_ORDER } from '@/lib/editing/subtitles/subtitles';
import { captionLookLabelMsg } from '@/lib/editing/labels-msg';
import { useT } from '@/i18n';

/** How a look reads on a picture, drawn in CSS so the choice is visible before anything is rendered. */
const LOOK_STYLE: Record<CaptionLook, React.CSSProperties> = {
  clean: { color: '#fff', textShadow: '0 1px 3px rgba(0,0,0,.8)', fontWeight: 500 },
  bold: { color: '#fff', fontWeight: 800, fontSize: 16, textShadow: '-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000' },
  boxed: { color: '#fff', background: 'rgba(0,0,0,.6)', padding: '1px 5px', borderRadius: 2, fontWeight: 500 },
  outline: { color: '#fff', fontWeight: 700, textShadow: '-2px -2px 0 #000, 2px -2px 0 #000, -2px 2px 0 #000, 2px 2px 0 #000' },
  spotlight: { color: '#ffe600', fontWeight: 700, textShadow: '-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000' },
};

export const POSITION_GRID: ReadonlyArray<{ value: TextPosition }> = [
  { value: 'topLeft' },
  { value: 'topCenter' },
  { value: 'topRight' },
  { value: 'middleLeft' },
  { value: 'center' },
  { value: 'middleRight' },
  { value: 'bottomLeft' },
  { value: 'bottomCenter' },
  { value: 'bottomRight' },
];

export interface CaptionLookPickerProps {
  look: CaptionLook;
  position: TextPosition;
  onLook: (look: CaptionLook) => void;
  onPosition: (position: TextPosition) => void;
  disabled?: boolean;
  /** Finger-sized targets (44 px) on a coarse pointer. */
  touch?: boolean;
  /** Whether to offer the position pad (a title has no look but has a position). */
  showLook?: boolean;
}

/** The look chips and the 3x3 position pad. Each is a real button, so everything works by tap or key. */
export function CaptionLookPicker({ look, position, onLook, onPosition, disabled = false, touch = false, showLook = true }: CaptionLookPickerProps) {
  const t = useT('editor.panels.captionLookPicker');
  const target = touch ? 44 : 28;
  return (
    <Stack gap={6} data-testid="caption-look-picker">
      {showLook && (
        <div>
          <Text size="xs" fw={500} mb={2}>
            {t('look')}
          </Text>
          <Group gap={6} role="radiogroup" aria-label={t('captionLook')}>
            {CAPTION_LOOK_ORDER.map((value) => (
              <UnstyledButton
                key={value}
                role="radio"
                aria-checked={look === value}
                aria-label={t.msg(captionLookLabelMsg(value))}
                disabled={disabled}
                onClick={() => onLook(value)}
                data-testid={`caption-look-${value}`}
                style={{ minHeight: target, minWidth: target, padding: '2px 8px', borderRadius: 4, background: '#2a2a2e', border: `2px solid ${look === value ? 'var(--mantine-color-blue-5)' : 'transparent'}`, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', opacity: disabled ? 0.5 : 1 }}
              >
                <span style={{ fontSize: 13, ...LOOK_STYLE[value] }}>{t('aa')}</span>
                <span style={{ fontSize: 12, color: '#ddd' }}>{t.msg(captionLookLabelMsg(value))}</span>
              </UnstyledButton>
            ))}
          </Group>
        </div>
      )}
      <div>
        <Text size="xs" fw={500} mb={2}>
          {t('position')}
        </Text>
        <SimpleGrid cols={3} spacing={3} w={touch ? 3 * 44 + 6 : 3 * 30 + 6} role="radiogroup" aria-label={t('textPosition')}>
          {POSITION_GRID.map((cell) => (
            <UnstyledButton
              key={cell.value}
              role="radio"
              aria-checked={position === cell.value}
              aria-label={t(`positions.${cell.value}`)}
              title={t(`positions.${cell.value}`)}
              disabled={disabled}
              onClick={() => onPosition(cell.value)}
              data-testid={`caption-position-${cell.value}`}
              style={{ height: touch ? 44 : 30, borderRadius: 3, background: '#2a2a2e', border: `2px solid ${position === cell.value ? 'var(--mantine-color-blue-5)' : 'transparent'}`, opacity: disabled ? 0.5 : 1 }}
            >
              <Box style={{ width: 8, height: 8, margin: 'auto', borderRadius: '50%', background: position === cell.value ? 'var(--mantine-color-blue-5)' : '#777' }} />
            </UnstyledButton>
          ))}
        </SimpleGrid>
      </div>
    </Stack>
  );
}
