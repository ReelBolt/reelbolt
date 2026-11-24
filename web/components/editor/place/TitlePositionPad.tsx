'use client';

import { useRef } from 'react';
import { Button, Text, UnstyledButton } from '@mantine/core';
import type { TextPosition } from '@/lib/editing/types';
import { TEXT_POSITION_CHOICES } from '@/lib/editing/structure/graphics';
import { useT } from '@/i18n';

export interface TitlePositionPadProps {
  /** The chosen spot, or null/undefined for "Template default". */
  value: TextPosition | null | undefined;
  onChange: (next: TextPosition | null) => void;
  disabled?: boolean;
  testIdPrefix?: string;
}

/**
 * R10.5: a 3x3 pad that places a title on the picture, plus "Template default" (no position stored).
 * One radio group: arrow keys move between the nine spots, Space or Enter chooses. Targets are 44 px.
 */
export function TitlePositionPad({ value, onChange, disabled = false, testIdPrefix = 'title-position' }: TitlePositionPadProps) {
  const t = useT('editor-misc.place');
  const refs = useRef<Array<HTMLButtonElement | null>>([]);
  const current = value ? TEXT_POSITION_CHOICES.findIndex((c) => c.id === value) : -1;
  const tabbable = current >= 0 ? current : 4;

  const onKeyDown = (e: React.KeyboardEvent, index: number) => {
    const col = index % 3;
    const row = Math.floor(index / 3);
    let next = index;
    if (e.key === 'ArrowLeft' && col > 0) next = index - 1;
    else if (e.key === 'ArrowRight' && col < 2) next = index + 1;
    else if (e.key === 'ArrowUp' && row > 0) next = index - 3;
    else if (e.key === 'ArrowDown' && row < 2) next = index + 3;
    else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      e.stopPropagation();
      onChange(TEXT_POSITION_CHOICES[index].id);
      return;
    } else return;
    e.preventDefault();
    e.stopPropagation();
    refs.current[next]?.focus();
    onChange(TEXT_POSITION_CHOICES[next].id);
  };

  return (
    <div data-testid={testIdPrefix}>
      <Text size="sm" fw={500} id={`${testIdPrefix}-label`}>
        {t('position')}
      </Text>
      <div role="radiogroup" aria-labelledby={`${testIdPrefix}-label`} style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 44px)', gap: 4, marginTop: 4 }}>
        {TEXT_POSITION_CHOICES.map((c, i) => {
          const selected = c.id === value;
          return (
            <UnstyledButton
              key={c.id}
              ref={(el: HTMLButtonElement | null) => {
                refs.current[i] = el;
              }}
              role="radio"
              aria-checked={selected}
              aria-label={t(`positions.${c.id}`)}
              title={t(`positions.${c.id}`)}
              disabled={disabled}
              tabIndex={i === tabbable ? 0 : -1}
              onClick={() => onChange(c.id)}
              onKeyDown={(e) => onKeyDown(e, i)}
              data-testid={`${testIdPrefix}-${c.id}`}
              style={{
                width: 44,
                height: 44,
                borderRadius: 6,
                border: '1px solid var(--mantine-color-default-border)',
                background: selected ? 'var(--mantine-primary-color-filled)' : 'transparent',
                display: 'grid',
                placeItems: 'center',
              }}
            >
              <span aria-hidden style={{ width: 10, height: 10, borderRadius: 5, background: selected ? '#ffffff' : 'var(--mantine-color-dimmed)' }} />
            </UnstyledButton>
          );
        })}
      </div>
      <Button
        variant={value ? 'default' : 'light'}
        size="sm"
        mt={6}
        disabled={disabled}
        aria-pressed={!value}
        onClick={() => onChange(null)}
        style={{ minHeight: 44 }}
        data-testid={`${testIdPrefix}-default`}
      >
        {t('templateDefault')}
      </Button>
    </div>
  );
}
