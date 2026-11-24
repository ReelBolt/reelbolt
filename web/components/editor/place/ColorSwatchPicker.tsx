'use client';

import { ColorSwatch, Group, TextInput, UnstyledButton, Text } from '@mantine/core';
import { COLOR_PRESETS, isHexColor } from '@/lib/editing/structure/graphics';
import { useState } from 'react';
import { useT } from '@/i18n';

export interface ColorSwatchPickerProps {
  value: string;
  onChange: (hex: string) => void;
  disabled?: boolean;
  testIdPrefix?: string;
  /** Overrides the custom field's test id (default `<prefix>-hex`). */
  hexTestId?: string;
}

const same = (a: string, b: string): boolean => a.trim().toLowerCase() === b.trim().toLowerCase();

/** Preset colours with names (so they are readable without sight of the colour) plus a custom hex field. */
export function ColorSwatchPicker({ value, onChange, disabled = false, testIdPrefix = 'swatch', hexTestId }: ColorSwatchPickerProps) {
  const t = useT('editor-misc.place');
  // The text being typed; null while the field simply mirrors the chosen colour.
  const [draft, setDraft] = useState<string | null>(null);
  const custom = draft ?? value;
  const invalid = custom.trim() !== '' && !isHexColor(custom);
  return (
    <div>
      <Group gap={6} role="radiogroup" aria-label={t('colourPresets')}>
        {COLOR_PRESETS.map((preset) => {
          const selected = same(preset.hex, value);
          return (
            <UnstyledButton
              key={preset.hex}
              role="radio"
              aria-checked={selected}
              aria-label={t(`colors.${preset.name.toLowerCase()}`)}
              title={t(`colors.${preset.name.toLowerCase()}`)}
              disabled={disabled}
              onClick={() => onChange(preset.hex)}
              data-testid={`${testIdPrefix}-${preset.name.toLowerCase()}`}
              style={{ width: 44, height: 44, display: 'grid', placeItems: 'center', borderRadius: 8, outline: selected ? '2px solid var(--mantine-primary-color-filled)' : undefined, outlineOffset: 1 }}
            >
              <ColorSwatch color={preset.hex} size={28} withShadow />
            </UnstyledButton>
          );
        })}
      </Group>
      <TextInput
        mt="xs"
        size="xs"
        label={t('customColour')}
        value={custom}
        disabled={disabled}
        error={invalid ? t('hexError', { example: '#1a73e8' }) : undefined}
        onChange={(e) => {
          const next = e.currentTarget.value;
          setDraft(next);
          if (isHexColor(next)) onChange(next.trim().toLowerCase());
        }}
        onBlur={() => setDraft(null)}
        onKeyDown={(e) => e.stopPropagation()}
        data-testid={hexTestId ?? `${testIdPrefix}-hex`}
      />
      <Text size="xs" c="dimmed" mt={4}>
        {t('selected', { name: (() => { const p = COLOR_PRESETS.find((c) => same(c.hex, value)); return p ? t(`colors.${p.name.toLowerCase()}`) : value; })() })}
      </Text>
    </div>
  );
}
