'use client';

import { useMediaQuery } from '@mantine/hooks';
import { Button, Group, Menu, Stack, Text, Tooltip } from '@mantine/core';
import { IconChevronDown, IconPlus } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { PLACE_MODES, PLACE_OVER, placeChoiceHintMsg, placeChoiceLabelMsg, type PlaceChoice } from './placeChoices';

export interface PlaceMenuProps {
  /** Runs one placement. The caller owns the document, marks and track targeting. */
  onPlace: (mode: PlaceChoice) => void;
  disabled?: boolean;
  /** `buttons` (source viewer): four visible buttons. `menu` (pool tiles): one "Place" button opening the same four. */
  variant?: 'buttons' | 'menu';
  /** Test id prefix, to tell several embeds apart. */
  idPrefix?: string;
}

/**
 * T-010: one placement button for a host that offers a single mode (the Cut source tape's Insert). It
 * keeps the shared label, hint and test-id vocabulary with the four-mode control.
 */
export function PlaceModeButton({ mode, onPlace, disabled = false, testId, variant = 'default', minHeight, showIcon = true, size = 'compact-xs', fontSize = 'xs' }: {
  mode: PlaceChoice;
  onPlace: () => void;
  disabled?: boolean;
  testId: string;
  variant?: 'filled' | 'default' | 'light' | 'subtle';
  /** Overrides the coarse-pointer minimum (phone hosts keep 44 px). */
  minHeight?: number;
  /** The phone kept a plain button: no leading glyph, its original size and label size. */
  showIcon?: boolean;
  size?: 'compact-xs' | 'compact-sm' | 'sm' | 'md';
  fontSize?: string;
}) {
  const t = useT('editor-misc.place');
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  return (
    <Tooltip label={t.msg(placeChoiceHintMsg(mode))} multiline w={240} withinPortal>
      <Button size={size} fz={fontSize} miw={44} mih={minHeight ?? (coarse ? 44 : 32)} variant={variant} leftSection={showIcon ? <IconPlus size={12} /> : undefined} disabled={disabled} onClick={onPlace} data-testid={testId}>
        {t.msg(placeChoiceLabelMsg(mode))}
      </Button>
    </Tooltip>
  );
}

/**
 * R03.6 / T-010: Insert / Overwrite / Add to end / Place on top. Shared by the source viewer (visible
 * buttons) and the pool tiles (a menu); the three-point edit comes from the source viewer's marks plus the
 * timeline playhead, which the caller applies in `onPlace`. Labels and hints come from `placeChoices.ts`.
 */
export function PlaceMenu({ onPlace, disabled = false, variant = 'buttons', idPrefix = '' }: PlaceMenuProps) {
  const t = useT('editor-misc.place');
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const minHeight = coarse ? 44 : 32;
  if (variant === 'menu') {
    return (
      <Menu position="bottom-start" withinPortal disabled={disabled}>
        <Menu.Target>
          <Button size="compact-sm" fz="xs" miw={44} mih={minHeight} leftSection={<IconPlus size={14} />} rightSection={<IconChevronDown size={13} />} disabled={disabled} data-testid={`${idPrefix}place-menu`}>
            {t('place')}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>{t('menuLabel')}</Menu.Label>
          {[...PLACE_MODES, PLACE_OVER].map((c) => (
            <Menu.Item key={c.mode} onClick={() => onPlace(c.mode)} data-testid={`${idPrefix}place-${c.mode}`}>
              <div>{t.msg(placeChoiceLabelMsg(c.mode))}</div>
              <Text size="xs" c="dimmed" maw={240}>
                {t.msg(placeChoiceHintMsg(c.mode))}
              </Text>
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
    );
  }
  return (
    <Stack gap={4}>
      <Group gap="xs" wrap="wrap">
        {[...PLACE_MODES, PLACE_OVER].map((c, i) => (
          <Tooltip key={c.mode} label={t.msg(placeChoiceHintMsg(c.mode))} multiline w={240} withinPortal>
            <Button size="xs" fz="xs" miw={44} mih={minHeight} variant={i === 0 ? 'filled' : 'default'} leftSection={i === 0 ? <IconPlus size={14} /> : undefined} disabled={disabled} onClick={() => onPlace(c.mode)} data-testid={`${idPrefix}${c.testId}`}>
              {t.msg(placeChoiceLabelMsg(c.mode))}
            </Button>
          </Tooltip>
        ))}
      </Group>
    </Stack>
  );
}
