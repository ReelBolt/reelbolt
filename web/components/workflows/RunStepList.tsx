'use client';

import { useEffect, useRef } from 'react';
import { Badge, Group, Loader, Stack, Text, UnstyledButton } from '@mantine/core';
import { useT } from '@/i18n';
import { IconCheck, IconCircleDashed, IconPlayerSkipForward, IconX } from '@tabler/icons-react';

export type RunStepState = 'Completed' | 'Running' | 'Failed' | 'Skipped' | 'NotRun' | 'Pending';

export interface RunStepListItem {
  id: string;
  order: number;
  name: string;
  state: RunStepState;
  /** Text duration such as "12s"; null when the step has not finished. */
  duration: string | null;
  /** What a running step is doing right now. */
  live?: string | null;
  /** The step result to open, when the step has one. */
  resultId: string | null;
}

const STATE_META: Record<RunStepState, { color: string; icon: React.ReactNode }> = {
  Completed: { color: 'green', icon: <IconCheck size={14} aria-hidden /> },
  Running: { color: 'blue', icon: <Loader size={12} aria-hidden /> },
  Failed: { color: 'red', icon: <IconX size={14} aria-hidden /> },
  Skipped: { color: 'gray', icon: <IconPlayerSkipForward size={14} aria-hidden /> },
  NotRun: { color: 'gray', icon: <IconCircleDashed size={14} aria-hidden /> },
  Pending: { color: 'gray', icon: <IconCircleDashed size={14} aria-hidden /> },
};

/**
 * What a step with no result means. On a Passed run it was on a branch that was not taken
 * ("Skipped"); on a failed or stopped run it never got to run ("Not run"); otherwise it is waiting.
 */
export function stepStateFor(status: string | undefined, runStatus: string): RunStepState {
  if (status === 'Completed' || status === 'Running' || status === 'Failed' || status === 'Skipped') return status;
  if (runStatus === 'Passed') return 'Skipped';
  if (runStatus === 'Failed' || runStatus === 'Cancelled' || runStatus === 'Stopped') return 'NotRun';
  return 'Pending';
}

interface Props {
  items: readonly RunStepListItem[];
  selectedResultId: string | null;
  onSelect: (resultId: string) => void;
}

/** The plain step list: number, name, status (icon and word), duration. The failed step is highlighted and scrolled into view. */
export function RunStepList({ items, selectedResultId, onSelect }: Props) {
  const t = useT('execution');
  const failedRef = useRef<HTMLElement | null>(null);
  const failedId = items.find((i) => i.state === 'Failed')?.id ?? null;

  useEffect(() => {
    failedRef.current?.scrollIntoView?.({ block: 'center', behavior: 'auto' });
  }, [failedId]);

  return (
    <Stack gap={6} component="ol" m={0} p={0} style={{ listStyle: 'none' }} data-testid="run-step-list">
      {items.map((item) => {
        const meta = STATE_META[item.state];
        const stateLabel = t(`stepState.${item.state}`);
        const failed = item.state === 'Failed';
        const selected = item.resultId !== null && item.resultId === selectedResultId;
        const openable = item.resultId !== null;
        return (
          <li key={item.id} data-testid="run-step-row" data-state={item.state}>
            <UnstyledButton
              ref={failed ? (failedRef as React.Ref<HTMLButtonElement>) : undefined}
              aria-disabled={!openable || undefined}
              onClick={() => item.resultId && onSelect(item.resultId)}
              aria-current={selected ? 'true' : undefined}
              aria-label={t('stepRow.ariaLabel', { order: item.order, name: item.name, state: stateLabel, live: item.state === 'Running' && item.live ? item.live : 'none', duration: item.duration ? item.duration : 'none' })}
              style={{
                display: 'block',
                width: '100%',
                minHeight: 44,
                padding: '8px 12px',
                borderRadius: 'var(--mantine-radius-md)',
                border: `${failed || selected ? 2 : 1}px solid ${
                  failed ? 'var(--mantine-color-red-6)' : selected ? 'var(--mantine-color-blue-6)' : 'light-dark(var(--mantine-color-gray-3), var(--mantine-color-dark-4))'
                }`,
                background: failed ? 'light-dark(var(--mantine-color-red-0), var(--mantine-color-dark-6))' : undefined,
                cursor: openable ? 'var(--rb-cursor-pointer)' : 'var(--rb-cursor-default)',
              }}
            >
              <Group gap="sm" wrap="nowrap" justify="space-between">
                <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
                  <Text size="sm" fw={700} c="dimmed" style={{ minWidth: 22 }}>{item.order}</Text>
                  <div style={{ minWidth: 0 }}>
                    <Text size="sm" fw={failed ? 700 : 500} style={{ overflowWrap: 'anywhere' }}>{item.name}</Text>
                    {item.state === 'Running' && item.live && <Text size="xs" c="dimmed">{item.live}</Text>}
                  </div>
                </Group>
                <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
                  {item.duration && <Text size="xs" c="dimmed">{item.duration}</Text>}
                  <Badge size="lg" styles={{ label: { fontSize: 12 } }} variant={failed ? 'filled' : 'light'} color={meta.color} leftSection={meta.icon}>
                    {stateLabel}
                  </Badge>
                </Group>
              </Group>
            </UnstyledButton>
          </li>
        );
      })}
    </Stack>
  );
}
