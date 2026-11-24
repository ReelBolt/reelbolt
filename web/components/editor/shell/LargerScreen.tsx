'use client';

import React from 'react';
import { Group, Text } from '@mantine/core';
import { IconDeviceDesktop } from '@tabler/icons-react';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { useT } from '@/i18n';

export interface NeedsLargerScreenProps {
  /** A stable id for the tool, for tests (`data-feature`). */
  feature: string;
  /** What needs the room, as the start of a sentence: "Editing curves". */
  what: string;
  /** What to do instead. Defaults to opening the timeline on a tablet or computer. */
  hint?: string;
}

/**
 * The one "needs a larger screen" state: a plain sentence where a tool that does not work on a
 * phone would otherwise be. Never a disabled or half-working control.
 */
export function NeedsLargerScreen({ feature, what, hint: hintProp }: NeedsLargerScreenProps) {
  const t = useT('editor.shell.largerScreen');
  const hint = hintProp ?? t('openThisTimelineOn');
  return (
    <Group gap={8} wrap="nowrap" align="flex-start" py={6} data-testid="needs-larger-screen" data-feature={feature} role="note">
      <IconDeviceDesktop size={18} stroke={1.7} aria-hidden style={{ flex: '0 0 auto', marginTop: 1 }} />
      <Text size="sm">
        {t('needsALargerScreen', { what, hint })}
      </Text>
    </Group>
  );
}

export interface LargerScreenOnlyProps extends NeedsLargerScreenProps {
  children: React.ReactNode;
}

/** Renders its children, or on a phone the "needs a larger screen" state in their place. */
export function LargerScreenOnly({ children, ...state }: LargerScreenOnlyProps) {
  const phone = useDeviceClass() === 'phone';
  return phone ? <NeedsLargerScreen {...state} /> : <>{children}</>;
}
