'use client';

import React from 'react';
import { Group, Stack, Text } from '@mantine/core';
import { IconAlertCircle, IconAlertTriangle, IconInfoCircle } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { renderCheckMessage, sortChecks, type RenderCheck, type RenderCheckSeverity } from '@/lib/editing/audio/render-checks';

const ICONS = { high: IconAlertTriangle, medium: IconAlertCircle, low: IconInfoCircle } as const;
const isSeverity = (s: string): s is RenderCheckSeverity => s === 'high' || s === 'medium' || s === 'low';

/** The sound checks of a finished render, one calm sentence each; severity is an icon and a word, never colour alone. */
export function RenderChecks({ checks }: { checks?: readonly RenderCheck[] | null }) {
  const t = useT('editor-audio.checks');
  if (!checks || checks.length === 0) return null;
  return (
    <Stack gap={2} data-testid="render-checks">
      {sortChecks(checks).map((check, i) => {
        const severity: RenderCheckSeverity = isSeverity(check.severity) ? check.severity : 'low';
        const Icon = ICONS[severity];
        const m = renderCheckMessage(check);
        return (
          <Group key={`${check.kind}-${i}`} gap={4} wrap="nowrap" align="flex-start" data-testid="render-check" data-kind={check.kind} data-severity={severity}>
            <Icon size={14} aria-hidden style={{ flexShrink: 0, marginTop: 2 }} />
            <Text size="xs">
              <Text span size="xs" fw={600}>{t(`severity.${severity}`)}: </Text>
              {t(m.key, m.values)}
            </Text>
          </Group>
        );
      })}
    </Stack>
  );
}
