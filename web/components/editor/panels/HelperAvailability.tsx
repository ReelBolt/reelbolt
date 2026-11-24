'use client';

import React from 'react';
import { Stack, Text } from '@mantine/core';
import type { ProposalAgent } from '@/lib/editing/sync/api';
import { AGENT_LABELS } from './useProposalFlow';
import { useT } from '@/i18n';

export interface HelperAvailabilityProps {
  unavailable: ReadonlyArray<{ agent: ProposalAgent; reason: string }>;
  /** "Used the spoken words and shots of 5 of 6 clips", or the server's note. */
  grounding: string | null;
  /** Text size token; the phone sheet uses a larger one. */
  size?: string;
}

/** R11.2: what each unavailable helper needs, and what the helper can see of the footage. Shared by the panel and the phone sheet. */
export function HelperAvailability({ unavailable, grounding, size = '12px' }: HelperAvailabilityProps) {
  const t = useT('editor.panels.helperAvailability');
  if (unavailable.length === 0 && !grounding) return null;
  return (
    <Stack gap={2} data-testid="proposal-availability">
      {unavailable.map((u) => (
        <Text key={u.agent} size={size} c="dimmed" data-testid={`proposal-unavailable-${u.agent}`}>
          {t('isOff', { label: t.msg(AGENT_LABELS[u.agent].label), reason: u.reason })}
        </Text>
      ))}
      {grounding && (
        <Text size={size} data-testid="proposal-grounding">
          {grounding}
        </Text>
      )}
    </Stack>
  );
}
