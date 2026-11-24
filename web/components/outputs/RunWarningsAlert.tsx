'use client';

import { Alert, List, Text } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import { warningTitle, type RunWarning } from '@/lib/utils/run-summary';
import { useT } from '@/i18n';

/**
 * A run's customer warnings (contract C1) as one yellow alert: a run can finish "successfully" and
 * still be missing most of its narration or its music, and that must be the first thing the
 * customer reads, not something buried in a step's JSON.
 */
export function RunWarningsAlert({ warnings, compact = false }: { warnings: RunWarning[]; compact?: boolean }) {
  const t = useT('outputs.warnings');
  if (warnings.length === 0) return null;
  const title = warnings.length === 1 ? warningTitle(warnings[0].code) : t('many', { count: warnings.length });
  return (
    <Alert
      color="yellow"
      variant="light"
      icon={<IconAlertTriangle size={18} />}
      title={title}
      role="alert"
      p={compact ? 'xs' : undefined}
    >
      {warnings.length === 1 ? (
        <Text size="sm">{warnings[0].message}</Text>
      ) : (
        <List size="sm" spacing={4}>
          {warnings.map((w) => (
            <List.Item key={`${w.code}:${w.message}`}>
              <Text span size="sm" fw={600}>{warningTitle(w.code)}: </Text>
              <Text span size="sm">{w.message}</Text>
            </List.Item>
          ))}
        </List>
      )}
    </Alert>
  );
}
